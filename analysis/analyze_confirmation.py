#!/usr/bin/env python3
"""Evaluate the confirmation of RouteBand minus BANDRANDOM on fresh seeds.

The decision rule implemented here was fixed before the graphs were generated.
The primary gate is fanout 7 in both attachment families; fanouts 5 and 11 are
secondary cells and do not affect the primary decision.

Usage:
    python3 analysis/analyze_confirmation.py <runs-directory>

The runs directory is expected to contain m<fanout>/<family>/seed-<n>/ trees of
per graph summary CSVs, which is what experiments/run_confirmation.sh writes.
"""

from __future__ import annotations

import csv
import glob
import math
import sys
from collections import defaultdict
from pathlib import Path

from scipy import stats

ALPHA = 0.025          # per family; Bonferroni over the two families
PRIMARY_FANOUT = 7
SECONDARY_FANOUTS = (5, 11)
FAMILIES = ("popularity", "uniform")


EXPECTED_N = {PRIMARY_FANOUT: 128, 5: 64, 11: 64}   # graphs per family per cell


class EvidenceError(RuntimeError):
    """Raised when a cell is not exactly the prespecified evidence."""


def load(root: Path, fanout: int, family: str) -> dict[int, dict[str, float]]:
    """Read the per graph summary rows of one cell and refuse anything that is
    not exactly the prespecified evidence: a duplicated seed/policy record, a
    nonfinite discovery rate, a graph missing either arm of the primary
    contrast, or a graph count other than the protocol's."""
    out: dict[int, dict[str, float]] = defaultdict(dict)
    pattern = f"{root}/m{fanout}/{family}/seed-*/path-aware-m{fanout}-s*.csv"
    for path in sorted(glob.glob(pattern)):
        name = Path(path).name
        if any(tag in name for tag in ("-candidates", "-selection", "degrees")):
            continue
        for row in csv.DictReader(open(path)):
            seed, policy = int(row["seed"]), row["policy"]
            value = float(row["pHat"]) * 100.0
            if not math.isfinite(value):
                raise EvidenceError(f"nonfinite pHat: {path} seed={seed} {policy}")
            if policy in out[seed]:
                raise EvidenceError(f"duplicate record: seed={seed} {policy} in {path}")
            out[seed][policy] = value
    missing = [s for s, v in out.items() if "routeband" not in v or "bandrandom" not in v]
    if missing:
        raise EvidenceError(f"m={fanout} {family}: graphs missing an arm: {sorted(missing)}")
    expected = EXPECTED_N.get(fanout)
    if expected is not None and len(out) != expected:
        raise EvidenceError(f"m={fanout} {family}: {len(out)} graphs, protocol expects {expected}")
    return out


def contrast(data, a: str = "routeband", b: str = "bandrandom") -> dict | None:
    diffs = [v[a] - v[b] for v in data.values() if a in v and b in v]
    n = len(diffs)
    if n < 2:
        return None
    mean = sum(diffs) / n
    sd = math.sqrt(sum((d - mean) ** 2 for d in diffs) / (n - 1))
    se = sd / math.sqrt(n)
    return {
        "n": n,
        "mean": mean,
        "sd": sd,
        "lower_bound": mean - stats.t.ppf(1 - ALPHA, n - 1) * se,
        "p": float(1 - stats.t.cdf(mean / se, n - 1)),
        "positive": sum(1 for d in diffs if d > 0),
    }


def trend(root: Path, family: str) -> dict | None:
    low = load(root, 5, family)
    high = load(root, 11, family)
    seeds = sorted(set(low) & set(high))
    inc = [
        (high[s]["routeband"] - high[s]["bandrandom"])
        - (low[s]["routeband"] - low[s]["bandrandom"])
        for s in seeds
    ]
    if len(inc) < 2:
        return None
    mean = sum(inc) / len(inc)
    sd = math.sqrt(sum((d - mean) ** 2 for d in inc) / (len(inc) - 1))
    se = sd / math.sqrt(len(inc))
    t = mean / se
    return {"n": len(inc), "mean": mean, "t": t,
            "p": float(2 * (1 - stats.t.cdf(abs(t), len(inc) - 1)))}


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "runs")
    if not root.is_dir():
        print(f"runs directory not found: {root}", file=sys.stderr)
        return 2
    # Validate every cell before printing anything, so that a malformed or
    # incomplete evidence set produces no gate result at all.
    try:
        for fanout in (PRIMARY_FANOUT, *SECONDARY_FANOUTS):
            for family in FAMILIES:
                load(root, fanout, family)
    except EvidenceError as err:
        print(f"evidence check failed: {err}", file=sys.stderr)
        return 3

    print("PRIMARY GATE -- fanout 7, RouteBand minus BANDRANDOM")
    print("  rule: mean > 0 and one-sided lower bound > 0, alpha = 0.025 per family")
    print(f"  {'family':<12}{'n':>5}{'mean':>9}{'sd':>8}{'LCB':>9}{'positive':>11}{'p':>11}")
    passed = True
    for family in FAMILIES:
        r = contrast(load(root, PRIMARY_FANOUT, family))
        if r is None:
            print(f"  {family:<12}  no data")
            passed = False
            continue
        ok = r["mean"] > 0 and r["lower_bound"] > 0
        passed &= ok
        print(f"  {family:<12}{r['n']:>5}{r['mean']:>+9.3f}{r['sd']:>8.3f}"
              f"{r['lower_bound']:>+9.3f}{r['positive']:>7}/{r['n']:<3}{r['p']:>11.2e}"
              f"  {'PASS' if ok else 'FAIL'}")
    print(f"\n  PRIMARY GATE: {'PASS' if passed else 'FAIL'}\n")

    print("SECONDARY (prespecified; does not affect the primary decision)")
    for fanout in SECONDARY_FANOUTS:
        for family in FAMILIES:
            r = contrast(load(root, fanout, family))
            if r is None:
                continue
            ok = r["mean"] > 0 and r["lower_bound"] > 0
            print(f"  m={fanout:<3}{family:<12}{r['n']:>5}{r['mean']:>+9.3f}"
                  f"{r['lower_bound']:>+9.3f}{r['positive']:>7}/{r['n']:<3}"
                  f"  {'pass' if ok else 'fail'}")

    print("\nTREND (paired increment, fanout 11 minus fanout 5)")
    for family in FAMILIES:
        r = trend(root, family)
        if r is None:
            continue
        print(f"  {family:<12}n={r['n']:<5}increment={r['mean']:>+8.3f}"
              f"  t={r['t']:>6.2f}  p={r['p']:.2e}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
