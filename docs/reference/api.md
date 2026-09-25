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
    EntryFilter? filter = null);   // filter runs before stat; see Filtering

IEnumerable<DirectoryBatch> WalkBatchBuffers(string root, int batchSize, WalkOptions? options = null);
```

`statParallelism: 0` means "use `FastNativeOptions.StatParallelism`". All methods are lazy; errors surface on the first
`MoveNext`, except argument validation, which throws immediately for the batch methods.

## Types

| Type | |
|---|---|
| `FileEntry` | `readonly record struct FileEntry(string Name, EntryType Type)` |
| `EntryType` | `Unknown`, `File`, `Directory`, `SymbolicLink`, `Other` |
| `DirectoryBatch` | reusable batch: `Count`, `GetNameUtf8`, `GetName`, `GetType`, indexer, `ToArray`, `GetSize`, `GetModifiedTimeUtc`, `Fields`, `DirectoryPath`, `Depth` |
| `StatFields` | flags: `None`, `Size`, `ModifiedTime` |
| `NativeBackend` | `Auto`, `Readdir`, `Getdents64` |
| `WalkOptions` | see [Recursive walk](../guide/walk.md) |
| `EntryFilter` | `delegate bool EntryFilter(ReadOnlySpan<byte> nameUtf8, EntryType type)` |
| `EntryFilters` | `Glob`, `Extension`, `Regex`, `OfType`, `Not`, `And`, `Or` |
| `FastNativeOptions` | static [configuration](configuration.md) |

## Exceptions

| | |
|---|---|
| `IOException` | directory cannot be opened or read (message includes the errno); out of file descriptors during a walk (errno 24) |
| `PlatformNotSupportedException` | a backend that is not available on this platform was requested explicitly |
| `InvalidOperationException` | `GetSize` / `GetModifiedTimeUtc` without requesting the field |
| `ArgumentOutOfRangeException` | `batchSize < 1`, negative depth or parallelism |
