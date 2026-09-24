# NFS and EFS

Network filesystems are latency-bound: every operation that is not answered from the client's cache costs a network
round trip. .NET's default per-file `lstat` therefore costs one round trip per file. This library reduces the count and
overlaps the rest.

## What helps, in order

1. **Do not stat what you do not need.** `Enumerate` and `EnumerateBatchBuffers` without `fields` never stat.
   Add an [`EntryFilter`](filtering.md) so non-matching entries are skipped before the stat pass.
2. **Allow cached attributes.** `allowCachedAttributes: true` (`AT_STATX_DONT_SYNC`) lets `statx` use the attributes
   the directory read already delivered (READDIRPLUS on NFSv3, attribute-returning READDIR on NFSv4).
   Values may be stale within `acregmin` / `acdirmin`.
3. **Overlap the remaining calls.** `statParallelism: 16` runs many `statx` calls at once, hiding per-call latency. EFS
   throughput generally scales with concurrency.
4. **Relative operations.** `statx` and `openat` relative to a directory descriptor avoid resolving the full path each
   time.

## Try it on your mount

The repository includes benchmarks you can run against any directory:

```bash
FASTNATIVEOPS_BENCH_DIR=/mnt/efs/scratch FASTNATIVEOPS_BENCH_FILES=50000 \
  dotnet run -c Release --project benchmarks/Bench -- --filter '*Stat*' --job short
```

See [Benchmarks](../benchmarks.md) for how to read the results.

## What does not help on EFS

- **`copy_file_range` and reflinks.** Server-side copy is an NFSv4.2 feature; EFS supports NFSv4.0/4.1 only (check the
  current AWS documentation), so a kernel copy still moves the data through the client.
- **io_uring.** A plain thread pool already hides latency for blocking syscalls; io_uring is often disabled in
  containers and is not used here.

## Mount options matter more than code

`rsize`/`wsize`, `nconnect` where supported, and the attribute cache timeouts (`acregmin`, `acregmax`, `acdirmin`,
`acdirmax`) change results more than any library. EFS mount helpers set sensible defaults.
