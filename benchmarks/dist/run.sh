#!/usr/bin/env bash
# Runs the FastNativeOps vs System.IO benchmarks. No .NET installation needed.
#   ./run.sh                          all benchmarks, short job
#   ./run.sh --job default            full run (slower, more accurate)
#   ./run.sh --filter '*Walk*'        subset
#   ./run.sh --dir /mnt/nfs/tmp       create test files there
#   FASTNATIVEOPS_BENCH_DIR=/mnt/nfs/x FASTNATIVEOPS_BENCH_FILES=1000,50000 ./run.sh
here="$(cd "$(dirname "$0")" && pwd)"
chmod +x "$here/Bench" 2>/dev/null
exec "$here/Bench" "$@"   # runs in the caller's current directory
