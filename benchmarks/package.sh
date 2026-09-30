#!/usr/bin/env bash
# Builds self-contained, unzip-and-run benchmark bundles: benchmarks/out/fastnativeops-bench-<rid>.zip
# Usage: benchmarks/package.sh [rid ...]   (default: linux-x64 linux-arm64 osx-arm64 osx-x64 win-x64 win-arm64)
set -euo pipefail
cd "$(dirname "$0")"
rids=("$@"); [ ${#rids[@]} -gt 0 ] || rids=(linux-x64 linux-arm64 osx-arm64 osx-x64 win-x64 win-arm64)
rm -rf out; mkdir -p out
for rid in "${rids[@]}"; do
  dir=out/fastnativeops-bench-$rid
  dotnet publish Bench/Bench.csproj -c Release -r "$rid" --self-contained -o "$dir" -p:DebugType=none
  cp dist/README.txt "$dir/"
  case $rid in win-*) cp dist/run.cmd "$dir/" ;; *) cp dist/run.sh "$dir/"; chmod +x "$dir/run.sh" "$dir/Bench" ;; esac
  (cd out && zip -qr "fastnativeops-bench-$rid.zip" "fastnativeops-bench-$rid" && rm -rf "fastnativeops-bench-$rid")
  echo "==> out/fastnativeops-bench-$rid.zip"
done
