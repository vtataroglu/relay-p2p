#!/usr/bin/env python3
"""Run the secondary Gnutella structural portability study."""

from __future__ import annotations

import argparse
import concurrent.futures
import csv
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path
from typing import Dict, List, Mapping, Sequence, Tuple

from gnutella_portability import (
    ARMS,
    IMPLEMENTATION_VERSION,
    PLAN_FIELDS,
    SCHEMA_VERSION,
    SNAPSHOT_FILES,
    SNAPSHOT_ORDER,
    SUMMARY_FIELDS,
    FANOUT,
    ExperimentConfig,
    ProtocolError,
    holder_plan_rows,
    load_snapshot,
    plan_snapshot,
    run_selected_holder,
    sha256_file,
    write_csv,
    write_json,
    write_manifest,
)


BASE_DIRECTORY = Path(__file__).resolve().parent


def _read_csv(path: Path) -> List[Dict[str, str]]:
    with path.open("r", encoding="utf-8", newline="") as handle:
        return list(csv.DictReader(handle))


def _run_snapshot(
    snapshot: str,
    data_directory_text: str,
    partial_root_text: str,
    fanout: int = FANOUT,
) -> Dict[str, object]:
    data_directory = Path(data_directory_text)
    partial_root = Path(partial_root_text)
    config = ExperimentConfig(fanout=fanout)
    filename, expected_hash = SNAPSHOT_FILES[snapshot]
    graph = load_snapshot(data_directory / filename, snapshot, expected_hash)
    plans = plan_snapshot(graph, config)
    selected = [plan for plan in plans if plan.selected]

    snapshot_directory = partial_root / snapshot
    snapshot_directory.mkdir(parents=False, exist_ok=False)
    write_csv(snapshot_directory / "holder_plan.csv", PLAN_FIELDS, holder_plan_rows(plans))
    all_summaries: List[Mapping[str, object]] = []
    holder_digests: Dict[str, str] = {}
    for plan in selected:
        holder_name = f"holder-{plan.holder_id}"
        summaries, digest = run_selected_holder(
            graph,
            plan,
            snapshot_directory / holder_name,
            config,
        )
        all_summaries.extend(summaries)
        holder_digests[holder_name] = digest
    write_csv(snapshot_directory / "holder_results.csv", SUMMARY_FIELDS, all_summaries)
    completion = {
        "schemaVersion": SCHEMA_VERSION,
        "implementationVersion": IMPLEMENTATION_VERSION,
        "snapshot": snapshot,
        "graphHash": graph.graph_hash,
        "nodeCount": graph.node_count,
        "edgeCount": graph.edge_count,
        "eligibleHolderCount": len(plans),
        "feasibleHolderCount": sum(plan.feasible for plan in plans),
        "selectedHolderCount": len(selected),
        "holderShortfall": 5 - len(selected),
        "holderDigests": holder_digests,
        "armOrder": list(ARMS),
    }
    write_json(snapshot_directory / "SNAPSHOT_COMPLETED.json", completion)
    relative_files = [
        str(path.relative_to(snapshot_directory))
        for path in snapshot_directory.rglob("*")
        if path.is_file() and path.name != "SNAPSHOT_MANIFEST.sha256"
    ]
    ordered_digest = write_manifest(
        snapshot_directory,
        relative_files,
        "SNAPSHOT_MANIFEST.sha256",
    )
    return {
        "snapshot": snapshot,
        "selectedHolderCount": len(selected),
        "holderShortfall": 5 - len(selected),
        "graphHash": graph.graph_hash,
        "nodeCount": graph.node_count,
        "edgeCount": graph.edge_count,
        "orderedDigest": ordered_digest,
    }


def _safe_output_paths(output_directory: Path) -> Tuple[Path, Path]:
    if not output_directory.is_absolute():
        output_directory = output_directory.resolve()
    if output_directory == Path("/") or len(output_directory.parts) < 4:
        raise ProtocolError("Output path is too broad for a fail closed run.")
    partial = output_directory.with_name(output_directory.name + ".partial")
    if output_directory.exists() or partial.exists():
        raise ProtocolError(
            f"Refusing to overwrite complete or partial output: {output_directory}, {partial}."
        )
    return output_directory, partial


