namespace FastNativeOps;

public static class FastDirectory
{
    /// <summary>Lists entries of a directory (excluding "." and ".."), using native APIs.</summary>
    public static IEnumerable<FileEntry> Enumerate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return OperatingSystem.IsWindows()
            ? Windows.WinDirectory.Enumerate(path)
            : Unix.UnixDirectory.Enumerate(path);
    }

    /// <summary>
    /// Lists a directory in batches of up to <paramref name="batchSize"/> entries. The directory handle stays
    /// open between batches, so dispose the enumerator (or finish the loop) promptly. Each batch is a fresh array.
    /// </summary>
    public static IEnumerable<FileEntry[]> EnumerateBatches(string path, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return Batches(path, batchSize);
    }

    private static IEnumerable<FileEntry[]> Batches(string path, int batchSize)
    {
        var buf = new FileEntry[batchSize];
        int n = 0;
        foreach (var e in Enumerate(path))
        {
            buf[n++] = e;
            if (n == batchSize)
            {
                yield return buf;
                buf = new FileEntry[batchSize];
                n = 0;
            }
        }
        if (n > 0) yield return buf[..n];
    }

    public static List<FileEntry> List(string path) => Enumerate(path).ToList();
}
