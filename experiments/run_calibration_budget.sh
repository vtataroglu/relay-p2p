#!/bin/bash
# Calibration budget sensitivity at fixed fanout 7.
#
# Within the utility band, where the ranking operates, the split-half Spearman
# correlation of the exposure estimate between two independent 200-trace halves
# is about 0.15, and the two halves agree on about 7 of the 50 selected relays,
# so the estimate is noisy at the point of use. This run measures how the
# RouteBand minus BANDRANDOM effect varies with the calibration budget K.
#
# The K loop below selects the budgets. The curve in the paper uses K=100, 200
# and 500 from this script and K=400 from the fanout sweep, on the same 64
# seeds per family.

set -u
cd "$(dirname "$0")"
ROOT=kbudget
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another kbudget run is already active (lock: $LOCK); refusing to start" >&2
  exit 1
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

mkdir -p "$ROOT"
JOBS="$ROOT/joblist.txt"
: > "$JOBS"

for K in 100 200 500; do
  for FAM in popularity uniform; do
    if [ "$FAM" = "popularity" ]; then LO=4301; UNI=0; else LO=4501; UNI=1; fi
    HI=$((LO + 63))
    for SEED in $(seq $LO $HI); do
      OUT="$ROOT/k$K/$FAM/seed-$SEED"
      [ -f "$OUT/path-aware-m7-s$SEED.csv" ] && continue
      echo "$OUT $SEED $UNI $K" >> "$JOBS"
    done
  done
done

echo "queued $(wc -l < "$JOBS" | tr -d ' ') runs across $WORKERS workers"

run_one() {
  read -r OUT SEED UNI K <<< "$1"
  rm -rf "$OUT"; mkdir -p "$OUT"
  if dotnet ../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll \
       path-aware "$OUT" "$SEED" 7 20000 50 "$K" 2000 2000 "$UNI" 0 500 100 \
       > "$OUT/run.log" 2>&1; then
    echo "ok   K=$K seed=$SEED"
  else
    echo "STOP K=$K seed=$SEED :: $(tail -n 3 "$OUT/run.log" | tr '\n' ' ')"
  fi
}
export -f run_one

xargs -P "$WORKERS" -I{} bash -c 'run_one "$@"' _ {} < "$JOBS" \
  > "$ROOT/kbudget-progress.log" 2>&1

echo "kbudget run finished"
grep -c '^ok'   "$ROOT/kbudget-progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/kbudget-progress.log" | sed 's/^/stopped:   /'
