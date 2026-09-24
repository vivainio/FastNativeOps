using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using FastNativeOps;

BenchmarkRunner.Run<ListBench>(args: args);

[MemoryDiagnoser]
public class ListBench
{
    private string _dir = "";

    // Override with FASTNATIVEOPS_BENCH_FILES=200000 (comma-separated for several sizes).
    [ParamsSource(nameof(FileCounts))] public int Files;

    public static IEnumerable<int> FileCounts =>
        (Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_FILES") ?? "50000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse);

    [GlobalSetup]
    public void Setup()
    {
        // FASTNATIVEOPS_BENCH_DIR: parent directory to create the test files in (default: system temp).
        var parent = Environment.GetEnvironmentVariable("FASTNATIVEOPS_BENCH_DIR") ?? Path.GetTempPath();
        _dir = Path.Combine(parent, "fastnativeops-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < Files; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"file-{i:D6}.txt"), []);
        for (int i = 0; i < 100; i++) Directory.CreateDirectory(Path.Combine(_dir, $"dir-{i:D3}"));
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [Benchmark(Baseline = true)]
    public int SystemIO_EnumerateEntries()
    {
        int n = 0;
        foreach (var _ in Directory.EnumerateFileSystemEntries(_dir)) n++;
        return n;
    }

    [Benchmark]
    public int SystemIO_FileSystemEnumerable_Names()
    {
        // The fastest built-in route: avoids full-path strings, materializes only what's asked for.
        int n = 0;
        var e = new System.IO.Enumeration.FileSystemEnumerable<int>(_dir, (ref System.IO.Enumeration.FileSystemEntry x) => x.FileName.Length)
        { ShouldIncludePredicate = null };
        foreach (var _ in e) n++;
        return n;
    }

    [Benchmark]
    public int Fast_Enumerate()
    {
        int n = 0;
        foreach (var _ in FastDirectory.Enumerate(_dir)) n++;
        return n;
    }

    [Benchmark]
    public int Fast_Enumerate_Readdir()
    {
        int n = 0;
        foreach (var _ in FastDirectory.Enumerate(_dir, NativeBackend.Readdir)) n++;
        return n;
    }

    [Benchmark]
    public int Fast_List() => FastDirectory.List(_dir).Count;

    [Benchmark]
    public int Fast_Batches_1000()
    {
        int n = 0;
        foreach (var b in FastDirectory.EnumerateBatches(_dir, 1000)) n += b.Length;
        return n;
    }

    [Benchmark]
    public int Fast_BatchBuffers_1000()
    {
        int n = 0;
        foreach (var b in FastDirectory.EnumerateBatchBuffers(_dir, 1000))
            for (int i = 0; i < b.Count; i++)
                n += b.GetNameUtf8(i).Length > 0 ? 1 : 0;   // touch the name without allocating
        return n;
    }

    // ---- listing + size/mtime for every entry ----

    [Benchmark]
    public long SystemIO_EnumerateFileSystemInfos_SizeMtime()
    {
        long n = 0;
        foreach (var fi in new DirectoryInfo(_dir).EnumerateFileSystemInfos())
            n += (fi is FileInfo f ? f.Length : 0) + fi.LastWriteTimeUtc.Ticks % 2;
        return n;
    }

    [Benchmark]
    public long Fast_BatchBuffers_1000_Stat() => StatSum(allowCached: false);

    [Benchmark]
    public long Fast_BatchBuffers_1000_StatCached() => StatSum(allowCached: true);

    private long StatSum(bool allowCached)
    {
        long n = 0;
        foreach (var b in FastDirectory.EnumerateBatchBuffers(
                     _dir, 1000, NativeBackend.Auto, StatFields.Size | StatFields.ModifiedTime, allowCached))
            for (int i = 0; i < b.Count; i++)
                n += b.GetSize(i) + b.GetModifiedTimeUtc(i).Ticks % 2;
        return n;
    }
}
