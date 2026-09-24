using System.Text;

namespace NfcTagger.Core;

public sealed record NdefDocument(string Summary, string RawHex, int Length);

public static class NdefCodec
{
    public static byte[] Text(string value)
    {
        var body = new byte[] { 0x02, (byte)'k', (byte)'o' }.Concat(Encoding.UTF8.GetBytes(value)).ToArray();
        return Record((byte)'T', body);
    }

    public static byte[] Uri(string value) => Record((byte)'U', new byte[] { 0x00 }.Concat(Encoding.UTF8.GetBytes(value)).ToArray());

    private static byte[] Record(byte type, byte[] payload)
    {
        if (payload.Length > 65535) throw new ArgumentOutOfRangeException(nameof(payload));
        if (payload.Length <= 255) return new byte[] { 0xD1, 0x01, (byte)payload.Length, type }.Concat(payload).ToArray();
        return new byte[] { 0xC1, 0x01, (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, type }.Concat(payload).ToArray();
    }

    public static NdefDocument Describe(byte[] message)
    {
        if (message.Length == 0) return new("NDEF 비어 있음", "", 0);
        if (message.Length < 4) throw new IOException("NDEF 메시지가 너무 짧습니다.");
        var offset = 0;
        var descriptions = new List<string>();
        while (offset < message.Length) {
            var header = message[offset++];
            var tnf = header & 0x07;
            var typeLength = message[offset++];
            var shortRecord = (header & 0x10) != 0;
            var idPresent = (header & 0x08) != 0;
            if (offset + (shortRecord ? 1 : 4) > message.Length) throw new IOException("NDEF 길이 필드가 짧습니다.");
            int length;
            if (shortRecord) length = message[offset++];
            else { length = (message[offset] << 24) | (message[offset + 1] << 16) | (message[offset + 2] << 8) | message[offset + 3]; offset += 4; }
            if (idPresent && offset >= message.Length) throw new IOException("NDEF ID 길이가 없습니다.");
            var idLength = idPresent ? message[offset++] : 0;
            if (length < 0 || offset + typeLength + idLength + length > message.Length) throw new IOException("NDEF 레코드 길이가 잘못되었습니다.");
            var type = Encoding.ASCII.GetString(message, offset, typeLength); offset += typeLength + idLength;
            var payload = message.AsSpan(offset, length); offset += length;
            if (tnf == 1 && type == "T" && payload.Length > 0) {
                var languageLength = payload[0] & 0x3F;
                if (payload.Length < 1 + languageLength) throw new IOException("NDEF 텍스트 레코드가 잘못되었습니다.");
                var encoding = (payload[0] & 0x80) != 0 ? Encoding.BigEndianUnicode : Encoding.UTF8;
                descriptions.Add("텍스트: " + encoding.GetString(payload[(1 + languageLength)..]));
            } else if (tnf == 1 && type == "U" && payload.Length > 0) {
                string[] prefixes = ["", "http://www.", "https://www.", "http://", "https://", "tel:", "mailto:",
                    "ftp://anonymous:anonymous@", "ftp://ftp.", "ftps://", "sftp://", "smb://", "nfs://", "ftp://",
                    "dav://", "news:", "telnet://", "imap:", "rtsp://", "urn:", "pop:", "sip:", "sips:", "tftp:",
                    "btspp://", "btl2cap://", "btgoep://", "tcpobex://", "irdaobex://", "file://", "urn:epc:id:",
                    "urn:epc:tag:", "urn:epc:pat:", "urn:epc:raw:", "urn:epc:", "urn:nfc:"];
                var prefix = payload[0] < prefixes.Length ? prefixes[payload[0]] : "";
                descriptions.Add("URL: " + prefix + Encoding.UTF8.GetString(payload[1..]));
            } else descriptions.Add($"TNF {tnf} / {type} 레코드 · {length}바이트");
            if ((header & 0x40) != 0) break;
        }
        return new(string.Join("\n", descriptions), Hex.Format(message), message.Length);
    }

    public static (int Offset, int Length, int HeaderLength) FindTlv(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length;) {
            var type = bytes[i];
            if (type == 0x00) { i++; continue; }
            if (type == 0xFE) break;
            if (i + 1 >= bytes.Length) break;
            var header = 2;
            int length = bytes[i + 1];
            if (length == 0xFF) {
                if (i + 3 >= bytes.Length) break;
                length = (bytes[i + 2] << 8) | bytes[i + 3];
                header = 4;
            }
            if (i + header + length > bytes.Length) break;
            if (type == 0x03) return (i, length, header);
            i += header + length;
        }
        throw new IOException("NDEF TLV가 없습니다. 먼저 NDEF로 포맷된 테스트 태그를 사용하세요.");
    }
}

