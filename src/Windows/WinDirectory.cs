using System.Runtime.InteropServices;

namespace FastNativeOps.Windows;

internal static partial class WinDirectory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindFirstFileEx(string name, int infoLevel, out WIN32_FIND_DATAW data, int searchOp, nint filter, int flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextFile(nint h, out WIN32_FIND_DATAW data);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint h);

    private const int FindExInfoBasic = 1, FindExSearchNameMatch = 0, LargeFetch = 2;
    private const uint DIR = 0x10, REPARSE = 0x400;
    private const int ERROR_NO_MORE_FILES = 18;

    public static IEnumerable<FileEntry> Enumerate(string path)
    {
        var pattern = Path.Join(path, "*");
        nint h = FindFirstFileEx(pattern, FindExInfoBasic, out var d, FindExSearchNameMatch, 0, LargeFetch);
        if (h == -1)
            throw new IOException($"Cannot open '{path}' (error {Marshal.GetLastPInvokeError()})");
        try
        {
            do
            {
                var e = Convert(d);
                if (e is not null) yield return e.Value;
            }
            while (FindNextFile(h, out d));

            int err = Marshal.GetLastPInvokeError();
            if (err != ERROR_NO_MORE_FILES)
                throw new IOException($"Enumeration of '{path}' failed (error {err})");
        }
        finally { FindClose(h); }
    }

    private static unsafe FileEntry? Convert(in WIN32_FIND_DATAW d)
    {
        fixed (WIN32_FIND_DATAW* p = &d)
        {
            var name = new string(p->cFileName);
            if (name is "." or "..") return null;
            var a = p->dwFileAttributes;
            var type = (a & REPARSE) != 0 ? EntryType.SymbolicLink
                     : (a & DIR) != 0 ? EntryType.Directory
                     : EntryType.File;
            return new FileEntry(name, type);
        }
    }
}
