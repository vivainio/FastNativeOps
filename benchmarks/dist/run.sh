#!/usr/bin/env bash
# Runs the FastNativeOps vs System.IO benchmarks (needs the .NET 8+ runtime). Runs in the current directory.
#   ./run.sh                          all benchmarks, short job
#   ./run.sh --job default            full run (slower, more accurate)
#   ./run.sh --filter '*Walk*'        subset
#   ./run.sh --dir /mnt/nfs/tmp       create test files there
#   FASTNATIVEOPS_BENCH_FILES=1000,50000 ./run.sh
here="$(cd "$(dirname "$0")" && pwd)"
exec dotnet "$here/Bench.dll" "$@"
