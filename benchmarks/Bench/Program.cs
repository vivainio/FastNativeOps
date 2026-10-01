using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using FastNativeOps;

// Defaults so the published bundle runs with no arguments and needs no .NET SDK on the target machine:
//  - in-process toolchain (BenchmarkDotNet's default spawns `dotnet build`, which needs the SDK)
//  - all benchmarks, short job (pass --job default for the full, slower run)
var list = args.ToList();
// --dir <path>: where to create the test files (same as FASTNATIVEOPS_BENCH_DIR); consumed here, not passed on.
for (int i = 0; i < list.Count; i++)
{
    if (list[i] == "--dir" && i + 1 < list.Count)
    {
        Environment.SetEnvironmentVariable("FASTNATIVEOPS_BENCH_DIR", Path.GetFullPath(list[i + 1]));
        list.RemoveRange(i, 2);
        break;
    }
    if (list[i].StartsWith("--dir=", StringComparison.Ordinal))
    {
        Environment.SetEnvironmentVariable("FASTNATIVEOPS_BENCH_DIR", Path.GetFullPath(list[i][6..]));
        list.RemoveAt(i);
        break;
    }
}
// --existing <path>: benchmark an existing directory as-is (nothing is created or deleted). Same as FASTNATIVEOPS_BENCH_EXISTING.
for (int i = 0; i < list.Count; i++)
{
    if (list[i] == "--existing" && i + 1 < list.Count)
    {
        Environment.SetEnvironmentVariable("FASTNATIVEOPS_BENCH_EXISTING", Path.GetFullPath(list[i + 1]));
        list.RemoveRange(i, 2);
        break;
    }
}
// --threads 1,5: run every benchmark from N concurrent callers (same as FASTNATIVEOPS_BENCH_THREADS; default 1).
for (int i = 0; i < list.Count; i++)
{
    if (list[i] == "--threads" && i + 1 < list.Count)
    {
        Environment.SetEnvironmentVariable("FASTNATIVEOPS_BENCH_THREADS", list[i + 1]);
        list.RemoveRange(i, 2);
        break;
    }
}
// --cold: drop the kernel dentry/inode/page caches before every invocation (Linux, needs root), one invocation per iteration.
if (list.Remove("--cold"))
{
    Environment.SetEnvironmentVariable("FASTNATIVEOPS_BENCH_COLD", "1");
    list.AddRange(["--invocationCount", "1", "--unrollFactor", "1", "--iterationCount", "10", "--warmupCount", "1"]);
}
bool Has(params string[] names) => list.Any(a => names.Contains(a, StringComparer.OrdinalIgnoreCase));
if (!Has("--inprocess", "--toolchain", "--outofproc") && Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_OUTOFPROC") != "1")
    list.Add("--inProcess");
if (!Has("--filter", "-f", "--list", "--anyCategories", "--allCategories")) list.AddRange(["--filter", "*"]);
if (!Has("--job", "-j")) list.AddRange(["--job", "short"]);
BenchmarkSwitcher.FromTypes([typeof(ListBench)]).Run(list.ToArray());

[MemoryDiagnoser]
public class ListBench
{
    private string _dir = "";

    // Override with FASTNATIVEOPS_BENCH_FILES=200000 (comma-separated for several sizes).
    // With --existing the directory is used as-is and Files is reported as 0.
    [ParamsSource(nameof(FileCounts))] public int Files;

    // Concurrent callers per benchmark invocation (FASTNATIVEOPS_BENCH_THREADS / --threads, comma-separated).
    [ParamsSource(nameof(ThreadCounts))] public int Threads;

    static readonly string? Existing = Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_EXISTING");
    static readonly bool Cold = Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_COLD") == "1";

    public static IEnumerable<int> FileCounts =>
        Existing != null ? new[] { 0 } :
        (Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_FILES") ?? "50000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse);

    public static IEnumerable<int> ThreadCounts =>
        (Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_THREADS") ?? "1")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse);

    // Runs f from Threads concurrent callers (inline when 1) and returns the sum, so the work can't be elided.
    private long Run(Func<long> f)
    {
        if (Threads <= 1) return f();
        var tasks = new Task<long>[Threads];
        for (int t = 0; t < tasks.Length; t++) tasks[t] = Task.Run(f);
        Task.WaitAll(tasks);
        return tasks.Sum(t => t.Result);
    }

    [IterationSetup]
    public void DropCaches()
    {
        if (!Cold) return;
        File.WriteAllText("/proc/sys/vm/drop_caches", "3");   // needs root
    }

    [GlobalSetup]
    public void Setup()
    {
        // FASTNATIVEOPS_BENCH_DIR: parent directory to create the test files in (default: system temp).
        if (Existing != null)
        {
            _dir = Existing;
            if (!Directory.Exists(_dir)) throw new DirectoryNotFoundException(_dir);
            return;
        }
        var parent = Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_DIR") ?? Path.GetTempPath();
        _dir = Path.Combine(parent, "fastnativeops-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < Files; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"file-{i:D6}.txt"), []);
        for (int i = 0; i < 100; i++) Directory.CreateDirectory(Path.Combine(_dir, $"dir-{i:D3}"));
    }

    [GlobalCleanup]
    public void Cleanup() { if (Existing == null) Directory.Delete(_dir, recursive: true); }

    [Benchmark(Baseline = true)]
    public long SystemIO_EnumerateEntries() => Run(SystemIO_EnumerateEntries_Impl);

    private long SystemIO_EnumerateEntries_Impl()
    {
        int n = 0;
        foreach (var _ in Directory.EnumerateFileSystemEntries(_dir)) n++;
        return n;
    }

    [Benchmark]
    public long SystemIO_FileSystemEnumerable_Names() => Run(SystemIO_FileSystemEnumerable_Names_Impl);

    private long SystemIO_FileSystemEnumerable_Names_Impl()
    {
        // The fastest built-in route: avoids full-path strings, materializes only what's asked for.
        int n = 0;
        var e = new System.IO.Enumeration.FileSystemEnumerable<int>(_dir, (ref System.IO.Enumeration.FileSystemEntry x) => x.FileName.Length)
        { ShouldIncludePredicate = null };
        foreach (var _ in e) n++;
        return n;
    }

    [Benchmark]
    public long Fast_Enumerate() => Run(Fast_Enumerate_Impl);

    private long Fast_Enumerate_Impl()
    {
        int n = 0;
        foreach (var _ in FastDirectory.Enumerate(_dir)) n++;
        return n;
    }

    [Benchmark]
    public long Fast_Enumerate_Readdir() => Run(Fast_Enumerate_Readdir_Impl);

    private long Fast_Enumerate_Readdir_Impl()
    {
        int n = 0;
        foreach (var _ in FastDirectory.Enumerate(_dir, NativeBackend.Readdir)) n++;
        return n;
    }

    [Benchmark]
    public long Fast_List() => Run(Fast_List_Impl);

    private long Fast_List_Impl() => FastDirectory.List(_dir).Count;

    [Benchmark]
    public long Fast_Batches_1000() => Run(Fast_Batches_1000_Impl);

    private long Fast_Batches_1000_Impl()
    {
        int n = 0;
        foreach (var b in FastDirectory.EnumerateBatches(_dir, 1000)) n += b.Length;
        return n;
    }

    [Benchmark]
    public long Fast_BatchBuffers_1000() => Run(Fast_BatchBuffers_1000_Impl);

    private long Fast_BatchBuffers_1000_Impl()
    {
        int n = 0;
        foreach (var b in FastDirectory.EnumerateBatchBuffers(_dir, 1000))
            for (int i = 0; i < b.Count; i++)
                n += b.GetNameUtf8(i).Length > 0 ? 1 : 0;   // touch the name without allocating
        return n;
    }

    // ---- the System.IO file-listing APIs apps usually call (names only, files only, top-level) ----

    [Benchmark]
    public long SystemIO_Directory_GetFiles() => Run(() => Directory.GetFiles(_dir).Length);

    [Benchmark]
    public long SystemIO_Directory_EnumerateFiles() => Run(() =>
    {
        long n = 0;
        foreach (var _ in Directory.EnumerateFiles(_dir)) n++;
        return n;
    });

    [Benchmark]
    public long SystemIO_DirectoryInfo_EnumerateFiles_Names() => Run(() =>
    {
        long n = 0;
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles()) n += f.Name.Length > 0 ? 1 : 0;
        return n;
    });

    // ---- listing + size/mtime for every entry ----

    [Benchmark]
    public long SystemIO_EnumerateFileSystemInfos_SizeMtime() => Run(SystemIO_EnumerateFileSystemInfos_SizeMtime_Impl);

    private long SystemIO_EnumerateFileSystemInfos_SizeMtime_Impl()
    {
        long n = 0;
        foreach (var fi in new DirectoryInfo(_dir).EnumerateFileSystemInfos())
            n += (fi is FileInfo f ? f.Length : 0) + fi.LastWriteTimeUtc.Ticks % 2;
        return n;
    }

    [Benchmark]
    public long Fast_BatchBuffers_1000_Stat() => Run(Fast_BatchBuffers_1000_Stat_Impl);

    private long Fast_BatchBuffers_1000_Stat_Impl() => StatSum(false, 1);

    [Benchmark]
    public long Fast_BatchBuffers_1000_StatCached() => Run(Fast_BatchBuffers_1000_StatCached_Impl);

    private long Fast_BatchBuffers_1000_StatCached_Impl() => StatSum(true, 1);

    [Benchmark]
    public long Fast_BatchBuffers_1000_Stat_Par8() => Run(Fast_BatchBuffers_1000_Stat_Par8_Impl);

    private long Fast_BatchBuffers_1000_Stat_Par8_Impl() => StatSum(false, 8);

    [Benchmark]
    public long Fast_BatchBuffers_1000_Stat_Par32() => Run(Fast_BatchBuffers_1000_Stat_Par32_Impl);

    private long Fast_BatchBuffers_1000_Stat_Par32_Impl() => StatSum(false, 32);

    [Benchmark]
    public long Fast_BatchBuffers_1000_StatCached_Par8() => Run(Fast_BatchBuffers_1000_StatCached_Par8_Impl);

    private long Fast_BatchBuffers_1000_StatCached_Par8_Impl() => StatSum(true, 8);

    private long StatSum(bool allowCached, int parallelism)
    {
        long n = 0;
        foreach (var b in FastDirectory.EnumerateBatchBuffers(
                     _dir, 1000, NativeBackend.Auto, StatFields.Size | StatFields.ModifiedTime, allowCached, parallelism))
            for (int i = 0; i < b.Count; i++)
                n += b.GetSize(i) + b.GetModifiedTimeUtc(i).Ticks % 2;
        return n;
    }

    // ---- recursive walk with stat, with and without a name filter (filter runs before the stat pass) ----

    [Benchmark]
    public long Fast_Walk_Stat() => Run(Fast_Walk_Stat_Impl);

    private long Fast_Walk_Stat_Impl() => WalkSum(null);

    [Benchmark]
    public long Fast_Walk_Stat_Filtered_10Files() => Run(Fast_Walk_Stat_Filtered_10Files_Impl);

    private long Fast_Walk_Stat_Filtered_10Files_Impl() => WalkSum(EntryFilters.Glob("file-00000?.txt"));

    [Benchmark]
    public long SystemIO_Recursive_FileInfo_Filtered_10Files() => Run(SystemIO_Recursive_FileInfo_Filtered_10Files_Impl);

    private long SystemIO_Recursive_FileInfo_Filtered_10Files_Impl()
    {
        long n = 0;
        foreach (var fi in new DirectoryInfo(_dir).EnumerateFileSystemInfos("file-00000?.txt", SearchOption.AllDirectories))
            n += (fi is FileInfo f ? f.Length : 0) + fi.LastWriteTimeUtc.Ticks % 2;
        return n;
    }

    private long WalkSum(EntryFilter? filter)
    {
        long n = 0;
        var o = new WalkOptions { Fields = StatFields.Size | StatFields.ModifiedTime, EntryFilter = filter };
        foreach (var b in FastDirectory.WalkBatchBuffers(_dir, 1000, o))
            for (int i = 0; i < b.Count; i++)
                n += b.GetSize(i) + b.GetModifiedTimeUtc(i).Ticks % 2;
        return n;
    }
}
