using System.Text;

namespace NfcTagger.Core;

public sealed record NdefDocument(string Summary, string RawHex, int Length)
{
    public NdefWriteInfo? WriteInfo { get; init; }
}

public enum NdefWriteStatus { Ready, ReadOnly, FollowingTlv }

// Message capacity excludes TLV overhead and stops before any reserved bytes.
public sealed record NdefWriteInfo(int Capacity, int MaxMessageLength, NdefWriteStatus Status)
{
    public bool CanWrite(int length) => Status == NdefWriteStatus.Ready && length > 0 && length <= MaxMessageLength;
    public string? WriteError => Status switch {
        NdefWriteStatus.ReadOnly => Strings.NdefReadOnly,
        NdefWriteStatus.FollowingTlv => Strings.NdefTrailingTlv,
        _ => null
    };
}

public static class NdefCodec
{
    // language is an IANA code such as "en" or "ko"; its length goes in the low 6 bits of the status byte.
    public static byte[] Text(string value, string language)
    {
        var code = Encoding.ASCII.GetBytes(language);
        ArgumentOutOfRangeException.ThrowIfZero(code.Length, nameof(language));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code.Length, 63, nameof(language));
        return Record((byte)'T', new[] { (byte)code.Length }.Concat(code).Concat(Encoding.UTF8.GetBytes(value)).ToArray());
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
        if (message.Length == 0) return new(Strings.NdefEmpty, "", 0);
        if (message.Length < 4) throw new IOException(Strings.NdefTooShort);
        var offset = 0;
        var descriptions = new List<string>();
        while (offset < message.Length) {
            if (message.Length - offset < 2) throw new IOException(Strings.NdefTooShort);
            var header = message[offset++];
            var tnf = header & 0x07;
            var typeLength = message[offset++];
            var shortRecord = (header & 0x10) != 0;
            var idPresent = (header & 0x08) != 0;
            if (offset + (shortRecord ? 1 : 4) > message.Length) throw new IOException(Strings.NdefLengthShort);
            int length;
            if (shortRecord) length = message[offset++];
            else { length = (message[offset] << 24) | (message[offset + 1] << 16) | (message[offset + 2] << 8) | message[offset + 3]; offset += 4; }
            if (idPresent && offset >= message.Length) throw new IOException(Strings.NdefIdLengthMissing);
            var idLength = idPresent ? message[offset++] : 0;
            if (length < 0 || length > message.Length - offset - typeLength - idLength) throw new IOException(Strings.NdefRecordLength);
            var type = Encoding.ASCII.GetString(message, offset, typeLength); offset += typeLength + idLength;
            var payload = message.AsSpan(offset, length); offset += length;
            if (tnf == 1 && type == "T" && payload.Length > 0) {
                var languageLength = payload[0] & 0x3F;
                if (payload.Length < 1 + languageLength) throw new IOException(Strings.NdefTextRecord);
                var encoding = (payload[0] & 0x80) != 0 ? Encoding.BigEndianUnicode : Encoding.UTF8;
                var language = Encoding.ASCII.GetString(payload[1..(1 + languageLength)]);
                descriptions.Add(Strings.NdefText(encoding.GetString(payload[(1 + languageLength)..]), language));
            } else if (tnf == 1 && type == "U" && payload.Length > 0) {
                string[] prefixes = ["", "http://www.", "https://www.", "http://", "https://", "tel:", "mailto:",
                    "ftp://anonymous:anonymous@", "ftp://ftp.", "ftps://", "sftp://", "smb://", "nfs://", "ftp://",
                    "dav://", "news:", "telnet://", "imap:", "rtsp://", "urn:", "pop:", "sip:", "sips:", "tftp:",
                    "btspp://", "btl2cap://", "btgoep://", "tcpobex://", "irdaobex://", "file://", "urn:epc:id:",
                    "urn:epc:tag:", "urn:epc:pat:", "urn:epc:raw:", "urn:epc:", "urn:nfc:"];
                var prefix = payload[0] < prefixes.Length ? prefixes[payload[0]] : "";
                descriptions.Add("URL: " + prefix + Encoding.UTF8.GetString(payload[1..]));
            } else descriptions.Add(Strings.NdefOtherRecord(tnf, type, length));
            if ((header & 0x40) != 0) break;
        }
        return new(string.Join("\n", descriptions), Hex.Format(message), message.Length);
    }

    public static (int Offset, int Length, int HeaderLength) FindTlv(ReadOnlySpan<byte> bytes)
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
        throw new IOException(Strings.NdefNoTlv);
    }

    // Type 2 lock and memory control TLVs mark bytes that NDEF data has to skip. NXP tags keep them after the
    // data area. Skipping isn't implemented, so callers refuse a message that would cover them.
    public static bool CoversReservedBytes(byte[] area, int areaStart, int from, int to) =>
        ContiguousEnd(area, areaStart, from) < to;

    internal static int ContiguousEnd(byte[] area, int areaStart, int from)
    {
        var end = area.Length;
        for (var i = 0; i + 4 < area.Length && area[i] is 0x00 or 0x01 or 0x02; i += area[i] == 0 ? 1 : 2 + area[i + 1]) {
            if (area[i] == 0 || area[i + 1] != 3) continue;
            int position = area[i + 2], size = area[i + 3] == 0 ? 256 : area[i + 3], pageSize = 1 << (area[i + 4] & 0x0F);
            var start = (position >> 4) * pageSize + (position & 0x0F);
            var length = area[i] == 0x01 ? (size + 7) / 8 : size; // lock control counts bits
            if (start + length > areaStart + from) end = Math.Min(end, Math.Max(from, start - areaStart));
        }
        return end;
    }
}

