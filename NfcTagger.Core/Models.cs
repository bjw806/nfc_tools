using System.IO.Ports;
using System.Numerics;
using PCSC;
using PCSC.Exceptions;

namespace NfcTagger.Core;

public enum ReaderKind { Atnfc102, Atnfc103, Acr1552U, Pcr532, Acr122U }
public enum CardFamily { Unknown, Ntag, MifareClassic, Iso15693, FelicaLiteS, Iso14443_4 }

public sealed record ReaderChoice(ReaderKind Kind, string DeviceId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record CardInfo(string Uid, CardFamily Family, string RawType, string Details)
{
    public string DisplayFamily => Family switch {
        CardFamily.Ntag => "NTAG / Ultralight",
        CardFamily.MifareClassic => "MIFARE Classic",
        CardFamily.Iso15693 => "ISO15693",
        CardFamily.FelicaLiteS => "FeliCa Lite-S",
        CardFamily.Iso14443_4 => "ISO14443-4",
        _ => "알 수 없는 카드"
    };
}

public sealed record MemoryUnit(int Address, string? Hex, string? Error);
public sealed record CardDump(string Reader, CardInfo Card, DateTimeOffset CapturedAt, int UnitSize, IReadOnlyList<MemoryUnit> Units);

public interface INfcReader : IDisposable
{
    ReaderKind Kind { get; }
    string Name { get; }
    void Open();
    CardInfo? Detect();
    byte[] ReadUnit(CardInfo card, int address, string? keyHex = null, bool keyB = false);
    // Consecutive units; readers with a multi-unit command override this.
    IReadOnlyList<byte[]> ReadUnits(CardInfo card, int address, int count, string? keyHex = null, bool keyB = false) =>
        Enumerable.Range(address, count).Select(x => ReadUnit(card, x, keyHex, keyB)).ToList();
    void WriteUnit(CardInfo card, int address, byte[] data, string? keyHex = null, bool keyB = false);
    byte[] TransmitApdu(byte[] command);
}

public static class ReaderDiscovery
{
    // The readers actually plugged in: serial ports are asked who they are (in parallel), PC/SC readers are known by
    // name. A port that cannot be opened cannot be asked: it goes to busyPorts when another program holds it, or to
    // deniedPorts when this user may not open it (Linux without the udev rule).
    public static IReadOnlyList<ReaderChoice> List(ICollection<string>? busyPorts = null, ICollection<string>? deniedPorts = null)
    {
        var probed = SerialPorts().AsParallel().Select(port => {
            try { return (Port: port, Reader: Identify(port), Error: (UnauthorizedAccessException?)null); }
            catch (UnauthorizedAccessException e) { return (Port: port, Reader: null, Error: e); }
            catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { return (Port: port, Reader: null, Error: null); }
        }).ToList();
        foreach (var (port, _, error) in probed)
            if (error is not null) (IsPermissionDenied(error) ? deniedPorts : busyPorts)?.Add(port);
        return probed.Select(x => x.Reader).OfType<ReaderChoice>().OrderBy(x => x.DeviceId).Concat(PcscReaders()).ToList();
    }

    // .NET reports every failed serial open as UnauthorizedAccessException. On Windows it means another program holds
    // the port. On Linux the inner IOException carries errno: EBUSY (16) means held (another program, or ModemManager
    // probing a freshly plugged ATNFC); anything else, EACCES in practice, is a missing permission.
    private static bool IsPermissionDenied(UnauthorizedAccessException e) =>
        !OperatingSystem.IsWindows() && e.InnerException is not IOException { HResult: 16 };

    // Bluetooth serial ports are left out: opening one tries to reach the paired device and can block for seconds.
    // On Linux only USB serial ports are asked: the readers are ttyACM (ATNFC) or ttyUSB (PCR532), and ttyS are the
    // mainboard's own UARTs, which only root may open and which must not be sent reader commands.
    public static IReadOnlyList<string> SerialPorts()
    {
        var ports = SerialPort.GetPortNames().Distinct();
        if (OperatingSystem.IsWindows()) {
            using var map = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            var bluetooth = new HashSet<string>();
            foreach (var device in map?.GetValueNames() ?? [])
                if (device.Contains("BthModem", StringComparison.OrdinalIgnoreCase) && map!.GetValue(device) is string com) bluetooth.Add(com);
            ports = ports.Where(x => !bluetooth.Contains(x));
        } else if (OperatingSystem.IsLinux()) {
            ports = ports.Where(x => x.StartsWith("/dev/ttyACM", StringComparison.Ordinal) || x.StartsWith("/dev/ttyUSB", StringComparison.Ordinal));
        }
        return ports.Order().ToList();
    }

    // Asks one serial port which reader it is, with the same commands Open sends: ATNFC answers AT+GMM, a PN532
    // (PCR532) answers GetFirmwareVersion; anything else is not a reader. Throws if another program holds the port.
    public static ReaderChoice? Identify(string port)
    {
        using var serial = new SerialPort(port, 115200, Parity.None, 8, StopBits.One) {
            Handshake = Handshake.None, NewLine = "\n", ReadTimeout = 50, WriteTimeout = 300
        };
        serial.Open();
        serial.DiscardInBuffer();
        serial.Write("\r\nAT+GMM\r\n"); // the leading CRLF ends any half-received line in an ATNFC
        for (var until = DateTime.UtcNow.AddMilliseconds(300); DateTime.UtcNow < until;) {
            string line;
            try { line = serial.ReadLine(); } catch (TimeoutException) { continue; }
            if (!line.Contains("+GMM:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("NFC-103", StringComparison.OrdinalIgnoreCase)) return new(ReaderKind.Atnfc103, port, $"ATNFC-103 · {port}");
            if (line.Contains("NFC-102", StringComparison.OrdinalIgnoreCase)) return new(ReaderKind.Atnfc102, port, $"ATNFC-102 · {port}");
            return null;
        }
        serial.DiscardInBuffer();
        serial.ReadTimeout = 200;
        var wakeAndAsk = new byte[] { 0x55, 0x55, 0x00, 0x00, 0x00 }.Concat(Pn532Frames.Encode([0xD4, 0x02])).ToArray();
        serial.Write(wakeAndAsk, 0, wakeAndAsk.Length);
        for (var until = DateTime.UtcNow.AddMilliseconds(400); DateTime.UtcNow < until;) {
            try { if (Pn532Frames.ReadFrame(serial, 400) is [0xD5, 0x03, 0x32, ..]) return new(ReaderKind.Pcr532, port, $"PCR532 / PN532 · {port}"); }
            catch (IOException) { /* a stray or garbled frame; keep listening */ }
            catch (TimeoutException) { break; }
        }
        return null;
    }

    public static IReadOnlyList<ReaderChoice> PcscReaders()
    {
        try {
            using var context = ContextFactory.Instance.Establish(SCardScope.System);
            return context.GetReaders().Select(FromPcscName).OfType<ReaderChoice>().ToList();
        } catch (Exception) {
            return []; // The Smart Card service may be stopped or no reader may be present.
        }
    }

    // Why PC/SC readers cannot be listed, for the Linux setup check; null when the service answers.
    public static string? PcscProblem()
    {
        try {
            using var context = ContextFactory.Instance.Establish(SCardScope.System);
            return null;
        } catch (Exception e) when (e is DllNotFoundException || e.InnerException is DllNotFoundException) {
            return "PC/SC 라이브러리(libpcsclite)가 없습니다.";
        } catch (NoServiceException) {
            return "PC/SC 서비스(pcscd)가 설치되어 있지 않거나 실행되지 않습니다.";
        } catch (PCSCException e) when (e.SCardError == SCardError.SecurityViolation) {
            return "PC/SC 서비스가 접근을 거부했습니다. 로컬 데스크톱 세션에서 실행하세요.";
        } catch (Exception e) {
            return e.Message;
        }
    }

    // ACR1552U shows a PICC (contactless) and a SAM slot; only PICC reads tags. Drivers name them differently:
    // "ACS ACR1552 1S CL Reader PICC 0" on Windows, "ACS ACR1552 1S CL Reader [ACR1552 1S CL Reader PICC] 00 00" with
    // Linux libccid, and the model without a SAM slot may carry no interface name at all, so SAM is what gets left out.
    // The ACR122U has a single slot: "ACS ACR122 0" (Microsoft driver), "ACS ACR122U PICC Interface 0" (ACS driver),
    // "ACS ACR122U PICC Interface 00 00" or "ACS ACR122U 00 00" on Linux. Other readers, such as a laptop's built-in
    // SIM (UICC) slot, are not NFC readers.
    public static ReaderChoice? FromPcscName(string name) =>
        name.Contains("ACR1552", StringComparison.OrdinalIgnoreCase) && !name.Contains("SAM", StringComparison.Ordinal)
            ? new(ReaderKind.Acr1552U, name, $"ACR1552U · {name}")
        : name.Contains("ACR122", StringComparison.OrdinalIgnoreCase) ? new(ReaderKind.Acr122U, name, $"ACR122U · {name}")
        : null;

    public static INfcReader Create(ReaderChoice choice) => choice.Kind switch {
        ReaderKind.Atnfc102 or ReaderKind.Atnfc103 => new AtnfcReader(choice),
        ReaderKind.Pcr532 => new Pn532Reader(choice),
        ReaderKind.Acr1552U or ReaderKind.Acr122U => new AcrReader(choice),
        _ => throw new NotSupportedException()
    };
}

public static class Hex
{
    public static byte[] Parse(string value)
    {
        var text = new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (text.Length == 0 || text.Length % 2 != 0 || text.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("HEX는 짝수 개의 0-9/A-F 문자여야 합니다.");
        return Convert.FromHexString(text);
    }
    public static string Format(byte[] value) => Convert.ToHexString(value);
}

public static class UidText
{
    // The renderings card-registration systems ask for; the ATNFC keyboard output offers the same four (HEX/DEC, big/little endian).
    public static (string Hex, string HexReversed, string Dec, string DecReversed)? Formats(string uid)
    {
        byte[] bytes;
        try { bytes = Hex.Parse(uid); }
        catch (ArgumentException) { return null; }
        var reversed = Enumerable.Reverse(bytes).ToArray();
        return (Hex.Format(bytes), Hex.Format(reversed),
            new BigInteger(bytes, isUnsigned: true, isBigEndian: true).ToString(),
            new BigInteger(reversed, isUnsigned: true, isBigEndian: true).ToString());
    }
}

public static class WriteGuard
{
    public static void Validate(CardInfo card, int address, int length)
    {
        if (address < 0 || address > 255) throw new ArgumentOutOfRangeException(nameof(address));
        var expected = card.Family switch {
            CardFamily.Ntag => 4,
            CardFamily.MifareClassic => 16,
            CardFamily.FelicaLiteS => 16,
            _ => length
        };
        if (length != expected || length == 0) throw new InvalidOperationException($"이 카드의 쓰기 단위는 {expected}바이트입니다.");
        if (card.Family == CardFamily.Ntag && address < 4)
            throw new InvalidOperationException("제조사·잠금·CC 페이지는 쓸 수 없습니다.");
        if (card.Family == CardFamily.MifareClassic && (address < 4 || (address < 128 ? address % 4 == 3 : (address - 128) % 16 == 15)))
            throw new InvalidOperationException("제조사·MAD 블록과 섹터 트레일러는 쓸 수 없습니다.");
        if (card.Family == CardFamily.Iso15693 && address == 0)
            throw new InvalidOperationException("ISO15693 CC/첫 블록은 직접 쓸 수 없습니다.");
        if (card.Family == CardFamily.FelicaLiteS && address > 0x0D)
            throw new InvalidOperationException("FeliCa 시스템·설정 블록은 쓸 수 없습니다.");
        if (card.Family is CardFamily.Unknown or CardFamily.Iso14443_4)
            throw new InvalidOperationException("이 카드 종류의 직접 메모리 쓰기는 지원하지 않습니다.");
    }
}
