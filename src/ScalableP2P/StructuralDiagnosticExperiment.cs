using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ScalableP2P
{
    partial class Graph
    {
        public void RunSweetSpotStructuralDiagnostics(string outputPath, int seed)
        {
            if (File.Exists(outputPath))
                throw new IOException("Refusing to overwrite result: " + outputPath);
            Node holder = getHolder(Trend != null ? Trend.TbpVal : 1000);
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException("Expected one saturated holder.");
            List<Node> baseNeighbours = SweetSpotNeighbours(holder);
            List<string> rows = new List<string>();
            foreach (int target in new int[] { 4, 8 })
            {
                try
                {
                    SweetSpotPlaceHolderAtExactDegree(holder, target, seed);
                    List<Node> selected = SweetSpotNeighbours(holder);
                    List<Node> live = SweetSpotLiveNodes();
                    List<Node> ranked = live.OrderByDescending(n => n.Degree)
                        .ThenBy(n => n.NodeID).ToList();
                    HashSet<Node> core = new HashSet<Node>(
                        ranked.Take(Math.Max(1, live.Count / 200)));
                    Dictionary<Node, int> distance = MultiSourceDistances(core);
                    double clustering = selected.Average(n => LocalClustering(n));
                    double meanNeighbourDegree = selected.Average(n =>
                        SweetSpotNeighbours(n).Average(v => (double)v.Degree));
                    double coreDistance = selected.Average(n => (double)distance[n]);
                    double coreNeighbourFraction = selected.Average(n =>
                    {
                        List<Node> neighbours = SweetSpotNeighbours(n);
                        return neighbours.Count == 0 ? 0.0 :
                            neighbours.Count(v => core.Contains(v)) / (double)neighbours.Count;
                    });
                    rows.Add(String.Join(",", new string[] {
                        seed.ToString(CultureInfo.InvariantCulture),
                        target.ToString(CultureInfo.InvariantCulture),
                        selected.Count.ToString(CultureInfo.InvariantCulture),
                        clustering.ToString("R", CultureInfo.InvariantCulture),
                        meanNeighbourDegree.ToString("R", CultureInfo.InvariantCulture),
                        coreDistance.ToString("R", CultureInfo.InvariantCulture),
                        coreNeighbourFraction.ToString("R", CultureInfo.InvariantCulture)
                    }));
                }
                finally
                {
                    SweetSpotRestoreHolder(holder, baseNeighbours);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using (StreamWriter writer = new StreamWriter(outputPath))
            {
                writer.WriteLine("seed,postDegree,n,clustering,meanNeighborDegree,distanceToTop0_5PctCore,coreNeighborFraction");
                foreach (string row in rows) writer.WriteLine(row);
            }
        }

        private Dictionary<Node, int> MultiSourceDistances(HashSet<Node> sources)
        {
            Dictionary<Node, int> distance = new Dictionary<Node, int>();
            Queue<Node> queue = new Queue<Node>();
            foreach (Node source in sources)
            {
                distance[source] = 0;
                queue.Enqueue(source);
            }
            while (queue.Count > 0)
            {
                Node current = queue.Dequeue();
                foreach (Node next in SweetSpotNeighbours(current))
                {
                    if (distance.ContainsKey(next)) continue;
                    distance[next] = distance[current] + 1;
                    queue.Enqueue(next);
                }
            }
            return distance;
        }

        private double LocalClustering(Node node)
        {
            List<Node> neighbours = SweetSpotNeighbours(node);
            int k = neighbours.Count;
            if (k < 2) return 0.0;
            int linkedPairs = 0;
            for (int i = 0; i < k; i++)
            {
                HashSet<Node> adjacent = new HashSet<Node>(SweetSpotNeighbours(neighbours[i]));
                for (int j = i + 1; j < k; j++)
                    if (adjacent.Contains(neighbours[j])) linkedPairs++;
            }
            return 2.0 * linkedPairs / (k * (k - 1.0));
        }
    }
}
