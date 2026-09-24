# How it works

## Why not just `System.IO`?

`Directory.EnumerateFileSystemEntries` is a good general API, and it is not slow. But it creates a string per entry
(often a full path), and its per-entry properties (`Length`, `LastWriteTime`) trigger one `lstat` on the full path per
entry. On a local disk that is fine. On NFS each of those calls can be a network round trip, and a listing of thousands of
files is dominated by them.

## The pieces

**`getdents64`.** libc's `readdir` returns one entry per call. The kernel's `getdents64` fills a 64 KB buffer with many
entries per syscall; the library parses the records itself. The record layout is fixed by the kernel ABI, so it does not
vary between glibc and musl.

**UTF-8 batch buffers.** Names stay as bytes, in one buffer, NUL-terminated so the same bytes can be passed straight back
to the kernel for `statx`. Strings are made only when you ask.

**`statx` on the directory descriptor.** The directory is opened once; each entry is stat'ed relative to that descriptor
with a mask of exactly the fields wanted. With `AT_STATX_DONT_SYNC` the kernel may use its attribute cache.

**Parallel stat.** A process-wide pool of dedicated threads shares the `statx` calls of a batch. The caller works too, so
correctness never depends on the pool.

**`openat` walk.** Subdirectories are opened relative to their parent's descriptor, depth-first. Directory descriptors
are closed as soon as they are no longer needed.

**Filters before stat.** The name filter runs while the batch is being filled, before the stat pass.

## Deliberate limits

- 64-bit only, and native fast paths only on Linux x64/arm64 (macOS: `readdir`; Windows: `System.IO`).
- No `io_uring`: a thread pool hides blocking-syscall latency well enough, with far less machinery and no dependency on
  a kernel feature that containers commonly disable.
- Symlinks are never followed in a walk.
- Names are reported as they exist on disk, decoded as UTF-8. Names that are not valid UTF-8 are replaced by U+FFFD when
  turned into strings.

## Testing

Each backend and batch size is compared against `System.IO` on generated trees (including non-ASCII names, symlink
loops, unreadable directories, a 2,100-level tree deeper than `PATH_MAX`, and running out of file descriptors). CI runs
the suite on Linux x64 and arm64, macOS and Windows, for .NET 8 and .NET 10.
