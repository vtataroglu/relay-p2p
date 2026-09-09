#!/usr/bin/env python3
"""Disentangle the target observing comparison into its two factors.

TARGETBAND differs from RouteBand in two ways at once: its holder is connected
while calibration traces are collected, and its estimate is conditioned on the
traces that succeeded. CONNECTEDBAND instantiates the missing factorial cell,
holder connected but estimate target blind, so the two factors separate:

    routeband  - connectedband   isolation alone
    connectedband - targetband   conditioning alone
    routeband  - targetband      both at once

The TARGETVISIT arms change the success criterion from "holder expanded" to
"holder reached", which is what a search observes, and the trailing K arms
equalise the effective sample. BANDRANDOM is the uninformed reference.

Usage:
    python3 analysis/analyze_disentangle.py <runs-directory>
"""

from __future__ import annotations

import csv
import glob
import math
import statistics as st
import sys
from collections import defaultdict
from pathlib import Path

from scipy import stats

FAMILIES = ("popularity", "uniform")

CONTRASTS = [
    ("routeband", "connectedband",
     "RouteBand - ConnectedBand (isolation alone)"),
    ("connectedband", "targetband",
     "ConnectedBand - TargetBand (conditioning alone)"),
    ("routeband", "targetband",
     "RouteBand - TargetBand (both at once)"),
    ("targetband", "bandrandom",
     "TargetBand - BandRandom (expanded criterion)"),
    ("targetbandk", "bandrandom",
     "TargetBandK - BandRandom (matched K)"),
    ("targetvisitband", "bandrandom",
     "TargetVisitBand - BandRandom (reached criterion)"),
    ("targetvisitbandk", "bandrandom",
     "TargetVisitBandK - BandRandom (reached, matched K)"),
    ("routeband", "targetvisitbandk",
     "RouteBand - TargetVisitBandK (constraint vs strongest arm)"),
    ("connectedband", "targetvisitbandk",
     "ConnectedBand - TargetVisitBandK (conditioning at matched K)"),
    ("bandexposure", "connectedexposure",
     "BandExposure - ConnectedExposure (exposure-only pair)"),
]


def load(root: Path, family: str) -> dict[int, dict[str, float]]:
    out: dict[int, dict[str, float]] = defaultdict(dict)
    for path in sorted(glob.glob(f"{root}/{family}/seed-*/path-aware-m7-s*.csv")):
        name = Path(path).name
        if any(tag in name for tag in ("-candidates", "-selection", "degrees")):
            continue
        for row in csv.DictReader(open(path)):
            out[int(row["seed"])][row["policy"]] = float(row["pHat"]) * 100.0
    return out


def paired(data, a: str, b: str) -> tuple | None:
    v = [x[a] - x[b] for x in data.values() if a in x and b in x]
    if len(v) < 2:
        return None
    mean = st.mean(v)
    half = stats.t.ppf(0.975, len(v) - 1) * st.stdev(v) / math.sqrt(len(v))
    return len(v), mean, mean - half, mean + half, sum(1 for x in v if x > 0)


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "runs")
    if not root.is_dir():
        print(f"runs directory not found: {root}", file=sys.stderr)
        return 2

    print("Paired graph mean differences in percentage points, "
          "two-sided 95 percent intervals.")
    print("Positive favours the first arm of the contrast.\n")
    print(f"  {'contrast':<58}{'popularity':>24}{'uniform':>24}")
    for a, b, label in CONTRASTS:
        cells = []
        for family in FAMILIES:
            r = paired(load(root, family), a, b)
            cells.append("no data" if r is None
                         else f"{r[1]:+7.3f} [{r[2]:+6.2f},{r[3]:+6.2f}]")
        print(f"  {label:<58}{cells[0]:>24}{cells[1]:>24}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
