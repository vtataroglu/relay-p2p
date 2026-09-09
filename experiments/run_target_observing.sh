#!/bin/bash
# What does the target-absent constraint cost?
#
# RouteBand's one genuinely distinguishing property is that no calibration
# trace can contain the target: the holder is isolated while traces are
# collected. Related methods do not accept that constraint -- interest based
# shortcuts learn from successful transfers of the item, path traceable routing
# records gain along paths that reached the target, and query guidance uses
# prior content distribution records. This run measures what the constraint
# costs against comparators that are allowed to observe the item.
#
# This adds two arms sharing RouteBand's band, tie key, relay count and query
# streams, differing only in that their exposure estimate was allowed to
# observe the target:
#   targetband     rank by observed exposure x forwarding factor
#   targetexposure rank by observed exposure alone
#
# The target observing pass is deliberately generous: its traces run to the
# visit budget rather than stopping at the holder, so it sees more of the
# network than a real target search would.
#
# Seeds are the fanout sweep seeds so that every pre-existing arm reproduces
# exactly and can be verified. This experiment is exploratory and carries no
# confirmatory gate.

set -u
cd "$(dirname "$0")"
ROOT=runs
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another target observing run is already active; refusing to start" >&2
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
    rm -f "$OUT"/*-probes.csv
    echo "ok   seed=$SEED"
  else
    echo "STOP seed=$SEED :: $(grep -oE '(Insufficient|Exception|collision)[^.]*' "$OUT/run.log" | head -1)"
  fi
}
export -f run_one

xargs -P "$WORKERS" -I{} bash -c 'run_one "$@"' _ {} < "$JOBS" \
  > "$ROOT/progress.log" 2>&1

echo "target observing run finished"
grep -c '^ok'   "$ROOT/progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/progress.log" | sed 's/^/stopped:   /'
