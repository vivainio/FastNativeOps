using FastNativeOps;

var dir = args.Length > 0 ? args[0] : ".";
var backend = args.Length > 2 && Enum.TryParse<NativeBackend>(args[2], true, out var b) ? b : NativeBackend.Auto;
if (args.Length > 1 && int.TryParse(args[1], out var n) && n > 0)
{
    foreach (var batch in FastDirectory.EnumerateBatches(dir, n, backend))
        Console.WriteLine($"-- batch of {batch.Length}: {batch[0].Name} ...");
    return;
}
foreach (var e in FastDirectory.Enumerate(dir, backend))
    Console.WriteLine($"{e.Type,-13} {e.Name}");
