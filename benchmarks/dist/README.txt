FastNativeOps benchmark bundle
==============================
Portable (any OS/CPU): unzip and run. Needs the .NET 8 (or newer) runtime or SDK on the machine.

  Linux/macOS:  ./run.sh
  Windows:      run.cmd

Defaults: all benchmarks, --job short (a few minutes). Extra arguments are passed to BenchmarkDotNet:
  ./run.sh --job default          full run
  ./run.sh --filter '*Walk*'      subset
  ./run.sh --list flat            list benchmarks

  ./run.sh --dir /mnt/nfs/tmp     create the test files there (any filesystem you want to measure)

Environment variables:
  FASTNATIVEOPS_BENCH_FILES   file count, comma-separated for several sizes (default 50000)
  FASTNATIVEOPS_BENCH_DIR     same as --dir; parent dir for the test files; point at the filesystem you care about (NFS, XFS, ...)

Runs in the current directory: results (markdown tables, CSV) are written to ./BenchmarkDotNet.Artifacts/results/
under wherever you launched it, so the bundle can live on a read-only share.
Test files are created before the run and deleted afterwards.
