# Recursive walk

```csharp
var options = new WalkOptions
{
    MaxDepth = 8,
    IgnoreInaccessible = true,
    ShouldDescend = (name, depth) => name != ".git",
    Fields = StatFields.Size | StatFields.ModifiedTime,
};

foreach (DirectoryBatch batch in FastDirectory.WalkBatchBuffers("/mnt/data", 1000, options))
    for (int i = 0; i < batch.Count; i++)
        Console.WriteLine($"{batch.DirectoryPath}/{batch.GetName(i)}  (depth {batch.Depth})");
```

Each batch holds entries of **one directory**; `DirectoryPath` and `Depth` say which. Order is unspecified. As with
[batch buffers](batch-buffers.md), the batch object is reused: copy what you keep.

## Options

| `WalkOptions` | Meaning |
|---|---|
| `MaxDepth` | levels below the root to enter (0 = only the root's own entries); default unlimited |
| `ShouldDescend(name, depth)` | return `false` to skip a subdirectory (it is still listed) |
| `EntryFilter` | which entries are reported, see [Filtering](filtering.md) |
| `IgnoreInaccessible` | skip directories that cannot be opened or read instead of throwing |
| `Fields`, `AllowCachedAttributes`, `StatParallelism` | stat options, as in [Stat fields](stat.md) |
| `Backend` | force a [backend](platforms.md) |

## Symbolic links

Symlinks are reported (`EntryType.SymbolicLink`) but **never followed**, so link loops cannot happen. Following links is
not implemented.

## How it works on Linux

Each subdirectory is opened with `openat(parentFd, name, O_DIRECTORY | O_NOFOLLOW)`:

- **No path length limit.** Trees deeper than `PATH_MAX` (4096 bytes) work; path-based APIs cannot open them.
- **One path component is resolved per open**, not the whole path.
- **Depth-first, with an explicit stack**, so deep trees cannot overflow the call stack.
- **File descriptors are bounded.** A directory's descriptor is closed as soon as its last subdirectory has been opened,
  so a plain deep chain needs a handful, and a tree that branches at every level needs about one per level of depth.
  If the process runs out (`EMFILE`), the walk throws `IOException` mentioning errno 24 and never silently skips the
  subtree, even with `IgnoreInaccessible`. Raise `ulimit -n` for extremely deep, heavily branching trees.
- A subdirectory that disappears or is replaced by a symlink between listing and opening is skipped.

On macOS and Windows the walk is emulated with path-based enumeration and has the same semantics.
