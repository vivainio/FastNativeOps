# Stat fields: size, times and attributes

Listing does not stat. Ask for the fields you need:

```csharp
var fields = StatFields.Size | StatFields.ModifiedTime;

foreach (var batch in FastDirectory.EnumerateBatchBuffers(path, 1000, fields: fields))
    for (int i = 0; i < batch.Count; i++)
        Console.WriteLine($"{batch.GetName(i)}  {batch.GetSize(i)}  {batch.GetModifiedTimeUtc(i):O}");
```

`GetSize` returns `-1`, the time getters return `DateTime.MinValue` and `GetAttributes` returns `(FileAttributes)(-1)`
for an entry that vanished after it was listed. Asking for a field you did not request throws `InvalidOperationException`.

## Creation time, access time and attributes

`StatFields.CreationTime`, `LastAccessTime` and `Attributes` reproduce what `DirectoryInfo.GetFileSystemInfos` reports,
so a caller building a `created / lastaccess / lastwrite / attributes / length` listing can switch over without changing
its output. They add bits to the same `statx` call, not extra calls.

| Field | Getter | On Linux |
| --- | --- | --- |
| `CreationTime` | `GetCreationTimeUtc` | the older of ctime and mtime, which is what .NET reports on Linux; btime is not used, so it is available on every filesystem, NFS and EFS included |
| `LastAccessTime` | `GetLastAccessTimeUtc` | `stx_atime`, subject to the mount's `relatime`/`noatime` policy |
| `Attributes` | `GetAttributes` | as .NET derives them: `Directory`; `ReadOnly` if the permission class that applies to the process (owner, a group it is in, other) can read but not write; `Hidden` for names starting with `.`; `ReparsePoint` for symbolic links; `Normal` if none apply |

Symbolic links are not followed. A link reports its own times and `ReparsePoint` (plus `Hidden`), while .NET also adds
`Directory` and `ReadOnly` from the link's target. Emulated platforms use `FileSystemInfo` and match .NET exactly.

## What happens on Linux

After each `getdents64` batch the library calls `statx(dirfd, name, ...)` once per entry:

- **Relative to the directory descriptor**, so the kernel resolves one path component, not the full path.
- **Only the requested fields** (`STATX_SIZE`, `STATX_MTIME`, ...) are asked for.
- `AT_SYMLINK_NOFOLLOW`: a symlink reports its own size, times and attributes.

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
