#!/usr/bin/env python3
"""Unit and adversarial tests for the Gnutella portability experiment."""

from __future__ import annotations

import csv
import gzip
import json
import shutil
import tempfile
import unittest
from pathlib import Path

from analyze_gnutella_portability import (
    ARMS,
    CANDIDATE_FIELDS,
    SELECTION_FIELDS,
    SUMMARY_FIELDS,
    TARGET_FIELDS,
    analyze_summaries,
    validate_holder_directory,
)
from gnutella_portability import (
    Candidate,
    ExperimentConfig,
    HolderGraph,
    ProtocolError,
    _source_for,
    _stream_seed,
    _transition_order,
    flood_probe,
    key_hex,
    load_snapshot,
    plan_snapshot,
    relay_utility,
    run_selected_holder,
    select_arms,
    sha256_file,
    write_manifest,
)


def write_fixture_graph(path: Path) -> None:
    edges = set()
    residual = list(range(1, 81))
    for node in residual:
        for offset in range(1, 5):
            neighbor = ((node - 1 + offset) % 80) + 1
            edges.add(tuple(sorted((node, neighbor))))
    for node in range(1, 61):
        edges.add((0, node))
    with gzip.open(path, "wt", encoding="utf-8", newline="") as handle:
        handle.write("# synthetic portability fixture\n")
        handle.write("1 1\n")
        for source, target in sorted(edges):
            handle.write(f"{source} {target}\n")
            if source == 1 and target == 2:
                handle.write(f"{target} {source}\n")


def read_rows(path: Path):
    with path.open("r", encoding="utf-8", newline="") as handle:
        return list(csv.DictReader(handle))


def rewrite_csv(path: Path, rows) -> None:
    with path.open("r", encoding="utf-8", newline="") as handle:
        fields = csv.DictReader(handle).fieldnames
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def refresh_holder_manifest(directory: Path) -> None:
    names = (
        "metadata.json",
        "calibration.csv",
        "candidates.csv",
        "selections.csv",
        "target_probes.csv",
        "summary.csv",
    )
    write_manifest(directory, names)


class PortabilityCoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.graph_path = self.root / "fixture.txt.gz"
        write_fixture_graph(self.graph_path)
        self.graph = load_snapshot(self.graph_path, "Fixture")
        self.config = ExperimentConfig(
            calibration_traces=12,
            calibration_visit_budget=60,
            target_probes=14,
            target_visit_budget=60,
        )
        self.plans = plan_snapshot(self.graph, self.config)
        self.plan = next(plan for plan in self.plans if plan.selected)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_preprocessing_removes_loop_duplicate_and_keeps_simple_graph(self) -> None:
        self.assertEqual(self.graph.node_count, 81)
        self.assertNotIn(1, self.graph.adjacency[1])
        self.assertEqual(len(self.graph.adjacency[1]), len(set(self.graph.adjacency[1])))
        self.assertEqual(self.graph.degree(0), 60)

    def test_holder_plan_is_outcome_free_deterministic_and_feasible(self) -> None:
        second = plan_snapshot(self.graph, self.config)
        self.assertEqual(self.plans, second)
        self.assertEqual(len(self.plans), 1)
        self.assertTrue(self.plan.feasible)
        self.assertTrue(self.plan.selected)
        self.assertEqual(len(self.plan.incumbents), 50)
        self.assertEqual(len(self.plan.roots), 16)
        self.assertGreaterEqual(len(self.plan.candidates), 50)
        self.assertGreaterEqual(len(self.plan.band_candidates), 50)

    def test_relay_utility_has_expected_fanout_band(self) -> None:
        maximum = relay_utility(7, 8)
        in_band = [degree for degree in range(4, 51) if relay_utility(7, degree) >= 0.95 * maximum]
        self.assertEqual(in_band, [8, 9, 10, 11, 12])

    def test_source_and_stream_coordinates_are_repeatable(self) -> None:
        view = HolderGraph(self.graph, self.plan.holder_id)
        self.assertEqual(_source_for(view, "target", 3), _source_for(view, "target", 3))
        self.assertEqual(_stream_seed(view, "target", 3), _stream_seed(view, "target", 3))
        self.assertNotEqual(_stream_seed(view, "target", 3), _stream_seed(view, "target", 4))

    def test_transition_order_preserves_common_edge_ranking(self) -> None:
        common = [3, 5, 7, 9, 11]
        first = _transition_order(1234, 20, 19, common, 20)
        second = _transition_order(1234, 20, 19, common + [13, 15], 20)
        restricted = [node for node in second if node in common]
        self.assertEqual(first, restricted)

    def test_target_probe_detects_holder_without_exceeding_budget(self) -> None:
        view = HolderGraph(self.graph, self.plan.holder_id)
        selected = self.plan.incumbents
        source = selected[0]
        result = flood_probe(view, selected, source, 100, 60, 9, True)
        self.assertTrue(result.success)
        self.assertLessEqual(result.unique_visited, 60)

    def test_ranked_arms_have_exact_contract(self) -> None:
        view = HolderGraph(self.graph, self.plan.holder_id)
        candidates = []
        maximum = relay_utility(self.config.fanout, self.config.fanout + 1)
        for index, node in enumerate(self.plan.candidates):
            base = view.residual_degree(node)
            utility = relay_utility(self.config.fanout, base + 1)
            exposure = (index % 12) / 12.0
            forwarding = min(1.0, self.config.fanout / float(base))
            candidates.append(
                Candidate(
                    node,
                    base,
                    base + 1,
                    [],
                    exposure,
                    forwarding,
                    exposure * forwarding,
                    utility,
                    utility >= self.config.utility_band_threshold * maximum,
                    node in self.plan.incumbents,
                    node in self.plan.roots,
                    key_hex(self.graph.snapshot, self.plan.holder_id, node, "candidate"),
                )
            )
        arms = select_arms(self.graph, self.plan, candidates, self.config)
        self.assertEqual(tuple(arms), ARMS)
        for policy in ARMS:
            self.assertEqual(len(arms[policy]), 50)
            self.assertEqual(len(set(arms[policy])), 50)

    def test_complete_holder_run_is_byte_deterministic(self) -> None:
        first = self.root / "first"
        second = self.root / "second"
        summaries_a, digest_a = run_selected_holder(self.graph, self.plan, first, self.config)
        summaries_b, digest_b = run_selected_holder(self.graph, self.plan, second, self.config)
        self.assertEqual(summaries_a, summaries_b)
        self.assertEqual(digest_a, digest_b)
        for name in (
            "metadata.json",
            "calibration.csv",
            "candidates.csv",
            "selections.csv",
            "target_probes.csv",
            "summary.csv",
            "MANIFEST.sha256",
        ):
            self.assertEqual(sha256_file(first / name), sha256_file(second / name))


class FailClosedHolderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.temporary = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temporary.name)
        cls.graph_path = cls.root / "fixture.txt.gz"
        write_fixture_graph(cls.graph_path)
        cls.graph = load_snapshot(cls.graph_path, "Fixture")
        cls.config = ExperimentConfig(
            calibration_traces=12,
            calibration_visit_budget=60,
            target_probes=14,
            target_visit_budget=60,
        )
        cls.plan = next(plan for plan in plan_snapshot(cls.graph, cls.config) if plan.selected)
        cls.clean = cls.root / "clean"
        run_selected_holder(cls.graph, cls.plan, cls.clean, cls.config)
        validate_holder_directory(cls.clean, cls.graph, cls.plan, cls.config)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.temporary.cleanup()

    def corrupt(self, name: str, filename: str, mutator, refresh: bool = True) -> Path:
        target = self.root / name
        shutil.copytree(self.clean, target)
        path = target / filename
        rows = read_rows(path)
        mutator(rows)
        rewrite_csv(path, rows)
        if refresh:
            refresh_holder_manifest(target)
        return target

    def assert_rejected(self, target: Path) -> None:
        with self.assertRaises(ProtocolError):
            validate_holder_directory(target, self.graph, self.plan, self.config)

    def test_manifest_detects_unacknowledged_byte_change(self) -> None:
        target = self.corrupt(
            "bad-hash",
            "summary.csv",
            lambda rows: rows[0].__setitem__("pHat", "0"),
            refresh=False,
        )
        self.assert_rejected(target)

    def test_rejects_nonbinary_success_after_manifest_refresh(self) -> None:
        target = self.corrupt(
            "bad-success",
            "target_probes.csv",
            lambda rows: rows[0].__setitem__("success", "2"),
        )
        self.assert_rejected(target)

    def test_rejects_missing_target_probe(self) -> None:
        target = self.corrupt("missing-probe", "target_probes.csv", lambda rows: rows.pop())
        self.assert_rejected(target)

    def test_rejects_wrong_shared_source(self) -> None:
        target = self.corrupt(
            "bad-source",
            "target_probes.csv",
            lambda rows: rows[0].__setitem__("sourceId", str(self.plan.holder_id)),
        )
        self.assert_rejected(target)

    def test_rejects_wrong_stream_coordinate(self) -> None:
        target = self.corrupt(
            "bad-stream",
            "target_probes.csv",
            lambda rows: rows[0].__setitem__("streamSeedHex", "0000000000000000"),
        )
        self.assert_rejected(target)

    def test_rejects_visit_budget_violation(self) -> None:
        target = self.corrupt(
            "bad-budget",
            "target_probes.csv",
            lambda rows: rows[0].__setitem__("uniqueVisited", "61"),
        )
        self.assert_rejected(target)

    def test_rejects_summary_p_hat_mismatch(self) -> None:
        target = self.corrupt(
            "bad-phat",
            "summary.csv",
            lambda rows: rows[0].__setitem__("pHat", "0.123"),
        )
        self.assert_rejected(target)

    def test_rejects_candidate_product_mismatch(self) -> None:
        target = self.corrupt(
            "bad-product",
            "candidates.csv",
            lambda rows: rows[0].__setitem__("productScore", "0.987"),
        )
        self.assert_rejected(target)

    def test_rejects_candidate_tie_key_mismatch(self) -> None:
        target = self.corrupt(
            "bad-tie",
            "candidates.csv",
            lambda rows: rows[0].__setitem__("candidateTieKey", "0" * 64),
        )
        self.assert_rejected(target)

    def test_rejects_selection_order_change(self) -> None:
        def swap(rows):
            rows[50], rows[51] = rows[51], rows[50]

        target = self.corrupt("bad-selection-order", "selections.csv", swap)
        self.assert_rejected(target)

    def test_rejects_duplicate_selected_node(self) -> None:
        target = self.corrupt(
            "duplicate-selection",
            "selections.csv",
            lambda rows: rows[51].__setitem__("nodeId", rows[50]["nodeId"]),
        )
        self.assert_rejected(target)

    def test_rejects_calibration_index_gap(self) -> None:
        target = self.corrupt(
            "calibration-gap",
            "calibration.csv",
            lambda rows: rows[1].__setitem__("traceIndex", "7"),
        )
        self.assert_rejected(target)

    def test_rejects_extra_file(self) -> None:
        target = self.root / "extra-file"
        shutil.copytree(self.clean, target)
        (target / "unexpected.txt").write_text("unexpected\n", encoding="utf-8")
        self.assert_rejected(target)

    def test_rejects_symlink(self) -> None:
        target = self.root / "symlink"
        shutil.copytree(self.clean, target)
        (target / "metadata.json").unlink()
        (target / "metadata.json").symlink_to(self.clean / "metadata.json")
        self.assert_rejected(target)


class DescriptiveAnalysisTests(unittest.TestCase):
    def test_snapshot_clustered_analysis_uses_unweighted_snapshot_means(self) -> None:
        rows = []
        for snapshot_index, snapshot in enumerate(("Gnutella04", "Gnutella05", "Gnutella06")):
            for holder in range(snapshot_index + 1):
                for policy in ARMS:
                    base = 0.2 + 0.01 * snapshot_index
                    value = base + (0.05 if policy == "routeband" else 0.0)
                    rows.append(
                        {
                            "snapshot": snapshot,
                            "holderId": str(holder),
                            "policy": policy,
                            "pHat": str(value),
                        }
                    )
        snapshot_rows, comparisons, analysis = analyze_summaries(rows)
        self.assertEqual(analysis["snapshotCount"], 3)
        self.assertFalse(analysis["pValuesComputed"])
        self.assertFalse(analysis["primaryGate"])
        self.assertEqual(len(snapshot_rows), 3 * len(ARMS))
        for comparison in comparisons:
            self.assertAlmostEqual(comparison["meanDifference"], 0.05)
            self.assertEqual(comparison["snapshotWins"], 3)


if __name__ == "__main__":
    unittest.main(verbosity=2)

