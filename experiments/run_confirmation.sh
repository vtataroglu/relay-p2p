#!/bin/bash
# Fresh-seed confirmation of RouteBand minus BANDRANDOM.
#
# The decision rule below was fixed before these graphs were generated.
#
# Seeds 6001-6128 / 6201-6328 have never been used in any prior run in this
# project; disjointness was verified against all 642 previously used seeds.
#
# Primary cell is m=7 with 128 seeds per family. Fanouts 5 and 11 are
# prespecified secondary cells with 64 seeds per family.
#
# Every completed graph enters the analysis regardless of effect direction.
# A failed contrast may not be replaced and no seed may be substituted.

set -u
cd "$(dirname "$0")"
SIM=../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll
ROOT=runs
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another confirmation run is already active; refusing to start" >&2
  exit 1
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

mkdir -p "$ROOT"
JOBS="$ROOT/joblist.txt"
: > "$JOBS"

# fanout : seeds-per-family
for spec in "7:128" "5:64" "11:64"; do
  M="${spec%%:*}"; N="${spec##*:}"
  for FAM in popularity uniform; do
    if [ "$FAM" = "popularity" ]; then LO=6001; UNI=0; else LO=6201; UNI=1; fi
    HI=$((LO + N - 1))
    for SEED in $(seq $LO $HI); do
      OUT="$ROOT/m$M/$FAM/seed-$SEED"
      [ -f "$OUT/path-aware-m$M-s$SEED.csv" ] && continue
      echo "$OUT $SEED $M $UNI" >> "$JOBS"
    done
  done
done

echo "queued $(wc -l < "$JOBS" | tr -d ' ') runs across $WORKERS workers"

run_one() {
  read -r OUT SEED M UNI <<< "$1"
  rm -rf "$OUT"; mkdir -p "$OUT"
  if dotnet ../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll \
       path-aware "$OUT" "$SEED" "$M" 20000 50 400 2000 2000 "$UNI" 0 500 100 \
       > "$OUT/run.log" 2>&1; then
    # The probe rows are the bulk of the output and nothing in the analysis
    # reads them; discard immediately rather than accumulating gigabytes.
    rm -f "$OUT"/*-probes.csv
    echo "ok   m=$M seed=$SEED"
  else
    echo "STOP m=$M seed=$SEED :: $(grep -oE '(Insufficient|Adaptive|Exception)[^.]*' "$OUT/run.log" | head -1)"
  fi
}
export -f run_one

xargs -P "$WORKERS" -I{} bash -c 'run_one "$@"' _ {} < "$JOBS" \
  > "$ROOT/progress.log" 2>&1

echo "confirmation run finished"
grep -c '^ok'   "$ROOT/progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/progress.log" | sed 's/^/stopped:   /'
