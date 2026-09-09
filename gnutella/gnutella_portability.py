#!/usr/bin/env python3
"""Deterministic simulator for the Gnutella portability study.

The implementation is intentionally independent of the C# synthetic graph
simulator.  It uses only the Python standard library and the published SNAP edge
lists.  Scientific constants live in this module so that the runner, analyzer,
and tests can enforce one contract.
"""

from __future__ import annotations

import csv
import gzip
import hashlib
import json
import math
import os
from collections import deque
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, Iterator, List, Mapping, MutableMapping, Optional, Sequence, Set, Tuple


IMPLEMENTATION_VERSION = "gnutella-portability-v1"
SCHEMA_VERSION = 1
SNAPSHOT_ORDER: Tuple[str, ...] = (
    "Gnutella04",
    "Gnutella05",
    "Gnutella06",
    "Gnutella08",
    "Gnutella09",
    "Gnutella24",
    "Gnutella25",
    "Gnutella30",
    "Gnutella31",
)
SNAPSHOT_FILES: Mapping[str, Tuple[str, str]] = {
    "Gnutella04": ("p2p-Gnutella04.txt.gz", "44b7bf7b2238dc1fae8f50146b769b803ed7455e1fd7a598daebce319803397e"),
    "Gnutella05": ("p2p-Gnutella05.txt.gz", "98054ae9961ae37286e58cec60676ca6ce3de8d159273cb73521c6a79b4231eb"),
    "Gnutella06": ("p2p-Gnutella06.txt.gz", "6a434703d41d94a66cad5caa5747ac0c5676e99dec3127f178fb523f62c055b8"),
    "Gnutella08": ("p2p-Gnutella08.txt.gz", "cb3019062be21e55b03f19a73e88edf34693bafb68ae581621cc7a037fa604a9"),
    "Gnutella09": ("p2p-Gnutella09.txt.gz", "5064ba72062edf2f122ca12c24bf9b379215d4f410901820748bb3178a4fe400"),
    "Gnutella24": ("p2p-Gnutella24.txt.gz", "6f0a91d504b0df4321b60a31bb66b4783db321f2a51a88b7f872375f91dee22e"),
    "Gnutella25": ("p2p-Gnutella25.txt.gz", "9fdf733d3edc66aab475518f23e2d74532a839f2a53e8a2c7200f3bd7bcbd1b2"),
    "Gnutella30": ("p2p-Gnutella30.txt.gz", "856c5e663d78a2edca9ae41fd4045a69ec518f49de420cb443aabdc480582cab"),
    "Gnutella31": ("p2p-Gnutella31.txt.gz", "20cf3b0642718e7628c7aa15a41b21e663aea872946878d5b9abc80ee360f869"),
}

ARMS: Tuple[str, ...] = (
    "sham",
    "maxdeg",
    "degreeutility",
    "exposure",
    "bandexposure",
    "product",
    "routeband",
    "bandrandom",
)

FANOUT = 7
HOLDER_DEGREE = 50
HOLDERS_PER_SNAPSHOT = 5
ROOT_COUNT = 16
CANDIDATE_HOPS = 2
CALIBRATION_TRACES = 400
CALIBRATION_VISIT_BUDGET = 2_000
TARGET_PROBES = 500
TARGET_VISIT_BUDGET = 2_000
UTILITY_BAND_THRESHOLD = 0.95
MIN_CANDIDATE_BASE_DEGREE = 3
MAX_CANDIDATE_BASE_DEGREE = 49

MASK64 = (1 << 64) - 1
MIX_A = 0x9E3779B97F4A7C15
MIX_B = 0xBF58476D1CE4E5B9
MIX_C = 0x94D049BB133111EB


