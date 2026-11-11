using System.Text;
using System.Text.RegularExpressions;
using FastNativeOps;

namespace FastNativeOps.Tests;

[ClassDataSource<TreeFixture>(Shared = SharedType.PerClass)]
public class FilterTests(TreeFixture fx)
{
    private static bool Match(EntryFilter f, string name, EntryType t = EntryType.File) => f(Encoding.UTF8.GetBytes(name), t);
    private static bool Match(NameFilter f, string name, EntryType t = EntryType.File) => f(Encoding.UTF8.GetBytes(name));

    [Test]
    [Arguments("*.cs", "a.cs", true)]
    [Arguments("*.cs", "a.csx", false)]
    [Arguments("*.cs", ".cs", true)]
    [Arguments("*", "", true)]
    [Arguments("*", "anything", true)]
    [Arguments("a?c", "abc", true)]
    [Arguments("a?c", "ac", false)]
    [Arguments("a?c", "abbc", false)]
    [Arguments("a*b*c", "aXXbYYc", true)]
    [Arguments("a*b*c", "aXXbYY", false)]
    [Arguments("a*b*c", "abcbc", true)]
    [Arguments("*é*", "café.txt", true)]
    [Arguments("caf?.txt", "café.txt", true)]          // ? matches one whole code point (2 bytes)
    [Arguments("caf??.txt", "café.txt", false)]
    [Arguments("?", "é", true)]
    [Arguments("?", "日", true)]                        // 3-byte character
    [Arguments("??", "日", false)]
    [Arguments("*?", "日本", true)]
    [Arguments("file-00??.txt", "file-0042.txt", true)]
    [Arguments("exact", "exact", true)]
    [Arguments("exact", "exactly", false)]
    [Arguments("", "", true)]
    [Arguments("", "x", false)]
    [Arguments("**a**", "bab", true)]
    public async Task Glob(string pattern, string name, bool expected) =>
        await Assert.That(Match(EntryFilters.Glob(pattern), name)).IsEqualTo(expected);

    [Test]
    public async Task Glob_IgnoreCase_FoldsAsciiOnly()
    {
        await Assert.That(Match(EntryFilters.Glob("*.CS", ignoreCase: true), "Program.cs")).IsTrue();
        await Assert.That(Match(EntryFilters.Glob("*.CS"), "Program.cs")).IsFalse();
        await Assert.That(Match(EntryFilters.Glob("É*", ignoreCase: true), "é")).IsFalse();   // documented: ASCII only
    }

    [Test]
    public async Task Extension_Regex_Type_Combinators()
    {
        await Assert.That(Match(EntryFilters.Extension(".TXT", ignoreCase: true), "a.txt")).IsTrue();
        await Assert.That(Match(EntryFilters.Extension(".txt"), "txt")).IsFalse();
        await Assert.That(Match(EntryFilters.Regex(new Regex(@"^report-\d{4}\.csv$")), "report-2024.csv")).IsTrue();
        await Assert.That(Match(EntryFilters.Regex(new Regex(@"^report-\d{4}\.csv$")), "report-24.csv")).IsFalse();
        await Assert.That(Match(EntryFilters.Regex(new Regex("é")), "café")).IsTrue();                     // non-ASCII decoded correctly
        await Assert.That(Match(EntryFilters.Regex(new Regex("x$")), new string('y', 600) + "x")).IsTrue();  // longer than the stack buffer
        await Assert.That(Match(EntryFilters.OfType(EntryType.Directory), "d", EntryType.Directory)).IsTrue();
        await Assert.That(Match(EntryFilters.OfType(EntryType.Directory), "d", EntryType.File)).IsFalse();
        var txt = EntryFilters.Extension(".txt");
        await Assert.That(Match(EntryFilters.Not(txt), "a.txt")).IsFalse();
        await Assert.That(Match(EntryFilters.And(txt, EntryFilters.Glob("a*")), "ab.txt")).IsTrue();
        await Assert.That(Match(EntryFilters.And(txt, EntryFilters.Glob("a*")), "b.txt")).IsFalse();
        await Assert.That(Match(EntryFilters.Or(EntryFilters.Extension(".cs"), txt), "x.txt")).IsTrue();
    }

