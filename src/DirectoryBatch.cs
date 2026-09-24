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
    private readonly int[] _starts;      // _starts[i].._starts[i+1] is name i; length capacity + 1
    private readonly EntryType[] _types;
    private int _used;

    internal DirectoryBatch(int capacity)
    {
        _starts = new int[capacity + 1];
        _types = new EntryType[capacity];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<byte> GetNameUtf8(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _names.AsSpan(_starts[index], _starts[index + 1] - _starts[index]);
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
        Reserve(utf8Name.Length);
        utf8Name.CopyTo(_names.AsSpan(_used));
        Commit(utf8Name.Length, type);
    }

    internal void Add(string name, EntryType type)
    {
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        Reserve(max);
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
        _used += length;
        Count++;
        _starts[Count] = _used;
    }
}
