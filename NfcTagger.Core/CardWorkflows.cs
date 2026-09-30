using System.Text.Json;

namespace NfcTagger.Core;

public sealed record VerifiedWrite(int Address, string AfterHex);

public static class CardWorkflows
{
    public static VerifiedWrite WriteVerified(INfcReader reader, CardInfo card, int address, byte[] value, string? keyHex, bool keyB)
    {
        WriteGuard.Validate(card, address, value.Length);
        if (card.Family == CardFamily.Ntag && address >= NtagEnd(reader, card))
            throw new InvalidOperationException(Strings.NtagOutOfRange);
        // Only ISO15693 has a variable block size. NDEF already knows its size from the CC.
        var size = card.Family == CardFamily.Iso15693 ? reader.ReadUnit(card, address, keyHex, keyB).Length : value.Length;
        return new(address, Hex.Format(WriteChecked(reader, card, address, value, size, keyHex, keyB)));
    }

    internal static void CheckCard(INfcReader reader, CardInfo card)
    {
        var current = reader.Detect() ?? throw new IOException(Strings.NoCardDetected);
        if (!current.Uid.Equals(card.Uid, StringComparison.OrdinalIgnoreCase) || current.Family != card.Family)
            throw new IOException(Strings.CardChanged);
    }

    // Layout is already known; identity and read-back checks still run for each write.
    internal static byte[] WriteChecked(INfcReader reader, CardInfo card, int address, byte[] value, int unitSize,
        string? keyHex = null, bool keyB = false)
    {
        WriteGuard.Validate(card, address, value.Length);
        if (value.Length != unitSize) throw new IOException(Strings.BlockLengthMismatch);
        CheckCard(reader, card);
        reader.WriteUnit(card, address, value, keyHex, keyB);
        var after = reader.ReadUnit(card, address, keyHex, keyB);
        if (!after.SequenceEqual(value)) throw new IOException(Strings.VerifyFailed);
        return after;
    }

    public static CardDump Dump(INfcReader reader, CardInfo card, string? keyHex, bool keyB, CancellationToken cancellationToken = default,
        IProgress<(int Done, int Total)>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var end = card.Family switch {
            CardFamily.Ntag => NtagEnd(reader, card),
            CardFamily.MifareClassic => IsClassic4K(card) ? 256 : 64,
            CardFamily.FelicaLiteS => 14,
            CardFamily.Iso15693 => 256,
            _ => throw new NotSupportedException(Strings.DumpUnsupported)
        };
        var units = new List<MemoryUnit>();
        var errors = 0;
        var batchSize = BatchSize(reader, card);
        for (var address = 0; address < end; address += batchSize) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var unit in ReadBatch(reader, card, address, Math.Min(batchSize, end - address), keyHex, keyB)) {
                units.Add(unit);
                errors = unit.Error is null ? 0 : errors + 1;
            }
            progress?.Report((units.Count, end));
            // ISO15693 size is unknown; stop after repeated failures.
            if (card.Family == CardFamily.Iso15693 && errors >= 8 && address >= 16) break;
        }
        var size = units.LastOrDefault(x => x.Hex is not null)?.Hex?.Length / 2 ?? 0;
        return new(reader.Name, card, DateTimeOffset.Now, size, units);
    }

    public static List<MemoryUnit> ReadRange(INfcReader reader, CardInfo card, int address, int count, string? keyHex, bool keyB)
    {
        var units = new List<MemoryUnit>();
        var batchSize = BatchSize(reader, card);
        for (var next = address; next < address + count; next += batchSize)
            units.AddRange(ReadBatch(reader, card, next, Math.Min(batchSize, address + count - next), keyHex, keyB));
        return units;
    }

    // NTAG pages are read in bulk (ATNFC does up to 60 per command), other cards one unit at a time.
    private static int BatchSize(INfcReader reader, CardInfo card) => reader is AtnfcReader && card.Family == CardFamily.Ntag ? 16 : 1;

    // On failure, retries one unit at a time to report errors per address.
    private static List<MemoryUnit> ReadBatch(INfcReader reader, CardInfo card, int address, int count, string? keyHex, bool keyB)
    {
        if (count > 1 && reader is AtnfcReader atnfc) {
            // Use the bulk command directly so fallback reads each address only once.
            try { return atnfc.ReadPages(address, count).Select((data, i) => new MemoryUnit(address + i, Hex.Format(data), null)).ToList(); }
            catch (Exception e) when (IsReadFailure(e)) { }
        }
        var units = new List<MemoryUnit>();
        for (var i = address; i < address + count; i++) {
            try { units.Add(new(i, Hex.Format(reader.ReadUnit(card, i, keyHex, keyB)), null)); }
            catch (Exception e) when (IsReadFailure(e)) { units.Add(new(i, null, e.Message)); }
        }
        return units;
    }

    private static bool IsReadFailure(Exception e) => e is IOException or TimeoutException or NotSupportedException;

    private static int NtagEnd(INfcReader reader, CardInfo card)
    {
        var cc = reader.ReadUnit(card, 3);
        if (cc.Length < 4 || cc[0] != 0xE1 || cc[2] == 0) return 40; // unformatted Ultralight
        return NtagMemory.FirstPage + NtagMemory.Capacity(cc) / NtagMemory.PageSize;
    }

    private static bool IsClassic4K(CardInfo card) =>
        card.RawType.Contains("SAK 18", StringComparison.OrdinalIgnoreCase) ||
        card.Details.StartsWith("18", StringComparison.OrdinalIgnoreCase) ||
        card.RawType.Contains("A000000306030002", StringComparison.OrdinalIgnoreCase);

    public static string ToJson(CardDump dump) => JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true });
}
