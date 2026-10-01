namespace NfcTagger.Core;

internal static class NtagMemory
{
    public const int FirstPage = 4;
    public const int PageSize = 4;

    public static int Capacity(byte[] cc)
    {
        if (cc.Length < 4 || cc[0] != 0xE1 || cc[2] == 0) throw new IOException(Strings.NtagNoCc);
        return Math.Min(cc[2] * 8, (256 - FirstPage) * PageSize);
    }
}