def run(data_directory: Path, output_directory: Path, workers: int,
        fanout: int = FANOUT) -> Path:
    config = ExperimentConfig(fanout=fanout)
    config.assert_valid()
    if not config.is_published():
        raise ProtocolError("The public runner must use the exact published configuration.")
    if workers < 1 or workers > len(SNAPSHOT_ORDER):
        raise ProtocolError(f"workers must be between 1 and {len(SNAPSHOT_ORDER)}.")
    if not data_directory.is_dir() or data_directory.is_symlink():
        raise ProtocolError("The data directory is missing or is a symlink.")
    for snapshot in SNAPSHOT_ORDER:
        filename, expected_hash = SNAPSHOT_FILES[snapshot]
        path = data_directory / filename
        if not path.is_file() or path.is_symlink():
            raise ProtocolError(f"Missing regular input file: {path}")
        actual = sha256_file(path)
        if actual != expected_hash:
            raise ProtocolError(
                f"Input hash mismatch for {snapshot}: expected {expected_hash}, found {actual}."
            )

    final_output, partial_output = _safe_output_paths(output_directory)
    partial_output.parent.mkdir(parents=True, exist_ok=True)
    partial_output.mkdir()
    try:
        run_metadata = {
            "schemaVersion": SCHEMA_VERSION,
            "implementationVersion": IMPLEMENTATION_VERSION,
            "config": config.as_dict(),
            "snapshotOrder": list(SNAPSHOT_ORDER),
            "armOrder": list(ARMS),
            "inputFiles": {
                snapshot: {
                    "filename": SNAPSHOT_FILES[snapshot][0],
                    "sha256": SNAPSHOT_FILES[snapshot][1],
                }
                for snapshot in SNAPSHOT_ORDER
            },
            "analysisBoundary": "secondary descriptive structural portability only",
            "targetOutcomeUsedForSelection": False,
        }
        write_json(partial_output / "RUN_METADATA.json", run_metadata)

        results: List[Dict[str, object]] = []
        if workers == 1:
            for snapshot in SNAPSHOT_ORDER:
                results.append(_run_snapshot(
                    snapshot, str(data_directory), str(partial_output), config.fanout))
        else:
            with concurrent.futures.ProcessPoolExecutor(max_workers=workers) as executor:
                future_by_snapshot = {
                    snapshot: executor.submit(
                        _run_snapshot,
                        snapshot,
                        str(data_directory),
                        str(partial_output),
                        config.fanout,
                    )
                    for snapshot in SNAPSHOT_ORDER
                }
                for snapshot in SNAPSHOT_ORDER:
                    results.append(future_by_snapshot[snapshot].result())

        global_plan_rows: List[Dict[str, str]] = []
        global_summary_rows: List[Dict[str, str]] = []
        for snapshot in SNAPSHOT_ORDER:
            global_plan_rows.extend(_read_csv(partial_output / snapshot / "holder_plan.csv"))
            global_summary_rows.extend(_read_csv(partial_output / snapshot / "holder_results.csv"))
        write_csv(partial_output / "holder_plan.csv", PLAN_FIELDS, global_plan_rows)
        write_csv(partial_output / "holder_results.csv", SUMMARY_FIELDS, global_summary_rows)

        root_files = [
            str(path.relative_to(partial_output))
            for path in partial_output.rglob("*")
            if path.is_file()
            and path.name not in ("RUN_MANIFEST.sha256", "COMPLETED.json")
        ]
        ordered_digest = write_manifest(partial_output, root_files, "RUN_MANIFEST.sha256")
        completion = {
            "schemaVersion": SCHEMA_VERSION,
            "implementationVersion": IMPLEMENTATION_VERSION,
            "snapshotCount": len(SNAPSHOT_ORDER),
            "selectedHolderCount": sum(int(result["selectedHolderCount"]) for result in results),
            "holderShortfall": sum(int(result["holderShortfall"]) for result in results),
            "snapshotDigests": {
                str(result["snapshot"]): str(result["orderedDigest"])
                for result in results
            },
            "runManifestSha256": sha256_file(partial_output / "RUN_MANIFEST.sha256"),
            "orderedFileDigest": ordered_digest,
        }
        write_json(partial_output / "COMPLETED.json", completion)
        os.replace(partial_output, final_output)
        return final_output
    except BaseException:
        # Keep the partial directory as an explicit failure record.  A later
        # run must use a new output path or remove it deliberately after audit.
        raise


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--data-dir",
        type=Path,
        default=BASE_DIRECTORY / "data",
        help="Directory containing the nine SNAP gzip files.",
    )
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument(
        "--fanout",
        type=int,
        default=FANOUT,
        help="Query fanout. The study reported in the paper used 7; other values probe "
             "whether the measured-topology result depends on the operating "
             "point rather than on the topology.",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    arguments = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        output = run(arguments.data_dir.resolve(), arguments.output_dir,
                     arguments.workers, arguments.fanout)
    except (ProtocolError, OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

