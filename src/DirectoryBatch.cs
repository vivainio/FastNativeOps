using System.Text;

namespace FastNativeOps;

/// <summary>
/// A reusable batch of directory entries with names stored as UTF-8 in one buffer.
/// The same instance is refilled on every iteration: do not keep it, or spans from it,
/// past the next MoveNext. Use <see cref="ToArray"/> to copy out.
/// </summary>
public sealed class DirectoryBatch
{
    private byte[] _names = new byte[4096];
    private readonly int[] _starts;      // name i occupies _starts[i].._starts[i+1]-1 (each followed by a NUL byte)
    private readonly EntryType[] _types;
    private readonly long[]? _sizes;
    private readonly long[]? _mtimes;    // DateTime ticks (UTC); DateTime.MinValue.Ticks if unavailable
    private int _used;

    internal DirectoryBatch(int capacity, StatFields fields = StatFields.None)
    {
        _starts = new int[capacity + 1];
        _types = new EntryType[capacity];
        Fields = fields;
        if ((fields & StatFields.Size) != 0) _sizes = new long[capacity];
        if ((fields & StatFields.ModifiedTime) != 0) _mtimes = new long[capacity];
    }

    /// <summary>The stat fields that were requested for this batch.</summary>
    public StatFields Fields { get; }

    /// <summary>File size in bytes, or -1 if unavailable (e.g. the file vanished after listing).</summary>
    public long GetSize(int index)
    {
        if (_sizes is null) throw new InvalidOperationException("StatFields.Size was not requested.");
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _sizes[index];
    }

    /// <summary>Last modification time (UTC), or DateTime.MinValue if unavailable.</summary>
    public DateTime GetModifiedTimeUtc(int index)
    {
        if (_mtimes is null) throw new InvalidOperationException("StatFields.ModifiedTime was not requested.");
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new DateTime(_mtimes[index], DateTimeKind.Utc);
    }

    internal byte[] NamesBuffer => _names;
    internal int NameOffset(int index) => _starts[index];

    internal void SetStat(int index, long size, long mtimeTicks)
    {
        if (_sizes is not null) _sizes[index] = size;
        if (_mtimes is not null) _mtimes[index] = mtimeTicks;
    }

    public int Count { get; private set; }

    public ReadOnlySpan<byte> GetNameUtf8(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _names.AsSpan(_starts[index], _starts[index + 1] - _starts[index] - 1);
    }

    public string GetName(int index) => Encoding.UTF8.GetString(GetNameUtf8(index));

    public EntryType GetType(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _types[index];
    }

    public FileEntry this[int index] => new(GetName(index), GetType(index));

    public FileEntry[] ToArray()
    {
        var a = new FileEntry[Count];
        for (int i = 0; i < a.Length; i++) a[i] = this[i];
        return a;
    }

    internal void Clear() { Count = 0; _used = 0; }

    internal int Capacity => _types.Length;

    internal void Add(ReadOnlySpan<byte> utf8Name, EntryType type)
    {
        Reserve(utf8Name.Length + 1);
        utf8Name.CopyTo(_names.AsSpan(_used));
        Commit(utf8Name.Length, type);
    }

    internal void Add(string name, EntryType type)
    {
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        Reserve(max + 1);
        int n = Encoding.UTF8.GetBytes(name, _names.AsSpan(_used));
        Commit(n, type);
    }

    private void Reserve(int extra)
    {
        if (_used + extra > _names.Length)
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _used + extra));
    }

    private void Commit(int length, EntryType type)
    {
        _starts[Count] = _used;
        _types[Count] = type;
        _names[_used + length] = 0;
        _used += length + 1;
        Count++;
        _starts[Count] = _used;
    }
}
