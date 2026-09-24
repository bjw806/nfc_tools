using System.IO.Ports;
using PCSC;

namespace NfcTagger.Core;

public enum ReaderKind { Atnfc102, Atnfc103, Acr1552U, Pcr532 }
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
    void WriteUnit(CardInfo card, int address, byte[] data, string? keyHex = null, bool keyB = false);
    byte[] TransmitApdu(byte[] command);
}

public static class ReaderDiscovery
{
    public static IReadOnlyList<ReaderChoice> List()
    {
        var choices = new List<ReaderChoice>();
        foreach (var port in SerialPort.GetPortNames().OrderBy(x => x)) {
            choices.Add(new(ReaderKind.Atnfc103, port, $"ATNFC-103 · {port}"));
            choices.Add(new(ReaderKind.Atnfc102, port, $"ATNFC-102 · {port}"));
            choices.Add(new(ReaderKind.Pcr532, port, $"PCR532 / PN532 · {port}"));
        }
        try {
            using var context = ContextFactory.Instance.Establish(SCardScope.System);
            foreach (var name in context.GetReaders().Where(x => x.Contains("ACR1552", StringComparison.OrdinalIgnoreCase) && x.Contains("PICC", StringComparison.OrdinalIgnoreCase)))
                choices.Add(new(ReaderKind.Acr1552U, name, $"ACR1552U · {name}"));
        } catch (Exception) {
            // The Smart Card service may be stopped or no reader may be present.
        }
        return choices;
    }

    public static INfcReader Create(ReaderChoice choice) => choice.Kind switch {
        ReaderKind.Atnfc102 or ReaderKind.Atnfc103 => new AtnfcReader(choice),
        ReaderKind.Pcr532 => new Pn532Reader(choice),
        ReaderKind.Acr1552U => new AcrReader(choice),
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
