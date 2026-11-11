using FastNativeOps;

namespace FastNativeOps.Tests;

public sealed class MetadataDirFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fno-meta-" + Guid.NewGuid().ToString("N"));

    public MetadataDirFixture()
    {
        Directory.CreateDirectory(Path);
        foreach (var n in new[] { "plain", ".hidden", "ro", "none", "wo", "group", "later-chmod" })
            File.WriteAllText(P(n), n);
        Directory.CreateDirectory(P("dir"));
        Directory.CreateDirectory(P(".hiddendir"));
        Directory.CreateDirectory(P("rodir"));
        // In the future: with relatime an atime older than a day (or than mtime) is bumped by any reader of the file.
        File.SetLastAccessTimeUtc(P("plain"), AccessTime);
        File.SetLastWriteTimeUtc(P("later-chmod"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));   // mtime < ctime
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(P("ro"), UnixFileMode.UserRead | UnixFileMode.GroupRead);
            File.SetUnixFileMode(P("none"), UnixFileMode.None);
            File.SetUnixFileMode(P("wo"), UnixFileMode.UserWrite);
            File.SetUnixFileMode(P("group"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            File.SetUnixFileMode(P("rodir"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            System.Diagnostics.Process.Start("mkfifo", P("fifo")).WaitForExit();
            File.CreateSymbolicLink(P("link"), P("ro"));
            File.CreateSymbolicLink(P(".dangling"), P("nope"));
        }
        else File.SetAttributes(P("ro"), FileAttributes.ReadOnly);
    }

    public static readonly DateTime AccessTime = DateTime.UnixEpoch.AddSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);

    public string P(string name) => System.IO.Path.Join(Path, name);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(P("rodir"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else File.SetAttributes(P("ro"), FileAttributes.Normal);
        Directory.Delete(Path, recursive: true);
    }
}

[ClassDataSource<MetadataDirFixture>(Shared = SharedType.PerClass)]
public class MetadataTests(MetadataDirFixture fx)
{
    private const StatFields All = StatFields.Size | StatFields.ModifiedTime | StatFields.CreationTime
                                 | StatFields.LastAccessTime | StatFields.Attributes;

    private static readonly string[] Links = [".dangling", "link"];

    private Dictionary<string, (DateTime C, DateTime A, DateTime W, FileAttributes Attr)> Collect(
        NativeBackend backend, int parallelism, bool cached)
    {
        var d = new Dictionary<string, (DateTime, DateTime, DateTime, FileAttributes)>();
        foreach (var b in FastDirectory.EnumerateBatchBuffers(fx.Path, 3, backend, All, cached, parallelism))
            for (int i = 0; i < b.Count; i++)
                d[b.GetName(i)] = (b.GetCreationTimeUtc(i), b.GetLastAccessTimeUtc(i), b.GetModifiedTimeUtc(i), b.GetAttributes(i));
        return d;
    }

    // Same values as DirectoryInfo.GetFileSystemInfos, the call these fields are meant to replace.
    [Test, MethodDataSource(typeof(DirectoryTests), nameof(DirectoryTests.Backends))]
    public async Task MatchesFileSystemInfo(NativeBackend backend)
    {
        var expected = new DirectoryInfo(fx.Path).GetFileSystemInfos().ToDictionary(f => f.Name);
        foreach (int par in new[] { 1, 4 })
        {
            var got = Collect(backend, par, cached: false);
            await Assert.That(got.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(expected.Keys.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
            foreach (var (name, fi) in expected)
            {
                var g = got[name];
                await Assert.That(g.C).IsEqualTo(fi.CreationTimeUtc);
                await Assert.That(g.W).IsEqualTo(fi.LastWriteTimeUtc);
                if (!Links.Contains(name))   // symlinks are not followed: see Symlink_IsReparsePointOnly
                    await Assert.That(fi.Attributes == g.Attr).IsTrue().Because($"{name}: expected {fi.Attributes}, got {g.Attr}");
            }
        }
    }

    [Test, MethodDataSource(typeof(DirectoryTests), nameof(DirectoryTests.Backends))]
    public async Task KnownValues(NativeBackend backend)
    {
        var got = Collect(backend, 1, cached: true);
        await Assert.That(got["plain"].A).IsEqualTo(MetadataDirFixture.AccessTime);
        if (OperatingSystem.IsLinux())   // no birth time: older of ctime/mtime
            await Assert.That(got["later-chmod"].C).IsEqualTo(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        else                             // real birth time, as FileInfo reports it
            await Assert.That(got["later-chmod"].C).IsEqualTo(new FileInfo(fx.P("later-chmod")).CreationTimeUtc);
        if (OperatingSystem.IsWindows())   // real attributes (Archive etc.), exactly as System.IO reports them
        {
            foreach (var name in new[] { "plain", ".hidden", "dir", ".hiddendir", "ro" })
                await Assert.That(got[name].Attr).IsEqualTo(File.GetAttributes(fx.P(name)));
            return;
        }
        await Assert.That(got["plain"].Attr).IsEqualTo(FileAttributes.Normal);
        await Assert.That(got[".hidden"].Attr).IsEqualTo(FileAttributes.Hidden);
        await Assert.That(got["dir"].Attr).IsEqualTo(FileAttributes.Directory);
        await Assert.That(got[".hiddendir"].Attr).IsEqualTo(FileAttributes.Directory | FileAttributes.Hidden);
        await Assert.That(got["ro"].Attr).IsEqualTo(FileAttributes.ReadOnly);
        await Assert.That(got["rodir"].Attr).IsEqualTo(FileAttributes.Directory | FileAttributes.ReadOnly);
        await Assert.That(got["none"].Attr).IsEqualTo(FileAttributes.Normal);    // not readable: not "read-only"
        await Assert.That(got["wo"].Attr).IsEqualTo(FileAttributes.Normal);
        await Assert.That(got["group"].Attr).IsEqualTo(FileAttributes.Normal);   // owner can write
        await Assert.That(got["fifo"].Attr).IsEqualTo(FileAttributes.Normal);
    }

    [Test, MethodDataSource(typeof(DirectoryTests), nameof(DirectoryTests.Backends))]
    public async Task Symlink_IsReparsePointOnly_OnStatxPath(NativeBackend backend)
    {
        if (OperatingSystem.IsWindows()) return;
        var got = Collect(backend, 1, cached: false);
        if (FastNativeOps.Unix.UnixDirectory.UsesGetdents(backend))
        {
            await Assert.That(got["link"].Attr).IsEqualTo(FileAttributes.ReparsePoint);   // .NET also adds ReadOnly from the target
            await Assert.That(got[".dangling"].Attr).IsEqualTo(FileAttributes.ReparsePoint | FileAttributes.Hidden);
        }
        else   // emulated with System.IO: exactly what .NET reports
            await Assert.That(got["link"].Attr).IsEqualTo(new FileInfo(fx.P("link")).Attributes);
    }

    [Test]
    public async Task Vanished_IsSentinel()
    {
        var b = new DirectoryBatch(1, All);
        b.Add("does-not-exist", EntryType.File);
        StatEmulation.FillOne(fx.Path, b, 0);
        await Assert.That(b.GetAttributes(0)).IsEqualTo((FileAttributes)(-1));
        await Assert.That(b.GetCreationTimeUtc(0)).IsEqualTo(DateTime.MinValue);
        await Assert.That(b.GetLastAccessTimeUtc(0)).IsEqualTo(DateTime.MinValue);
    }

    [Test]
    public async Task NotRequested_Throws()
    {
        var b = FastDirectory.EnumerateBatchBuffers(fx.Path, 10, fields: StatFields.Size).First();
        await Assert.That(() => b.GetCreationTimeUtc(0)).Throws<InvalidOperationException>();
        await Assert.That(() => b.GetLastAccessTimeUtc(0)).Throws<InvalidOperationException>();
        await Assert.That(() => b.GetAttributes(0)).Throws<InvalidOperationException>();
    }
}
