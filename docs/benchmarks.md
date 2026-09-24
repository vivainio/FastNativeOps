# Benchmarks

BenchmarkDotNet, `--job short` (3 iterations, so treat as indicative), 50,000 files plus 100 subdirectories, warm cache.
Results depend heavily on OS, filesystem and hardware. **Run them on your own target environment**: see
[Run them yourself](#run-them-yourself).

!!! warning "Local filesystems only"
    These numbers are from local disks. They do **not** show the effect of network latency, which is what
    [`allowCachedAttributes`](guide/stat.md#cached-attributes) and [parallel stat](guide/nfs.md) are for. Measure on
    your NFS or EFS mount.

Environment: .NET 10.0.12, Linux arm64 (Ubuntu 24.04 container in a VM on an Apple M3); macOS arm64 (Apple M3, APFS).

## Listing 50,000 entries (names and types)

| Method (Linux arm64) | Mean | Allocated |
|---|---:|---:|
| `Directory.EnumerateFileSystemEntries` (baseline) | 4.25 ms | 8.4 MB |
| `FileSystemEnumerable` with a non-allocating transform | 3.37 ms | 216 B |
| `FastDirectory.Enumerate` (getdents64) | 1.92 ms | 2.8 MB |
| `FastDirectory.Enumerate` (`Readdir` backend) | 2.22 ms | 2.8 MB |
| `EnumerateBatches(1000)` | 2.09 ms | 3.6 MB |
| **`EnumerateBatchBuffers(1000)`** | **1.31 ms** | **37 KB** |

`getdents64` is about 14% faster than `readdir`, and batch buffers remove almost all allocation.

| Method (macOS arm64) | Mean | Allocated |
|---|---:|---:|
| `Directory.EnumerateFileSystemEntries` (baseline) | 17.1 ms | 12.8 MB |
| `FastDirectory.Enumerate` | 15.2 ms | 2.8 MB |
| `EnumerateBatchBuffers(1000)` | 15.8 ms | 2.8 MB |

On macOS the time is dominated by the filesystem and `readdir`; the gain is mostly fewer allocations.

## Listing with size and modification time

Linux arm64, local filesystem:

| Method | Mean | Allocated |
|---|---:|---:|
| `DirectoryInfo.EnumerateFileSystemInfos` (Length + LastWriteTime) | 50.6 ms | 20.4 MB |
| `EnumerateBatchBuffers` + stat | 34.6 ms | 52 KB |
| same, `allowCachedAttributes` | 32.6 ms | 52 KB |
| same, `statParallelism: 8` | 11.8 ms | 57 KB |
| same, `statParallelism: 32` | 11.4 ms | 57 KB |
| `allowCachedAttributes` + `statParallelism: 8` | 11.4 ms | 57 KB |

Even on a local filesystem, where a stat is cheap, parallelism helps (about 3x). Cached attributes make no difference
here because a local stat never goes to a server; that is the case on NFS.

## Filtered walk with stat

Recursive walk with size and mtime, Linux arm64:

| Method | Mean | Allocated |
|---|---:|---:|
| `WalkBatchBuffers` + stat, no filter (50,100 entries) | 32.3 ms | 81 KB |
| `WalkBatchBuffers` + stat, `Glob` keeping 10 files | **1.8 ms** | 57 KB |
| `DirectoryInfo.EnumerateFileSystemInfos(pattern, AllDirectories)`, same 10 files | 3.2 ms | 23 KB |

The filter runs before the stat pass, so 49,990 entries cost no `statx`. .NET's pattern overload also skips stat for
non-matching names, so the fair comparison is the last two rows; the unfiltered row shows what stat costs when every
entry is needed.

## Run them yourself

Requires the .NET 10 SDK. From the repository root:

```bash
dotnet run -c Release --project benchmarks/Bench -- --filter '*' --job short
```

| Environment variable | Default | |
|---|---|---|
| `FASTNATIVEOPS_BENCH_FILES` | `50000` | number of files to create; comma-separated for several sizes |
| `FASTNATIVEOPS_BENCH_DIR` | system temp | parent directory for the test files: point it at the filesystem you care about |

These two variables belong to the benchmark program only; the library itself is configured through
[`FastNativeOptions`](reference/configuration.md). The test directory is created before the run and deleted after.

!!! tip "On NFS or EFS"
    ```bash
    FASTNATIVEOPS_BENCH_DIR=/mnt/efs/scratch dotnet run -c Release --project benchmarks/Bench -- --filter '*Stat*' --job short
    ```
    Compare `Fast_BatchBuffers_1000_Stat`, `..._StatCached` and the `_Par8` / `_Par32` variants against
    `SystemIO_EnumerateFileSystemInfos_SizeMtime`, and please share the table.
