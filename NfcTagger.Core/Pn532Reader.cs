using System.IO.Ports;

namespace NfcTagger.Core;

public sealed class Pn532Reader(ReaderChoice choice) : INfcReader
{
    private SerialPort? _port;
    private byte _target = 1;
    private string _uid = "";
    public ReaderKind Kind => ReaderKind.Pcr532;
    public string Name => choice.DisplayName;

    public void Open()
    {
        _port = new SerialPort(choice.DeviceId, 115200, Parity.None, 8, StopBits.One) {
            ReadTimeout = 1700, WriteTimeout = 1700, Handshake = Handshake.None
        };
        _port.Open();
        _port.DiscardInBuffer();
        _port.Write(new byte[] { 0x55, 0x55, 0x00, 0x00, 0x00 }, 0, 5);
        var version = Command(0x02);
        if (version.Length < 4 || version[0] != 0x32) {
            Dispose();
            throw new IOException("PCR532 포트에서 PN532 펌웨어 응답을 확인하지 못했습니다.");
        }
        Command(0x14, 0x01, 0x14, 0x01); // SAM normal mode
        Command(0x32, 0x05, 0xFF, 0x01, 0x01); // one passive activation retry
    }

    private byte[] Command(byte command, params byte[] args)
    {
        if (_port is not { IsOpen: true } port) throw new IOException("리더가 연결되지 않았습니다.");
        var data = new byte[2 + args.Length];
        data[0] = 0xD4; data[1] = command;
        args.CopyTo(data, 2);
        var frame = Pn532Frames.Encode(data);
        port.Write(frame, 0, frame.Length);
        var response = Pn532Frames.ReadFrame(port);
        if (response.Length < 2 || response[0] != 0xD5 || response[1] != command + 1)
            throw new IOException("PN532 명령 응답 코드가 올바르지 않습니다.");
        return response[2..];
    }

    public CardInfo? Detect()
    {
        var a = Command(0x4A, 0x01, 0x00);
        if (a.Length >= 6 && a[0] > 0) {
            _target = a[1];
            var uidLength = a[5];
            if (a.Length < 6 + uidLength) throw new IOException("PN532 UID 응답이 짧습니다.");
            _uid = Hex.Format(a.AsSpan(6, uidLength).ToArray());
            var sak = a[4];
            var family = sak switch {
                0x08 or 0x18 => CardFamily.MifareClassic,
                0x00 => CardFamily.Ntag,
                _ when (sak & 0x20) != 0 => CardFamily.Iso14443_4,
                _ => CardFamily.Unknown
            };
            return new(_uid, family, $"SAK {sak:X2}", $"ATQA {a[2]:X2}{a[3]:X2}");
        }
        var f = Command(0x4A, 0x01, 0x01, 0x00, 0x88, 0xB4, 0x01, 0x00);
        if (f.Length >= 13 && f[0] > 0) {
            _target = f[1];
            // POL_RES length and response code precede the eight-byte IDm.
            var idmOffset = 4;
            if (f.Length < idmOffset + 8) throw new IOException("FeliCa IDm 응답이 짧습니다.");
            _uid = Hex.Format(f.AsSpan(idmOffset, 8).ToArray());
            return new(_uid, CardFamily.FelicaLiteS, "FeliCa", "시스템 코드 88B4");
        }
        _uid = "";
        return null;
    }

    private byte[] Exchange(params byte[] data)
    {
        var response = Command(0x40, new byte[] { _target }.Concat(data).ToArray());
        if (response.Length == 0 || (response[0] & 0x3F) != 0)
            throw new IOException($"PN532 카드 교환 실패 (상태 {(response.Length == 0 ? "없음" : response[0].ToString("X2"))})");
        return response[1..];
    }

    private void CheckCard(CardInfo card)
    {
        if (_uid.Length == 0 || !_uid.Equals(card.Uid, StringComparison.OrdinalIgnoreCase))
            throw new IOException("카드 세션이 바뀌었습니다. 다시 감지하세요.");
    }

    public byte[] ReadUnit(CardInfo card, int address, string? keyHex = null, bool keyB = false)
    {
        CheckCard(card);
        if (address is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(address));
        switch (card.Family) {
            case CardFamily.Ntag:
                var pages = Exchange(0x30, (byte)address);
                if (pages.Length < 4) throw new IOException("NTAG 읽기 응답이 짧습니다.");
                return pages[..4];
            case CardFamily.MifareClassic:
                Authenticate(address, keyHex, keyB);
                var block = Exchange(0x30, (byte)address);
                if (block.Length < 16) throw new IOException("MIFARE 블록 응답이 짧습니다.");
                return block[..16];
            case CardFamily.FelicaLiteS:
                var frame = Hex.Parse($"1006{card.Uid}010B000180{address:X2}");
                return FelicaFrames.ReadData(Exchange(frame), card.Uid);
            default: throw new NotSupportedException("PCR532은 이 카드 메모리 읽기를 지원하지 않습니다.");
        }
    }

