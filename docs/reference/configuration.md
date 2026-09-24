# Configuration

Process-wide settings live on the static `FastNativeOptions` class. Set them **once at startup**; per-call arguments
override the defaults where noted. There are no environment variables.

| Property | Default | Meaning |
|---|---|---|
| `StatWorkerThreads` | `16` | Size of the shared pool of dedicated worker threads used for parallel stat. Created lazily on first parallel use. Changing it replaces the pool. |
| `StatParallelism` | `1` | Concurrent stat calls per enumeration (the caller counts as one, the rest come from the pool). `1` = sequential. Overridable per call with `statParallelism:` / `WalkOptions.StatParallelism`. |
| `StatParallelMinEntries` | `32` | Only batches with at least this many entries use the pool; smaller ones are stat'ed sequentially, so tiny directories never pay for thread startup. |

```csharp
FastNativeOptions.StatWorkerThreads = 32;
FastNativeOptions.StatParallelism = 16;
FastNativeOptions.StatParallelMinEntries = 64;
```

## Why dedicated threads

Parallel stat spends its time blocked in a syscall. The .NET thread pool assumes work is mostly CPU-bound and adds
threads slowly, so blocked calls would starve it. The pool here is process-wide, shared by all enumerations, and the
calling thread always works on its own batch too, so a busy or resized pool can never cause a hang.

## Guidance

- Local SSD: leave the defaults. Parallel stat gives little there.
- NFS / EFS: start with `StatParallelism` between 8 and 32 and measure with the [benchmarks](../benchmarks.md).
- Very small directories are unaffected either way (`StatParallelMinEntries`).
