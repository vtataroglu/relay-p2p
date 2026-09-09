using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ScalableP2P
{
    partial class Graph
    {
        public void RunOriginalMultiHolder(
            string outputPath, int experimentSeed, int holderCount,
            int fanout, int probes, int visitBudget, int expectedLiveNodes)
        {
            if (holderCount <= 0 || probes <= 0 || visitBudget <= 1)
                throw new ArgumentOutOfRangeException("multi-holder design");
            if (File.Exists(outputPath))
                throw new IOException("Refusing to overwrite result: " + outputPath);
            int live;
            long baseEdges;
            SweetSpotValidateTopology(out live, out baseEdges);
            if (live != expectedLiveNodes) throw new InvalidOperationException("live-node mismatch");

            List<Node> pool = SweetSpotLiveNodes().Where(n => n.Degree >= 8).ToList();
            pool.Sort((a,b) => MultiKey(experimentSeed, a.NodeID, 17)
                .CompareTo(MultiKey(experimentSeed, b.NodeID, 17)));
            if (pool.Count < holderCount) throw new InvalidOperationException("insufficient holders");
            List<Node> holders = pool.Take(holderCount).ToList();
            HashSet<Node> holderSet = new HashSet<Node>(holders);
            Dictionary<Node,int> values = new Dictionary<Node,int>();
            for (int i=0; i<holders.Count; i++)
            {
                int value = 50000 + i;
                holders[i].Item.addItem(value);
                values[holders[i]] = value;
            }
            List<Node> sources = SweetSpotLiveNodes();
            foreach (Node h in holders) sources.Remove(h);
            sources.Sort((a,b) => a.NodeID.CompareTo(b.NodeID));

            double sham = MultiDiscovery(holders, values, sources, fanout,
                probes, visitBudget, experimentSeed);
            long accepted = 0;
            for (int round=0; round<40; round++)
            {
                List<Node> order = holders.OrderBy(h =>
                    MultiKey(experimentSeed + round * 7919, h.NodeID, 23)).ToList();
                for (int pos=0; pos<order.Count; pos++)
                    if (MultiLocalAction(order[pos], holderSet, fanout,
                        experimentSeed * 100000 + round * 101 + pos)) accepted++;
            }
            int afterLive;
            long afterEdges;
            SweetSpotValidateTopology(out afterLive, out afterEdges);
            int components, largest;
            SweetSpotComponents(out components, out largest);
            if (afterLive != live || afterEdges != baseEdges || components != 1 || largest != live)
                throw new InvalidOperationException("multi-holder structural invariant failed");
            double ulocal = MultiDiscovery(holders, values, sources, fanout,
                probes, visitBudget, experimentSeed);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using (StreamWriter w = new StreamWriter(outputPath))
            {
                w.WriteLine("seed,holders,fanout,probes,visitBudget,shamDiscovery," +
                    "ulocalDiscovery,difference,acceptedSwaps,baseEdges,terminalEdges," +
                    "components,largestComponent");
                w.WriteLine(experimentSeed + "," + holderCount + "," + fanout + "," +
                    probes + "," + visitBudget + "," + SweetSpotFormat(sham) + "," +
                    SweetSpotFormat(ulocal) + "," + SweetSpotFormat(ulocal-sham) + "," +
                    accepted + "," + baseEdges + "," + afterEdges + "," + components + "," + largest);
            }
        }

        private double MultiDiscovery(List<Node> holders, Dictionary<Node,int> values,
            List<Node> sources, int fanout, int probes, int budget, int seed)
        {
            double total = 0.0;
            for (int h=0; h<holders.Count; h++)
            {
                int successes = 0;
                for (int i=0; i<probes; i++)
                {
                    Random sourceRandom = new Random(SweetSpotStableSeed(seed, 9103+h, i));
                    Node source = sources[sourceRandom.Next(sources.Count)];
                    Random probeRandom = new Random(SweetSpotStableSeed(seed, 9127+h, i));
                    SweetSpotProbeResult result = SweetSpotBudgetSearch(
                        source, values[holders[h]], fanout, budget, probeRandom);
                    if (result.Success) successes++;
                }
                total += successes / (double)probes;
            }
            return total / holders.Count;
        }

        private bool MultiLocalAction(Node holder, HashSet<Node> holders, int fanout, int seed)
        {
            List<Node> incumbents = SweetSpotNeighbours(holder)
                .Where(n => n.Degree > minDegree).ToList();
            if (incumbents.Count == 0) return false;
            Node prune = incumbents.OrderBy(n => RelayUtility(fanout,n.Degree))
                .ThenBy(n => MultiKey(seed,n.NodeID,31)).First();
            List<Node> live = SweetSpotLiveNodes();
            Random rng = new Random(SweetSpotStableSeed(seed, 9151, 0));
            Node root = live[rng.Next(live.Count)];
            HashSet<Node> sample = new HashSet<Node>();
            sample.Add(root);
            List<Node> frontier = new List<Node>{root};
            for (int hop=0; hop<2; hop++)
            {
                List<Node> next = new List<Node>();
                foreach (Node u in frontier)
                    foreach (Node v in SweetSpotNeighbours(u))
                        if (sample.Add(v)) next.Add(v);
                frontier = next;
            }
            List<Node> candidates = sample.Where(n => n != holder && !holders.Contains(n) &&
                n.Degree < maxDegree && !doesLinkExist(n,holder.NodeID)).ToList();
            if (candidates.Count == 0) return false;
            Node graft = candidates.OrderByDescending(n => RelayUtility(fanout,n.Degree+1))
                .ThenBy(n => MultiKey(seed,n.NodeID,37)).First();
            if (RelayUtility(fanout,graft.Degree+1) <= RelayUtility(fanout,prune.Degree)+1e-12)
                return false;
            int holderBefore=holder.Degree;
            SweetSpotRemoveUndirectedEdge(holder,prune);
            SweetSpotAddUndirectedEdge(holder,graft);
            if (holder.Degree != holderBefore || prune.Degree < minDegree || graft.Degree > maxDegree)
                throw new InvalidOperationException("multi-holder swap invariant failed");
            return true;
        }

        private static uint MultiKey(int seed, int node, int role)
        {
            uint x=unchecked((uint)seed*2654435761u ^ (uint)node*2246822519u ^ (uint)role*3266489917u);
            x^=x>>16; x*=2246822519u; x^=x>>13; return x;
        }
    }
}
