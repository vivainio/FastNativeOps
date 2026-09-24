#!/usr/bin/env bash
# Creates files with known sizes/mtimes and verifies size + mtime from EnumerateBatchBuffers(..., fields).
set -euo pipefail
dll="${1:?path to ls-sample.dll}"
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
settime() { touch -d "@$2" "$1" 2>/dev/null || touch -t "$(date -r "$2" +%Y%m%d%H%M.%S)" "$1"; }
expected=""
for i in $(seq 1 250); do
  f="$d/f$i"; head -c $((i * 7)) /dev/zero > "$f"; ts=$((1700000000 + i * 1000)); settime "$f" $ts
  expected+="File f$i $((i * 7)) $ts"$'\n'
done
mkdir "$d/sub"
expected=$(printf '%s' "$expected" | sort)
modes=("auto" "readdir"); [ "$(uname)" = Linux ] && modes+=("getdents64")
fail=0
for be in "${modes[@]}"; do for bs in 1 7 100 1000; do for cached in "" cached; do
  got=$(dotnet "$dll" "$d" $bs $be buf stat $cached | grep '^File' | sed 's/  */ /g' | sort)
  if [ "$got" = "$expected" ]; then echo "OK   $be bs=$bs ${cached:-synced}"; else echo "FAIL $be bs=$bs ${cached:-synced}"; fail=1
    diff <(echo "$expected") <(echo "$got") | head -4; fi
done; done; done
exit $fail
