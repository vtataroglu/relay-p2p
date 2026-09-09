using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ScalableP2P
{
    partial class Graph
    {
        private const int Stage2PolicyUtilityLocal = 1;
        private const int Stage2PolicyUtilityGlobal = 2;
        private const int Stage2PolicyLegacy = 3;
        private const int Stage2PolicyRandom = 4;
        private const int Stage2PolicySham = 5;
        private const int Stage2PolicyMaxDegree = 6;
        private const int Stage2PolicyTargetBand = 7;
        private const int Stage2PolicyUtilityHysteresis = 8;

        public bool Stage2ExperimentEnabled = false;
        public bool Stage2WorkloadEnabled = false;
        private int Stage2PolicyMode = 0;
        private string Stage2PolicyName = "";
        private int Stage2ExperimentSeed = 0;
        private Random Stage2ChurnRandom = new Random(1);
        private Random Stage2RootRandom = new Random(2);
        private Random Stage2ChoiceRandom = new Random(3);
        private Random Stage2RepairRandom = new Random(4);
        private bool Stage2WindowActive = false;
        private bool Stage2PolicyActivated = false;
        private bool Stage2ActivatedAtWindowStart = false;
        private string Stage2WindowPhase = "";

        private long Stage2Opportunities = 0;
        private long Stage2AcceptedSwaps = 0;
        private long Stage2NoImprovement = 0;
        private long Stage2NoCandidate = 0;
        private long Stage2ShamNoops = 0;
        private long Stage2LocalSampleStarts = 0;
        private long Stage2SampleUniqueNodes = 0;
        private long Stage2AdjacencyScans = 0;
        private long Stage2LocalDegreeReads = 0;
        private long Stage2HolderDegreeReads = 0;
        private long Stage2GlobalDegreeReads = 0;
        private long Stage2UndirectedLinkOps = 0;
        private long Stage2DirectedLinkMutations = 0;
        private int Stage2TriggerWindows = 0;
        private int Stage2PolicyWindows = 0;
        private long Stage2CapRepairJoins = 0;
        private long Stage2CapRepairSampleNodes = 0;
        private long Stage2CapRepairDegreeReads = 0;
        private int Stage2FirstTriggerStep = -1;
        private int Stage2FirstCapStep = -1;
        private string Stage2PrePolicyHash = "";
        private string Stage2FirstTreatmentHash = "";
        private int Stage2FirstTreatmentStep = -1;
        private long Stage2FirstTreatmentEdges = -1;
        private int Stage2FirstTreatmentLiveNodes = -1;
        private bool Stage2TerminalPrepared = false;
        private double Stage2HysteresisEpsilon = 0.0;
        private long Stage2ExactTargetAvailable = 0;
        private readonly List<string> Stage2SwapRows = new List<string>();
        private long Stage2PrePolicyEdges = -1;
        private int Stage2PrePolicyLiveNodes = -1;
        private int Stage2MinLargestComponent = int.MaxValue;
        private int Stage2MaxComponents = 0;
        private readonly List<string> Stage2WindowRows = new List<string>();

        private sealed class Stage2ProbeResult
        {
            public bool Success;
            public int UniqueVisited;
            public int Messages;
        }

        public static void AssertUtilityPolicySelfTest()
        {
            AssertRelayUtilitySelfTest();

            // Under d_min=3, every eligible current or prospective post degree
            // is at least four. On that domain u_3 is strictly decreasing, so
            // utility selection is exactly the inherited max-prune/min-graft
            // degree ordering (apart from seeded tie breaking).
            for (int degree = 4; degree < 50; degree++)
                if (!(RelayUtility(3, degree) > RelayUtility(3, degree + 1)))
                    throw new InvalidOperationException(
                        "m=3 utility/legacy ordering mismatch at d=" + degree + ".");

            double optimum = RelayUtility(7, 8);
            for (int degree = 4; degree <= 50; degree++)
                if (degree != 8 && !(optimum > RelayUtility(7, degree)))
                    throw new InvalidOperationException(
                        "m=7 unique eligible optimum failed at d=" + degree + ".");
        }

        public void ConfigureUtilityPolicy(string policyName, int experimentSeed, int fanout)
        {
            if (Stage2ExperimentEnabled)
                throw new InvalidOperationException("Utility policy is already configured.");
            if (fanout <= 0) throw new ArgumentOutOfRangeException("fanout");

            string normalized = policyName.Trim().ToLowerInvariant();
            if (normalized == "ulocal") Stage2PolicyMode = Stage2PolicyUtilityLocal;
            else if (normalized == "gglobal") Stage2PolicyMode = Stage2PolicyUtilityGlobal;
            else if (normalized == "legacy") Stage2PolicyMode = Stage2PolicyLegacy;
            else if (normalized == "random") Stage2PolicyMode = Stage2PolicyRandom;
            else if (normalized == "sham") Stage2PolicyMode = Stage2PolicySham;
            else if (normalized == "maxdeg") Stage2PolicyMode = Stage2PolicyMaxDegree;
            else if (normalized == "targetband") Stage2PolicyMode = Stage2PolicyTargetBand;
            else if (normalized.StartsWith("ulocal-hyst-"))
            {
                Stage2PolicyMode = Stage2PolicyUtilityHysteresis;
                string value = normalized.Substring("ulocal-hyst-".Length);
                Stage2HysteresisEpsilon = double.Parse(
                    value, CultureInfo.InvariantCulture);
                if (Stage2HysteresisEpsilon <= 0)
                    throw new ArgumentOutOfRangeException("hysteresis epsilon");
            }
            else throw new ArgumentException("Unknown utility policy: " + policyName);

            AssertUtilityPolicySelfTest();
            Stage2ExperimentEnabled = true;
            Stage2WorkloadEnabled = false;
            Stage2PolicyName = normalized;
            Stage2ExperimentSeed = experimentSeed;
            ForwardFanout = fanout;
            Stage2ChurnRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 8209, 11));
            Stage2RootRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 8219, 13));
            Stage2ChoiceRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 8231, 17));
            Stage2RepairRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 8243, 31));
        }

        private void Stage2BeginWindow()
        {
            Stage2WindowActive = false;
            if (Trend == null) return;
            Node holder = getHolder(Trend.TbpVal);
            if (holder == null || !holder.Persistent) return;

            Stage2WindowActive = true;
            Stage2TriggerWindows++;
            Stage2ActivatedAtWindowStart = Stage2PolicyActivated;
            Stage2WindowPhase = Stage2PolicyActivated ? "policy" : "ramp";
            if (Stage2ActivatedAtWindowStart) Stage2PolicyWindows++;
            if (Stage2FirstTriggerStep < 0) Stage2FirstTriggerStep = simTime;

            int liveNodes;
            long edges;
            int components;
            int largest;
            Stage2ValidateWholeGraph(
                out liveNodes, out edges, out components, out largest);
        }

        private void Stage2EndWindow()
        {
            if (!Stage2WindowActive) return;

            int liveNodes;
            long edges;
            int components;
            int largest;
            Stage2ValidateWholeGraph(
                out liveNodes, out edges, out components, out largest);

            Node holder = Trend == null ? null : getHolder(Trend.TbpVal);
            Stage2WindowRows.Add(
                Stage2ExperimentSeed + "," + Stage2PolicyName + "," + simTime + "," +
                Stage2WindowPhase + "," + (holder == null ? -1 : holder.Degree) + "," +
                liveNodes + "," + edges + "," + components + "," + largest + "," +
                Stage2Opportunities + "," + Stage2AcceptedSwaps);
        }

        private int Stage2RunPolicyWindow(Node holder)
        {
            if (!Stage2PolicyActivated)
            {
                for (int joinIndex = 0;
                     joinIndex < RewireJoinsPerNode && holder.Degree < maxDegree;
                     joinIndex++)
                    join(holder);
                if (holder.Degree >= maxDegree)
                    Stage2ActivateAtCap(holder);
                return 0;
            }

            Stage2RepairHolderToCap(holder);
            if (String.IsNullOrEmpty(Stage2FirstTreatmentHash))
            {
                int liveNodes;
                long edges;
                SweetSpotValidateTopology(out liveNodes, out edges);
                Stage2FirstTreatmentStep = simTime;
                Stage2FirstTreatmentHash = SweetSpotTopologyHash();
                Stage2FirstTreatmentEdges = edges;
                Stage2FirstTreatmentLiveNodes = liveNodes;
            }

            int swaps = 0;
            for (int attempt = 0; attempt < RewireJoinsPerNode; attempt++)
                if (Stage2SwapOnce(holder)) swaps++;
            return swaps;
        }

        private void Stage2RepairHolderToCap(Node holder)
        {
            int guard = 0;
            while (holder != null && holder.Degree < maxDegree && guard++ < 1000)
            {
                if (Stage2RepairJoin(holder)) Stage2CapRepairJoins++;
            }
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException(
                    "utility policy holder cap repair failed.");
        }

        private bool Stage2RepairJoin(Node holder)
        {
            Node root = null;
            int guard = 0;
            while ((root == null || root == holder) && guard++ < 1000)
                root = search(Stage2RepairRandom.Next(0, N + 1));
            if (root == null || root == holder) return false;

            ArrayList sample = createSubGraph(root, tj);
            Stage2CapRepairSampleNodes += sample.Count;
            Node selected = null;
            double selectedKey = double.NegativeInfinity;
            for (int i = 0; i < sample.Count; i++)
            {
                Node candidate = (Node)sample[i];
                Stage2CapRepairDegreeReads++;
                if (candidate == holder || candidate.Degree >= maxDegree ||
                    doesLinkExist(candidate, holder.NodeID))
                    continue;

                double weight = weightOf(candidate);
                double key = weight <= 0
                    ? 0
                    : Math.Pow(Stage2RepairRandom.NextDouble(), 1.0 / weight);
                if (selected == null || key > selectedKey ||
                    (key == selectedKey &&
                     Stage2TieKey(candidate, 127) < Stage2TieKey(selected, 127)))
                {
                    selected = candidate;
                    selectedKey = key;
                }
            }
            if (selected == null) return false;
            SweetSpotAddUndirectedEdge(holder, selected);
            return true;
        }

        private void Stage2ActivateAtCap(Node holder)
        {
            if (Stage2PolicyActivated || holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException("Invalid utility policy activation.");

            int liveNodes;
            long edges;
            SweetSpotValidateTopology(out liveNodes, out edges);
            int components;
            int largest;
            SweetSpotComponents(out components, out largest);
            if (components != 1 || largest != liveNodes)
                throw new InvalidOperationException(
                    "utility policy common prefix disconnected before policy activation.");

            Stage2PolicyActivated = true;
            Stage2WindowPhase = "activation";
            Stage2FirstCapStep = simTime;
            Stage2PrePolicyHash = SweetSpotTopologyHash();
            Stage2PrePolicyEdges = edges;
            Stage2PrePolicyLiveNodes = liveNodes;
        }

        private void Stage2ValidateWholeGraph(
            out int liveNodes,
            out long edges,
            out int components,
            out int largest)
        {
            SweetSpotValidateTopology(out liveNodes, out edges);
            SweetSpotComponents(out components, out largest);
            Stage2MinLargestComponent = Math.Min(Stage2MinLargestComponent, largest);
            Stage2MaxComponents = Math.Max(Stage2MaxComponents, components);
            if (components != 1 || largest != liveNodes)
                throw new InvalidOperationException(
                    "utility policy connectivity audit failed at step " + simTime + ".");
        }

        private bool Stage2SwapOnce(Node holder)
        {
            if (!Stage2ExperimentEnabled)
                throw new InvalidOperationException("utility policy dispatch without configuration.");
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException("utility policy requires a saturated holder.");
            if (!Stage2PolicyActivated || !Stage2ActivatedAtWindowStart)
                throw new InvalidOperationException(
                    "Utility policy opportunity occurred outside a policy window.");

            Stage2Opportunities++;

            if (Stage2PolicyMode == Stage2PolicySham)
            {
                Stage2ShamNoops++;
                return false;
            }

            List<Node> pruneCandidates = new List<Node>();
            List<Node> holderNeighbours = SweetSpotNeighbours(holder);
            for (int i = 0; i < holderNeighbours.Count; i++)
            {
                Stage2HolderDegreeReads++;
                if (holderNeighbours[i].Degree > minDegree)
                    pruneCandidates.Add(holderNeighbours[i]);
            }
            if (pruneCandidates.Count == 0)
            {
                Stage2NoCandidate++;
                return false;
            }

            Node prune;
            Node graft;
            if (Stage2PolicyMode == Stage2PolicyUtilityGlobal)
            {
                prune = Stage2SelectUtilityPrune(pruneCandidates);
                graft = Stage2SelectGlobalUtilityGraft(holder);
                if (graft == null)
                {
                    Stage2NoCandidate++;
                    return false;
                }
                if (!(RelayUtility(ForwardFanout, graft.Degree + 1) >
                      RelayUtility(ForwardFanout, prune.Degree) + 1e-12))
                {
                    Stage2NoImprovement++;
                    return false;
                }
            }
            else
            {
                List<Node> sample = Stage2LocalSample();
                List<Node> graftCandidates = new List<Node>();
                for (int i = 0; i < sample.Count; i++)
                {
                    Node candidate = sample[i];
                    Stage2LocalDegreeReads++;
                    if (candidate != holder && candidate.Degree < maxDegree &&
                        !doesLinkExist(candidate, holder.NodeID))
                        graftCandidates.Add(candidate);
                }
                if (graftCandidates.Count == 0)
                {
                    Stage2NoCandidate++;
                    return false;
                }

                bool exactAvailable = false;
                for (int i = 0; i < graftCandidates.Count; i++)
                    if (graftCandidates[i].Degree + 1 == ForwardFanout + 1)
                    {
                        exactAvailable = true;
                        break;
                    }
                if (exactAvailable) Stage2ExactTargetAvailable++;

                if (Stage2PolicyMode == Stage2PolicyUtilityLocal ||
                    Stage2PolicyMode == Stage2PolicyUtilityHysteresis)
                {
                    prune = Stage2SelectUtilityPrune(pruneCandidates);
                    graft = Stage2SelectUtilityGraft(graftCandidates);
                    double improvement =
                        RelayUtility(ForwardFanout, graft.Degree + 1) -
                        RelayUtility(ForwardFanout, prune.Degree);
                    double threshold = Stage2PolicyMode == Stage2PolicyUtilityHysteresis
                        ? Stage2HysteresisEpsilon : 1e-12;
                    if (!(improvement > threshold))
                    {
                        Stage2NoImprovement++;
                        return false;
                    }
                }
                else if (Stage2PolicyMode == Stage2PolicyMaxDegree)
                {
                    prune = Stage2SelectLowestDegree(pruneCandidates, 131);
                    graft = Stage2SelectHighestDegree(graftCandidates, 137);
                    if (graft.Degree + 1 <= prune.Degree)
                    {
                        Stage2NoImprovement++;
                        return false;
                    }
                }
                else if (Stage2PolicyMode == Stage2PolicyTargetBand)
                {
                    prune = Stage2SelectWorstTargetBand(pruneCandidates, false);
                    graft = Stage2SelectBestTargetBand(graftCandidates, true);
                    if (!(Stage2TargetBandRank(graft.Degree + 1) >
                          Stage2TargetBandRank(prune.Degree)))
                    {
                        Stage2NoImprovement++;
                        return false;
                    }
                }
                else if (Stage2PolicyMode == Stage2PolicyLegacy)
                {
                    prune = Stage2SelectHighestDegree(pruneCandidates, 101);
                    graft = Stage2SelectLowestDegree(graftCandidates, 103);
                    if (graft.Degree >= prune.Degree)
                    {
                        Stage2NoImprovement++;
                        return false;
                    }
                }
                else if (Stage2PolicyMode == Stage2PolicyRandom)
                {
                    pruneCandidates.Sort(
                        delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
                    graftCandidates.Sort(
                        delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
                    prune = pruneCandidates[Stage2ChoiceRandom.Next(pruneCandidates.Count)];
                    graft = graftCandidates[Stage2ChoiceRandom.Next(graftCandidates.Count)];
                }
                else throw new InvalidOperationException("Invalid utility policy mode.");
            }

            Stage2ApplyDegreeNeutralSwap(holder, prune, graft);
            return true;
        }

        private double Stage2TargetBandRank(int postDegree)
        {
            int target = ForwardFanout + 1;
            if (postDegree <= target) return 1000000.0 + postDegree;
            return 1000000.0 - (postDegree - target);
        }

        private Node Stage2SelectBestTargetBand(List<Node> candidates, bool prospective)
        {
            Node best = null;
            double bestRank = double.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                int degree = candidates[i].Degree + (prospective ? 1 : 0);
                double rank = Stage2TargetBandRank(degree);
                if (best == null || rank > bestRank ||
                    (rank == bestRank && Stage2TieKey(candidates[i], 139) <
                     Stage2TieKey(best, 139)))
                {
                    best = candidates[i];
                    bestRank = rank;
                }
            }
            return best;
        }

        private Node Stage2SelectWorstTargetBand(List<Node> candidates, bool prospective)
        {
            Node worst = null;
            double worstRank = double.PositiveInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                int degree = candidates[i].Degree + (prospective ? 1 : 0);
                double rank = Stage2TargetBandRank(degree);
                if (worst == null || rank < worstRank ||
                    (rank == worstRank && Stage2TieKey(candidates[i], 149) <
                     Stage2TieKey(worst, 149)))
                {
                    worst = candidates[i];
                    worstRank = rank;
                }
            }
            return worst;
        }

        private List<Node> Stage2LocalSample()
        {
            Stage2LocalSampleStarts++;
            Node root = null;
            int guard = 0;
            while (root == null && guard++ < 1000)
                root = search(Stage2RootRandom.Next(0, N + 1));
            if (root == null)
                throw new InvalidOperationException("Could not draw a utility policy sample root.");

            List<Node> sample = new List<Node>();
            List<Node> frontier = new List<Node>();
            HashSet<Node> seen = new HashSet<Node>();
            sample.Add(root);
            frontier.Add(root);
            seen.Add(root);
            for (int hop = 0; hop < tj; hop++)
            {
                List<Node> next = new List<Node>();
                for (int i = 0; i < frontier.Count; i++)
                {
                    Link link = frontier[i].NextLink;
                    while (link != null)
                    {
                        Stage2AdjacencyScans++;
                        Node candidate = link.VertexLink;
                        if (candidate != null && seen.Add(candidate))
                        {
                            sample.Add(candidate);
                            next.Add(candidate);
                        }
                        link = link.NextLink;
                    }
                }
                frontier = next;
            }
            Stage2SampleUniqueNodes += sample.Count;
            return sample;
        }

        private Node Stage2SelectGlobalUtilityGraft(Node holder)
        {
            Node best = null;
            double bestUtility = double.NegativeInfinity;
            Node current = nodeHead;
            while (current != null)
            {
                Stage2GlobalDegreeReads++;
                if (current != holder && current.Degree < maxDegree &&
                    !doesLinkExist(current, holder.NodeID))
                {
                    double utility = RelayUtility(ForwardFanout, current.Degree + 1);
                    if (best == null || utility > bestUtility + 1e-12 ||
                        (Math.Abs(utility - bestUtility) <= 1e-12 &&
                         Stage2TieKey(current, 107) < Stage2TieKey(best, 107)))
                    {
                        best = current;
                        bestUtility = utility;
                    }
                }
                current = current.NextNode;
            }
            return best;
        }

        private Node Stage2SelectUtilityPrune(List<Node> candidates)
        {
            Node worst = null;
            double worstUtility = double.PositiveInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                Node candidate = candidates[i];
                double utility = RelayUtility(ForwardFanout, candidate.Degree);
                if (worst == null || utility < worstUtility - 1e-12 ||
                    (Math.Abs(utility - worstUtility) <= 1e-12 &&
                     Stage2TieKey(candidate, 109) < Stage2TieKey(worst, 109)))
                {
                    worst = candidate;
                    worstUtility = utility;
                }
            }
            return worst;
        }

        private Node Stage2SelectUtilityGraft(List<Node> candidates)
        {
            Node best = null;
            double bestUtility = double.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                Node candidate = candidates[i];
                double utility = RelayUtility(ForwardFanout, candidate.Degree + 1);
                if (best == null || utility > bestUtility + 1e-12 ||
                    (Math.Abs(utility - bestUtility) <= 1e-12 &&
                     Stage2TieKey(candidate, 113) < Stage2TieKey(best, 113)))
                {
                    best = candidate;
                    bestUtility = utility;
                }
            }
            return best;
        }

        private Node Stage2SelectHighestDegree(List<Node> candidates, int role)
        {
            Node best = null;
            for (int i = 0; i < candidates.Count; i++)
                if (best == null || candidates[i].Degree > best.Degree ||
                    (candidates[i].Degree == best.Degree &&
                     Stage2TieKey(candidates[i], role) < Stage2TieKey(best, role)))
                    best = candidates[i];
            return best;
        }

        private Node Stage2SelectLowestDegree(List<Node> candidates, int role)
        {
            Node best = null;
            for (int i = 0; i < candidates.Count; i++)
                if (best == null || candidates[i].Degree < best.Degree ||
                    (candidates[i].Degree == best.Degree &&
                     Stage2TieKey(candidates[i], role) < Stage2TieKey(best, role)))
                    best = candidates[i];
            return best;
        }

        private uint Stage2TieKey(Node node, int role)
        {
            uint value = unchecked(
                (uint)Stage2ExperimentSeed * 2654435761u ^
                (uint)node.NodeID * 2246822519u ^
                (uint)Stage2Opportunities * 3266489917u ^
                (uint)role * 668265263u);
            value ^= value >> 16;
            value *= 2246822519u;
            value ^= value >> 13;
            value *= 3266489917u;
            value ^= value >> 16;
            return value;
        }

        private void Stage2ApplyDegreeNeutralSwap(Node holder, Node prune, Node graft)
        {
            if (holder == null || prune == null || graft == null || prune == graft)
                throw new InvalidOperationException("Invalid utility policy swap endpoints.");
            if (!doesLinkExist(holder, prune.NodeID) ||
                !doesLinkExist(prune, holder.NodeID))
                throw new InvalidOperationException("utility policy prune edge is absent/asymmetric.");
            if (doesLinkExist(holder, graft.NodeID) ||
                doesLinkExist(graft, holder.NodeID))
                throw new InvalidOperationException("utility policy graft edge already exists/asymmetric.");
            if (prune.Degree <= minDegree || graft.Degree >= maxDegree)
                throw new InvalidOperationException("utility policy degree eligibility failed.");

            int holderBefore = holder.Degree;
            int pruneBefore = prune.Degree;
            int graftBefore = graft.Degree;
            double deltaUtility = RelayUtility(ForwardFanout, graftBefore + 1) -
                RelayUtility(ForwardFanout, pruneBefore);
            int localDegreeSum = holderBefore + pruneBefore + graftBefore;

            SweetSpotRemoveUndirectedEdge(holder, prune);
            SweetSpotAddUndirectedEdge(holder, graft);

            if (holder.Degree != holderBefore ||
                prune.Degree != pruneBefore - 1 ||
                graft.Degree != graftBefore + 1 ||
                holder.Degree + prune.Degree + graft.Degree != localDegreeSum ||
                doesLinkExist(holder, prune.NodeID) ||
                doesLinkExist(prune, holder.NodeID) ||
                !doesLinkExist(holder, graft.NodeID) ||
                !doesLinkExist(graft, holder.NodeID))
                throw new InvalidOperationException("utility policy local swap invariant failed.");
            Stage2AssertConnectedAfterSwap(holder, prune);

            Stage2AcceptedSwaps++;
            Stage2SwapRows.Add(
                Stage2ExperimentSeed + "," + Stage2PolicyName + "," + simTime + "," +
                Stage2Opportunities + "," + prune.NodeID + "," + pruneBefore + "," +
                graft.NodeID + "," + graftBefore + "," + (graftBefore + 1) + "," +
                SweetSpotFormat(deltaUtility));
            Stage2UndirectedLinkOps += 2;
            Stage2DirectedLinkMutations += 4;
        }

        private void Stage2AssertConnectedAfterSwap(Node holder, Node prunedNeighbour)
        {
            // The graph was fully validated at window entry (and after the
            // preceding swap). A degree-neutral swap removes exactly one edge
            // and adds one edge. Therefore proving the pruned endpoint still
            // reaches the holder is sufficient to prove that the accepted
            // swap did not split the previously connected graph.
            HashSet<Node> seen = new HashSet<Node>();
            Queue<Node> queue = new Queue<Node>();
            seen.Add(prunedNeighbour);
            queue.Enqueue(prunedNeighbour);
            while (queue.Count > 0)
            {
                Node current = queue.Dequeue();
                if (current == holder) return;
                Link link = current.NextLink;
                while (link != null)
                {
                    if (link.VertexLink != null && seen.Add(link.VertexLink))
                        queue.Enqueue(link.VertexLink);
                    link = link.NextLink;
                }
            }
            throw new InvalidOperationException(
                "utility policy accepted swap disconnected the graph at step " +
                simTime + ".");
        }

        public void PrepareUtilityPolicyTerminal()
        {
            if (!Stage2ExperimentEnabled || Stage2TerminalPrepared)
                throw new InvalidOperationException(
                    "Invalid utility policy terminal preparation state.");
            int targetValue = Trend == null ? 1000 : Trend.TbpVal;
            Node holder = getHolder(targetValue);
            if (holder == null)
                throw new InvalidOperationException("utility policy terminal holder is absent.");
            if (Stage2FirstCapStep < 0 || String.IsNullOrEmpty(Stage2PrePolicyHash) ||
                Stage2FirstTreatmentStep < 0 ||
                String.IsNullOrEmpty(Stage2FirstTreatmentHash))
                throw new InvalidOperationException(
                    "utility policy never reached the frozen policy regime.");
            Stage2RepairHolderToCap(holder);

            int liveNodes;
            long edges;
            int components;
            int largest;
            Stage2ValidateWholeGraph(
                out liveNodes, out edges, out components, out largest);
            Stage2TerminalPrepared = true;
        }

        public void RunUtilityPolicyTerminal(
            string summaryPath,
            int experimentSeed,
            int probes,
            int backgroundProbes,
            int visitBudget,
            int backgroundVisitBudget,
            int expectedLiveNodes)
        {
            if (!Stage2ExperimentEnabled)
                throw new InvalidOperationException("Utility policy is not configured.");
            if (!Stage2TerminalPrepared)
                throw new InvalidOperationException(
                    "utility policy terminal evaluator ran before cap preparation.");
            if (experimentSeed != Stage2ExperimentSeed)
                throw new InvalidOperationException("utility policy seed mismatch.");
            if (probes <= 0 || backgroundProbes <= 0 ||
                visitBudget <= 1 || backgroundVisitBudget <= 1)
                throw new ArgumentOutOfRangeException("utility policy terminal budget.");
            if (File.Exists(summaryPath))
                throw new IOException("Refusing to overwrite result: " + summaryPath);

            string directory = Path.GetDirectoryName(summaryPath) ?? ".";
            string stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(summaryPath));
            string targetRawPath = stem + "-target-probes.csv";
            string backgroundRawPath = stem + "-background-probes.csv";
            string neighbourPath = stem + "-neighbours.csv";
            string relayLoadPath = stem + "-relay-load.csv";
            string windowPath = stem + "-windows.csv";
            string swapPath = stem + "-swaps.csv";
            string[] outputs =
                { targetRawPath, backgroundRawPath, neighbourPath, relayLoadPath, windowPath,
                  swapPath };
            for (int i = 0; i < outputs.Length; i++)
                if (File.Exists(outputs[i]))
                    throw new IOException("Refusing to overwrite result: " + outputs[i]);
            Directory.CreateDirectory(directory);

            int liveNodes;
            long edges;
            int components;
            int largest;
            Stage2ValidateWholeGraph(
                out liveNodes, out edges, out components, out largest);
            if (liveNodes != expectedLiveNodes)
                throw new InvalidOperationException(
                    "utility policy live-node invariant failed: expected " + expectedLiveNodes +
                    ", observed " + liveNodes + ".");

            int targetValue = Trend == null ? 1000 : Trend.TbpVal;
            Node holder = getHolder(targetValue);
            if (holder == null || holder.Degree != maxDegree)
                throw new InvalidOperationException(
                    "utility policy terminal preparation did not preserve the holder cap.");

            string terminalHash = SweetSpotTopologyHash();
            List<Node> sourcePool = SweetSpotLiveNodes();
            sourcePool.Remove(holder);
            sourcePool.Sort(
                delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });

            List<Node> holderNeighbours = SweetSpotNeighbours(holder);
            holderNeighbours.Sort(
                delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });
            HashSet<int> holderNeighbourIds = new HashSet<int>();
            for (int i = 0; i < holderNeighbours.Count; i++)
                holderNeighbourIds.Add(holderNeighbours[i].NodeID);

            Dictionary<int, long> forwarded = new Dictionary<int, long>();
            Dictionary<int, long> neighbourVisits = new Dictionary<int, long>();
            Dictionary<int, long> holderDeliveries = new Dictionary<int, long>();
            List<string> targetRows = new List<string>(probes);
            Random sourceRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 8291, 19));
            int targetSuccess = 0;
            long targetVisited = 0;
            long targetMessages = 0;
            for (int i = 0; i < probes; i++)
            {
                Node source = sourcePool[sourceRandom.Next(sourcePool.Count)];
                Random probeRandom = new Random(
                    SweetSpotStableSeed(experimentSeed, 8303, i));
                Stage2ProbeResult result = Stage2BudgetSearch(
                    source, targetValue, ForwardFanout, visitBudget, probeRandom,
                    holder, holderNeighbourIds, forwarded, neighbourVisits,
                    holderDeliveries);
                if (result.Success) targetSuccess++;
                targetVisited += result.UniqueVisited;
                targetMessages += result.Messages;
                targetRows.Add(
                    experimentSeed + "," + Stage2PolicyName + "," + i + "," +
                    source.NodeID + "," + (result.Success ? 1 : 0) + "," +
                    result.UniqueVisited + "," + result.Messages);
            }

            List<int> backgroundTargets = new List<int>(backgroundProbes);
            for (int i = 0; i < backgroundProbes; i++)
                backgroundTargets.Add((i % 100) + 1);
            Random backgroundOrder = new Random(
                SweetSpotStableSeed(experimentSeed, 8311, 23));
            for (int i = backgroundTargets.Count - 1; i > 0; i--)
            {
                int j = backgroundOrder.Next(i + 1);
                int temp = backgroundTargets[i];
                backgroundTargets[i] = backgroundTargets[j];
                backgroundTargets[j] = temp;
            }
            Random backgroundSourceRandom = new Random(
                SweetSpotStableSeed(experimentSeed, 8317, 29));
            List<string> backgroundRows = new List<string>(backgroundProbes);
            int backgroundSuccess = 0;
            long backgroundVisited = 0;
            long backgroundMessages = 0;
            for (int i = 0; i < backgroundProbes; i++)
            {
                Node source = sourcePool[backgroundSourceRandom.Next(sourcePool.Count)];
                int value = backgroundTargets[i];
                Random probeRandom = new Random(
                    SweetSpotStableSeed(experimentSeed, 8329 + value, i));
                Stage2ProbeResult result = Stage2BudgetSearch(
                    source, value, ForwardFanout, backgroundVisitBudget, probeRandom,
                    null, null, null, null, null);
                if (result.Success) backgroundSuccess++;
                backgroundVisited += result.UniqueVisited;
                backgroundMessages += result.Messages;
                backgroundRows.Add(
                    experimentSeed + "," + Stage2PolicyName + "," + i + "," +
                    source.NodeID + "," + value + "," +
                    (result.Success ? 1 : 0) + "," + result.UniqueVisited + "," +
                    result.Messages);
            }

            if (SweetSpotTopologyHash() != terminalHash)
                throw new InvalidOperationException("utility policy terminal probes mutated topology.");

            double utilitySum = 0;
            long neighbourDegreeSum = 0;
            int minNeighbourDegree = int.MaxValue;
            int maxNeighbourDegree = int.MinValue;
            int degreeEightCount = 0;
            for (int i = 0; i < holderNeighbours.Count; i++)
            {
                int degree = holderNeighbours[i].Degree;
                utilitySum += RelayUtility(ForwardFanout, degree);
                neighbourDegreeSum += degree;
                minNeighbourDegree = Math.Min(minNeighbourDegree, degree);
                maxNeighbourDegree = Math.Max(maxNeighbourDegree, degree);
                if (degree == ForwardFanout + 1) degreeEightCount++;
            }

            List<long> relayLoads = new List<long>(sourcePool.Count);
            for (int i = 0; i < sourcePool.Count; i++)
            {
                long count;
                forwarded.TryGetValue(sourcePool[i].NodeID, out count);
                relayLoads.Add(count);
            }
            relayLoads.Sort();
            double loadP50 = Stage2Quantile(relayLoads, 0.50) / (double)probes;
            double loadP95 = Stage2Quantile(relayLoads, 0.95) / (double)probes;
            double loadP99 = Stage2Quantile(relayLoads, 0.99) / (double)probes;
            double loadMax = relayLoads[relayLoads.Count - 1] / (double)probes;

            string targetTemp = targetRawPath + ".partial";
            string backgroundTemp = backgroundRawPath + ".partial";
            string neighbourTemp = neighbourPath + ".partial";
            string relayLoadTemp = relayLoadPath + ".partial";
            string windowTemp = windowPath + ".partial";
            string summaryTemp = summaryPath + ".partial";
            string swapTemp = swapPath + ".partial";
            string[] partials =
                {
                    targetTemp, backgroundTemp, neighbourTemp, relayLoadTemp,
                    windowTemp, swapTemp, summaryTemp
                };
            for (int i = 0; i < partials.Length; i++)
                if (File.Exists(partials[i]))
                    throw new IOException("Partial output already exists: " + partials[i]);

            using (StreamWriter writer = Stage2CreateNewWriter(targetTemp))
            {
                writer.WriteLine(
                    "seed,policy,probeIndex,sourceId,success,uniqueVisited,messages");
                for (int i = 0; i < targetRows.Count; i++) writer.WriteLine(targetRows[i]);
            }
            using (StreamWriter writer = Stage2CreateNewWriter(backgroundTemp))
            {
                writer.WriteLine(
                    "seed,policy,probeIndex,sourceId,targetValue,success," +
                    "uniqueVisited,messages");
                for (int i = 0; i < backgroundRows.Count; i++)
                    writer.WriteLine(backgroundRows[i]);
            }
            using (StreamWriter writer = Stage2CreateNewWriter(neighbourTemp))
            {
                writer.WriteLine(
                    "seed,policy,nodeId,degree,utility,visits,forwardedMessages," +
                    "holderDeliveries");
                for (int i = 0; i < holderNeighbours.Count; i++)
                {
                    Node neighbour = holderNeighbours[i];
                    long visits;
                    long sent;
                    long deliveries;
                    neighbourVisits.TryGetValue(neighbour.NodeID, out visits);
                    forwarded.TryGetValue(neighbour.NodeID, out sent);
                    holderDeliveries.TryGetValue(neighbour.NodeID, out deliveries);
                    writer.WriteLine(
                        experimentSeed + "," + Stage2PolicyName + "," +
                        neighbour.NodeID + "," + neighbour.Degree + "," +
                        SweetSpotFormat(RelayUtility(ForwardFanout, neighbour.Degree)) +
                        "," + visits + "," + sent + "," + deliveries);
                }
            }
            using (StreamWriter writer = Stage2CreateNewWriter(relayLoadTemp))
            {
                writer.WriteLine("seed,policy,nodeId,forwardedMessages,messagesPerProbe");
                for (int i = 0; i < sourcePool.Count; i++)
                {
                    long sent;
                    forwarded.TryGetValue(sourcePool[i].NodeID, out sent);
                    writer.WriteLine(
                        experimentSeed + "," + Stage2PolicyName + "," +
                        sourcePool[i].NodeID + "," + sent + "," +
                        SweetSpotFormat(sent / (double)probes));
                }
            }
            using (StreamWriter writer = Stage2CreateNewWriter(windowTemp))
            {
                writer.WriteLine(
                    "seed,policy,step,phase,holderDegree,liveNodes,edgeCount," +
                    "components,largestComponent,cumulativeOpportunities," +
                    "cumulativeAcceptedSwaps");
                for (int i = 0; i < Stage2WindowRows.Count; i++)
                    writer.WriteLine(Stage2WindowRows[i]);
            }
            using (StreamWriter writer = Stage2CreateNewWriter(swapTemp))
            {
                writer.WriteLine(
                    "seed,policy,step,opportunity,pruneNodeId,pruneDegree," +
                    "graftNodeId,graftPreDegree,graftPostDegree,deltaUtility");
                for (int i = 0; i < Stage2SwapRows.Count; i++)
                    writer.WriteLine(Stage2SwapRows[i]);
            }
            using (StreamWriter writer = Stage2CreateNewWriter(summaryTemp))
            {
                writer.WriteLine(
                    "seed,policy,fanout,probes,successes,pHat,visitBudget," +
                    "meanVisited,meanMessages,backgroundProbes,backgroundSuccesses," +
                    "backgroundPHat,backgroundVisitBudget,backgroundMeanVisited," +
                    "backgroundMeanMessages," +
                    "holderNodeId,holderDegree,neighbourUtilitySum,meanNeighbourDegree," +
                    "minNeighbourDegree,maxNeighbourDegree,optimumDegreeCount," +
                    "relayLoadP50,relayLoadP95,relayLoadP99,relayLoadMax," +
                    "opportunities,acceptedSwaps,noImprovement,noCandidate,shamNoops," +
                    "exactTargetAvailable,hysteresisEpsilon," +
                    "localSampleStarts,sampleUniqueNodes,adjacencyScans," +
                    "localDegreeReads,holderDegreeReads,globalDegreeReads," +
                    "undirectedLinkOps,directedLinkMutations,triggerWindows," +
                    "policyWindows,capRepairJoins,capRepairSampleNodes," +
                    "capRepairDegreeReads,firstTriggerStep,firstCapStep," +
                    "firstTreatmentStep,prePolicyLiveNodes,prePolicyEdges," +
                    "firstTreatmentLiveNodes,firstTreatmentEdges," +
                    "terminalLiveNodes,terminalEdges,terminalComponents,terminalLargest," +
                    "minLargestComponent,maxComponents,prePolicyHash," +
                    "firstTreatmentHash,terminalHash");
                writer.WriteLine(
                    experimentSeed + "," + Stage2PolicyName + "," + ForwardFanout + "," +
                    probes + "," + targetSuccess + "," +
                    SweetSpotFormat(targetSuccess / (double)probes) + "," +
                    visitBudget + "," +
                    SweetSpotFormat(targetVisited / (double)probes) + "," +
                    SweetSpotFormat(targetMessages / (double)probes) + "," +
                    backgroundProbes + "," + backgroundSuccess + "," +
                    SweetSpotFormat(backgroundSuccess / (double)backgroundProbes) + "," +
                    backgroundVisitBudget + "," +
                    SweetSpotFormat(backgroundVisited / (double)backgroundProbes) + "," +
                    SweetSpotFormat(backgroundMessages / (double)backgroundProbes) + "," +
                    holder.NodeID + "," + holder.Degree + "," +
                    SweetSpotFormat(utilitySum) + "," +
                    SweetSpotFormat(neighbourDegreeSum / (double)holderNeighbours.Count) + "," +
                    minNeighbourDegree + "," + maxNeighbourDegree + "," +
                    degreeEightCount + "," + SweetSpotFormat(loadP50) + "," +
                    SweetSpotFormat(loadP95) + "," + SweetSpotFormat(loadP99) + "," +
                    SweetSpotFormat(loadMax) + "," + Stage2Opportunities + "," +
                    Stage2AcceptedSwaps + "," + Stage2NoImprovement + "," +
                    Stage2NoCandidate + "," + Stage2ShamNoops + "," +
                    Stage2ExactTargetAvailable + "," +
                    SweetSpotFormat(Stage2HysteresisEpsilon) + "," +
                    Stage2LocalSampleStarts + "," + Stage2SampleUniqueNodes + "," +
                    Stage2AdjacencyScans + "," + Stage2LocalDegreeReads + "," +
                    Stage2HolderDegreeReads + "," + Stage2GlobalDegreeReads + "," +
                    Stage2UndirectedLinkOps + "," + Stage2DirectedLinkMutations + "," +
                    Stage2TriggerWindows + "," + Stage2PolicyWindows + "," +
                    Stage2CapRepairJoins + "," + Stage2CapRepairSampleNodes + "," +
                    Stage2CapRepairDegreeReads + "," + Stage2FirstTriggerStep + "," +
                    Stage2FirstCapStep + "," + Stage2FirstTreatmentStep + "," +
                    Stage2PrePolicyLiveNodes + "," + Stage2PrePolicyEdges + "," +
                    Stage2FirstTreatmentLiveNodes + "," + Stage2FirstTreatmentEdges + "," +
                    liveNodes + "," + edges + "," + components + "," + largest + "," +
                    Stage2MinLargestComponent + "," + Stage2MaxComponents + "," +
                    Stage2PrePolicyHash + "," + Stage2FirstTreatmentHash + "," +
                    terminalHash);
            }

            File.Move(targetTemp, targetRawPath);
            File.Move(backgroundTemp, backgroundRawPath);
            File.Move(neighbourTemp, neighbourPath);
            File.Move(relayLoadTemp, relayLoadPath);
            File.Move(windowTemp, windowPath);
            File.Move(swapTemp, swapPath);
            // Compact summary is the completion marker and is moved last.
            File.Move(summaryTemp, summaryPath);
        }

        private Stage2ProbeResult Stage2BudgetSearch(
            Node begin,
            int targetValue,
            int fanout,
            int visitBudget,
            Random probeRandom,
            Node holder,
            HashSet<int> holderNeighbourIds,
            Dictionary<int, long> forwarded,
            Dictionary<int, long> neighbourVisits,
            Dictionary<int, long> holderDeliveries)
        {
            HashSet<Node> visited = new HashSet<Node>();
            List<Node> frontierNodes = new List<Node>();
            List<Node> frontierParents = new List<Node>();
            visited.Add(begin);
            if (holderNeighbourIds != null && holderNeighbourIds.Contains(begin.NodeID))
                Stage2Increment(neighbourVisits, begin.NodeID);
            if (begin.Item.Values.Contains(targetValue))
                return new Stage2ProbeResult
                    { Success = true, UniqueVisited = 1, Messages = 0 };
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
                    neighbours.Sort(
                        delegate(Node a, Node b) { return a.NodeID.CompareTo(b.NodeID); });

                    int forward = Math.Min(fanout, neighbours.Count);
                    for (int k = 0; k < forward && visited.Count < visitBudget; k++)
                    {
                        int pick = probeRandom.Next(k, neighbours.Count);
                        Node temp = neighbours[pick];
                        neighbours[pick] = neighbours[k];
                        neighbours[k] = temp;
                        Node candidate = neighbours[k];
                        messages++;
                        if (forwarded != null) Stage2Increment(forwarded, current.NodeID);
                        if (visited.Contains(candidate)) continue;
                        visited.Add(candidate);
                        if (holderNeighbourIds != null &&
                            holderNeighbourIds.Contains(candidate.NodeID))
                            Stage2Increment(neighbourVisits, candidate.NodeID);
                        if (candidate.Item.Values.Contains(targetValue))
                        {
                            if (holder != null && candidate == holder &&
                                holderDeliveries != null)
                                Stage2Increment(holderDeliveries, current.NodeID);
                            return new Stage2ProbeResult
                            {
                                Success = true,
                                UniqueVisited = visited.Count,
                                Messages = messages
                            };
                        }
                        nextNodes.Add(candidate);
                        nextParents.Add(current);
                    }
                }
                frontierNodes = nextNodes;
                frontierParents = nextParents;
            }

            return new Stage2ProbeResult
            {
                Success = false,
                UniqueVisited = visited.Count,
                Messages = messages
            };
        }

        private static void Stage2Increment(Dictionary<int, long> counts, int id)
        {
            long value;
            counts.TryGetValue(id, out value);
            counts[id] = value + 1;
        }

        private static long Stage2Quantile(List<long> sorted, double probability)
        {
            if (sorted == null || sorted.Count == 0)
                throw new ArgumentException("Empty utility policy quantile sample.");
            int index = (int)Math.Ceiling(probability * sorted.Count) - 1;
            index = Math.Max(0, Math.Min(sorted.Count - 1, index));
            return sorted[index];
        }

        private static StreamWriter Stage2CreateNewWriter(string path)
        {
            return new StreamWriter(
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        }
    }
}
