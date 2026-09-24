namespace FastNativeOps;

public enum EntryType { Unknown, File, Directory, SymbolicLink, Other }

public readonly record struct FileEntry(string Name, EntryType Type);
