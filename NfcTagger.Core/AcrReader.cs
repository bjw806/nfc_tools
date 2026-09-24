using PCSC;
using PCSC.Exceptions;

namespace NfcTagger.Core;

// ACS PC/SC readers (ACR1552U, ACR122U) share the same pseudo-APDUs for UID, block read/write and MIFARE keys.
// The ACR122U is PN532-based: it cannot read ISO15693, and its FeliCa pass-through differs, so a FeliCa card on it
// fails the FeliCa check in Identify and shows up as an unknown card.
public sealed class AcrReader(ReaderChoice choice) : INfcReader
{
    private ISCardContext? _context;
    private ICardReader? _card;
    private string _uid = "";
    public ReaderKind Kind => choice.Kind;
    public string Name => choice.DisplayName;

    public void Open()
    {
        _context = ContextFactory.Instance.Establish(SCardScope.System);
        if (!_context.GetReaders().Contains(choice.DeviceId)) {
            Dispose();
            throw new IOException($"{choice.DisplayName} PC/SC 리더가 연결되어 있지 않습니다.");
        }
    }

    public CardInfo? Detect()
    {
        if (_context is null) throw new IOException("리더가 연결되지 않았습니다.");
        _card?.Dispose();
        _card = null;
        try { return Identify(_context); }
        catch (Exception e) when (e is CardLiftedException || IsCardGone(e)) {
            _card?.Dispose();
            _card = null;
            _uid = "";
            return null;
        }
    }

    // Card-state failures, not reader faults. The Windows CCID driver reports a card lifted mid-command as
    // Win32 ERROR_NO_MEDIA_IN_DRIVE (1112) instead of a PC/SC code; on the ACR1552U this happens on every removal.
    private static bool IsCardGone(Exception e) =>
        e is NoSmartcardException or RemovedCardException or UnpoweredCardException or UnresponsiveCardException ||
        e is PCSCException { SCardError: SCardError.ResetCard or (SCardError)1112 };

    private CardInfo Identify(ISCardContext context)
    {
        _card = context.ConnectReader(choice.DeviceId, SCardShareMode.Shared, SCardProtocol.Any);
        var atr = _card.GetAttrib(SCardAttribute.AtrString);
        var uidResult = Send(Hex.Parse("FFCA000000"));
        var uid = Hex.Format(Data(uidResult));
        var family = ClassifyAtr(atr);
        if (family == CardFamily.Unknown) {
            try {
                var details = Data(Send(Hex.Parse("FFCA020000")));
                if (details.Length >= uid.Length / 2 + 3) {
                    var sak = details[^1];
                    family = sak switch {
                        0x08 or 0x18 => CardFamily.MifareClassic,
                        0x00 => CardFamily.Ntag,
                        _ when (sak & 0x20) != 0 => CardFamily.Iso14443_4,
                        _ => CardFamily.Unknown
                    };
                }
            } catch (IOException e) when (e is not CardLiftedException) { /* Some tag families do not provide Type A activation data. */ }
        }
        if (family == CardFamily.FelicaLiteS) {
            try {
                var poll = Data(Send(Hex.Parse("FF00000006060088B40100")));
                if (!Hex.Format(poll).Contains("88B4", StringComparison.OrdinalIgnoreCase)) family = CardFamily.Unknown;
            } catch (IOException e) when (e is not CardLiftedException) { family = CardFamily.Unknown; }
        }
        if (family == CardFamily.Unknown && atr.Length <= 12) family = CardFamily.Iso14443_4;
        _uid = uid;
        return new(uid, family, Hex.Format(atr), $"ATR {Hex.Format(atr)}");
    }

    public static CardFamily ClassifyAtr(byte[] atr)
    {
        var rid = Hex.Parse("A000000306");
        for (var i = 0; i <= atr.Length - rid.Length - 3; i++) {
            if (!atr.AsSpan(i, rid.Length).SequenceEqual(rid)) continue;
            var standard = atr[i + 5];
            var code = (atr[i + 6] << 8) | atr[i + 7];
            if (standard == 0x11 && code == 0x003B) return CardFamily.FelicaLiteS;
            if (standard == 0x0B || standard == 0x0A) return CardFamily.Iso15693;
            if (standard == 0x03 && code is 0x0001 or 0x0002) return CardFamily.MifareClassic;
            if (standard == 0x03 && code is 0x0003 or 0x003A) return CardFamily.Ntag;
        }
        return CardFamily.Unknown;
    }

    private byte[] Send(byte[] apdu)
    {
        if (_card is null) throw new IOException("카드가 감지되지 않았습니다.");
        var receive = new byte[65538];
        int length;
        try { length = _card.Transmit(apdu, receive); }
        catch (PCSCException e) when (IsCardGone(e)) { throw new CardLiftedException(); }
        if (length < 2) throw new IOException("PC/SC 응답이 짧습니다.");
        return receive[..length];
    }

