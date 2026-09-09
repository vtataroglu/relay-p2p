using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ScalableP2P
{
    partial class Graph
    {
        private sealed class PositionCandidate
        {
            public Node Node;
            public double Exposure;
            public double CoreDistance;
            public double Clustering;
            public double MeanNeighborDegree;
            public double ExposurePercentile;
            public double ClusteringPercentile;
            public double NeighborPercentile;
        }

        private sealed class PositionTriple
        {
            public PositionCandidate D8;
            public PositionCandidate D20;
            public PositionCandidate D24;
            public double Cost;
        }

        public void RunPositionMatchedExperiment(
            string summaryPath, int experimentSeed, int probes,
            int visitBudget, int balanceProbes, int expectedLiveNodes,
            int targetValueOverride = int.MinValue)
        {
            if (probes <= 0 || visitBudget <= 1 || balanceProbes <= 0)
                throw new ArgumentOutOfRangeException("position matched budget");
            string summaryPartial = summaryPath + ".partial";
            if (File.Exists(summaryPath) || File.Exists(summaryPartial))
                throw new IOException(
                    "Refusing to overwrite complete or partial position matched result: " +
                    summaryPath);

            int liveNodes;
            long baseEdges;
            SweetSpotValidateTopology(out liveNodes, out baseEdges);
            if (liveNodes != expectedLiveNodes)
                throw new InvalidOperationException("Position matched live node mismatch.");

            int targetValue = targetValueOverride != int.MinValue
                ? targetValueOverride
                : (Trend != null ? Trend.TbpVal : 1000);
            Node holder = getHolder(targetValue);
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException("Expected one saturated holder.");
            if (targetValueOverride != int.MinValue &&
                SweetSpotLiveNodes().Count(n => n.Item.Values.Contains(targetValue)) != 1)
                throw new InvalidOperationException(
                    "The explicit position matched target must have one replica.");
            List<Node> baseNeighbors = SweetSpotNeighbours(holder);
            HashSet<Node> excluded = new HashSet<Node>(baseNeighbors);
            excluded.Add(holder);
            string baseHash = SweetSpotTopologyHash();

            List<Node> live = SweetSpotLiveNodes();
            List<Node> ranked = live.Where(n => n != holder)
                .OrderByDescending(n => n.Degree).ThenBy(n => n.NodeID).ToList();
            HashSet<Node> core = new HashSet<Node>(
                ranked.Take(Math.Max(1, ranked.Count / 200)));
            Dictionary<Node, int> coreDistance = MultiSourceDistances(core);

            Dictionary<Node, int> visits = new Dictionary<Node, int>();
            List<Node> sourcePool = live.Where(n => n != holder)
                .OrderBy(n => n.NodeID).ToList();
            Random balanceSourceRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 2309, 0));
            for (int i = 0; i < balanceProbes; i++)
            {
                Node source = sourcePool[balanceSourceRandom.Next(sourcePool.Count)];
                Random walkRandom = new Random(
                    SweetSpotStableSeed(experimentSeed, 2311, i));
                HashSet<Node> reached = PositionTrace(
                    source, 7, visitBudget, walkRandom);
                foreach (Node node in reached)
                {
                    int count;
                    visits.TryGetValue(node, out count);
                    visits[node] = count + 1;
                }
            }
            if (SweetSpotTopologyHash() != baseHash)
                throw new InvalidOperationException("Balance probes changed the graph.");

            List<PositionCandidate> d8 = PositionPool(
                live, excluded, 7, visits, balanceProbes, coreDistance);
            List<PositionCandidate> d20 = PositionPool(
                live, excluded, 19, visits, balanceProbes, coreDistance);
            List<PositionCandidate> d24 = PositionPool(
                live, excluded, 23, visits, balanceProbes, coreDistance);
            if (d8.Count < maxDegree || d20.Count < maxDegree || d24.Count < maxDegree)
                throw new InvalidOperationException(
                    "Insufficient exact degree candidates for position matching.");

            PositionAddRanks(d8);
            PositionAddRanks(d20);
            PositionAddRanks(d24);

            List<PositionTriple> triples = PositionMatchTriples(d8, d20, d24, maxDegree);
            if (triples.Count != maxDegree)
                throw new InvalidOperationException("Position matcher returned an incomplete set.");

            Dictionary<int, List<PositionCandidate>> arms =
                new Dictionary<int, List<PositionCandidate>>();
            arms[8] = triples.Select(t => t.D8).ToList();
            arms[20] = triples.Select(t => t.D20).ToList();
            arms[24] = triples.Select(t => t.D24).ToList();

            Node[] sources = new Node[probes];
            Random sourceRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 2333, 0));
            for (int i = 0; i < probes; i++)
                sources[i] = sourcePool[sourceRandom.Next(sourcePool.Count)];
            string probeCoordinateHash = NeutralProbeCoordinateHash(
                experimentSeed, 2333, 7, sources);
            string topologyFamily = UniformLocalAttachment
                ? "uniform" : "popularity";

            List<string> rows = new List<string>();
            foreach (int postDegree in new int[] { 8, 20, 24 })
            {
                List<PositionCandidate> selected = arms[postDegree];
                string armHash = null;
                int armNodes = 0;
                long armEdges = 0;
                int armComponents = 0;
                int armLargestComponent = 0;
                int successes = 0;
                long visited = 0;
                long messages = 0;
                try
                {
                    PositionPlace(holder, selected.Select(x => x.Node).ToList());
                    if (holder.Degree != maxDegree ||
                        SweetSpotNeighbours(holder).Any(n => n.Degree != postDegree))
                        throw new InvalidOperationException(
                            "Position matched exact degree invariant failed.");
                    SweetSpotValidateTopology(out armNodes, out armEdges);
                    if (armNodes != liveNodes || armEdges != baseEdges)
                        throw new InvalidOperationException(
                            "Position matched node or edge invariant failed.");
                    armHash = SweetSpotTopologyHash();
                    SweetSpotComponents(out armComponents, out armLargestComponent);
                    if (armComponents != 1 || armLargestComponent != liveNodes)
                        throw new InvalidOperationException(
                            "Position matched connectivity invariant failed.");

                    for (int i = 0; i < probes; i++)
                    {
                        Random probeRandom = new Random(
                            SweetSpotStableSeed(experimentSeed, 7, i));
                        SweetSpotProbeResult result = SweetSpotBudgetSearch(
                            sources[i], targetValue, 7, visitBudget, probeRandom);
                        if (result.Success) successes++;
                        visited += result.UniqueVisited;
                        messages += result.Messages;
                    }
                    if (SweetSpotTopologyHash() != armHash)
                        throw new InvalidOperationException("Outcome probes changed the graph.");
                }
                finally
                {
                    SweetSpotRestoreHolder(holder, baseNeighbors);
                }
                string restoredHash = SweetSpotTopologyHash();
                if (restoredHash != baseHash)
                    throw new InvalidOperationException("Position matched restore failed.");

                double meanPairGap = postDegree == 8 ? 0.0 :
                    triples.Average(t => postDegree == 20
                        ? Math.Abs(t.D8.Exposure - t.D20.Exposure)
                        : Math.Abs(t.D8.Exposure - t.D24.Exposure));
                rows.Add(String.Join(",", new string[] {
                    experimentSeed.ToString(CultureInfo.InvariantCulture),
                    topologyFamily,
                    "7",
                    postDegree.ToString(CultureInfo.InvariantCulture),
                    probes.ToString(CultureInfo.InvariantCulture),
                    successes.ToString(CultureInfo.InvariantCulture),
                    (successes / (double)probes).ToString("R", CultureInfo.InvariantCulture),
                    visitBudget.ToString(CultureInfo.InvariantCulture),
                    balanceProbes.ToString(CultureInfo.InvariantCulture),
                    (postDegree == 8 ? d8.Count : postDegree == 20 ? d20.Count : d24.Count)
                        .ToString(CultureInfo.InvariantCulture),
                    selected.Average(x => x.Exposure).ToString("R", CultureInfo.InvariantCulture),
                    selected.Average(x => x.ExposurePercentile).ToString("R", CultureInfo.InvariantCulture),
                    selected.Average(x => x.CoreDistance).ToString("R", CultureInfo.InvariantCulture),
                    selected.Average(x => x.Clustering).ToString("R", CultureInfo.InvariantCulture),
                    selected.Average(x => x.MeanNeighborDegree).ToString("R", CultureInfo.InvariantCulture),
                    meanPairGap.ToString("R", CultureInfo.InvariantCulture),
                    (visited / (double)probes).ToString("R", CultureInfo.InvariantCulture),
                    (messages / (double)probes).ToString("R", CultureInfo.InvariantCulture),
                    armNodes.ToString(CultureInfo.InvariantCulture),
                    armEdges.ToString(CultureInfo.InvariantCulture),
                    armComponents.ToString(CultureInfo.InvariantCulture),
                    armLargestComponent.ToString(CultureInfo.InvariantCulture),
                    targetValue.ToString(CultureInfo.InvariantCulture),
                    holder.NodeID.ToString(CultureInfo.InvariantCulture),
                    probeCoordinateHash,
                    baseHash,
                    armHash,
                    restoredHash
                }));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? ".");
            using (FileStream stream = new FileStream(
                summaryPartial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream))
            {
                writer.WriteLine(
                    "seed,topologyFamily,fanout,targetPostDegree,probes,successes,pHat,visitBudget," +
                    "balanceProbes,candidatePool,meanBaseExposure,meanExposurePercentile," +
                    "meanCoreDistance,meanClustering,meanNeighborDegree,meanPairedExposureGap," +
                    "meanVisited,meanMessages,liveNodes,edgeCount,components,largestComponent," +
                    "targetValue,holderId,probeCoordinateHash,baseHash,armHash,restoredHash");
                foreach (string row in rows) writer.WriteLine(row);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(summaryPartial, summaryPath);
        }

        private List<PositionCandidate> PositionPool(
            List<Node> live, HashSet<Node> excluded, int preDegree,
            Dictionary<Node, int> visits, int balanceProbes,
            Dictionary<Node, int> coreDistance)
        {
            List<PositionCandidate> result = new List<PositionCandidate>();
            foreach (Node node in live)
            {
                if (excluded.Contains(node) || node.Degree != preDegree ||
                    node.Degree >= maxDegree) continue;
                int count;
                visits.TryGetValue(node, out count);
                List<Node> neighbors = SweetSpotNeighbours(node);
                PositionCandidate item = new PositionCandidate();
                item.Node = node;
                item.Exposure = count / (double)balanceProbes;
                item.CoreDistance = coreDistance[node];
                item.Clustering = LocalClustering(node);
                item.MeanNeighborDegree = neighbors.Count == 0 ? 0.0 :
                    neighbors.Average(n => (double)n.Degree);
                result.Add(item);
            }
            return result;
        }

        private List<PositionTriple> PositionMatchTriples(
            List<PositionCandidate> d8, List<PositionCandidate> d20,
            List<PositionCandidate> d24, int required)
        {
            List<PositionCandidate> ordered = d24.OrderBy(c =>
                PositionNearest(c, d8, null).Item2 +
                PositionNearest(c, d20, null).Item2)
                .ThenBy(c => c.Node.NodeID).ToList();
            HashSet<Node> used8 = new HashSet<Node>();
            HashSet<Node> used20 = new HashSet<Node>();
            List<PositionTriple> result = new List<PositionTriple>();
            foreach (PositionCandidate c in ordered)
            {
                Tuple<PositionCandidate, double> a = PositionNearest(c, d8, used8);
                Tuple<PositionCandidate, double> b = PositionNearest(c, d20, used20);
                if (a.Item1 == null || b.Item1 == null) break;
                used8.Add(a.Item1.Node);
                used20.Add(b.Item1.Node);
                result.Add(new PositionTriple {
                    D8 = a.Item1, D20 = b.Item1, D24 = c,
                    Cost = a.Item2 + b.Item2
                });
                if (result.Count == required) break;
            }
            return result;
        }

        private Tuple<PositionCandidate, double> PositionNearest(
            PositionCandidate source, List<PositionCandidate> pool,
            HashSet<Node> used)
        {
            PositionCandidate best = null;
            double bestDistance = double.PositiveInfinity;
            foreach (PositionCandidate candidate in pool)
            {
                if (used != null && used.Contains(candidate.Node)) continue;
                double distance = PositionDistance(source, candidate);
                if (distance < bestDistance ||
                    (Math.Abs(distance - bestDistance) < 1e-15 && best != null &&
                     candidate.Node.NodeID < best.Node.NodeID))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return Tuple.Create(best, bestDistance);
        }

        private double PositionDistance(PositionCandidate a, PositionCandidate b)
        {
            return 4.0 * Math.Abs(a.ExposurePercentile - b.ExposurePercentile) +
                Math.Abs(a.CoreDistance - b.CoreDistance) +
                0.5 * Math.Abs(a.ClusteringPercentile - b.ClusteringPercentile) +
                0.5 * Math.Abs(a.NeighborPercentile - b.NeighborPercentile);
        }

        private void PositionAddRanks(List<PositionCandidate> pool)
        {
            PositionAssignRank(pool.OrderBy(x => x.Exposure).ThenBy(x => x.Node.NodeID).ToList(),
                delegate(PositionCandidate x, double value) { x.ExposurePercentile = value; });
            PositionAssignRank(pool.OrderBy(x => x.Clustering).ThenBy(x => x.Node.NodeID).ToList(),
                delegate(PositionCandidate x, double value) { x.ClusteringPercentile = value; });
            PositionAssignRank(pool.OrderBy(x => x.MeanNeighborDegree).ThenBy(x => x.Node.NodeID).ToList(),
                delegate(PositionCandidate x, double value) { x.NeighborPercentile = value; });
        }

        private void PositionAssignRank(
            List<PositionCandidate> ordered, Action<PositionCandidate, double> assign)
        {
            double denominator = Math.Max(1, ordered.Count - 1);
            for (int i = 0; i < ordered.Count; i++) assign(ordered[i], i / denominator);
        }

        private void PositionPlace(Node holder, List<Node> selected)
        {
            List<Node> current = SweetSpotNeighbours(holder);
            foreach (Node node in current) SweetSpotRemoveUndirectedEdge(holder, node);
            foreach (Node node in selected) SweetSpotAddUndirectedEdge(holder, node);
        }

        private HashSet<Node> PositionTrace(
            Node begin, int fanout, int visitBudget, Random random)
        {
            HashSet<Node> visited = new HashSet<Node>();
            List<Node> frontier = new List<Node>();
            List<Node> parents = new List<Node>();
            visited.Add(begin);
            frontier.Add(begin);
            parents.Add(null);
            while (frontier.Count > 0 && visited.Count < visitBudget)
            {
                List<Node> next = new List<Node>();
                List<Node> nextParents = new List<Node>();
                for (int f = 0; f < frontier.Count && visited.Count < visitBudget; f++)
                {
                    Node current = frontier[f];
                    Node parent = parents[f];
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
                        if (!visited.Add(candidate)) continue;
                        next.Add(candidate);
                        nextParents.Add(current);
                    }
                }
                frontier = next;
                parents = nextParents;
            }
            return visited;
        }
    }
}
