namespace FastNativeOps;

public enum NativeBackend
{
    /// <summary>Best available: getdents64 on Linux x64/arm64, readdir on other Unix, FindFirstFileEx on Windows.</summary>
    Auto,
    /// <summary>libc opendir/readdir (Linux and macOS).</summary>
    Readdir,
    /// <summary>Raw getdents64 syscall (Linux x64/arm64 only).</summary>
    Getdents64,
}
