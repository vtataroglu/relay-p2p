using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ScalableP2P
{
    partial class Graph
    {
        internal sealed class ExposureAblationRunEvidence
        {
            public string Family;
            public int Seed;
            public string BaseHash;
            public Dictionary<string, string> ArmHashes =
                new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> RestoreHashes =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private sealed class ExposureAblationArmMeasurement
        {
            public PathAwareArm Arm;
            public string ArmHash;
            public string RestoreHash;
            public int Successes;
            public long Visited;
            public long Messages;
            public int BackgroundSuccesses;
            public long BackgroundVisited;
            public long BackgroundMessages;
            public double MeanDegree;
            public double MeanExposure;
            public double MeanForwardingFactor;
            public double MeanProduct;
        }

        public ExposureAblationRunEvidence RunExposureAblationExperiment(
            string outputDirectory,
            int experimentSeed,
            int fanout,
            int calibrationProbes,
            int calibrationVisitBudget,
            int outcomeProbes,
            int targetVisitBudget,
            int relayCount,
            int expectedLiveNodes,
            int backgroundProbes,
            int backgroundVisitBudget)
        {
            if (fanout <= 0 || calibrationProbes <= 0 || outcomeProbes <= 0 ||
                calibrationVisitBudget <= 1 || targetVisitBudget <= 1 ||
                relayCount <= 0 || relayCount > maxDegree ||
                backgroundProbes <= 0 || backgroundVisitBudget <= 1)
                throw new ArgumentOutOfRangeException(
                    "exposure ablation experiment budget");

            PathAwareAssertStreamPlan(
                experimentSeed, calibrationProbes, outcomeProbes,
                backgroundProbes);

            string summaryPath = Path.Combine(outputDirectory, "summary.csv");
            string candidatePath = Path.Combine(outputDirectory, "candidates.csv");
            string selectionPath = Path.Combine(outputDirectory, "selection.csv");
            string probePath = Path.Combine(outputDirectory, "probes.csv");
            string[] outputPaths = new string[] {
                summaryPath, candidatePath, selectionPath, probePath
            };
            if (outputPaths.SelectMany(path => new string[] {
                    path, path + ".partial"
                }).Any(File.Exists))
                throw new IOException(
                    "Refusing to overwrite complete or partial exposure ablation output.");

            if (Trend != null || RewireEnabled)
                throw new InvalidOperationException(
                    "The exposure ablation requires a neutral graph without trend state or rewiring.");

            const int targetValue = 1000000007;
            const string implementationVersion = "exposure-ablation-v1";
            const int candidateRootCount = 16;
            const int candidateHopLimit = 2;
            const double utilityBandFraction = 0.95;
            const int tieRole = 41;

            PathAwareHolderAudit holderAudit = PathAwarePrepareNeutralHolder(
                experimentSeed, targetValue);
            Node holder = holderAudit.Holder;
            int targetReplicaCount = SweetSpotLiveNodes().Count(
                node => node.Item.Values.Contains(targetValue));
            if (targetReplicaCount != 1 || holder.Item.Values.Count != 1)
                throw new InvalidOperationException(
                    "The exposure ablation requires one target replica and one holder item.");

            int liveNodes;
            long baseEdges;
            SweetSpotValidateTopology(out liveNodes, out baseEdges);
            if (liveNodes != expectedLiveNodes)
                throw new InvalidOperationException(
                    "Exposure ablation live node mismatch.");
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException(
                    "The exposure ablation requires one saturated holder.");
            PathAwareAssertConnectedFeasible(holder, liveNodes, false);

            List<Node> baseNeighbors = SweetSpotNeighbours(holder);
            HashSet<Node> baseNeighborSet = new HashSet<Node>(baseNeighbors);
            string baseHash = SweetSpotTopologyHash();
            string family = UniformLocalAttachment ? "uniform" : "popularity";
            List<Node> sourcePool = SweetSpotLiveNodes()
                .Where(node => node != holder)
                .OrderBy(node => node.NodeID)
                .ToList();

            List<PathAwareCandidate> candidates;
            Dictionary<Node, PathAwareCandidate> candidateByNode;
            long candidateAdjacencyChecks = 0;
            string candidateRootIds = "";
            long calibrationVisited = 0;
            long calibrationMessages = 0;
            List<string> probeRows = new List<string>();

            PositionPlace(holder, new List<Node>());
            try
            {
                List<Node> sampledPool = PathAwareSamplePool(
                    baseNeighbors, holder, experimentSeed, candidateRootCount,
                    candidateHopLimit, out candidateAdjacencyChecks,
                    out candidateRootIds);
                candidateRootIds = String.Join("|", candidateRootIds.Split(
                        new char[] { '|' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.Parse(
                        value, CultureInfo.InvariantCulture))
                    .OrderBy(nodeId => nodeId)
                    .Select(nodeId => nodeId.ToString(
                        CultureInfo.InvariantCulture)));
                sampledPool = sampledPool.Concat(baseNeighbors)
                    .Distinct()
                    .OrderBy(node => node.NodeID)
                    .ToList();
                candidates = sampledPool
                    .Where(node => node != holder && node.Degree >= 3 &&
                                   node.Degree < maxDegree)
                    .Select(node => new PathAwareCandidate {
                        Node = node,
                        BaseDegree = node.Degree
                    })
                    .ToList();
                if (candidates.Count < relayCount)
                    throw new InvalidOperationException(
                        "Insufficient exposure ablation candidates.");
                PathAwareAssertConnectedFeasible(holder, liveNodes, true);

                candidateByNode = candidates.ToDictionary(
                    candidate => candidate.Node, candidate => candidate);
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
                    PathAwareTraceResult trace = PathAwareExpandedTrace(
                        source, fanout, calibrationVisitBudget,
                        new Random(traceSeed));
                    calibrationVisited += trace.UniqueVisited;
                    calibrationMessages += trace.Messages;
                    probeRows.Add(String.Join(",", new string[] {
                        experimentSeed.ToString(CultureInfo.InvariantCulture),
                        family,
                        "calibration",
                        "calibration",
                        probeIndex.ToString(CultureInfo.InvariantCulture),
                        source.NodeID.ToString(CultureInfo.InvariantCulture),
                        "0",
                        traceSeed.ToString(CultureInfo.InvariantCulture),
                        "0",
                        trace.UniqueVisited.ToString(CultureInfo.InvariantCulture),
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
                    "Holder absent calibration restoration failed.");

            long routeTelemetryBytes = (long)candidates.Count *
                (((calibrationProbes + 7) / 8) + 4);
            if (calibrationVisited > 1000000L ||
                calibrationMessages > 1100000L ||
                routeTelemetryBytes > 1048576L)
                throw new InvalidOperationException(
                    "Exposure ablation calibration cost ceiling exceeded.");

            double maximumDegreeUtility = RelayUtility(fanout, fanout + 1);
            List<PathAwareCandidate> bandCandidates = candidates
                .Where(candidate => candidate.DegreeUtility >=
                    utilityBandFraction * maximumDegreeUtility)
                .ToList();
            if (bandCandidates.Count < relayCount)
                throw new InvalidOperationException(
                    "Insufficient candidates in the fixed utility band.");

            HashSet<int> sampledRootIds = new HashSet<int>(
                candidateRootIds.Split(
                    new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(
                    value, CultureInfo.InvariantCulture)));
            if (sampledRootIds.Count != candidateRootCount)
                throw new InvalidOperationException(
                    "Exposure ablation sampled root identity mismatch.");
            List<uint> candidateTieKeys = candidates
                .Select(candidate => PathAwareTieKey(
                    candidate.Node, experimentSeed, tieRole))
                .ToList();
            if (candidateTieKeys.Count != candidateTieKeys.Distinct().Count())
                throw new InvalidOperationException(
                    "Exposure ablation candidate tie key collision.");
            List<string> candidateRows = candidates
                .OrderBy(candidate => candidate.Node.NodeID)
                .Select(candidate => String.Join(",", new string[] {
                    experimentSeed.ToString(CultureInfo.InvariantCulture),
                    family,
                    fanout.ToString(CultureInfo.InvariantCulture),
                    candidate.Node.NodeID.ToString(CultureInfo.InvariantCulture),
                    candidate.BaseDegree.ToString(CultureInfo.InvariantCulture),
                    String.Join("|", candidate.TraceIndices.Select(index =>
                        index.ToString(CultureInfo.InvariantCulture))),
                    candidate.Exposure.ToString("R", CultureInfo.InvariantCulture),
                    candidate.ForwardingFactor.ToString(
                        "R", CultureInfo.InvariantCulture),
                    candidate.ProductScore.ToString(
                        "R", CultureInfo.InvariantCulture),
                    candidate.DegreeUtility.ToString(
                        "R", CultureInfo.InvariantCulture),
                    bandCandidates.Contains(candidate) ? "1" : "0",
                    baseNeighborSet.Contains(candidate.Node) ? "1" : "0",
                    sampledRootIds.Contains(candidate.Node.NodeID) ? "1" : "0",
                    PathAwareTieKey(candidate.Node, experimentSeed, tieRole)
                        .ToString(CultureInfo.InvariantCulture)
                })).ToList();

            List<PathAwareArm> arms = new List<PathAwareArm> {
                PathAwareRankedArm(
                    "routeband", bandCandidates, relayCount,
                    candidate => candidate.ProductScore,
                    calibrationProbes, candidateByNode, fanout,
                    experimentSeed, tieRole, utilityBandFraction),
                PathAwareRankedArm(
                    "bandforwarding", bandCandidates, relayCount,
                    candidate => candidate.ForwardingFactor,
                    calibrationProbes, candidateByNode, fanout,
                    experimentSeed, tieRole, utilityBandFraction)
            };
            if (arms.Count != 2 || arms.Any(arm => arm.Nodes.Count != relayCount))
                throw new InvalidOperationException(
                    "Exposure ablation requires exactly two complete relay sets.");
            foreach (PathAwareArm arm in arms)
            {
                arm.FinalChangedRelays = arm.Nodes.Count(
                    node => !baseNeighborSet.Contains(node));
                arm.CostEligible = true;
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
                int temporary = backgroundValues[i];
                backgroundValues[i] = backgroundValues[j];
                backgroundValues[j] = temporary;
            }

            List<ExposureAblationArmMeasurement> measurements =
                new List<ExposureAblationArmMeasurement>();
            List<string> selectionRows = new List<string>();
            foreach (PathAwareArm arm in arms)
            {
                ExposureAblationArmMeasurement measurement =
                    new ExposureAblationArmMeasurement { Arm = arm };
                try
                {
                    PositionPlace(holder, arm.Nodes);
                    if (holder.Degree != maxDegree)
                        throw new InvalidOperationException(
                            "Exposure ablation holder degree invariant failed.");
                    int armNodes;
                    long armEdges;
                    SweetSpotValidateTopology(out armNodes, out armEdges);
                    if (armNodes != liveNodes || armEdges != baseEdges)
                        throw new InvalidOperationException(
                            "Exposure ablation node or edge invariant failed.");
                    PathAwareAssertConnectedFeasible(holder, liveNodes, false);
                    measurement.ArmHash = SweetSpotTopologyHash();

                    for (int i = 0; i < outcomeProbes; i++)
                    {
                        int probeSeed = PathAwareStreamSeed(
                            experimentSeed, 700 + i);
                        SweetSpotProbeResult result = SweetSpotBudgetSearch(
                            outcomeSources[i], targetValue, fanout,
                            targetVisitBudget, new Random(probeSeed));
                        if (result.Success) measurement.Successes++;
                        measurement.Visited += result.UniqueVisited;
                        measurement.Messages += result.Messages;
                        probeRows.Add(String.Join(",", new string[] {
                            experimentSeed.ToString(CultureInfo.InvariantCulture),
                            family,
                            arm.Name,
                            "target",
                            i.ToString(CultureInfo.InvariantCulture),
                            outcomeSources[i].NodeID.ToString(
                                CultureInfo.InvariantCulture),
                            targetValue.ToString(CultureInfo.InvariantCulture),
                            probeSeed.ToString(CultureInfo.InvariantCulture),
                            result.Success ? "1" : "0",
                            result.UniqueVisited.ToString(
                                CultureInfo.InvariantCulture),
                            result.Messages.ToString(CultureInfo.InvariantCulture),
                            "0",
                            ""
                        }));
                    }

                    for (int i = 0; i < backgroundProbes; i++)
                    {
                        int probeSeed = PathAwareStreamSeed(
                            experimentSeed, 3000 + i);
                        SweetSpotProbeResult result = SweetSpotBudgetSearch(
                            backgroundSources[i], backgroundValues[i], fanout,
                            backgroundVisitBudget, new Random(probeSeed));
                        if (result.Success) measurement.BackgroundSuccesses++;
                        measurement.BackgroundVisited += result.UniqueVisited;
                        measurement.BackgroundMessages += result.Messages;
                        probeRows.Add(String.Join(",", new string[] {
                            experimentSeed.ToString(CultureInfo.InvariantCulture),
                            family,
                            arm.Name,
                            "background",
                            i.ToString(CultureInfo.InvariantCulture),
                            backgroundSources[i].NodeID.ToString(
                                CultureInfo.InvariantCulture),
                            backgroundValues[i].ToString(
                                CultureInfo.InvariantCulture),
                            probeSeed.ToString(CultureInfo.InvariantCulture),
                            result.Success ? "1" : "0",
                            result.UniqueVisited.ToString(
                                CultureInfo.InvariantCulture),
                            result.Messages.ToString(CultureInfo.InvariantCulture),
                            "0",
                            ""
                        }));
                    }
                    if (SweetSpotTopologyHash() != measurement.ArmHash)
                        throw new InvalidOperationException(
                            "Exposure ablation probes changed the graph.");

                    measurement.MeanDegree = arm.Nodes.Average(
                        node => (double)node.Degree);
                    measurement.MeanExposure = arm.Nodes.Average(node =>
                        candidateByNode[node].Exposure);
                    measurement.MeanForwardingFactor = arm.Nodes.Average(node =>
                        candidateByNode[node].ForwardingFactor);
                    measurement.MeanProduct = arm.Nodes.Average(node =>
                        candidateByNode[node].ProductScore);

                    for (int rank = 0; rank < arm.Nodes.Count; rank++)
                    {
                        Node node = arm.Nodes[rank];
                        PathAwareCandidate candidate = candidateByNode[node];
                        double selectionScore = arm.Name == "routeband"
                            ? candidate.ProductScore
                            : candidate.ForwardingFactor;
                        selectionRows.Add(String.Join(",", new string[] {
                            experimentSeed.ToString(CultureInfo.InvariantCulture),
                            family,
                            fanout.ToString(CultureInfo.InvariantCulture),
                            arm.Name,
                            (rank + 1).ToString(CultureInfo.InvariantCulture),
                            node.NodeID.ToString(CultureInfo.InvariantCulture),
                            node.Degree.ToString(CultureInfo.InvariantCulture),
                            candidate.BaseDegree.ToString(
                                CultureInfo.InvariantCulture),
                            candidate.Exposure.ToString(
                                "R", CultureInfo.InvariantCulture),
                            candidate.ForwardingFactor.ToString(
                                "R", CultureInfo.InvariantCulture),
                            selectionScore.ToString(
                                "R", CultureInfo.InvariantCulture),
                            candidate.ProductScore.ToString(
                                "R", CultureInfo.InvariantCulture),
                            candidate.DegreeUtility.ToString(
                                "R", CultureInfo.InvariantCulture),
                            "1",
                            PathAwareTieKey(node, experimentSeed, tieRole)
                                .ToString(CultureInfo.InvariantCulture)
                        }));
                    }
                }
                finally
                {
                    SweetSpotRestoreHolder(holder, baseNeighbors);
                }
                measurement.RestoreHash = SweetSpotTopologyHash();
                if (measurement.RestoreHash != baseHash)
                    throw new InvalidOperationException(
                        "Exposure ablation arm restoration failed.");
                measurements.Add(measurement);
            }

            List<string> summaryRows = measurements.Select(measurement =>
                String.Join(",", new string[] {
                    experimentSeed.ToString(CultureInfo.InvariantCulture),
                    implementationVersion,
                    family,
                    liveNodes.ToString(CultureInfo.InvariantCulture),
                    baseEdges.ToString(CultureInfo.InvariantCulture),
                    minDegree.ToString(CultureInfo.InvariantCulture),
                    maxDegree.ToString(CultureInfo.InvariantCulture),
                    targetValue.ToString(CultureInfo.InvariantCulture),
                    targetReplicaCount.ToString(CultureInfo.InvariantCulture),
                    holder.Item.Values.Count.ToString(CultureInfo.InvariantCulture),
                    candidateRootCount.ToString(CultureInfo.InvariantCulture),
                    candidateHopLimit.ToString(CultureInfo.InvariantCulture),
                    utilityBandFraction.ToString(
                        "R", CultureInfo.InvariantCulture),
                    fanout.ToString(CultureInfo.InvariantCulture),
                    measurement.Arm.Name,
                    calibrationProbes.ToString(CultureInfo.InvariantCulture),
                    calibrationVisitBudget.ToString(CultureInfo.InvariantCulture),
                    outcomeProbes.ToString(CultureInfo.InvariantCulture),
                    measurement.Successes.ToString(CultureInfo.InvariantCulture),
                    (measurement.Successes / (double)outcomeProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    targetVisitBudget.ToString(CultureInfo.InvariantCulture),
                    candidates.Count.ToString(CultureInfo.InvariantCulture),
                    measurement.Arm.CalibrationObjective.ToString(
                        "R", CultureInfo.InvariantCulture),
                    measurement.Arm.FinalChangedRelays.ToString(
                        CultureInfo.InvariantCulture),
                    measurement.MeanDegree.ToString(
                        "R", CultureInfo.InvariantCulture),
                    measurement.MeanExposure.ToString(
                        "R", CultureInfo.InvariantCulture),
                    measurement.MeanForwardingFactor.ToString(
                        "R", CultureInfo.InvariantCulture),
                    measurement.MeanProduct.ToString(
                        "R", CultureInfo.InvariantCulture),
                    (measurement.Visited / (double)outcomeProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    (measurement.Messages / (double)outcomeProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    backgroundProbes.ToString(CultureInfo.InvariantCulture),
                    measurement.BackgroundSuccesses.ToString(
                        CultureInfo.InvariantCulture),
                    (measurement.BackgroundSuccesses /
                        (double)backgroundProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    backgroundVisitBudget.ToString(CultureInfo.InvariantCulture),
                    (measurement.BackgroundVisited /
                        (double)backgroundProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    (measurement.BackgroundMessages /
                        (double)backgroundProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    (calibrationVisited / (double)calibrationProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    (calibrationMessages / (double)calibrationProbes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    candidateAdjacencyChecks.ToString(
                        CultureInfo.InvariantCulture),
                    routeTelemetryBytes.ToString(CultureInfo.InvariantCulture),
                    measurement.Arm.SelectionEvaluations.ToString(
                        CultureInfo.InvariantCulture),
                    candidateRootIds,
                    holder.NodeID.ToString(CultureInfo.InvariantCulture),
                    holderAudit.SafetyPrefilterHolders.ToString(
                        CultureInfo.InvariantCulture),
                    holderAudit.PretreatmentDegree.ToString(
                        CultureInfo.InvariantCulture),
                    holderAudit.PretreatmentItems.ToString(
                        CultureInfo.InvariantCulture),
                    holderAudit.OriginalNeighborMinDegree.ToString(
                        CultureInfo.InvariantCulture),
                    holderAudit.OriginalNeighborMeanDegree.ToString(
                        "R", CultureInfo.InvariantCulture),
                    holderAudit.OriginalNeighborMaxDegree.ToString(
                        CultureInfo.InvariantCulture),
                    holderAudit.PreSaturationHash,
                    holderAudit.PostSaturationHash,
                    baseHash,
                    measurement.ArmHash,
                    measurement.RestoreHash
                })).ToList();

            Directory.CreateDirectory(outputDirectory);
            WriteExposureAblationCsv(
                probePath,
                "seed,family,arm,probeType,probeIndex,sourceId,targetValue," +
                "rngSeed,success,uniqueVisited,messages,expandedCount," +
                "expandedNodeIds",
                probeRows);
            WriteExposureAblationCsv(
                candidatePath,
                "seed,family,fanout,nodeId,baseDegree,traceIndices,exposure," +
                "forwardingFactor,productScore,degreeUtility,inUtilityBand," +
                "isIncumbent,isSampledRoot,tieKey",
                candidateRows);
            WriteExposureAblationCsv(
                selectionPath,
                "seed,family,fanout,arm,rank,nodeId,postDegree,baseDegree," +
                "calibrationExposure,forwardingFactor,selectionScore," +
                "productScore,degreeUtility,inUtilityBand,tieKey",
                selectionRows);
            WriteExposureAblationCsv(
                summaryPath,
                "seed,implementationVersion,family,liveNodes,baseEdges," +
                "minDegree,degreeCutoff,targetValue,targetReplicaCount," +
                "holderPostItems,candidateRootCount,candidateHopLimit," +
                "utilityBandFraction,fanout,arm,calibrationProbes," +
                "calibrationVisitBudget,outcomeProbes,successes,pHat," +
                "targetVisitBudget,candidatePool,calibrationObjective," +
                "finalChangedRelays,meanRelayDegree,meanCalibrationExposure," +
                "meanForwardingFactor,meanProductScore,meanVisited," +
                "meanMessages,backgroundProbes,backgroundSuccesses," +
                "backgroundPHat,backgroundVisitBudget,backgroundMeanVisited," +
                "backgroundMeanMessages,calibrationMeanVisited," +
                "calibrationMeanMessages,candidateAdjacencyChecks," +
                "candidateTelemetryBytes,selectionEvaluations,candidateRootIds," +
                "holderId,safetyPrefilterHolders,holderPretreatmentDegree," +
                "holderPretreatmentItems,originalNeighborMinDegree," +
                "originalNeighborMeanDegree,originalNeighborMaxDegree," +
                "preSaturationHash,postSaturationHash,baseHash,armHash," +
                "postArmRestoreHash",
                summaryRows);

            ExposureAblationRunEvidence evidence =
                new ExposureAblationRunEvidence {
                    Family = family,
                    Seed = experimentSeed,
                    BaseHash = baseHash
                };
            foreach (ExposureAblationArmMeasurement measurement in measurements)
            {
                evidence.ArmHashes.Add(
                    measurement.Arm.Name, measurement.ArmHash);
                evidence.RestoreHashes.Add(
                    measurement.Arm.Name, measurement.RestoreHash);
            }
            return evidence;
        }

        private void WriteExposureAblationCsv(
            string path, string header, IEnumerable<string> rows)
        {
            string partialPath = path + ".partial";
            using (StreamWriter writer = new StreamWriter(partialPath))
            {
                writer.WriteLine(header);
                foreach (string row in rows) writer.WriteLine(row);
            }
            File.Move(partialPath, path);
        }
    }
}
