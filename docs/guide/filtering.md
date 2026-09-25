# Filtering

!!! info "Where filters apply"
    A filter can be passed in two places:

    - **`WalkBatchBuffers`**, through `WalkOptions.EntryFilter`, for a recursive walk
    - **`EnumerateBatchBuffers`**, through the `filter:` parameter, for a single directory

    `Enumerate`, `List` and `EnumerateBatches` do not take a filter. They already give you string names, so a LINQ
    `Where` is just as cheap there.

```csharp
// recursive walk
var options = new WalkOptions
{
    Fields = StatFields.Size,
    EntryFilter = EntryFilters.Regex(new Regex(@"^report-\d{4}\.csv$")),
};

// one directory
foreach (var batch in FastDirectory.EnumerateBatchBuffers(path, 1000, fields: StatFields.Size,
                                                          filter: EntryFilters.Glob("*.csv")))
{ /* ... */ }
```

An `EntryFilter` is `bool (ReadOnlySpan<byte> nameUtf8, EntryType type)`. It receives the UTF-8 name, so a custom filter
allocates nothing:

```csharp
options.EntryFilter = (name, type) => type == EntryType.File && name.EndsWith(".log"u8);
```

## Why a filter instead of an `if` in your loop

The filter runs **before the stat pass**. Entries that do not match never cost a `statx` call, which is where the time
goes on NFS. See the [benchmark](../benchmarks.md#filtered-walk-with-stat).

## Filtering by type: symbolic links

Filters see the type straight from the directory entry, and links are **not** followed. `OfType(EntryType.File)` drops
every symlink, and `OfType(EntryType.Directory)` misses symlinks to directories. If you need the `System.IO` split
(where a link to a directory counts as a directory), use
[`EnumerateFiles` / `EnumerateDirectories`](listing.md#drop-in-for-directorygetfiles-getdirectories).

## It decides what is reported, not where the walk goes

Subdirectories are entered whether or not the filter reports them: a `*.cs` filter hides every directory entry (they do
not match) but still finds `src/a/b/c.cs`. To prune, use `ShouldDescend`.

## Built-in filters

| | |
|---|---|
| `EntryFilters.Glob("*.cs")` | `*` any sequence, `?` one whole character (code point) |
| `EntryFilters.Extension(".cs")` | names ending with the text |
| `EntryFilters.Regex(regex)` | `Regex.IsMatch` on the name; decodes into a stack buffer, no string per entry |
| `EntryFilters.OfType(EntryType.File)` | only that type |
| `Not(f)`, `And(f, g, ...)`, `Or(f, g, ...)` | combinators |

`Glob` and `Extension` take `ignoreCase: true`, which folds ASCII letters only. Filters match the entry **name**, not the
path.

!!! tip "Combining"
    `EntryFilters.And(EntryFilters.OfType(EntryType.File), EntryFilters.Extension(".csv"))` reports only CSV files, so
    directories are no longer reported, but they are still walked.