public static class NdefService
{
    public static CardDump Backup(INfcReader reader, CardInfo card)
    {
        int first;
        int count;
        int size;
        switch (card.Family) {
            case CardFamily.Ntag:
                var cc2 = reader.ReadUnit(card, 3);
                if (cc2.Length < 3 || cc2[0] != 0xE1) throw new IOException("NTAG NDEF CC가 없습니다.");
                first = 4; size = 4; count = Math.Min(cc2[2] * 2, 252);
                break;
            case CardFamily.Iso15693:
                var type5 = GetType5Info(reader, card);
                first = type5.FirstAddress; size = type5.UnitSize;
                count = Math.Min((type5.Capacity + size - 1) / size, 256 - first);
                break;
            case CardFamily.FelicaLiteS:
                var attr = reader.ReadUnit(card, 0);
                ValidateType3(attr);
                first = 0; size = 16; count = 1 + Math.Min((attr[3] << 8) | attr[4], 13);
                break;
            default: throw new NotSupportedException("이 카드의 NDEF 백업을 지원하지 않습니다.");
        }
        var units = new List<MemoryUnit>();
        for (var address = first; address < first + count; address++)
            units.Add(new(address, Hex.Format(reader.ReadUnit(card, address)), null));
        return new(reader.Name, card, DateTimeOffset.Now, size, units);
    }

    public static NdefDocument Read(INfcReader reader, CardInfo card, string? keyHex = null, bool keyB = false)
    {
        var message = card.Family switch {
            CardFamily.Ntag => ReadTlv(reader, card, 3, 4, 4, keyHex, keyB),
            CardFamily.Iso15693 => ReadType5(reader, card),
            CardFamily.FelicaLiteS => ReadType3(reader, card),
            _ => throw new NotSupportedException("이 카드의 NDEF 읽기는 지원하지 않습니다. 메모리 또는 APDU 화면을 사용하세요.")
        };
        return NdefCodec.Describe(message);
    }

    public static NdefDocument Write(INfcReader reader, CardInfo card, bool uri, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("쓸 내용을 입력하세요.");
        var message = uri ? NdefCodec.Uri(value.Trim()) : NdefCodec.Text(value);
        switch (card.Family) {
            case CardFamily.Ntag: WriteTlv(reader, card, 3, 4, 4, message); break;
            case CardFamily.Iso15693: WriteType5(reader, card, message); break;
            case CardFamily.FelicaLiteS: WriteType3(reader, card, message); break;
            default: throw new NotSupportedException("이 카드의 NDEF 쓰기는 지원하지 않습니다.");
        }
        var check = Read(reader, card);
        if (!check.RawHex.Equals(Hex.Format(message), StringComparison.OrdinalIgnoreCase))
            throw new IOException("NDEF 쓰기 후 검증에 실패했습니다.");
        return check;
    }

    private static byte[] ReadTlv(INfcReader reader, CardInfo card, int ccAddress, int firstAddress, int unitSize, string? key, bool keyB, int? capacityOverride = null)
    {
        var cc = reader.ReadUnit(card, ccAddress, key, keyB);
        if (cc.Length < 3 || cc[0] is not (0xE1 or 0xE2)) throw new IOException("NDEF CC가 없습니다.");
        var capacity = capacityOverride ?? Math.Min(cc[2] * 8, 2048);
        var memory = ReadBytes(reader, card, firstAddress, capacity, unitSize, key, keyB);
        var tlv = NdefCodec.FindTlv(memory);
        return memory.AsSpan(tlv.Offset + tlv.HeaderLength, tlv.Length).ToArray();
    }