public static class NdefService
{
    private sealed record TlvInfo(int FirstAddress, int UnitSize, int Capacity, bool Writable);

    public static CardDump Backup(INfcReader reader, CardInfo card)
    {
        int first;
        int count;
        int size;
        switch (card.Family) {
            case CardFamily.Ntag:
            case CardFamily.Iso15693:
                var info = GetTlvInfo(reader, card);
                first = info.FirstAddress; size = info.UnitSize;
                count = (info.Capacity + size - 1) / size;
                break;
            case CardFamily.FelicaLiteS:
                var attr = reader.ReadUnit(card, 0);
                ValidateType3(attr);
                first = 0; size = 16; count = 1 + Type3Blocks(attr);
                break;
            default: throw new NotSupportedException(Strings.NdefBackupUnsupported);
        }
        var units = reader.ReadUnits(card, first, count).Select((data, i) => new MemoryUnit(first + i, Hex.Format(data), null)).ToList();
        return new(reader.Name, card, DateTimeOffset.Now, size, units);
    }

    public static NdefWriteInfo Inspect(INfcReader reader, CardInfo card)
    {
        CardWorkflows.CheckCard(reader, card);
        if (card.Family == CardFamily.FelicaLiteS) {
            var attr = reader.ReadUnit(card, 0);
            ValidateType3(attr);
            return Type3WriteInfo(attr);
        }
        var info = GetTlvInfo(reader, card);
        var memory = ReadBytes(reader, card, info, null, false);
        var tlv = NdefCodec.FindTlv(memory.AsSpan(0, info.Capacity));
        return TlvWriteInfo(card, info, memory, tlv);
    }

    public static NdefDocument Read(INfcReader reader, CardInfo card, string? keyHex = null, bool keyB = false)
    {
        var (message, info) = card.Family switch {
            CardFamily.Ntag or CardFamily.Iso15693 => ReadTlv(reader, card, GetTlvInfo(reader, card), keyHex, keyB),
            CardFamily.FelicaLiteS => ReadType3(reader, card),
            _ => throw new NotSupportedException(Strings.NdefReadUnsupported)
        };
        return NdefCodec.Describe(message) with { WriteInfo = info };
    }

