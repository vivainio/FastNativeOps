using System.Runtime.InteropServices;
using FastNativeOps;

namespace FastNativeOps.Tests;

public sealed class TreeFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fno-walk-" + Guid.NewGuid().ToString("N"));

    public TreeFixture()
    {
        Directory.CreateDirectory(Root);
        for (int i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(Root, $"top-{i:D2}.txt"), new byte[i]);
        foreach (var d in new[] { "a/b/c", "a/x", "d/é-ünï", ".hidden/deep", "empty" })
            Directory.CreateDirectory(Path.Combine(Root, d));
        foreach (var f in new[] { "a/1.txt", "a/b/2.txt", "a/b/c/3.txt", "a/x/4.txt", "d/é-ünï/5.txt", ".hidden/deep/6.txt" })
            File.WriteAllText(Path.Combine(Root, f), f);
        for (int i = 0; i < 100; i++) File.WriteAllText(Path.Combine(Root, "a/b", $"many-{i:D3}"), "x");
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class WalkTests(TreeFixture fx) : IClassFixture<TreeFixture>
{
    public static IEnumerable<object[]> Backends() => DirectoryTests.Backends();

    private static List<string> Walk(string root, int bs, WalkOptions? o = null)
    {
        var list = new List<string>();
        foreach (var b in FastDirectory.WalkBatchBuffers(root, bs, o))
            for (int i = 0; i < b.Count; i++)
                list.Add(Path.GetRelativePath(root, Path.Join(b.DirectoryPath, b.GetName(i))));
        return list;
    }

    private static string[] Sorted(IEnumerable<string> x) => x.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    private string[] Reference() =>
        Sorted(Directory.EnumerateFileSystemEntries(fx.Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(p => Path.GetRelativePath(fx.Root, p)));

    [Theory, MemberData(nameof(Backends))]
    public void Walk_MatchesSystemIO(NativeBackend backend)
    {
        foreach (int bs in new[] { 1, 7, 64, 100_000 })
            Assert.Equal(Reference(), Sorted(Walk(fx.Root, bs, new WalkOptions { Backend = backend })));
    }

    [Fact]
    public void Walk_BatchesNeverSpanDirectories_AndReportDepth()
    {
        foreach (var b in FastDirectory.WalkBatchBuffers(fx.Root, 16))
        {
            int expectedDepth = b.DirectoryPath == fx.Root ? 0 : Path.GetRelativePath(fx.Root, b.DirectoryPath).Split(Path.DirectorySeparatorChar).Length;
            Assert.Equal(expectedDepth, b.Depth);
            for (int i = 0; i < b.Count; i++)
                Assert.Equal(b.DirectoryPath, Path.GetDirectoryName(Path.Join(b.DirectoryPath, b.GetName(i))));
        }
    }

    [Fact]
    public void Walk_MaxDepth()
    {
        Assert.Equal(Sorted(Directory.GetFileSystemEntries(fx.Root).Select(p => Path.GetRelativePath(fx.Root, p))),
                     Sorted(Walk(fx.Root, 50, new WalkOptions { MaxDepth = 0 })));
        var d1 = Walk(fx.Root, 50, new WalkOptions { MaxDepth = 1 });
        Assert.Contains(Path.Join("a", "1.txt"), d1);
        Assert.DoesNotContain(Path.Join("a", "b", "2.txt"), d1);
    }

    [Fact]
    public void Walk_ShouldDescend_Prunes()
    {
        var got = Walk(fx.Root, 50, new WalkOptions { ShouldDescend = (name, depth) => name != "b" });
        Assert.Contains(Path.Join("a", "b"), got);                       // the directory itself is listed
        Assert.DoesNotContain(Path.Join("a", "b", "2.txt"), got);        // but not entered
        Assert.Contains(Path.Join("a", "x", "4.txt"), got);
    }

    [Theory, MemberData(nameof(Backends))]
    public void Walk_WithStat_ReportsSizes(NativeBackend backend)
    {
        int checkedFiles = 0;
        var o = new WalkOptions { Fields = StatFields.Size, Backend = backend, StatParallelism = 4 };
        foreach (var b in FastDirectory.WalkBatchBuffers(fx.Root, 20, o))
            for (int i = 0; i < b.Count; i++)
            {
                var name = b.GetName(i);
                if (b.Depth == 0 && name.StartsWith("top-"))
                {
                    Assert.Equal(int.Parse(name.AsSpan(4, 2)), b.GetSize(i));
                    checkedFiles++;
                }
            }
        Assert.Equal(40, checkedFiles);
    }

    [Fact]
    public void Walk_MissingRoot_Throws() =>
        Assert.ThrowsAny<IOException>(() => FastDirectory.WalkBatchBuffers(Path.Join(fx.Root, "nope"), 10).ToList());

    [Fact]
    public void Walk_SymlinkLoop_IsReportedNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "fno-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "a", "f.txt"), "x");
            Directory.CreateSymbolicLink(Path.Combine(dir, "a", "loop"), dir);   // points back at the root
            Directory.CreateSymbolicLink(Path.Combine(dir, "link-to-a"), Path.Combine(dir, "a"));
            var types = new Dictionary<string, EntryType>();
            foreach (var b in FastDirectory.WalkBatchBuffers(dir, 10))
                for (int i = 0; i < b.Count; i++)
                    types[Path.GetRelativePath(dir, Path.Join(b.DirectoryPath, b.GetName(i)))] = b.GetType(i);
            Assert.Equal(EntryType.SymbolicLink, types[Path.Join("a", "loop")]);
            Assert.Equal(EntryType.SymbolicLink, types["link-to-a"]);
            Assert.Equal(4, types.Count);            // a, a/f.txt, a/loop, link-to-a
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Walk_Inaccessible_DirectoryIsSkippedOrThrows()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "fno-perm-" + Guid.NewGuid().ToString("N"));
        var locked = Path.Combine(dir, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(dir, "ok.txt"), "x");
        File.WriteAllText(Path.Combine(locked, "secret.txt"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            try { Directory.GetFileSystemEntries(locked); return; } catch (UnauthorizedAccessException) { }   // running as root: nothing to test

            Assert.ThrowsAny<IOException>(() => Walk(dir, 10));
            var got = Walk(dir, 10, new WalkOptions { IgnoreInaccessible = true });
            Assert.Equal(new[] { "locked", "ok.txt" }, Sorted(got));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Walk_TreeDeeperThanPathMax_OnLinux()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return;
        var dir = Path.Combine(Path.GetTempPath(), "fno-deep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const int Levels = 2100;                               // 2100 * 2 bytes ("d/") > PATH_MAX (4096)
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;                // build with relative paths: absolute would exceed PATH_MAX
            for (int i = 0; i < Levels; i++) { Directory.CreateDirectory("d"); Environment.CurrentDirectory = "d"; }
            File.WriteAllText("leaf.txt", "x");
        }
        catch (IOException) { Environment.CurrentDirectory = saved; Directory.Delete(dir, true); return; }   // can't build it here
        finally { Environment.CurrentDirectory = saved; }

        try
        {
            int dirs = 0, leaf = 0, maxDepth = 0;
            try
            {
                foreach (var b in FastDirectory.WalkBatchBuffers(dir, 10))
                {
                    maxDepth = Math.Max(maxDepth, b.Depth);
                    for (int i = 0; i < b.Count; i++) { if (b.GetName(i) == "leaf.txt") leaf++; else dirs++; }
                }
            }
            catch (IOException e) when (e.Message.Contains("errno 24")) { return; }   // EMFILE: fd limit lower than depth
            Assert.Equal(Levels, dirs);
            Assert.Equal(1, leaf);
            Assert.Equal(Levels, maxDepth);
        }
        finally
        {
            // delete bottom-up with relative paths (recursive delete by absolute path would hit PATH_MAX)
            try
            {
                Environment.CurrentDirectory = dir;
                var chain = new List<string>(); 
                for (int i = 0; i < Levels; i++) { chain.Add("d"); Environment.CurrentDirectory = "d"; }
                File.Delete("leaf.txt");
                for (int i = Levels - 1; i >= 0; i--) { Environment.CurrentDirectory = ".."; Directory.Delete("d"); }
            }
            catch { }
            finally { Environment.CurrentDirectory = saved; try { Directory.Delete(dir, true); } catch { } }
        }
    }
}

[CollectionDefinition("NoParallel", DisableParallelization = true)]
public class NoParallelCollection;

[Collection("NoParallel")]
public partial class FdLimitTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit { public ulong Cur, Max; }

    [DllImport("libc", SetLastError = true)] private static extern int getrlimit(int resource, out RLimit rlim);
    [DllImport("libc", SetLastError = true)] private static extern int setrlimit(int resource, ref RLimit rlim);
    private const int RLIMIT_NOFILE = 7;   // Linux

    private static void MakeFullBinaryTree(string path, int depth)
    {
        Directory.CreateDirectory(path);
        if (depth == 0) return;
        MakeFullBinaryTree(Path.Combine(path, "l"), depth - 1);
        MakeFullBinaryTree(Path.Combine(path, "r"), depth - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Walk_RunningOutOfFileDescriptors_Throws_NeverSilentlySkips(bool ignoreInaccessible)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return;

        var dir = Path.Combine(Path.GetTempPath(), "fno-fd-" + Guid.NewGuid().ToString("N"));
        MakeFullBinaryTree(dir, 10);      // every directory keeps its fd while its first child is walked: ~10 fds in use
        getrlimit(RLIMIT_NOFILE, out var original);
        try
        {
            int open = Directory.GetFileSystemEntries("/proc/self/fd").Length;
            var low = new RLimit { Cur = (ulong)open + 4, Max = original.Max };
            if (setrlimit(RLIMIT_NOFILE, ref low) != 0) return;                 // not allowed here

            var ex = Assert.ThrowsAny<IOException>(() =>
            {
                foreach (var _ in FastDirectory.WalkBatchBuffers(dir, 10, new WalkOptions { IgnoreInaccessible = ignoreInaccessible })) { }
            });
            Assert.Contains("errno 24", ex.Message);
        }
        finally
        {
            setrlimit(RLIMIT_NOFILE, ref original);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Walk_BinaryTree_WithEnoughDescriptors_ListsEverything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fno-bt-" + Guid.NewGuid().ToString("N"));
        MakeFullBinaryTree(dir, 10);
        try
        {
            int n = 0;
            foreach (var b in FastDirectory.WalkBatchBuffers(dir, 10)) n += b.Count;
            Assert.Equal((1 << 11) - 2, n);          // 2 + 4 + ... + 2^10 directories below the root
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