    private static byte[] ReadType5(INfcReader reader, CardInfo card)
    {
        var info = GetType5Info(reader, card);
        return ReadTlv(reader, card, 0, info.FirstAddress, info.UnitSize, null, false, info.Capacity);
    }

    private sealed record Type5Info(int FirstAddress, int UnitSize, int Capacity);

    private static Type5Info GetType5Info(INfcReader reader, CardInfo card)
    {
        var firstBlock = reader.ReadUnit(card, 0);
        if (firstBlock.Length < 4 || firstBlock[0] is not (0xE1 or 0xE2))
            throw new IOException("ISO15693 NDEF CC가 없습니다.");
        var ccLength = firstBlock[0] == 0xE2 ? 8 : 4;
        var unitSize = firstBlock.Length;
        var cc = new byte[ccLength];
        firstBlock.AsSpan(0, Math.Min(ccLength, unitSize)).CopyTo(cc);
        if (ccLength > unitSize) {
            var extra = reader.ReadUnit(card, 1);
            if (extra.Length != unitSize) throw new IOException("ISO15693 CC 블록 길이가 다릅니다.");
            extra.AsSpan(0, ccLength - unitSize).CopyTo(cc.AsSpan(unitSize));
        }
        var sizeUnits = cc[2] != 0 ? cc[2] : ccLength == 8 ? (cc[6] << 8) | cc[7] : 0;
        if (sizeUnits == 0) throw new IOException("ISO15693 NDEF 용량을 알 수 없습니다.");
        var first = (ccLength + unitSize - 1) / unitSize;
        return new(first, unitSize, Math.Min(sizeUnits * 8, (256 - first) * unitSize));
    }

    private static byte[] ReadBytes(INfcReader reader, CardInfo card, int firstAddress, int capacity, int unitSize, string? key, bool keyB)
    {
        if (unitSize <= 0) throw new IOException("카드 블록 크기가 올바르지 않습니다.");
        var bytes = new byte[capacity];
        for (var i = 0; i < capacity; i += unitSize) {
            var unit = reader.ReadUnit(card, firstAddress + i / unitSize, key, keyB);
            if (unit.Length != unitSize) throw new IOException("카드 블록 크기가 변경되었습니다.");
            unit.AsSpan(0, Math.Min(unitSize, capacity - i)).CopyTo(bytes.AsSpan(i));
        }
        return bytes;
    }

    private static void WriteTlv(INfcReader reader, CardInfo card, int ccAddress, int firstAddress, int unitSize, byte[] message, int? capacityOverride = null)
    {
        var cc = reader.ReadUnit(card, ccAddress);
        if (cc.Length < 4) throw new IOException("NDEF CC가 짧습니다.");
        var writable = card.Family == CardFamily.Iso15693 ? (cc[1] & 0x03) == 0 : (cc[3] & 0x0F) == 0;
        if (cc[0] is not (0xE1 or 0xE2) || !writable)
            throw new IOException("NDEF 포맷 또는 쓰기 권한을 확인할 수 없습니다.");
        var capacity = capacityOverride ?? Math.Min(cc[2] * 8, 2048);
        var before = ReadBytes(reader, card, firstAddress, capacity, unitSize, null, false);
        var tlv = NdefCodec.FindTlv(before);
        var afterOld = tlv.Offset + tlv.HeaderLength + tlv.Length;
        if (afterOld < before.Length && before[afterOld] is not (0x00 or 0xFE))
            throw new IOException("NDEF 뒤에 다른 TLV가 있어 안전한 덮어쓰기를 보장할 수 없습니다.");
        var header = message.Length <= 254 ? new byte[] { 0x03, (byte)message.Length } : new byte[] { 0x03, 0xFF, (byte)(message.Length >> 8), (byte)message.Length };
        var replacement = header.Concat(message).Append((byte)0xFE).ToArray();
        if (tlv.Offset + replacement.Length > before.Length) throw new IOException("태그 용량이 부족합니다.");
        var next = before.ToArray();
        replacement.CopyTo(next, tlv.Offset);
        var headerBlockStart = (tlv.Offset / unitSize) * unitSize;
        var staged = next.ToArray();
        if (header.Length == 2) staged[tlv.Offset + 1] = 0;
        else { staged[tlv.Offset + 2] = 0; staged[tlv.Offset + 3] = 0; }
        staged[tlv.Offset + header.Length] = 0xFE;
        if (!before.AsSpan(headerBlockStart, unitSize).SequenceEqual(staged.AsSpan(headerBlockStart, unitSize)))
            CardWorkflows.WriteVerified(reader, card, firstAddress + headerBlockStart / unitSize,
                staged.AsSpan(headerBlockStart, unitSize).ToArray(), null, false);
        for (var i = 0; i < next.Length; i += unitSize) {
            if (i == headerBlockStart) continue;
            var length = Math.Min(unitSize, next.Length - i);
            if (length != unitSize) break;
            if (before.AsSpan(i, unitSize).SequenceEqual(next.AsSpan(i, unitSize))) continue;
            CardWorkflows.WriteVerified(reader, card, firstAddress + i / unitSize, next.AsSpan(i, unitSize).ToArray(), null, false);
        }
        if (!staged.AsSpan(headerBlockStart, unitSize).SequenceEqual(next.AsSpan(headerBlockStart, unitSize)))
            CardWorkflows.WriteVerified(reader, card, firstAddress + headerBlockStart / unitSize,
                next.AsSpan(headerBlockStart, unitSize).ToArray(), null, false);
    }

