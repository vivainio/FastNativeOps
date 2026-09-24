# FastNativeOps

Fast cross-platform file system access for .NET using direct P/Invoke.

| Platform | Backend |
|---|---|
| Linux x64 / arm64 | raw `getdents64` syscall (default), or libc `readdir` |
| macOS | libc `opendir` / `readdir` |
| Windows | `FindFirstFileExW` (basic info, large fetch) |

## Usage

```csharp
using FastNativeOps;

foreach (var e in FastDirectory.Enumerate("/usr"))
    Console.WriteLine($"{e.Type} {e.Name}");
```

Entries are `FileEntry(Name, Type)` where `Type` is `File`, `Directory`, `SymbolicLink`, `Other` or `Unknown`.
`.` and `..` are skipped. Enumeration is lazy: the native handle is opened on first iteration and closed when the
loop ends or is disposed. `FastDirectory.List(path)` returns everything as a `List<FileEntry>`.

### Batches

For directories with thousands of entries:

```csharp
foreach (FileEntry[] batch in FastDirectory.EnumerateBatches(path, 1000)) { /* ... */ }
```

### Batch buffers (fewer allocations)

`EnumerateBatchBuffers` yields one reused `DirectoryBatch` with names stored as UTF-8 in a single buffer, so there is
no per-entry string and no per-batch array:

```csharp
foreach (DirectoryBatch batch in FastDirectory.EnumerateBatchBuffers(path, 1000))
    for (int i = 0; i < batch.Count; i++)
    {
        ReadOnlySpan<byte> name = batch.GetNameUtf8(i);   // no allocation
        if (batch.GetType(i) == EntryType.File && name.EndsWith(".cs"u8)) { /* ... */ }
    }
```

Do not keep the batch or its spans past the next iteration (`batch.ToArray()` copies out). This is fully zero-copy on
Linux x64/arm64 (`getdents64`); on other platforms it is emulated by copying names into the same buffer, so the API is
identical but the allocation savings are smaller.

### Choosing a backend

`NativeBackend.Auto` (default) picks the best backend for the platform. Force one with:

```csharp
FastDirectory.Enumerate(path, NativeBackend.Readdir);
FastDirectory.EnumerateBatches(path, 1000, NativeBackend.Getdents64);
```

`Getdents64` is Linux x64/arm64 only; requesting an unsupported backend throws `PlatformNotSupportedException`.

## Status

- Directory listing only (no size / mtime yet).
- 64-bit only. Targets net8.0 and net10.0.
- CI builds and checks entry counts on Linux (x64, arm64), macOS and Windows. musl (Alpine) was verified manually on arm64 only.
