using System.Text;
using System.Text.RegularExpressions;

namespace FastNativeOps;

/// <summary>Decides whether an entry is reported. Receives the UTF-8 name, so filters need not allocate strings.</summary>
public delegate bool EntryFilter(ReadOnlySpan<byte> nameUtf8, EntryType type);

/// <summary>Ready-made <see cref="EntryFilter"/>s. Case-insensitive matching folds ASCII letters only.</summary>
public static class EntryFilters
{
    /// <summary>Shell-style glob on the entry name: <c>*</c> = any sequence, <c>?</c> = any single character.</summary>
    public static EntryFilter Glob(string pattern, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var pat = Encoding.UTF8.GetBytes(pattern);
        return (name, _) => GlobMatch(pat, name, ignoreCase);
    }

    /// <summary>Names ending with the given text, e.g. ".cs".</summary>
    public static EntryFilter Extension(string extension, bool ignoreCase = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        var ext = Encoding.UTF8.GetBytes(extension);
        return (name, _) => name.Length >= ext.Length && SpanEquals(name[^ext.Length..], ext, ignoreCase);
    }

    /// <summary>Names matching a regular expression (<see cref="Regex.IsMatch(ReadOnlySpan{char})"/>; no string is allocated for names up to 512 bytes).</summary>
    public static EntryFilter Regex(Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);
        return (name, _) =>
        {
            Span<char> chars = name.Length <= 512 ? stackalloc char[512] : new char[name.Length];
            int n = Encoding.UTF8.GetChars(name, chars);
            return regex.IsMatch(chars[..n]);
        };
    }

    /// <summary>Only entries of the given type.</summary>
    public static EntryFilter OfType(EntryType type) => (_, t) => t == type;

    public static EntryFilter Not(EntryFilter f) => (n, t) => !f(n, t);
    public static EntryFilter And(params EntryFilter[] fs) => (n, t) => { foreach (var f in fs) if (!f(n, t)) return false; return true; };
    public static EntryFilter Or(params EntryFilter[] fs) => (n, t) => { foreach (var f in fs) if (f(n, t)) return true; return false; };

    private static byte Fold(byte b, bool ic) => ic && b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static bool SpanEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, bool ic)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (Fold(a[i], ic) != Fold(b[i], ic)) return false;
        return true;
    }

    // Length of the UTF-8 sequence starting with this lead byte, clamped to what is left.
    private static int CpLen(byte b, int left) => Math.Min(left, b < 0x80 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4);

    // Iterative wildcard match with single-star backtracking; '?' and star backtracking step by whole code points.
    private static bool GlobMatch(ReadOnlySpan<byte> pat, ReadOnlySpan<byte> s, bool ic)
    {
        int p = 0, i = 0, star = -1, mark = 0;
        while (i < s.Length)
        {
            if (p < pat.Length && pat[p] == (byte)'*') { star = p++; mark = i; }
            else if (p < pat.Length && pat[p] == (byte)'?') { p++; i += CpLen(s[i], s.Length - i); }
            else if (p < pat.Length && Fold(pat[p], ic) == Fold(s[i], ic)) { p++; i++; }
            else if (star >= 0) { p = star + 1; mark += CpLen(s[mark], s.Length - mark); i = mark; }
            else return false;
        }
        while (p < pat.Length && pat[p] == (byte)'*') p++;
        return p == pat.Length;
    }
}
