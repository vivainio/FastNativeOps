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

public class MetadataTests(MetadataDirFixture fx) : IClassFixture<MetadataDirFixture>
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
    [Theory, MemberData(nameof(DirectoryTests.Backends), MemberType = typeof(DirectoryTests))]
    public void MatchesFileSystemInfo(NativeBackend backend)
    {
        var expected = new DirectoryInfo(fx.Path).GetFileSystemInfos().ToDictionary(f => f.Name);
        foreach (int par in new[] { 1, 4 })
        {
            var got = Collect(backend, par, cached: false);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), got.Keys.Order(StringComparer.Ordinal));
            foreach (var (name, fi) in expected)
            {
                var g = got[name];
                Assert.Equal(fi.CreationTimeUtc, g.C);
                Assert.Equal(fi.LastWriteTimeUtc, g.W);
                if (!Links.Contains(name))   // symlinks are not followed: see Symlink_IsReparsePointOnly
                    Assert.True(fi.Attributes == g.Attr, $"{name}: expected {fi.Attributes}, got {g.Attr}");
            }
        }
    }

    [Theory, MemberData(nameof(DirectoryTests.Backends), MemberType = typeof(DirectoryTests))]
    public void KnownValues(NativeBackend backend)
    {
        var got = Collect(backend, 1, cached: true);
        Assert.Equal(MetadataDirFixture.AccessTime, got["plain"].A);
        Assert.Equal(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), got["later-chmod"].C);   // older of ctime/mtime
        Assert.Equal(FileAttributes.Normal, got["plain"].Attr);
        Assert.Equal(FileAttributes.Hidden, got[".hidden"].Attr);
        Assert.Equal(FileAttributes.Directory, got["dir"].Attr);
        Assert.Equal(FileAttributes.Directory | FileAttributes.Hidden, got[".hiddendir"].Attr);
        Assert.Equal(FileAttributes.ReadOnly, got["ro"].Attr);
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(FileAttributes.Directory | FileAttributes.ReadOnly, got["rodir"].Attr);
        Assert.Equal(FileAttributes.Normal, got["none"].Attr);    // not readable: not "read-only"
        Assert.Equal(FileAttributes.Normal, got["wo"].Attr);
        Assert.Equal(FileAttributes.Normal, got["group"].Attr);   // owner can write
        Assert.Equal(FileAttributes.Normal, got["fifo"].Attr);
    }

    [Theory, MemberData(nameof(DirectoryTests.Backends), MemberType = typeof(DirectoryTests))]
    public void Symlink_IsReparsePointOnly_OnStatxPath(NativeBackend backend)
    {
        if (OperatingSystem.IsWindows()) return;
        var got = Collect(backend, 1, cached: false);
        if (FastNativeOps.Unix.UnixDirectory.UsesGetdents(backend))
        {
            Assert.Equal(FileAttributes.ReparsePoint, got["link"].Attr);   // .NET also adds ReadOnly from the target
            Assert.Equal(FileAttributes.ReparsePoint | FileAttributes.Hidden, got[".dangling"].Attr);
        }
        else   // emulated with System.IO: exactly what .NET reports
            Assert.Equal(new FileInfo(fx.P("link")).Attributes, got["link"].Attr);
    }

    [Fact]
    public void Vanished_IsSentinel()
    {
        var b = new DirectoryBatch(1, All);
        b.Add("does-not-exist", EntryType.File);
        StatEmulation.FillOne(fx.Path, b, 0);
        Assert.Equal((FileAttributes)(-1), b.GetAttributes(0));
        Assert.Equal(DateTime.MinValue, b.GetCreationTimeUtc(0));
        Assert.Equal(DateTime.MinValue, b.GetLastAccessTimeUtc(0));
    }

    [Fact]
    public void NotRequested_Throws()
    {
        var b = FastDirectory.EnumerateBatchBuffers(fx.Path, 10, fields: StatFields.Size).First();
        Assert.Throws<InvalidOperationException>(() => b.GetCreationTimeUtc(0));
        Assert.Throws<InvalidOperationException>(() => b.GetLastAccessTimeUtc(0));
        Assert.Throws<InvalidOperationException>(() => b.GetAttributes(0));
    }
}
