using FastNativeOps;

var dir = args.Length > 0 ? args[0] : ".";
if (args.Length > 1 && int.TryParse(args[1], out var n))
{
    foreach (var batch in FastDirectory.EnumerateBatches(dir, n))
        Console.WriteLine($"-- batch of {batch.Length}: {batch[0].Name} ...");
    return;
}
foreach (var e in FastDirectory.Enumerate(dir))
    Console.WriteLine($"{e.Type,-13} {e.Name}");
