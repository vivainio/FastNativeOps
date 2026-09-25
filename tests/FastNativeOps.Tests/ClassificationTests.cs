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

public class ClassificationTests(LinkDirFixture fx) : IClassFixture<LinkDirFixture>
{
    private static string[] Sorted(IEnumerable<string> xs) => xs.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // No relative-path form: WalkTests changes the process cwd while test classes run in parallel.
    public static IEnumerable<object[]> PathForms() => [["abs"], ["trailing-slash"]];

    private string Form(string form) => form == "trailing-slash" ? fx.Path + System.IO.Path.DirectorySeparatorChar : fx.Path;

    [Theory, MemberData(nameof(PathForms))]
    public void EnumerateFiles_MatchesSystemIO(string form)
    {
        var p = Form(form);
        Assert.Equal(Sorted(Directory.GetFiles(p)), Sorted(FastDirectory.EnumerateFiles(p)));
    }

    [Theory, MemberData(nameof(PathForms))]
    public void EnumerateDirectories_MatchesSystemIO(string form)
    {
        var p = Form(form);
        Assert.Equal(Sorted(Directory.GetDirectories(p)), Sorted(FastDirectory.EnumerateDirectories(p)));
    }

    [Fact]
    public void SymlinkToDirectory_IsADirectory()
    {
        // Pin the semantics explicitly, not only "same as System.IO".
        var dirs = FastDirectory.EnumerateDirectories(fx.Path).Select(System.IO.Path.GetFileName);
        var files = FastDirectory.EnumerateFiles(fx.Path).Select(System.IO.Path.GetFileName);
        Assert.Equal(["linkdir", "linklinkdir", "realdir"], Sorted(dirs!));
        string[] expectedFiles = OperatingSystem.IsWindows()
            ? ["dangling", "linkfile", "realfile"]
            : ["dangling", "fifo", "linkfile", "realfile"];
        Assert.Equal(expectedFiles, Sorted(files!));
    }

    // Entries whose d_type was DT_UNKNOWN and whose probe failed arrive as Unknown; they are resolved by stat.
    [Theory]
    [InlineData("realfile", false)]
    [InlineData("realdir", true)]
    [InlineData("linkfile", false)]
    [InlineData("linkdir", true)]
    [InlineData("dangling", false)]
    [InlineData("vanished", false)]
    public void IsDirectoryLike_ResolvesUnknownAndLinks(string name, bool expected)
    {
        Assert.Equal(expected, FastDirectory.IsDirectoryLike(fx.Path, new FileEntry(name, EntryType.Unknown)));
        if (name.StartsWith("link") || name == "dangling")
            Assert.Equal(expected, FastDirectory.IsDirectoryLike(fx.Path, new FileEntry(name, EntryType.SymbolicLink)));
    }

    [Fact]
    public void MissingDirectory_Throws()
    {
        var missing = fx.P("nope");
        Assert.ThrowsAny<IOException>(() => FastDirectory.EnumerateFiles(missing).ToList());
        Assert.ThrowsAny<IOException>(() => FastDirectory.EnumerateDirectories(missing).ToList());
    }
}
