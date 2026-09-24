# Listing a directory

```csharp
IEnumerable<FileEntry> entries = FastDirectory.Enumerate(path);
List<FileEntry> all = FastDirectory.List(path);
```

`FileEntry` is a `readonly record struct FileEntry(string Name, EntryType Type)`.

| `EntryType` | Meaning |
|---|---|
| `File` | regular file (on Windows: anything that is not a directory or reparse point) |
| `Directory` | directory |
| `SymbolicLink` | symbolic link (not followed; Windows: reparse point) |
| `Other` | socket, FIFO, device, ... |
| `Unknown` | the filesystem did not say and the fallback probe failed |

The type comes free with the directory entry (`d_type`). On filesystems that do not report it (older XFS, some FUSE and
network mounts) the library asks the OS for that entry instead, which costs one call.

## Laziness and errors

`Enumerate` reads from the native directory handle as you iterate and closes it when the loop ends, throws, or is
disposed (a `break` disposes the enumerator). Errors such as a missing directory surface on the first `MoveNext`, not when
you call `Enumerate`.

## Batches

```csharp
foreach (FileEntry[] batch in FastDirectory.EnumerateBatches(path, 1000))
{
    // up to 1000 entries; the last batch may be smaller
}
```

Each batch is a fresh array, so you may keep it. The directory handle stays open between batches: finish the loop
promptly. For lower allocation use [batch buffers](batch-buffers.md).

## Backend

Every listing call takes an optional [`NativeBackend`](platforms.md); the default (`Auto`) is right for almost everyone.
