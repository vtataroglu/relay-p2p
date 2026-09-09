#!/bin/bash
# Neutral complete set diagnostic for the degree prior.
#
# This experiment does not evaluate RouteBand. It asks whether the analytical
# prior transfers across attachment families by comparing complete relay sets
# held at exact degrees, so its unit of intervention is the same as the
# selector's while the selection rule is removed.
#
# Each seed builds one graph and evaluates three arm families:
#   exact             50 relays at residual degree 7, 19 or 23, so degrees
#                     after connection are exactly 8, 20 or 24
#   full forwarding   the degree 8 and degree 24 placements re-evaluated with
#                     every nonparent neighbour examined in identifier order
#   position matched  triples drawn without replacement after 500 target free
#                     traces, balanced on exposure rank and core distance
#
# Seeds 4701-4764 are popularity attachment and 4801-4864 uniform, 64 graphs
# per family. Trend state, demand detection and online rewiring are disabled.

set -u
cd "$(dirname "$0")"
SIM=../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll
ROOT=neutral
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another neutral diagnostic run is already active; refusing to start" >&2
  exit 1
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

mkdir -p "$ROOT"
JOBS="$ROOT/joblist.txt"
: > "$JOBS"

for FAM in popularity uniform; do
  if [ "$FAM" = "popularity" ]; then LO=4701; UNI=0; else LO=4801; UNI=1; fi
  HI=$((LO + 63))
  for SEED in $(seq $LO $HI); do
    OUT="$ROOT/$FAM/seed-$SEED"
    [ -f "$OUT/neutral-exact-s$SEED.csv" ] && continue
    echo "$OUT $SEED $UNI" >> "$JOBS"
  done
done

echo "queued $(wc -l < "$JOBS" | tr -d ' ') runs across $WORKERS workers"

run_one() {
  read -r OUT SEED UNI <<< "$1"
  rm -rf "$OUT"; mkdir -p "$OUT"
  if dotnet ../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll \
       neutral-mechanism "$OUT" "$SEED" "$UNI" 20000 50 1000 2000 500 \
       > "$OUT/run.log" 2>&1; then
    # The probe rows are the bulk of the output and nothing in the analysis
    # reads them; discard immediately rather than accumulating gigabytes.
    rm -f "$OUT"/*-probes.csv
    echo "ok   seed=$SEED"
  else
    echo "STOP seed=$SEED :: $(grep -oE '(Insufficient|Exception)[^.]*' "$OUT/run.log" | head -1)"
  fi
}
export -f run_one

xargs -P "$WORKERS" -I{} bash -c 'run_one "$@"' _ {} < "$JOBS" \
  > "$ROOT/progress.log" 2>&1

echo "neutral diagnostic run finished"
grep -c '^ok'   "$ROOT/progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/progress.log" | sed 's/^/stopped:   /'
