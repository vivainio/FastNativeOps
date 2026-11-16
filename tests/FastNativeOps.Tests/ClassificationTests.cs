using FastNativeOps;

namespace FastNativeOps.Tests;

public sealed class LinkDirFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fno-links-" + Guid.NewGuid().ToString("N"));

    public LinkDirFixture()
    {
        Directory.CreateDirectory(Path);
        File.WriteAllText(P("realfile"), "x");
        Directory.CreateDirectory(P("realdir"));
        File.CreateSymbolicLink(P("linkfile"), P("realfile"));
        Directory.CreateSymbolicLink(P("linkdir"), P("realdir"));
        Directory.CreateSymbolicLink(P("linklinkdir"), P("linkdir"));   // chain ending in a directory
        File.CreateSymbolicLink(P("dangling"), P("nope"));
        if (!OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start("mkfifo", P("fifo")).WaitForExit();
    }

    public string P(string name) => System.IO.Path.Join(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

[ClassDataSource<LinkDirFixture>(Shared = SharedType.PerClass)]
public class ClassificationTests(LinkDirFixture fx)
{
    private static string[] Sorted(IEnumerable<string> xs) => xs.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // No relative-path form: WalkTests changes the process cwd while test classes run in parallel.
    public static IEnumerable<string> PathForms() => ["abs", "trailing-slash"];

    private string Form(string form) => form == "trailing-slash" ? fx.Path + System.IO.Path.DirectorySeparatorChar : fx.Path;

    [Test, MethodDataSource(nameof(PathForms))]
    public async Task EnumerateFiles_MatchesSystemIO(string form)
    {
        var p = Form(form);
        await Assert.That(Sorted(FastDirectory.EnumerateFiles(p))).IsEquivalentTo(Sorted(Directory.GetFiles(p)), CollectionOrdering.Matching);
    }

    [Test, MethodDataSource(nameof(PathForms))]
    public async Task EnumerateDirectories_MatchesSystemIO(string form)
    {
        var p = Form(form);
        await Assert.That(Sorted(FastDirectory.EnumerateDirectories(p))).IsEquivalentTo(Sorted(Directory.GetDirectories(p)), CollectionOrdering.Matching);
    }

    [Test]
    public async Task SymlinkToDirectory_IsADirectory()
    {
        // Pin the semantics explicitly, not only "same as System.IO".
        var dirs = FastDirectory.EnumerateDirectories(fx.Path).Select(System.IO.Path.GetFileName);
        var files = FastDirectory.EnumerateFiles(fx.Path).Select(System.IO.Path.GetFileName);
        await Assert.That(Sorted(dirs!)).IsEquivalentTo(["linkdir", "linklinkdir", "realdir"], CollectionOrdering.Matching);
        string[] expectedFiles = OperatingSystem.IsWindows()
            ? ["dangling", "linkfile", "realfile"]
            : ["dangling", "fifo", "linkfile", "realfile"];
        await Assert.That(Sorted(files!)).IsEquivalentTo(expectedFiles, CollectionOrdering.Matching);
    }

    // Entries whose d_type was DT_UNKNOWN and whose probe failed arrive as Unknown; they are resolved by stat.
    [Test]
    [Arguments("realfile", false)]
    [Arguments("realdir", true)]
    [Arguments("linkfile", false)]
    [Arguments("linkdir", true)]
    [Arguments("dangling", false)]
    [Arguments("vanished", false)]
    public async Task IsDirectoryLike_ResolvesUnknownAndLinks(string name, bool expected)
    {
        await Assert.That(FastDirectory.IsDirectoryLike(fx.Path, new FileEntry(name, EntryType.Unknown))).IsEqualTo(expected);
        if (name.StartsWith("link") || name == "dangling")
            await Assert.That(FastDirectory.IsDirectoryLike(fx.Path, new FileEntry(name, EntryType.SymbolicLink))).IsEqualTo(expected);
    }

    [Test]
    public async Task MissingDirectory_Throws()
    {
        var missing = fx.P("nope");
        await Assert.That(() => FastDirectory.EnumerateFiles(missing).ToList()).Throws<DirectoryNotFoundException>();
        await Assert.That(() => FastDirectory.EnumerateDirectories(missing).ToList()).Throws<DirectoryNotFoundException>();
    }
}