    private void Authenticate(int address, string? keyHex, bool keyB)
    {
        if (keyHex is null) throw new ArgumentException("MIFARE Classic 키가 필요합니다.");
        var key = Hex.Parse(keyHex);
        if (key.Length != 6) throw new ArgumentException("MIFARE Classic 키는 6바이트여야 합니다.");
        var uid = Hex.Parse(_uid);
        if (uid.Length < 4) throw new IOException("인증용 UID가 짧습니다.");
        Exchange(new byte[] { keyB ? (byte)0x61 : (byte)0x60, (byte)address }
            .Concat(key).Concat(uid.TakeLast(4)).ToArray());
    }

    public void WriteUnit(CardInfo card, int address, byte[] data, string? keyHex = null, bool keyB = false)
    {
        CheckCard(card);
        WriteGuard.Validate(card, address, data.Length);
        switch (card.Family) {
            case CardFamily.Ntag:
                Exchange(new byte[] { 0xA2, (byte)address }.Concat(data).ToArray()); break;
            case CardFamily.MifareClassic:
                Authenticate(address, keyHex, keyB);
                Exchange(new byte[] { 0xA0, (byte)address }.Concat(data).ToArray()); break;
            case CardFamily.FelicaLiteS:
                var frame = Hex.Parse($"2008{card.Uid}0109000180{address:X2}{Hex.Format(data)}");
                FelicaFrames.CheckWrite(Exchange(frame), card.Uid); break;
            default: throw new NotSupportedException();
        }
    }

    public byte[] TransmitApdu(byte[] command) => Exchange(command);

    public void Dispose() { _port?.Dispose(); _port = null; _uid = ""; }
}

public static class Pn532Frames
{
    public static byte[] Encode(byte[] data)
    {
        if (data.Length > 255) throw new ArgumentOutOfRangeException(nameof(data), "PN532 프레임은 255바이트 이하입니다.");
        var frame = new byte[data.Length + 7];
        frame[0] = 0; frame[1] = 0; frame[2] = 0xFF;
        frame[3] = (byte)data.Length; frame[4] = unchecked((byte)-data.Length);
        data.CopyTo(frame, 5);
        frame[^2] = unchecked((byte)-data.Sum(x => x));
        frame[^1] = 0;
        return frame;
    }

    public static byte[] Decode(byte[] frame)
    {
        if (frame.Length < 7 || frame[0] != 0 || frame[1] != 0 || frame[2] != 0xFF)
            throw new IOException("PN532 프레임 시작이 올바르지 않습니다.");
        var length = frame[3];
        if (frame.Length != length + 7 || unchecked((byte)(length + frame[4])) != 0 || frame[^1] != 0)
            throw new IOException("PN532 프레임 길이가 올바르지 않습니다.");
        if (unchecked((byte)frame.AsSpan(5, length + 1).ToArray().Sum(x => x)) != 0)
            throw new IOException("PN532 체크섬이 올바르지 않습니다.");
        return frame.AsSpan(5, length).ToArray();
    }

    public static byte[] ReadFrame(SerialPort port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var state = 0;
        while (DateTime.UtcNow < deadline) {
            int value;
            try { value = port.ReadByte(); } catch (TimeoutException) { continue; }
            state = state switch { 0 when value == 0 => 1, 1 when value == 0 => 2, 2 when value == 0xFF => 3, _ => value == 0 ? 1 : 0 };
            if (state != 3) continue;
            var len = port.ReadByte();
            var lcs = port.ReadByte();
            if (len == 0 && lcs == 0xFF) { port.ReadByte(); state = 0; continue; } // ACK
            if (len is < 0 or > 255) throw new IOException("PN532 프레임 길이 오류");
            var rest = new byte[len + 2];
            var offset = 0;
            while (offset < rest.Length) {
                var n = port.Read(rest, offset, rest.Length - offset);
                if (n <= 0) throw new TimeoutException("PN532 프레임 수신 시간 초과");
                offset += n;
            }
            return Decode(new byte[] { 0, 0, 0xFF, (byte)len, (byte)lcs }.Concat(rest).ToArray());
        }
        throw new TimeoutException("PN532 응답 시간 초과");
    }
}
