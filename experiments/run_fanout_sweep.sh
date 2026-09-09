#!/bin/bash
# Fanout sweep for the RouteBand study.
#
# Varies the query fanout over m = 3, 5, 7 and 11, and includes bandrandom,
# a uniform draw inside the 95 percent utility band, as the uninformed control
# against which the value of the measured exposure is read.
#
# All arms are emitted (confirmOnly=0). The graph seed remains the inferential
# unit and the seed ranges are the published ones, so each fanout point is a
# paired replication of the same 256 graphs under a different fanout.

set -u
cd "$(dirname "$0")"
DLL=../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll
ROOT=sweep
# The simulator is single threaded, but .NET server GC gives each process a GC
# thread per core. Eight concurrent processes on ten cores drove load average
# past 20 and roughly quadrupled per run wall time. Workstation GC plus a
# smaller worker count keeps the machine near, not past, saturation.
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1
export DOTNET_TieredPGO=0

# Two concurrent sweeps silently clobber each other's output directories,
# because each job clears its own directory before running. Refuse to start a
# second instance rather than produce a half written matrix.
LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another sweep is already running (lock: $LOCK); refusing to start" >&2
  exit 1
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

mkdir -p "$ROOT"
JOBS="$ROOT/joblist.txt"
: > "$JOBS"

# Seeds per family per fanout. The v1 confirmation used 128; this sweep uses the
# first 64 of each published range, fixed before any sweep outcome was seen.
# With the SDs observed in v1 (1.3 to 1.9 pp), 64 paired graphs give a standard
# error near 0.23 pp, so a 2 pp effect lands at t ~ 9 and even a 1 pp effect at
# t ~ 4.4. The extra 64 seeds per cell would buy no decision at four times the
# machine time. Contrasts remain paired within seed and fanout.
SEEDS_PER_FAMILY=64

for M in 3 5 7 11; do
  for FAM in popularity uniform; do
    if [ "$FAM" = "popularity" ]; then LO=4301; UNI=0; else LO=4501; UNI=1; fi
    HI=$((LO + SEEDS_PER_FAMILY - 1))
    for SEED in $(seq $LO $HI); do
      OUT="$ROOT/m$M/$FAM/seed-$SEED"
      # Skip work that already completed, so the sweep is resumable.
      if [ -f "$OUT/path-aware-m$M-s$SEED.csv" ]; then continue; fi
      echo "$OUT $SEED $M $UNI" >> "$JOBS"
    done
  done
done

TOTAL=$(wc -l < "$JOBS" | tr -d ' ')
echo "queued $TOTAL runs across $WORKERS workers"

run_one() {
  read -r OUT SEED M UNI <<< "$1"
  rm -rf "$OUT"; mkdir -p "$OUT"
  if dotnet ../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll \
       path-aware "$OUT" "$SEED" "$M" 20000 50 400 2000 2000 "$UNI" 0 500 100 \
       > "$OUT/run.log" 2>&1; then
    echo "ok   m=$M seed=$SEED"
  else
    # A band-infeasibility stop is a scientific result, not a crash to hide.
    echo "STOP m=$M seed=$SEED :: $(tail -n 3 "$OUT/run.log" | tr '\n' ' ')"
  fi
}
export -f run_one

xargs -P "$WORKERS" -I{} bash -c 'run_one "$@"' _ {} < "$JOBS" \
  > "$ROOT/sweep-progress.log" 2>&1

echo "sweep finished"
grep -c '^ok'   "$ROOT/sweep-progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/sweep-progress.log" | sed 's/^/stopped:   /'
