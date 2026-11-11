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

    [Test]
    public async Task Probe_with_fd_classifies_types_via_statx()
    {
        if (!OperatingSystem.IsLinux()) return;
        int fd = sys_open(_dir, 0);
        await Assert.That(fd >= 0).IsTrue();
        try
        {
            await Assert.That(UnixDirectory.Probe(fd, _dir, U("plain.txt"))).IsEqualTo(EntryType.File);
            await Assert.That(UnixDirectory.Probe(fd, _dir, U("é-ünïcode.txt"))).IsEqualTo(EntryType.File);
            await Assert.That(UnixDirectory.Probe(fd, _dir, U("sub"))).IsEqualTo(EntryType.Directory);
            await Assert.That(UnixDirectory.Probe(fd, _dir, U("link"))).IsEqualTo(EntryType.SymbolicLink);   // lstat semantics: the link itself
            await Assert.That(UnixDirectory.Probe(fd, _dir, U("missing"))).IsEqualTo(EntryType.Unknown);      // vanished since listing
        }
        finally { sys_close(fd); }
    }

    [Test]
    public async Task Probe_with_fd_agrees_with_path_based_probe()
    {
        if (!OperatingSystem.IsLinux()) return;
        int fd = sys_open(_dir, 0);
        try
        {
            foreach (var name in new[] { "plain.txt", "é-ünïcode.txt", "sub", "link" })
                await Assert.That(UnixDirectory.Probe(fd, _dir, U(name))).IsEqualTo(UnixDirectory.Probe(_dir, name));
        }
        finally { sys_close(fd); }
    }

    [Test]
    public async Task Probe_falls_back_to_the_path_when_there_is_no_fd()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.That(UnixDirectory.Probe(-1, _dir, U("sub"))).IsEqualTo(EntryType.Directory);
        await Assert.That(UnixDirectory.Probe(-1, _dir, U("plain.txt"))).IsEqualTo(EntryType.File);
    }

    [Test]
    public async Task Pending_batch_types_resolve_lazily_and_are_cached()
    {
        if (OperatingSystem.IsWindows()) return;
        int fd = OperatingSystem.IsLinux() ? sys_open(_dir, 0) : -1;
        try
        {
            var b = new DirectoryBatch(8, StatFields.None) { DirectoryPath = _dir, DirFd = fd };
            b.Add(U("plain.txt"), DirectoryBatch.PendingType);
            b.Add(U("sub"), DirectoryBatch.PendingType);
            b.Add(U("link"), EntryType.SymbolicLink);                  // already known: untouched
            await Assert.That(b.GetType(0)).IsEqualTo(EntryType.File);
            await Assert.That(b.GetType(1)).IsEqualTo(EntryType.Directory);
            await Assert.That(b.GetType(2)).IsEqualTo(EntryType.SymbolicLink);
            // cached: still correct after the fd is gone
            if (fd >= 0) { sys_close(fd); fd = -1; }
            await Assert.That(b.GetType(1)).IsEqualTo(EntryType.Directory);
        }
        finally { if (fd >= 0) sys_close(fd); }
    }

    [Test]
    public async Task Pending_batch_types_fall_back_when_the_fd_is_unavailable()
    {
        if (OperatingSystem.IsWindows()) return;
        var b = new DirectoryBatch(4, StatFields.None) { DirectoryPath = _dir, DirFd = -1 };
        b.Add(U("sub"), DirectoryBatch.PendingType);
        await Assert.That(b.GetType(0)).IsEqualTo(EntryType.Directory);
    }

    [Test]
    public async Task Name_only_batches_never_resolve_types_but_typed_access_still_works()
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
        await Assert.That(names.Count).IsEqualTo(4);
        await Assert.That(types["sub"]).IsEqualTo(EntryType.Directory);
        await Assert.That(types["plain.txt"]).IsEqualTo(EntryType.File);
        await Assert.That(types["link"]).IsEqualTo(EntryType.SymbolicLink);
    }

    // Synthetic getdents64 buffer: ino(8) off(8) reclen(2) type(1) name\0, every entry DT_UNKNOWN.
    private static byte[] Dirents(params string[] names)
    {
        var ms = new MemoryStream();
        foreach (var name in names)
        {
            var nm = U(name);
            int reclen = (19 + nm.Length + 1 + 7) & ~7;
            var rec = new byte[reclen];
            BitConverter.GetBytes((ushort)reclen).CopyTo(rec, 16);
            nm.CopyTo(rec, 19);
            ms.Write(rec);
        }
        return ms.ToArray();
    }

    private DirectoryBatch Fill(byte[] buf, NameFilter? nameFilter, EntryFilter? filter, Action<DirectoryBatch>? whileOpen = null)
    {
        int fd = sys_open(_dir, 0);
        var batch = new DirectoryBatch(16) { DirectoryPath = _dir, DirFd = fd };
        try
        {
            UnixDirectory.FillBatch(buf, 0, buf.Length, batch, _dir, fd, nameFilter, filter);
            whileOpen?.Invoke(batch);
        }
        finally { sys_close(fd); }
        return batch;
    }

    [Test]
    public async Task Name_filter_rejects_unknown_entries_without_resolving_their_type()
    {
        if (!OperatingSystem.IsLinux()) return;
        // "gone.log" does not exist: resolving its type would yield EntryType.Unknown, which the entry filter would then see.
        var sawGone = false;
        EntryType first = default;
        var batch = Fill(Dirents("plain.txt", "gone.log"), EntryFilters.Glob("*.txt"),
            (name, type) => { if (Encoding.UTF8.GetString(name) == "gone.log") sawGone = true; return type != EntryType.Directory; },
            b => first = b.GetType(0));
        await Assert.That(first).IsEqualTo(EntryType.File);
        await Assert.That(sawGone).IsFalse();
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(batch.GetName(0)).IsEqualTo("plain.txt");
    }

    [Test]
    public async Task Name_filter_alone_leaves_unknown_types_pending()
    {
        if (!OperatingSystem.IsLinux()) return;
        // With no entry filter nothing resolves the type eagerly; it is looked up when asked for.
        int count = 0;
        EntryType type = default;
        Fill(Dirents("plain.txt", "sub"), EntryFilters.Glob("*.txt"), null, batch =>
        {
            count = batch.Count;
            type = batch.GetType(0);
        });
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(type).IsEqualTo(EntryType.File);
    }
}
