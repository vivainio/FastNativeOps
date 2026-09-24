# FastNativeOps

Fast cross-platform file system access for .NET using direct P/Invoke.

- Linux / macOS: `opendir` / `readdir` / `closedir` (glibc and musl)
- Windows: `FindFirstFileExW` (basic info, large fetch)

```csharp
using FastNativeOps;

foreach (var e in FastDirectory.Enumerate("/usr"))
    Console.WriteLine($"{e.Type} {e.Name}");
```

Status: directory listing only. 64-bit only. Tested on macOS arm64, Debian and Alpine (arm64).
