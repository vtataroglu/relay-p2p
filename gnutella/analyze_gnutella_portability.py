#!/usr/bin/env python3
"""Fail closed analyzer for the Gnutella portability experiment."""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import statistics
import sys
from collections import defaultdict
from pathlib import Path
from typing import Dict, Iterable, List, Mapping, Optional, Sequence, Tuple

from gnutella_portability import (
    ARMS,
    CALIBRATION_TRACES,
    CALIBRATION_VISIT_BUDGET,
    FANOUT,
    HOLDER_DEGREE,
    IMPLEMENTATION_VERSION,
    PLAN_FIELDS,
    ROOT_COUNT,
    SCHEMA_VERSION,
    SNAPSHOT_FILES,
    SNAPSHOT_ORDER,
    SUMMARY_FIELDS,
    TARGET_PROBES,
    TARGET_VISIT_BUDGET,
    UTILITY_BAND_THRESHOLD,
    Candidate,
    ExperimentConfig,
    HolderGraph,
    ProtocolError,
    _source_for,
    _stream_seed,
    calibrate_candidates,
    float_text,
    key_hex,
    key_u64,
    load_snapshot,
    plan_snapshot,
    relay_utility,
    run_target_probes,
    sha256_file,
    write_csv,
    write_json,
)


BASE_DIRECTORY = Path(__file__).resolve().parent
T_CRITICAL_975 = {
    1: 12.7062047364,
    2: 4.30265272975,
    3: 3.18244630528,
    4: 2.77644510520,
    5: 2.57058183564,
    6: 2.44691184879,
    7: 2.36462425101,
    8: 2.30600413503,
}


def _assert_close(actual: float, expected: float, label: str, tolerance: float = 1e-12) -> None:
    if not math.isfinite(actual) or not math.isfinite(expected):
        raise ProtocolError(f"Nonfinite numeric value in {label}.")
    if abs(actual - expected) > tolerance * max(1.0, abs(actual), abs(expected)):
        raise ProtocolError(f"Numeric mismatch in {label}: expected {expected}, found {actual}.")


def _int(value: str, label: str) -> int:
    try:
        parsed = int(value)
    except ValueError as error:
        raise ProtocolError(f"Invalid integer in {label}: {value!r}.") from error
    return parsed


def _float(value: str, label: str) -> float:
    try:
        parsed = float(value)
    except ValueError as error:
        raise ProtocolError(f"Invalid float in {label}: {value!r}.") from error
    if not math.isfinite(parsed):
        raise ProtocolError(f"Nonfinite float in {label}.")
    return parsed


def read_csv_exact(path: Path, fields: Sequence[str]) -> List[Dict[str, str]]:
    if not path.is_file() or path.is_symlink():
        raise ProtocolError(f"Required CSV is missing, nonregular, or a symlink: {path}")
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames != list(fields):
            raise ProtocolError(
                f"CSV schema mismatch for {path}: expected {list(fields)}, found {reader.fieldnames}."
            )
        rows = list(reader)
    if any(None in row for row in rows):
        raise ProtocolError(f"CSV contains excess columns: {path}")
    return rows


def read_json_object(path: Path) -> Dict[str, object]:
    if not path.is_file() or path.is_symlink():
        raise ProtocolError(f"Required JSON is missing, nonregular, or a symlink: {path}")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise ProtocolError(f"Invalid JSON file {path}: {error}") from error
    if not isinstance(value, dict):
        raise ProtocolError(f"Expected a JSON object in {path}.")
    return value


def verify_manifest(
    directory: Path,
    manifest_name: str,
    expected_entries: Optional[Iterable[str]] = None,
) -> List[str]:
    manifest_path = directory / manifest_name
    if not manifest_path.is_file() or manifest_path.is_symlink():
        raise ProtocolError(f"Missing regular manifest: {manifest_path}")
    entries: List[str] = []
    seen = set()
    for line_number, line in enumerate(manifest_path.read_text(encoding="ascii").splitlines(), start=1):
        fields = line.split("  ", 1)
        if len(fields) != 2 or len(fields[0]) != 64:
            raise ProtocolError(f"Malformed manifest line {manifest_path}:{line_number}.")
        digest, relative = fields
        relative_path = Path(relative)
        if relative_path.is_absolute() or ".." in relative_path.parts:
            raise ProtocolError(f"Unsafe manifest path: {relative}")
        if relative in seen:
            raise ProtocolError(f"Duplicate manifest path: {relative}")
        seen.add(relative)
        target = directory / relative_path
        if not target.is_file() or target.is_symlink():
            raise ProtocolError(f"Manifest target is missing, nonregular, or a symlink: {target}")
        actual = sha256_file(target)
        if actual != digest:
            raise ProtocolError(
                f"Manifest hash mismatch for {target}: expected {digest}, found {actual}."
            )
        entries.append(relative)
    if entries != sorted(entries):
        raise ProtocolError(f"Manifest entries are not in canonical sorted order: {manifest_path}")
    if expected_entries is not None and set(entries) != set(expected_entries):
        raise ProtocolError(
            f"Manifest membership mismatch in {manifest_path}; "
            f"missing={sorted(set(expected_entries) - set(entries))}, "
            f"extra={sorted(set(entries) - set(expected_entries))}."
        )
    return entries


