# Getting started

## Install

```bash
dotnet add package FastNativeOps
```

Targets `net8.0` and `net10.0`, 64-bit only. The library uses unsafe code internally; your project does not need to.

## List a directory

```csharp
using FastNativeOps;

foreach (FileEntry e in FastDirectory.Enumerate("/usr"))
    Console.WriteLine($"{e.Type,-13} {e.Name}");
```

`Enumerate` is lazy and returns names and entry types only. It does **not** stat anything, which is what makes it fast.
`.` and `..` are skipped. A missing or unreadable directory throws `IOException` when iteration starts.

## Choose the right call

| You want | Use |
|---|---|
| Names and types, simple code | [`Enumerate` / `List`](guide/listing.md) |
| Replace `Directory.GetFiles` / `GetDirectories` | [`EnumerateFiles` / `EnumerateDirectories`](guide/listing.md#drop-in-for-directorygetfiles-getdirectories) |
| Chunks of N entries as arrays | [`EnumerateBatches`](guide/listing.md#batches) |
| Lowest allocation, optional size / mtime | [`EnumerateBatchBuffers`](guide/batch-buffers.md) |
| Everything under a directory | [`WalkBatchBuffers`](guide/walk.md) |
| Only some names (skipping stat for the rest) | [`EntryFilter`](guide/filtering.md) with `EnumerateBatchBuffers` or `WalkBatchBuffers` |

## Configure once at startup

```csharp
FastNativeOptions.StatWorkerThreads = 16;      // shared worker pool for parallel stat
FastNativeOptions.StatParallelism = 16;        // concurrent stat calls per enumeration (default 1)
```

See [Configuration](reference/configuration.md). On a fast local disk the defaults are fine; on NFS or EFS read
[NFS and EFS](guide/nfs.md).
