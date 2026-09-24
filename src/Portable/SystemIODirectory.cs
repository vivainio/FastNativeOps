using System.IO.Enumeration;

namespace FastNativeOps.Portable;

/// <summary>Plain System.IO implementation, used on Windows (where speed is not a goal).</summary>
internal static class SystemIODirectory
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,            // include hidden and system entries, like readdir does
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        RecurseSubdirectories = false,
    };

    public static IEnumerable<FileEntry> Enumerate(string path) =>
        new FileSystemEnumerable<FileEntry>(path, static (ref FileSystemEntry e) =>
            new FileEntry(e.FileName.ToString(),
                (e.Attributes & FileAttributes.ReparsePoint) != 0 ? EntryType.SymbolicLink
                : e.IsDirectory ? EntryType.Directory
                : EntryType.File), Options);
}