    private static void WriteType5(INfcReader reader, CardInfo card, byte[] message)
    {
        var info = GetType5Info(reader, card);
        WriteTlv(reader, card, 0, info.FirstAddress, info.UnitSize, message, info.Capacity);
    }

    private static byte[] ReadType3(INfcReader reader, CardInfo card)
    {
        var attr = reader.ReadUnit(card, 0);
        ValidateType3(attr);
        var length = (attr[11] << 16) | (attr[12] << 8) | attr[13];
        var maxBlocks = Math.Min((attr[3] << 8) | attr[4], 13);
        if (length > maxBlocks * 16) throw new IOException("FeliCa NDEF 길이가 용량을 초과합니다.");
        var output = new byte[length];
        for (var i = 0; i < length; i += 16) {
            var block = reader.ReadUnit(card, 1 + i / 16);
            block.AsSpan(0, Math.Min(16, length - i)).CopyTo(output.AsSpan(i));
        }
        return output;
    }

    private static void ValidateType3(byte[] attr)
    {
        if (attr.Length != 16 || attr[0] != 0x10) throw new IOException("FeliCa Type 3 NDEF 속성 블록이 아닙니다.");
        var sum = attr.AsSpan(0, 14).ToArray().Sum(x => x);
        if (((attr[14] << 8) | attr[15]) != sum) throw new IOException("FeliCa 속성 블록 체크섬 오류");
    }

    private static void WriteType3(INfcReader reader, CardInfo card, byte[] message)
    {
        var attr = reader.ReadUnit(card, 0);
        ValidateType3(attr);
        if (attr[10] != 0x01) throw new IOException("FeliCa 태그가 읽기 전용입니다.");
        var maxBlocks = Math.Min((attr[3] << 8) | attr[4], 13);
        if (message.Length > maxBlocks * 16) throw new IOException("FeliCa 태그 용량이 부족합니다.");
        var writing = attr.ToArray();
        writing[9] = 0x0F;
        writing[11] = writing[12] = writing[13] = 0;
        SetType3Checksum(writing);
        CardWorkflows.WriteVerified(reader, card, 0, writing, null, false);
        for (var i = 0; i < message.Length; i += 16) {
            var block = reader.ReadUnit(card, 1 + i / 16);
            message.AsSpan(i, Math.Min(16, message.Length - i)).CopyTo(block);
            CardWorkflows.WriteVerified(reader, card, 1 + i / 16, block, null, false);
        }
        var done = attr.ToArray();
        done[9] = 0x00;
        done[11] = (byte)(message.Length >> 16);
        done[12] = (byte)(message.Length >> 8);
        done[13] = (byte)message.Length;
        SetType3Checksum(done);
        CardWorkflows.WriteVerified(reader, card, 0, done, null, false);
    }

    private static void SetType3Checksum(byte[] attr)
    {
        var sum = attr.AsSpan(0, 14).ToArray().Sum(x => x);
        attr[14] = (byte)(sum >> 8); attr[15] = (byte)sum;
    }
}
