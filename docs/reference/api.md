# API overview

Namespace `FastNativeOps`.

## `FastDirectory`

```csharp
IEnumerable<FileEntry>      Enumerate(string path);
IEnumerable<FileEntry>      Enumerate(string path, NativeBackend backend);
List<FileEntry>             List(string path);

IEnumerable<string>         EnumerateFiles(string path);        // full paths, same set as Directory.EnumerateFiles
IEnumerable<string>         EnumerateDirectories(string path);  // full paths, same set as Directory.EnumerateDirectories

IEnumerable<FileEntry[]>    EnumerateBatches(string path, int batchSize, NativeBackend backend = Auto);

IEnumerable<DirectoryBatch> EnumerateBatchBuffers(
    string path, int batchSize, NativeBackend backend = Auto,
    StatFields fields = None, bool allowCachedAttributes = false, int statParallelism = 0,
    EntryFilter? filter = null, NameFilter? nameFilter = null);   // both run before stat; see Filtering

IEnumerable<DirectoryBatch> WalkBatchBuffers(string root, int batchSize, WalkOptions? options = null);
```

`statParallelism: 0` means "use `FastNativeOptions.StatParallelism`". All methods are lazy; errors surface on the first
`MoveNext`, except argument validation, which throws immediately for the batch methods.

## Types

| Type | |
|---|---|
| `FileEntry` | `readonly record struct FileEntry(string Name, EntryType Type)` |
| `EntryType` | `[Flags]`: `Unknown`, `File`, `Directory`, `SymbolicLink`, `Other`; combo `NotDirectory`. An entry has exactly one value |
| `DirectoryBatch` | reusable batch: `Count`, `GetNameUtf8`, `GetName`, `GetType`, indexer, `ToArray`, `GetSize`, `GetModifiedTimeUtc`, `GetCreationTimeUtc`, `GetLastAccessTimeUtc`, `GetAttributes`, `Fields`, `DirectoryPath`, `Depth` |
| `StatFields` | flags: `None`, `Size`, `ModifiedTime`, `CreationTime`, `LastAccessTime`, `Attributes` |
| `NativeBackend` | `Auto`, `Readdir`, `Getdents64` |
| `WalkOptions` | see [Recursive walk](../guide/walk.md) |
| `NameFilter` | `delegate bool NameFilter(ReadOnlySpan<byte> nameUtf8)` |
| `EntryFilter` | `delegate bool EntryFilter(ReadOnlySpan<byte> nameUtf8, EntryType type)` |
| `EntryFilters` | `Glob`, `Extension`, `Regex`, `OfType`, `Not`, `And`, `Or` |
| `FastNativeOptions` | static [configuration](configuration.md) |

## Exceptions

| | |
|---|---|
| `DirectoryNotFoundException` | the directory does not exist or a path component is not a directory (as System.IO) |
| `UnauthorizedAccessException` | permission denied opening the directory (as System.IO) |
| `IOException` | other open or read failures (message includes the errno); out of file descriptors during a walk (errno 24) |
| `PlatformNotSupportedException` | a backend that is not available on this platform was requested explicitly |
| `InvalidOperationException` | any stat getter without requesting its field |
| `ArgumentOutOfRangeException` | `batchSize < 1`, negative depth or parallelism |
