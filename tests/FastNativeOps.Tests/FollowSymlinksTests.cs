namespace FastNativeOps.Tests;

/// <summary>SymlinkHandling.Follow: links report their target's type (as System.IO classifies them) and .NET's attributes.</summary>
public sealed class FollowSymlinksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fno-follow-" + Guid.NewGuid().ToString("N"));

    public FollowSymlinksTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "real.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "realdir"));
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(_dir, "link-to-file"), "real.txt");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "link-to-dir"), "realdir");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "chain"), "link-to-dir");
        File.CreateSymbolicLink(Path.Combine(_dir, "dangling"), "does-not-exist");
        File.CreateSymbolicLink(Path.Combine(_dir, "loop"), "loop");
        File.CreateSymbolicLink(Path.Combine(_dir, ".hidden-link-to-dir"), "realdir");
        File.CreateSymbolicLink(Path.Combine(_dir, "link-to-null"), "/dev/null");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static IEnumerable<NativeBackend> Backends() => DirectoryTests.Backends();

    private Dictionary<string, EntryType> Types(NativeBackend backend, bool follow, EntryFilter? filter = null)
    {
        var result = new Dictionary<string, EntryType>();
        foreach (var batch in FastDirectory.EnumerateBatchBuffers(_dir, 3, backend, filter: filter, symlinks: follow ? SymlinkHandling.Follow : SymlinkHandling.Report))
            for (int i = 0; i < batch.Count; i++)
                result[batch.GetName(i)] = batch.GetType(i);
        return result;
    }

    private static readonly EntryFilter Everything = (_, _) => true;

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Links_report_the_type_of_their_target(NativeBackend backend)
    {
        if (OperatingSystem.IsWindows()) return;
        // null filter: types stay pending until GetType. Everything filter: types are resolved while filling the batch.
        foreach (var filter in new EntryFilter?[] { null, Everything })
        {
            var t = Types(backend, follow: true, filter);
            await Assert.That(t["real.txt"]).IsEqualTo(EntryType.File);
            await Assert.That(t["realdir"]).IsEqualTo(EntryType.Directory);
            await Assert.That(t["link-to-file"]).IsEqualTo(EntryType.File);
            await Assert.That(t["link-to-dir"]).IsEqualTo(EntryType.Directory);
            await Assert.That(t["chain"]).IsEqualTo(EntryType.Directory);
            await Assert.That(t[".hidden-link-to-dir"]).IsEqualTo(EntryType.Directory);
            await Assert.That(t["dangling"]).IsEqualTo(EntryType.SymbolicLink);
            await Assert.That(t["loop"]).IsEqualTo(EntryType.SymbolicLink);
            if (backend != NativeBackend.Readdir || OperatingSystem.IsLinux())
                await Assert.That(new[] { EntryType.Other, EntryType.File }).Contains(t["link-to-null"]);   // File only on the path-based fallback
        }
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Without_Follow_links_stay_links(NativeBackend backend)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var filter in new EntryFilter?[] { null, Everything })
        {
            var t = Types(backend, follow: false, filter);
            foreach (var name in new[] { "link-to-file", "link-to-dir", "chain", "dangling", "loop", "link-to-null" })
                await Assert.That(t[name]).IsEqualTo(EntryType.SymbolicLink);
        }
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Directory_and_NotDirectory_filters_match_System_IO(NativeBackend backend)
    {
        if (OperatingSystem.IsWindows()) return;
        List<string> Names(EntryFilter f)
        {
            var names = new List<string>();
            foreach (var batch in FastDirectory.EnumerateBatchBuffers(_dir, 4, backend, filter: f, symlinks: SymlinkHandling.Follow))
                for (int i = 0; i < batch.Count; i++) names.Add(Path.Join(_dir, batch.GetName(i)));
            return names.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        List<string> Sorted(IEnumerable<string> p) => p.OrderBy(x => x, StringComparer.Ordinal).ToList();

        await Assert.That(Names(EntryFilters.OfType(EntryType.Directory))).IsEquivalentTo(Sorted(Directory.GetDirectories(_dir)), CollectionOrdering.Matching);
        await Assert.That(Names(EntryFilters.OfType(EntryType.NotDirectory))).IsEquivalentTo(Sorted(Directory.GetFiles(_dir)), CollectionOrdering.Matching);
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task NameFilter_runs_before_the_link_is_resolved(NativeBackend backend)
    {
        if (OperatingSystem.IsWindows()) return;
        var seen = new List<string>();
        EntryFilter spy = (name, type) => { seen.Add(System.Text.Encoding.UTF8.GetString(name)); return true; };
        foreach (var batch in FastDirectory.EnumerateBatchBuffers(_dir, 8, backend, filter: spy,
                     nameFilter: EntryFilters.Glob("real*"), symlinks: SymlinkHandling.Follow))
            for (int i = 0; i < batch.Count; i++)
                await Assert.That(batch.GetName(i)).StartsWith("real");
        foreach (var n in seen)
            await Assert.That(n).StartsWith("real");
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Attributes_match_FileSystemInfo_for_links(NativeBackend backend)
    {
        if (OperatingSystem.IsWindows()) return;
        var expected = new DirectoryInfo(_dir)
            .GetFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 })
            .ToDictionary(e => e.Name, e => e.Attributes);

        foreach (var filter in new EntryFilter?[] { null, Everything })
        {
            var got = new Dictionary<string, FileAttributes>();
            foreach (var batch in FastDirectory.EnumerateBatchBuffers(_dir, 3, backend, StatFields.Attributes,
                         filter: filter, symlinks: SymlinkHandling.Follow))
                for (int i = 0; i < batch.Count; i++) got[batch.GetName(i)] = batch.GetAttributes(i);

            await Assert.That(got.Keys.OrderBy(x => x)).IsEquivalentTo(expected.Keys.OrderBy(x => x), CollectionOrdering.Matching);
            foreach (var (name, attrs) in expected)
                await Assert.That(attrs == got[name]).IsTrue().Because($"{name}: .NET {attrs}, got {got[name]}");
        }
    }

    [Test]
    public async Task Without_Follow_attributes_stay_the_links_own()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var batch in FastDirectory.EnumerateBatchBuffers(_dir, 100, fields: StatFields.Attributes))
            for (int i = 0; i < batch.Count; i++)
                if (batch.GetName(i) == "link-to-dir")
                    await Assert.That(batch.GetAttributes(i)).IsEqualTo(FileAttributes.ReparsePoint);
    }

    [Test]
    public async Task Walk_does_not_follow_links()
    {
        if (OperatingSystem.IsWindows()) return;
        var types = new Dictionary<string, EntryType>();
        foreach (var batch in FastDirectory.WalkBatchBuffers(_dir, 100))
            for (int i = 0; i < batch.Count; i++)
                if (batch.Depth == 0) types[batch.GetName(i)] = batch.GetType(i);
        await Assert.That(types["link-to-dir"]).IsEqualTo(EntryType.SymbolicLink);
    }
}
