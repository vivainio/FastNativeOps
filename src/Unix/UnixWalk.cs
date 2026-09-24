using System.Buffers;
using System.Runtime.InteropServices;

namespace FastNativeOps.Unix;

internal static unsafe partial class UnixDirectory
{
    [LibraryImport(Lib, EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int openat(int dirfd, string path, int flags);

    [LibraryImport(Lib, EntryPoint = "close")]
    private static partial int close(int fd);

    private const int AT_FDCWD = -100, O_CLOEXEC = 0x80000;
    private const int ENOENT = 2, ENOTDIR = 20, ELOOP = 40, ENFILE = 23, EMFILE = 24;

    // O_DIRECTORY / O_NOFOLLOW differ between x86 and the generic (arm64) ABI.
    private static readonly int ODirectory = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static readonly int ONoFollow = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    private sealed class WalkFrame(int fd, string path, int depth)
    {
        public int Fd = fd;                      // -1 once closed early
        public readonly string Path = path;
        public readonly int Depth = depth;
        public bool Read;
        public List<string>? Subdirs;
        public int Next;
    }

    internal static IEnumerable<DirectoryBatch> WalkGetdents(string root, int batchSize, WalkOptions o, int parallelism)
    {
        int rootFd = openat(AT_FDCWD, root, ODirectory | O_CLOEXEC);      // the root may itself be a symlink
        if (rootFd < 0)
            throw new IOException($"Cannot open '{root}' (errno {Marshal.GetLastPInvokeError()})");

        var stack = new Stack<WalkFrame>();
        stack.Push(new WalkFrame(rootFd, root, 0));
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var batch = new DirectoryBatch(batchSize, o.Fields);
        int minEntries = FastNativeOptions.StatParallelMinEntries;
        bool stat = o.Fields != StatFields.None;
        try
        {
            while (stack.Count > 0)
            {
                var f = stack.Peek();

                if (!f.Read)
                {
                    f.Read = true;
                    batch.Clear();
                    batch.DirectoryPath = f.Path;
                    batch.Depth = f.Depth;
                    int pos = 0, n = 0;
                    while (true)
                    {
                        if (pos >= n)
                        {
                            n = GetDents(f.Fd, buf);
                            pos = 0;
                            if (n < 0)
                            {
                                int errno = Marshal.GetLastPInvokeError();
                                if (errno == EINTR) { n = 0; continue; }
                                if (f.Depth > 0 && o.IgnoreInaccessible) break;
                                throw new IOException($"Cannot read '{f.Path}' (errno {errno})");
                            }
                            if (n == 0) break;
                        }
                        pos = FillBatch(buf, pos, n, batch, f.Path);
                        if (batch.Count == batch.Capacity)
                        {
                            CollectSubdirs(f, batch, o);
                            if (stat) StatBatch(f.Fd, f.Path, batch, o.AllowCachedAttributes, batch.Count >= minEntries ? parallelism - 1 : 0);
                            yield return batch;
                            batch.Clear();
                        }
                    }
                    if (batch.Count > 0)
                    {
                        CollectSubdirs(f, batch, o);
                        if (stat) StatBatch(f.Fd, f.Path, batch, o.AllowCachedAttributes, batch.Count >= minEntries ? parallelism - 1 : 0);
                        yield return batch;
                        batch.Clear();
                    }
                    continue;
                }

                if (f.Subdirs is not null && f.Next < f.Subdirs.Count)
                {
                    string name = f.Subdirs[f.Next++];
                    if (!(o.ShouldDescend?.Invoke(name, f.Depth + 1) ?? true)) continue;
                    int child = openat(f.Fd, name, ODirectory | ONoFollow | O_CLOEXEC);
                    if (child < 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        if (errno is ENOENT or ENOTDIR or ELOOP) continue;   // removed or replaced since it was listed
                        if (errno is EMFILE or ENFILE)                        // never silently drop subtrees for this
                            throw new IOException($"Too many open files while opening '{Path.Join(f.Path, name)}' " +
                                                  $"(errno {errno}); the walk needs file descriptors for the directories above it that still have unvisited subdirectories. Raise ulimit -n.");
                        if (o.IgnoreInaccessible) continue;
                        throw new IOException($"Cannot open '{Path.Join(f.Path, name)}' (errno {errno})");
                    }
                    stack.Push(new WalkFrame(child, Path.Join(f.Path, name), f.Depth + 1));
                    if (f.Next >= f.Subdirs.Count)      // last subdirectory taken: this directory's fd is no longer needed
                    {
                        close(f.Fd);
                        f.Fd = -1;
                    }
                    continue;
                }

                if (f.Fd >= 0) close(f.Fd);
                stack.Pop();
            }
        }
        finally
        {
            while (stack.Count > 0)
            {
                int fd = stack.Pop().Fd;
                if (fd >= 0) close(fd);
            }
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void CollectSubdirs(WalkFrame f, DirectoryBatch batch, WalkOptions o)
    {
        if (f.Depth >= o.MaxDepth) return;
        for (int i = 0; i < batch.Count; i++)
            if (batch.GetType(i) == EntryType.Directory)
                (f.Subdirs ??= []).Add(batch.GetName(i));
    }
}
