#!/bin/bash
# Identification test: vary the utility band threshold at FIXED fanout 7.
#
# The fanout sweep cannot separate band width from fanout, because at a fixed
# threshold the width is a deterministic function of the fanout. At m=7 the
# thresholds 0.975, 0.95 and 0.90 admit 2, 5 and 29 prospective degrees, so
# this run measures the RouteBand minus BANDRANDOM contrast across three band
# widths at one fanout.

set -u
cd "$(dirname "$0")"
ROOT=tau
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another tau run is already active (lock: $LOCK); refusing to start" >&2
  exit 1
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

mkdir -p "$ROOT"
JOBS="$ROOT/joblist.txt"
: > "$JOBS"

for FAM in popularity uniform; do
  if [ "$FAM" = "popularity" ]; then LO=4301; UNI=0; else LO=4501; UNI=1; fi
  HI=$((LO + 63))
  for SEED in $(seq $LO $HI); do
    OUT="$ROOT/$FAM/seed-$SEED"
    [ -f "$OUT/path-aware-m7-s$SEED.csv" ] && continue
    echo "$OUT $SEED $UNI" >> "$JOBS"
  done
done

echo "queued $(wc -l < "$JOBS" | tr -d ' ') runs across $WORKERS workers"

run_one() {
  read -r OUT SEED UNI <<< "$1"
  rm -rf "$OUT"; mkdir -p "$OUT"
  if dotnet ../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll \
       path-aware "$OUT" "$SEED" 7 20000 50 400 2000 2000 "$UNI" 0 500 100 \
       > "$OUT/run.log" 2>&1; then
    echo "ok   seed=$SEED"
  else
    echo "STOP seed=$SEED :: $(tail -n 3 "$OUT/run.log" | tr '\n' ' ')"
  fi
}
export -f run_one

xargs -P "$WORKERS" -I{} bash -c 'run_one "$@"' _ {} < "$JOBS" \
  > "$ROOT/tau-progress.log" 2>&1

echo "tau run finished"
grep -c '^ok'   "$ROOT/tau-progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/tau-progress.log" | sed 's/^/stopped:   /'
