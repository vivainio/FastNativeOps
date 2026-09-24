using FastNativeOps;

namespace FastNativeOps.Tests;

public sealed class TempDirFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fno-tests-" + Guid.NewGuid().ToString("N"));
    public const int Files = 500;
    public static long MTime(int i) => 1_700_000_000L + i * 1000;

    public TempDirFixture()
    {
        Directory.CreateDirectory(Path);
        for (int i = 0; i < Files; i++)
        {
            var f = System.IO.Path.Combine(Path, $"file-{i:D4}-é.txt");   // non-ASCII name on purpose
            File.WriteAllBytes(f, new byte[i * 3]);
            File.SetLastWriteTimeUtc(f, DateTime.UnixEpoch.AddSeconds(MTime(i)));
        }
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "sub"));
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class DirectoryTests(TempDirFixture fx) : IClassFixture<TempDirFixture>
{
    private string[] Expected() =>
        Directory.GetFileSystemEntries(fx.Path).Select(System.IO.Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;

    public static IEnumerable<object[]> Backends()
    {
        yield return [NativeBackend.Auto];
        yield return [NativeBackend.Readdir];
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64)
            yield return [NativeBackend.Getdents64];
    }

    [Theory, MemberData(nameof(Backends))]
    public void Enumerate_MatchesSystemIO(NativeBackend backend)
    {
        var got = FastDirectory.Enumerate(fx.Path, backend).Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(Expected(), got);
    }

    [Fact]
    public void Enumerate_ReportsTypes()
    {
        var entries = FastDirectory.List(fx.Path);
        Assert.Equal(EntryType.Directory, entries.Single(e => e.Name == "sub").Type);
        Assert.All(entries.Where(e => e.Name != "sub"), e => Assert.Equal(EntryType.File, e.Type));
    }

    [Fact]
    public void Enumerate_MissingDirectory_Throws() =>
        Assert.Throws<IOException>(() => FastDirectory.Enumerate(System.IO.Path.Combine(fx.Path, "nope")).ToList());

    [Theory, MemberData(nameof(Backends))]
    public void BatchBuffers_MatchesSystemIO_ForVariousBatchSizes(NativeBackend backend)
    {
        foreach (int bs in new[] { 1, 7, 64, 100_000 })
        {
            var got = new List<string>();
            foreach (var b in FastDirectory.EnumerateBatchBuffers(fx.Path, bs, backend))
            {
                Assert.InRange(b.Count, 1, bs);
                for (int i = 0; i < b.Count; i++) got.Add(b.GetName(i));
            }
            Assert.Equal(Expected(), got.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void EnumerateBatches_MatchesAndRespectsSize()
    {
        var all = FastDirectory.EnumerateBatches(fx.Path, 50).ToList();
        Assert.All(all.SkipLast(1), b => Assert.Equal(50, b.Length));
        Assert.Equal(Expected(), all.SelectMany(b => b).Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static void AssertStat(string dir, int batchSize, int parallelism, bool cached, NativeBackend backend)
    {
        int seen = 0;
        var fields = StatFields.Size | StatFields.ModifiedTime;
        foreach (var b in FastDirectory.EnumerateBatchBuffers(dir, batchSize, backend, fields, cached, parallelism))
            for (int i = 0; i < b.Count; i++)
            {
                var name = b.GetName(i);
                if (b.GetType(i) != EntryType.File) continue;
                int n = int.Parse(name.AsSpan(5, 4));
                Assert.Equal(n * 3, b.GetSize(i));
                Assert.Equal(TempDirFixture.MTime(n), new DateTimeOffset(b.GetModifiedTimeUtc(i)).ToUnixTimeSeconds());
                seen++;
            }
        Assert.Equal(TempDirFixture.Files, seen);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Stat_SizeAndMtime_AreCorrect(NativeBackend backend)
    {
        foreach (int bs in new[] { 1, 7, 100, 1000 })
            foreach (int par in new[] { 1, 8 })
                foreach (bool cached in new[] { false, true })
                    AssertStat(fx.Path, bs, par, cached, backend);
    }

    [Fact]
    public void Stat_NotRequested_Throws()
    {
        var b = FastDirectory.EnumerateBatchBuffers(fx.Path, 10).First();
        Assert.Throws<InvalidOperationException>(() => b.GetSize(0));
        Assert.Throws<InvalidOperationException>(() => b.GetModifiedTimeUtc(0));
    }

    [Fact]
    public void Stat_ConcurrentEnumerations_WithPoolResizing_AreCorrect()
    {
        int original = FastNativeOptions.StatWorkerThreads, originalMin = FastNativeOptions.StatParallelMinEntries;
        FastNativeOptions.StatParallelMinEntries = 1;    // force the parallel path for every batch
        try
        {
            using var cts = new CancellationTokenSource();
            var resizer = Task.Run(() =>
            {
                int k = 0;
                while (!cts.IsCancellationRequested)
                {
                    FastNativeOptions.StatWorkerThreads = 2 + (k++ % 6);
                    Thread.Sleep(2);
                }
            });
            var workers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
            {
                for (int rep = 0; rep < 15; rep++)
                    AssertStat(fx.Path, 20 + w * 13, 1 + w, cached: rep % 2 == 0, NativeBackend.Auto);
            })).ToArray();

            Assert.True(Task.WaitAll(workers, TimeSpan.FromMinutes(2)), "concurrent stat enumerations hung");
            cts.Cancel();
            resizer.Wait();
        }
        finally
        {
            FastNativeOptions.StatWorkerThreads = original;
            FastNativeOptions.StatParallelMinEntries = originalMin;
        }
    }
}
