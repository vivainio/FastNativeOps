# FastNativeOps

Fast cross-platform file system access for .NET using direct P/Invoke.

| Platform | Backend |
|---|---|
| Linux x64 / arm64 | raw `getdents64` syscall (default), or libc `readdir` |
| macOS | libc `opendir` / `readdir` |
| Windows | plain `System.IO` (no native code; speed is not a goal there) |

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

### Size and modification time

Ask for stat fields when you enumerate batch buffers:

```csharp
var fields = StatFields.Size | StatFields.ModifiedTime;
foreach (DirectoryBatch batch in FastDirectory.EnumerateBatchBuffers(path, 1000, fields: fields, allowCachedAttributes: true))
    for (int i = 0; i < batch.Count; i++)
        Console.WriteLine($"{batch.GetName(i)} {batch.GetSize(i)} {batch.GetModifiedTimeUtc(i):O}");
```

On Linux x64/arm64 this calls `statx` relative to the directory fd, requesting only the fields you asked for. With
`allowCachedAttributes: true` it passes `AT_STATX_DONT_SYNC`, so the kernel may answer from its attribute cache. On NFS
that means the attributes already delivered by READDIRPLUS are used instead of one server round trip per file, at the
price of possibly slightly stale values (subject to the mount's `acregmin`/`acdirmin` settings). Without the flag,
values are revalidated like a normal `stat`. `GetSize` returns -1 and `GetModifiedTimeUtc` returns `DateTime.MinValue`
for an entry that vanished after it was listed. On other platforms (and if `statx` is blocked, e.g. by a seccomp
profile) it falls back to `System.IO`, which is slower.

### Parallel stat (NFS and other high-latency filesystems)

Every stat is a syscall, and on NFS a cache miss costs a network round trip. Running several in parallel hides that
latency. Configure it once at startup with the static `FastNativeOptions` object:

```csharp
FastNativeOptions.StatWorkerThreads = 16;    // size of the shared worker pool (default 16)
FastNativeOptions.StatParallelism = 16;      // concurrent stats per enumeration (default 1 = sequential)
FastNativeOptions.StatParallelMinEntries = 32; // only batches at least this big use the pool (default 32)

// or override the parallelism for a single call
FastDirectory.EnumerateBatchBuffers(path, 1000, fields: fields, statParallelism: 4);
```

The worker pool is process-wide and created lazily the first time a parallel stat is needed. It uses dedicated
threads, because blocking syscalls would starve the .NET thread pool, which also ramps up slowly. All enumerations share
it, and the calling thread always takes part in its own batch, so a busy pool never causes a hang. Small directories and
short batches are stat'ed sequentially and never touch the pool. On a fast local filesystem parallelism gives little or
nothing; measure on your mount with the benchmarks.

### Choosing a backend

`NativeBackend.Auto` (default) picks the best backend for the platform. Force one with:

```csharp
FastDirectory.Enumerate(path, NativeBackend.Readdir);
FastDirectory.EnumerateBatches(path, 1000, NativeBackend.Getdents64);
```

`Getdents64` is Linux x64/arm64 only; requesting an unsupported backend throws `PlatformNotSupportedException`.

## Benchmarks

See [benchmarks/](benchmarks/README.md); build and run them in your own environment. On Linux arm64 with 50,000 files,
`EnumerateBatchBuffers` took about 1.3 ms and 37 KB versus 4.2 ms and 8.4 MB for `Directory.EnumerateFileSystemEntries`.
On macOS the gain is mostly allocations.

## Status

- Directory listing plus size / mtime (`statx` on Linux). No recursive walk yet.
- 64-bit only. Targets net8.0 and net10.0.
- CI builds and checks entry counts on Linux (x64, arm64), macOS and Windows. musl (Alpine) was verified manually on arm64 only.
