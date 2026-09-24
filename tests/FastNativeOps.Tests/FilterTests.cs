using System.Text;
using System.Text.RegularExpressions;
using FastNativeOps;

namespace FastNativeOps.Tests;

public class FilterTests(TreeFixture fx) : IClassFixture<TreeFixture>
{
    private static bool Match(EntryFilter f, string name, EntryType t = EntryType.File) => f(Encoding.UTF8.GetBytes(name), t);

    [Theory]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "a.csx", false)]
    [InlineData("*.cs", ".cs", true)]
    [InlineData("*", "", true)]
    [InlineData("*", "anything", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("a?c", "abbc", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("a*b*c", "abcbc", true)]
    [InlineData("*é*", "café.txt", true)]
    [InlineData("caf?.txt", "café.txt", true)]          // ? matches one whole code point (2 bytes)
    [InlineData("caf??.txt", "café.txt", false)]
    [InlineData("?", "é", true)]
    [InlineData("?", "日", true)]                        // 3-byte character
    [InlineData("??", "日", false)]
    [InlineData("*?", "日本", true)]
    [InlineData("file-00??.txt", "file-0042.txt", true)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    [InlineData("**a**", "bab", true)]
    public void Glob(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Match(EntryFilters.Glob(pattern), name));

    [Fact]
    public void Glob_IgnoreCase_FoldsAsciiOnly()
    {
        Assert.True(Match(EntryFilters.Glob("*.CS", ignoreCase: true), "Program.cs"));
        Assert.False(Match(EntryFilters.Glob("*.CS"), "Program.cs"));
        Assert.False(Match(EntryFilters.Glob("É*", ignoreCase: true), "é"));   // documented: ASCII only
    }

    [Fact]
    public void Extension_Regex_Type_Combinators()
    {
        Assert.True(Match(EntryFilters.Extension(".TXT", ignoreCase: true), "a.txt"));
        Assert.False(Match(EntryFilters.Extension(".txt"), "txt"));
        Assert.True(Match(EntryFilters.Regex(new Regex(@"^report-\d{4}\.csv$")), "report-2024.csv"));
        Assert.False(Match(EntryFilters.Regex(new Regex(@"^report-\d{4}\.csv$")), "report-24.csv"));
        Assert.True(Match(EntryFilters.Regex(new Regex("é")), "café"));                     // non-ASCII decoded correctly
        Assert.True(Match(EntryFilters.Regex(new Regex("x$")), new string('y', 600) + "x"));  // longer than the stack buffer
        Assert.True(Match(EntryFilters.OfType(EntryType.Directory), "d", EntryType.Directory));
        Assert.False(Match(EntryFilters.OfType(EntryType.Directory), "d", EntryType.File));
        var txt = EntryFilters.Extension(".txt");
        Assert.False(Match(EntryFilters.Not(txt), "a.txt"));
        Assert.True(Match(EntryFilters.And(txt, EntryFilters.Glob("a*")), "ab.txt"));
        Assert.False(Match(EntryFilters.And(txt, EntryFilters.Glob("a*")), "b.txt"));
        Assert.True(Match(EntryFilters.Or(EntryFilters.Extension(".cs"), txt), "x.txt"));
    }

    public static IEnumerable<object[]> Backends() => DirectoryTests.Backends();

    private List<string> Walk(EntryFilter? filter, NativeBackend backend, int bs = 16, Action<WalkOptions>? tweak = null)
    {
        var o = new WalkOptions { EntryFilter = filter, Backend = backend };
        tweak?.Invoke(o);
        var list = new List<string>();
        foreach (var b in FastDirectory.WalkBatchBuffers(fx.Root, bs, o))
            for (int i = 0; i < b.Count; i++)
                list.Add(Path.GetRelativePath(fx.Root, Path.Join(b.DirectoryPath, b.GetName(i))));
        return list.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private IEnumerable<string> Reference(Func<string, bool> pred) =>
        Directory.EnumerateFileSystemEntries(fx.Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(p => Path.GetRelativePath(fx.Root, p)).Where(pred).OrderBy(x => x, StringComparer.Ordinal);

    [Theory, MemberData(nameof(Backends))]
    public void Walk_Regex_MatchesReference_AndDescendsIntoHiddenDirectories(NativeBackend backend)
    {
        var rx = new Regex(@"^\d\.txt$");                      // matches a/1.txt, a/x/4.txt, d/é-ünï/5.txt, ...
        var got = Walk(EntryFilters.Regex(rx), backend);
        Assert.Equal(Reference(p => rx.IsMatch(Path.GetFileName(p))), got);
        Assert.Contains(Path.Join(".hidden", "deep", "6.txt"), got);
        Assert.DoesNotContain("a", got);                        // directories that do not match are not reported...
        Assert.Contains(Path.Join("a", "b", "2.txt"), got);       // ...but were still walked
    }

    [Theory, MemberData(nameof(Backends))]
    public void Walk_Glob_And_Extension_MatchReference(NativeBackend backend)
    {
        Assert.Equal(Reference(p => Path.GetFileName(p).StartsWith("top-0")).ToList(),
                     Walk(EntryFilters.Glob("top-0*"), backend, bs: 3));
        Assert.Equal(Reference(p => p.EndsWith(".txt", StringComparison.Ordinal)).ToList(),
                     Walk(EntryFilters.Extension(".txt"), backend, bs: 1000));
    }

    [Theory, MemberData(nameof(Backends))]
    public void Walk_FilterRejectingEverything_YieldsNothing_WithoutStoppingTheWalk(NativeBackend backend)
    {
        int visitedDirs = 0;
        var got = Walk((_, t) => false, backend, tweak: o => o.ShouldDescend = (_, _) => { visitedDirs++; return true; });
        Assert.Empty(got);
        Assert.True(visitedDirs >= 8, $"walk stopped early: only {visitedDirs} directories visited");
    }

    [Theory, MemberData(nameof(Backends))]
    public void Walk_FilterCombinedWithMaxDepthAndStat(NativeBackend backend)
    {
        var o = new WalkOptions { EntryFilter = EntryFilters.Extension(".txt"), MaxDepth = 1, Fields = StatFields.Size, Backend = backend };
        int n = 0;
        foreach (var b in FastDirectory.WalkBatchBuffers(fx.Root, 25, o))
        {
            Assert.InRange(b.Depth, 0, 1);
            for (int i = 0; i < b.Count; i++)
            {
                Assert.EndsWith(".txt", b.GetName(i));
                Assert.True(b.GetSize(i) >= 0);
                n++;
            }
        }
        Assert.Equal(40 + 1, n);          // 40 top-level files + a/1.txt
    }

    [Fact]
    public void Walk_FilterReceivesTypes()
    {
        var types = new HashSet<EntryType>();
        Walk((_, t) => { lock (types) types.Add(t); return true; }, NativeBackend.Auto);
        Assert.Contains(EntryType.File, types);
        Assert.Contains(EntryType.Directory, types);
    }
}