    public static NdefDocument Write(INfcReader reader, CardInfo card, bool uri, string value, string language = "en")
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Strings.EnterContent);
        var message = uri ? NdefCodec.Uri(value.Trim()) : NdefCodec.Text(value, language);
        switch (card.Family) {
            case CardFamily.Ntag:
            case CardFamily.Iso15693: WriteTlv(reader, card, GetTlvInfo(reader, card), message); break;
            case CardFamily.FelicaLiteS: WriteType3(reader, card, message); break;
            default: throw new NotSupportedException(Strings.NdefWriteUnsupported);
        }
        var check = Read(reader, card);
        if (!check.RawHex.Equals(Hex.Format(message), StringComparison.OrdinalIgnoreCase))
            throw new IOException(Strings.NdefVerifyFailed);
        return check;
    }

    private static TlvInfo GetTlvInfo(INfcReader reader, CardInfo card)
    {
        if (card.Family == CardFamily.Ntag) {
            var cc = reader.ReadUnit(card, 3);
            return new(NtagMemory.FirstPage, NtagMemory.PageSize, NtagMemory.Capacity(cc), (cc[3] & 0x0F) == 0);
        }
        if (card.Family != CardFamily.Iso15693) throw new NotSupportedException(Strings.NdefReadUnsupported);
        var firstBlock = reader.ReadUnit(card, 0);
        if (firstBlock.Length < 4 || firstBlock[0] is not (0xE1 or 0xE2))
            throw new IOException(Strings.Iso15693NoCc);
        var ccLength = firstBlock[0] == 0xE2 ? 8 : 4;
        var unitSize = firstBlock.Length;
        var cc5 = new byte[ccLength];
        firstBlock.AsSpan(0, Math.Min(ccLength, unitSize)).CopyTo(cc5);
        if (ccLength > unitSize) {
            var extra = reader.ReadUnit(card, 1);
            if (extra.Length != unitSize) throw new IOException(Strings.Iso15693CcLength);
            extra.AsSpan(0, ccLength - unitSize).CopyTo(cc5.AsSpan(unitSize));
        }
        var sizeUnits = cc5[2] != 0 ? cc5[2] : ccLength == 8 ? (cc5[6] << 8) | cc5[7] : 0;
        if (sizeUnits == 0) throw new IOException(Strings.Iso15693Capacity);
        var first = (ccLength + unitSize - 1) / unitSize;
        return new(first, unitSize, Math.Min(sizeUnits * 8, (256 - first) * unitSize), (cc5[1] & 0x03) == 0);
    }

    private static byte[] ReadBytes(INfcReader reader, CardInfo card, TlvInfo info, string? key, bool keyB)
    {
        var count = (info.Capacity + info.UnitSize - 1) / info.UnitSize;
        // Keep the whole last block so writes preserve bytes beyond the declared NDEF area.
        var bytes = new byte[count * info.UnitSize];
        var units = reader.ReadUnits(card, info.FirstAddress, count, key, keyB);
        if (units.Count != count) throw new IOException(Strings.BlockSizeChanged);
        for (var i = 0; i < units.Count; i++) {
            if (units[i].Length != info.UnitSize) throw new IOException(Strings.BlockSizeChanged);
            units[i].CopyTo(bytes, i * info.UnitSize);
        }
        return bytes;
    }

    private static (byte[] Message, NdefWriteInfo Info) ReadTlv(INfcReader reader, CardInfo card, TlvInfo info, string? key, bool keyB)
    {
        var memory = ReadBytes(reader, card, info, key, keyB);
        var tlv = NdefCodec.FindTlv(memory.AsSpan(0, info.Capacity));
        if (card.Family == CardFamily.Ntag &&
            NdefCodec.CoversReservedBytes(memory, info.FirstAddress * info.UnitSize, tlv.Offset, tlv.Offset + tlv.HeaderLength + tlv.Length))
            throw new IOException(Strings.NdefReservedBytes);
        return (memory.AsSpan(tlv.Offset + tlv.HeaderLength, tlv.Length).ToArray(), TlvWriteInfo(card, info, memory, tlv));
    }

    private static NdefWriteInfo TlvWriteInfo(CardInfo card, TlvInfo info, byte[] memory, (int Offset, int Length, int HeaderLength) tlv)
    {
        var status = info.Writable ? NdefWriteStatus.Ready : NdefWriteStatus.ReadOnly;
        // A NULL TLV may separate the NDEF from another TLV; do not overwrite it either.
        for (var i = tlv.Offset + tlv.HeaderLength + tlv.Length; i < info.Capacity; i++) {
            if (memory[i] == 0x00) continue;
            if (memory[i] != 0xFE && status == NdefWriteStatus.Ready) status = NdefWriteStatus.FollowingTlv;
            break;
        }
        var end = card.Family == CardFamily.Ntag
            ? Math.Min(info.Capacity, NdefCodec.ContiguousEnd(memory, info.FirstAddress * info.UnitSize, tlv.Offset))
            : info.Capacity;
        var available = end - tlv.Offset;
        // Short TLV: two header bytes + terminator. Extended TLV: four + terminator.
        var maxMessage = available >= 260 ? available - 5 : Math.Clamp(available - 3, 0, 254);
        return new(info.Capacity, maxMessage, status);
    }

    private static void CheckWrite(NdefWriteInfo info, int length)
    {
        if (info.WriteError is { } error) throw new IOException(error);
        if (!info.CanWrite(length)) throw new IOException(Strings.TagFull);
    }

    private static void WriteTlv(INfcReader reader, CardInfo card, TlvInfo info, byte[] message)
    {
        var before = ReadBytes(reader, card, info, null, false);
        var tlv = NdefCodec.FindTlv(before.AsSpan(0, info.Capacity));
        CheckWrite(TlvWriteInfo(card, info, before, tlv), message.Length);
        var header = message.Length <= 254 ? new byte[] { 0x03, (byte)message.Length } : new byte[] { 0x03, 0xFF, (byte)(message.Length >> 8), (byte)message.Length };
        var replacement = header.Concat(message).Append((byte)0xFE).ToArray();
        var next = before.ToArray();
        replacement.CopyTo(next, tlv.Offset);
        // Publish the length last. Until then every completed block write leaves an empty NDEF.
        var flip = (tlv.Offset + 1) / info.UnitSize * info.UnitSize;
        var staged = next.ToArray();
        staged[tlv.Offset + 1] = 0;
        staged[tlv.Offset + 2] = 0xFE;
        var tag = before.ToArray();
        void Put(byte[] image, int i) {
            if (tag.AsSpan(i, info.UnitSize).SequenceEqual(image.AsSpan(i, info.UnitSize))) return;
            CardWorkflows.WriteChecked(reader, card, info.FirstAddress + i / info.UnitSize, image.AsSpan(i, info.UnitSize).ToArray(), info.UnitSize);
            image.AsSpan(i, info.UnitSize).CopyTo(tag.AsSpan(i));
        }
        Put(staged, flip);
        Put(staged, (tlv.Offset + 2) / info.UnitSize * info.UnitSize);
        for (var i = 0; i < next.Length; i += info.UnitSize)
            if (i != flip) Put(next, i);
        Put(next, flip);
    }

    private static int Type3Blocks(byte[] attr) => Math.Min((attr[3] << 8) | attr[4], 13);

    private static NdefWriteInfo Type3WriteInfo(byte[] attr) =>
        new(Type3Blocks(attr) * 16, Type3Blocks(attr) * 16, attr[10] == 0x01 ? NdefWriteStatus.Ready : NdefWriteStatus.ReadOnly);

    private static (byte[] Message, NdefWriteInfo Info) ReadType3(INfcReader reader, CardInfo card)
    {
        var attr = reader.ReadUnit(card, 0);
        ValidateType3(attr);
        var length = (attr[11] << 16) | (attr[12] << 8) | attr[13];
        var info = Type3WriteInfo(attr);
        if (length > info.Capacity) throw new IOException(Strings.FelicaNdefTooLong);
        var output = new byte[length];
        for (var i = 0; i < length; i += 16) {
            var block = reader.ReadUnit(card, 1 + i / 16);
            if (block.Length != 16) throw new IOException(Strings.BlockSizeChanged);
            block.AsSpan(0, Math.Min(16, length - i)).CopyTo(output.AsSpan(i));
        }
        return (output, info);
    }

    private static void ValidateType3(byte[] attr)
    {
        if (attr.Length != 16 || attr[0] != 0x10) throw new IOException(Strings.FelicaNotType3);
        var sum = attr.AsSpan(0, 14).ToArray().Sum(x => x);
        if (((attr[14] << 8) | attr[15]) != sum) throw new IOException(Strings.FelicaChecksum);
    }

    private static void WriteType3(INfcReader reader, CardInfo card, byte[] message)
    {
        var attr = reader.ReadUnit(card, 0);
        ValidateType3(attr);
        CheckWrite(Type3WriteInfo(attr), message.Length);
        var writing = attr.ToArray();
        writing[9] = 0x0F;
        writing[11] = writing[12] = writing[13] = 0;
        SetType3Checksum(writing);
        CardWorkflows.WriteChecked(reader, card, 0, writing, 16);
        for (var i = 0; i < message.Length; i += 16) {
            var block = reader.ReadUnit(card, 1 + i / 16);
            if (block.Length != 16) throw new IOException(Strings.BlockSizeChanged);
            message.AsSpan(i, Math.Min(16, message.Length - i)).CopyTo(block);
            CardWorkflows.WriteChecked(reader, card, 1 + i / 16, block, 16);
        }
        var done = attr.ToArray();
        done[9] = 0x00;
        done[11] = (byte)(message.Length >> 16);
        done[12] = (byte)(message.Length >> 8);
        done[13] = (byte)message.Length;
        SetType3Checksum(done);
        CardWorkflows.WriteChecked(reader, card, 0, done, 16);
    }

    private static void SetType3Checksum(byte[] attr)
    {
        var sum = attr.AsSpan(0, 14).ToArray().Sum(x => x);
        attr[14] = (byte)(sum >> 8); attr[15] = (byte)sum;
    }
}
