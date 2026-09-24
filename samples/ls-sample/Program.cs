using FastNativeOps;

foreach (var e in FastDirectory.Enumerate(args.Length > 0 ? args[0] : "."))
    Console.WriteLine($"{e.Type,-13} {e.Name}");
