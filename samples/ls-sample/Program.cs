using FastNativeOps;

// usage: ls-sample [dir] [batchSize] [Auto|Readdir|Getdents64] [buf]
var dir = args.Length > 0 ? args[0] : ".";
var backend = args.Length > 2 && Enum.TryParse<NativeBackend>(args[2], true, out var b) ? b : NativeBackend.Auto;
bool buf = args.Length > 3 && args[3] == "buf";

if (args.Length > 1 && int.TryParse(args[1], out var n) && n > 0)
{
    if (buf)
    {
        foreach (var batch in FastDirectory.EnumerateBatchBuffers(dir, n, backend))
            for (int i = 0; i < batch.Count; i++)
                Console.WriteLine($"{batch.GetType(i),-13} {batch.GetName(i)}");
        return;
    }
    foreach (var batch in FastDirectory.EnumerateBatches(dir, n, backend))
        Console.WriteLine($"-- batch of {batch.Length}: {batch[0].Name} ...");
    return;
}
foreach (var e in FastDirectory.Enumerate(dir, backend))
    Console.WriteLine($"{e.Type,-13} {e.Name}");
