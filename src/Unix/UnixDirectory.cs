using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FastNativeOps.Unix;

internal static unsafe partial class UnixDirectory
{
    [LibraryImport(Lib, EntryPoint = "opendir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint opendir(string path);

    [LibraryImport(Lib, EntryPoint = "readdir", SetLastError = true)]
    private static partial nint readdir(nint dir);

    [LibraryImport(Lib, EntryPoint = "closedir")]
    private static partial int closedir(nint dir);

    [LibraryImport(Lib, EntryPoint = "dirfd")]
    private static partial int dirfd(nint dir);

    // syscall() is variadic in C, but on Linux x64/arm64 the calling convention matches a fixed signature.
    [LibraryImport(Lib, EntryPoint = "syscall", SetLastError = true)]
    private static partial nint syscall(nint number, int fd, byte* buf, nint count);

    // getdents64 syscall numbers (asm-generic table on arm64).
    private static readonly int SysGetdents64 =
        !OperatingSystem.IsLinux() ? 0 :
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 217,
            Architecture.Arm64 => 61,
            _ => 0,
        };

    [LibraryImport(Lib, EntryPoint = "syscall", SetLastError = true)]
    private static partial nint syscallStatx(nint number, int dirfd, byte* path, int flags, uint mask, byte* buf);

    private static readonly int SysStatx =
        !OperatingSystem.IsLinux() ? 0 :
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 332,
            Architecture.Arm64 => 291,
            _ => 0,
        };

    private static volatile bool s_statxUnavailable;

    private const int EINTR = 4;

    private const string Lib = "FastNativeOpsLibc";

