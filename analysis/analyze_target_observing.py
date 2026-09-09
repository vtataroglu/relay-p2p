#!/usr/bin/env python3
"""Compare target-free calibration against calibration that observes the item.

RouteBand collects calibration traces while the content holder is isolated, so
no trace can contain the item being sought. This script evaluates two arms that
share RouteBand's candidate pool, utility band, forwarding factor, relay count,
tie rule and query streams, and differ only in that their exposure estimate was
allowed to observe the target:

    targetband      observed exposure times the forwarding factor
    targetexposure  observed exposure alone

BANDRANDOM, a uniform draw from the same band, is included as the
uninformed reference: an estimate that cannot beat it carries no usable
signal.

Usage:
    python3 analysis/analyze_target_observing.py <runs-directory>
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
    ("routeband", "targetband",
     "RouteBand - TargetBand (product rule)"),
    ("bandexposure", "targetexposure",
     "BandExposure - TargetExposure (exposure only)"),
    ("targetband", "bandrandom",
     "TargetBand - BandRandom (beats no information?)"),
    ("targetexposure", "bandrandom",
     "TargetExposure - BandRandom (beats no information?)"),
    ("routeband", "bandrandom",
     "RouteBand - BandRandom (reference)"),
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
    print("Positive means the arm that did not observe the target wins.\n")
    print(f"  {'contrast':<48}{'popularity':>24}{'uniform':>24}")
    for a, b, label in CONTRASTS:
        cells = []
        for family in FAMILIES:
            r = paired(load(root, family), a, b)
            cells.append("no data" if r is None
                         else f"{r[1]:+7.3f} [{r[2]:+6.2f},{r[3]:+6.2f}]")
        print(f"  {label:<48}{cells[0]:>24}{cells[1]:>24}")

    print("\nAbsolute discovery means (percent)")
    for family in FAMILIES:
        d = load(root, family)
        arms = ["routeband", "targetband", "targetexposure",
                "bandexposure", "bandrandom", "degreeutility"]
        cells = "  ".join(
            f"{a}={st.mean(x[a] for x in d.values() if a in x):.2f}"
            for a in arms if any(a in x for x in d.values()))
        print(f"  {family:<12}{cells}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