class ProtocolError(RuntimeError):
    """Raised when a protocol invariant is not satisfied."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _key_bytes(*parts: object) -> bytes:
    encoded = [IMPLEMENTATION_VERSION.encode("utf-8")]
    encoded.extend(str(part).encode("utf-8") for part in parts)
    return hashlib.sha256(b"\x1f".join(encoded)).digest()


def key_hex(*parts: object) -> str:
    return _key_bytes(*parts).hex()


def key_u64(*parts: object) -> int:
    return int.from_bytes(_key_bytes(*parts)[:8], "big", signed=False)


def splitmix64(value: int) -> int:
    value = (value + MIX_A) & MASK64
    value = ((value ^ (value >> 30)) * MIX_B) & MASK64
    value = ((value ^ (value >> 27)) * MIX_C) & MASK64
    return (value ^ (value >> 31)) & MASK64


def float_text(value: float) -> str:
    if not math.isfinite(value):
        raise ProtocolError("Nonfinite value cannot be serialized.")
    return format(value, ".17g")


@dataclass(frozen=True)
class ExperimentConfig:
    fanout: int = FANOUT
    holder_degree: int = HOLDER_DEGREE
    roots: int = ROOT_COUNT
    candidate_hops: int = CANDIDATE_HOPS
    calibration_traces: int = CALIBRATION_TRACES
    calibration_visit_budget: int = CALIBRATION_VISIT_BUDGET
    target_probes: int = TARGET_PROBES
    target_visit_budget: int = TARGET_VISIT_BUDGET
    utility_band_threshold: float = UTILITY_BAND_THRESHOLD

    def assert_valid(self) -> None:
        integer_values = (
            self.fanout,
            self.holder_degree,
            self.roots,
            self.candidate_hops,
            self.calibration_traces,
            self.calibration_visit_budget,
            self.target_probes,
            self.target_visit_budget,
        )
        if any(value <= 0 for value in integer_values):
            raise ProtocolError("Every experiment count and budget must be positive.")
        if self.holder_degree < self.roots:
            raise ProtocolError("The incumbent set cannot be smaller than the root set.")
        if not (0.0 < self.utility_band_threshold <= 1.0):
            raise ProtocolError("The utility band threshold must be in (0, 1].")

    def as_dict(self) -> Dict[str, object]:
        return {
            "fanout": self.fanout,
            "holderDegree": self.holder_degree,
            "rootCount": self.roots,
            "candidateHopLimit": self.candidate_hops,
            "calibrationTraces": self.calibration_traces,
            "calibrationVisitBudget": self.calibration_visit_budget,
            "targetProbes": self.target_probes,
            "targetVisitBudget": self.target_visit_budget,
            "utilityBandThreshold": self.utility_band_threshold,
        }

    def is_published(self) -> bool:
        # Every parameter except the query fanout must match the published
        # configuration. Fanout is varied deliberately: the reported Gnutella
        # study measured a single operating point, and the grown-topology
        # results show the effect of route exposure depends on fanout, so a
        # measured-topology check at one fanout cannot settle the question.
        return self == ExperimentConfig(fanout=self.fanout)


@dataclass(frozen=True)
class Graph:
    snapshot: str
    adjacency: Mapping[int, Tuple[int, ...]]
    nodes: Tuple[int, ...]
    directed_input_edges: int
    simple_edges_before_component: int
    component_count: int
    graph_hash: str

    @property
    def node_count(self) -> int:
        return len(self.nodes)

    @property
    def edge_count(self) -> int:
        return sum(len(self.adjacency[node]) for node in self.nodes) // 2

    def degree(self, node: int) -> int:
        return len(self.adjacency[node])


@dataclass(frozen=True)
class HolderPlan:
    snapshot: str
    holder_id: int
    holder_key: str
    observed_degree: int
    incumbents: Tuple[int, ...]
    roots: Tuple[int, ...]
    candidates: Tuple[int, ...]
    band_candidates: Tuple[int, ...]
    candidate_adjacency_checks: int
    residual_component_count: int
    residual_largest_component: int
    feasible: bool
    reason: str
    selected: bool = False


@dataclass
class Candidate:
    node_id: int
    base_degree: int
    post_degree: int
    trace_indices: List[int]
    exposure: float
    forwarding_factor: float
    product_score: float
    degree_utility: float
    in_band: bool
    is_incumbent: bool
    is_root: bool
    tie_key: str


@dataclass(frozen=True)
class ProbeResult:
    success: bool
    unique_visited: int
    messages: int
    expanded: Tuple[int, ...]


def _largest_component(adjacency: Mapping[int, Set[int]]) -> Tuple[Set[int], int]:
    remaining = set(adjacency)
    components: List[Set[int]] = []
    while remaining:
        start = min(remaining)
        component: Set[int] = {start}
        queue = deque([start])
        remaining.remove(start)
        while queue:
            node = queue.popleft()
            for neighbor in adjacency[node]:
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    component.add(neighbor)
                    queue.append(neighbor)
        components.append(component)
    if not components:
        raise ProtocolError("The edge list contains no graph nodes.")
    components.sort(key=lambda part: (-len(part), min(part)))
    return components[0], len(components)


def _canonical_graph_hash(adjacency: Mapping[int, Tuple[int, ...]], nodes: Sequence[int]) -> str:
    digest = hashlib.sha256()
    for node in nodes:
        for neighbor in adjacency[node]:
            if node < neighbor:
                digest.update(f"{node} {neighbor}\n".encode("ascii"))
    return digest.hexdigest()


def load_snapshot(path: Path, snapshot: str, expected_hash: Optional[str] = None) -> Graph:
    if expected_hash is not None:
        actual = sha256_file(path)
        if actual != expected_hash:
            raise ProtocolError(
                f"Input hash mismatch for {snapshot}: expected {expected_hash}, found {actual}."
            )
    adjacency: MutableMapping[int, Set[int]] = {}
    directed_input_edges = 0
    try:
        with gzip.open(path, "rt", encoding="utf-8", newline="") as handle:
            for line_number, raw in enumerate(handle, start=1):
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue
                fields = line.split()
                if len(fields) != 2:
                    raise ProtocolError(
                        f"Malformed edge at {path.name}:{line_number}; expected two columns."
                    )
                try:
                    source, target = int(fields[0]), int(fields[1])
                except ValueError as error:
                    raise ProtocolError(
                        f"Nonnumeric edge at {path.name}:{line_number}."
                    ) from error
                if source < 0 or target < 0:
                    raise ProtocolError(
                        f"Negative node identifier at {path.name}:{line_number}."
                    )
                directed_input_edges += 1
                adjacency.setdefault(source, set())
                adjacency.setdefault(target, set())
                if source == target:
                    continue
                adjacency[source].add(target)
                adjacency[target].add(source)
    except (OSError, EOFError) as error:
        raise ProtocolError(f"Cannot read gzip edge list {path}: {error}") from error

    simple_edges_before = sum(len(neighbors) for neighbors in adjacency.values()) // 2
    largest, component_count = _largest_component(adjacency)
    frozen_nodes = tuple(sorted(largest))
    frozen_adjacency: Dict[int, Tuple[int, ...]] = {
        node: tuple(sorted(neighbor for neighbor in adjacency[node] if neighbor in largest))
        for node in frozen_nodes
    }
    for node in frozen_nodes:
        if node in frozen_adjacency[node]:
            raise ProtocolError("A self loop survived preprocessing.")
        for neighbor in frozen_adjacency[node]:
            if node not in frozen_adjacency.get(neighbor, ()):
                raise ProtocolError("Preprocessed graph is not symmetric.")
    return Graph(
        snapshot=snapshot,
        adjacency=frozen_adjacency,
        nodes=frozen_nodes,
        directed_input_edges=directed_input_edges,
        simple_edges_before_component=simple_edges_before,
        component_count=component_count,
        graph_hash=_canonical_graph_hash(frozen_adjacency, frozen_nodes),
    )


def relay_utility(fanout: int, post_degree: int) -> float:
    if fanout <= 0 or post_degree <= 1:
        raise ProtocolError("Relay utility requires positive fanout and post degree above one.")
    return post_degree * min(1.0, fanout / float(post_degree - 1))


class HolderGraph:
    """Read only graph view with all observed holder edges removed."""

    def __init__(self, graph: Graph, holder: int):
        if holder not in graph.adjacency:
            raise ProtocolError("Holder is absent from the graph.")
        self.graph = graph
        self.holder = holder
        self.original_neighbors = tuple(graph.adjacency[holder])
        self.original_neighbor_set = frozenset(self.original_neighbors)
        self.nodes_without_holder = tuple(node for node in graph.nodes if node != holder)

    def residual_neighbors(self, node: int) -> Tuple[int, ...]:
        if node == self.holder:
            return ()
        neighbors = self.graph.adjacency[node]
        if node not in self.original_neighbor_set:
            return neighbors
        return tuple(neighbor for neighbor in neighbors if neighbor != self.holder)

    def residual_degree(self, node: int) -> int:
        return len(self.residual_neighbors(node))

    def arm_neighbors(self, node: int, selected: Set[int], selected_order: Sequence[int]) -> Tuple[int, ...]:
        if node == self.holder:
            return tuple(selected_order)
        residual = self.residual_neighbors(node)
        if node in selected:
            return residual + (self.holder,)
        return residual


def _residual_component_stats(view: HolderGraph) -> Tuple[int, int]:
    remaining = set(view.nodes_without_holder)
    components = 0
    largest = 0
    while remaining:
        start = min(remaining)
        remaining.remove(start)
        queue = deque([start])
        size = 1
        while queue:
            node = queue.popleft()
            for neighbor in view.residual_neighbors(node):
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    queue.append(neighbor)
                    size += 1
        components += 1
        largest = max(largest, size)
    return components, largest


def construct_holder_plan(graph: Graph, holder: int, config: ExperimentConfig) -> HolderPlan:
    config.assert_valid()
    holder_key_value = key_hex(graph.snapshot, holder, "holder")
    observed_degree = graph.degree(holder)
    if observed_degree < config.holder_degree:
        return HolderPlan(
            graph.snapshot,
            holder,
            holder_key_value,
            observed_degree,
            (),
            (),
            (),
            (),
            0,
            0,
            0,
            False,
            "observed degree below 50",
        )
    view = HolderGraph(graph, holder)
    incumbents = tuple(
        sorted(
            view.original_neighbors,
            key=lambda node: (key_hex(graph.snapshot, holder, node, "incumbent"), node),
        )[: config.holder_degree]
    )
    if len(incumbents) != config.holder_degree:
        raise ProtocolError("An eligible holder did not provide exactly 50 incumbents.")
    roots = tuple(
        sorted(
            incumbents,
            key=lambda node: (key_hex(graph.snapshot, holder, node, "root"), node),
        )[: config.roots]
    )
    pooled: Set[int] = set(incumbents)
    seen: Set[int] = set(roots)
    frontier = list(roots)
    adjacency_checks = 0
    for _ in range(config.candidate_hops):
        next_frontier: List[int] = []
        for node in sorted(frontier):
            for neighbor in view.residual_neighbors(node):
                adjacency_checks += 1
                if neighbor == holder or neighbor in seen:
                    continue
                seen.add(neighbor)
                pooled.add(neighbor)
                next_frontier.append(neighbor)
        frontier = next_frontier
    candidates = tuple(
        sorted(
            node
            for node in pooled
            if node != holder
            and MIN_CANDIDATE_BASE_DEGREE
            <= view.residual_degree(node)
            <= MAX_CANDIDATE_BASE_DEGREE
        )
    )
    maximum_utility = relay_utility(config.fanout, config.fanout + 1)
    band_candidates = tuple(
        node
        for node in candidates
        if relay_utility(config.fanout, view.residual_degree(node) + 1)
        >= config.utility_band_threshold * maximum_utility
    )
    residual_components, residual_largest = _residual_component_stats(view)
    reasons: List[str] = []
    if len(candidates) < config.holder_degree:
        reasons.append("candidate pool below 50")
    if len(band_candidates) < config.holder_degree:
        reasons.append("95 percent utility band below 50")
    feasible = not reasons
    return HolderPlan(
        graph.snapshot,
        holder,
        holder_key_value,
        observed_degree,
        incumbents,
        roots,
        candidates,
        band_candidates,
        adjacency_checks,
        residual_components,
        residual_largest,
        feasible,
        "feasible" if feasible else "; ".join(reasons),
    )


def plan_snapshot(graph: Graph, config: ExperimentConfig) -> List[HolderPlan]:
    eligible = [node for node in graph.nodes if graph.degree(node) >= config.holder_degree]
    eligible.sort(key=lambda node: (key_hex(graph.snapshot, node, "holder"), node))
    plans = [construct_holder_plan(graph, node, config) for node in eligible]
    selected_ids = {
        plan.holder_id
        for plan in [plan for plan in plans if plan.feasible][:HOLDERS_PER_SNAPSHOT]
    }
    return [
        HolderPlan(**{**plan.__dict__, "selected": plan.holder_id in selected_ids})
        for plan in plans
    ]


def _transition_order(
    seed: int,
    current: int,
    parent: Optional[int],
    neighbors: Sequence[int],
    fanout: int,
) -> List[int]:
    parent_word = MASK64 if parent is None else parent & MASK64
    state = splitmix64(seed ^ splitmix64(current & MASK64) ^ splitmix64(parent_word))
    ranked = sorted(
        neighbors,
        key=lambda neighbor: (
            splitmix64(state ^ ((neighbor & MASK64) * MIX_A & MASK64)),
            neighbor,
        ),
    )
    return ranked[: min(fanout, len(ranked))]


def _source_for(view: HolderGraph, phase: str, index: int) -> int:
    pool = view.nodes_without_holder
    if not pool:
        raise ProtocolError("The source pool is empty.")
    position = key_u64(view.graph.snapshot, view.holder, phase, index, "source") % len(pool)
    return pool[position]


def _stream_seed(view: HolderGraph, phase: str, index: int) -> int:
    return key_u64(view.graph.snapshot, view.holder, phase, index, "stream")


def flood_probe(
    view: HolderGraph,
    selected_order: Sequence[int],
    source: int,
    fanout: int,
    visit_budget: int,
    stream_seed: int,
    target_holder: bool,
) -> ProbeResult:
    if source == view.holder:
        raise ProtocolError("The holder cannot be a query source.")
    if len(selected_order) not in (0, HOLDER_DEGREE):
        raise ProtocolError("A graph view must use zero or exactly 50 holder edges.")
    if len(set(selected_order)) != len(selected_order):
        raise ProtocolError("An arm contains duplicate relay endpoints.")
    selected = set(selected_order)
    visited: Set[int] = {source}
    frontier: List[Tuple[int, Optional[int]]] = [(source, None)]
    expanded: Set[int] = set()
    messages = 0
    while frontier and len(visited) < visit_budget:
        following: List[Tuple[int, int]] = []
        for current, parent in frontier:
            if len(visited) >= visit_budget:
                break
            if current != source:
                expanded.add(current)
            neighbors = view.arm_neighbors(current, selected, selected_order)
            eligible = tuple(neighbor for neighbor in neighbors if neighbor != parent)
            for candidate in _transition_order(stream_seed, current, parent, eligible, fanout):
                if len(visited) >= visit_budget:
                    break
                messages += 1
                if candidate in visited:
                    continue
                visited.add(candidate)
                if target_holder and candidate == view.holder:
                    return ProbeResult(True, len(visited), messages, tuple(sorted(expanded)))
                following.append((candidate, current))
        frontier = following
    return ProbeResult(False, len(visited), messages, tuple(sorted(expanded)))


def calibrate_candidates(
    graph: Graph,
    plan: HolderPlan,
    config: ExperimentConfig,
) -> Tuple[List[Candidate], List[Dict[str, object]]]:
    if not plan.feasible or not plan.selected:
        raise ProtocolError("Calibration requires a selected feasible holder.")
    view = HolderGraph(graph, plan.holder_id)
    trace_membership: Dict[int, List[int]] = {node: [] for node in plan.candidates}
    trace_rows: List[Dict[str, object]] = []
    for trace_index in range(config.calibration_traces):
        source = _source_for(view, "calibration", trace_index)
        stream_seed = _stream_seed(view, "calibration", trace_index)
        result = flood_probe(
            view,
            (),
            source,
            config.fanout,
            config.calibration_visit_budget,
            stream_seed,
            False,
        )
        for node in result.expanded:
            membership = trace_membership.get(node)
            if membership is not None:
                membership.append(trace_index)
        trace_rows.append(
            {
                "traceIndex": trace_index,
                "sourceId": source,
                "streamSeedHex": f"{stream_seed:016x}",
                "uniqueVisited": result.unique_visited,
                "messages": result.messages,
                "expandedCount": len(result.expanded),
            }
        )
    maximum_utility = relay_utility(config.fanout, config.fanout + 1)
    incumbent_set = set(plan.incumbents)
    root_set = set(plan.roots)
    candidates: List[Candidate] = []
    for node in plan.candidates:
        base_degree = view.residual_degree(node)
        post_degree = base_degree + 1
        memberships = trace_membership[node]
        exposure = len(memberships) / float(config.calibration_traces)
        forwarding_factor = min(1.0, config.fanout / float(base_degree))
        utility = relay_utility(config.fanout, post_degree)
        candidates.append(
            Candidate(
                node_id=node,
                base_degree=base_degree,
                post_degree=post_degree,
                trace_indices=memberships,
                exposure=exposure,
                forwarding_factor=forwarding_factor,
                product_score=exposure * forwarding_factor,
                degree_utility=utility,
                in_band=utility >= config.utility_band_threshold * maximum_utility,
                is_incumbent=node in incumbent_set,
                is_root=node in root_set,
                tie_key=key_hex(graph.snapshot, plan.holder_id, node, "candidate"),
            )
        )
    if sum(candidate.in_band for candidate in candidates) < config.holder_degree:
        raise ProtocolError("Calibration produced fewer than 50 utility band candidates.")
    return candidates, trace_rows


def select_arms(
    graph: Graph,
    plan: HolderPlan,
    candidates: Sequence[Candidate],
    config: ExperimentConfig,
) -> Dict[str, Tuple[int, ...]]:
    by_node = {candidate.node_id: candidate for candidate in candidates}
    if set(by_node) != set(plan.candidates):
        raise ProtocolError("Candidate records do not match the stored holder plan.")

    def ranked(pool: Iterable[Candidate], score_name: str) -> Tuple[int, ...]:
        materialized = list(pool)
        materialized.sort(
            key=lambda candidate: (
                -float(getattr(candidate, score_name)),
                candidate.tie_key,
                candidate.node_id,
            )
        )
        selected = tuple(candidate.node_id for candidate in materialized[: config.holder_degree])
        if len(selected) != config.holder_degree or len(set(selected)) != config.holder_degree:
            raise ProtocolError("A ranked arm did not select exactly 50 distinct endpoints.")
        return selected

    def uniform_from(pool: Iterable[Candidate]) -> Tuple[int, ...]:
        # Deterministic uniform draw from an already band-restricted pool.
        # This is the no-information control: it fixes prospective degree
        # exactly as the banded ranked arms do while supplying no route
        # information, so the paired contrast isolates the value of ranking
        # rather than the value of the band. The order is derived from the
        # holder-scoped key stream, so it is reproducible without a global RNG.
        materialized = sorted(pool, key=lambda candidate: candidate.node_id)
        keyed = sorted(
            materialized,
            key=lambda candidate: (
                key_u64(graph.snapshot, plan.holder_id, "bandrandom", candidate.node_id, "draw"),
                candidate.node_id,
            ),
        )
        selected = tuple(candidate.node_id for candidate in keyed[: config.holder_degree])
        if len(selected) != config.holder_degree or len(set(selected)) != config.holder_degree:
            raise ProtocolError("The uniform band arm did not select exactly 50 distinct endpoints.")
        return selected

    band = [candidate for candidate in candidates if candidate.in_band]
    arms: Dict[str, Tuple[int, ...]] = {
        "sham": tuple(plan.incumbents),
        "maxdeg": ranked(candidates, "base_degree"),
        "degreeutility": ranked(candidates, "degree_utility"),
        "exposure": ranked(candidates, "exposure"),
        "bandexposure": ranked(band, "exposure"),
        "product": ranked(candidates, "product_score"),
        "routeband": ranked(band, "product_score"),
        "bandrandom": uniform_from(band),
    }
    if tuple(arms) != ARMS:
        raise ProtocolError("Policy order differs from the fixed arm order.")
    for policy, nodes in arms.items():
        if len(nodes) != config.holder_degree or len(set(nodes)) != config.holder_degree:
            raise ProtocolError(f"Policy {policy} does not contain 50 unique endpoints.")
        if policy != "sham" and not set(nodes).issubset(by_node):
            raise ProtocolError(f"Policy {policy} selected a node outside the candidate pool.")
        if policy in ("bandexposure", "routeband", "bandrandom") and not all(by_node[node].in_band for node in nodes):
            raise ProtocolError(f"Policy {policy} selected a node outside the utility band.")
    return arms


def run_target_probes(
    graph: Graph,
    plan: HolderPlan,
    arms: Mapping[str, Sequence[int]],
    config: ExperimentConfig,
) -> List[Dict[str, object]]:
    if tuple(arms) != ARMS:
        raise ProtocolError("Target evaluation requires the fixed arm order.")
    view = HolderGraph(graph, plan.holder_id)
    sources = [_source_for(view, "target", index) for index in range(config.target_probes)]
    seeds = [_stream_seed(view, "target", index) for index in range(config.target_probes)]
    rows: List[Dict[str, object]] = []
    for policy in ARMS:
        selected = tuple(arms[policy])
        for probe_index, (source, stream_seed) in enumerate(zip(sources, seeds)):
            result = flood_probe(
                view,
                selected,
                source,
                config.fanout,
                config.target_visit_budget,
                stream_seed,
                True,
            )
            rows.append(
                {
                    "policy": policy,
                    "probeIndex": probe_index,
                    "sourceId": source,
                    "streamSeedHex": f"{stream_seed:016x}",
                    "success": 1 if result.success else 0,
                    "uniqueVisited": result.unique_visited,
                    "messages": result.messages,
                }
            )
    return rows


def summarize_holder(
    graph: Graph,
    plan: HolderPlan,
    candidates: Sequence[Candidate],
    arms: Mapping[str, Sequence[int]],
    probe_rows: Sequence[Mapping[str, object]],
    config: ExperimentConfig,
) -> List[Dict[str, object]]:
    by_node = {candidate.node_id: candidate for candidate in candidates}
    grouped: Dict[str, List[Mapping[str, object]]] = {policy: [] for policy in ARMS}
    for row in probe_rows:
        policy = str(row["policy"])
        if policy not in grouped:
            raise ProtocolError("A target probe has an unknown policy.")
        grouped[policy].append(row)
    view = HolderGraph(graph, plan.holder_id)
    summaries: List[Dict[str, object]] = []
    for policy in ARMS:
        rows = grouped[policy]
        if len(rows) != config.target_probes:
            raise ProtocolError(f"Policy {policy} has the wrong target probe count.")
        selected = tuple(arms[policy])
        successes = sum(int(row["success"]) for row in rows)
        score_candidates = [by_node[node] for node in selected if node in by_node]
        post_degrees = [view.residual_degree(node) + 1 for node in selected]
        summaries.append(
            {
                "snapshot": graph.snapshot,
                "holderId": plan.holder_id,
                "policy": policy,
                "successes": successes,
                "probes": config.target_probes,
                "pHat": successes / float(config.target_probes),
                "meanVisited": sum(int(row["uniqueVisited"]) for row in rows) / float(config.target_probes),
                "meanMessages": sum(int(row["messages"]) for row in rows) / float(config.target_probes),
                "meanRelayDegree": sum(post_degrees) / float(config.holder_degree),
                "meanCalibrationExposure": (
                    sum(candidate.exposure for candidate in score_candidates) / float(config.holder_degree)
                ),
                "meanProductScore": (
                    sum(candidate.product_score for candidate in score_candidates) / float(config.holder_degree)
                ),
                "candidatePool": len(candidates),
                "utilityBandPool": sum(candidate.in_band for candidate in candidates),
                "selectionEvaluations": 0 if policy == "sham" else (
                    sum(candidate.in_band for candidate in candidates)
                    if policy in ("bandexposure", "routeband")
                    else len(candidates)
                ),
            }
        )
    return summaries


def _write_csv(path: Path, fieldnames: Sequence[str], rows: Iterable[Mapping[str, object]]) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames, lineterminator="\n")
        writer.writeheader()
        for row in rows:
            serialized: Dict[str, object] = {}
            for field in fieldnames:
                value = row[field]
                serialized[field] = float_text(value) if isinstance(value, float) else value
            writer.writerow(serialized)


def _write_json(path: Path, value: object) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, sort_keys=True, indent=2, ensure_ascii=True)
        handle.write("\n")


def _write_manifest(directory: Path, names: Sequence[str], manifest_name: str = "MANIFEST.sha256") -> str:
    rows: List[str] = []
    aggregate = hashlib.sha256()
    for name in sorted(names):
        digest = sha256_file(directory / name)
        rows.append(f"{digest}  {name}")
        aggregate.update(f"{digest}  {name}\n".encode("ascii"))
    manifest = directory / manifest_name
    manifest.write_text("\n".join(rows) + "\n", encoding="ascii")
    return aggregate.hexdigest()


def write_holder_outputs(
    directory: Path,
    graph: Graph,
    plan: HolderPlan,
    candidates: Sequence[Candidate],
    calibration_rows: Sequence[Mapping[str, object]],
    arms: Mapping[str, Sequence[int]],
    target_rows: Sequence[Mapping[str, object]],
    summaries: Sequence[Mapping[str, object]],
    config: ExperimentConfig,
) -> str:
    if directory.exists():
        raise ProtocolError(f"Refusing to overwrite holder output {directory}.")
    directory.mkdir(parents=True)
    metadata = {
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
    _write_json(directory / "metadata.json", metadata)
    _write_csv(
        directory / "calibration.csv",
        ("traceIndex", "sourceId", "streamSeedHex", "uniqueVisited", "messages", "expandedCount"),
        calibration_rows,
    )
    candidate_rows = [
        {
            "nodeId": candidate.node_id,
            "baseDegree": candidate.base_degree,
            "postDegree": candidate.post_degree,
            "traceIndices": "|".join(str(index) for index in candidate.trace_indices),
            "exposureCount": len(candidate.trace_indices),
            "exposure": candidate.exposure,
            "forwardingFactor": candidate.forwarding_factor,
            "productScore": candidate.product_score,
            "degreeUtility": candidate.degree_utility,
            "inUtilityBand": 1 if candidate.in_band else 0,
            "isIncumbent": 1 if candidate.is_incumbent else 0,
            "isRoot": 1 if candidate.is_root else 0,
            "candidateTieKey": candidate.tie_key,
        }
        for candidate in sorted(candidates, key=lambda item: item.node_id)
    ]
    _write_csv(
        directory / "candidates.csv",
        (
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
        ),
        candidate_rows,
    )
    by_node = {candidate.node_id: candidate for candidate in candidates}
    view = HolderGraph(graph, plan.holder_id)
    selection_rows: List[Dict[str, object]] = []
    for policy in ARMS:
        for rank, node in enumerate(arms[policy], start=1):
            candidate = by_node.get(node)
            selection_rows.append(
                {
                    "policy": policy,
                    "rank": rank,
                    "nodeId": node,
                    "baseDegree": view.residual_degree(node),
                    "postDegree": view.residual_degree(node) + 1,
                    "exposure": candidate.exposure if candidate else 0.0,
                    "forwardingFactor": candidate.forwarding_factor if candidate else 0.0,
                    "productScore": candidate.product_score if candidate else 0.0,
                    "degreeUtility": candidate.degree_utility if candidate else 0.0,
                    "inUtilityBand": 1 if candidate and candidate.in_band else 0,
                    "candidateTieKey": candidate.tie_key if candidate else "",
                }
            )
    _write_csv(
        directory / "selections.csv",
        (
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
        ),
        selection_rows,
    )
    _write_csv(
        directory / "target_probes.csv",
        ("policy", "probeIndex", "sourceId", "streamSeedHex", "success", "uniqueVisited", "messages"),
        target_rows,
    )
    _write_csv(
        directory / "summary.csv",
        (
            "snapshot",
            "holderId",
            "policy",
            "successes",
            "probes",
            "pHat",
            "meanVisited",
            "meanMessages",
            "meanRelayDegree",
            "meanCalibrationExposure",
            "meanProductScore",
            "candidatePool",
            "utilityBandPool",
            "selectionEvaluations",
        ),
        summaries,
    )
    names = (
        "metadata.json",
        "calibration.csv",
        "candidates.csv",
        "selections.csv",
        "target_probes.csv",
        "summary.csv",
    )
    return _write_manifest(directory, names)


def run_selected_holder(
    graph: Graph,
    plan: HolderPlan,
    output_directory: Path,
    config: ExperimentConfig,
) -> Tuple[List[Dict[str, object]], str]:
    candidates, calibration_rows = calibrate_candidates(graph, plan, config)
    arms = select_arms(graph, plan, candidates, config)
    target_rows = run_target_probes(graph, plan, arms, config)
    summaries = summarize_holder(graph, plan, candidates, arms, target_rows, config)
    digest = write_holder_outputs(
        output_directory,
        graph,
        plan,
        candidates,
        calibration_rows,
        arms,
        target_rows,
        summaries,
        config,
    )
    return summaries, digest


def holder_plan_rows(plans: Sequence[HolderPlan]) -> List[Dict[str, object]]:
    return [
        {
            "snapshot": plan.snapshot,
            "holderId": plan.holder_id,
            "holderKey": plan.holder_key,
            "observedDegree": plan.observed_degree,
            "feasible": 1 if plan.feasible else 0,
            "selected": 1 if plan.selected else 0,
            "reason": plan.reason,
            "incumbentCount": len(plan.incumbents),
            "rootCount": len(plan.roots),
            "candidatePool": len(plan.candidates),
            "utilityBandPool": len(plan.band_candidates),
            "candidateAdjacencyChecks": plan.candidate_adjacency_checks,
            "residualComponentCount": plan.residual_component_count,
            "residualLargestComponent": plan.residual_largest_component,
        }
        for plan in plans
    ]


PLAN_FIELDS: Tuple[str, ...] = (
    "snapshot",
    "holderId",
    "holderKey",
    "observedDegree",
    "feasible",
    "selected",
    "reason",
    "incumbentCount",
    "rootCount",
    "candidatePool",
    "utilityBandPool",
    "candidateAdjacencyChecks",
    "residualComponentCount",
    "residualLargestComponent",
)

SUMMARY_FIELDS: Tuple[str, ...] = (
    "snapshot",
    "holderId",
    "policy",
    "successes",
    "probes",
    "pHat",
    "meanVisited",
    "meanMessages",
    "meanRelayDegree",
    "meanCalibrationExposure",
    "meanProductScore",
    "candidatePool",
    "utilityBandPool",
    "selectionEvaluations",
)


def write_csv(path: Path, fieldnames: Sequence[str], rows: Iterable[Mapping[str, object]]) -> None:
    _write_csv(path, fieldnames, rows)


def write_json(path: Path, value: object) -> None:
    _write_json(path, value)


def write_manifest(directory: Path, names: Sequence[str], manifest_name: str = "MANIFEST.sha256") -> str:
    return _write_manifest(directory, names, manifest_name)

