namespace FastNativeOps;

/// <summary>Options for <see cref="FastDirectory.WalkBatchBuffers"/>.</summary>
public sealed class WalkOptions
{
    /// <summary>How many levels below the root to descend (0 = only the root's own entries). Default: unlimited.</summary>
    public int MaxDepth { get; set; } = int.MaxValue;

    /// <summary>Called for each subdirectory before descending into it: (directory name, its depth). Return false to skip it.</summary>
    public Func<string, int, bool>? ShouldDescend { get; set; }

    /// <summary>Skip directories that cannot be opened or read (e.g. permission denied) instead of throwing.</summary>
    public bool IgnoreInaccessible { get; set; }

    /// <summary>Optional size / modification time for every entry (see <see cref="FastDirectory.EnumerateBatchBuffers"/>).</summary>
    public StatFields Fields { get; set; }

    /// <summary>Allow the kernel to answer stat from cached attributes (AT_STATX_DONT_SYNC); see EnumerateBatchBuffers.</summary>
    public bool AllowCachedAttributes { get; set; }

    /// <summary>Concurrent stat calls per batch (0 = <see cref="FastNativeOptions.StatParallelism"/>).</summary>
    public int StatParallelism { get; set; }

    public NativeBackend Backend { get; set; } = NativeBackend.Auto;
}
