#!/bin/bash
# Dynamic Adaptive Utility Slicing (DAUS) evaluation.
#
# DAUS relaxes the utility threshold in 0.05 steps from 0.95 until the eligible
# set holds at least 2 x k_c candidates, floor 0.50. The claim under test is
# that this rescues feasibility on topologies where the fixed band cannot fill
# the relay set, without costing anything where the fixed band works.
#
# Three regimes:
#   A  kc=50 m=7   fixed band holds 245-1278 candidates. DAUS must never
#                  trigger, so it must equal RouteBand exactly. Identity check,
#                  so a small sample suffices.
#   C  kc=16 m=15  fixed band holds ~22 candidates against a 32 trigger, so
#                  DAUS relaxes and the relaxed slice still leaves room to rank.
#   B  kc=24 m=23  fixed band cannot fill the relay set at all: RouteBand stops.
#                  This is the regime DAUS exists for.
#
# Regimes B and C run with dausOnly=1 so an infeasible fixed band omits the
# fixed band arms instead of aborting the graph.

set -u
cd "$(dirname "$0")"
ROOT=daus
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another daus run is already active (lock: $LOCK); refusing to start" >&2
  exit 1
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

mkdir -p "$ROOT"
JOBS="$ROOT/joblist.txt"
: > "$JOBS"

# regime  kc  m   seeds-per-family  dausOnly
for spec in "A:50:7:8:1" "C:16:15:64:1" "B:24:23:64:1"; do
  IFS=: read -r REG KC M NSEED DO <<< "$spec"
  for FAM in popularity uniform; do
    if [ "$FAM" = "popularity" ]; then LO=4301; UNI=0; else LO=4501; UNI=1; fi
    HI=$((LO + NSEED - 1))
    for SEED in $(seq $LO $HI); do
      OUT="$ROOT/$REG/$FAM/seed-$SEED"
      [ -f "$OUT/path-aware-m$M-s$SEED.csv" ] && continue
      echo "$OUT $SEED $M $KC $UNI $DO" >> "$JOBS"
    done
  done
done

echo "queued $(wc -l < "$JOBS" | tr -d ' ') runs across $WORKERS workers"

run_one() {
  read -r OUT SEED M KC UNI DO <<< "$1"
  rm -rf "$OUT"; mkdir -p "$OUT"
  if dotnet ../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll \
       path-aware "$OUT" "$SEED" "$M" 20000 "$KC" 400 500 2000 "$UNI" 0 200 100 "$DO" \
       > "$OUT/run.log" 2>&1; then
    echo "ok   kc=$KC m=$M seed=$SEED"
  else
    echo "STOP kc=$KC m=$M seed=$SEED :: $(grep -oE '(Insufficient|Adaptive)[^.]*' "$OUT/run.log" | head -1)"
  fi
}
export -f run_one

xargs -P "$WORKERS" -I{} bash -c 'run_one "$@"' _ {} < "$JOBS" \
  > "$ROOT/daus-progress.log" 2>&1

echo "daus run finished"
grep -c '^ok'   "$ROOT/daus-progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/daus-progress.log" | sed 's/^/stopped:   /'
