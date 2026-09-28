namespace FastNativeOps;

/// <summary>
/// Type of a directory entry. Every entry has exactly one of the single-bit values; the flags form exists so
/// <see cref="EntryFilters.OfType"/> can take a set such as <c>File | SymbolicLink</c>.
/// </summary>
[Flags]
public enum EntryType
{
    Unknown = 1,
    File = 2,
    Directory = 4,
    SymbolicLink = 8,
    Other = 16,

    /// <summary>Everything except <see cref="Directory"/>.</summary>
    NotDirectory = Unknown | File | SymbolicLink | Other,
}

public readonly record struct FileEntry(string Name, EntryType Type);
