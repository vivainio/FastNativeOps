# Filtering

```csharp
var options = new WalkOptions
{
    Fields = StatFields.Size,
    EntryFilter = EntryFilters.Regex(new Regex(@"^report-\d{4}\.csv$")),
};
```

An `EntryFilter` is `bool (ReadOnlySpan<byte> nameUtf8, EntryType type)`. It receives the UTF-8 name, so a custom filter
allocates nothing:

```csharp
options.EntryFilter = (name, type) => type == EntryType.File && name.EndsWith(".log"u8);
```

## Why a filter instead of an `if` in your loop

The filter runs **before the stat pass**. Entries that do not match never cost a `statx` call, which is where the time
goes on NFS. See the [benchmark](../benchmarks.md#filtered-walk-with-stat).

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
