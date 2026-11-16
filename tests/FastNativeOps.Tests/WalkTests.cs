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

[ClassDataSource<TreeFixture>(Shared = SharedType.PerClass)]
public class WalkTests(TreeFixture fx)
{
    public static IEnumerable<NativeBackend> Backends() => DirectoryTests.Backends();

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

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Walk_MatchesSystemIO(NativeBackend backend)
    {
        foreach (int bs in new[] { 1, 7, 64, 100_000 })
            await Assert.That(Sorted(Walk(fx.Root, bs, new WalkOptions { Backend = backend }))).IsEquivalentTo(Reference(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Walk_BatchesNeverSpanDirectories_AndReportDepth()
    {
        foreach (var b in FastDirectory.WalkBatchBuffers(fx.Root, 16))
        {
            int expectedDepth = b.DirectoryPath == fx.Root ? 0 : Path.GetRelativePath(fx.Root, b.DirectoryPath).Split(Path.DirectorySeparatorChar).Length;
            await Assert.That(b.Depth).IsEqualTo(expectedDepth);
            for (int i = 0; i < b.Count; i++)
                await Assert.That(Path.GetDirectoryName(Path.Join(b.DirectoryPath, b.GetName(i)))).IsEqualTo(b.DirectoryPath);
        }
    }

    [Test]
    public async Task Walk_MaxDepth()
    {
        await Assert.That(Sorted(Walk(fx.Root, 50, new WalkOptions { MaxDepth = 0 }))).IsEquivalentTo(Sorted(Directory.GetFileSystemEntries(fx.Root).Select(p => Path.GetRelativePath(fx.Root, p))), CollectionOrdering.Matching);
        var d1 = Walk(fx.Root, 50, new WalkOptions { MaxDepth = 1 });
        await Assert.That(d1).Contains(Path.Join("a", "1.txt"));
        await Assert.That(d1).DoesNotContain(Path.Join("a", "b", "2.txt"));
    }

    [Test]
    public async Task Walk_ShouldDescend_Prunes()
    {
        var got = Walk(fx.Root, 50, new WalkOptions { ShouldDescend = (name, depth) => name != "b" });
        await Assert.That(got).Contains(Path.Join("a", "b"));                       // the directory itself is listed
        await Assert.That(got).DoesNotContain(Path.Join("a", "b", "2.txt"));        // but not entered
        await Assert.That(got).Contains(Path.Join("a", "x", "4.txt"));
    }

    [Test, MethodDataSource(nameof(Backends))]
    public async Task Walk_WithStat_ReportsSizes(NativeBackend backend)
    {
        int checkedFiles = 0;
        var o = new WalkOptions { Fields = StatFields.Size, Backend = backend, StatParallelism = 4 };
        foreach (var b in FastDirectory.WalkBatchBuffers(fx.Root, 20, o))
            for (int i = 0; i < b.Count; i++)
            {
                var name = b.GetName(i);
                if (b.Depth == 0 && name.StartsWith("top-"))
                {
                    await Assert.That(b.GetSize(i)).IsEqualTo(int.Parse(name.AsSpan(4, 2)));
                    checkedFiles++;
                }
            }
        await Assert.That(checkedFiles).IsEqualTo(40);
    }

    [Test]
    public async Task Walk_MissingRoot_Throws() =>
        await Assert.That(() => FastDirectory.WalkBatchBuffers(Path.Join(fx.Root, "nope"), 10).ToList()).Throws<DirectoryNotFoundException>();

    [Test]
    public async Task Walk_SymlinkLoop_IsReportedNotFollowed()
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
            await Assert.That(types[Path.Join("a", "loop")]).IsEqualTo(EntryType.SymbolicLink);
            await Assert.That(types["link-to-a"]).IsEqualTo(EntryType.SymbolicLink);
            await Assert.That(types.Count).IsEqualTo(4);            // a, a/f.txt, a/loop, link-to-a
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Walk_Inaccessible_DirectoryIsSkippedOrThrows()
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

            await Assert.That(() => Walk(dir, 10)).Throws<UnauthorizedAccessException>();   // as System.IO
            var got = Walk(dir, 10, new WalkOptions { IgnoreInaccessible = true });
            await Assert.That(Sorted(got)).IsEquivalentTo(new[] { "locked", "ok.txt" }, CollectionOrdering.Matching);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Walk_TreeDeeperThanPathMax_OnLinux()
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
            await Assert.That(dirs).IsEqualTo(Levels);
            await Assert.That(leaf).IsEqualTo(1);
            await Assert.That(maxDepth).IsEqualTo(Levels);
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

[NotInParallel]
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Walk_RunningOutOfFileDescriptors_Throws_NeverSilentlySkips(bool ignoreInaccessible)
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

            var ex = await Assert.That(() =>
            {
                foreach (var _ in FastDirectory.WalkBatchBuffers(dir, 10, new WalkOptions { IgnoreInaccessible = ignoreInaccessible })) { }
            }).Throws<IOException>();
            await Assert.That(ex!.Message).Contains("errno 24");
        }
        finally
        {
            setrlimit(RLIMIT_NOFILE, ref original);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Walk_BinaryTree_WithEnoughDescriptors_ListsEverything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fno-bt-" + Guid.NewGuid().ToString("N"));
        MakeFullBinaryTree(dir, 10);
        try
        {
            int n = 0;
            foreach (var b in FastDirectory.WalkBatchBuffers(dir, 10)) n += b.Count;
            await Assert.That(n).IsEqualTo((1 << 11) - 2);          // 2 + 4 + ... + 2^10 directories below the root
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
