#!/usr/bin/env python3
"""Analyze the fanout sweep.

Reports, for each fanout m = 3, 5, 7, 11:

  * RouteBand minus BANDRANDOM, a uniform draw from the same utility band,
    which is the uninformed control and the primary contrast
  * RouteBand minus DEGREEUTILITY, the analytical rule that costs nothing to
    run, with MAXDEG kept as a reference point

The graph seed remains the inferential unit; every contrast is a paired
difference over the same seed under the same fanout.
"""

from __future__ import annotations

import csv
import math
from collections import defaultdict
import sys
from pathlib import Path

from scipy import stats

HERE = Path(__file__).resolve().parent
# The runners write under experiments/; pass another directory as the first
# argument to analyze a sweep stored elsewhere.
SWEEP = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parent / "experiments" / "sweep"
FANOUTS = [3, 5, 7, 11]
FAMILIES = ["popularity", "uniform"]

# Contrasts reported per fanout and family. The first entry is the new primary.
CONTRASTS = [
    ("routeband", "bandrandom", "exposure value inside the band"),
    ("routeband", "degreeutility", "value over the free closed-form rule"),
    ("routeband", "bandexposure", "forwarding factor inside the band"),
    ("routeband", "product", "band, given the product score"),
    ("routeband", "maxdeg", "legacy comparator"),
    ("routeband", "sham", "unchanged incumbents"),
    ("bandrandom", "degreeutility", "band alone, no exposure"),
]


def utility(m: int, d: int) -> float:
    return d * min(1.0, m / (d - 1))


def band_degrees(m: int, tau: float = 0.95) -> list[int]:
    peak = utility(m, m + 1)
    return [d for d in range(2, 400) if utility(m, d) >= tau * peak]


def load(fanout: int, family: str) -> dict[int, dict[str, float]]:
    """seed -> {policy: target discovery percent}"""
    out: dict[int, dict[str, float]] = defaultdict(dict)
    root = SWEEP / f"m{fanout}" / family
    if not root.is_dir():
        return {}
    for path in sorted(root.glob(f"seed-*/path-aware-m{fanout}-s*.csv")):
        if any(tag in path.name for tag in ("-probes", "-candidates", "-selection", "degrees")):
            continue
        for row in csv.DictReader(path.open()):
            out[int(row["seed"])][row["policy"]] = float(row["pHat"]) * 100.0
    return out


def paired(data: dict[int, dict[str, float]], a: str, b: str,
           alpha: float) -> dict | None:
    diffs = [v[a] - v[b] for v in data.values() if a in v and b in v]
    n = len(diffs)
    if n < 2:
        return None
    mean = sum(diffs) / n
    sd = math.sqrt(sum((d - mean) ** 2 for d in diffs) / (n - 1))
    positive = sum(1 for d in diffs if d > 0)
    # A contrast that is identically zero in every graph is a structural
    # identity, not a measured effect. Reporting p = 0 for it would be an
    # artifact of dividing by a zero standard error, so flag it instead.
    if sd == 0.0:
        return {"n": n, "mean": mean, "sd": 0.0, "lcb": mean, "p": None,
                "positive": positive, "degenerate": True}
    se = sd / math.sqrt(n)
    # One-sided lower confidence bound in the lower tail, Bonferroni-adjusted
    # across the two attachment families.
    crit = stats.t.ppf(1 - alpha, n - 1)
    lcb = mean - crit * se
    tstat = mean / se
    pval = 1.0 - stats.t.cdf(tstat, n - 1)
    return {"n": n, "mean": mean, "sd": sd, "lcb": lcb, "p": pval,
            "positive": positive, "degenerate": False}


def main() -> None:
    alpha = 0.025  # Bonferroni over the two families, 95 percent simultaneous

    print("Band structure (tau = 0.95, prospective relay degrees after connection)")
    print(f"{'m':>3}  {'peak':>4}  {'band':>12}  {'width':>5}")
    for m in FANOUTS + [15]:
        b = band_degrees(m)
        print(f"{m:>3}  {m+1:>4}  {str(b[0])+'..'+str(b[-1]):>12}  {len(b):>5}")
    print()

    loaded = {(m, f): load(m, f) for m in FANOUTS for f in FAMILIES}
    print("Completed graphs per cell")
    for m in FANOUTS:
        counts = "  ".join(f"{f}={len(loaded[(m, f)])}" for f in FAMILIES)
        print(f"  m={m:<3} {counts}")
    print()

    for label_a, label_b, why in CONTRASTS:
        print(f"=== {label_a} minus {label_b}  ({why}) ===")
        print(f"{'m':>3}  {'family':<12} {'n':>4} {'mean':>8} {'sd':>7} "
              f"{'LCB':>8} {'pos/n':>8}  {'p':>10}")
        for m in FANOUTS:
            for fam in FAMILIES:
                r = paired(loaded[(m, fam)], label_a, label_b, alpha)
                if r is None:
                    print(f"{m:>3}  {fam:<12} {'--':>4} {'(no data)':>8}")
                    continue
                tail = ("  identically zero in all graphs (no test)"
                        if r["degenerate"] else f"{r['p']:>10.2e}")
                print(f"{m:>3}  {fam:<12} {r['n']:>4} {r['mean']:>+8.3f} "
                      f"{r['sd']:>7.3f} {r['lcb']:>+8.3f} "
                      f"{r['positive']:>4}/{r['n']:<3} {tail}")
        print()

    # Mean selected relay degree per policy, to expose which arms are really
    # just degree rules in disguise.
    print("=== mean selected relay degree by policy ===")
    for m in FANOUTS:
        for fam in FAMILIES:
            root = SWEEP / f"m{m}" / fam
            degs: dict[str, list[float]] = defaultdict(list)
            for path in sorted(root.glob(f"seed-*/path-aware-m{m}-s*.csv")):
                if any(t in path.name for t in ("-probes", "-candidates",
                                                "-selection", "degrees")):
                    continue
                for row in csv.DictReader(path.open()):
                    degs[row["policy"]].append(float(row["meanRelayDegree"]))
            if not degs:
                continue
            shown = ["routeband", "bandrandom", "bandexposure", "degreeutility",
                     "product", "maxdeg", "exposure"]
            cells = "  ".join(
                f"{p}={sum(degs[p])/len(degs[p]):.2f}" for p in shown if degs.get(p))
            print(f"  m={m:<3} {fam:<12} {cells}")
    print()


if __name__ == "__main__":
    main()
