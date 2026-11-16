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

[ClassDataSource<TempDirFixture>(Shared = SharedType.PerClass)]
public class DirectoryTests(TempDirFixture fx)
{
    private static Exception? Capture(Action a)
    {
        try { a(); return null; } catch (Exception e) { return e; }
    }

    private string[] Expected() =>
        Directory.GetFileSystemEntries(fx.Path).Select(System.IO.Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;

    public static IEnumerable<NativeBackend> Backends()
    {
        yield return NativeBackend.Auto;
        if (!OperatingSystem.IsWindows()) yield return NativeBackend.Readdir;
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64)
            yield return NativeBackend.Getdents64;
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Enumerate_MatchesSystemIO(NativeBackend backend)
    {
        var got = FastDirectory.Enumerate(fx.Path, backend).Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        await Assert.That(got).IsEquivalentTo(Expected(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Enumerate_ReportsTypes()
    {
        var entries = FastDirectory.List(fx.Path);
        await Assert.That(entries.Single(e => e.Name == "sub").Type).IsEqualTo(EntryType.Directory);
        foreach (var e in entries.Where(e => e.Name != "sub"))
            await Assert.That(e.Type).IsEqualTo(EntryType.File);
    }

    [Test]
    public async Task Enumerate_MissingDirectory_Throws() =>
        await Assert.That(() => FastDirectory.Enumerate(System.IO.Path.Combine(fx.Path, "nope")).ToList()).Throws<DirectoryNotFoundException>();

    [Test, MethodDataSource(nameof(Backends))]
    public async Task OpenFailures_ThrowSameExceptionTypeAsSystemIO(NativeBackend backend)
    {
        var file = System.IO.Path.Combine(fx.Path, "file-0001-é.txt");
        foreach (var bad in new[] { System.IO.Path.Combine(fx.Path, "nope"), System.IO.Path.Combine(file, "x"), file })
        {
            var expected = Capture(() => Directory.GetFileSystemEntries(bad))!.GetType();
            await Assert.That(Capture(() => FastDirectory.Enumerate(bad, backend).ToList())?.GetType()).IsEqualTo(expected);
            await Assert.That(Capture(() => FastDirectory.EnumerateBatchBuffers(bad, 10, backend, StatFields.Size).ToList())?.GetType()).IsEqualTo(expected);
            await Assert.That(Capture(() => FastDirectory.WalkBatchBuffers(bad, 10, new WalkOptions { Backend = backend }).ToList())?.GetType()).IsEqualTo(expected);
        }
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task BatchBuffers_MatchesSystemIO_ForVariousBatchSizes(NativeBackend backend)
    {
        foreach (int bs in new[] { 1, 7, 64, 100_000 })
        {
            var got = new List<string>();
            foreach (var b in FastDirectory.EnumerateBatchBuffers(fx.Path, bs, backend))
            {
                await Assert.That(b.Count).IsBetween(1, bs);
                for (int i = 0; i < b.Count; i++) got.Add(b.GetName(i));
            }
            await Assert.That(got.OrderBy(x => x, StringComparer.Ordinal).ToArray()).IsEquivalentTo(Expected(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task EnumerateBatches_MatchesAndRespectsSize()
    {
        var all = FastDirectory.EnumerateBatches(fx.Path, 50).ToList();
        foreach (var b in all.SkipLast(1))
            await Assert.That(b.Length).IsEqualTo(50);
        await Assert.That(all.SelectMany(b => b).Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray()).IsEquivalentTo(Expected(), CollectionOrdering.Matching);
    }

    private static async Task AssertStat(string dir, int batchSize, int parallelism, bool cached, NativeBackend backend)
    {
        int seen = 0;
        var fields = StatFields.Size | StatFields.ModifiedTime;
        foreach (var b in FastDirectory.EnumerateBatchBuffers(dir, batchSize, backend, fields, cached, parallelism))
            for (int i = 0; i < b.Count; i++)
            {
                var name = b.GetName(i);
                if (b.GetType(i) != EntryType.File) continue;
                int n = int.Parse(name.AsSpan(5, 4));
                await Assert.That(b.GetSize(i)).IsEqualTo(n * 3);
                await Assert.That(new DateTimeOffset(b.GetModifiedTimeUtc(i)).ToUnixTimeSeconds()).IsEqualTo(TempDirFixture.MTime(n));
                seen++;
            }
        await Assert.That(seen).IsEqualTo(TempDirFixture.Files);
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Stat_SizeAndMtime_AreCorrect(NativeBackend backend)
    {
        foreach (int bs in new[] { 1, 7, 100, 1000 })
            foreach (int par in new[] { 1, 8 })
                foreach (bool cached in new[] { false, true })
                    await AssertStat(fx.Path, bs, par, cached, backend);
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task BatchBuffers_Filter_ReportsOnlyMatches_WithCorrectStat(NativeBackend backend)
    {
        var expected = Expected().Where(n => n.StartsWith("file-00")).ToArray();   // file-0000 .. file-0099
        await Assert.That(expected.Length).IsEqualTo(100);
        foreach (int bs in new[] { 1, 7, 64, 100_000 })
        {
            var got = new List<string>();
            foreach (var b in FastDirectory.EnumerateBatchBuffers(fx.Path, bs, backend, StatFields.Size, nameFilter: EntryFilters.Glob("file-00*")))
            {
                await Assert.That(b.Count).IsBetween(1, bs);
                for (int i = 0; i < b.Count; i++)
                {
                    var name = b.GetName(i);
                    await Assert.That(b.GetSize(i)).IsEqualTo(int.Parse(name.AsSpan(5, 4)) * 3);   // stat stays aligned with the filtered entries
                    got.Add(name);
                }
            }
            await Assert.That(got.OrderBy(x => x, StringComparer.Ordinal).ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task BatchBuffers_Filter_ByType_And_RejectAll(NativeBackend backend)
    {
        var dirs = FastDirectory.EnumerateBatchBuffers(fx.Path, 10, backend, filter: EntryFilters.OfType(EntryType.Directory))
            .SelectMany(b => Enumerable.Range(0, b.Count).Select(b.GetName)).ToList();
        await Assert.That(dirs).IsEquivalentTo(["sub"], CollectionOrdering.Matching);
        await Assert.That(FastDirectory.EnumerateBatchBuffers(fx.Path, 10, backend, StatFields.Size, filter: (_, _) => false)).IsEmpty();
    }

    [Test]
    public async Task Stat_NotRequested_Throws()
    {
        var b = FastDirectory.EnumerateBatchBuffers(fx.Path, 10).First();
        await Assert.That(() => b.GetSize(0)).Throws<InvalidOperationException>();
        await Assert.That(() => b.GetModifiedTimeUtc(0)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Stat_ConcurrentEnumerations_WithPoolResizing_AreCorrect()
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
            var workers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
            {
                for (int rep = 0; rep < 15; rep++)
                    await AssertStat(fx.Path, 20 + w * 13, 1 + w, cached: rep % 2 == 0, NativeBackend.Auto);
            })).ToArray();

            await Assert.That(Task.WaitAll(workers, TimeSpan.FromMinutes(2))).IsTrue().Because("concurrent stat enumerations hung");
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
