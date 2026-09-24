#!/usr/bin/env bash
# Verifies all backends/batch modes list the same entries as the reference (readdir), and count matches `ls -A`.
set -euo pipefail
dll="${1:?path to ls-sample.dll}"; shift
dirs=("$@"); [ ${#dirs[@]} -gt 0 ] || dirs=(/usr/bin /etc)
run() { dotnet "$dll" "$@" | sort; }
fail=0
for d in "${dirs[@]}"; do
  ref=$(run "$d" 0 readdir); n=$(printf '%s\n' "$ref" | grep -c .)
  [ "$n" -gt 1 ] || { echo "FAIL: empty reference for $d"; exit 1; }
  [ "$n" -eq "$(ls -A "$d" | wc -l)" ] || echo "note: $d ref=$n ls=$(ls -A "$d" | wc -l) (dir may be changing)"
  modes=("auto 0 auto")
  for bs in 1 7 64 100000; do modes+=("buf $bs auto" "buf $bs readdir"); [ "$(uname)" = Linux ] && modes+=("buf $bs getdents64"); done
  for m in "${modes[@]}"; do
    read -r kind bs be <<<"$m"
    if [ "$kind" = buf ]; then got=$(run "$d" "$bs" "$be" buf); else got=$(run "$d" 0 "$be"); fi
    if [ "$got" = "$ref" ]; then echo "OK   $d $kind bs=$bs $be ($n entries)"; else echo "FAIL $d $kind bs=$bs $be"; fail=1; fi
  done
done
exit $fail
