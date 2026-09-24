namespace FastNativeOps;

public static class FastDirectory
{
    /// <summary>Lists entries of a directory (excluding "." and ".."), using native APIs.</summary>
    public static IEnumerable<FileEntry> Enumerate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return OperatingSystem.IsWindows()
            ? Windows.WinDirectory.Enumerate(path)
            : Unix.UnixDirectory.Enumerate(path);
    }

    public static List<FileEntry> List(string path) => Enumerate(path).ToList();
}