    // Resolve libc across distros: glibc (libc.so.6), musl/Alpine (libc.musl-*.so.1 / libc.so),
    // Android (libc.so), macOS/BSD (libSystem / libc).
    static UnixDirectory()
    {
        NativeLibrary.SetDllImportResolver(typeof(UnixDirectory).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return 0;
            string[] candidates = OperatingSystem.IsMacOS()
                ? ["/usr/lib/libSystem.B.dylib", "libSystem.dylib"]
                : ["libc.so.6", "libc.so", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1",
                   "libc.musl-armhf.so.1", "libc"];
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, out var h)) return h;
            return 0;
        });
    }

    // struct dirent layouts as returned by readdir():
    //  Linux glibc/musl (64-bit only): ino(8) off(8) reclen(2) type(1) name@19
    //  macOS 64-bit inode:       ino(8) seekoff(8) reclen(2) namlen(2) type(1) name@21
    private static readonly int TypeOffset =
        OperatingSystem.IsMacOS() ? 20 : 18;
    private static readonly int NameOffset =
        OperatingSystem.IsMacOS() ? 21 : 19;

    private const byte DT_DIR = 4, DT_REG = 8, DT_LNK = 10;

    // DT_UNKNOWN happens on some filesystems (older XFS, some network/FUSE mounts): ask the OS.
    private static EntryType Probe(string dir, string name)
    {
        try
        {
            var a = File.GetAttributes(Path.Join(dir, name));
            return (a & FileAttributes.ReparsePoint) != 0 ? EntryType.SymbolicLink
                 : (a & FileAttributes.Directory) != 0 ? EntryType.Directory
                 : EntryType.File;
        }
        catch { return EntryType.Unknown; }
    }

    private static (string Name, byte DType) ReadDirent(nint ent)
    {
        byte* p = (byte*)ent;
        return (Marshal.PtrToStringUTF8((nint)(p + NameOffset))!, p[TypeOffset]);
    }

    private static int GetDents(int fd, byte[] buf)
    {
        fixed (byte* p = buf)
            return (int)syscall(SysGetdents64, fd, p, buf.Length);
    }

    // linux_dirent64: ino(8) off(8) reclen(2) type(1) name@19 (NUL-terminated). Same on glibc and musl.
    private static IEnumerable<FileEntry> EnumerateGetdents(string path)
    {
        nint dir = opendir(path);
        if (dir == 0)
            throw new IOException($"Cannot open '{path}' (errno {Marshal.GetLastPInvokeError()})");
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int fd = dirfd(dir);
            while (true)
            {
                int n = GetDents(fd, buf);
                if (n < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR) continue;
                    throw new IOException($"Cannot read '{path}' (errno {errno})");
                }
                if (n == 0) yield break;

                for (int pos = 0; pos < n;)
                {
                    var rec = buf.AsSpan(pos);
                    int reclen = BitConverter.ToUInt16(rec[16..18]);
                    byte dtype = rec[18];
                    var nameSpan = rec[19..reclen];
                    nameSpan = nameSpan[..nameSpan.IndexOf((byte)0)];
                    pos += reclen;

                    if (nameSpan is [(byte)'.'] or [(byte)'.', (byte)'.']) continue;
                    var name = System.Text.Encoding.UTF8.GetString(nameSpan);
                    var type = dtype switch
                    {
                        DT_REG => EntryType.File,
                        DT_DIR => EntryType.Directory,
                        DT_LNK => EntryType.SymbolicLink,
                        0 => Probe(path, name),
                        _ => EntryType.Other,
                    };
                    yield return new FileEntry(name, type);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            closedir(dir);
        }
    }

    internal static bool UsesGetdents(NativeBackend backend) => backend switch
    {
        NativeBackend.Readdir => false,
        NativeBackend.Getdents64 when SysGetdents64 == 0
            => throw new PlatformNotSupportedException("getdents64 is only supported on Linux x64/arm64."),
        NativeBackend.Getdents64 => true,
        _ => SysGetdents64 != 0,
    };

    // Parses records from pos until the batch is full or the buffer is consumed; returns the new pos.
    private static int FillBatch(byte[] buf, int pos, int n, DirectoryBatch batch, string path)
    {
        while (pos < n && batch.Count < batch.Capacity)
        {
            var rec = buf.AsSpan(pos);
            int reclen = BitConverter.ToUInt16(rec[16..18]);
            byte dtype = rec[18];
            var name = rec[19..reclen];
            name = name[..name.IndexOf((byte)0)];
            pos += reclen;

            if (name is [(byte)'.'] or [(byte)'.', (byte)'.']) continue;
            var type = dtype switch
            {
                DT_REG => EntryType.File,
                DT_DIR => EntryType.Directory,
                DT_LNK => EntryType.SymbolicLink,
                0 => Probe(path, System.Text.Encoding.UTF8.GetString(name)),
                _ => EntryType.Other,
            };
            batch.Add(name, type);
        }
        return pos;
    }

    private const int AT_SYMLINK_NOFOLLOW = 0x100, AT_STATX_DONT_SYNC = 0x4000;
    private const uint STATX_MTIME = 0x40, STATX_SIZE = 0x200;
    private const int ENOSYS = 38, EPERM = 1;

    /// <summary>
    /// Fills size/mtime for every entry in the batch with statx(dirfd, name, ...), relative to the open directory fd.
    /// With <paramref name="allowStale"/>, AT_STATX_DONT_SYNC lets the kernel answer from cached attributes
    /// (on NFS: the attributes READDIRPLUS already delivered) instead of a server round trip per file.
    /// </summary>
    internal static void StatBatch(int fd, string dir, DirectoryBatch batch, bool allowStale, int helpers)
    {
        if (helpers > 0 && batch.Count > 1) { StatWorkers.Run(fd, dir, batch, allowStale, helpers); return; }
        byte* sx = stackalloc byte[256];
        for (int i = 0; i < batch.Count; i++) StatOne(fd, dir, batch, i, allowStale, sx);
    }

    // Thread-safe: writes only entry i of the batch; sx is a per-thread 256-byte scratch buffer.
    private static void StatOne(int fd, string dir, DirectoryBatch batch, int i, bool allowStale, byte* sx)
    {
        if (SysStatx == 0 || s_statxUnavailable) { StatEmulation.FillOne(dir, batch, i); return; }

        uint mask = ((batch.Fields & StatFields.Size) != 0 ? STATX_SIZE : 0)
                  | ((batch.Fields & StatFields.ModifiedTime) != 0 ? STATX_MTIME : 0);
        int flags = AT_SYMLINK_NOFOLLOW | (allowStale ? AT_STATX_DONT_SYNC : 0);

        nint r;
        fixed (byte* np = batch.NamesBuffer)
            r = syscallStatx(SysStatx, fd, np + batch.NameOffset(i), flags, mask, sx);

        if (r < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is ENOSYS or EPERM)      // old kernel, or blocked by a seccomp profile
            {
                s_statxUnavailable = true;
                StatEmulation.FillOne(dir, batch, i);
            }
            else batch.SetStat(i, -1, DateTime.MinValue.Ticks);   // e.g. ENOENT: vanished since listing
            return;
        }
        // struct statx: mask@0 size@40 mtime{sec@112, nsec@120}
        if ((*(uint*)sx & mask) != mask)         // filesystem didn't provide it: fall back for this entry
        {
            StatEmulation.FillOne(dir, batch, i);
            return;
        }
        long size = (long)*(ulong*)(sx + 40);
        long sec = *(long*)(sx + 112);
        uint nsec = *(uint*)(sx + 120);
        batch.SetStat(i, size, DateTime.UnixEpoch.Ticks + sec * TimeSpan.TicksPerSecond + nsec / 100);
    }

    /// <summary>
    /// Process-wide pool of dedicated worker threads for parallel statx. Enumerations submit a <see cref="Job"/> and
    /// the calling thread works on it too, so correctness never depends on the workers being available.
    /// </summary>
    internal sealed class StatWorkers
    {
        private const int Chunk = 4;
        private static readonly object s_lock = new();
        private static StatWorkers? s_current;

        private readonly ConcurrentQueue<Job> _tickets = new();
        private readonly SemaphoreSlim _signal = new(0);
        private volatile bool _retired;

        public int Size { get; }

        private StatWorkers(int size)
        {
            Size = size;
            for (int i = 0; i < size; i++)
                new Thread(Worker) { IsBackground = true, Name = "FastNativeOps.Stat" }.Start();
        }

        private static StatWorkers Get()
        {
            int desired = FastNativeOptions.StatWorkerThreads;
            var c = s_current;
            if (c is not null && c.Size == desired) return c;
            lock (s_lock)
            {
                c = s_current;
                if (c is null || c.Size != desired)
                {
                    c?.Retire();               // in-flight jobs still finish: their callers work on them too
                    s_current = c = new StatWorkers(desired);
                }
                return c;
            }
        }

        private void Retire()
        {
            _retired = true;
            _signal.Release(Size);
        }

        private void Worker()
        {
            while (true)
            {
                _signal.Wait();
                if (_retired) return;
                if (_tickets.TryDequeue(out var job)) job.Work();
            }
        }

        /// <summary>Stats every entry of the batch using up to <paramref name="helpers"/> pool threads plus the caller.</summary>
        public static void Run(int fd, string dir, DirectoryBatch batch, bool allowStale, int helpers)
        {
            var pool = Get();
            var job = new Job(fd, dir, batch, allowStale);
            helpers = Math.Min(helpers, Math.Min(pool.Size, (batch.Count + Chunk - 1) / Chunk - 1));
            for (int i = 0; i < helpers; i++) pool._tickets.Enqueue(job);
            if (helpers > 0) pool._signal.Release(helpers);
            job.Work();
            job.Done.Wait();
        }

        internal sealed class Job(int fd, string dir, DirectoryBatch batch, bool allowStale)
        {
            private int _next, _completed;
            public readonly ManualResetEventSlim Done = new(false);

            public void Work()
            {
                byte* sx = stackalloc byte[256];
                int count = batch.Count;
                while (true)
                {
                    int i = Interlocked.Add(ref _next, Chunk) - Chunk;
                    if (i >= count) return;          // (a stale ticket for a finished job ends up here)
                    int end = Math.Min(i + Chunk, count);
                    for (int j = i; j < end; j++) StatOne(fd, dir, batch, j, allowStale, sx);
                    if (Interlocked.Add(ref _completed, end - i) == count) Done.Set();
                }
            }
        }
    }

    /// <summary>Zero-allocation batches straight from the kernel's getdents64 buffer. The same batch is reused.</summary>
    internal static IEnumerable<DirectoryBatch> EnumerateGetdentsBatches(
        string path, int batchSize, StatFields fields, bool allowStale, int parallelism)
    {
        nint dir = opendir(path);
        if (dir == 0)
            throw new IOException($"Cannot open '{path}' (errno {Marshal.GetLastPInvokeError()})");
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var batch = new DirectoryBatch(batchSize, fields) { DirectoryPath = path };
        int minEntries = FastNativeOptions.StatParallelMinEntries;
        try
        {
            int fd = dirfd(dir), pos = 0, n = 0;
            while (true)
            {
                if (pos >= n)
                {
                    n = GetDents(fd, buf);
                    pos = 0;
                    if (n < 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        if (errno == EINTR) { n = 0; continue; }
                        throw new IOException($"Cannot read '{path}' (errno {errno})");
                    }
                    if (n == 0) break;
                }
                pos = FillBatch(buf, pos, n, batch, path);
                if (batch.Count == batch.Capacity)
                {
                    if (fields != StatFields.None)
                    {
                        StatBatch(fd, path, batch, allowStale, batch.Count >= minEntries ? parallelism - 1 : 0);
                    }
                    yield return batch;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                if (fields != StatFields.None)
                {
                    StatBatch(fd, path, batch, allowStale, batch.Count >= minEntries ? parallelism - 1 : 0);
                }
                yield return batch;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            closedir(dir);
        }
    }

    public static IEnumerable<FileEntry> Enumerate(string path, NativeBackend backend)
    {
        switch (backend)
        {
            case NativeBackend.Readdir:
                return EnumerateReaddir(path);
            case NativeBackend.Getdents64:
                if (SysGetdents64 == 0)
                    throw new PlatformNotSupportedException("getdents64 is only supported on Linux x64/arm64.");
                return EnumerateGetdents(path);
            default:
                return SysGetdents64 != 0 ? EnumerateGetdents(path) : EnumerateReaddir(path);
        }
    }

    private static IEnumerable<FileEntry> EnumerateReaddir(string path)
    {
        nint dir = opendir(path);
        if (dir == 0)
            throw new IOException($"Cannot open '{path}' (errno {Marshal.GetLastPInvokeError()})");
        try
        {
            while (true)
            {
                nint ent = readdir(dir);
                if (ent == 0) yield break;

                var (name, dtype) = ReadDirent(ent);
                if (name is "." or "..") continue;

                var type = dtype switch
                {
                    DT_REG => EntryType.File,
                    DT_DIR => EntryType.Directory,
                    DT_LNK => EntryType.SymbolicLink,
                    0 => Probe(path, name),
                    _ => EntryType.Other,
                };
                yield return new FileEntry(name, type);
            }
        }
        finally { closedir(dir); }
    }
}