    private static byte[] Data(byte[] response)
    {
        if (response.Length < 2 || response[^2] != 0x90 || response[^1] != 0x00)
            throw new IOException($"카드/리더 명령 실패: {Hex.Format(response)}");
        return response[..^2];
    }

    private void CheckCard(CardInfo card)
    {
        if (_uid.Length == 0 || !_uid.Equals(card.Uid, StringComparison.OrdinalIgnoreCase))
            throw new IOException("카드 세션이 바뀌었습니다. 다시 감지하세요.");
    }

    private void Authenticate(int address, string? keyHex, bool keyB)
    {
        if (keyHex is null) throw new ArgumentException("MIFARE Classic 키가 필요합니다.");
        var key = Hex.Parse(keyHex);
        if (key.Length != 6) throw new ArgumentException("MIFARE Classic 키는 6바이트여야 합니다.");
        Data(Send(new byte[] { 0xFF, 0x82, 0x00, 0x00, 0x06 }.Concat(key).ToArray()));
        Data(Send(new byte[] { 0xFF, 0x86, 0x00, 0x00, 0x05, 0x01, 0x00, (byte)address,
            keyB ? (byte)0x61 : (byte)0x60, 0x00 }));
    }

    public byte[] ReadUnit(CardInfo card, int address, string? keyHex = null, bool keyB = false)
    {
        CheckCard(card);
        if (address is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(address));
        switch (card.Family) {
            case CardFamily.Ntag:
                return Data(Send(new byte[] { 0xFF, 0xB0, 0x00, (byte)address, 0x04 }));
            case CardFamily.MifareClassic:
                Authenticate(address, keyHex, keyB);
                return Data(Send(new byte[] { 0xFF, 0xB0, 0x00, (byte)address, 0x10 }));
            case CardFamily.Iso15693:
                var result = Data(Send(new byte[] { 0xFF, 0xFB, 0x00, 0x00, 0x02, 0x20, (byte)address }));
                if (result.Length < 2 || result[0] != 0) throw new IOException("ISO15693 블록 읽기 실패");
                return result[1..];
            case CardFamily.FelicaLiteS:
                var frame = Hex.Parse($"1006{card.Uid}010B000180{address:X2}");
                var response = Data(Send(new byte[] { 0xFF, 0x00, 0x00, 0x00, (byte)frame.Length }.Concat(frame).ToArray()));
                return response.Length == 16 ? response : FelicaFrames.ReadData(response, card.Uid);
            default: throw new NotSupportedException("이 카드의 직접 메모리 읽기는 지원하지 않습니다.");
        }
    }

    public void WriteUnit(CardInfo card, int address, byte[] data, string? keyHex = null, bool keyB = false)
    {
        CheckCard(card);
        WriteGuard.Validate(card, address, data.Length);
        switch (card.Family) {
            case CardFamily.Ntag:
                Data(Send(new byte[] { 0xFF, 0xD6, 0x00, (byte)address, 0x04 }.Concat(data).ToArray())); break;
            case CardFamily.MifareClassic:
                Authenticate(address, keyHex, keyB);
                Data(Send(new byte[] { 0xFF, 0xD6, 0x00, (byte)address, 0x10 }.Concat(data).ToArray())); break;
            case CardFamily.Iso15693:
                if (data.Length > 250) throw new ArgumentOutOfRangeException(nameof(data));
                var result = Data(Send(new byte[] { 0xFF, 0xFB, 0x00, 0x00, (byte)(data.Length + 2), 0x21, (byte)address }.Concat(data).ToArray()));
                if (result.Length > 0 && result[0] != 0) throw new IOException("ISO15693 블록 쓰기 실패");
                break;
            case CardFamily.FelicaLiteS:
                var frame = Hex.Parse($"2008{card.Uid}0109000180{address:X2}{Hex.Format(data)}");
                var response = Data(Send(new byte[] { 0xFF, 0x00, 0x00, 0x00, (byte)frame.Length }.Concat(frame).ToArray()));
                if (response.Length != 0) FelicaFrames.CheckWrite(response, card.Uid);
                break;
            default: throw new NotSupportedException();
        }
    }

    public byte[] TransmitApdu(byte[] command) => Send(command);

    public void Dispose()
    {
        _card?.Dispose(); _card = null;
        _context?.Dispose(); _context = null;
        _uid = "";
    }
}

// An IOException, so reads report it per address like any other read failure instead of aborting.
sealed class CardLiftedException() : IOException("카드가 리더에서 떨어졌습니다. 카드를 다시 올려 주세요.");
