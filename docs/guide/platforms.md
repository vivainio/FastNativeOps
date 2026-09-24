# Platforms and backends

| Platform | Listing | Stat |
|---|---|---|
| Linux x64 / arm64 (glibc and musl) | `getdents64` syscall | `statx` |
| macOS | `opendir` / `readdir` | `System.IO` |
| Windows | `System.IO` | `System.IO` |
| Other Linux architectures | `readdir` | `System.IO` |

64-bit only. There is no native code to build: it is P/Invoke into libc.

## Backends

```csharp
FastDirectory.Enumerate(path, NativeBackend.Readdir);
FastDirectory.EnumerateBatchBuffers(path, 1000, NativeBackend.Getdents64);
```

| `NativeBackend` | |
|---|---|
| `Auto` (default) | best available for the platform |
| `Readdir` | libc `opendir` / `readdir` / `closedir` (Linux and macOS) |
| `Getdents64` | raw `getdents64` syscall (Linux x64/arm64 only) |

Requesting an unavailable backend throws `PlatformNotSupportedException`; on Windows any explicit backend does.

## Linux across distributions

- libc is located at run time (`libc.so.6` for glibc, `libc.musl-*.so.1` and `libc.so` for musl, `libSystem` on macOS),
  because a bare `"libc"` import does not resolve reliably everywhere.
- The `linux_dirent64` layout is fixed by the kernel ABI, so it is the same on glibc and musl.
- Syscall numbers are per architecture (`getdents64`: 217 on x64, 61 on arm64; `statx`: 332 and 291). Other
  architectures use `readdir`.
- If `statx` is blocked (old kernel, restrictive seccomp profile), stat falls back to `System.IO` automatically.

Tested in CI on Ubuntu x64 and arm64, macOS and Windows, and manually on Debian and Alpine (arm64).

## Windows

Windows uses plain `System.IO`. Speed is not a goal there, and it keeps the native surface small.
