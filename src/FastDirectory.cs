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
    /// <para><paramref name="nameFilter"/> and <paramref name="filter"/> decide which entries are reported (see
    /// <see cref="EntryFilters"/>); both must accept. They run before the stat pass, so rejected entries cost no stat call.
    /// <paramref name="nameFilter"/> runs first and sees only the name, so an entry it rejects never has its type
    /// resolved either, which matters on filesystems that report DT_UNKNOWN.</para>
    /// <para>With <paramref name="symlinks"/> set to <see cref="SymlinkHandling.Follow"/> a symbolic link reports the type of its target, the way
    /// <see cref="Directory.GetDirectories(string)"/> classifies it: <see cref="EntryType.Directory"/> for a link to a
    /// directory, <see cref="EntryType.File"/> for a link to a file, <see cref="EntryType.Other"/> for a device, FIFO or
    /// socket. A link that cannot be resolved (dangling, loop, no permission) stays <see cref="EntryType.SymbolicLink"/>,
    /// which <see cref="EntryType.NotDirectory"/> includes. <see cref="StatFields.Attributes"/> then reports
    /// <see cref="FileAttributes.ReparsePoint"/> plus Directory / ReadOnly from the target, as .NET does; times and size
    /// stay the link's own. Only links pay: one extra statx each, and none when the name filter rejects them.
    /// Not applied by <see cref="WalkBatchBuffers"/>, which never follows links.</para>
    /// </summary>
    public static IEnumerable<DirectoryBatch> EnumerateBatchBuffers(
        string path, int batchSize, NativeBackend backend = NativeBackend.Auto,
        StatFields fields = StatFields.None, bool allowCachedAttributes = false, int statParallelism = 0,
        EntryFilter? filter = null, NameFilter? nameFilter = null,
        SymlinkHandling symlinks = SymlinkHandling.Report)
    {
        if (!Enum.IsDefined(symlinks)) throw new ArgumentOutOfRangeException(nameof(symlinks));
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(statParallelism);
        int par = statParallelism == 0 ? FastNativeOptions.StatParallelism : statParallelism;
        if (!OperatingSystem.IsWindows() && Unix.UnixDirectory.UsesGetdents(backend))
            return Unix.UnixDirectory.EnumerateGetdentsBatches(path, batchSize, fields, allowCachedAttributes, par, filter, nameFilter, symlinks);
        return EmulatedBatchBuffers(path, batchSize, backend, fields, par, filter, nameFilter, symlinks);
    }

    private static IEnumerable<DirectoryBatch> EmulatedBatchBuffers(
        string path, int batchSize, NativeBackend backend, StatFields fields, int parallelism, EntryFilter? filter, NameFilter? nameFilter,
        SymlinkHandling symlinks)
    {
        var batch = new DirectoryBatch(batchSize, fields) { DirectoryPath = path, Symlinks = symlinks };
        foreach (var e in Enumerate(path, backend))
        {
            var type = symlinks == SymlinkHandling.Follow && e.Type == EntryType.SymbolicLink ? ResolveLinkByPath(path, e.Name) : e.Type;
            if (!Passes(nameFilter, filter, e.Name, type)) continue;
            batch.Add(e.Name, type);
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

    private static bool Passes(NameFilter? nameFilter, EntryFilter? filter, string name, EntryType type)
    {
        if (nameFilter is null && filter is null) return true;
        Span<byte> utf8 = name.Length <= 256 ? stackalloc byte[768] : new byte[System.Text.Encoding.UTF8.GetMaxByteCount(name.Length)];
        int n = System.Text.Encoding.UTF8.GetBytes(name, utf8);
        return (nameFilter is null || nameFilter(utf8[..n])) && (filter is null || filter(utf8[..n], type));
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
                if (!Passes(o.NameFilter, o.EntryFilter, entry.Name, entry.Type)) continue;
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

    // Path-based link resolution for platforms and fallbacks without statx on the directory fd. File.Exists is true for a
    // dangling link, so the final target is resolved and checked itself. A FIFO or device counts as File here.
    internal static EntryType ResolveLinkByPath(string dir, string name)
    {
        var path = Path.Join(dir, name);
        if (Directory.Exists(path)) return EntryType.Directory;
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return (target?.Exists ?? File.Exists(path)) ? EntryType.File : EntryType.SymbolicLink;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)   // link loop, no permission
        {
            return EntryType.SymbolicLink;
        }
    }

    public static List<FileEntry> List(string path) => Enumerate(path).ToList();

    /// <summary>
    /// Full paths (<c>Path.Join(path, name)</c>) of the entries that <see cref="Directory.EnumerateFiles(string)"/>
    /// would return: everything except directories and symbolic links to directories. Dangling links, FIFOs,
    /// sockets and devices count as files. Only symbolic links (and entries of unknown type) cost an extra stat call.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string path) => Classified(path, directories: false);

    /// <summary>
    /// Full paths of the entries that <see cref="Directory.EnumerateDirectories(string)"/> would return:
    /// directories and symbolic links to directories.
    /// </summary>
    public static IEnumerable<string> EnumerateDirectories(string path) => Classified(path, directories: true);

    private static IEnumerable<string> Classified(string path, bool directories)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (OperatingSystem.IsWindows())
            return directories ? Directory.EnumerateDirectories(path) : Directory.EnumerateFiles(path);
        return ClassifiedUnix(path, directories);
    }

    private static IEnumerable<string> ClassifiedUnix(string path, bool directories)
    {
        foreach (var e in Enumerate(path))
            if (IsDirectoryLike(path, e) == directories)
                yield return Path.Join(path, e.Name);
    }

    // System.IO's rule on Unix: a directory, or anything whose target (following links) is one. Directory.Exists
    // follows links and returns false for dangling links and entries that vanished.
    internal static bool IsDirectoryLike(string dir, FileEntry e) => e.Type switch
    {
        EntryType.Directory => true,
        EntryType.File or EntryType.Other => false,
        _ => Directory.Exists(Path.Join(dir, e.Name)),
    };
}
