#!/bin/bash
# Disentangle the target observing comparison.
#
# TARGETBAND differs from RouteBand in two ways at once: its holder is
# connected while calibration traces are collected, and its estimate is built
# from successful traces only. These arms separate the two factors and probe
# the success criterion:
#   connectedband, connectedexposure   holder connected, estimate target blind
#   targetbandk, targetexposurek       pass continues until 400 successes
#   targetvisitband, targetvisitexposure, targetvisitbandk
#                                      success = holder reached, not expanded
#   bandquartile                       exposure kept only at quartile precision
#   bandrandomb, bandrandomc           two further uniform draws from the band
#
# Seeds are the fanout sweep seeds, so every pre-existing arm reproduces
# exactly and can be verified against the earlier runs. This experiment is
# exploratory and carries no confirmatory gate.
set -u
cd "$(dirname "$0")"
ROOT=runs
WORKERS=5
export DOTNET_gcServer=0
export DOTNET_GCHeapCount=1

LOCK="$ROOT.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another disentangle run is already active; refusing to start" >&2
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

echo "disentangle run finished"
grep -c '^ok'   "$ROOT/progress.log" | sed 's/^/completed: /'
grep -c '^STOP' "$ROOT/progress.log" | sed 's/^/stopped:   /'
