using System.Text.Json;

namespace NfcTagger.Core;

public sealed record VerifiedWrite(int Address, string BeforeHex, string AfterHex);

public static class CardWorkflows
{
    public static VerifiedWrite WriteVerified(INfcReader reader, CardInfo card, int address, byte[] value, string? keyHex, bool keyB)
    {
        WriteGuard.Validate(card, address, value.Length);
        if (card.Family == CardFamily.Ntag && address >= NtagEnd(reader, card))
            throw new InvalidOperationException("NTAG 사용자 데이터 영역을 벗어났습니다.");
        var current = reader.Detect() ?? throw new IOException("카드가 감지되지 않았습니다.");
        if (!current.Uid.Equals(card.Uid, StringComparison.OrdinalIgnoreCase) || current.Family != card.Family)
            throw new IOException("카드가 바뀌었습니다. 쓰기를 취소했습니다.");
        var before = reader.ReadUnit(card, address, keyHex, keyB);
        if (before.Length != value.Length) throw new IOException("기존 블록 길이와 쓰기 데이터 길이가 다릅니다.");
        reader.WriteUnit(card, address, value, keyHex, keyB);
        var after = reader.ReadUnit(card, address, keyHex, keyB);
        if (!after.SequenceEqual(value)) throw new IOException("쓰기 후 재읽기 검증에 실패했습니다.");
        return new(address, Hex.Format(before), Hex.Format(after));
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
            _ => throw new NotSupportedException("이 카드의 메모리 덤프는 지원하지 않습니다.")
        };
        var units = new List<MemoryUnit>();
        var errors = 0;
        for (var address = 0; address < end; address += BatchSize(card)) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var unit in ReadBatch(reader, card, address, Math.Min(BatchSize(card), end - address), keyHex, keyB)) {
                units.Add(unit);
                errors = unit.Error is null ? 0 : errors + 1;
            }
            progress?.Report((units.Count, end));
            // ISO15693 size is unknown here: stop after a run of failures past the first blocks.
            if (card.Family == CardFamily.Iso15693 && errors >= 8 && address >= 16) break;
        }
        var size = units.LastOrDefault(x => x.Hex is not null)?.Hex?.Length / 2 ?? 0;
        return new(reader.Name, card, DateTimeOffset.Now, size, units);
    }

    public static List<MemoryUnit> ReadRange(INfcReader reader, CardInfo card, int address, int count, string? keyHex, bool keyB)
    {
        var units = new List<MemoryUnit>();
        for (var next = address; next < address + count; next += BatchSize(card))
            units.AddRange(ReadBatch(reader, card, next, Math.Min(BatchSize(card), address + count - next), keyHex, keyB));
        return units;
    }

    // NTAG pages are read in bulk (ATNFC does up to 60 per command); keyed, sized-by-trial and other cards stay unit by unit.
    private static int BatchSize(CardInfo card) => card.Family == CardFamily.Ntag ? 16 : 1;

    // A failed batch is retried unit by unit so each error stays on its own address.
    private static List<MemoryUnit> ReadBatch(INfcReader reader, CardInfo card, int address, int count, string? keyHex, bool keyB)
    {
        if (count > 1) {
            try { return reader.ReadUnits(card, address, count, keyHex, keyB).Select((data, i) => new MemoryUnit(address + i, Hex.Format(data), null)).ToList(); }
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
        if (cc.Length < 3 || cc[0] != 0xE1) return 40;
        return Math.Clamp(4 + cc[2] * 2, 4, 232);
    }

    private static bool IsClassic4K(CardInfo card) =>
        card.RawType.Contains("SAK 18", StringComparison.OrdinalIgnoreCase) ||
        card.Details.StartsWith("18", StringComparison.OrdinalIgnoreCase) ||
        card.RawType.Contains("A000000306030002", StringComparison.OrdinalIgnoreCase);

    public static string ToJson(CardDump dump) => JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true });
}
