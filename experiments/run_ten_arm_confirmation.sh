#!/bin/bash
# Ten arm RouteBand confirmation on the design seeds.
#
# This is the gate battery of the main text: RouteBand against MAXDEG, SHAM,
# PRODUCT, BANDEXPOSURE and BANDGREEDY, together with the 90 and 97.5 percent
# threshold sensitivity arms, at fanout 7 on 128 graphs per attachment family.
# The out of sample repetition of the central contrast lives in
# run_confirmation.sh and uses seeds that appear nowhere else.
#
# Seeds 4301-4428 are popularity attachment and 4501-4628 uniform. The
# confirmOnly flag selects the ten arm set, which requires the fixed utility
# band to be feasible in every graph.

set -u
cd "$(dirname "$0")"
SIM=../src/ScalableP2P/bin/Release/net7.0/Diagnostics.dll
ROOT=ten-arm
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another ten arm run is already active; refusing to start" >&2
  exit 1
fi
trap 'rmdir "$LOCK" 2>/dev/null' EXIT

mkdir -p "$ROOT"
JOBS="$ROOT/joblist.txt"
: > "$JOBS"

for FAM in popularity uniform; do
  if [ "$FAM" = "popularity" ]; then LO=4301; UNI=0; else LO=4501; UNI=1; fi
  HI=$((LO + 127))
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
       path-aware "$OUT" "$SEED" 7 20000 50 400 2000 2000 "$UNI" 1 500 100 \
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

echo "ten arm run finished"
grep -c '^ok'   "$ROOT/progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/progress.log" | sed 's/^/stopped:   /'
