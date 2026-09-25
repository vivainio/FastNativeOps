# Batch buffers

`EnumerateBatchBuffers` yields one **reused** `DirectoryBatch`. Names are stored as UTF-8 in a single buffer, so there is
no string per entry and no array per batch.

```csharp
foreach (DirectoryBatch batch in FastDirectory.EnumerateBatchBuffers(path, 1000))
    for (int i = 0; i < batch.Count; i++)
    {
        ReadOnlySpan<byte> name = batch.GetNameUtf8(i);          // no allocation
        if (batch.GetType(i) == EntryType.File && name.EndsWith(".cs"u8))
            Process(batch.GetName(i));                            // allocate a string only when you need one
    }
```

| Member | |
|---|---|
| `Count` | entries in this batch |
| `GetNameUtf8(i)` | name as a UTF-8 span |
| `GetName(i)` | name as a string (allocates) |
| `GetType(i)` | `EntryType` |
| `this[i]` / `ToArray()` | `FileEntry` / copy the batch out |
| `DirectoryPath`, `Depth` | which directory the entries belong to (see [walk](walk.md)) |
| `GetSize(i)`, `GetModifiedTimeUtc(i)` | only if requested, see [Size and modification time](stat.md) |

## Filtering before stat

Pass `filter:` to report only matching entries. The filter runs before the stat pass, so entries it rejects cost no
stat call, which matters on NFS. See [Filtering](filtering.md).

```csharp
foreach (var batch in FastDirectory.EnumerateBatchBuffers(path, 1000,
             fields: StatFields.Size | StatFields.ModifiedTime,
             filter: EntryFilters.And(EntryFilters.OfType(EntryType.File), EntryFilters.Extension(".log"))))
    for (int i = 0; i < batch.Count; i++)
        Console.WriteLine($"{batch.GetName(i)} {batch.GetSize(i)}");   // only .log files were stat'ed
```

!!! warning "Do not keep the batch"
    The same object, and the spans you got from it, are overwritten on the next iteration. Copy what you need
    (`GetName`, `ToArray`) before moving on.

## How much does it save?

On Linux `getdents64` hands us packed UTF-8 names in a 64 KB buffer; the batch copies them without decoding. On macOS
and Windows the API is identical but emulated by copying each name into the same buffer, so the allocation savings are
smaller. See [Benchmarks](../benchmarks.md).
