using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ScalableP2P
{
    partial class Graph
    {
        private const string NeutralMechanismImplementationVersion =
            "neutral-mechanism-v1";

        private sealed class NeutralMechanismArmResult
        {
            public string ForwardingMode;
            public int Fanout;
            public int TargetPostDegree;
            public int CandidateCount;
            public int Successes;
            public double MeanVisited;
            public double MeanMessages;
            public double MeanNeighborDegree;
            public int MinNeighborDegree;
            public int MaxNeighborDegree;
            public int LiveNodes;
            public long EdgeCount;
            public int Components;
            public int LargestComponent;
            public string ArmHash;
            public string RestoredHash;
        }

        private sealed class NeutralMechanismArmSpec
        {
            public string ForwardingMode;
            public int Fanout;
            public int TargetPostDegree;
        }

        public void RunNeutralMechanismSuite(
            string exactSummaryPath,
            string matchedSummaryPath,
            int experimentSeed,
            int probes,
            int visitBudget,
            int balanceProbes,
            int expectedLiveNodes)
        {
            if (probes <= 0 || visitBudget <= 1 || balanceProbes <= 0)
                throw new ArgumentOutOfRangeException("neutral mechanism budget");
            if (String.Equals(
                    Path.GetFullPath(exactSummaryPath),
                    Path.GetFullPath(matchedSummaryPath),
                    StringComparison.Ordinal))
                throw new ArgumentException(
                    "Exact and position matched summaries require distinct paths.");
            if (Trend != null || RewireEnabled)
                throw new InvalidOperationException(
                    "The neutral mechanism suite requires null trend state and disabled rewiring.");

            string exactProbePath = NeutralExactProbePath(exactSummaryPath);
            string[] protectedPaths = new string[] {
                exactSummaryPath,
                exactSummaryPath + ".partial",
                exactProbePath,
                exactProbePath + ".partial",
                matchedSummaryPath,
                matchedSummaryPath + ".partial"
            };
            if (protectedPaths.Any(File.Exists))
                throw new IOException(
                    "Refusing to overwrite complete or partial neutral mechanism output.");

            const int targetValue = 1000000007;
            PathAwareHolderAudit holderAudit = PathAwarePrepareNeutralHolder(
                experimentSeed, targetValue);
            int targetReplicaCount = SweetSpotLiveNodes().Count(
                node => node.Item.Values.Contains(targetValue));
            if (targetReplicaCount != 1 || holderAudit.Holder.Item.Values.Count != 1)
                throw new InvalidOperationException(
                    "The neutral mechanism suite requires one target replica and one holder item.");
            if (holderAudit.Holder.Degree != maxDegree)
                throw new InvalidOperationException(
                    "The conditioned neutral holder is not saturated.");

            RunNeutralExactDegreeExperiment(
                exactSummaryPath, experimentSeed, probes, visitBudget,
                expectedLiveNodes, targetValue, targetReplicaCount, holderAudit);

            if (SweetSpotTopologyHash() != holderAudit.PostSaturationHash)
                throw new InvalidOperationException(
                    "Exact degree experiment did not restore the conditioned graph.");

            RunPositionMatchedExperiment(
                matchedSummaryPath, experimentSeed, probes, visitBudget,
                balanceProbes, expectedLiveNodes, targetValue);
        }

        private void RunNeutralExactDegreeExperiment(
            string summaryPath,
            int experimentSeed,
            int probes,
            int visitBudget,
            int expectedLiveNodes,
            int targetValue,
            int targetReplicaCount,
            PathAwareHolderAudit holderAudit)
        {
            string probePath = NeutralExactProbePath(summaryPath);
            string summaryPartial = summaryPath + ".partial";
            string probePartial = probePath + ".partial";
            if (new string[] {
                    summaryPath, summaryPartial, probePath, probePartial
                }.Any(File.Exists))
                throw new IOException(
                    "Refusing to overwrite complete or partial neutral exact degree output.");

            AssertRelayUtilitySelfTest();
            int liveNodes;
            long baseEdges;
            SweetSpotValidateTopology(out liveNodes, out baseEdges);
            if (liveNodes != expectedLiveNodes)
                throw new InvalidOperationException(
                    "Neutral exact degree live node mismatch: expected " +
                    expectedLiveNodes + ", observed " + liveNodes + ".");

            Node holder = holderAudit.Holder;
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException(
                    "Neutral exact degree experiment expected one saturated holder.");
            if (SweetSpotLiveNodes().Count(
                    node => node.Item.Values.Contains(targetValue)) != targetReplicaCount ||
                targetReplicaCount != 1)
                throw new InvalidOperationException(
                    "Neutral exact degree target replication invariant failed.");

            List<Node> baseNeighbors = SweetSpotNeighbours(holder);
            if (baseNeighbors.Count != maxDegree)
                throw new InvalidOperationException(
                    "Neutral exact degree holder adjacency does not match the hard cap.");
            string baseHash = SweetSpotTopologyHash();
            if (baseHash != holderAudit.PostSaturationHash)
                throw new InvalidOperationException(
                    "Neutral exact degree base hash differs from the conditioned graph.");
            int baseComponents;
            int baseLargestComponent;
            SweetSpotComponents(out baseComponents, out baseLargestComponent);
            if (baseComponents != 1 || baseLargestComponent != liveNodes)
                throw new InvalidOperationException(
                    "Neutral exact degree base graph is disconnected.");

            List<Node> sourcePool = SweetSpotLiveNodes()
                .Where(node => node != holder)
                .OrderBy(node => node.NodeID)
                .ToList();
            Random sourceRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 2401, 0));
            Node[] sources = new Node[probes];
            for (int i = 0; i < probes; i++)
                sources[i] = sourcePool[sourceRandom.Next(sourcePool.Count)];
            string probeCoordinateHash = NeutralProbeCoordinateHash(
                experimentSeed, 2401, 7, sources);
            string topologyFamily = UniformLocalAttachment
                ? "uniform" : "popularity";

            NeutralMechanismArmSpec[] armSpecs = new NeutralMechanismArmSpec[] {
                new NeutralMechanismArmSpec {
                    ForwardingMode = "fixed", Fanout = 7, TargetPostDegree = 8 },
                new NeutralMechanismArmSpec {
                    ForwardingMode = "fixed", Fanout = 7, TargetPostDegree = 20 },
                new NeutralMechanismArmSpec {
                    ForwardingMode = "fixed", Fanout = 7, TargetPostDegree = 24 },
                new NeutralMechanismArmSpec {
                    ForwardingMode = "full", Fanout = 0, TargetPostDegree = 8 },
                new NeutralMechanismArmSpec {
                    ForwardingMode = "full", Fanout = 0, TargetPostDegree = 24 }
            };
            List<NeutralMechanismArmResult> results =
                new List<NeutralMechanismArmResult>();
            List<string> probeRows = new List<string>(probes * armSpecs.Length);
            Dictionary<int, string> placementHashes = new Dictionary<int, string>();

            foreach (NeutralMechanismArmSpec spec in armSpecs)
            {
                NeutralMechanismArmResult arm = new NeutralMechanismArmResult();
                arm.ForwardingMode = spec.ForwardingMode;
                arm.Fanout = spec.Fanout;
                arm.TargetPostDegree = spec.TargetPostDegree;
                try
                {
                    arm.CandidateCount = SweetSpotPlaceHolderAtExactDegree(
                        holder, spec.TargetPostDegree, experimentSeed);
                    List<Node> placedNeighbors = SweetSpotNeighbours(holder);
                    if (holder.Degree != maxDegree ||
                        placedNeighbors.Count != maxDegree ||
                        placedNeighbors.Any(node => node.Degree != spec.TargetPostDegree))
                        throw new InvalidOperationException(
                            "Neutral exact postconnection degree invariant failed for d=" +
                            spec.TargetPostDegree + ".");

                    SweetSpotValidateTopology(out arm.LiveNodes, out arm.EdgeCount);
                    if (arm.LiveNodes != liveNodes || arm.EdgeCount != baseEdges)
                        throw new InvalidOperationException(
                            "Neutral exact node or edge invariant failed.");
                    SweetSpotComponents(out arm.Components, out arm.LargestComponent);
                    if (arm.Components != 1 || arm.LargestComponent != liveNodes)
                        throw new InvalidOperationException(
                            "Neutral exact arm is disconnected.");
                    arm.ArmHash = SweetSpotTopologyHash();
                    string priorPlacementHash;
                    if (placementHashes.TryGetValue(
                            spec.TargetPostDegree, out priorPlacementHash))
                    {
                        if (priorPlacementHash != arm.ArmHash)
                            throw new InvalidOperationException(
                                "Fixed and full forwarding used different placements at d=" +
                                spec.TargetPostDegree + ".");
                    }
                    else
                    {
                        placementHashes[spec.TargetPostDegree] = arm.ArmHash;
                    }

                    long visitedTotal = 0;
                    long messageTotal = 0;
                    int successes = 0;
                    for (int probeIndex = 0; probeIndex < probes; probeIndex++)
                    {
                        SweetSpotProbeResult probe;
                        if (spec.ForwardingMode == "full")
                        {
                            probe = NeutralFullForwardingSearch(
                                sources[probeIndex], targetValue, visitBudget);
                        }
                        else
                        {
                            Random probeRandom = new Random(
                                SweetSpotStableSeed(experimentSeed, 7, probeIndex));
                            probe = SweetSpotBudgetSearch(
                                sources[probeIndex], targetValue, 7,
                                visitBudget, probeRandom);
                        }
                        if (probe.Success) successes++;
                        visitedTotal += probe.UniqueVisited;
                        messageTotal += probe.Messages;
                        probeRows.Add(String.Join(",", new string[] {
                            experimentSeed.ToString(CultureInfo.InvariantCulture),
                            topologyFamily,
                            spec.ForwardingMode,
                            spec.Fanout.ToString(CultureInfo.InvariantCulture),
                            spec.TargetPostDegree.ToString(CultureInfo.InvariantCulture),
                            probeIndex.ToString(CultureInfo.InvariantCulture),
                            sources[probeIndex].NodeID.ToString(CultureInfo.InvariantCulture),
                            probe.Success ? "1" : "0",
                            probe.UniqueVisited.ToString(CultureInfo.InvariantCulture),
                            probe.Messages.ToString(CultureInfo.InvariantCulture)
                        }));
                    }
                    if (SweetSpotTopologyHash() != arm.ArmHash)
                        throw new InvalidOperationException(
                            "Neutral exact probes changed the graph.");

                    arm.Successes = successes;
                    arm.MeanVisited = visitedTotal / (double)probes;
                    arm.MeanMessages = messageTotal / (double)probes;
                    arm.MeanNeighborDegree =
                        placedNeighbors.Average(node => (double)node.Degree);
                    arm.MinNeighborDegree = placedNeighbors.Min(node => node.Degree);
                    arm.MaxNeighborDegree = placedNeighbors.Max(node => node.Degree);
                }
                finally
                {
                    SweetSpotRestoreHolder(holder, baseNeighbors);
                }

                arm.RestoredHash = SweetSpotTopologyHash();
                if (arm.RestoredHash != baseHash)
                    throw new InvalidOperationException(
                        "Neutral exact holder restoration failed.");
                results.Add(arm);
            }

            if (results.Count != 5 || probeRows.Count != probes * 5)
                throw new InvalidOperationException(
                    "Neutral exact experiment produced an incomplete arm matrix.");

            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? ".");
            NeutralWriteCsvPartial(
                probePartial,
                "seed,topologyFamily,forwardingMode,fanout,targetPostDegree," +
                "probeIndex,sourceId,success,uniqueVisited,messages",
                probeRows);
            NeutralWriteCsvPartial(
                summaryPartial,
                "seed,topologyFamily,implementationVersion,forwardingMode,fanout," +
                "targetPostDegree,candidateCount,probes,successes,pHat,visitBudget," +
                "meanVisited,meanMessages,meanNeighborDegree,minNeighborDegree," +
                "maxNeighborDegree,liveNodes,edgeCount,components,largestComponent," +
                "targetValue,targetReplicaCount,holderId,safetyPrefilterHolders," +
                "holderPretreatmentDegree,holderPretreatmentItems,holderPostItems," +
                "originalNeighborMinDegree,originalNeighborMeanDegree," +
                "originalNeighborMaxDegree,probeCoordinateHash,preSaturationHash," +
                "postSaturationHash,baseHash,armHash,restoredHash",
                results.Select(arm => String.Join(",", new string[] {
                    experimentSeed.ToString(CultureInfo.InvariantCulture),
                    topologyFamily,
                    NeutralMechanismImplementationVersion,
                    arm.ForwardingMode,
                    arm.Fanout.ToString(CultureInfo.InvariantCulture),
                    arm.TargetPostDegree.ToString(CultureInfo.InvariantCulture),
                    arm.CandidateCount.ToString(CultureInfo.InvariantCulture),
                    probes.ToString(CultureInfo.InvariantCulture),
                    arm.Successes.ToString(CultureInfo.InvariantCulture),
                    (arm.Successes / (double)probes).ToString(
                        "R", CultureInfo.InvariantCulture),
                    visitBudget.ToString(CultureInfo.InvariantCulture),
                    arm.MeanVisited.ToString("R", CultureInfo.InvariantCulture),
                    arm.MeanMessages.ToString("R", CultureInfo.InvariantCulture),
                    arm.MeanNeighborDegree.ToString("R", CultureInfo.InvariantCulture),
                    arm.MinNeighborDegree.ToString(CultureInfo.InvariantCulture),
                    arm.MaxNeighborDegree.ToString(CultureInfo.InvariantCulture),
                    arm.LiveNodes.ToString(CultureInfo.InvariantCulture),
                    arm.EdgeCount.ToString(CultureInfo.InvariantCulture),
                    arm.Components.ToString(CultureInfo.InvariantCulture),
                    arm.LargestComponent.ToString(CultureInfo.InvariantCulture),
                    targetValue.ToString(CultureInfo.InvariantCulture),
                    targetReplicaCount.ToString(CultureInfo.InvariantCulture),
                    holder.NodeID.ToString(CultureInfo.InvariantCulture),
                    holderAudit.SafetyPrefilterHolders.ToString(CultureInfo.InvariantCulture),
                    holderAudit.PretreatmentDegree.ToString(CultureInfo.InvariantCulture),
                    holderAudit.PretreatmentItems.ToString(CultureInfo.InvariantCulture),
                    holder.Item.Values.Count.ToString(CultureInfo.InvariantCulture),
                    holderAudit.OriginalNeighborMinDegree.ToString(CultureInfo.InvariantCulture),
                    holderAudit.OriginalNeighborMeanDegree.ToString(
                        "R", CultureInfo.InvariantCulture),
                    holderAudit.OriginalNeighborMaxDegree.ToString(CultureInfo.InvariantCulture),
                    probeCoordinateHash,
                    holderAudit.PreSaturationHash,
                    holderAudit.PostSaturationHash,
                    baseHash,
                    arm.ArmHash,
                    arm.RestoredHash
                })).ToList());

            File.Move(probePartial, probePath);
            File.Move(summaryPartial, summaryPath);
        }

        private SweetSpotProbeResult NeutralFullForwardingSearch(
            Node begin, int targetValue, int visitBudget)
        {
            HashSet<Node> visited = new HashSet<Node>();
            List<Node> frontierNodes = new List<Node>();
            List<Node> frontierParents = new List<Node>();
            visited.Add(begin);
            if (begin.Item.Values.Contains(targetValue))
                return new SweetSpotProbeResult {
                    Success = true, UniqueVisited = 1, Messages = 0
                };
            frontierNodes.Add(begin);
            frontierParents.Add(null);
            int messages = 0;

            while (frontierNodes.Count > 0 && visited.Count < visitBudget)
            {
                List<Node> nextNodes = new List<Node>();
                List<Node> nextParents = new List<Node>();
                for (int f = 0; f < frontierNodes.Count && visited.Count < visitBudget; f++)
                {
                    Node current = frontierNodes[f];
                    Node parent = frontierParents[f];
                    List<Node> neighbors = SweetSpotNeighbours(current);
                    if (parent != null) neighbors.Remove(parent);
                    neighbors.Sort((a, b) => a.NodeID.CompareTo(b.NodeID));
                    for (int k = 0; k < neighbors.Count && visited.Count < visitBudget; k++)
                    {
                        Node candidate = neighbors[k];
                        messages++;
                        if (!visited.Add(candidate)) continue;
                        if (candidate.Item.Values.Contains(targetValue))
                            return new SweetSpotProbeResult {
                                Success = true,
                                UniqueVisited = visited.Count,
                                Messages = messages
                            };
                        nextNodes.Add(candidate);
                        nextParents.Add(current);
                    }
                }
                frontierNodes = nextNodes;
                frontierParents = nextParents;
            }

            return new SweetSpotProbeResult {
                Success = false,
                UniqueVisited = visited.Count,
                Messages = messages
            };
        }

        private static string NeutralExactProbePath(string summaryPath)
        {
            return Path.Combine(
                Path.GetDirectoryName(summaryPath) ?? ".",
                Path.GetFileNameWithoutExtension(summaryPath) + "-probes.csv");
        }

        private static void NeutralWriteCsvPartial(
            string partialPath, string header, IEnumerable<string> rows)
        {
            using (FileStream stream = new FileStream(
                partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream))
            {
                writer.WriteLine(header);
                foreach (string row in rows) writer.WriteLine(row);
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static string NeutralProbeCoordinateHash(
            int experimentSeed, int sourceRole, int fanout, IList<Node> sources)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("neutral-probe-coordinate-v1");
                writer.Write(experimentSeed);
                writer.Write(sourceRole);
                writer.Write(fanout);
                writer.Write(sources.Count);
                for (int i = 0; i < sources.Count; i++)
                {
                    writer.Write(i);
                    writer.Write(sources[i].NodeID);
                    writer.Write(SweetSpotStableSeed(experimentSeed, fanout, i));
                }
                writer.Flush();
                stream.Position = 0;
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream))
                        .Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
