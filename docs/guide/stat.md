# Size and modification time

Listing does not stat. Ask for the fields you need:

```csharp
var fields = StatFields.Size | StatFields.ModifiedTime;

foreach (var batch in FastDirectory.EnumerateBatchBuffers(path, 1000, fields: fields))
    for (int i = 0; i < batch.Count; i++)
        Console.WriteLine($"{batch.GetName(i)}  {batch.GetSize(i)}  {batch.GetModifiedTimeUtc(i):O}");
```

`GetSize` returns `-1` and `GetModifiedTimeUtc` returns `DateTime.MinValue` for an entry that vanished after it was
listed. Asking for a field you did not request throws `InvalidOperationException`.

## What happens on Linux

After each `getdents64` batch the library calls `statx(dirfd, name, ...)` once per entry:

- **Relative to the directory descriptor**, so the kernel resolves one path component, not the full path.
- **Only the requested fields** (`STATX_SIZE`, `STATX_MTIME`) are asked for.
- `AT_SYMLINK_NOFOLLOW`: a symlink reports its own size and time.

.NET's own `FileSystemEntry.Length` instead calls `lstat` on the full path, one call per entry.

## Cached attributes

```csharp
FastDirectory.EnumerateBatchBuffers(path, 1000, fields: fields, allowCachedAttributes: true);
```

Adds `AT_STATX_DONT_SYNC`: the kernel may answer from cached attributes without contacting the server. On NFS that
means the attributes the directory read already delivered are used instead of one round trip per file. The price is that
values can be slightly stale, within the mount's attribute cache timeouts (`acregmin`, `acdirmin`). Without the flag the
call is revalidated like an ordinary `stat`.

## Parallel stat

```csharp
FastDirectory.EnumerateBatchBuffers(path, 1000, fields: fields, statParallelism: 16);
```

Runs several `statx` calls at once on a shared pool of dedicated threads. It is aimed at high-latency filesystems: see
[NFS and EFS](nfs.md) and [Configuration](../reference/configuration.md).

## Other platforms

Off Linux x64/arm64 (and if `statx` is blocked, for example by a seccomp profile) stat is filled in with
`System.IO` (`FileInfo`), which is slower but gives the same results.
