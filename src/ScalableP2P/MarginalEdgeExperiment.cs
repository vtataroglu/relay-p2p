using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScalableP2P
{
    partial class Graph
    {
        public void RunMarginalEdgeExperiment(
            string summaryPath, int experimentSeed, int probes,
            int visitBudget, int expectedLiveNodes)
        {
            if (probes <= 0 || visitBudget <= 1)
                throw new ArgumentOutOfRangeException("marginal-edge budget");
            if (File.Exists(summaryPath))
                throw new IOException("Refusing to overwrite result: " + summaryPath);

            int liveNodes;
            long baseEdges;
            SweetSpotValidateTopology(out liveNodes, out baseEdges);
            if (liveNodes != expectedLiveNodes)
                throw new InvalidOperationException("Marginal-edge live-node mismatch.");

            int targetValue = Trend != null ? Trend.TbpVal : 1000;
            Node holder = getHolder(targetValue);
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException("Marginal-edge holder is not saturated.");
            List<Node> baseNeighbours = SweetSpotNeighbours(holder);
            baseNeighbours.Sort(delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
            Node removed = null;
            for (int i = 0; i < baseNeighbours.Count; i++)
                if (baseNeighbours[i].Degree > minDegree)
                {
                    removed = baseNeighbours[i];
                    break;
                }
            if (removed == null)
                throw new InvalidOperationException("No removable marginal-edge incumbent.");

            string baseHash = SweetSpotTopologyHash();
            List<Node> sourcePool = SweetSpotLiveNodes();
            sourcePool.Remove(holder);
            sourcePool.Sort(delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
            Node[] sources = new Node[probes];
            Random sourceRandom = new Random(SweetSpotStableSeed(experimentSeed, 1901, 0));
            for (int i = 0; i < probes; i++)
                sources[i] = sourcePool[sourceRandom.Next(sourcePool.Count)];

            int[,] arms = new int[,] { {3,4}, {3,8}, {7,8}, {7,20}, {7,24} };
            List<string> summaries = new List<string>();
            string rawPath = Path.Combine(
                Path.GetDirectoryName(summaryPath) ?? ".",
                Path.GetFileNameWithoutExtension(summaryPath) + "-probes.csv");
            string rawTemp = rawPath + ".partial";
            string summaryTemp = summaryPath + ".partial";
            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? ".");
            using (StreamWriter raw = new StreamWriter(
                new FileStream(rawTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                raw.WriteLine("seed,fanout,targetPostDegree,probeIndex,sourceId,success,uniqueVisited,messages");
                for (int a = 0; a < arms.GetLength(0); a++)
                {
                    int fanout = arms[a,0];
                    int targetPostDegree = arms[a,1];
                    Node graft = null;
                    int available = 0;
                    try
                    {
                        SweetSpotRemoveUndirectedEdge(holder, removed);
                        HashSet<int> fixedIds = new HashSet<int>(
                            SweetSpotNeighbours(holder).Select(n => n.NodeID));
                        List<Node> candidates = SweetSpotLiveNodes().Where(delegate(Node n)
                        {
                            return n != holder && n != removed &&
                                !fixedIds.Contains(n.NodeID) &&
                                n.Degree == targetPostDegree - 1 && n.Degree < maxDegree &&
                                !doesLinkExist(n, holder.NodeID);
                        }).ToList();
                        available = candidates.Count;
                        if (available == 0)
                            throw new InvalidOperationException(
                                "No exact marginal candidate for d=" + targetPostDegree + ".");
                        candidates.Sort(delegate(Node x, Node y) { return x.NodeID.CompareTo(y.NodeID); });
                        Random placement = new Random(
                            SweetSpotStableSeed(experimentSeed, targetPostDegree, 1931));
                        graft = candidates[placement.Next(candidates.Count)];
                        SweetSpotAddUndirectedEdge(holder, graft);

                        int armLive;
                        long armEdges;
                        SweetSpotValidateTopology(out armLive, out armEdges);
                        if (armLive != liveNodes || armEdges != baseEdges ||
                            holder.Degree != maxDegree || graft.Degree != targetPostDegree)
                            throw new InvalidOperationException("Marginal-edge invariant failed.");
                        string armHash = SweetSpotTopologyHash();
                        int successes = 0;
                        long visited = 0;
                        long messages = 0;
                        for (int i = 0; i < probes; i++)
                        {
                            Random probeRandom = new Random(
                                SweetSpotStableSeed(experimentSeed, fanout, i));
                            SweetSpotProbeResult result = SweetSpotBudgetSearch(
                                sources[i], targetValue, fanout, visitBudget, probeRandom);
                            if (result.Success) successes++;
                            visited += result.UniqueVisited;
                            messages += result.Messages;
                            raw.WriteLine(
                                experimentSeed + "," + fanout + "," + targetPostDegree + "," +
                                i + "," + sources[i].NodeID + "," + (result.Success ? 1 : 0) +
                                "," + result.UniqueVisited + "," + result.Messages);
                        }
                        if (SweetSpotTopologyHash() != armHash)
                            throw new InvalidOperationException("Marginal probes mutated topology.");
                        summaries.Add(
                            experimentSeed + "," + fanout + "," + targetPostDegree + "," +
                            available + "," + removed.NodeID + "," + removed.Degree + "," +
                            graft.NodeID + "," + probes + "," + successes + "," +
                            SweetSpotFormat(successes / (double)probes) + "," + visitBudget + "," +
                            SweetSpotFormat(visited / (double)probes) + "," +
                            SweetSpotFormat(messages / (double)probes) + "," + baseHash + "," + armHash);
                    }
                    finally
                    {
                        if (graft != null && doesLinkExist(holder, graft.NodeID))
                            SweetSpotRemoveUndirectedEdge(holder, graft);
                        if (!doesLinkExist(holder, removed.NodeID))
                            SweetSpotAddUndirectedEdge(holder, removed);
                    }
                    if (SweetSpotTopologyHash() != baseHash)
                        throw new InvalidOperationException("Marginal-edge restore failed.");
                }
            }
            using (StreamWriter summary = new StreamWriter(
                new FileStream(summaryTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                summary.WriteLine(
                    "seed,fanout,targetPostDegree,candidateCount,removedNodeId," +
                    "removedBaseDegree,graftNodeId,probes,successes,pHat,visitBudget," +
                    "meanVisited,meanMessages,baseHash,armHash");
                for (int i = 0; i < summaries.Count; i++) summary.WriteLine(summaries[i]);
            }
            File.Move(rawTemp, rawPath);
            File.Move(summaryTemp, summaryPath);
        }
    }
}
