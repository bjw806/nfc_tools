using System.IO.Ports;
using System.Numerics;
using PCSC;

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
        _ => Strings.UnknownCard
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
    // Probes serial ports in parallel and matches PC/SC readers by name. A port another program holds
    // can't be probed, so it goes to busyPorts instead.
    public static IReadOnlyList<ReaderChoice> List(ICollection<string>? busyPorts = null)
    {
        var probed = SerialPorts().AsParallel().Select(port => {
            try { return (Port: port, Reader: Identify(port), Busy: false); }
            catch (UnauthorizedAccessException) { return (Port: port, Reader: null, Busy: true); }
            catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { return (Port: port, Reader: null, Busy: false); }
        }).ToList();
        foreach (var busy in probed.Where(x => x.Busy)) busyPorts?.Add(busy.Port);
        return probed.Select(x => x.Reader).OfType<ReaderChoice>().OrderBy(x => x.DeviceId).Concat(PcscReaders()).ToList();
    }

    // Skips Bluetooth COM ports, since opening one can hang for seconds.
    public static IReadOnlyList<string> SerialPorts()
    {
        var ports = SerialPort.GetPortNames().Distinct();
        if (OperatingSystem.IsWindows()) {
            using var map = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            var bluetooth = new HashSet<string>();
            foreach (var device in map?.GetValueNames() ?? [])
                if (device.Contains("BthModem", StringComparison.OrdinalIgnoreCase) && map!.GetValue(device) is string com) bluetooth.Add(com);
            ports = ports.Where(x => !bluetooth.Contains(x));
        }
        return ports.Order().ToList();
    }

    // ATNFC answers AT+GMM, PN532 answers GetFirmwareVersion. Throws if the port is in use.
    public static ReaderChoice? Identify(string port)
    {
        using var serial = new SerialPort(port, 115200, Parity.None, 8, StopBits.One) {
            Handshake = Handshake.None, NewLine = "\n", ReadTimeout = 50, WriteTimeout = 300
        };
        serial.Open();
        serial.DiscardInBuffer();
        serial.Write("\r\nAT+GMM\r\n"); // leading CRLF flushes a partial line
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
            catch (IOException) { } // garbled frame, keep listening
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
            return []; // service stopped or no readers
        }
    }

    // ACR1552U shows a PICC slot and a SAM slot, and only PICC reads tags. ACR122U has one slot,
    // "ACS ACR122 0" (Microsoft driver) or "ACS ACR122U PICC Interface 0" (ACS driver).
    public static ReaderChoice? FromPcscName(string name) =>
        name.Contains("ACR1552", StringComparison.OrdinalIgnoreCase) && name.Contains("PICC", StringComparison.OrdinalIgnoreCase)
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
            throw new ArgumentException(Strings.HexFormat);
        return Convert.FromHexString(text);
    }
    public static string Format(byte[] value) => Convert.ToHexString(value);
}

public static class UidText
{
    // The formats card registration systems usually ask for, same as the ATNFC keyboard output.
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
        if (length != expected || length == 0) throw new InvalidOperationException(Strings.WriteUnit(expected));
        if (card.Family == CardFamily.Ntag && address < 4)
            throw new InvalidOperationException(Strings.NtagProtected);
        if (card.Family == CardFamily.MifareClassic && (address < 4 || (address < 128 ? address % 4 == 3 : (address - 128) % 16 == 15)))
            throw new InvalidOperationException(Strings.MifareProtected);
        if (card.Family == CardFamily.Iso15693 && address == 0)
            throw new InvalidOperationException(Strings.Iso15693Protected);
        if (card.Family == CardFamily.FelicaLiteS && address > 0x0D)
            throw new InvalidOperationException(Strings.FelicaProtected);
        if (card.Family is CardFamily.Unknown or CardFamily.Iso14443_4)
            throw new InvalidOperationException(Strings.WriteUnsupported);
    }
}
