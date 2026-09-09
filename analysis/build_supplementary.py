#!/usr/bin/env python3
"""Build machine readable supplementary data from the per graph summaries.

Emits one JSON per experiment containing every paired contrast quoted in the
manuscript, recomputed from the per graph CSVs, together with the per graph
differences themselves so that each reported mean, bound and count can be
re-derived without the raw archive.
"""

from __future__ import annotations

import csv
import glob
import json
import math
import sys
from collections import defaultdict
from pathlib import Path

from scipy import stats

HERE = Path(__file__).resolve().parent
# Experiment outputs live under experiments/; the runners write there.
DATA = HERE.parent / "experiments"
OUT = HERE.parent / "results" / "supplementary"
ALPHA = 0.025  # Bonferroni over the two attachment families


def utility(m: int, d: int) -> float:
    return d * min(1.0, m / (d - 1))


def band_width(m: int, tau: float) -> int:
    thr = tau * utility(m, m + 1)
    return len([d for d in range(2, 600) if utility(m, d) >= thr])


def load(pattern: str) -> dict[int, dict[str, float]]:
    """Read per graph summary rows; refuse to continue when a pattern matches
    nothing, so that a missing experiment directory cannot silently produce an
    empty JSON that overwrites a complete one."""
    out: dict[int, dict[str, float]] = defaultdict(dict)
    paths = sorted(glob.glob(pattern))
    if not paths:
        sys.exit(f"no run records match {pattern}; run the corresponding experiment first")
    for path in paths:
        if any(t in path for t in ("-probes", "-candidates", "-selection", "degrees")):
            continue
        for row in csv.DictReader(open(path)):
            out[int(row["seed"])][row["policy"]] = float(row["pHat"]) * 100.0
    return out


def paired(data: dict[int, dict[str, float]], a: str, b: str) -> dict | None:
    seeds = sorted(s for s in data if a in data[s] and b in data[s])
    diffs = [data[s][a] - data[s][b] for s in seeds]
    n = len(diffs)
    if n < 2:
        return None
    mean = sum(diffs) / n
    sd = math.sqrt(sum((d - mean) ** 2 for d in diffs) / (n - 1))
    rec = {
        "contrast": f"{a} minus {b}",
        "nGraphs": n,
        "meanPercentagePoints": mean,
        "sdPercentagePoints": sd,
        "positiveGraphs": sum(1 for d in diffs if d > 0),
        "perGraph": {str(s): round(v, 10) for s, v in zip(seeds, diffs)},
    }
    if sd == 0.0:
        rec["degenerate"] = True
        rec["note"] = "identically zero in every graph; structural identity, no test applies"
        return rec
    se = sd / math.sqrt(n)
    rec["degenerate"] = False
    rec["standardError"] = se
    rec["lowerBoundPercentagePoints"] = mean - stats.t.ppf(1 - ALPHA, n - 1) * se
    rec["oneSidedP"] = float(1.0 - stats.t.cdf(mean / se, n - 1))
    return rec


def emit(name: str, payload: dict) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / name
    path.write_text(json.dumps(payload, indent=1, sort_keys=True))
    print(f"wrote {path.relative_to(HERE.parent)}  ({path.stat().st_size:,} bytes)")


def main() -> None:
    # S3 -- fanout sweep
    s3 = {"experiment": "fanout-sweep", "boundAlpha": ALPHA,
          "note": "No confirmatory gate. Thresholds were not fixed before the "
                  "outcomes; these are estimates, not formal tests. Seeds "
                  "are the first 64 of the published confirmation ranges, so the "
                  "m=7 cells re-analyse already-examined graphs.",
          "cells": []}
    for m in (3, 5, 7, 11):
        for fam in ("popularity", "uniform"):
            d = load(f"{DATA}/sweep/m{m}/{fam}/seed-*/path-aware-m{m}-s*.csv")
            if not d:
                continue
            cell = {"fanout": m, "family": fam, "bandWidthAtTau095": band_width(m, 0.95),
                    "contrasts": []}
            for a, b in [("routeband", "bandrandom"), ("routeband", "degreeutility"),
                         ("routeband", "bandexposure"), ("routeband", "product"),
                         ("routeband", "maxdeg"), ("routeband", "sham"),
                         ("routeband", "bandgreedy"), ("bandrandom", "degreeutility")]:
                r = paired(d, a, b)
                if r:
                    cell["contrasts"].append(r)
            s3["cells"].append(cell)
    emit("Supplementary-Data-S3-fanout-sweep.json", s3)

    # S4 -- threshold identification at fixed fanout 7
    s4 = {"experiment": "threshold-identification", "fanout": 7, "boundAlpha": ALPHA,
          "note": "Varies the utility band threshold at fixed fanout so that band "
                  "width is not confounded with fanout.",
          "cells": []}
    for fam in ("popularity", "uniform"):
        d = load(f"{DATA}/tau/{fam}/seed-*/path-aware-m7-s*.csv")
        if not d:
            continue
        for tau, rb, br in ((0.975, "routeband975", "bandrandom975"),
                            (0.95, "routeband", "bandrandom"),
                            (0.90, "routeband90", "bandrandom90")):
            cell = {"family": fam, "tau": tau, "bandWidth": band_width(7, tau),
                    "contrasts": []}
            for a, b in ((rb, br), (rb, "degreeutility"), (br, "degreeutility")):
                r = paired(d, a, b)
                if r:
                    cell["contrasts"].append(r)
            s4["cells"].append(cell)
    emit("Supplementary-Data-S4-threshold-identification.json", s4)

    # S5 -- adaptive slicing
    s5 = {"experiment": "adaptive-utility-slicing", "boundAlpha": ALPHA,
          "note": "Narrow-degree regimes were produced by lowering kc, which in "
                  "this simulator also sets the degree cutoff and the relay count, "
                  "so narrow degree and high fanout relative to the degree budget "
                  "are confounded by construction.",
          "cells": []}
    for reg, kc, m in (("A", 50, 7), ("C", 16, 15), ("B", 24, 23)):
        for fam in ("popularity", "uniform"):
            d = load(f"{DATA}/daus/{reg}/{fam}/seed-*/path-aware-m{m}-s*.csv")
            if not d:
                continue
            cell = {"regime": reg, "kc": kc, "fanout": m, "family": fam,
                    "contrasts": []}
            for a, b in (("daus", "dausrandom"), ("daus", "degreeutility"),
                         ("daus", "maxdeg"), ("daus", "sham"), ("daus", "routeband")):
                r = paired(d, a, b)
                if r:
                    cell["contrasts"].append(r)
            s5["cells"].append(cell)
    emit("Supplementary-Data-S5-adaptive-slicing.json", s5)


if __name__ == "__main__":
    main()
