using System.IO.Ports;

namespace NfcTagger.Core;

public sealed class AtnfcReader(ReaderChoice choice) : INfcReader
{
    private SerialPort? _port;
    private bool _restoreUrc, _restoreBeep;
    private string? _lastUid;
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
        // With URC on, the reader's own card search pushes "+FIND:…" + "OK" and "+CME ERROR:EA" whenever a card
        // comes or goes. They are indistinguishable from command replies, so URC is switched off (not saved) while
        // this app owns the port and restored on Dispose. Until it is off a report can answer in place of the reply,
        // so the setting is read back until it sticks. Firmware without AT+CURC simply keeps its setting.
        try {
            _restoreUrc = Query("AT+CURC?", "+CURC:") == "1";
            for (var i = 0; _restoreUrc && i < 3 && Query("AT+CURC?", "+CURC:") != "0"; i++)
                try { Command("AT+CURC=0"); } catch (IOException) { /* a card report answered; read back and retry */ }
        } catch (TimeoutException) { }
        // The 103 beeps from its own card search, which races this app's polling: a new card beeped only when the
        // reader saw it first. Its beep policy is switched off (not saved) and Detect beeps once per new card instead.
        if (choice.Kind == ReaderKind.Atnfc103)
            try {
                _restoreBeep = Query("AT+BEEPEN?", "+BEEPEN:") == "1";
                if (_restoreBeep) Command("AT+BEEPEN=0");
            } catch (Exception e) when (e is IOException or TimeoutException) { }
    }

    // Asks up to three times: a card report can arrive in place of the reply.
    private string? Query(string command, string prefix)
    {
        for (var attempt = 0; attempt < 3; attempt++) {
            try { return Payload(command, prefix); }
            catch (IOException) { }
        }
        return null;
    }

    private IReadOnlyList<string> Command(string command)
    {
        if (_port is not { IsOpen: true }) throw new IOException("리더가 연결되지 않았습니다.");
        _port.DiscardInBuffer(); // a late or unsolicited reply must not answer this command
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
            // Older firmware's card-in/card-out notification; current firmware reports are silenced in Open.
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
            var uid = fields[0].ToUpperInvariant();
            if (_restoreBeep && uid != _lastUid)
                try { Command("AT+BEEP=1"); } catch (IOException) { /* a missed beep must not fail detection */ }
            _lastUid = uid;
            return new(uid, family, fields[1].ToUpperInvariant(), string.Join(" · ", fields.Skip(2)));
        } catch (AtnfcException e) when (e.Message.EndsWith("E1", StringComparison.OrdinalIgnoreCase)) {
            _lastUid = null;
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
        // Put back what Open switched off; an unplugged reader gets its saved settings on power-up anyway.
        foreach (var (restore, command) in new[] { (_restoreBeep, "AT+BEEPEN=1"), (_restoreUrc, "AT+CURC=1") })
            if (restore) try { Command(command); } catch (Exception) { }
        _restoreBeep = _restoreUrc = false;
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
