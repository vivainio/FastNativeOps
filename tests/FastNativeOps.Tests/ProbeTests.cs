using System.Runtime.InteropServices;
using System.Text;
using FastNativeOps.Unix;

namespace FastNativeOps.Tests;

/// <summary>DT_UNKNOWN resolution: statx relative to the directory fd, lazy resolution in batches, and the path-based fallback.</summary>
public sealed class ProbeTests : IDisposable
{
    [DllImport("libc", EntryPoint = "open")] private static extern int sys_open(string path, int flags);
    [DllImport("libc", EntryPoint = "close")] private static extern int sys_close(int fd);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fno-probe-" + Guid.NewGuid().ToString("N"));

    public ProbeTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "plain.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "é-ünïcode.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(Path.Combine(_dir, "link"), "plain.txt");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Probe_with_fd_classifies_types_via_statx()
    {
        if (!OperatingSystem.IsLinux()) return;
        int fd = sys_open(_dir, 0);
        Assert.True(fd >= 0);
        try
        {
            Assert.Equal(EntryType.File, UnixDirectory.Probe(fd, _dir, U("plain.txt")));
            Assert.Equal(EntryType.File, UnixDirectory.Probe(fd, _dir, U("é-ünïcode.txt")));
            Assert.Equal(EntryType.Directory, UnixDirectory.Probe(fd, _dir, U("sub")));
            Assert.Equal(EntryType.SymbolicLink, UnixDirectory.Probe(fd, _dir, U("link")));   // lstat semantics: the link itself
            Assert.Equal(EntryType.Unknown, UnixDirectory.Probe(fd, _dir, U("missing")));      // vanished since listing
        }
        finally { sys_close(fd); }
    }

    [Fact]
    public void Probe_with_fd_agrees_with_path_based_probe()
    {
        if (!OperatingSystem.IsLinux()) return;
        int fd = sys_open(_dir, 0);
        try
        {
            foreach (var name in new[] { "plain.txt", "é-ünïcode.txt", "sub", "link" })
                Assert.Equal(UnixDirectory.Probe(_dir, name), UnixDirectory.Probe(fd, _dir, U(name)));
        }
        finally { sys_close(fd); }
    }

    [Fact]
    public void Probe_falls_back_to_the_path_when_there_is_no_fd()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(EntryType.Directory, UnixDirectory.Probe(-1, _dir, U("sub")));
        Assert.Equal(EntryType.File, UnixDirectory.Probe(-1, _dir, U("plain.txt")));
    }

    [Fact]
    public void Pending_batch_types_resolve_lazily_and_are_cached()
    {
        if (OperatingSystem.IsWindows()) return;
        int fd = OperatingSystem.IsLinux() ? sys_open(_dir, 0) : -1;
        try
        {
            var b = new DirectoryBatch(8, StatFields.None) { DirectoryPath = _dir, DirFd = fd };
            b.Add(U("plain.txt"), DirectoryBatch.PendingType);
            b.Add(U("sub"), DirectoryBatch.PendingType);
            b.Add(U("link"), EntryType.SymbolicLink);                  // already known: untouched
            Assert.Equal(EntryType.File, b.GetType(0));
            Assert.Equal(EntryType.Directory, b.GetType(1));
            Assert.Equal(EntryType.SymbolicLink, b.GetType(2));
            // cached: still correct after the fd is gone
            if (fd >= 0) { sys_close(fd); fd = -1; }
            Assert.Equal(EntryType.Directory, b.GetType(1));
        }
        finally { if (fd >= 0) sys_close(fd); }
    }

    [Fact]
    public void Pending_batch_types_fall_back_when_the_fd_is_unavailable()
    {
        if (OperatingSystem.IsWindows()) return;
        var b = new DirectoryBatch(4, StatFields.None) { DirectoryPath = _dir, DirFd = -1 };
        b.Add(U("sub"), DirectoryBatch.PendingType);
        Assert.Equal(EntryType.Directory, b.GetType(0));
    }

    [Fact]
    public void Name_only_batches_never_resolve_types_but_typed_access_still_works()
    {
        if (OperatingSystem.IsWindows()) return;
        var names = new List<string>();
        var types = new Dictionary<string, EntryType>();
        foreach (var b in FastDirectory.EnumerateBatchBuffers(_dir, 100))
            for (int i = 0; i < b.Count; i++)
            {
                names.Add(b.GetName(i));
                types[b.GetName(i)] = b.GetType(i);
            }
        Assert.Equal(4, names.Count);
        Assert.Equal(EntryType.Directory, types["sub"]);
        Assert.Equal(EntryType.File, types["plain.txt"]);
        Assert.Equal(EntryType.SymbolicLink, types["link"]);
    }
}
