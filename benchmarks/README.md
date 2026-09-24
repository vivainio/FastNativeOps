# Benchmarks

BenchmarkDotNet comparison of `FastNativeOps` against `System.IO` on a directory with many files. Results depend a
lot on OS, filesystem and hardware, so please run it in your own target environment.

Requires the .NET 10 SDK. From the repo root:

```bash
dotnet run -c Release --project benchmarks/Bench -- --filter '*'
```

Quick, noisier run (a few minutes less):

```bash
dotnet run -c Release --project benchmarks/Bench -- --filter '*' --job short
```

Options (environment variables):

| Variable | Default | Meaning |
|---|---|---|
| `FASTNATIVEOPS_BENCH_FILES` | `50000` | Number of files to create; comma-separated for several sizes, e.g. `1000,50000,500000` |
| `FASTNATIVEOPS_BENCH_DIR` | system temp | Parent directory for the test files. Point it at the filesystem you care about (ext4, XFS, NFS, ...) |

The test directory (plus 100 subdirectories) is created before the run and deleted afterwards.

## What is measured

| Benchmark | What it does |
|---|---|
| `SystemIO_EnumerateEntries` | `Directory.EnumerateFileSystemEntries` (baseline) |
| `SystemIO_FileSystemEnumerable_Names` | `FileSystemEnumerable` with a non-allocating transform, the fastest built-in route |
| `Fast_Enumerate` | `FastDirectory.Enumerate` (default backend: getdents64 on Linux) |
| `Fast_Enumerate_Readdir` | same with `NativeBackend.Readdir` |
| `Fast_List` | `FastDirectory.List` |
| `Fast_Batches_1000` | `EnumerateBatches`, batches of 1000 |
| `Fast_BatchBuffers_1000` | `EnumerateBatchBuffers`, batches of 1000, reading name spans without allocating |

Please share your results (the markdown table plus the `BenchmarkDotNet` header lines with OS and CPU) in an issue.

## Sample results (short job, 50,000 files)

Rough numbers from `--job short` (3 iterations); take them as indicative only.

**Linux arm64** (Ubuntu 24.04 container in a VM on an Apple M3)

| Method | Mean | Allocated |
|---|---|---|
| SystemIO_EnumerateEntries | 4.25 ms | 8.4 MB |
| SystemIO_FileSystemEnumerable_Names | 3.37 ms | 216 B |
| Fast_Enumerate | 1.92 ms | 2.8 MB |
| Fast_Enumerate_Readdir | 2.22 ms | 2.8 MB |
| Fast_Batches_1000 | 2.09 ms | 3.6 MB |
| Fast_BatchBuffers_1000 | 1.31 ms | 37 KB |

**macOS arm64** (Apple M3, APFS)

| Method | Mean | Allocated |
|---|---|---|
| SystemIO_EnumerateEntries | 17.1 ms | 12.8 MB |
| SystemIO_FileSystemEnumerable_Names | 103 ms | 216 B |
| Fast_Enumerate | 15.2 ms | 2.8 MB |
| Fast_BatchBuffers_1000 | 15.8 ms | 2.8 MB |

On Linux the getdents64 path and batch buffers are clearly ahead. On macOS the time is dominated by the filesystem and
`readdir`, so the gain is mostly fewer allocations. Windows has not been measured yet.
