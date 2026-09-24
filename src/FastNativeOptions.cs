namespace FastNativeOps;

/// <summary>Process-wide configuration. Set these once at startup; per-call arguments override the defaults.</summary>
public static class FastNativeOptions
{
    /// <summary>
    /// Size of the process-wide pool of dedicated worker threads that perform parallel stat calls. The pool is created
    /// lazily on first parallel use and shared by all enumerations. Dedicated threads are used instead of the .NET
    /// thread pool, which ramps up slowly and would be starved by blocking syscalls. Changing this value replaces the pool.
    /// Default 16.
    /// </summary>
    public static int StatWorkerThreads
    {
        get => _statWorkerThreads;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _statWorkerThreads = value;
        }
    }

    private static int _statWorkerThreads = 16;

    /// <summary>
    /// Number of concurrent stat calls one enumeration uses when filling <see cref="DirectoryBatch"/> stat fields
    /// (1 = sequential; the calling thread counts as one, the rest come from the shared worker pool, so the effective
    /// maximum is <see cref="StatWorkerThreads"/> + 1). On high-latency filesystems such as NFS, raising this hides
    /// the per-file round trip. Can be overridden per call. Default 1.
    /// </summary>
    public static int StatParallelism
    {
        get => _statParallelism;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _statParallelism = value;
        }
    }

    /// <summary>
    /// Parallel stat is only used for batches with at least this many entries; smaller batches (and directories)
    /// are stat'ed sequentially, so tiny directories never pay for thread startup.
    /// Default 32.
    /// </summary>
    public static int StatParallelMinEntries
    {
        get => _statParallelMinEntries;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _statParallelMinEntries = value;
        }
    }

    private static int _statParallelMinEntries = 32;

    private static int _statParallelism = 1;
}
