---
icon: lucide/folder-tree
hide:
  - toc
---

# FastNativeOps

Fast file system access for .NET, using direct P/Invoke instead of the general-purpose `System.IO` layers.
Built for big directories and high-latency filesystems such as NFS and AWS EFS.

```csharp
using FastNativeOps;

var options = new WalkOptions
{
    Fields = StatFields.Size | StatFields.ModifiedTime,
    EntryFilter = EntryFilters.Glob("*.csv"),
};

foreach (DirectoryBatch batch in FastDirectory.WalkBatchBuffers("/mnt/data", 1000, options))
    for (int i = 0; i < batch.Count; i++)
        Console.WriteLine($"{batch.DirectoryPath}/{batch.GetName(i)}  {batch.GetSize(i)} bytes");
```

<div class="grid cards" markdown>

-   :lucide-list: **[Directory listing](guide/listing.md)**

    Names and entry types straight from `getdents64` (Linux) or `readdir` (macOS), lazily, or as arrays.

-   :lucide-layers: **[Batch buffers](guide/batch-buffers.md)**

    One reusable buffer of UTF-8 names per batch: close to zero allocations per entry.

-   :lucide-ruler: **[Stat fields](guide/stat.md)**

    Size, times and attributes matching `FileSystemInfo`: one `statx` per entry relative to the directory descriptor, optionally cached, optionally parallel.

-   :lucide-folder-tree: **[Recursive walk](guide/walk.md)**

    `openat`-based, no path length limit, symlink-loop safe, with depth limits and pruning.

-   :lucide-filter: **[Filtering](guide/filtering.md)**

    Glob, extension and regex filters applied *before* the stat pass, so skipped entries cost nothing.

-   :lucide-network: **[NFS and EFS](guide/nfs.md)**

    Where the time actually goes on network filesystems, and which knobs help.

</div>

## Status

Early (0.0.x). Directory listing, batches, `statx`, walk, filters and parallel stat are implemented and tested on Linux
(x64, arm64), macOS and Windows. Windows uses plain `System.IO`; the native fast paths are for Linux (and `readdir` on
macOS). 64-bit only. Targets .NET 8 and .NET 10.

