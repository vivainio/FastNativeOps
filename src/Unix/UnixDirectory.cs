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

    public static IEnumerable<FileEntry> Enumerate(string path)
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
