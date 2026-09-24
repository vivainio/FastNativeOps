using FastNativeOps;

// usage: ls-sample [dir] [batchSize] [Auto|Readdir|Getdents64] [buf] [stat] [cached] [par=N] [min=N] [threads=N]
var dir = args.Length > 0 ? args[0] : ".";
var backend = args.Length > 2 && Enum.TryParse<NativeBackend>(args[2], true, out var b) ? b : NativeBackend.Auto;
int par = args.Select(a => a.StartsWith("par=") ? int.Parse(a[4..]) : 0).Max();
foreach (var a in args)
{
    if (a.StartsWith("min=")) FastNativeOptions.StatParallelMinEntries = int.Parse(a[4..]);
    if (a.StartsWith("threads=")) FastNativeOptions.StatWorkerThreads = int.Parse(a[8..]);
}
bool buf = args.Contains("buf"), stat = args.Contains("stat"), cached = args.Contains("cached");

if (args.Contains("diag"))
{
    // Did the statx fast path stay enabled (false = statx never had to fall back)?
    var t = typeof(FastDirectory).Assembly.GetType("FastNativeOps.Unix.UnixDirectory");
    var f = t?.GetField("s_statxUnavailable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    foreach (var batch in FastDirectory.EnumerateBatchBuffers(dir, 100, backend, StatFields.Size, cached)) { }
    Console.WriteLine($"statxUnavailable={f?.GetValue(null)}");
    return;
}

if (args.Length > 1 && int.TryParse(args[1], out var n) && n > 0)
{
    if (buf)
    {
        var fields = stat ? StatFields.Size | StatFields.ModifiedTime : StatFields.None;
        foreach (var batch in FastDirectory.EnumerateBatchBuffers(dir, n, backend, fields, cached, par))
            for (int i = 0; i < batch.Count; i++)
                Console.WriteLine(stat
                    ? $"{batch.GetType(i),-13} {batch.GetName(i)} {batch.GetSize(i)} {new DateTimeOffset(batch.GetModifiedTimeUtc(i)).ToUnixTimeSeconds()}"
                    : $"{batch.GetType(i),-13} {batch.GetName(i)}");
        return;
    }
    foreach (var batch in FastDirectory.EnumerateBatches(dir, n, backend))
        Console.WriteLine($"-- batch of {batch.Length}: {batch[0].Name} ...");
    return;
}
foreach (var e in FastDirectory.Enumerate(dir, backend))
    Console.WriteLine($"{e.Type,-13} {e.Name}");
