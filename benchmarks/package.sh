#!/usr/bin/env bash
# Builds one portable, framework-dependent bundle: benchmarks/out/fastnativeops-bench.zip
# Runs on any OS/CPU that has the .NET 8 (or newer) runtime: unzip, then ./run.sh or run.cmd.
set -euo pipefail
cd "$(dirname "$0")"
rm -rf out; mkdir -p out
dir=out/fastnativeops-bench
dotnet publish Bench/Bench.csproj -c Release -o "$dir" -p:UseAppHost=false -p:DebugType=none
cp dist/README.txt dist/run.cmd dist/run.sh "$dir/"
chmod +x "$dir/run.sh"
(cd out && zip -qr fastnativeops-bench.zip fastnativeops-bench && rm -rf fastnativeops-bench)
echo "==> out/fastnativeops-bench.zip"
