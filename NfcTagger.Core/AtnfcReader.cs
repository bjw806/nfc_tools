using System.IO.Ports;

namespace NfcTagger.Core;

public sealed class AtnfcReader(ReaderChoice choice) : INfcReader
{
    private SerialPort? _port;
    public ReaderKind Kind => choice.Kind;
    public string Name => choice.DisplayName;

    public void Open()
    {
        _port = new SerialPort(choice.DeviceId, 115200, Parity.None, 8, StopBits.One) {
            Handshake = Handshake.None, NewLine = "\n", ReadTimeout = 1500, WriteTimeout = 1500
        };
        _port.Open();
        _port.DiscardInBuffer();
        var model = Command("AT+GMM").FirstOrDefault(x => x.StartsWith("+GMM:", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (!model.Contains("NFC-10", StringComparison.OrdinalIgnoreCase)) {
            Dispose();
            throw new IOException($"선택한 포트에서 ATNFC 응답을 확인하지 못했습니다: {model}");
        }
        if (choice.Kind == ReaderKind.Atnfc103 && !model.Contains("103") ||
            choice.Kind == ReaderKind.Atnfc102 && !model.Contains("102")) {
            Dispose();
            throw new IOException($"선택한 모델과 실제 장치가 다릅니다: {model}");
        }
    }

    private IReadOnlyList<string> Command(string command)
    {
        if (_port is not { IsOpen: true }) throw new IOException("리더가 연결되지 않았습니다.");
        _port.Write(command + "\r\n");
        var lines = new List<string>();
        var deadline = DateTime.UtcNow.AddMilliseconds(1800);
        while (DateTime.UtcNow < deadline) {
            string line;
            try { line = _port.ReadLine().Trim(); }
            catch (TimeoutException) { continue; }
            if (line.Length == 0 || line.Equals(command, StringComparison.OrdinalIgnoreCase)) continue;
            if (line == "OK") return lines;
            if (line.StartsWith("+CME ERROR:", StringComparison.OrdinalIgnoreCase))
                throw new AtnfcException(line);
            // Unsolicited card-in/card-out notifications can arrive between reply lines.
            if (line.StartsWith("+EA:", StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(line);
        }
        throw new TimeoutException($"AT 명령 응답 시간 초과: {command.Split('=')[0]}");
    }

    private string Payload(string command, string prefix)
    {
        var line = Command(command).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? throw new IOException($"{prefix} 응답이 없습니다.") : line[prefix.Length..].Trim();
    }

    public CardInfo? Detect()
    {
        try {
            var fields = Payload("AT+FIND", "+FIND:").Split(',');
            if (fields.Length < 2) throw new IOException("카드 정보 형식이 잘못되었습니다.");
            var family = fields[1].ToUpperInvariant() switch {
                "01" => CardFamily.MifareClassic,
                "02" => CardFamily.Ntag,
                "03" or "04" or "07" => CardFamily.Iso14443_4,
                "05" => CardFamily.Iso15693,
                "06" when fields.Length >= 4 && fields[3].Equals("88B4", StringComparison.OrdinalIgnoreCase) => CardFamily.FelicaLiteS,
                _ => CardFamily.Unknown
            };
            return new(fields[0].ToUpperInvariant(), family, fields[1].ToUpperInvariant(), string.Join(" · ", fields.Skip(2)));
        } catch (AtnfcException e) when (e.Message.EndsWith("E1", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
    }

    public byte[] ReadUnit(CardInfo card, int address, string? keyHex = null, bool keyB = false)
    {
        if (address is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(address));
        return card.Family switch {
            CardFamily.Ntag => Hex.Parse(Payload($"AT+NTAGREAD={address},1", "+NTAGREAD:")),
            CardFamily.MifareClassic => ReadMifare(address, keyHex, keyB),
            CardFamily.Iso15693 => Hex.Parse(Payload($"AT+15693READ={address},1,{card.Uid}", "+15693READ:")),
            CardFamily.FelicaLiteS => FelicaRead(card.Uid, address),
            _ => throw new NotSupportedException("이 카드의 직접 메모리 읽기는 지원하지 않습니다.")
        };
    }

    // AT+NTAGREAD takes up to 60 pages per command. A refused or odd-looking batch is re-read page by page,
    // so firmware without multi-page reads still works and a failing page reports its own error.
    public IReadOnlyList<byte[]> ReadUnits(CardInfo card, int address, int count, string? keyHex = null, bool keyB = false)
    {
        if (card.Family != CardFamily.Ntag || count < 2)
            return Enumerable.Range(address, count).Select(x => ReadUnit(card, x, keyHex, keyB)).ToList();
        if (address < 0 || address + count > 256) throw new ArgumentOutOfRangeException(nameof(address));
        var units = new List<byte[]>(count);
        for (var next = address; next < address + count;) {
            var n = Math.Min(60, address + count - next);
            byte[]? data = null;
            try { data = Hex.Parse(Payload($"AT+NTAGREAD={next},{n}", "+NTAGREAD:")); }
            catch (Exception e) when (e is IOException or ArgumentException) { }
            if (data?.Length == n * 4) units.AddRange(data.Chunk(4));
            else units.AddRange(Enumerable.Range(next, n).Select(x => ReadUnit(card, x)));
            next += n;
        }
        return units;
    }

    private byte[] ReadMifare(int address, string? keyHex, bool keyB)
    {
        Authenticate(address, keyHex, keyB);
        return Hex.Parse(Payload($"AT+M1READ={address}", "+M1READ:"));
    }

    private void Authenticate(int address, string? keyHex, bool keyB)
    {
        if (keyHex is null || Hex.Parse(keyHex).Length != 6) throw new ArgumentException("MIFARE Classic 6바이트 키를 입력하세요.");
        Command($"AT+M1AUTH={address},{(keyB ? "B" : "A")},{keyHex.Replace(" ", "").ToUpperInvariant()}");
    }

    public void WriteUnit(CardInfo card, int address, byte[] data, string? keyHex = null, bool keyB = false)
    {
        WriteGuard.Validate(card, address, data.Length);
        var hex = Hex.Format(data);
        switch (card.Family) {
            case CardFamily.Ntag: Command($"AT+NTAGWRITE={address},{hex}"); break;
            case CardFamily.MifareClassic:
                Authenticate(address, keyHex, keyB);
                Command($"AT+M1WRITE={address},{hex}"); break;
            case CardFamily.Iso15693: Command($"AT+15693WRITE={address},{hex},{card.Uid}"); break;
            case CardFamily.FelicaLiteS: FelicaWrite(card.Uid, address, data); break;
            default: throw new NotSupportedException();
        }
    }

    public byte[] TransmitApdu(byte[] command) => Hex.Parse(Payload($"AT+APDU={Hex.Format(command)}", "+APDU:"));

    private byte[] FelicaRead(string idm, int block)
    {
        var frame = $"1006{idm}010B000180{block:X2}";
        var reply = Hex.Parse(Payload($"AT+FELICA={frame},CRC,6", "+FELICA:"));
        return FelicaFrames.ReadData(reply, idm);
    }

    private void FelicaWrite(string idm, int block, byte[] data)
    {
        var frame = $"2008{idm}0109000180{block:X2}{Hex.Format(data)}";
        var reply = Hex.Parse(Payload($"AT+FELICA={frame},CRC,6", "+FELICA:"));
        FelicaFrames.CheckWrite(reply, idm);
    }

    public void Dispose()
    {
        _port?.Dispose();
        _port = null;
    }
}

public sealed class AtnfcException(string message) : IOException(message);

public static class FelicaFrames
{
    public static byte[] ReadData(byte[] response, string idm)
    {
        // Reader firmware may include or omit the leading LEN field.
        var start = response.Length > 0 && response[0] == response.Length ? 1 : 0;
        if (response.Length < start + 13 || response[start] != 0x07 ||
            !response.AsSpan(start + 1, 8).SequenceEqual(Hex.Parse(idm)) ||
            response[start + 9] != 0 || response[start + 10] != 0 || response[start + 11] != 1)
            throw new IOException("FeliCa 읽기 응답 또는 상태 플래그가 올바르지 않습니다.");
        if (response.Length < start + 12 + 16) throw new IOException("FeliCa 블록 데이터가 짧습니다.");
        return response.AsSpan(start + 12, 16).ToArray();
    }
    public static void CheckWrite(byte[] response, string idm)
    {
        var start = response.Length > 0 && response[0] == response.Length ? 1 : 0;
        if (response.Length < start + 11 || response[start] != 0x09 ||
            !response.AsSpan(start + 1, 8).SequenceEqual(Hex.Parse(idm)) ||
            response[start + 9] != 0 || response[start + 10] != 0)
            throw new IOException("FeliCa 쓰기 응답 또는 상태 플래그가 올바르지 않습니다.");
    }
}
