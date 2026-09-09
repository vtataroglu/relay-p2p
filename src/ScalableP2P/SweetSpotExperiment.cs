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
        private sealed class SweetSpotArmResult
        {
            public int Seed;
            public int Fanout;
            public int TargetPostDegree;
            public int CandidateCount;
            public int Probes;
            public int Successes;
            public int VisitBudget;
            public double MeanVisited;
            public double MeanMessages;
            public double UtilitySum;
            public double MeanNeighbourDegree;
            public int MinNeighbourDegree;
            public int MaxNeighbourDegree;
            public long EdgeCount;
            public int Components;
            public int LargestComponent;
            public string BaseHash;
            public string ArmHash;
            public string RestoredHash;
        }

        private struct SweetSpotProbeResult
        {
            public bool Success;
            public int UniqueVisited;
            public int Messages;
        }

        public static double RelayUtility(int fanout, int postDegree)
        {
            if (fanout <= 0) throw new ArgumentOutOfRangeException("fanout");
            if (postDegree <= 1) throw new ArgumentOutOfRangeException("postDegree");
            double relayProbability = Math.Min(1.0, fanout / (double)(postDegree - 1));
            return postDegree * relayProbability;
        }

        public static void AssertRelayUtilitySelfTest()
        {
            for (int fanout = 1; fanout <= 12; fanout++)
            {
                int optimum = fanout + 1;
                double best = RelayUtility(fanout, optimum);
                for (int degree = 2; degree <= 64; degree++)
                {
                    double value = RelayUtility(fanout, degree);
                    if (value > best + 1e-12)
                        throw new InvalidOperationException(
                            "Relay utility maximum failed for m=" + fanout +
                            ": d=" + degree + " exceeds d*=m+1.");
                }
            }

            if (!(RelayUtility(3, 4) > RelayUtility(3, 8)))
                throw new InvalidOperationException("Expected U_3(4) > U_3(8).");
            if (!(RelayUtility(7, 8) > RelayUtility(7, 4)))
                throw new InvalidOperationException("Expected U_7(8) > U_7(4).");
        }

        public void RunSweetSpotCrossover(
            string summaryPath,
            int experimentSeed,
            int probes,
            int visitBudget,
            int expectedLiveNodes,
            bool reverseArmOrder)
        {
            if (probes <= 0) throw new ArgumentOutOfRangeException("probes");
            if (visitBudget <= 1) throw new ArgumentOutOfRangeException("visitBudget");
            if (File.Exists(summaryPath))
                throw new IOException("Refusing to overwrite result: " + summaryPath);

            string rawPath = Path.Combine(
                Path.GetDirectoryName(summaryPath) ?? ".",
                Path.GetFileNameWithoutExtension(summaryPath) + "-probes.csv");
            if (File.Exists(rawPath))
                throw new IOException("Refusing to overwrite raw result: " + rawPath);

            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? ".");
            AssertRelayUtilitySelfTest();

            int liveNodes;
            long baseEdges;
            SweetSpotValidateTopology(out liveNodes, out baseEdges);
            if (liveNodes != expectedLiveNodes)
                throw new InvalidOperationException(
                    "Live-node invariant failed: expected " + expectedLiveNodes +
                    ", observed " + liveNodes + ".");

            int targetValue = Trend != null ? Trend.TbpVal : 1000;
            Node holder = getHolder(targetValue);
            if (holder == null)
                throw new InvalidOperationException("TBP holder is absent at terminal evaluation.");
            if (holder.Degree != maxDegree)
                throw new InvalidOperationException(
                    "Holder is not saturated: expected degree " + maxDegree +
                    ", observed " + holder.Degree + ".");

            List<Node> baseNeighbours = SweetSpotNeighbours(holder);
            if (baseNeighbours.Count != maxDegree)
                throw new InvalidOperationException("Holder adjacency does not match the hard cap.");

            string baseHash = SweetSpotTopologyHash();
            int baseComponents, baseLargest;
            SweetSpotComponents(out baseComponents, out baseLargest);

            List<Node> sourcePool = SweetSpotLiveNodes();
            sourcePool.Remove(holder);
            sourcePool.Sort(delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
            Random sourceRandom = new Random(SweetSpotStableSeed(experimentSeed, 1709, 0));
            Node[] sources = new Node[probes];
            for (int i = 0; i < probes; i++)
                sources[i] = sourcePool[sourceRandom.Next(sourcePool.Count)];

            List<SweetSpotArmResult> results = new List<SweetSpotArmResult>();
            List<string> rawRows = new List<string>(probes * 4);
            int[] targets = reverseArmOrder ? new int[] { 8, 4 } : new int[] { 4, 8 };
            int[] fanouts = reverseArmOrder ? new int[] { 7, 3 } : new int[] { 3, 7 };

            for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                int targetPostDegree = targets[targetIndex];
                int candidateCount = 0;
                try
                {
                    candidateCount = SweetSpotPlaceHolderAtExactDegree(
                        holder, targetPostDegree, experimentSeed);

                    if (holder.Degree != maxDegree)
                        throw new InvalidOperationException("Counterfactual holder degree changed.");

                    List<Node> placedNeighbours = SweetSpotNeighbours(holder);
                    if (placedNeighbours.Count != maxDegree ||
                        placedNeighbours.Any(n => n.Degree != targetPostDegree))
                        throw new InvalidOperationException(
                            "Exact post-graft degree invariant failed for d=" +
                            targetPostDegree + ".");

                    int armLive;
                    long armEdges;
                    SweetSpotValidateTopology(out armLive, out armEdges);
                    if (armLive != liveNodes || armEdges != baseEdges)
                        throw new InvalidOperationException(
                            "Node/edge invariant failed during counterfactual placement.");

                    string armHash = SweetSpotTopologyHash();
                    int components, largestComponent;
                    SweetSpotComponents(out components, out largestComponent);

                    for (int fanoutIndex = 0; fanoutIndex < fanouts.Length; fanoutIndex++)
                    {
                        int fanout = fanouts[fanoutIndex];
                        long visitedTotal = 0;
                        long messageTotal = 0;
                        int successes = 0;

                        for (int probeIndex = 0; probeIndex < probes; probeIndex++)
                        {
                            Random probeRandom = new Random(
                                SweetSpotStableSeed(experimentSeed, fanout, probeIndex));
                            SweetSpotProbeResult probe = SweetSpotBudgetSearch(
                                sources[probeIndex], targetValue, fanout,
                                visitBudget, probeRandom);
                            if (probe.Success) successes++;
                            visitedTotal += probe.UniqueVisited;
                            messageTotal += probe.Messages;
                            rawRows.Add(
                                experimentSeed + "," + fanout + "," + targetPostDegree + "," +
                                probeIndex + "," + sources[probeIndex].NodeID + "," +
                                (probe.Success ? 1 : 0) + "," + probe.UniqueVisited + "," +
                                probe.Messages);
                        }

                        double utilitySum = 0;
                        int minNeighbourDegree = int.MaxValue;
                        int maxNeighbourDegree = int.MinValue;
                        long neighbourDegreeSum = 0;
                        for (int i = 0; i < placedNeighbours.Count; i++)
                        {
                            int degree = placedNeighbours[i].Degree;
                            utilitySum += RelayUtility(fanout, degree);
                            neighbourDegreeSum += degree;
                            minNeighbourDegree = Math.Min(minNeighbourDegree, degree);
                            maxNeighbourDegree = Math.Max(maxNeighbourDegree, degree);
                        }

                        SweetSpotArmResult result = new SweetSpotArmResult();
                        result.Seed = experimentSeed;
                        result.Fanout = fanout;
                        result.TargetPostDegree = targetPostDegree;
                        result.CandidateCount = candidateCount;
                        result.Probes = probes;
                        result.Successes = successes;
                        result.VisitBudget = visitBudget;
                        result.MeanVisited = visitedTotal / (double)probes;
                        result.MeanMessages = messageTotal / (double)probes;
                        result.UtilitySum = utilitySum;
                        result.MeanNeighbourDegree =
                            neighbourDegreeSum / (double)placedNeighbours.Count;
                        result.MinNeighbourDegree = minNeighbourDegree;
                        result.MaxNeighbourDegree = maxNeighbourDegree;
                        result.EdgeCount = armEdges;
                        result.Components = components;
                        result.LargestComponent = largestComponent;
                        result.BaseHash = baseHash;
                        result.ArmHash = armHash;
                        results.Add(result);

                        string postProbeHash = SweetSpotTopologyHash();
                        if (postProbeHash != armHash)
                            throw new InvalidOperationException(
                                "A terminal probe mutated the topology.");
                    }
                }
                finally
                {
                    SweetSpotRestoreHolder(holder, baseNeighbours);
                }

                int restoredLive;
                long restoredEdges;
                SweetSpotValidateTopology(out restoredLive, out restoredEdges);
                string restoredHash = SweetSpotTopologyHash();
                if (restoredLive != liveNodes || restoredEdges != baseEdges ||
                    restoredHash != baseHash)
                    throw new InvalidOperationException(
                        "Counterfactual restore failed for target d=" +
                        targetPostDegree + ".");

                for (int i = 0; i < results.Count; i++)
                    if (results[i].TargetPostDegree == targetPostDegree)
                        results[i].RestoredHash = restoredHash;
            }

            if (SweetSpotTopologyHash() != baseHash)
                throw new InvalidOperationException("Final base topology hash mismatch.");

            results.Sort(delegate(SweetSpotArmResult a, SweetSpotArmResult b)
            {
                int c = a.Fanout.CompareTo(b.Fanout);
                return c != 0 ? c : a.TargetPostDegree.CompareTo(b.TargetPostDegree);
            });
            rawRows.Sort(delegate(string a, string b)
            {
                string[] aa = a.Split(',');
                string[] bb = b.Split(',');
                int c = int.Parse(aa[1]).CompareTo(int.Parse(bb[1]));
                if (c != 0) return c;
                c = int.Parse(aa[2]).CompareTo(int.Parse(bb[2]));
                if (c != 0) return c;
                return int.Parse(aa[3]).CompareTo(int.Parse(bb[3]));
            });

            string summaryTemp = summaryPath + ".partial";
            string rawTemp = rawPath + ".partial";
            if (File.Exists(summaryTemp) || File.Exists(rawTemp))
                throw new IOException("Partial output already exists; refusing to overwrite it.");

            using (StreamWriter writer = new StreamWriter(
                new FileStream(summaryTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                writer.WriteLine(
                    "seed,fanout,targetPostDegree,candidateCount,probes,successes,pHat," +
                    "visitBudget,meanVisited,meanMessages,utilitySum,meanNeighbourDegree," +
                    "minNeighbourDegree,maxNeighbourDegree,edgeCount,components," +
                    "largestComponent,baseComponents,baseLargest,baseHash,armHash,restoredHash");
                for (int i = 0; i < results.Count; i++)
                {
                    SweetSpotArmResult r = results[i];
                    writer.WriteLine(
                        r.Seed + "," + r.Fanout + "," + r.TargetPostDegree + "," +
                        r.CandidateCount + "," + r.Probes + "," + r.Successes + "," +
                        SweetSpotFormat(r.Successes / (double)r.Probes) + "," +
                        r.VisitBudget + "," + SweetSpotFormat(r.MeanVisited) + "," +
                        SweetSpotFormat(r.MeanMessages) + "," +
                        SweetSpotFormat(r.UtilitySum) + "," +
                        SweetSpotFormat(r.MeanNeighbourDegree) + "," +
                        r.MinNeighbourDegree + "," + r.MaxNeighbourDegree + "," +
                        r.EdgeCount + "," + r.Components + "," + r.LargestComponent + "," +
                        baseComponents + "," + baseLargest + "," + r.BaseHash + "," +
                        r.ArmHash + "," + r.RestoredHash);
                }
            }

            using (StreamWriter writer = new StreamWriter(
                new FileStream(rawTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                writer.WriteLine(
                    "seed,fanout,targetPostDegree,probeIndex,sourceId,success," +
                    "uniqueVisited,messages");
                for (int i = 0; i < rawRows.Count; i++) writer.WriteLine(rawRows[i]);
            }

            File.Move(rawTemp, rawPath);
            // The compact summary is the completion marker and is moved last.
            File.Move(summaryTemp, summaryPath);
        }

        private SweetSpotProbeResult SweetSpotBudgetSearch(
            Node begin,
            int targetValue,
            int fanout,
            int visitBudget,
            Random probeRandom)
        {
            HashSet<Node> visited = new HashSet<Node>();
            List<Node> frontierNodes = new List<Node>();
            List<Node> frontierParents = new List<Node>();
            visited.Add(begin);
            if (begin.Item.Values.Contains(targetValue))
                return new SweetSpotProbeResult
                {
                    Success = true,
                    UniqueVisited = 1,
                    Messages = 0
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
                    List<Node> neighbours = SweetSpotNeighbours(current);
                    if (parent != null) neighbours.Remove(parent);
                    neighbours.Sort(delegate(Node a, Node b)
                    {
                        return a.NodeID.CompareTo(b.NodeID);
                    });

                    int forward = Math.Min(fanout, neighbours.Count);
                    for (int k = 0; k < forward && visited.Count < visitBudget; k++)
                    {
                        int pick = probeRandom.Next(k, neighbours.Count);
                        Node temp = neighbours[pick];
                        neighbours[pick] = neighbours[k];
                        neighbours[k] = temp;
                        Node candidate = neighbours[k];
                        messages++;
                        if (visited.Contains(candidate)) continue;
                        visited.Add(candidate);
                        if (candidate.Item.Values.Contains(targetValue))
                            return new SweetSpotProbeResult
                            {
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

            return new SweetSpotProbeResult
            {
                Success = false,
                UniqueVisited = visited.Count,
                Messages = messages
            };
        }

        private int SweetSpotPlaceHolderAtExactDegree(
            Node holder,
            int targetPostDegree,
            int experimentSeed)
        {
            List<Node> current = SweetSpotNeighbours(holder);
            for (int i = 0; i < current.Count; i++)
                SweetSpotRemoveUndirectedEdge(holder, current[i]);

            int targetPreDegree = targetPostDegree - 1;
            List<Node> candidates = SweetSpotLiveNodes().Where(delegate(Node n)
            {
                return n != holder && n.Degree == targetPreDegree &&
                       n.Degree < maxDegree && !doesLinkExist(n, holder.NodeID);
            }).ToList();
            int available = candidates.Count;
            if (available < maxDegree)
                throw new InvalidOperationException(
                    "Exact candidate stratum d_pre=" + targetPreDegree +
                    " has only " + available + " nodes; need " + maxDegree + ".");

            candidates.Sort(delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
            Random placementRandom = new Random(
                SweetSpotStableSeed(experimentSeed, targetPostDegree, 7919));
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = placementRandom.Next(i + 1);
                Node temp = candidates[i];
                candidates[i] = candidates[j];
                candidates[j] = temp;
            }
            for (int i = 0; i < maxDegree; i++)
                SweetSpotAddUndirectedEdge(holder, candidates[i]);
            return available;
        }

        private void SweetSpotRestoreHolder(Node holder, List<Node> baseNeighbours)
        {
            List<Node> current = SweetSpotNeighbours(holder);
            for (int i = 0; i < current.Count; i++)
                SweetSpotRemoveUndirectedEdge(holder, current[i]);
            for (int i = 0; i < baseNeighbours.Count; i++)
                SweetSpotAddUndirectedEdge(holder, baseNeighbours[i]);
        }

        private void SweetSpotAddUndirectedEdge(Node a, Node b)
        {
            if (a == b) throw new InvalidOperationException("Self-edge requested.");
            if (doesLinkExist(a, b.NodeID) || doesLinkExist(b, a.NodeID))
                throw new InvalidOperationException("Duplicate/asymmetric edge requested.");

            Link lastA = findLastLink(a.NextLink);
            if (lastA == null) a.NextLink = createLink(b.NodeID);
            else lastA.NextLink = createLink(b.NodeID);
            a.Degree++;

            Link lastB = findLastLink(b.NextLink);
            if (lastB == null) b.NextLink = createLink(a.NodeID);
            else lastB.NextLink = createLink(a.NodeID);
            b.Degree++;
        }

        private void SweetSpotRemoveUndirectedEdge(Node a, Node b)
        {
            bool removedA = SweetSpotRemoveDirectedEdge(a, b.NodeID);
            bool removedB = SweetSpotRemoveDirectedEdge(b, a.NodeID);
            if (!removedA || !removedB)
                throw new InvalidOperationException("Asymmetric/missing edge during removal.");
        }

        private bool SweetSpotRemoveDirectedEdge(Node from, int toId)
        {
            Link current = from.NextLink;
            Link previous = null;
            while (current != null)
            {
                if (current.Id == toId)
                {
                    if (previous == null) from.NextLink = current.NextLink;
                    else previous.NextLink = current.NextLink;
                    from.Degree--;
                    return true;
                }
                previous = current;
                current = current.NextLink;
            }
            return false;
        }

        private List<Node> SweetSpotNeighbours(Node node)
        {
            List<Node> neighbours = new List<Node>();
            Link link = node.NextLink;
            while (link != null)
            {
                if (link.VertexLink == null)
                    throw new InvalidOperationException("Null neighbour pointer.");
                neighbours.Add(link.VertexLink);
                link = link.NextLink;
            }
            return neighbours;
        }

        private List<Node> SweetSpotLiveNodes()
        {
            List<Node> nodes = new List<Node>();
            Node current = nodeHead;
            while (current != null)
            {
                nodes.Add(current);
                current = current.NextNode;
            }
            return nodes;
        }

        private void SweetSpotValidateTopology(out int liveNodes, out long edgeCount)
        {
            List<Node> nodes = SweetSpotLiveNodes();
            HashSet<int> liveIds = new HashSet<int>();
            HashSet<Node> liveSet = new HashSet<Node>(nodes);
            for (int i = 0; i < nodes.Count; i++)
                if (!liveIds.Add(nodes[i].NodeID))
                    throw new InvalidOperationException("Duplicate live node ID.");
            long directedEdges = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                Node node = nodes[i];
                HashSet<int> adjacent = new HashSet<int>();
                Link link = node.NextLink;
                while (link != null)
                {
                    if (link.VertexLink == null || !liveSet.Contains(link.VertexLink))
                        throw new InvalidOperationException("Null/non-live adjacency.");
                    if (link.VertexLink.NodeID == node.NodeID)
                        throw new InvalidOperationException("Self adjacency.");
                    if (!adjacent.Add(link.VertexLink.NodeID))
                        throw new InvalidOperationException("Duplicate adjacency.");
                    if (!doesLinkExist(link.VertexLink, node.NodeID))
                        throw new InvalidOperationException("Non-reciprocal adjacency.");
                    directedEdges++;
                    link = link.NextLink;
                }
                if (adjacent.Count != node.Degree)
                    throw new InvalidOperationException(
                        "Stored degree differs from adjacency count at node " +
                        node.NodeID + ".");
            }
            if ((directedEdges & 1L) != 0)
                throw new InvalidOperationException("Odd directed-edge total.");
            liveNodes = nodes.Count;
            edgeCount = directedEdges / 2;
        }

        private void SweetSpotComponents(out int componentCount, out int largestComponent)
        {
            List<Node> nodes = SweetSpotLiveNodes();
            HashSet<Node> seen = new HashSet<Node>();
            componentCount = 0;
            largestComponent = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                Node start = nodes[i];
                if (seen.Contains(start)) continue;
                componentCount++;
                int size = 0;
                Queue<Node> queue = new Queue<Node>();
                queue.Enqueue(start);
                seen.Add(start);
                while (queue.Count > 0)
                {
                    Node node = queue.Dequeue();
                    size++;
                    Link link = node.NextLink;
                    while (link != null)
                    {
                        Node neighbour = link.VertexLink;
                        if (neighbour != null && seen.Add(neighbour))
                            queue.Enqueue(neighbour);
                        link = link.NextLink;
                    }
                }
                largestComponent = Math.Max(largestComponent, size);
            }
        }

        private string SweetSpotTopologyHash()
        {
            List<Node> nodes = SweetSpotLiveNodes();
            nodes.Sort(delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
            List<long> edges = new List<long>();
            for (int i = 0; i < nodes.Count; i++)
            {
                Node node = nodes[i];
                Link link = node.NextLink;
                while (link != null)
                {
                    int other = link.VertexLink.NodeID;
                    if (node.NodeID < other)
                        edges.Add(((long)node.NodeID << 32) | (uint)other);
                    link = link.NextLink;
                }
            }
            edges.Sort();

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(nodes.Count);
                for (int i = 0; i < nodes.Count; i++)
                {
                    writer.Write(nodes[i].NodeID);
                    writer.Write(nodes[i].Degree);
                }
                writer.Write(edges.Count);
                for (int i = 0; i < edges.Count; i++) writer.Write(edges[i]);
                writer.Flush();
                stream.Position = 0;
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream))
                        .Replace("-", "").ToLowerInvariant();
            }
        }

        private static int SweetSpotStableSeed(int seed, int factor, int index)
        {
            int value = unchecked(
                seed * 1000003 ^ factor * 9176 ^ index * 7919 ^ 0x2c9277b5);
            value &= 0x7fffffff;
            return value == 0 ? 1 : value;
        }

        private static string SweetSpotFormat(double value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }
    }
}
