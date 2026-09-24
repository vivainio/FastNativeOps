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
            return Portable.SystemIODirectory.Enumerate(path);
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
    /// <para>Pass <paramref name="fields"/> to also get size / mtime. On Linux this uses statx relative to the
    /// directory fd; with <paramref name="allowCachedAttributes"/> it may answer from the kernel's attribute cache
    /// (AT_STATX_DONT_SYNC), which avoids per-file server round trips on NFS at the price of possibly stale values.
    /// Elsewhere it is emulated with System.IO (slower).</para>
    /// <para><paramref name="statParallelism"/> sets how many stat calls run concurrently (0 = use
    /// <see cref="FastNativeOptions.StatParallelism"/>); it mainly helps on high-latency filesystems such as NFS.</para>
    /// </summary>
    public static IEnumerable<DirectoryBatch> EnumerateBatchBuffers(
        string path, int batchSize, NativeBackend backend = NativeBackend.Auto,
        StatFields fields = StatFields.None, bool allowCachedAttributes = false, int statParallelism = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(statParallelism);
        int par = statParallelism == 0 ? FastNativeOptions.StatParallelism : statParallelism;
        if (!OperatingSystem.IsWindows() && Unix.UnixDirectory.UsesGetdents(backend))
            return Unix.UnixDirectory.EnumerateGetdentsBatches(path, batchSize, fields, allowCachedAttributes, par);
        return EmulatedBatchBuffers(path, batchSize, backend, fields, par);
    }

    private static IEnumerable<DirectoryBatch> EmulatedBatchBuffers(
        string path, int batchSize, NativeBackend backend, StatFields fields, int parallelism)
    {
        var batch = new DirectoryBatch(batchSize, fields) { DirectoryPath = path };
        foreach (var e in Enumerate(path, backend))
        {
            batch.Add(e.Name, e.Type);
            if (batch.Count == batchSize)
            {
                if (fields != StatFields.None) StatEmulation.Fill(path, batch, batch.Count >= FastNativeOptions.StatParallelMinEntries ? parallelism : 1);
                yield return batch;
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            if (fields != StatFields.None) StatEmulation.Fill(path, batch, batch.Count >= FastNativeOptions.StatParallelMinEntries ? parallelism : 1);
            yield return batch;
        }
    }

    /// <summary>
    /// Recursively lists a directory tree, yielding one reused <see cref="DirectoryBatch"/> at a time. A batch never
    /// spans directories: see <see cref="DirectoryBatch.DirectoryPath"/> and <see cref="DirectoryBatch.Depth"/>.
    /// Symbolic links are reported but not followed. On Linux x64/arm64 the walk opens each subdirectory with
    /// openat relative to its parent's file descriptor (no path length limit, one path component resolved per open;
    /// needs about one file descriptor per level of depth); elsewhere it is emulated with path-based enumeration.
    /// Order is unspecified. Do not keep the batch past the next iteration.
    /// </summary>
    public static IEnumerable<DirectoryBatch> WalkBatchBuffers(string root, int batchSize, WalkOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        options ??= new WalkOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StatParallelism);
        int par = options.StatParallelism == 0 ? FastNativeOptions.StatParallelism : options.StatParallelism;
        if (!OperatingSystem.IsWindows() && Unix.UnixDirectory.UsesGetdents(options.Backend))
            return Unix.UnixDirectory.WalkGetdents(root, batchSize, options, par);
        return WalkEmulated(root, batchSize, options, par);
    }

    private static bool Passes(EntryFilter filter, string name, EntryType type)
    {
        Span<byte> utf8 = name.Length <= 256 ? stackalloc byte[768] : new byte[System.Text.Encoding.UTF8.GetMaxByteCount(name.Length)];
        int n = System.Text.Encoding.UTF8.GetBytes(name, utf8);
        return filter(utf8[..n], type);
    }

    private static IEnumerable<DirectoryBatch> WalkEmulated(string root, int batchSize, WalkOptions o, int par)
    {
        var batch = new DirectoryBatch(batchSize, o.Fields);
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (path, depth) = stack.Pop();
            batch.Clear();
            batch.DirectoryPath = path;
            batch.Depth = depth;
            var subdirs = new List<string>();
            using var it = Enumerate(path, o.Backend).GetEnumerator();
            while (true)
            {
                try
                {
                    if (!it.MoveNext()) break;
                }
                catch (Exception e) when (depth > 0 && o.IgnoreInaccessible && e is IOException or UnauthorizedAccessException)
                {
                    break;
                }
                var entry = it.Current;
                if (entry.Type == EntryType.Directory && depth < o.MaxDepth) subdirs.Add(entry.Name);   // descend regardless of the filter
                if (o.EntryFilter is not null && !Passes(o.EntryFilter, entry.Name, entry.Type)) continue;
                batch.Add(entry.Name, entry.Type);
                if (batch.Count == batchSize)
                {
                    if (o.Fields != StatFields.None) StatEmulation.Fill(path, batch, par);
                    yield return batch;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                if (o.Fields != StatFields.None) StatEmulation.Fill(path, batch, par);
                yield return batch;
                batch.Clear();
            }
            for (int i = subdirs.Count - 1; i >= 0; i--)
                if (o.ShouldDescend?.Invoke(subdirs[i], depth + 1) ?? true)
                    stack.Push((Path.Join(path, subdirs[i]), depth + 1));
        }
    }

    public static List<FileEntry> List(string path) => Enumerate(path).ToList();
}