CALIBRATION_FIELDS = (
    "traceIndex",
    "sourceId",
    "streamSeedHex",
    "uniqueVisited",
    "messages",
    "expandedCount",
)
CANDIDATE_FIELDS = (
    "nodeId",
    "baseDegree",
    "postDegree",
    "traceIndices",
    "exposureCount",
    "exposure",
    "forwardingFactor",
    "productScore",
    "degreeUtility",
    "inUtilityBand",
    "isIncumbent",
    "isRoot",
    "candidateTieKey",
)
SELECTION_FIELDS = (
    "policy",
    "rank",
    "nodeId",
    "baseDegree",
    "postDegree",
    "exposure",
    "forwardingFactor",
    "productScore",
    "degreeUtility",
    "inUtilityBand",
    "candidateTieKey",
)
TARGET_FIELDS = (
    "policy",
    "probeIndex",
    "sourceId",
    "streamSeedHex",
    "success",
    "uniqueVisited",
    "messages",
)


def validate_holder_directory(
    directory: Path,
    graph,
    plan,
    config: ExperimentConfig,
) -> List[Dict[str, str]]:
    expected_names = {
        "metadata.json",
        "calibration.csv",
        "candidates.csv",
        "selections.csv",
        "target_probes.csv",
        "summary.csv",
        "MANIFEST.sha256",
    }
    actual_names = {path.name for path in directory.iterdir()}
    if actual_names != expected_names:
        raise ProtocolError(
            f"Unexpected holder directory membership in {directory}; "
            f"missing={sorted(expected_names - actual_names)}, extra={sorted(actual_names - expected_names)}."
        )
    if any(path.is_symlink() or not path.is_file() for path in directory.iterdir()):
        raise ProtocolError(f"Holder directory contains a symlink or nonregular entry: {directory}")
    verify_manifest(directory, "MANIFEST.sha256", expected_names - {"MANIFEST.sha256"})

    metadata = read_json_object(directory / "metadata.json")
    required_metadata = {
        "schemaVersion",
        "implementationVersion",
        "snapshot",
        "holderId",
        "holderKey",
        "observedHolderDegree",
        "incumbents",
        "roots",
        "candidateCount",
        "utilityBandCount",
        "candidateAdjacencyChecks",
        "residualComponentCount",
        "residualLargestComponent",
        "graphNodeCount",
        "graphEdgeCount",
        "graphHash",
        "config",
        "randomness",
        "targetReplicaCount",
        "targetLocation",
    }
    if set(metadata) != required_metadata:
        raise ProtocolError(f"Metadata field mismatch in {directory}.")
    expected_metadata = {
        "schemaVersion": SCHEMA_VERSION,
        "implementationVersion": IMPLEMENTATION_VERSION,
        "snapshot": graph.snapshot,
        "holderId": plan.holder_id,
        "holderKey": plan.holder_key,
        "observedHolderDegree": plan.observed_degree,
        "incumbents": list(plan.incumbents),
        "roots": list(plan.roots),
        "candidateCount": len(plan.candidates),
        "utilityBandCount": len(plan.band_candidates),
        "candidateAdjacencyChecks": plan.candidate_adjacency_checks,
        "residualComponentCount": plan.residual_component_count,
        "residualLargestComponent": plan.residual_largest_component,
        "graphNodeCount": graph.node_count,
        "graphEdgeCount": graph.edge_count,
        "graphHash": graph.graph_hash,
        "config": config.as_dict(),
        "randomness": "shared counter based transition ranking using SHA256 seeds and SplitMix64",
        "targetReplicaCount": 1,
        "targetLocation": plan.holder_id,
    }
    if metadata != expected_metadata:
        raise ProtocolError(f"Metadata values differ from the stored graph and plan in {directory}.")

    view = HolderGraph(graph, plan.holder_id)
    calibration_rows = read_csv_exact(directory / "calibration.csv", CALIBRATION_FIELDS)
    if len(calibration_rows) != config.calibration_traces:
        raise ProtocolError(f"Calibration row count mismatch in {directory}.")
    for expected_index, row in enumerate(calibration_rows):
        index = _int(row["traceIndex"], "calibration traceIndex")
        if index != expected_index:
            raise ProtocolError(f"Calibration indices are not complete and ordered in {directory}.")
        expected_source = _source_for(view, "calibration", index)
        expected_seed = _stream_seed(view, "calibration", index)
        if _int(row["sourceId"], "calibration sourceId") != expected_source:
            raise ProtocolError(f"Calibration source mismatch in {directory} at trace {index}.")
        if row["streamSeedHex"] != f"{expected_seed:016x}":
            raise ProtocolError(f"Calibration stream mismatch in {directory} at trace {index}.")
        visited = _int(row["uniqueVisited"], "calibration uniqueVisited")
        messages = _int(row["messages"], "calibration messages")
        expanded = _int(row["expandedCount"], "calibration expandedCount")
        if not (1 <= visited <= config.calibration_visit_budget):
            raise ProtocolError(f"Calibration visit budget violation in {directory}.")
        if messages < 0 or not (0 <= expanded <= visited - 1):
            raise ProtocolError(f"Invalid calibration counts in {directory}.")

    candidate_rows = read_csv_exact(directory / "candidates.csv", CANDIDATE_FIELDS)
    if len(candidate_rows) != len(plan.candidates):
        raise ProtocolError(f"Candidate row count mismatch in {directory}.")
    candidate_objects: Dict[int, Candidate] = {}
    previous_node = -1
    maximum_utility = relay_utility(config.fanout, config.fanout + 1)
    for row in candidate_rows:
        node = _int(row["nodeId"], "candidate nodeId")
        if node <= previous_node or node in candidate_objects:
            raise ProtocolError(f"Candidate identifiers are not unique and sorted in {directory}.")
        previous_node = node
        if node not in plan.candidates:
            raise ProtocolError(f"Unplanned candidate {node} in {directory}.")
        base_degree = _int(row["baseDegree"], "candidate baseDegree")
        post_degree = _int(row["postDegree"], "candidate postDegree")
        if base_degree != view.residual_degree(node) or post_degree != base_degree + 1:
            raise ProtocolError(f"Candidate degree mismatch for node {node} in {directory}.")
        raw_indices = row["traceIndices"]
        indices = [] if raw_indices == "" else [_int(value, "candidate trace index") for value in raw_indices.split("|")]
        if indices != sorted(set(indices)) or any(index < 0 or index >= config.calibration_traces for index in indices):
            raise ProtocolError(f"Invalid trace membership for node {node} in {directory}.")
        exposure_count = _int(row["exposureCount"], "candidate exposureCount")
        if exposure_count != len(indices):
            raise ProtocolError(f"Exposure count mismatch for node {node} in {directory}.")
        exposure = _float(row["exposure"], "candidate exposure")
        forwarding = _float(row["forwardingFactor"], "candidate forwardingFactor")
        product = _float(row["productScore"], "candidate productScore")
        utility = _float(row["degreeUtility"], "candidate degreeUtility")
        expected_exposure = len(indices) / float(config.calibration_traces)
        expected_forwarding = min(1.0, config.fanout / float(base_degree))
        expected_utility = relay_utility(config.fanout, post_degree)
        _assert_close(exposure, expected_exposure, "candidate exposure")
        _assert_close(forwarding, expected_forwarding, "candidate forwarding factor")
        _assert_close(product, exposure * forwarding, "candidate product score")
        _assert_close(utility, expected_utility, "candidate degree utility")
        expected_band = utility >= config.utility_band_threshold * maximum_utility
        in_band = _int(row["inUtilityBand"], "candidate inUtilityBand")
        if in_band not in (0, 1) or bool(in_band) != expected_band:
            raise ProtocolError(f"Utility band flag mismatch for node {node} in {directory}.")
        is_incumbent = _int(row["isIncumbent"], "candidate isIncumbent")
        is_root = _int(row["isRoot"], "candidate isRoot")
        if is_incumbent not in (0, 1) or bool(is_incumbent) != (node in plan.incumbents):
            raise ProtocolError(f"Incumbent flag mismatch for node {node} in {directory}.")
        if is_root not in (0, 1) or bool(is_root) != (node in plan.roots):
            raise ProtocolError(f"Root flag mismatch for node {node} in {directory}.")
        expected_key = key_hex(graph.snapshot, plan.holder_id, node, "candidate")
        if row["candidateTieKey"] != expected_key:
            raise ProtocolError(f"Candidate tie key mismatch for node {node} in {directory}.")
        candidate_objects[node] = Candidate(
            node,
            base_degree,
            post_degree,
            indices,
            exposure,
            forwarding,
            product,
            utility,
            bool(in_band),
            bool(is_incumbent),
            bool(is_root),
            expected_key,
        )
    if set(candidate_objects) != set(plan.candidates):
        raise ProtocolError(f"Candidate membership mismatch in {directory}.")

    # Reexecute every target free calibration trace from the stored graph.
    # The stored exposure ledger is therefore not trusted merely because its
    # arithmetic and manifest are internally consistent.
    replay_candidates, replay_calibration = calibrate_candidates(graph, plan, config)
    replay_candidate_by_node = {candidate.node_id: candidate for candidate in replay_candidates}
    for actual_row, replay_row in zip(calibration_rows, replay_calibration):
        for field in ("traceIndex", "sourceId", "uniqueVisited", "messages", "expandedCount"):
            if _int(actual_row[field], f"calibration {field}") != int(replay_row[field]):
                raise ProtocolError(f"Calibration replay mismatch in {field}, {directory}.")
        if actual_row["streamSeedHex"] != replay_row["streamSeedHex"]:
            raise ProtocolError(f"Calibration replay stream mismatch in {directory}.")
    for node, actual in candidate_objects.items():
        replay = replay_candidate_by_node[node]
        if actual.trace_indices != replay.trace_indices:
            raise ProtocolError(f"Candidate exposure ledger does not replay for node {node} in {directory}.")
        for label, actual_value, replay_value in (
            ("exposure", actual.exposure, replay.exposure),
            ("forwarding factor", actual.forwarding_factor, replay.forwarding_factor),
            ("product score", actual.product_score, replay.product_score),
            ("degree utility", actual.degree_utility, replay.degree_utility),
        ):
            _assert_close(actual_value, replay_value, f"replayed candidate {label}")
        if actual.in_band != replay.in_band or actual.tie_key != replay.tie_key:
            raise ProtocolError(f"Candidate band or tie key does not replay for node {node} in {directory}.")

    selection_rows = read_csv_exact(directory / "selections.csv", SELECTION_FIELDS)
    if len(selection_rows) != len(ARMS) * config.holder_degree:
        raise ProtocolError(f"Selection row count mismatch in {directory}.")
    selected_by_policy: Dict[str, List[int]] = {policy: [] for policy in ARMS}
    selection_row_by_policy_node: Dict[Tuple[str, int], Dict[str, str]] = {}
    for row_index, row in enumerate(selection_rows):
        expected_policy = ARMS[row_index // config.holder_degree]
        expected_rank = row_index % config.holder_degree + 1
        if row["policy"] != expected_policy or _int(row["rank"], "selection rank") != expected_rank:
            raise ProtocolError(f"Selection policy or rank order mismatch in {directory}.")
        node = _int(row["nodeId"], "selection nodeId")
        if node in selected_by_policy[expected_policy]:
            raise ProtocolError(f"Duplicate selected node in {expected_policy}, {directory}.")
        selected_by_policy[expected_policy].append(node)
        selection_row_by_policy_node[(expected_policy, node)] = row
        candidate = candidate_objects.get(node)
        if expected_policy != "sham" and candidate is None:
            raise ProtocolError(f"Policy {expected_policy} selected a noncandidate in {directory}.")
        expected_base = view.residual_degree(node)
        if _int(row["baseDegree"], "selection baseDegree") != expected_base:
            raise ProtocolError(f"Selection base degree mismatch in {directory}.")
        if _int(row["postDegree"], "selection postDegree") != expected_base + 1:
            raise ProtocolError(f"Selection post degree mismatch in {directory}.")
        expected_values = (
            candidate.exposure if candidate else 0.0,
            candidate.forwarding_factor if candidate else 0.0,
            candidate.product_score if candidate else 0.0,
            candidate.degree_utility if candidate else 0.0,
        )
        for field, expected in zip(
            ("exposure", "forwardingFactor", "productScore", "degreeUtility"),
            expected_values,
        ):
            _assert_close(_float(row[field], f"selection {field}"), expected, f"selection {field}")
        expected_band_flag = 1 if candidate and candidate.in_band else 0
        if _int(row["inUtilityBand"], "selection inUtilityBand") != expected_band_flag:
            raise ProtocolError(f"Selection utility band flag mismatch in {directory}.")
        expected_key = candidate.tie_key if candidate else ""
        if row["candidateTieKey"] != expected_key:
            raise ProtocolError(f"Selection tie key mismatch in {directory}.")

    def expected_ranked(pool: Iterable[Candidate], score: str) -> List[int]:
        return [
            candidate.node_id
            for candidate in sorted(
                pool,
                key=lambda candidate: (
                    -float(getattr(candidate, score)),
                    candidate.tie_key,
                    candidate.node_id,
                ),
            )[: config.holder_degree]
        ]

    all_candidates = list(candidate_objects.values())
    band_candidates = [candidate for candidate in all_candidates if candidate.in_band]
    expected_selections = {
        "sham": list(plan.incumbents),
        "maxdeg": expected_ranked(all_candidates, "base_degree"),
        "degreeutility": expected_ranked(all_candidates, "degree_utility"),
        "exposure": expected_ranked(all_candidates, "exposure"),
        "bandexposure": expected_ranked(band_candidates, "exposure"),
        "product": expected_ranked(all_candidates, "product_score"),
        "routeband": expected_ranked(band_candidates, "product_score"),
        "bandrandom": [
            candidate.node_id
            for candidate in sorted(
                band_candidates,
                key=lambda candidate: (
                    key_u64(graph.snapshot, plan.holder_id, "bandrandom",
                            candidate.node_id, "draw"),
                    candidate.node_id,
                ),
            )[: config.holder_degree]
        ],
    }
    for policy, expected in expected_selections.items():
        if selected_by_policy.get(policy) != expected:
            raise ProtocolError(f"Policy selection {policy} does not reproduce in {directory}.")
    unmodelled = set(selected_by_policy) - set(expected_selections)
    if unmodelled:
        raise ProtocolError(f"Unmodelled policy arms in {directory}: {sorted(unmodelled)}.")

    target_rows = read_csv_exact(directory / "target_probes.csv", TARGET_FIELDS)
    if len(target_rows) != len(ARMS) * config.target_probes:
        raise ProtocolError(f"Target probe row count mismatch in {directory}.")
    target_by_policy: Dict[str, List[Dict[str, str]]] = {policy: [] for policy in ARMS}
    for row_index, row in enumerate(target_rows):
        expected_policy = ARMS[row_index // config.target_probes]
        expected_index = row_index % config.target_probes
        if row["policy"] != expected_policy or _int(row["probeIndex"], "target probeIndex") != expected_index:
            raise ProtocolError(f"Target policy or probe order mismatch in {directory}.")
        expected_source = _source_for(view, "target", expected_index)
        expected_seed = _stream_seed(view, "target", expected_index)
        if _int(row["sourceId"], "target sourceId") != expected_source:
            raise ProtocolError(f"Target source mismatch in {directory} at probe {expected_index}.")
        if row["streamSeedHex"] != f"{expected_seed:016x}":
            raise ProtocolError(f"Target stream mismatch in {directory} at probe {expected_index}.")
        success = _int(row["success"], "target success")
        visited = _int(row["uniqueVisited"], "target uniqueVisited")
        messages = _int(row["messages"], "target messages")
        if success not in (0, 1):
            raise ProtocolError(f"Target success is not binary in {directory}.")
        if not (1 <= visited <= config.target_visit_budget) or messages < 0:
            raise ProtocolError(f"Target probe budget or message violation in {directory}.")
        target_by_policy[expected_policy].append(row)

    # Reexecute every target probe. This catches a self-consistent rewrite of
    # raw outcomes, summaries, and manifests rather than checking only their
    # internal arithmetic.
    replay_target_rows = run_target_probes(graph, plan, expected_selections, config)
    if len(replay_target_rows) != len(target_rows):
        raise ProtocolError(f"Target replay count mismatch in {directory}.")
    for actual, replay in zip(target_rows, replay_target_rows):
        for field in ("policy", "streamSeedHex"):
            if actual[field] != str(replay[field]):
                raise ProtocolError(f"Target replay mismatch in {field}, {directory}.")
        for field in ("probeIndex", "sourceId", "success", "uniqueVisited", "messages"):
            if _int(actual[field], f"target {field}") != int(replay[field]):
                raise ProtocolError(f"Target replay mismatch in {field}, {directory}.")

    summary_rows = read_csv_exact(directory / "summary.csv", SUMMARY_FIELDS)
    if len(summary_rows) != len(ARMS):
        raise ProtocolError(f"Summary policy count mismatch in {directory}.")
    for policy_index, row in enumerate(summary_rows):
        policy = ARMS[policy_index]
        if row["snapshot"] != graph.snapshot or _int(row["holderId"], "summary holderId") != plan.holder_id:
            raise ProtocolError(f"Summary identity mismatch in {directory}.")
        if row["policy"] != policy:
            raise ProtocolError(f"Summary policy order mismatch in {directory}.")
        probes = target_by_policy[policy]
        successes = sum(_int(probe["success"], "target success") for probe in probes)
        if _int(row["successes"], "summary successes") != successes:
            raise ProtocolError(f"Summary success count mismatch for {policy} in {directory}.")
        if _int(row["probes"], "summary probes") != config.target_probes:
            raise ProtocolError(f"Summary probe count mismatch for {policy} in {directory}.")
        _assert_close(_float(row["pHat"], "summary pHat"), successes / float(config.target_probes), "summary pHat")
        _assert_close(
            _float(row["meanVisited"], "summary meanVisited"),
            sum(_int(probe["uniqueVisited"], "target uniqueVisited") for probe in probes) / float(config.target_probes),
            "summary meanVisited",
        )
        _assert_close(
            _float(row["meanMessages"], "summary meanMessages"),
            sum(_int(probe["messages"], "target messages") for probe in probes) / float(config.target_probes),
            "summary meanMessages",
        )
        selected = selected_by_policy[policy]
        _assert_close(
            _float(row["meanRelayDegree"], "summary meanRelayDegree"),
            sum(view.residual_degree(node) + 1 for node in selected) / float(config.holder_degree),
            "summary meanRelayDegree",
        )
        score_candidates = [candidate_objects[node] for node in selected if node in candidate_objects]
        _assert_close(
            _float(row["meanCalibrationExposure"], "summary meanCalibrationExposure"),
            sum(candidate.exposure for candidate in score_candidates) / float(config.holder_degree),
            "summary meanCalibrationExposure",
        )
        _assert_close(
            _float(row["meanProductScore"], "summary meanProductScore"),
            sum(candidate.product_score for candidate in score_candidates) / float(config.holder_degree),
            "summary meanProductScore",
        )
        if _int(row["candidatePool"], "summary candidatePool") != len(candidate_objects):
            raise ProtocolError(f"Summary candidate pool mismatch in {directory}.")
        band_count = sum(candidate.in_band for candidate in candidate_objects.values())
        if _int(row["utilityBandPool"], "summary utilityBandPool") != band_count:
            raise ProtocolError(f"Summary utility band count mismatch in {directory}.")
        expected_evaluations = 0 if policy == "sham" else (
            band_count if policy in ("bandexposure", "routeband") else len(candidate_objects)
        )
        if _int(row["selectionEvaluations"], "summary selectionEvaluations") != expected_evaluations:
            raise ProtocolError(f"Summary selection evaluation mismatch in {directory}.")
    return summary_rows


def _manifest_file_set(run_directory: Path) -> List[str]:
    return sorted(
        str(path.relative_to(run_directory))
        for path in run_directory.rglob("*")
        if path.is_file() and path.name not in ("RUN_MANIFEST.sha256", "COMPLETED.json")
    )


def validate_run(run_directory: Path, data_directory: Path) -> List[Dict[str, str]]:
    if not run_directory.is_dir() or run_directory.is_symlink():
        raise ProtocolError("Run directory is missing or is a symlink.")
    root_expected = {
        "RUN_METADATA.json",
        "holder_plan.csv",
        "holder_results.csv",
        "RUN_MANIFEST.sha256",
        "COMPLETED.json",
        *SNAPSHOT_ORDER,
    }
    root_actual = {path.name for path in run_directory.iterdir()}
    if root_actual != root_expected:
        raise ProtocolError(
            f"Run root membership mismatch; missing={sorted(root_expected-root_actual)}, "
            f"extra={sorted(root_actual-root_expected)}."
        )
    if any(path.is_symlink() for path in run_directory.rglob("*")):
        raise ProtocolError("Run tree contains a symlink.")
    manifest_entries = verify_manifest(
        run_directory,
        "RUN_MANIFEST.sha256",
        _manifest_file_set(run_directory),
    )
    completion = read_json_object(run_directory / "COMPLETED.json")
    if completion.get("runManifestSha256") != sha256_file(run_directory / "RUN_MANIFEST.sha256"):
        raise ProtocolError("Completion marker does not bind the run manifest.")
    aggregate = __import__("hashlib").sha256()
    for line in (run_directory / "RUN_MANIFEST.sha256").read_text(encoding="ascii").splitlines():
        aggregate.update((line + "\n").encode("ascii"))
    if completion.get("orderedFileDigest") != aggregate.hexdigest():
        raise ProtocolError("Completion marker ordered digest mismatch.")

    metadata = read_json_object(run_directory / "RUN_METADATA.json")
    config = ExperimentConfig()
    expected_metadata_fields = {
        "schemaVersion",
        "implementationVersion",
        "config",
        "snapshotOrder",
        "armOrder",
        "inputFiles",
        "analysisBoundary",
        "targetOutcomeUsedForSelection",
    }
    if set(metadata) != expected_metadata_fields:
        raise ProtocolError("Run metadata field mismatch.")
    if metadata["schemaVersion"] != SCHEMA_VERSION or metadata["implementationVersion"] != IMPLEMENTATION_VERSION:
        raise ProtocolError("Run version mismatch.")
    if metadata["config"] != config.as_dict() or metadata["snapshotOrder"] != list(SNAPSHOT_ORDER):
        raise ProtocolError("Run metadata configuration or snapshot order mismatch.")
    if metadata["armOrder"] != list(ARMS) or metadata["targetOutcomeUsedForSelection"] is not False:
        raise ProtocolError("Run metadata arm order or outcome independence flag mismatch.")

    global_plan_rows = read_csv_exact(run_directory / "holder_plan.csv", PLAN_FIELDS)
    global_summary_rows = read_csv_exact(run_directory / "holder_results.csv", SUMMARY_FIELDS)
    expected_global_plan: List[Dict[str, str]] = []
    validated_summaries: List[Dict[str, str]] = []
    total_selected = 0
    snapshot_digests: Dict[str, str] = {}

    for snapshot in SNAPSHOT_ORDER:
        filename, expected_hash = SNAPSHOT_FILES[snapshot]
        graph = load_snapshot(data_directory / filename, snapshot, expected_hash)
        plans = plan_snapshot(graph, config)
        selected_plans = [plan for plan in plans if plan.selected]
        total_selected += len(selected_plans)
        snapshot_directory = run_directory / snapshot
        expected_snapshot_names = {
            "holder_plan.csv",
            "holder_results.csv",
            "SNAPSHOT_COMPLETED.json",
            "SNAPSHOT_MANIFEST.sha256",
            *(f"holder-{plan.holder_id}" for plan in selected_plans),
        }
        actual_snapshot_names = {path.name for path in snapshot_directory.iterdir()}
        if actual_snapshot_names != expected_snapshot_names:
            raise ProtocolError(f"Snapshot directory membership mismatch for {snapshot}.")
        snapshot_manifest_entries = sorted(
            str(path.relative_to(snapshot_directory))
            for path in snapshot_directory.rglob("*")
            if path.is_file() and path.name != "SNAPSHOT_MANIFEST.sha256"
        )
        verify_manifest(snapshot_directory, "SNAPSHOT_MANIFEST.sha256", snapshot_manifest_entries)
        snapshot_digest = __import__("hashlib").sha256()
        for line in (snapshot_directory / "SNAPSHOT_MANIFEST.sha256").read_text(encoding="ascii").splitlines():
            snapshot_digest.update((line + "\n").encode("ascii"))
        snapshot_digests[snapshot] = snapshot_digest.hexdigest()
        snapshot_completion = read_json_object(snapshot_directory / "SNAPSHOT_COMPLETED.json")
        if snapshot_completion.get("snapshot") != snapshot:
            raise ProtocolError(f"Snapshot completion identity mismatch for {snapshot}.")
        if snapshot_completion.get("selectedHolderCount") != len(selected_plans):
            raise ProtocolError(f"Snapshot selected holder count mismatch for {snapshot}.")
        if snapshot_completion.get("holderShortfall") != 5 - len(selected_plans):
            raise ProtocolError(f"Snapshot holder shortfall mismatch for {snapshot}.")
        if snapshot_completion.get("graphHash") != graph.graph_hash:
            raise ProtocolError(f"Snapshot graph hash mismatch for {snapshot}.")

        snapshot_plan_rows = read_csv_exact(snapshot_directory / "holder_plan.csv", PLAN_FIELDS)
        if len(snapshot_plan_rows) != len(plans):
            raise ProtocolError(f"Holder plan row count mismatch for {snapshot}.")
        expected_selected = [plan.holder_id for plan in plans if plan.feasible][:5]
        observed_selected: List[int] = []
        for row, plan in zip(snapshot_plan_rows, plans):
            expected_row = {
                "snapshot": plan.snapshot,
                "holderId": str(plan.holder_id),
                "holderKey": plan.holder_key,
                "observedDegree": str(plan.observed_degree),
                "feasible": "1" if plan.feasible else "0",
                "selected": "1" if plan.selected else "0",
                "reason": plan.reason,
                "incumbentCount": str(len(plan.incumbents)),
                "rootCount": str(len(plan.roots)),
                "candidatePool": str(len(plan.candidates)),
                "utilityBandPool": str(len(plan.band_candidates)),
                "candidateAdjacencyChecks": str(plan.candidate_adjacency_checks),
                "residualComponentCount": str(plan.residual_component_count),
                "residualLargestComponent": str(plan.residual_largest_component),
            }
            if row != expected_row:
                raise ProtocolError(f"Holder plan values do not reproduce for {snapshot}, holder {plan.holder_id}.")
            if row["selected"] == "1":
                observed_selected.append(plan.holder_id)
        if observed_selected != expected_selected:
            raise ProtocolError(f"Selected holders are not the first five feasible SHA256 ordered holders for {snapshot}.")
        expected_global_plan.extend(snapshot_plan_rows)

        snapshot_summaries: List[Dict[str, str]] = []
        for plan in selected_plans:
            holder_rows = validate_holder_directory(
                snapshot_directory / f"holder-{plan.holder_id}",
                graph,
                plan,
                config,
            )
            snapshot_summaries.extend(holder_rows)
            validated_summaries.extend(holder_rows)
        stored_snapshot_summaries = read_csv_exact(snapshot_directory / "holder_results.csv", SUMMARY_FIELDS)
        if stored_snapshot_summaries != snapshot_summaries:
            raise ProtocolError(f"Snapshot summary aggregation mismatch for {snapshot}.")

    if global_plan_rows != expected_global_plan:
        raise ProtocolError("Global holder plan does not equal the ordered snapshot plans.")
    if global_summary_rows != validated_summaries:
        raise ProtocolError("Global holder results do not equal validated holder summaries.")
    if completion.get("snapshotCount") != len(SNAPSHOT_ORDER):
        raise ProtocolError("Run completion snapshot count mismatch.")
    if completion.get("selectedHolderCount") != total_selected:
        raise ProtocolError("Run completion selected holder count mismatch.")
    if completion.get("holderShortfall") != len(SNAPSHOT_ORDER) * 5 - total_selected:
        raise ProtocolError("Run completion holder shortfall mismatch.")
    if completion.get("snapshotDigests") != snapshot_digests:
        raise ProtocolError("Run completion snapshot digest mismatch.")
    return validated_summaries


def analyze_summaries(summary_rows: Sequence[Mapping[str, str]]) -> Tuple[List[Dict[str, object]], List[Dict[str, object]], Dict[str, object]]:
    holder_values: Dict[Tuple[str, int, str], float] = {}
    for row in summary_rows:
        key = (row["snapshot"], _int(row["holderId"], "holderId"), row["policy"])
        if key in holder_values:
            raise ProtocolError(f"Duplicate holder summary key: {key}")
        holder_values[key] = _float(row["pHat"], "pHat")
    by_snapshot_policy: Dict[Tuple[str, str], List[float]] = defaultdict(list)
    for (snapshot, _holder, policy), value in holder_values.items():
        by_snapshot_policy[(snapshot, policy)].append(value)

    snapshot_rows: List[Dict[str, object]] = []
    snapshot_means: Dict[Tuple[str, str], float] = {}
    included_snapshots: List[str] = []
    for snapshot in SNAPSHOT_ORDER:
        counts = {policy: len(by_snapshot_policy[(snapshot, policy)]) for policy in ARMS}
        if len(set(counts.values())) != 1:
            raise ProtocolError(f"Policy holder counts differ within snapshot {snapshot}.")
        holder_count = counts[ARMS[0]]
        if holder_count == 0:
            continue
        included_snapshots.append(snapshot)
        for policy in ARMS:
            mean = statistics.fmean(by_snapshot_policy[(snapshot, policy)])
            snapshot_means[(snapshot, policy)] = mean
            snapshot_rows.append(
                {
                    "snapshot": snapshot,
                    "policy": policy,
                    "holderCount": holder_count,
                    "snapshotMean": mean,
                }
            )
    if len(included_snapshots) < 2:
        raise ProtocolError("At least two snapshots with feasible holders are required for paired intervals.")
    if len(included_snapshots) - 1 not in T_CRITICAL_975:
        raise ProtocolError("No tabulated t critical value exists for the available snapshot count.")

    policy_means = {
        policy: statistics.fmean(snapshot_means[(snapshot, policy)] for snapshot in included_snapshots)
        for policy in ARMS
    }
    comparison_rows: List[Dict[str, object]] = []
    route_policy = "routeband"
    for comparator in ARMS:
        if comparator == route_policy:
            continue
        differences = [
            snapshot_means[(snapshot, route_policy)] - snapshot_means[(snapshot, comparator)]
            for snapshot in included_snapshots
        ]
        mean_difference = statistics.fmean(differences)
        standard_deviation = statistics.stdev(differences)
        half_width = T_CRITICAL_975[len(differences) - 1] * standard_deviation / math.sqrt(len(differences))
        comparison_rows.append(
            {
                "referencePolicy": route_policy,
                "comparator": comparator,
                "snapshotCount": len(differences),
                "snapshotWins": sum(difference > 0 for difference in differences),
                "snapshotTies": sum(difference == 0 for difference in differences),
                "meanDifference": mean_difference,
                "meanDifferencePercentagePoints": 100.0 * mean_difference,
                "paired95Lower": mean_difference - half_width,
                "paired95Upper": mean_difference + half_width,
                "paired95LowerPercentagePoints": 100.0 * (mean_difference - half_width),
                "paired95UpperPercentagePoints": 100.0 * (mean_difference + half_width),
            }
        )
    analysis = {
        "schemaVersion": SCHEMA_VERSION,
        "implementationVersion": IMPLEMENTATION_VERSION,
        "analysisType": "secondary descriptive snapshot clustered sensitivity",
        "snapshotCount": len(included_snapshots),
        "includedSnapshots": included_snapshots,
        "unweightedMeanAcrossSnapshotMeans": policy_means,
        "interval": "paired two sided 95 percent Student t interval over snapshot mean differences",
        "pValuesComputed": False,
        "primaryGate": False,
        "canRescuePrimaryConfirmation": False,
        "claimBoundary": (
            "Results address structural portability to the measured August 2002 Gnutella snapshots only; "
            "they do not validate a current protocol, workload, churn process, latency, packet implementation, "
            "directed semantics, or deployed performance."
        ),
    }
    return snapshot_rows, comparison_rows, analysis


def write_analysis(output_directory: Path, snapshot_rows, comparison_rows, analysis) -> Path:
    partial = output_directory.with_name(output_directory.name + ".partial")
    if output_directory.exists() or partial.exists():
        raise ProtocolError("Refusing to overwrite complete or partial analysis output.")
    partial.parent.mkdir(parents=True, exist_ok=True)
    partial.mkdir()
    write_csv(
        partial / "snapshot_means.csv",
        ("snapshot", "policy", "holderCount", "snapshotMean"),
        snapshot_rows,
    )
    write_csv(
        partial / "routeband_comparisons.csv",
        (
            "referencePolicy",
            "comparator",
            "snapshotCount",
            "snapshotWins",
            "snapshotTies",
            "meanDifference",
            "meanDifferencePercentagePoints",
            "paired95Lower",
            "paired95Upper",
            "paired95LowerPercentagePoints",
            "paired95UpperPercentagePoints",
        ),
        comparison_rows,
    )
    write_json(partial / "GNUTELLA_PORTABILITY_ANALYSIS.json", analysis)
    os.replace(partial, output_directory)
    return output_directory


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--data-dir", type=Path, default=BASE_DIRECTORY / "data")
    parser.add_argument("--output-dir", type=Path, required=True)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    arguments = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        summaries = validate_run(arguments.run_dir.resolve(), arguments.data_dir.resolve())
        snapshot_rows, comparison_rows, analysis = analyze_summaries(summaries)
        output = write_analysis(arguments.output_dir, snapshot_rows, comparison_rows, analysis)
    except (ProtocolError, OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
