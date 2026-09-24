# FastNativeOps

Fast cross-platform file system access for .NET using direct P/Invoke.

- Linux / macOS: `opendir` / `readdir` / `closedir`
- Windows: `FindFirstFileExW` (basic info, large fetch)

```csharp
using FastNativeOps;

foreach (var e in FastDirectory.Enumerate("/usr"))
    Console.WriteLine($"{e.Type} {e.Name}");
```

For big directories, get entries in batches:

```csharp
foreach (FileEntry[] batch in FastDirectory.EnumerateBatches(path, 1000)) { /* ... */ }
```

Backends: `NativeBackend.Auto` (default) uses raw `getdents64` on Linux x64/arm64, `readdir` on other Unix, and `FindFirstFileExW` on Windows. Pass `NativeBackend.Readdir` or `Getdents64` to `Enumerate` / `EnumerateBatches` to force one.

Status: directory listing only. 64-bit only. Tested on macOS arm64 (arm64).
