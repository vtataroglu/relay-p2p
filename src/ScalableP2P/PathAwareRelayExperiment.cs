using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ScalableP2P
{
    partial class Graph
    {
        private sealed class PathAwareCandidate
        {
            public Node Node;
            public int BaseDegree;
            public List<int> TraceIndices = new List<int>();
            public double Exposure;
            public double ForwardingFactor;
            public double ProductScore;
            public double DegreeUtility;
            public double ObservedExposure;
            public double ObservedProductScore;
            // Holder connected, target blind: occupancy over ALL traces of the
            // connected pass, ignoring whether a trace reached the holder.
            // This is the missing cell of the 2x2 that separates isolation of
            // the holder from conditioning on success.
            public double ConnectedExposure;
            public double ConnectedProductScore;
            // Holder connected, successful traces only, but collected until
            // the number of SUCCESSFUL traces equals the calibration budget,
            // so effective sample size matches the target free arms.
            public double MatchedObservedExposure;
            public double MatchedObservedProductScore;
            // Same as ObservedExposure but a trace counts as successful when
            // the holder was REACHED rather than expanded, which is the
            // criterion a real search uses, and the plain and matched effective
            // K versions of it.
            public double VisitObservedExposure;
            public double VisitObservedProductScore;
            public double MatchedVisitObservedExposure;
            public double MatchedVisitObservedProductScore;
            // Coarse ranking: exposure quartile within the band, ties broken by
            // the deterministic key. Tests whether in-band candidates are
            // interchangeable enough that a weak per-candidate signal can still
            // produce a stable set-level gain.
            public double BandExposureQuartile;
        }

        private sealed class PathAwareArm
        {
            public string Name;
            public List<Node> Nodes;
            public double CalibrationObjective;
            public int SelectedSwaps;
            public int FinalChangedRelays;
            public long SelectionEvaluations;
            public bool CostEligible;
            public double UtilityBandFraction;
        }

        private sealed class PathAwareTraceResult
        {
            public HashSet<Node> Expanded;
            public int UniqueVisited;
            public int Messages;
            // A peer in the final frontier is visited but never expanded,
            // because the loop exits on the visit budget. Membership of
            // Expanded is therefore strictly stronger than being reached. A
            // real search succeeds on reaching the holder, so crediting a
            // target observing estimate needs the reached set as well.
            public HashSet<Node> Visited;
        }

        private sealed class PathAwareHolderAudit
        {
            public Node Holder;
            public int SafetyPrefilterHolders;
            public int PretreatmentDegree;
            public int PretreatmentItems;
            public int OriginalNeighborMinDegree;
            public double OriginalNeighborMeanDegree;
            public int OriginalNeighborMaxDegree;
            public string PreSaturationHash;
            public string PostSaturationHash;
        }

        public void RunPathAwareRelayExperiment(
            string summaryPath,
            int experimentSeed,
            int fanout,
            int calibrationProbes,
            int outcomeProbes,
            int visitBudget,
            int expectedLiveNodes,
            int backgroundProbes,
            int backgroundVisitBudget,
            bool confirmOnly,
            // When true, a fixed band that cannot fill the relay set is not a
            // fatal condition: the fixed band arms are omitted and the run
            // proceeds with the adaptively sliced arms. This is the regime the
            // adaptive slice exists for, so refusing to run in it would make
            // the mechanism untestable.
            bool dausOnly = false)
        {
            if (fanout <= 0 || calibrationProbes <= 0 || outcomeProbes <= 0 ||
                visitBudget <= 1 || backgroundProbes <= 0 ||
                backgroundVisitBudget <= 1)
                throw new ArgumentOutOfRangeException("path aware experiment budget");
            PathAwareAssertStreamPlan(
                experimentSeed, calibrationProbes, outcomeProbes,
                backgroundProbes);
            string outputDirectory = Path.GetDirectoryName(summaryPath) ?? ".";
            string outputStem = Path.GetFileNameWithoutExtension(summaryPath);
            string selectionPath = Path.Combine(
                outputDirectory, outputStem + "-selection.csv");
            string probePath = Path.Combine(
                outputDirectory, outputStem + "-probes.csv");
            string candidatePath = Path.Combine(
                outputDirectory, outputStem + "-candidates.csv");
            string summaryPartial = summaryPath + ".partial";
            string selectionPartial = selectionPath + ".partial";
            string probePartial = probePath + ".partial";
            string candidatePartial = candidatePath + ".partial";
            if (new string[] { summaryPath, selectionPath, probePath,
                    candidatePath, summaryPartial, selectionPartial,
                    probePartial, candidatePartial }
                .Any(File.Exists))
                throw new IOException(
                    "Refusing to overwrite complete or partial RouteBand output.");

            if (Trend != null || RewireEnabled)
                throw new InvalidOperationException(
                    "RouteBand requires a neutral graph without trend state or rewiring.");

            const int targetValue = 1000000007;
            const string implementationVersion = "routeband-v1";
            const int candidateRootCount = 16;
            const int candidateHopLimit = 2;
            const double routeBand90Fraction = 0.90;
            const double utilityBandFraction = 0.95;
            const double routeBand975Fraction = 0.975;
            PathAwareHolderAudit holderAudit = PathAwarePrepareNeutralHolder(
                experimentSeed, targetValue);
            Node holder = holderAudit.Holder;
            int targetReplicaCount = SweetSpotLiveNodes().Count(
                node => node.Item.Values.Contains(targetValue));
            if (targetReplicaCount != 1 || holder.Item.Values.Count != 1)
                throw new InvalidOperationException(
                    "RouteBand requires one target replica and one holder item.");

            int liveNodes;
            long baseEdges;
            SweetSpotValidateTopology(out liveNodes, out baseEdges);
            if (liveNodes != expectedLiveNodes)
                throw new InvalidOperationException("Path aware live node mismatch.");

            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException("Expected one saturated holder.");
            PathAwareAssertConnectedFeasible(holder, liveNodes, false);

            List<Node> baseNeighbors = SweetSpotNeighbours(holder);
            HashSet<Node> baseNeighborSet = new HashSet<Node>(baseNeighbors);
            string baseHash = SweetSpotTopologyHash();
            string topologyFamily = UniformLocalAttachment
                ? "uniform" : "popularity";

            List<Node> sourcePool = SweetSpotLiveNodes()
                .Where(n => n != holder)
                .OrderBy(n => n.NodeID)
                .ToList();
            List<PathAwareCandidate> candidates;
            Dictionary<Node, PathAwareCandidate> candidateByNode;
            long candidateAdjacencyChecks = 0;
            string candidateRootIds = "";
            long calibrationVisited = 0;
            long calibrationMessages = 0;
            List<string> calibrationRows = new List<string>();
            PositionPlace(holder, new List<Node>());
            try
            {
                List<Node> sampledPool = PathAwareSamplePool(
                    baseNeighbors, holder, experimentSeed, candidateRootCount,
                    candidateHopLimit,
                    out candidateAdjacencyChecks, out candidateRootIds);
                sampledPool = sampledPool.Concat(baseNeighbors)
                    .Distinct()
                    .OrderBy(n => n.NodeID)
                    .ToList();
                candidates = sampledPool
                    .Where(n => n != holder && n.Degree >= 3 &&
                                n.Degree < maxDegree)
                    .Select(n => new PathAwareCandidate {
                        Node = n, BaseDegree = n.Degree
                    })
                    .ToList();
                if (candidates.Count < maxDegree)
                    throw new InvalidOperationException(
                        "Insufficient path aware candidates.");
                PathAwareAssertConnectedFeasible(holder, liveNodes, true);

                candidateByNode = candidates.ToDictionary(c => c.Node, c => c);
                Random calibrationSourceRandom = new Random(
                    PathAwareStreamSeed(experimentSeed, 1));
                for (int probeIndex = 0;
                     probeIndex < calibrationProbes;
                     probeIndex++)
                {
                    Node source = sourcePool[
                        calibrationSourceRandom.Next(sourcePool.Count)];
                    int traceSeed = PathAwareStreamSeed(
                        experimentSeed, 16 + probeIndex);
                    Random traceRandom = new Random(traceSeed);
                    PathAwareTraceResult trace = PathAwareExpandedTrace(
                        source, fanout, visitBudget, traceRandom);
                    calibrationVisited += trace.UniqueVisited;
                    calibrationMessages += trace.Messages;
                    calibrationRows.Add(String.Join(",", new string[] {
                        experimentSeed.ToString(CultureInfo.InvariantCulture),
                        UniformLocalAttachment ? "uniform" : "popularity",
                        "calibration", "calibration",
                        probeIndex.ToString(CultureInfo.InvariantCulture),
                        source.NodeID.ToString(CultureInfo.InvariantCulture),
                        "0", traceSeed.ToString(CultureInfo.InvariantCulture),
                        "0", trace.UniqueVisited.ToString(CultureInfo.InvariantCulture),
                        trace.Messages.ToString(CultureInfo.InvariantCulture),
                        trace.Expanded.Count.ToString(CultureInfo.InvariantCulture),
                        String.Join("|", trace.Expanded
                            .Select(node => node.NodeID)
                            .OrderBy(nodeId => nodeId)
                            .Select(nodeId => nodeId.ToString(
                                CultureInfo.InvariantCulture)))
                    }));
                    foreach (Node node in trace.Expanded)
                    {
                        PathAwareCandidate candidate;
                        if (candidateByNode.TryGetValue(node, out candidate))
                            candidate.TraceIndices.Add(probeIndex);
                    }
                }

                foreach (PathAwareCandidate candidate in candidates)
                {
                    candidate.Exposure = candidate.TraceIndices.Count /
                        (double)calibrationProbes;
                    // With the holder absent, BaseDegree is exactly the
                    // number of eligible nonparent edges after the holder
                    // edge is restored.
                    candidate.ForwardingFactor = Math.Min(
                        1.0, fanout / (double)candidate.BaseDegree);
                    candidate.ProductScore = candidate.Exposure *
                        candidate.ForwardingFactor;
                    candidate.DegreeUtility = RelayUtility(
                        fanout, candidate.BaseDegree + 1);
                }
            }
            finally
            {
                SweetSpotRestoreHolder(holder, baseNeighbors);
            }
            if (SweetSpotTopologyHash() != baseHash)
                throw new InvalidOperationException(
                    "Holder absent calibration restore failed.");

            // Target observing calibration.
            //
            // RouteBand's distinguishing constraint is that no calibration
            // trace can contain the target, because the holder is isolated
            // while traces are collected. Related methods do not accept that
            // constraint: interest based shortcuts learn from successful
            // transfers of the item, path traceable routing records gain along
            // paths that reached the target, and query guidance uses prior
            // content distribution records. The constraint has therefore never
            // been priced.
            //
            // This pass collects a second set of traces with the holder
            // connected to its incumbents, so traces can and do reach the
            // target, and credits candidates that appear on traces which found
            // it. The definition is deliberately generous to the target
            // observing arm: traces run to the visit budget rather than
            // stopping at the holder, so it sees more of the network than a
            // real target search would. If target free calibration matches it
            // anyway, the constraint is cheap.
            //
            // TARGETBAND as originally defined differs from RouteBand in TWO
            // ways at once: (a) the holder is connected while the traces are
            // collected, and (b) the estimate uses successful traces only.
            // Attributing its failure to (b) requires the missing cell of the
            // 2x2, so this pass also builds a target BLIND estimate over ALL
            // traces of the same connected pass. If conditioning on success is
            // the active ingredient, that cell lands near RouteBand; if
            // isolation itself is, it lands near TARGETBAND.
            //
            // Discovery under the incumbent configuration is roughly one half,
            // so the successful subset of K traces is an effective sample of
            // about K/2. Section on calibration budget shows sample size alone
            // is worth points, so the pass continues past K until K SUCCESSFUL
            // traces exist, giving a matched effective K contrast.
            int targetObservingProbes = 0;
            int targetObservingSuccesses = 0;
            long targetObservingVisited = 0;
            long targetObservingMessages = 0;
            int matchedObservingProbes = 0;
            int matchedObservingSuccesses = 0;
            int visitObservingSuccesses = 0;
            int matchedVisitObservingProbes = 0;
            int matchedVisitObservingSuccesses = 0;
            {
                int observedProbeCeiling = Math.Min(
                    4000, calibrationProbes * 10);
                Random observedSourceRandom = new Random(
                    PathAwareObservedStreamSeed(experimentSeed, 0));
                List<HashSet<Node>> firstPassTraces =
                    new List<HashSet<Node>>();
                List<HashSet<Node>> successfulTraces =
                    new List<HashSet<Node>>();
                List<HashSet<Node>> matchedSuccessfulTraces =
                    new List<HashSet<Node>>();
                List<HashSet<Node>> visitSuccessfulTraces =
                    new List<HashSet<Node>>();
                List<HashSet<Node>> matchedVisitSuccessfulTraces =
                    new List<HashSet<Node>>();
                for (int probeIndex = 0;
                     probeIndex < observedProbeCeiling;
                     probeIndex++)
                {
                    bool withinFirstPass = probeIndex < calibrationProbes;
                    bool matchedStillOpen =
                        matchedSuccessfulTraces.Count < calibrationProbes;
                    bool matchedVisitStillOpen =
                        matchedVisitSuccessfulTraces.Count < calibrationProbes;
                    if (!withinFirstPass && !matchedStillOpen &&
                        !matchedVisitStillOpen) break;
                    Node source = sourcePool[
                        observedSourceRandom.Next(sourcePool.Count)];
                    Random traceRandom = new Random(
                        PathAwareObservedStreamSeed(experimentSeed, 1 + probeIndex));
                    PathAwareTraceResult trace = PathAwareExpandedTrace(
                        source, fanout, visitBudget, traceRandom);
                    bool expandedHolder = trace.Expanded.Contains(holder);
                    bool reachedHolder = trace.Visited.Contains(holder);
                    if (withinFirstPass)
                    {
                        targetObservingProbes++;
                        targetObservingVisited += trace.UniqueVisited;
                        targetObservingMessages += trace.Messages;
                        firstPassTraces.Add(trace.Expanded);
                        if (expandedHolder)
                        {
                            targetObservingSuccesses++;
                            successfulTraces.Add(trace.Expanded);
                        }
                        if (reachedHolder)
                        {
                            visitObservingSuccesses++;
                            visitSuccessfulTraces.Add(trace.Expanded);
                        }
                    }
                    if (matchedStillOpen)
                    {
                        matchedObservingProbes++;
                        if (expandedHolder)
                            matchedSuccessfulTraces.Add(trace.Expanded);
                    }
                    if (matchedVisitStillOpen)
                    {
                        matchedVisitObservingProbes++;
                        if (reachedHolder)
                            matchedVisitSuccessfulTraces.Add(trace.Expanded);
                    }
                }
                matchedObservingSuccesses = matchedSuccessfulTraces.Count;
                matchedVisitObservingSuccesses =
                    matchedVisitSuccessfulTraces.Count;
                foreach (PathAwareCandidate candidate in candidates)
                {
                    int hits = successfulTraces.Count(
                        expanded => expanded.Contains(candidate.Node));
                    candidate.ObservedExposure = targetObservingSuccesses > 0
                        ? hits / (double)targetObservingSuccesses
                        : 0.0;
                    candidate.ObservedProductScore =
                        candidate.ObservedExposure * candidate.ForwardingFactor;

                    int blindHits = firstPassTraces.Count(
                        expanded => expanded.Contains(candidate.Node));
                    candidate.ConnectedExposure = firstPassTraces.Count > 0
                        ? blindHits / (double)firstPassTraces.Count
                        : 0.0;
                    candidate.ConnectedProductScore =
                        candidate.ConnectedExposure * candidate.ForwardingFactor;

                    int matchedHits = matchedSuccessfulTraces.Count(
                        expanded => expanded.Contains(candidate.Node));
                    candidate.MatchedObservedExposure =
                        matchedSuccessfulTraces.Count > 0
                            ? matchedHits / (double)matchedSuccessfulTraces.Count
                            : 0.0;
                    candidate.MatchedObservedProductScore =
                        candidate.MatchedObservedExposure *
                        candidate.ForwardingFactor;

                    int visitHits = visitSuccessfulTraces.Count(
                        expanded => expanded.Contains(candidate.Node));
                    candidate.VisitObservedExposure =
                        visitSuccessfulTraces.Count > 0
                            ? visitHits / (double)visitSuccessfulTraces.Count
                            : 0.0;
                    candidate.VisitObservedProductScore =
                        candidate.VisitObservedExposure *
                        candidate.ForwardingFactor;

                    int matchedVisitHits = matchedVisitSuccessfulTraces.Count(
                        expanded => expanded.Contains(candidate.Node));
                    candidate.MatchedVisitObservedExposure =
                        matchedVisitSuccessfulTraces.Count > 0
                            ? matchedVisitHits /
                              (double)matchedVisitSuccessfulTraces.Count
                            : 0.0;
                    candidate.MatchedVisitObservedProductScore =
                        candidate.MatchedVisitObservedExposure *
                        candidate.ForwardingFactor;
                }
            }
            long routeTelemetryBytes = (long)candidates.Count *
                (((calibrationProbes + 7) / 8) + 4);
            // The published ceilings (1,000,000 visits and 1,100,000 messages)
            // were written for the frozen setting of 400 calibration probes at
            // a visit budget of 2,000, where the structural maximum is
            // 400 x 2,000 = 800,000. Expressing them per probe keeps exactly
            // that headroom ratio while allowing the calibration budget itself
            // to be varied, which the fixed constants would otherwise forbid.
            long visitCeiling = (long)calibrationProbes * visitBudget * 5L / 4L;
            long messageCeiling = visitCeiling * 11L / 10L;
            long telemetryCeiling = 1048576L * calibrationProbes / 400L;
            if (calibrationVisited > visitCeiling ||
                calibrationMessages > messageCeiling ||
                routeTelemetryBytes > telemetryCeiling)
                throw new InvalidOperationException(
                    "RouteBand frozen calibration cost ceiling exceeded: " +
                    "visited=" + calibrationVisited + ", messages=" +
                    calibrationMessages + ", telemetryBytes=" +
                    routeTelemetryBytes + ".");

            List<PathAwareArm> arms = new List<PathAwareArm>();
            arms.Add(new PathAwareArm {
                Name = "sham",
                Nodes = new List<Node>(baseNeighbors),
                CalibrationObjective = PathAwareObjective(
                    baseNeighbors, candidateByNode, calibrationProbes, fanout),
                SelectionEvaluations = 0
            });
            arms.Add(PathAwareRankedArm(
                "maxdeg", candidates, maxDegree,
                c => c.BaseDegree, calibrationProbes, candidateByNode, fanout,
                experimentSeed, 41));
            arms.Add(PathAwareRankedArm(
                "degreeutility", candidates, maxDegree,
                c => c.DegreeUtility, calibrationProbes, candidateByNode, fanout,
                experimentSeed, 41));
            arms.Add(PathAwareRankedArm(
                "exposure", candidates, maxDegree,
                c => c.Exposure, calibrationProbes, candidateByNode, fanout,
                experimentSeed, 41));
            double maximumDegreeUtility = RelayUtility(fanout, fanout + 1);
            List<PathAwareCandidate> routeBand90Candidates = candidates
                .Where(c => c.DegreeUtility >=
                            routeBand90Fraction * maximumDegreeUtility)
                .ToList();
            List<PathAwareCandidate> targetBandCandidates = candidates
                .Where(c => c.DegreeUtility >=
                            utilityBandFraction * maximumDegreeUtility)
                .ToList();
            List<PathAwareCandidate> routeBand975Candidates = candidates
                .Where(c => c.DegreeUtility >=
                            routeBand975Fraction * maximumDegreeUtility)
                .ToList();
            // Coarse binned exposure within the primary band. The per candidate
            // exposure estimate is noisy where the ranking operates, yet the
            // set level gain is stable. If in-band candidates are largely
            // interchangeable above a coarse threshold, then a rule that keeps
            // only the quartile and discards the within-quartile order should
            // retain most of the gain. Quartile 4 is the most exposed quarter.
            {
                List<double> bandExposures = targetBandCandidates
                    .Select(c => c.Exposure)
                    .OrderBy(value => value)
                    .ToList();
                foreach (PathAwareCandidate candidate in candidates)
                    candidate.BandExposureQuartile = 0.0;
                if (bandExposures.Count > 0)
                {
                    double[] cut = new double[3];
                    for (int q = 1; q <= 3; q++)
                    {
                        int index = (int)Math.Floor(
                            q * 0.25 * bandExposures.Count);
                        if (index >= bandExposures.Count)
                            index = bandExposures.Count - 1;
                        cut[q - 1] = bandExposures[index];
                    }
                    foreach (PathAwareCandidate candidate in targetBandCandidates)
                    {
                        double quartile = 1.0;
                        for (int q = 0; q < 3; q++)
                            if (candidate.Exposure >= cut[q]) quartile = q + 2.0;
                        candidate.BandExposureQuartile = quartile;
                    }
                }
            }
            // Dynamic Adaptive Utility Slicing. A fixed threshold fails outright
            // when the local degree distribution does not populate the band: the
            // selector cannot fill its relay set and the run stops. DAUS instead
            // relaxes the threshold in fixed steps until the eligible set holds
            // at least dausPoolFactor * maxDegree candidates, then ranks that set
            // with the ordinary RouteBand score. Where the fixed band is already
            // populated, tau* stays at 0.95 and DAUS reduces to RouteBand.
            const double dausStep = 0.05;
            const double dausMinFraction = 0.50;
            const double dausPoolFactor = 2.0;
            double dausFraction = utilityBandFraction;
            List<PathAwareCandidate> dausCandidates = targetBandCandidates;
            while (dausCandidates.Count < (int)(dausPoolFactor * maxDegree) &&
                   dausFraction > dausMinFraction)
            {
                dausFraction -= dausStep;
                double floorUtility = dausFraction * maximumDegreeUtility;
                dausCandidates = candidates
                    .Where(c => c.DegreeUtility >= floorUtility)
                    .ToList();
            }

            bool fixedBandsFeasible =
                routeBand90Candidates.Count >= maxDegree &&
                targetBandCandidates.Count >= maxDegree &&
                routeBand975Candidates.Count >= maxDegree;
            if (!fixedBandsFeasible && !dausOnly)
                throw new InvalidOperationException(
                    "Insufficient utility band candidates for confirmatory selection.");
            if (dausOnly && dausCandidates.Count < maxDegree)
                throw new InvalidOperationException(
                    "Adaptive utility slicing could not reach the relay set size " +
                    "even at the minimum threshold: candidates=" +
                    dausCandidates.Count + ", required=" + maxDegree + ".");
            HashSet<int> sampledRootIds = new HashSet<int>(
                candidateRootIds.Split(
                    new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(
                    value, CultureInfo.InvariantCulture)));
            List<string> candidateRows = candidates
                .OrderBy(candidate => candidate.Node.NodeID)
                .Select(candidate => String.Join(",", new string[] {
                    experimentSeed.ToString(CultureInfo.InvariantCulture),
                    topologyFamily,
                    fanout.ToString(CultureInfo.InvariantCulture),
                    candidate.Node.NodeID.ToString(CultureInfo.InvariantCulture),
                    candidate.BaseDegree.ToString(CultureInfo.InvariantCulture),
                    String.Join("|", candidate.TraceIndices.Select(index =>
                        index.ToString(CultureInfo.InvariantCulture))),
                    candidate.Exposure.ToString("R", CultureInfo.InvariantCulture),
                    candidate.ForwardingFactor.ToString(
                        "R", CultureInfo.InvariantCulture),
                    candidate.ProductScore.ToString("R", CultureInfo.InvariantCulture),
                    candidate.DegreeUtility.ToString("R", CultureInfo.InvariantCulture),
                    // The raw schema keeps the primary 0.95 membership flag.
                    // The sensitivity memberships are exactly recoverable from
                    // degreeUtility and the maximum utility within each seed.
                    targetBandCandidates.Contains(candidate) ? "1" : "0",
                    baseNeighborSet.Contains(candidate.Node) ? "1" : "0",
                    sampledRootIds.Contains(candidate.Node.NodeID) ? "1" : "0"
                })).ToList();
            arms.Add(PathAwareRankedArm(
                "product", candidates, maxDegree,
                c => c.ProductScore, calibrationProbes, candidateByNode, fanout,
                experimentSeed, 41));
            // DAUS ranks the adaptively sliced set with the ordinary RouteBand
            // score, and is paired with a uniform draw from the same set so the
            // value of ranking is isolated at whatever threshold DAUS chose.
            arms.Add(PathAwareRankedArm(
                "daus", dausCandidates, maxDegree,
                c => c.ProductScore, calibrationProbes, candidateByNode, fanout,
                experimentSeed, 41, dausFraction));
            arms.Add(PathAwareBandRandomArm(
                "dausrandom", dausCandidates, maxDegree, calibrationProbes,
                candidateByNode, fanout, experimentSeed, 3703, dausFraction));
            if (fixedBandsFeasible)
            {
                arms.Add(PathAwareRankedArm(
                    "bandexposure", targetBandCandidates, maxDegree,
                    c => c.Exposure, calibrationProbes, candidateByNode, fanout,
                    experimentSeed, 41, utilityBandFraction));
                arms.Add(PathAwareRankedArm(
                    "routeband", targetBandCandidates, maxDegree,
                    c => c.ProductScore, calibrationProbes, candidateByNode, fanout,
                    experimentSeed, 41, utilityBandFraction));
                // Same rule, same band, same tie key; the only difference is
                // that the exposure estimate was allowed to observe the target.
                arms.Add(PathAwareRankedArm(
                    "targetband", targetBandCandidates, maxDegree,
                    c => c.ObservedProductScore, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                arms.Add(PathAwareRankedArm(
                    "targetexposure", targetBandCandidates, maxDegree,
                    c => c.ObservedExposure, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                // Missing cell of the 2x2. Holder connected as in TARGETBAND,
                // but the estimate is blind to the target: it counts occupancy
                // over every trace of the connected pass rather than over the
                // successful subset.
                arms.Add(PathAwareRankedArm(
                    "connectedband", targetBandCandidates, maxDegree,
                    c => c.ConnectedProductScore, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                arms.Add(PathAwareRankedArm(
                    "connectedexposure", targetBandCandidates, maxDegree,
                    c => c.ConnectedExposure, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                // Target observing at matched effective K: the pass ran until
                // the number of successful traces equalled the calibration
                // budget, so the estimate is built on as many traces as the
                // target free estimate rather than on the surviving half.
                arms.Add(PathAwareRankedArm(
                    "targetbandk", targetBandCandidates, maxDegree,
                    c => c.MatchedObservedProductScore, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                arms.Add(PathAwareRankedArm(
                    "targetexposurek", targetBandCandidates, maxDegree,
                    c => c.MatchedObservedExposure, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                // Target observing under the criterion a real search uses: a
                // trace counts as successful when the holder was reached, not
                // when it was expanded. This is the more generous and the more
                // faithful definition of a content history trace.
                arms.Add(PathAwareRankedArm(
                    "targetvisitband", targetBandCandidates, maxDegree,
                    c => c.VisitObservedProductScore, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                arms.Add(PathAwareRankedArm(
                    "targetvisitexposure", targetBandCandidates, maxDegree,
                    c => c.VisitObservedExposure, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                arms.Add(PathAwareRankedArm(
                    "targetvisitbandk", targetBandCandidates, maxDegree,
                    c => c.MatchedVisitObservedProductScore, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                // Coarse binned ranking: exposure quartile only, order inside
                // the quartile settled by the deterministic tie key.
                arms.Add(PathAwareRankedArm(
                    "bandquartile", targetBandCandidates, maxDegree,
                    c => c.BandExposureQuartile, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                arms.Add(PathAwareGreedyArm(
                    "bandgreedy", targetBandCandidates, maxDegree,
                    calibrationProbes, candidateByNode, fanout, experimentSeed, 41,
                    utilityBandFraction));
                arms.Add(PathAwareRankedArm(
                    "routeband90", routeBand90Candidates, maxDegree,
                    c => c.ProductScore, calibrationProbes, candidateByNode, fanout,
                    experimentSeed, 41, routeBand90Fraction));
                arms.Add(PathAwareRankedArm(
                    "routeband975", routeBand975Candidates, maxDegree,
                    c => c.ProductScore, calibrationProbes, candidateByNode, fanout,
                    experimentSeed, 41, routeBand975Fraction));
            }
            arms.Add(PathAwareGreedyArm(
                "pathgreedy", candidates, maxDegree, calibrationProbes,
                candidateByNode, fanout, experimentSeed, 23));
            arms.Add(PathAwareSwapArm(
                "pathswap", baseNeighbors, candidates, maxDegree,
                calibrationProbes, candidateByNode, experimentSeed, 31));
            if (fixedBandsFeasible)
            {
                foreach (int swapLimit in new int[] { 1, 4, 8, 16, 32 })
                    arms.Add(PathAwareSwapArm(
                        "bandswap" + swapLimit, baseNeighbors,
                        targetBandCandidates, swapLimit, calibrationProbes,
                        candidateByNode, experimentSeed, 37 + swapLimit,
                        utilityBandFraction));
                arms.Add(PathAwareSwapArm(
                    "bandswap", baseNeighbors, targetBandCandidates, maxDegree,
                    calibrationProbes, candidateByNode, experimentSeed, 37,
                    utilityBandFraction));
            }

            List<PathAwareCandidate> shuffled = candidates
                .OrderBy(c => c.Node.NodeID).ToList();
            Random randomArm = new Random(
                PathAwareStreamSeed(experimentSeed, 3600));
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = randomArm.Next(i + 1);
                PathAwareCandidate temp = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = temp;
            }
            List<Node> randomNodes = shuffled.Take(maxDegree)
                .Select(c => c.Node).ToList();
            arms.Add(new PathAwareArm {
                Name = "random",
                Nodes = randomNodes,
                CalibrationObjective = PathAwareObjective(
                    randomNodes, candidateByNode, calibrationProbes, fanout),
                SelectionEvaluations = candidates.Count
            });

            // Uniform draw restricted to the primary utility band. This is the
            // no information control for the band: it fixes prospective degree
            // exactly as RouteBand does but supplies no route exposure, so the
            // paired contrast isolates the ranking value of exposure rather
            // than the value of the band. Ranking by the forwarding factor
            // alone cannot serve this role, because inside the band that
            // factor is monotone decreasing in degree and therefore ties every
            // candidate at the band's lowest degree.
            // Band width is a deterministic function of fanout at a fixed
            // threshold, so a fanout sweep alone cannot separate the two. The
            // threshold arms below vary band width at FIXED fanout: at m=7 the
            // 0.975, 0.95 and 0.90 thresholds admit 2, 5 and 29 prospective
            // degrees respectively. Comparing RouteBand with BANDRANDOM at each
            // threshold identifies band width independently of fanout.
            if (fixedBandsFeasible)
            {
                arms.Add(PathAwareBandRandomArm(
                    "bandrandom", targetBandCandidates, maxDegree, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 3700,
                    utilityBandFraction));
                // Two further independent realisations of the same uniform
                // in-band draw. The primary BANDRANDOM arm is a single draw per
                // graph, so these quantify how much of a paired contrast
                // against it is draw-to-draw noise.
                arms.Add(PathAwareBandRandomArm(
                    "bandrandomb", targetBandCandidates, maxDegree, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 3704,
                    utilityBandFraction));
                arms.Add(PathAwareBandRandomArm(
                    "bandrandomc", targetBandCandidates, maxDegree, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 3705,
                    utilityBandFraction));
                arms.Add(PathAwareBandRandomArm(
                    "bandrandom90", routeBand90Candidates, maxDegree, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 3701,
                    routeBand90Fraction));
                arms.Add(PathAwareBandRandomArm(
                    "bandrandom975", routeBand975Candidates, maxDegree, calibrationProbes,
                    candidateByNode, fanout, experimentSeed, 3702,
                    routeBand975Fraction));
            }

            foreach (PathAwareArm arm in arms)
            {
                arm.FinalChangedRelays = arm.Nodes.Count(
                    node => !baseNeighborSet.Contains(node));
                arm.CostEligible = !PathAwareUsesCalibration(arm.Name) ||
                    arm.SelectionEvaluations <= 500000L;
            }
            if (confirmOnly)
            {
                if (!fixedBandsFeasible)
                    throw new InvalidOperationException(
                        "The confirmatory arm set requires the fixed utility bands.");
                string[] confirmatoryNames = new string[] {
                    "sham", "maxdeg", "degreeutility", "exposure",
                    "bandexposure", "product", "routeband", "bandgreedy",
                    "routeband90", "routeband975"
                };
                Dictionary<string, PathAwareArm> armByName = arms.ToDictionary(
                    arm => arm.Name, arm => arm);
                arms = confirmatoryNames.Select(name => armByName[name]).ToList();
            }

            Node[] outcomeSources = new Node[outcomeProbes];
            Random outcomeSourceRandom = new Random(
                PathAwareStreamSeed(experimentSeed, 600));
            for (int i = 0; i < outcomeProbes; i++)
                outcomeSources[i] = sourcePool[
                    outcomeSourceRandom.Next(sourcePool.Count)];
            Node[] backgroundSources = new Node[backgroundProbes];
            int[] backgroundValues = new int[backgroundProbes];
            Random backgroundSourceRandom = new Random(
                PathAwareStreamSeed(experimentSeed, 2800));
            for (int i = 0; i < backgroundProbes; i++)
            {
                backgroundSources[i] = sourcePool[
                    backgroundSourceRandom.Next(sourcePool.Count)];
                backgroundValues[i] = (i % 100) + 1;
            }
            Random backgroundOrderRandom = new Random(
                PathAwareStreamSeed(experimentSeed, 2801));
            for (int i = backgroundValues.Length - 1; i > 0; i--)
            {
                int j = backgroundOrderRandom.Next(i + 1);
                int temp = backgroundValues[i];
                backgroundValues[i] = backgroundValues[j];
                backgroundValues[j] = temp;
            }

            List<string> summaryRows = new List<string>();
            List<string> selectionRows = new List<string>();
            List<string> probeRows = new List<string>(calibrationRows);
            foreach (PathAwareArm arm in arms)
            {
                string armHash = null;
                try
                {
                    PositionPlace(holder, arm.Nodes);
                    if (holder.Degree != maxDegree)
                        throw new InvalidOperationException(
                            "Path aware holder degree invariant failed.");
                    int armNodes;
                    long armEdges;
                    SweetSpotValidateTopology(out armNodes, out armEdges);
                    if (armNodes != liveNodes || armEdges != baseEdges)
                        throw new InvalidOperationException(
                            "Path aware node or edge invariant failed.");
                    PathAwareAssertConnectedFeasible(holder, liveNodes, false);
                    armHash = SweetSpotTopologyHash();

                    int successes = 0;
                    long visited = 0;
                    long messages = 0;
                    for (int i = 0; i < outcomeProbes; i++)
                    {
                        int probeSeed = PathAwareStreamSeed(
                            experimentSeed, 700 + i);
                        Random probeRandom = new Random(probeSeed);
                        SweetSpotProbeResult result = SweetSpotBudgetSearch(
                            outcomeSources[i], targetValue, fanout,
                            visitBudget, probeRandom);
                        if (result.Success) successes++;
                        visited += result.UniqueVisited;
                        messages += result.Messages;
                        probeRows.Add(String.Join(",", new string[] {
                            experimentSeed.ToString(CultureInfo.InvariantCulture),
                            topologyFamily, arm.Name, "target",
                            i.ToString(CultureInfo.InvariantCulture),
                            outcomeSources[i].NodeID.ToString(CultureInfo.InvariantCulture),
                            targetValue.ToString(CultureInfo.InvariantCulture),
                            probeSeed.ToString(CultureInfo.InvariantCulture),
                            result.Success ? "1" : "0",
                            result.UniqueVisited.ToString(CultureInfo.InvariantCulture),
                            result.Messages.ToString(CultureInfo.InvariantCulture),
                            "0", ""
                        }));
                    }
                    int backgroundSuccesses = 0;
                    long backgroundVisited = 0;
                    long backgroundMessages = 0;
                    for (int i = 0; i < backgroundProbes; i++)
                    {
                        int probeSeed = PathAwareStreamSeed(
                            experimentSeed, 3000 + i);
                        Random probeRandom = new Random(probeSeed);
                        SweetSpotProbeResult result = SweetSpotBudgetSearch(
                            backgroundSources[i], backgroundValues[i], fanout,
                            backgroundVisitBudget, probeRandom);
                        if (result.Success) backgroundSuccesses++;
                        backgroundVisited += result.UniqueVisited;
                        backgroundMessages += result.Messages;
                        probeRows.Add(String.Join(",", new string[] {
                            experimentSeed.ToString(CultureInfo.InvariantCulture),
                            topologyFamily, arm.Name, "background",
                            i.ToString(CultureInfo.InvariantCulture),
                            backgroundSources[i].NodeID.ToString(CultureInfo.InvariantCulture),
                            backgroundValues[i].ToString(CultureInfo.InvariantCulture),
                            probeSeed.ToString(CultureInfo.InvariantCulture),
                            result.Success ? "1" : "0",
                            result.UniqueVisited.ToString(CultureInfo.InvariantCulture),
                            result.Messages.ToString(CultureInfo.InvariantCulture),
                            "0", ""
                        }));
                    }
                    if (SweetSpotTopologyHash() != armHash)
                        throw new InvalidOperationException(
                            "Outcome probes changed the graph.");

                    double meanDegree = arm.Nodes.Average(n => (double)n.Degree);
                    double meanExposure = arm.Nodes.Average(n => {
                        PathAwareCandidate c;
                        return candidateByNode.TryGetValue(n, out c)
                            ? c.Exposure : 0.0;
                    });
                    double meanProduct = arm.Nodes.Average(n => {
                        PathAwareCandidate c;
                        return candidateByNode.TryGetValue(n, out c)
                            ? c.ProductScore : 0.0;
                    });
                    bool usesCalibration = PathAwareUsesCalibration(arm.Name);
                    bool usesCandidatePool = arm.Name != "sham";
                    summaryRows.Add(String.Join(",", new string[] {
                        experimentSeed.ToString(CultureInfo.InvariantCulture),
                        implementationVersion,
                        topologyFamily,
                        liveNodes.ToString(CultureInfo.InvariantCulture),
                        baseEdges.ToString(CultureInfo.InvariantCulture),
                        minDegree.ToString(CultureInfo.InvariantCulture),
                        maxDegree.ToString(CultureInfo.InvariantCulture),
                        confirmOnly ? "1" : "0",
                        targetValue.ToString(CultureInfo.InvariantCulture),
                        targetReplicaCount.ToString(CultureInfo.InvariantCulture),
                        holder.Item.Values.Count.ToString(CultureInfo.InvariantCulture),
                        candidateRootCount.ToString(CultureInfo.InvariantCulture),
                        candidateHopLimit.ToString(CultureInfo.InvariantCulture),
                        arm.UtilityBandFraction.ToString(
                            "R", CultureInfo.InvariantCulture),
                        fanout.ToString(CultureInfo.InvariantCulture),
                        arm.Name,
                        calibrationProbes.ToString(CultureInfo.InvariantCulture),
                        visitBudget.ToString(CultureInfo.InvariantCulture),
                        outcomeProbes.ToString(CultureInfo.InvariantCulture),
                        successes.ToString(CultureInfo.InvariantCulture),
                        (successes / (double)outcomeProbes).ToString(
                            "R", CultureInfo.InvariantCulture),
                        visitBudget.ToString(CultureInfo.InvariantCulture),
                        candidates.Count.ToString(CultureInfo.InvariantCulture),
                        arm.CalibrationObjective.ToString(
                            "R", CultureInfo.InvariantCulture),
                        arm.SelectedSwaps.ToString(CultureInfo.InvariantCulture),
                        arm.FinalChangedRelays.ToString(CultureInfo.InvariantCulture),
                        meanDegree.ToString("R", CultureInfo.InvariantCulture),
                        meanExposure.ToString("R", CultureInfo.InvariantCulture),
                        meanProduct.ToString("R", CultureInfo.InvariantCulture),
                        (visited / (double)outcomeProbes).ToString(
                            "R", CultureInfo.InvariantCulture),
                        (messages / (double)outcomeProbes).ToString(
                            "R", CultureInfo.InvariantCulture),
                        backgroundProbes.ToString(CultureInfo.InvariantCulture),
                        backgroundSuccesses.ToString(CultureInfo.InvariantCulture),
                        (backgroundSuccesses / (double)backgroundProbes).ToString(
                            "R", CultureInfo.InvariantCulture),
                        backgroundVisitBudget.ToString(CultureInfo.InvariantCulture),
                        (backgroundVisited / (double)backgroundProbes).ToString(
                            "R", CultureInfo.InvariantCulture),
                        (backgroundMessages / (double)backgroundProbes).ToString(
                            "R", CultureInfo.InvariantCulture),
                        (usesCalibration
                            ? calibrationVisited / (double)calibrationProbes : 0.0)
                            .ToString(
                            "R", CultureInfo.InvariantCulture),
                        (usesCalibration
                            ? calibrationMessages / (double)calibrationProbes : 0.0)
                            .ToString(
                            "R", CultureInfo.InvariantCulture),
                        (usesCandidatePool ? candidateAdjacencyChecks : 0L)
                            .ToString(CultureInfo.InvariantCulture),
                        (usesCalibration ? routeTelemetryBytes :
                            (usesCandidatePool ? (long)candidates.Count * 4L : 0L))
                            .ToString(CultureInfo.InvariantCulture),
                        arm.SelectionEvaluations.ToString(CultureInfo.InvariantCulture),
                        arm.CostEligible ? "1" : "0",
                        // Effective sample size of the target observing
                        // estimate. The first pass draws targetObservingProbes
                        // traces of which targetObservingSuccesses reached the
                        // holder, and only those enter the estimate, so the
                        // effective K of TARGETBAND is the success count and
                        // not the nominal calibration budget. The matched
                        // columns record the pass that continued until the
                        // success count equalled the budget.
                        targetObservingProbes.ToString(CultureInfo.InvariantCulture),
                        targetObservingSuccesses.ToString(CultureInfo.InvariantCulture),
                        targetObservingVisited.ToString(CultureInfo.InvariantCulture),
                        targetObservingMessages.ToString(CultureInfo.InvariantCulture),
                        matchedObservingProbes.ToString(CultureInfo.InvariantCulture),
                        matchedObservingSuccesses.ToString(CultureInfo.InvariantCulture),
                        visitObservingSuccesses.ToString(CultureInfo.InvariantCulture),
                        matchedVisitObservingProbes.ToString(CultureInfo.InvariantCulture),
                        matchedVisitObservingSuccesses.ToString(CultureInfo.InvariantCulture),
                        candidateRootIds,
                        holder.NodeID.ToString(CultureInfo.InvariantCulture),
                        holderAudit.SafetyPrefilterHolders.ToString(CultureInfo.InvariantCulture),
                        holderAudit.PretreatmentDegree.ToString(CultureInfo.InvariantCulture),
                        holderAudit.PretreatmentItems.ToString(CultureInfo.InvariantCulture),
                        holderAudit.OriginalNeighborMinDegree.ToString(CultureInfo.InvariantCulture),
                        holderAudit.OriginalNeighborMeanDegree.ToString(
                            "R", CultureInfo.InvariantCulture),
                        holderAudit.OriginalNeighborMaxDegree.ToString(CultureInfo.InvariantCulture),
                        holderAudit.PreSaturationHash,
                        holderAudit.PostSaturationHash,
                        baseHash,
                        armHash
                    }));
                    for (int rank = 0; rank < arm.Nodes.Count; rank++)
                    {
                        Node node = arm.Nodes[rank];
                        PathAwareCandidate candidate;
                        candidateByNode.TryGetValue(node, out candidate);
                        selectionRows.Add(String.Join(",", new string[] {
                            experimentSeed.ToString(CultureInfo.InvariantCulture),
                            topologyFamily,
                            fanout.ToString(CultureInfo.InvariantCulture),
                            arm.Name,
                            (rank + 1).ToString(CultureInfo.InvariantCulture),
                            node.NodeID.ToString(CultureInfo.InvariantCulture),
                            node.Degree.ToString(CultureInfo.InvariantCulture),
                            (candidate == null ? 0.0 : candidate.Exposure)
                                .ToString("R", CultureInfo.InvariantCulture),
                            (candidate == null ? 0.0 : candidate.ForwardingFactor)
                                .ToString("R", CultureInfo.InvariantCulture),
                            (candidate == null ? 0.0 : candidate.ProductScore)
                                .ToString("R", CultureInfo.InvariantCulture)
                        }));
                    }
                }
                finally
                {
                    SweetSpotRestoreHolder(holder, baseNeighbors);
                }
                if (SweetSpotTopologyHash() != baseHash)
                    throw new InvalidOperationException("Path aware restore failed.");
            }

            Directory.CreateDirectory(outputDirectory);
            using (StreamWriter writer = new StreamWriter(probePartial))
            {
                writer.WriteLine(
                    "seed,family,policy,type,index,sourceId,targetValue," +
                    "rngSeed,success,visited,messages,expanded,expandedNodeIds");
                foreach (string row in probeRows) writer.WriteLine(row);
            }
            File.Move(probePartial, probePath);

            using (StreamWriter writer = new StreamWriter(candidatePartial))
            {
                writer.WriteLine(
                    "seed,family,fanout,nodeId,baseDegree,traceIndices,exposure," +
                    "forwardingFactor,productScore,degreeUtility,inUtilityBand," +
                    "isIncumbent,isSampledRoot");
                foreach (string row in candidateRows) writer.WriteLine(row);
            }
            File.Move(candidatePartial, candidatePath);

            using (StreamWriter writer = new StreamWriter(selectionPartial))
            {
                writer.WriteLine(
                    "seed,family,fanout,policy,rank,nodeId,postDegree," +
                    "calibrationExposure,forwardingFactor,productScore");
                foreach (string row in selectionRows) writer.WriteLine(row);
            }
            File.Move(selectionPartial, selectionPath);

            using (StreamWriter writer = new StreamWriter(summaryPartial))
            {
                writer.WriteLine(
                    "seed,implementationVersion,topologyFamily,liveNodes,baseEdges," +
                    "minDegree,degreeCutoff,confirmOnly,targetValue," +
                    "targetReplicaCount,holderPostItems,candidateRootCount," +
                    "candidateHopLimit,utilityBandFraction,fanout,policy," +
                    "calibrationProbes,calibrationVisitBudget,outcomeProbes,successes," +
                    "pHat,targetVisitBudget,candidatePool,calibrationObjective," +
                    "exchangeOperations,finalChangedRelays," +
                    "meanRelayDegree," +
                    "meanCalibrationExposure,meanProductScore,meanVisited," +
                    "meanMessages,backgroundProbes,backgroundSuccesses," +
                    "backgroundPHat,backgroundVisitBudget,backgroundMeanVisited," +
                    "backgroundMeanMessages,calibrationMeanVisited," +
                    "calibrationMeanMessages,candidateAdjacencyChecks," +
                    "candidateTelemetryBytes,selectionEvaluations,costEligible," +
                    "targetObservingProbes,targetObservingSuccesses," +
                    "targetObservingVisited,targetObservingMessages," +
                    "matchedObservingProbes,matchedObservingSuccesses," +
                    "visitObservingSuccesses,matchedVisitObservingProbes," +
                    "matchedVisitObservingSuccesses," +
                    "candidateRootIds,holderId," +
                    "safetyPrefilterHolders,holderPretreatmentDegree," +
                    "holderPretreatmentItems,originalNeighborMinDegree," +
                    "originalNeighborMeanDegree,originalNeighborMaxDegree," +
                    "preSaturationHash,postSaturationHash,baseHash,armHash");
                foreach (string row in summaryRows) writer.WriteLine(row);
            }
            File.Move(summaryPartial, summaryPath);
        }

        private PathAwareArm PathAwareRankedArm(
            string name,
            List<PathAwareCandidate> candidates,
            int count,
            Func<PathAwareCandidate, double> score,
            int calibrationProbes,
            Dictionary<Node, PathAwareCandidate> candidateByNode,
            int fanout,
            int experimentSeed,
            int tieRole,
            double utilityBandFraction = 0.0)
        {
            List<Node> selected = candidates
                .OrderByDescending(score)
                .ThenBy(c => PathAwareTieKey(c.Node, experimentSeed, tieRole))
                .Take(count)
                .Select(c => c.Node)
                .ToList();
            return new PathAwareArm {
                Name = name,
                Nodes = selected,
                CalibrationObjective = PathAwareObjective(
                    selected, candidateByNode, calibrationProbes, fanout),
                SelectedSwaps = 0,
                SelectionEvaluations = candidates.Count,
                // Zero is the exact no band threshold because relay utility
                // is nonnegative. Banded arms store their actual threshold.
                UtilityBandFraction = utilityBandFraction
            };
        }

        private PathAwareArm PathAwareGreedyArm(
            string name,
            List<PathAwareCandidate> candidates,
            int count,
            int calibrationProbes,
            Dictionary<Node, PathAwareCandidate> candidateByNode,
            int fanout,
            int experimentSeed,
            int tieRole,
            double utilityBandFraction = 0.0)
        {
            double[] residual = Enumerable.Repeat(1.0, calibrationProbes).ToArray();
            HashSet<Node> selectedSet = new HashSet<Node>();
            List<Node> selected = new List<Node>();
            long evaluations = 0;
            for (int step = 0; step < count; step++)
            {
                PathAwareCandidate best = null;
                double bestGain = double.NegativeInfinity;
                foreach (PathAwareCandidate candidate in candidates)
                {
                    if (selectedSet.Contains(candidate.Node)) continue;
                    evaluations++;
                    double gain = 0.0;
                    foreach (int traceIndex in candidate.TraceIndices)
                        gain += residual[traceIndex] * candidate.ForwardingFactor;
                    if (best == null || gain > bestGain + 1e-15 ||
                        (Math.Abs(gain - bestGain) <= 1e-15 &&
                         // RouteBand and BANDGREEDY use the same fixed
                         // candidate tie order.  Step
                         // dependent tie roles would introduce an avoidable
                         // difference unrelated to route overlap.
                         PathAwareTieKey(candidate.Node, experimentSeed, tieRole) <
                         PathAwareTieKey(best.Node, experimentSeed, tieRole)))
                    {
                        best = candidate;
                        bestGain = gain;
                    }
                }
                if (best == null)
                    throw new InvalidOperationException("Greedy selector exhausted candidates.");
                selected.Add(best.Node);
                selectedSet.Add(best.Node);
                foreach (int traceIndex in best.TraceIndices)
                    residual[traceIndex] *= 1.0 - best.ForwardingFactor;
            }
            return new PathAwareArm {
                Name = name,
                Nodes = selected,
                CalibrationObjective = residual.Average(x => 1.0 - x),
                SelectedSwaps = 0,
                SelectionEvaluations = evaluations,
                UtilityBandFraction = utilityBandFraction
            };
        }

        private PathAwareArm PathAwareSwapArm(
            string name,
            List<Node> incumbents,
            List<PathAwareCandidate> graftCandidates,
            int maxSwaps,
            int calibrationProbes,
            Dictionary<Node, PathAwareCandidate> candidateByNode,
            int experimentSeed,
            int tieRole,
            double utilityBandFraction = 0.0)
        {
            List<Node> current = new List<Node>(incumbents);
            HashSet<Node> currentSet = new HashSet<Node>(current);
            double currentObjective = PathAwareObjective(
                current, candidateByNode, calibrationProbes, 0);
            int swaps = 0;
            long evaluations = 0;
            for (int step = 0; step < maxSwaps; step++)
            {
                Node bestPrune = null;
                PathAwareCandidate bestGraft = null;
                double bestObjective = currentObjective;
                foreach (Node prune in current)
                {
                    double[] residual = Enumerable.Repeat(
                        1.0, calibrationProbes).ToArray();
                    foreach (Node retained in current)
                    {
                        if (retained == prune) continue;
                        PathAwareCandidate retainedCandidate;
                        if (!candidateByNode.TryGetValue(
                            retained, out retainedCandidate)) continue;
                        foreach (int traceIndex in retainedCandidate.TraceIndices)
                            residual[traceIndex] *=
                                1.0 - retainedCandidate.ForwardingFactor;
                    }
                    double withoutPrune = residual.Average(x => 1.0 - x);
                    foreach (PathAwareCandidate graft in graftCandidates)
                    {
                        if (currentSet.Contains(graft.Node)) continue;
                        evaluations++;
                        double gain = 0.0;
                        foreach (int traceIndex in graft.TraceIndices)
                            gain += residual[traceIndex] * graft.ForwardingFactor;
                        double objective = withoutPrune + gain / calibrationProbes;
                        ulong pairTieKey = PathAwarePairTieKey(
                            prune, graft.Node, experimentSeed, tieRole + step);
                        ulong bestPairTieKey = bestGraft == null
                            ? ulong.MaxValue
                            : PathAwarePairTieKey(bestPrune, bestGraft.Node,
                                experimentSeed, tieRole + step);
                        bool stablePairFallback = bestGraft != null &&
                            pairTieKey == bestPairTieKey &&
                            (prune.NodeID < bestPrune.NodeID ||
                             (prune.NodeID == bestPrune.NodeID &&
                              graft.Node.NodeID < bestGraft.Node.NodeID));
                        bool tieWins = bestGraft != null &&
                            Math.Abs(objective - bestObjective) <= 1e-15 &&
                            (pairTieKey < bestPairTieKey || stablePairFallback);
                        if (objective > bestObjective + 1e-15 || tieWins)
                        {
                            bestObjective = objective;
                            bestPrune = prune;
                            bestGraft = graft;
                        }
                    }
                }
                if (bestPrune == null || bestGraft == null ||
                    !(bestObjective > currentObjective + 1e-15)) break;
                current.Remove(bestPrune);
                currentSet.Remove(bestPrune);
                current.Add(bestGraft.Node);
                currentSet.Add(bestGraft.Node);
                currentObjective = bestObjective;
                swaps++;
            }
            return new PathAwareArm {
                Name = name,
                Nodes = current,
                CalibrationObjective = currentObjective,
                SelectedSwaps = swaps,
                SelectionEvaluations = evaluations,
                UtilityBandFraction = utilityBandFraction
            };
        }

        private double PathAwareObjective(
            List<Node> nodes,
            Dictionary<Node, PathAwareCandidate> candidateByNode,
            int calibrationProbes,
            int fanout)
        {
            double[] residual = Enumerable.Repeat(1.0, calibrationProbes).ToArray();
            foreach (Node node in nodes)
            {
                PathAwareCandidate candidate;
                if (!candidateByNode.TryGetValue(node, out candidate)) continue;
                foreach (int traceIndex in candidate.TraceIndices)
                    residual[traceIndex] *= 1.0 - candidate.ForwardingFactor;
            }
            return residual.Average(x => 1.0 - x);
        }

        // Uniform draw of maxDegree relays from an already band-restricted
        // candidate list. Supplies no route information, so a paired contrast
        // against a ranked arm over the same list isolates the ranking.
        private PathAwareArm PathAwareBandRandomArm(
            string name,
            List<PathAwareCandidate> bandCandidates,
            int maxDegree,
            int calibrationProbes,
            Dictionary<Node, PathAwareCandidate> candidateByNode,
            int fanout,
            int experimentSeed,
            int streamRole,
            double bandFraction)
        {
            List<PathAwareCandidate> shuffled = bandCandidates
                .OrderBy(c => c.Node.NodeID).ToList();
            Random random = new Random(
                PathAwareStreamSeed(experimentSeed, streamRole));
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                PathAwareCandidate temp = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = temp;
            }
            List<Node> nodes = shuffled.Take(maxDegree)
                .Select(c => c.Node).ToList();
            return new PathAwareArm {
                Name = name,
                Nodes = nodes,
                CalibrationObjective = PathAwareObjective(
                    nodes, candidateByNode, calibrationProbes, fanout),
                SelectionEvaluations = bandCandidates.Count,
                UtilityBandFraction = bandFraction
            };
        }

        private bool PathAwareUsesCalibration(string armName)
        {
            return armName == "exposure" || armName == "bandexposure" ||
                armName == "routeband" || armName == "routeband90" ||
                armName == "routeband975" || armName == "product" ||
                armName == "pathgreedy" ||
                armName == "bandgreedy" || armName == "pathswap" ||
                armName.StartsWith("bandswap", StringComparison.Ordinal);
        }

        private uint PathAwareTieKey(Node node, int seed, int role)
        {
            uint value = unchecked(
                (uint)seed * 2654435761u ^
                (uint)node.NodeID * 2246822519u ^
                (uint)role * 3266489917u);
            value ^= value >> 16;
            value *= 2246822519u;
            value ^= value >> 13;
            value *= 3266489917u;
            value ^= value >> 16;
            return value;
        }

        private int PathAwareStreamSeed(int experimentSeed, int ordinal)
        {
            if (experimentSeed < 0 || experimentSeed > 500000 ||
                ordinal < 0 || ordinal >= 4096)
                throw new ArgumentOutOfRangeException(
                    "RouteBand stream coordinate is outside the injective range.");
            long code = (long)experimentSeed * 4096L + ordinal;
            if (code >= 2147483646L)
                throw new ArgumentOutOfRangeException(
                    "RouteBand stream seed exceeds System.Random range.");
            return 1 + (int)((48271L * code + 12345L) % 2147483646L);
        }

        private void PathAwareAssertStreamPlan(
            int experimentSeed, int calibrationProbes,
            int outcomeProbes, int backgroundProbes)
        {
            if (calibrationProbes > 500 || outcomeProbes > 2000 ||
                backgroundProbes > 500)
                throw new ArgumentOutOfRangeException(
                    "RouteBand probe count exceeds the frozen stream allocation.");
            List<int> ordinals = new List<int> { 1, 600, 2800, 2801, 3600 };
            ordinals.AddRange(Enumerable.Range(16, calibrationProbes));
            ordinals.AddRange(Enumerable.Range(700, outcomeProbes));
            ordinals.AddRange(Enumerable.Range(3000, backgroundProbes));
            if (ordinals.Count != ordinals.Distinct().Count())
                throw new InvalidOperationException(
                    "RouteBand stream ordinal collision.");
            List<int> seeds = ordinals
                .Select(o => PathAwareStreamSeed(experimentSeed, o)).ToList();
            // The target observing pass may run past the calibration budget in
            // order to gather calibrationProbes SUCCESSFUL traces, so the
            // reserved ordinal span covers the ceiling used by that loop.
            seeds.AddRange(
                Enumerable.Range(0, Math.Min(4000, calibrationProbes * 10) + 1)
                .Select(i => PathAwareObservedStreamSeed(experimentSeed, i)));
            if (seeds.Count != seeds.Distinct().Count())
                throw new InvalidOperationException(
                    "RouteBand System.Random seed collision.");
        }

        // The ordinal space below 4096 is fully allocated by the frozen plan,
        // and no contiguous block of 400 remains. The target observing pass
        // therefore draws from a disjoint region so that every existing stream
        // keeps exactly the seed it had before this arm was added. The explicit
        // collision check in PathAwareAssertStreamPlan covers both regions.
        private int PathAwareObservedStreamSeed(int experimentSeed, int index)
        {
            if (experimentSeed < 0 || experimentSeed > 500000 ||
                index < 0 || index > 4096)
                throw new ArgumentOutOfRangeException(
                    "Target observing stream coordinate is outside its range.");
            long code = 1500000000L + (long)experimentSeed * 4097L + index;
            if (code >= 2147483646L)
                throw new ArgumentOutOfRangeException(
                    "Target observing stream seed exceeds System.Random range.");
            return 1 + (int)((48271L * code + 12345L) % 2147483646L);
        }

        private ulong PathAwarePairTieKey(
            Node prune, Node graft, int seed, int role)
        {
            uint pruneKey = PathAwareTieKey(prune, seed, role * 2 + 1);
            uint graftKey = PathAwareTieKey(graft, seed, role * 2 + 2);
            return ((ulong)pruneKey << 32) | graftKey;
        }

        private PathAwareTraceResult PathAwareExpandedTrace(
            Node begin, int fanout, int visitBudget, Random random)
        {
            HashSet<Node> visited = new HashSet<Node>();
            HashSet<Node> expanded = new HashSet<Node>();
            List<Node> frontier = new List<Node>();
            List<Node> parents = new List<Node>();
            visited.Add(begin);
            frontier.Add(begin);
            parents.Add(null);
            int messages = 0;
            while (frontier.Count > 0 && visited.Count < visitBudget)
            {
                List<Node> next = new List<Node>();
                List<Node> nextParents = new List<Node>();
                for (int f = 0; f < frontier.Count && visited.Count < visitBudget; f++)
                {
                    Node current = frontier[f];
                    Node parent = parents[f];
                    // A trace source has no parent, so its probability of
                    // selecting a future holder edge has a different
                    // denominator.  Excluding it keeps one exact candidate
                    // factor for every credited trace membership.
                    if (current != begin) expanded.Add(current);
                    List<Node> neighbors = SweetSpotNeighbours(current);
                    if (parent != null) neighbors.Remove(parent);
                    neighbors.Sort((a, b) => a.NodeID.CompareTo(b.NodeID));
                    int forward = Math.Min(fanout, neighbors.Count);
                    for (int k = 0; k < forward && visited.Count < visitBudget; k++)
                    {
                        int pick = random.Next(k, neighbors.Count);
                        Node temp = neighbors[pick];
                        neighbors[pick] = neighbors[k];
                        neighbors[k] = temp;
                        Node candidate = neighbors[k];
                        messages++;
                        if (!visited.Add(candidate)) continue;
                        next.Add(candidate);
                        nextParents.Add(current);
                    }
                }
                frontier = next;
                parents = nextParents;
            }
            return new PathAwareTraceResult {
                Expanded = expanded,
                UniqueVisited = visited.Count,
                Messages = messages,
                Visited = visited
            };
        }

        private List<Node> PathAwareSamplePool(
            List<Node> rootPool,
            Node holder,
            int experimentSeed,
            int sampleStarts,
            int hopLimit,
            out long adjacencyChecks,
            out string rootIds)
        {
            adjacencyChecks = 0;
            HashSet<Node> pooled = new HashSet<Node>();
            List<Node> roots = rootPool
                .Where(n => n != holder)
                .OrderBy(n => PathAwareTieKey(n, experimentSeed, 107))
                .ThenBy(n => n.NodeID)
                .Take(sampleStarts)
                .ToList();
            if (roots.Count < sampleStarts)
                throw new InvalidOperationException(
                    "Insufficient incumbent roots for local candidate sampling.");
            rootIds = String.Join("|", roots.Select(n =>
                n.NodeID.ToString(CultureInfo.InvariantCulture)));
            foreach (Node root in roots)
            {
                HashSet<Node> seen = new HashSet<Node>();
                List<Node> frontier = new List<Node>();
                seen.Add(root);
                frontier.Add(root);
                pooled.Add(root);
                for (int hop = 0; hop < hopLimit; hop++)
                {
                    List<Node> next = new List<Node>();
                    foreach (Node current in frontier)
                    {
                        foreach (Node candidate in SweetSpotNeighbours(current))
                        {
                            adjacencyChecks++;
                            if (candidate == holder || !seen.Add(candidate)) continue;
                            pooled.Add(candidate);
                            next.Add(candidate);
                        }
                    }
                    frontier = next;
                }
            }
            return pooled.OrderBy(n => n.NodeID).ToList();
        }

        private PathAwareHolderAudit PathAwarePrepareNeutralHolder(
            int experimentSeed, int targetValue)
        {
            List<Node> live = SweetSpotLiveNodes();
            string preSaturationHash = SweetSpotTopologyHash();
            if (live.Any(n => n.Item.Values.Contains(targetValue)))
                throw new InvalidOperationException(
                    "Neutral target value already exists in the graph.");

            List<Node> holderCandidates = live
                .Where(n => n.Degree > 0 && n.Degree < maxDegree)
                .Where(n => SweetSpotNeighbours(n).All(v => v.Degree > 3))
                .OrderBy(n => PathAwareTieKey(n, experimentSeed, 101))
                .ThenBy(n => n.NodeID)
                .ToList();

            foreach (Node holder in holderCandidates)
            {
                List<Node> original = SweetSpotNeighbours(holder);
                int pretreatmentDegree = holder.Degree;
                int pretreatmentItems = holder.Item.Values.Count;
                int neighborMin = original.Min(n => n.Degree);
                double neighborMean = original.Average(n => (double)n.Degree);
                int neighborMax = original.Max(n => n.Degree);
                PositionPlace(holder, new List<Node>());
                int residualComponents;
                int residualLargest;
                SweetSpotComponents(out residualComponents, out residualLargest);
                if (residualComponents != 2 || residualLargest != live.Count - 1)
                {
                    SweetSpotRestoreHolder(holder, original);
                    continue;
                }
                SweetSpotRestoreHolder(holder, original);
                int attempts = 0;
                int stalled = 0;
                while (holder.Degree < maxDegree && attempts < 10000)
                {
                    int before = holder.Degree;
                    join(holder);
                    attempts++;
                    if (holder.Degree == before) stalled++;
                    else stalled = 0;
                    if (stalled >= 1000) break;
                }
                if (holder.Degree != maxDegree)
                    throw new InvalidOperationException(
                        "Native local attachment could not saturate the neutral holder.");
                int finalComponents;
                int finalLargest;
                SweetSpotComponents(out finalComponents, out finalLargest);
                if (finalComponents != 1 || finalLargest != live.Count)
                    throw new InvalidOperationException(
                        "Neutral holder saturation did not restore connectivity.");
                holder.Item = new Item();
                holder.addItem(targetValue);
                return new PathAwareHolderAudit {
                    Holder = holder,
                    SafetyPrefilterHolders = holderCandidates.Count,
                    PretreatmentDegree = pretreatmentDegree,
                    PretreatmentItems = pretreatmentItems,
                    OriginalNeighborMinDegree = neighborMin,
                    OriginalNeighborMeanDegree = neighborMean,
                    OriginalNeighborMaxDegree = neighborMax,
                    PreSaturationHash = preSaturationHash,
                    PostSaturationHash = SweetSpotTopologyHash()
                };
            }

            throw new InvalidOperationException(
                "No outcome-free neutral holder satisfies the safety constraints.");
        }

        private void PathAwareAssertConnectedFeasible(
            Node holder, int liveNodes, bool holderAbsent)
        {
            foreach (Node node in SweetSpotLiveNodes())
            {
                if (node == holder && holderAbsent)
                {
                    if (node.Degree != 0)
                        throw new InvalidOperationException(
                            "Logically absent holder still has incident edges.");
                    continue;
                }
                if (node.Degree < 3 || node.Degree > maxDegree)
                    throw new InvalidOperationException(
                        "RouteBand degree feasibility violation at node " +
                        node.NodeID + ".");
            }
            int components;
            int largest;
            SweetSpotComponents(out components, out largest);
            int expectedComponents = holderAbsent ? 2 : 1;
            int expectedLargest = holderAbsent ? liveNodes - 1 : liveNodes;
            if (components != expectedComponents || largest != expectedLargest)
                throw new InvalidOperationException(
                    "RouteBand connectivity invariant failed.");
        }
    }
}
