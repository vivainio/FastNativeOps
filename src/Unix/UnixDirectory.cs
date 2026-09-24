using System.Buffers;
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

    /// <summary>Zero-allocation batches straight from the kernel's getdents64 buffer. The same batch is reused.</summary>
    internal static IEnumerable<DirectoryBatch> EnumerateGetdentsBatches(string path, int batchSize)
    {
        nint dir = opendir(path);
        if (dir == 0)
            throw new IOException($"Cannot open '{path}' (errno {Marshal.GetLastPInvokeError()})");
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var batch = new DirectoryBatch(batchSize);
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
                    yield return batch;
                    batch.Clear();
                }
            }
            if (batch.Count > 0) yield return batch;
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
