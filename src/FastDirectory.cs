namespace FastNativeOps;

public static class FastDirectory
{
    /// <summary>Lists entries of a directory (excluding "." and ".."), using native APIs.</summary>
    public static IEnumerable<FileEntry> Enumerate(string path) => Enumerate(path, NativeBackend.Auto);

    public static IEnumerable<FileEntry> Enumerate(string path, NativeBackend backend)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (OperatingSystem.IsWindows())
        {
            if (backend != NativeBackend.Auto)
                throw new PlatformNotSupportedException($"Backend {backend} is not available on Windows.");
            return Windows.WinDirectory.Enumerate(path);
        }
        return Unix.UnixDirectory.Enumerate(path, backend);
    }

    /// <summary>
    /// Lists a directory in batches of up to <paramref name="batchSize"/> entries. The directory handle stays
    /// open between batches, so dispose the enumerator (or finish the loop) promptly. Each batch is a fresh array.
    /// </summary>
    public static IEnumerable<FileEntry[]> EnumerateBatches(string path, int batchSize, NativeBackend backend = NativeBackend.Auto)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return Batches(path, batchSize, backend);
    }

    private static IEnumerable<FileEntry[]> Batches(string path, int batchSize, NativeBackend backend)
    {
        var buf = new FileEntry[batchSize];
        int n = 0;
        foreach (var e in Enumerate(path, backend))
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

    /// <summary>
    /// Like <see cref="EnumerateBatches"/> but yields one reused <see cref="DirectoryBatch"/> holding UTF-8 names,
    /// avoiding per-entry string and per-batch array allocations. Fastest on Linux x64/arm64 (getdents64);
    /// other platforms emulate it by copying names into the same buffer. Do not keep the batch past the next iteration.
    /// </summary>
    public static IEnumerable<DirectoryBatch> EnumerateBatchBuffers(
        string path, int batchSize, NativeBackend backend = NativeBackend.Auto)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        if (!OperatingSystem.IsWindows() && Unix.UnixDirectory.UsesGetdents(backend))
            return Unix.UnixDirectory.EnumerateGetdentsBatches(path, batchSize);
        return EmulatedBatchBuffers(path, batchSize, backend);
    }

    private static IEnumerable<DirectoryBatch> EmulatedBatchBuffers(string path, int batchSize, NativeBackend backend)
    {
        var batch = new DirectoryBatch(batchSize);
        foreach (var e in Enumerate(path, backend))
        {
            batch.Add(e.Name, e.Type);
            if (batch.Count == batchSize)
            {
                yield return batch;
                batch.Clear();
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    public static List<FileEntry> List(string path) => Enumerate(path).ToList();
}