    [Test]
    public async Task OfType_AcceptsCombinations()
    {
        EntryType[] all = [EntryType.Unknown, EntryType.File, EntryType.Directory, EntryType.SymbolicLink, EntryType.Other];
        var fileOrLink = EntryFilters.OfType(EntryType.File | EntryType.SymbolicLink);
        var notDir = EntryFilters.OfType(EntryType.NotDirectory);
        foreach (var t in all)
        {
            await Assert.That(Match(fileOrLink, "x", t)).IsEqualTo(t is EntryType.File or EntryType.SymbolicLink);
            await Assert.That(Match(notDir, "x", t)).IsEqualTo(t != EntryType.Directory);
            await Assert.That(Match(notDir, "x", t)).IsEqualTo(Match(EntryFilters.Not(EntryFilters.OfType(EntryType.Directory)), "x", t));
        }
    }

    public static IEnumerable<NativeBackend> Backends() => DirectoryTests.Backends();

    private List<string> Walk(NameFilter nameFilter, NativeBackend backend, int bs = 16, Action<WalkOptions>? tweak = null) =>
        Walk(null, backend, bs, tweak, nameFilter);

    private List<string> Walk(EntryFilter? filter, NativeBackend backend, int bs = 16, Action<WalkOptions>? tweak = null, NameFilter? nameFilter = null)
    {
        var o = new WalkOptions { EntryFilter = filter, NameFilter = nameFilter, Backend = backend };
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

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Walk_Regex_MatchesReference_AndDescendsIntoHiddenDirectories(NativeBackend backend)
    {
        var rx = new Regex(@"^\d\.txt$");                      // matches a/1.txt, a/x/4.txt, d/é-ünï/5.txt, ...
        var got = Walk(EntryFilters.Regex(rx), backend);
        await Assert.That(got).IsEquivalentTo(Reference(p => rx.IsMatch(Path.GetFileName(p))), CollectionOrdering.Matching);
        await Assert.That(got).Contains(Path.Join(".hidden", "deep", "6.txt"));
        await Assert.That(got).DoesNotContain("a");                        // directories that do not match are not reported...
        await Assert.That(got).Contains(Path.Join("a", "b", "2.txt"));       // ...but were still walked
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Walk_Glob_And_Extension_MatchReference(NativeBackend backend)
    {
        await Assert.That(Walk(EntryFilters.Glob("top-0*"), backend, bs: 3)).IsEquivalentTo(Reference(p => Path.GetFileName(p).StartsWith("top-0")).ToList(), CollectionOrdering.Matching);
        await Assert.That(Walk(EntryFilters.Extension(".txt"), backend, bs: 1000)).IsEquivalentTo(Reference(p => p.EndsWith(".txt", StringComparison.Ordinal)).ToList(), CollectionOrdering.Matching);
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Walk_FilterRejectingEverything_YieldsNothing_WithoutStoppingTheWalk(NativeBackend backend)
    {
        int visitedDirs = 0;
        var got = Walk((_, t) => false, backend, tweak: o => o.ShouldDescend = (_, _) => { visitedDirs++; return true; });
        await Assert.That(got).IsEmpty();
        await Assert.That(visitedDirs >= 8).IsTrue().Because($"walk stopped early: only {visitedDirs} directories visited");
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Walk_FilterCombinedWithMaxDepthAndStat(NativeBackend backend)
    {
        var o = new WalkOptions { NameFilter = EntryFilters.Extension(".txt"), MaxDepth = 1, Fields = StatFields.Size, Backend = backend };
        int n = 0;
        foreach (var b in FastDirectory.WalkBatchBuffers(fx.Root, 25, o))
        {
            await Assert.That(b.Depth).IsBetween(0, 1);
            for (int i = 0; i < b.Count; i++)
            {
                await Assert.That(b.GetName(i)).EndsWith(".txt");
                await Assert.That(b.GetSize(i) >= 0).IsTrue();
                n++;
            }
        }
        await Assert.That(n).IsEqualTo(40 + 1);          // 40 top-level files + a/1.txt
    }

    [Test]
    public async Task Walk_FilterReceivesTypes()
    {
        var types = new HashSet<EntryType>();
        Walk((_, t) => { lock (types) types.Add(t); return true; }, NativeBackend.Auto);
        await Assert.That(types).Contains(EntryType.File);
        await Assert.That(types).Contains(EntryType.Directory);
    }
}
