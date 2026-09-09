using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScalableP2P
{
    partial class Graph
    {
        ArrayList removed = new ArrayList();
        // growth steps at which TBP contents enter, and their placement
        // state; a single element array in the single content default
        public int[] TbpEntrySteps = new int[] { 2000 };
        bool[] tbpPlaced = null;
        // single replica TBP holders are protected from churn during measurement
        HashSet<int> protectedIds = new HashSet<int>();
        // if >0 the TBP holder is disconnected at this simulation step and
        // the content reappears on the next joining node
        public int HolderCrashStep = 0;
        // control baselines: each window grants the same join budget to
        // this many random nodes (isolates the importance of placement)
        public int RandomRewireNodes = 0;
        // Oracle detector used to isolate detection delay.
        public bool OracleDetector = false;
        // A positive value replaces self calibration with an absolute threshold.
        public double FixedThreshold = 0;
        // High water mark for the level rule.
        public double HotShare = 0.10;
        public double Pp = 0;
        public double Pd = 1;
        public double Pt = 0;                  // trend weight for preferential attachment
        public bool UniformLocalAttachment = false;
        public bool RewireEnabled = false;     // proactive rewiring after a trend trigger
        public TrendModel Trend = null;        // time varying query workload
        public System.IO.StreamWriter MetricsWriter = null;
        public int QueryEvery = 5;             // simulation steps between workload queries
        public int WorkloadTtl = 8;
        public int ProbeTtl = 12;
        public int WindowSize = 200;
        public int RuleMask = 7;               // bit 0 k sigma, bit 1 CUSUM, bit 2 level
        // Self calibrated decision limit: D >= max(SigmaK * sigmaD, ShareFloor).
        public double SigmaK = 5.0;
        public double ShareFloor = 0.02;
        // Bernoulli CUSUM threshold in excess hit units.
        public double CusumTheta = 6.0;
        // Number of recent windows required to suppress transient fluctuations.
        public int PersistWindows = 2;
        // Maximum local joins initiated by an eligible node in one window.
        public int RewireJoinsPerNode = 2;
        int lastWindowRewireNodes = 0;
        int lastWindowRewireJoins = 0;
        // Background targets use a fixed stream across configurations.
        Zipf bgZipf = new Zipf(0.3, 100);
        // Optional reseeding for the background control.
        public void reseedBackgroundProbes(int seed) { bgZipf = new Zipf(0.3, 100, 30011 + seed); }
        int simTime = 0;
        int cnt;
        int target;
        int minDegree;
        // Search fanout is a forwarding-policy parameter, not a topology-growth
        // parameter. Historically both happened to equal d_min=3. Keeping a
        // separate field lets the relay-sweet-spot experiment vary fanout
        // without changing the generated graph.
        public int ForwardFanout = 3;
        // Hard degree cutoff. The default value of 10,000 effectively disables it.
        // CutoffAppliesToJoiner enables the cutoff for the joining peer as well.
        public int maxDegree = 10000 ;
        public bool CutoffAppliesToJoiner = false;
        // Diagnostic placement mode: 0 off, 1 highest degree eligible peers,
        // 2 lowest degree eligible peers. It applies only to the target holder.
        public int OraclePlacement = 0;
        // Degree neutral swap: prune one neighbor and graft one eligible peer
        // while holding holder degree fixed.
        public bool SwapEnabled = false;
        int lastWindowSwaps = 0;
        int N = -1;
        int tj = 2;
        int tl = 2;
        double u = 300;
        Node nodeHead;
        //Random rnd = new Random(1000003);
        //Random rnd = new Random(10007);

          Random rnd = new Random();

        public Graph(int degree)
        {
            minDegree = degree;
            ForwardFanout = degree;
        }

        public Graph(int degree, int seed)
        {
            minDegree = degree;
            ForwardFanout = degree;
            rnd = new Random(seed);
        }

        public Node createNode(int index)
        {
            return new Node(index);
        }

        public Link createLink(int index)
        {
            Link temp = new Link(index);
            temp.VertexLink = search(index);

            return temp;
        }
        public void initialGraph()
        {
            
            nodeHead = createNode(0);
            Node n1 = createNode(1);
            Node n2 = createNode(2);
            Node n3 = createNode(3);
            Node n4 = createNode(4);
            Node n5 = createNode(5);
            Node n6 = createNode(6);
            Node n7 = createNode(7);
            Node n8 = createNode(8);
            Node n9 = createNode(9);

            nodeHead.NextNode = n1;
            n1.NextNode = n2;
            n2.NextNode = n3;
            n3.NextNode = n4;
            n4.NextNode = n5;
            n5.NextNode = n6;
            n6.NextNode = n7;
            n7.NextNode = n8;
            n8.NextNode = n9;

            nodeHead.NextLink = createLink(1); nodeHead.Degree++;
            nodeHead.NextLink.NextLink = createLink(2); nodeHead.Degree++;
            nodeHead.NextLink.NextLink.NextLink = createLink(3); nodeHead.Degree++;
            n1.NextLink = createLink(0); n1.Degree++;
            n1.NextLink.NextLink = createLink(2); n1.Degree++;
            n1.NextLink.NextLink.NextLink = createLink(3); n1.Degree++;
            n1.NextLink.NextLink.NextLink.NextLink = createLink(9); n1.Degree++;
            
            n2.NextLink = createLink(1); n2.Degree++;
            n2.NextLink.NextLink = createLink(0); n2.Degree++;
            n2.NextLink.NextLink.NextLink = createLink(6); n2.Degree++;
            n3.NextLink = createLink(0); n3.Degree++;
            n3.NextLink.NextLink = createLink(4); n3.Degree++;
            n3.NextLink.NextLink.NextLink = createLink(1); n3.Degree++;
            n4.NextLink = createLink(3); n4.Degree++;
            n4.NextLink.NextLink = createLink(5); n4.Degree++;
            n4.NextLink.NextLink.NextLink = createLink(8); n4.Degree++;
            n5.NextLink = createLink(4); n5.Degree++;
            n5.NextLink.NextLink = createLink(8); n5.Degree++;
            n5.NextLink.NextLink.NextLink = createLink(9); n5.Degree++;
            n6.NextLink = createLink(2); n6.Degree++;
            n6.NextLink.NextLink = createLink(7); n6.Degree++;
            n6.NextLink.NextLink.NextLink = createLink(8); n6.Degree++;
            n7.NextLink = createLink(6); n7.Degree++;
            n7.NextLink.NextLink = createLink(9); n7.Degree++;
            n7.NextLink.NextLink.NextLink = createLink(8); n7.Degree++;

            n8.NextLink = createLink(6); n8.Degree++;
            n8.NextLink.NextLink = createLink(4); n8.Degree++;
            n8.NextLink.NextLink.NextLink = createLink(5); n8.Degree++;
            n8.NextLink.NextLink.NextLink.NextLink = createLink(7); n8.Degree++;

            n9.NextLink = createLink(7); n9.Degree++;
            n9.NextLink.NextLink = createLink(5); n9.Degree++;
            n9.NextLink.NextLink.NextLink = createLink(1); n9.Degree++;
            
            N = 9;//already have 10 nodes


            //ArrayList sub = createSubGraph(nodeHead, 4);
            //for (int i = 0; i < sub.Count; i++)
            //{
            //    Console.WriteLine(((Node)sub[i]).NodeID);
            //} 
                
            
        }
        public void grow(int nTarget)
        {
            target = nTarget + 10;
            for (int i = 0; i < nTarget+1; i++)
            {
                //if (i==2005|| i == 3000 || i == 4000 || i == 5000 || i == 6000 || i == 7000 || i == 8000 || i == 9000 || i == 10000)
                //    Console.WriteLine(i + "  " + searchForVal(1000));
                //if ((i + 200) % 200 == 0)
                //    Console.WriteLine(i);    
                addNode(i);
                
                // utility policy uses a policy-independent churn-event stream so local
                // sampling cannot shift the later exogenous join/leave schedule.
                // The default path remains byte-identical for every legacy mode.
                Random churnRnd = Stage2ExperimentEnabled ? Stage2ChurnRandom : rnd;
                double num = churnRnd.Next(1, 1000);
               
                if (num < u)
                {
                    Node nodeDelete;
                    int id;
                    do
                    {
                        id = churnRnd.Next(0, N);
                        nodeDelete = search(id);
                         
                    } while (nodeDelete == null || protectedIds.Contains(id));
                    
                    removed.Add(id);
                    leaveNode(nodeDelete, tl);
                    cnt++;
                    i--;
                }

                simTime++;
                // controlled holder crash: the node leaves with all of its
                // links; the content is reborn as one copy on the next joiner
                if (HolderCrashStep > 0 && simTime == HolderCrashStep && Trend != null)
                {
                    Node holder = getHolder(Trend.TbpVal);
                    if (holder != null)
                    {
                        protectedIds.Remove(holder.NodeID);
                        removed.Add(holder.NodeID);
                        leaveNode(holder, tl);
                        cnt++;
                        TbpEntrySteps[0] = i + 1;
                        tbpPlaced[0] = false;
                    }
                }
                if (Trend != null && (!Stage2ExperimentEnabled || Stage2WorkloadEnabled) &&
                    simTime % QueryEvery == 0)
                {
                    int dummy = 0;
                    nfSearch(getRndNode(), Trend.getQueryValue(simTime), WorkloadTtl, true, ref dummy);
                }
                if (simTime % WindowSize == 0)
                {
                    updateTrends();
                    if (RewireEnabled) rewireTrending();
                    if (RandomRewireNodes > 0) rewireRandom();
                    if (MetricsWriter != null) logMetrics();
                }
            }
            
            Console.WriteLine("\n Growing has been completed\n*********************************************************************\n");

        }

        public void addNode(int i)
        {
            N++;
            if (nodeHead == null)
            {
                nodeHead = createNode(N);
                nodeHead.addItemsRandomly();
            }else
            {
                Node n=null;
                int lastId = N - 1;
                do
                {
                    n = search(lastId);
                    if (n == null)
                        lastId = lastId - 1;
                } while (n == null);
                //Console.WriteLine("here"+N);
                n.NextNode = createNode(N);
                if (tbpPlaced == null) tbpPlaced = new bool[TbpEntrySteps.Length];
                int slot = -1;
                for (int k = 0; k < TbpEntrySteps.Length; k++)
                    if (i == TbpEntrySteps[k] && !tbpPlaced[k]) { slot = k; break; }
                if (slot >= 0)
                {
                    int val = Trend != null ? Trend.TbpVals[slot] : 1000;
                    n.NextNode.addItem(val);
                    // TBP scenario: demand has not formed when the content
                    // enters, so static popularity stays low; demand rises
                    // later through the Trend model
                    n.NextNode.Item.Popularity = 1;
                    protectedIds.Add(n.NextNode.NodeID);
                    tbpPlaced[slot] = true;
                }
                else
                    n.NextNode.addItemsRandomly();
                join();
            }
            

        }

        public void join()
        {
            int numOfLinks = 0;
            ArrayList randomNodes = new ArrayList();
            int attempts = 0;
            while (numOfLinks < minDegree)
            {
                // safety guard: at a low kc this loop would never end once
                // no eligible candidate remains. It never triggers in normal
                // operation; if it does, it is reported.
                if (++attempts > 1000)
                {
                    Console.WriteLine("WARN join(): giving up after 1000 attempts, links=" + numOfLinks + " kc=" + maxDegree);
                    break;
                }
                // pick a random node from the network and take its neighborhood subgraph to connect into
                Node n;
                do
                {
                    n = search(rnd.Next(0, N));
                    if (!randomNodes.Contains(n))
                        randomNodes.Add(n);
                    else
                        continue;
                } while (n == null);
                
                //Node n = search(4);
                //Console.WriteLine("random"+n.NodeID);
               
                ArrayList nodes = createSubGraph(n,tj);
                Node current = search(N);
                numOfLinks += preferentialAttachment(current, nodes, 0, null, numOfLinks);
            }
        }
        public void join(Node current)
        {
            int numOfLinks = current.Degree;
            int currrentdegree = numOfLinks;
            ArrayList randomNodes = new ArrayList();
            int counter = 0;
            while (numOfLinks == currrentdegree && counter<3)
            {
                // pick a random node from the network and take its neighborhood subgraph to connect into
                counter++;
                Node n;
                do
                {

                    n = search(rnd.Next(0, N));
                    //if(n!=null) Console.WriteLine("here" + n.NodeID);
                    // Console.WriteLine("N..:"+N);
                    //if (n == null)
                    //    Console.WriteLine("null");
                    //else
                    //    Console.WriteLine("id.." + n.NodeID);
                    if (!randomNodes.Contains(n))
                        randomNodes.Add(n);
                    else
                        continue;
                } while (n == null || n.NodeID==current.NodeID);

                //Node n = search(4);
                //Console.WriteLine("random"+n.NodeID);

                ArrayList nodes = createSubGraph(n, tj);
                //Console.WriteLine("nodes.count.."+nodes.Count);
                int nlinks=preferentialAttachment(current, nodes, 1, null, numOfLinks);
                //Console.WriteLine("linkss.."+nlinks+ "   "+ nodes.Count);
                numOfLinks += nlinks;
            }
        }

        public ArrayList createSubGraph(Node n, int hop)
        {
            ArrayList nodes = new ArrayList();
            // buyuk alt-graflarda O(k^2)'ye donusen ArrayList.Contains yerine HashSet
            HashSet<Node> seen = new HashSet<Node>();
            ArrayList newNodes = new ArrayList();
            nodes.Add(n);
            seen.Add(n);
            newNodes.Add(n);
            for (int i = 0; i < hop; i++)
            {
                ArrayList newlyVisited = new ArrayList();
                for (int j = 0; j < newNodes.Count; j++)
                {
                    Link iteratorLink = ((Node)newNodes[j]).NextLink;
                    while (iteratorLink != null)
                    {
                        Node temp = iteratorLink.VertexLink;
                        if (!seen.Contains(temp))
                        {
                            seen.Add(temp);
                            nodes.Add(temp);
                            newlyVisited.Add(temp);
                        }
                        iteratorLink = iteratorLink.NextLink;
                    }
                }
                newNodes = newlyVisited;
            }
            return nodes;
        }
        
        public int preferentialAttachment(Node joiningNode, ArrayList subGraph, int limit, ArrayList immediateNeighbours, int totalNumberOfLinks)
        {
            int links = 0;
            int loopCount;
            //Node joiningNode = search(id);
            if (joiningNode == null)
                return 0;
            //subGraph = sort(subGraph);
            //Console.WriteLine("before PA "+ subGraph.Count);
            subGraph = sortWithPA(subGraph, Pp, Pd);
            //Console.WriteLine("after PA "+subGraph.Count);
            //for (int i = 0; i < subGraph.Count; i++)
            //{
            //    Console.WriteLine(((Node)subGraph[i]).NodeID + "  " + ((Node)subGraph[i]).Item.Popularity);
            //}
            //if (limit == 0 || subGraph.Count<limit)
             loopCount = subGraph.Count;
            //if (immediateNeighbours != null)
            //{
            //    while (subGraph.Count>0 && ((Node)subGraph[0]).Degree == maxDegree)
            //    {
            //        subGraph.RemoveAt(0);
            //    }
            //}
            if (subGraph.Count == 0) return 0;

            for (int i = 0; i < loopCount; i++)
            {
                Node item = (Node)subGraph[i];
                //Console.WriteLine(item.Degree + "  " + item.NodeID + "  " + joiningNode.NodeID);
                //if (((limit == 1 && doesLinkExist(item, joiningNode.NodeID)) || ((limit==1) && item.NodeID == joiningNode.NodeID)) && subGraph.Count > 1)
                //    do
                //    {
                //        item = (Node)subGraph[i + 1];
                //        i++;
                //    } while (i<subGraph.Count-1 &&  doesLinkExist(item, joiningNode.NodeID));
 
                //Console.Write(item.Degree + "  "+ item.NodeID+ "  "+joiningNode.NodeID);
                //Console.WriteLine(doesLinkExist(item, joiningNode.NodeID));
                //if (item.Degree > target)
                //{
                //    Console.Write(item.Degree + "  ");
                //    Link iterator = ((Node)subGraph[i]).NextLink;
                //    while (iterator != null)
                //    {
                //        Console.Write(iterator.Id+" ");
                //        iterator = iterator.NextLink;
                //    }
                //    Environment.Exit(0);
                //}
                //Console.WriteLine("before if");
                if (CutoffAppliesToJoiner && joiningNode.Degree >= maxDegree)
                    return links;
                if (item.Degree < maxDegree && item.NodeID != joiningNode.NodeID && !doesLinkExist(item, joiningNode.NodeID))
                {
                    //Console.WriteLine("inside");
                    if (joiningNode.NextLink == null)
                        joiningNode.NextLink = createLink(item.NodeID);
                    else
                        findLastLink(joiningNode.NextLink).NextLink = createLink(item.NodeID);  // link from the added node into the subgraph
                    joiningNode.Degree++;

                    Link lastOne=findLastLink(item.NextLink);
                    if(lastOne!=null) // link from the subgraph back to the added node
                        lastOne.NextLink = createLink(joiningNode.NodeID);
                    else
                        item.NextLink = createLink(joiningNode.NodeID);
                    //used to prevent extra links when a node leaves 
                    if (immediateNeighbours != null)
                    {
                        immediateNeighbours.Remove(item);
                        //subGraph.Remove(item);
                    }
                    
                    item.Degree++;
                    links++;
                    if ((totalNumberOfLinks + links) >= minDegree)
                        return links;
                    if (limit == 1) return links;
                }
            }
            return links;
        }

        public void leaveNode(Node nodeDelete, int tl)
        {
            // the subgraph formed by the neighbors of the node being removed
            
            if (nodeDelete == null)
                return;
            ArrayList subGraph = createSubGraph(nodeDelete,tl);
            ArrayList immediateNeighbours = createSubGraph(nodeDelete, 1);
            foreach (Node item in immediateNeighbours)
            {
                 
                Link iteratorLink = item.NextLink;
                Link prevIterator = item.NextLink;
                while (iteratorLink != null)
                {
                   
                    // links from the subgraph to the removed node are torn down
                    if (iteratorLink.Id == nodeDelete.NodeID)
                    {
                        if (iteratorLink == prevIterator)
                            item.NextLink = iteratorLink.NextLink;
                        else
                            prevIterator.NextLink = iteratorLink.NextLink;
                        item.Degree--;
                        break;
                    }
                    prevIterator = iteratorLink;
                    iteratorLink = iteratorLink.NextLink;
                }
            }

            // agdan nodeDelete siliniyor
            if (nodeDelete == nodeHead)
                nodeHead = nodeHead.NextNode;
            else
            {
                Node iterator = nodeHead;
                Node prevNode = nodeHead;
                while (iterator != null)
                {
                    if (iterator == nodeDelete)
                    {
                        prevNode.NextNode = iterator.NextNode;
                        break;
                    }
                    prevNode = iterator;
                    iterator = iterator.NextNode;
                }
            }
             
            subGraph.Remove(nodeDelete);//remove the deleted node from subgraph
            immediateNeighbours.Remove(nodeDelete);
            ArrayList originalSubGraph = copyList(subGraph);
            //Console.WriteLine("....................................");
            //display();
            //Console.WriteLine("....................................");
            //Console.WriteLine("after removing six");
            //for (int i = 0; i < originalSubGraph.Count; i++)
            //{
            //    Console.WriteLine(((Node)originalSubGraph[i]).NodeID + "  " + ((Node)originalSubGraph[i]).Item.Popularity);
            //}
            //Console.WriteLine("immediate neighbours");
            //for (int i = 0; i < immediateNeighbours.Count; i++)
            //{
            //    Console.WriteLine(((Node)immediateNeighbours[i]).NodeID + "  " + ((Node)immediateNeighbours[i]).Item.Popularity);
            //}
            for (int i = 0; i < immediateNeighbours.Count; i++)
            {

                Node current = (Node)immediateNeighbours[i];
                Link iterator = current.NextLink;
                while (iterator != null)
                {
                     
                    subGraph.Remove(iterator.VertexLink);
                    iterator = iterator.NextLink;
                }
                subGraph.Remove(current);
                //if (subGraph.Count==0)
                //    Console.WriteLine("000000000000000000  "+ immediateNeighbours.Count+ "  "+ originalSubGraph.Count);
                //Console.WriteLine("calling for "+ current.NodeID);
                if (subGraph.Count == 0)
                {
                    //Console.WriteLine("00000000000000000000000000000000");
                    //int currentDegree = current.Degree;
                    //do
                    //{
                        
                        join(current);
                        
                    //} while (current.Degree == currentDegree);
                }
                else
                {

                        //int currentDegree = current.Degree;
                        //ArrayList copy = copyList(subGraph);
                        //Console.WriteLine("before "+subGraph.Count);
                        preferentialAttachment(current, subGraph, 1, immediateNeighbours, current.Degree);
                        //Console.WriteLine("after  " + subGraph.Count);
                        //if (currentDegree == current.Degree)
                        //{
                        //    Console.WriteLine("sameeeee" + ((Node)subGraph[0]).Degree + "  " + immediateNeighbours.Count + "  " + current.Degree +
                        //        doesLinkExist(current, ((Node)subGraph[0]).NodeID));
                        //}
                        //int counter = 0;
                        //while (currentDegree == current.Degree && counter<10)
                        //{
                        //    join(current);
                        //    counter++;
                        //}
                }
                //Console.WriteLine("....................................");
                //display();
                //Console.WriteLine("....................................");

                subGraph = copyList(originalSubGraph);
                

            }
            for (int i = 0; i < immediateNeighbours.Count; i++)
            {
                if (((Node)immediateNeighbours[i]).Degree < minDegree)
                {
                    Node current=(Node)immediateNeighbours[i];
                    //int currentDegree = current.Degree;
                    join(current);
                   
                }
            }
            
        }

       
        public void display()
        {
            Node iterator = nodeHead;
            if (nodeHead == null)
                Console.WriteLine("Empty Grapf");
            else
            {
                while (iterator != null)
                {
                    Link iteratorLink = iterator.NextLink;
                    //if (iteratorLink != null)
                    //{
                        Console.Write("[Node:" + iterator.NodeID +" "+iterator.Degree+ "](");
                        foreach (var item in iterator.Item.Values)
                        {
                            Console.Write(item + ",");
                        }
                        Console.Write(")\n");
                    //}
                    while (iteratorLink != null)
                    {
                        Node x = iteratorLink.VertexLink;
                        //Console.Write(x.NodeItem + "-");
                        iteratorLink = iteratorLink.NextLink;
                        Console.Write("[index:" + x.NodeID + "](");
                        for (int i = 0; i < x.Item.Values.Count; i++)
                        {
                            Console.Write(x.Item.Values[i] + ",");
                        }
                        Console.Write(")\n");
                    }
                    Console.WriteLine("\n--------------------------------------");
                    iterator = iterator.NextNode;
                }
            }
            Console.WriteLine("removed  "+cnt);
            for (int i = 0; i < removed.Count; i++)
            {
                Console.WriteLine(removed[i]);
            }
        }

        public Node search(int index)
        {
            Node iterator = nodeHead;
            while (iterator != null && iterator.NodeID.CompareTo(index) != 0)
            {
                iterator = iterator.NextNode;
            }
            return iterator;
        }
        public int searchForVal(int val)
        {
            Node iterator = nodeHead;
            while (!iterator.Item.Values.Contains(val))
            {
                iterator = iterator.NextNode;
            }
            return iterator.Degree;
        }

        public bool doesLinkExist(Node current, int id)
        {
            Link iterator = current.NextLink;
            while (iterator != null)
            {
                if (iterator.Id == id)
                    return true;
                iterator = iterator.NextLink;
            }
            return false;

        }
        public void addItemToNode(int index, object item)
        {
            Node n = search(index);
            n.addItem(item);
            //Console.WriteLine("popularity"+ n.NodeID + "  "+n.Item.Popularity);
        }

        // returns the most recent link this node created
        public Link findLastLink(Link iteratorLink)
        {
            if (iteratorLink != null)
            {
                while (iteratorLink.NextLink != null)
                    iteratorLink = iteratorLink.NextLink;
            }
            return iteratorLink;
        }

        public ArrayList getAll()
        {
            ArrayList all = new ArrayList();
            Node iterator = nodeHead;
            while(iterator!=null)
            {
                all.Add(iterator);
                iterator = iterator.NextNode;
            }
            return all;
        }
        public void display(ArrayList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Console.Write(((Node)list[i]).Item.Popularity+ "  ");
            }
        }
        public ArrayList sort(ArrayList graph, double Pp, double Pd)
        {
            Node temp;
            if (graph.Count == 1) return graph;
            for (int i = 0; i < graph.Count; i++)
            {
                for (int j = 0; j < graph.Count - i - 1; j++)
                {
                    if (weightOf((Node)graph[j]) < weightOf((Node)graph[j + 1]))
                    {
                        temp = (Node)graph[j];
                        graph[j] = graph[j + 1];
                        graph[j + 1] = temp;
                    }
                }
            }
            return graph;
        }
        public ArrayList sortWithPA(ArrayList graph, double Pp, double Pd)
        {
            if (graph.Count <= 1) return graph;
            // Efraimidis-Spirakis weighted shuffle: sequential roulette
            // draws (weight proportional, without replacement) give the same
            // distribution in O(k log k); the old O(k^2) roulette took hours
            // on the growing subgraphs of hub heavy networks
            List<KeyValuePair<double, Node>> keyed = new List<KeyValuePair<double, Node>>();
            for (int i = 0; i < graph.Count; i++)
            {
                Node n = (Node)graph[i];
                double w = AttachmentSelectionWeight(n);
                double key = w <= 0 ? 0 : Math.Pow(rnd.NextDouble(), 1.0 / w);
                keyed.Add(new KeyValuePair<double, Node>(key, n));
            }
            keyed.Sort(delegate(KeyValuePair<double, Node> a, KeyValuePair<double, Node> b)
            {
                return b.Key.CompareTo(a.Key);
            });
            ArrayList sorted = new ArrayList();
            for (int i = 0; i < keyed.Count; i++)
            {
                sorted.Add(keyed[i].Value);
            }
            return sorted;
        }
        public ArrayList sortWithPA2(ArrayList graph, double Pp, double Pd)
        {
            double selected;
            ArrayList sorted = new ArrayList();
            graph = sort(graph, Pp, Pd);
            double totalPop = 0;
            double prev = 0;
            for (int i = 0; i < graph.Count; i++)
            {
                totalPop += ((Node)graph[i]).Item.Popularity  ;
            }
            do
            {

                selected = rnd.Next(0, (int)(totalPop + 1));
                prev = 0;
                for (int i = 0; i < graph.Count; i++)
                {
                    if (selected <= prev + ((Node)graph[i]).Item.Popularity  )
                    {
                        sorted.Add((Node)graph[i]);
                        totalPop -= (((Node)graph[i]).Item.Popularity  );
                        graph.RemoveAt(i);
                        break;
                    }
                    prev += ((Node)graph[i]).Item.Popularity  ;
                }
            } while (graph.Count > 1);

            //Console.WriteLine("here");
            //if(graph.Count>0) sorted.Add((Node)graph[0]);
            //for (int i = 0; i < sorted.Count; i++)
            //{
            //    Console.Write(((Node)sorted[i]).Degree);
            //}
            //Console.WriteLine();

            return sorted;
        }
        public bool randomWalk(int val, int ttl)
        {
            int id;
            Node begin;
            bool flag = false;
            do
            {
                id = rnd.Next(0, N - 1);
                //id = 7;
                Console.WriteLine("id for random walk...:" + id);
                begin = search(id);
            } while (begin == null);
            Node previous = begin;
            Link iterator;
            int randomNeighbour;
            for (int j = 0; j < ttl; j++)
            {
                int counter=0;
                do
                {
                    counter++;
                    randomNeighbour = rnd.Next(0, begin.Degree);
                    iterator = begin.NextLink;
                    for (int i = 0; i < randomNeighbour; i++)
                    {
                        iterator = iterator.NextLink;
                    }
                } while (iterator.Id == previous.NodeID && counter<maxDegree);
                Node temp = iterator.VertexLink;
                if (temp == null)
                    return false;
                if (temp.Item.Values.Contains(val))
                    flag = true;
                previous = begin;
                begin = iterator.VertexLink;
            }
            
            return flag;
        }

        public bool flooding(int val, int ttl)
        {
            int nodeCount = 1;
            bool flag = false;
            ArrayList neighbours = new ArrayList();
            ArrayList newNeighbours = new ArrayList();
            ArrayList visited = new ArrayList();
            //int id = 9;//rnd.Next(0, N - 1);
            int id;
            Node begin;
            do
            {
                id = rnd.Next(0, N - 1);
                //id = 7;
                Console.WriteLine("id for  flooding...:" + id);
                begin = search(id);
            } while (begin == null);
            if (begin.Item.Values.Contains(val))
                flag = true;
            //Node previous = begin;
            Link iterator;
            neighbours.Add(begin);
            newNeighbours.Add(begin);
            newNeighbours.Add(new Node(-1));
            //newNeighbours.Add(new Node(-2));
            //newNeighbours.Add(new Node(-3));
            
            //visited.Add(begin);
            for (int i = 0; i < ttl; i++)
            {
                neighbours = copyList(newNeighbours);
                
                //for (int l = 0; l < neighbours.Count; l++)
                //{
                //    Console.WriteLine(((Node)neighbours[l]).NodeID);
                //}
                newNeighbours.Clear();
                for (int j = 0; j < neighbours.Count; j=j+2)
                {
                    begin = (Node)neighbours[j];

                    iterator = begin.NextLink;
                    while (iterator != null)
                    {
                        if (iterator.Id != ((Node)neighbours[j+1]).NodeID)
                        {
                            nodeCount++;
                            Node temp = iterator.VertexLink;
                            if (temp.Item.Values.Contains(val))
                                flag = true;
                            if (!newNeighbours.Contains(temp))
                            {
                                newNeighbours.Add(temp);
                                newNeighbours.Add(begin);
                                //visited.Add(temp);
                            }
                        }
                        iterator = iterator.NextLink;
                        
                    }
                    //for (int k = 0; k < newNeighbours.Count; k++)
                    //{
                    //    Console.WriteLine("newneighbours of " + ((Node)neighbours[j]).NodeID + "  " + ((Node)newNeighbours[k]).NodeID);
                    //}
                    //Console.WriteLine("done");
                      
                }
                //previous = (Node)visited[i];
   
            }//for times ttl
            Console.WriteLine(nodeCount);
            return flag;
        }

        public Node getRndNode()
        {
            int id;
            Node begin;
            do
            {
                id = rnd.Next(0, N - 1);
                //id = 7;
                //Console.WriteLine("id for normalized flooding...:" + id);
                begin = search(id);
            } while (begin == null);
            return begin;
        }

        // the trend term in the attachment weight uses only trends that
        // passed the persistence filter (statistically significant), so
        // noise cannot distort the popularity based attachment structure
        public double weightOf(Node n)
        {
            double trendTerm = n.Persistent ? n.TrendScore : 0;
            return n.Item.Popularity * Pp + n.Degree * Pd + trendTerm * Pt;
        }

        private double AttachmentSelectionWeight(Node n)
        {
            return UniformLocalAttachment ? 1.0 : weightOf(n);
        }

        // the same spreading rule as normalized flooding (each node forwards
        // to at most ForwardFanout random neighbors, parent excluded); a
        // HashSet based efficient form for measuring large networks.
        // observe=true lets visited nodes record the query for trend estimation.
        public int nfSearch(Node begin, int val, int ttl, bool observe, ref int visitedCount)
        {
            int hits = 0;
            HashSet<Node> visited = new HashSet<Node>();
            List<Node[]> frontier = new List<Node[]>();
            visited.Add(begin);
            if (observe) begin.observeQuery(val);
            if (begin.Item.Values.Contains(val)) hits++;
            frontier.Add(new Node[] { begin, null });
            for (int h = 0; h < ttl && frontier.Count > 0; h++)
            {
                List<Node[]> next = new List<Node[]>();
                for (int f = 0; f < frontier.Count; f++)
                {
                    Node current = frontier[f][0];
                    Node parent = frontier[f][1];
                    List<Node> neighbours = new List<Node>();
                    Link iterator = current.NextLink;
                    while (iterator != null)
                    {
                        if (iterator.VertexLink != null && (parent == null || iterator.Id != parent.NodeID))
                            neighbours.Add(iterator.VertexLink);
                        iterator = iterator.NextLink;
                    }
                    int forward = Math.Min(ForwardFanout, neighbours.Count);
                    for (int k = 0; k < forward; k++)
                    {
                        int pick = rnd.Next(k, neighbours.Count);
                        Node swap = neighbours[pick]; neighbours[pick] = neighbours[k]; neighbours[k] = swap;
                        Node candidate = neighbours[k];
                        if (visited.Contains(candidate))
                            continue;
                        visited.Add(candidate);
                        if (observe) candidate.observeQuery(val);
                        if (candidate.Item.Values.Contains(val)) hits++;
                        next.Add(new Node[] { candidate, current });
                    }
                }
                frontier = next;
            }
            visitedCount += visited.Count;
            return hits;
        }

        public Node getHolder(int val)
        {
            Node iterator = nodeHead;
            while (iterator != null)
            {
                if (iterator.Item.Values.Contains(val))
                    return iterator;
                iterator = iterator.NextNode;
            }
            return null;
        }

        private void updateTrends()
        {
            Node iterator = nodeHead;
            while (iterator != null)
            {
                iterator.updateTrendWindow(SigmaK, ShareFloor, PersistWindows, CusumTheta, FixedThreshold, HotShare, RuleMask);
                iterator = iterator.NextNode;
            }
            // oracle: the holder is held in trend mode from the ramp start on
            if (OracleDetector && Trend != null && simTime >= Trend.TStarts[0])
            {
                Node holder = getHolder(Trend.TbpVal);
                if (holder != null) holder.forceTrending();
            }
        }

        // control baseline: the same join budget goes to random nodes
        private void rewireRandom()
        {
            lastWindowRewireNodes = 0;
            lastWindowRewireJoins = 0;
            for (int i = 0; i < RandomRewireNodes; i++)
            {
                Node candidate = getRndNode();
                int before = candidate.Degree;
                for (int j = 0; j < RewireJoinsPerNode; j++)
                    join(candidate);
                lastWindowRewireNodes++;
                lastWindowRewireJoins += candidate.Degree - before;
            }
        }

        // nodes that pass the persistence filter build extra links through
        // the existing join mechanism and move toward a more central
        // position. The rule is entirely local: each node consults only its
        // own trend state; there is no global ranking or selection. How many
        // nodes make how many joins per window is measured and written to CSV.
        private void rewireTrending()
        {
            lastWindowRewireNodes = 0;
            lastWindowRewireJoins = 0;
            lastWindowSwaps = 0;
            if (Stage2ExperimentEnabled) Stage2BeginWindow();
            ArrayList trending = new ArrayList();
            Node iterator = nodeHead;
            while (iterator != null)
            {
                if (iterator.Persistent)
                    trending.Add(iterator);
                iterator = iterator.NextNode;
            }
            Node tbpHolder = (OraclePlacement > 0 && Trend != null) ? getHolder(Trend.TbpVal) : null;
            for (int i = 0; i < trending.Count; i++)
            {
                Node candidate = (Node)trending[i];
                int before = candidate.Degree;
                if (Stage2ExperimentEnabled)
                {
                    Node stage2Holder = Trend == null ? null : getHolder(Trend.TbpVal);
                    if (trending.Count != 1 || candidate != stage2Holder)
                        throw new InvalidOperationException(
                            "utility policy expects exactly the protected TBP holder to be active.");
                    lastWindowSwaps += Stage2RunPolicyWindow(candidate);
                    lastWindowRewireNodes++;
                    lastWindowRewireJoins += candidate.Degree - before;
                    continue;
                }
                if (candidate == tbpHolder) { oraclePlace(candidate); lastWindowRewireNodes++; continue; }
                for (int j = 0; j < RewireJoinsPerNode; j++)
                {
                    // mechanism unity: an actuator at the degree cap makes a
                    // degree neutral swap instead of wasting join attempts.
                    // One code path both repairs placement and clears windup.
                    if (SwapEnabled && candidate.Degree >= maxDegree)
                    { if (swapOnce(candidate)) lastWindowSwaps++; }
                    else join(candidate);
                }
                lastWindowRewireNodes++;
                lastWindowRewireJoins += candidate.Degree - before;
            }
            if (Stage2ExperimentEnabled) Stage2EndWindow();
        }

        // diagnostic arm: removes ALL of the holder's links and reattaches
        // them to the maxDegree eligible nodes of highest (mode 1) or lowest
        // (mode 2) degree. Degree stays fixed at exactly maxDegree; only the
        // placement changes. This deliberately steps outside the protocol,
        // solely to answer causally whether placement at fixed degree matters.
        private void oraclePlace(Node h)
        {
            // 1) tear down the existing links (both directions)
            Link it = h.NextLink;
            while (it != null)
            {
                Node nb = it.VertexLink;
                if (nb != null)
                {
                    Link p = nb.NextLink, prev = null;
                    while (p != null)
                    {
                        if (p.Id == h.NodeID)
                        {
                            if (prev == null) nb.NextLink = p.NextLink;
                            else prev.NextLink = p.NextLink;
                            nb.Degree--;
                            break;
                        }
                        prev = p; p = p.NextLink;
                    }
                }
                it = it.NextLink;
            }
            h.NextLink = null;
            h.Degree = 0;

            // 2) collect eligible candidates (those meeting the cutoff, excluding the holder)
            List<Node> cand = new List<Node>();
            Node w = nodeHead;
            while (w != null)
            {
                if (w != h && w.Degree < maxDegree) cand.Add(w);
                w = w.NextNode;
            }
            List<Node> chosen;
            if (OraclePlacement == 3)
            {
                // Diagnostic arm that greedily maximizes the union of one hop
                // candidate neighborhoods without using degree in the score.
                // The candidate pool is sampled to bound computational cost.
                const int POOL = 1000;
                List<Node> pool = new List<Node>();
                if (cand.Count <= POOL) pool.AddRange(cand);
                else for (int i = 0; i < POOL; i++) pool.Add(cand[rnd.Next(cand.Count)]);
                List<HashSet<int>> basin = new List<HashSet<int>>();
                for (int i = 0; i < pool.Count; i++)
                {
                    HashSet<int> b = new HashSet<int>();
                    Link L = pool[i].NextLink;
                    while (L != null) { if (L.VertexLink != null) b.Add(L.Id); L = L.NextLink; }
                    b.Add(pool[i].NodeID);
                    basin.Add(b);
                }
                HashSet<int> cov = new HashSet<int>();
                bool[] used = new bool[pool.Count];
                chosen = new List<Node>();
                int want = Math.Min(maxDegree, pool.Count);
                for (int pick = 0; pick < want; pick++)
                {
                    int best = -1, bestGain = -1;
                    for (int i = 0; i < pool.Count; i++)
                    {
                        if (used[i]) continue;
                        int gain = 0;
                        foreach (int id in basin[i]) if (!cov.Contains(id)) gain++;
                        if (gain > bestGain) { bestGain = gain; best = i; }
                    }
                    if (best < 0) break;
                    used[best] = true; chosen.Add(pool[best]);
                    foreach (int id in basin[best]) cov.Add(id);
                }
            }
            else if (OraclePlacement == 4)
            {
                // Dynamic low degree diagnostic. Random tie breaking refreshes
                // the low degree membership in every window. Only this arm
                // consumes the additional random stream.
                Dictionary<Node, int> key = new Dictionary<Node, int>();
                for (int i = 0; i < cand.Count; i++) key[cand[i]] = rnd.Next();
                cand.Sort(delegate(Node a, Node b)
                {
                    int c = a.Degree.CompareTo(b.Degree);
                    return c != 0 ? c : key[a].CompareTo(key[b]);
                });
                chosen = cand;
            }
            else
            {
                if (OraclePlacement == 1) cand.Sort(delegate(Node a, Node b) { return b.Degree.CompareTo(a.Degree); });
                else                      cand.Sort(delegate(Node a, Node b) { return a.Degree.CompareTo(b.Degree); });
                chosen = cand;
            }

            // 3) tam maxDegree tanesine bagla
            int take = Math.Min(maxDegree, chosen.Count);
            for (int i = 0; i < take; i++)
            {
                Node t = chosen[i];
                if (h.NextLink == null) h.NextLink = createLink(t.NodeID);
                else findLastLink(h.NextLink).NextLink = createLink(t.NodeID);
                h.Degree++;
                Link last = findLastLink(t.NextLink);
                if (last != null) last.NextLink = createLink(h.NodeID);
                else t.NextLink = createLink(h.NodeID);
                t.Degree++;
            }
        }

        // One degree neutral swap. Protocol native: the candidate sample is
        // the join's own t_j-hop sample, and candidate degrees are already
        // reported there, so no extra message is needed. Without a strict
        // improvement nothing happens (prevents needless link churn). A prune
        // that would push a neighbor below minDegree is forbidden. Returns
        // true when a swap took place.
        private bool swapOnce(Node h)
        {
            // 1) PRUNE candidate: the highest degree neighbor (above minDegree)
            Node worst = null; Link it = h.NextLink;
            while (it != null)
            {
                Node nb = it.VertexLink;
                if (nb != null && nb.Degree > minDegree && (worst == null || nb.Degree > worst.Degree))
                    worst = nb;
                it = it.NextLink;
            }
            if (worst == null) return false;

            // 2) GRAFT candidate: the lowest degree eligible node in the join's own sample
            Node n = null;
            int guard = 0;
            while ((n == null || n.NodeID == h.NodeID) && guard++ < 20) n = search(rnd.Next(0, N));
            if (n == null) return false;
            ArrayList sample = createSubGraph(n, tj);
            Node best = null;
            for (int i = 0; i < sample.Count; i++)
            {
                Node c = (Node)sample[i];
                if (c.NodeID == h.NodeID || c.Degree >= maxDegree) continue;
                if (doesLinkExist(c, h.NodeID)) continue;
                if (best == null || c.Degree < best.Degree) best = c;
            }
            if (best == null) return false;

            // 3) yalniz kesin iyilesme varsa uygula
            if (best.Degree >= worst.Degree) return false;

            // 3a) tear down the worst <-> h link (both directions)
            Link p = h.NextLink, prev = null;
            while (p != null)
            {
                if (p.Id == worst.NodeID)
                {
                    if (prev == null) h.NextLink = p.NextLink; else prev.NextLink = p.NextLink;
                    h.Degree--; break;
                }
                prev = p; p = p.NextLink;
            }
            p = worst.NextLink; prev = null;
            while (p != null)
            {
                if (p.Id == h.NodeID)
                {
                    if (prev == null) worst.NextLink = p.NextLink; else prev.NextLink = p.NextLink;
                    worst.Degree--; break;
                }
                prev = p; p = p.NextLink;
            }
            // 3b) create the best <-> h link
            if (h.NextLink == null) h.NextLink = createLink(best.NodeID);
            else findLastLink(h.NextLink).NextLink = createLink(best.NodeID);
            h.Degree++;
            Link last = findLastLink(best.NextLink);
            if (last != null) last.NextLink = createLink(h.NodeID);
            else best.NextLink = createLink(h.NodeID);
            best.Degree++;
            return true;
        }

        // total edge count (degree sum / 2); for equal cost comparison
        public long countEdges()
        {
            long total = 0;
            Node iterator = nodeHead;
            while (iterator != null)
            {
                total += iterator.Degree;
                iterator = iterator.NextNode;
            }
            return total / 2;
        }

        // degree histogram of the final topology (for the CCDF figure)
        public void writeDegreeHistogram(string path)
        {
            Dictionary<int, int> hist = new Dictionary<int, int>();
            Node iterator = nodeHead;
            while (iterator != null)
            {
                if (!hist.ContainsKey(iterator.Degree)) hist[iterator.Degree] = 0;
                hist[iterator.Degree]++;
                iterator = iterator.NextNode;
            }
            List<int> keys = new List<int>(hist.Keys);
            keys.Sort();
            using (System.IO.StreamWriter w = new System.IO.StreamWriter(path))
            {
                w.WriteLine("degree,count");
                for (int i = 0; i < keys.Count; i++)
                    w.WriteLine(keys[i] + "," + hist[keys[i]]);
            }
        }

        private void logMetrics()
        {
            int tbpVal = Trend != null ? Trend.TbpVal : 1000;
            Node holder = getHolder(tbpVal);
            int probes = 20;
            // TTL=12 walks half the network and saturates the metric, so
            // the unsaturated lower TTLs are measured as well; the success
            // difference can only be interpreted that way
            int[] ttls = new int[] { 4, 6, 8, 12 };
            int[] success = new int[ttls.Length];
            int[] visitedTotal = new int[ttls.Length];
            for (int t = 0; t < ttls.Length; t++)
            {
                for (int k = 0; k < probes; k++)
                {
                    int visitedCount = 0;
                    if (nfSearch(getRndNode(), tbpVal, ttls[t], false, ref visitedCount) > 0)
                        success[t]++;
                    visitedTotal[t] += visitedCount;
                }
            }
            // Background probes use ordinary Zipf targets at TTL 8.
            int bgProbes = 20;
            int bgSuccess = 0;
            int bgHits = 0;
            int bgVisited = 0;
            for (int k = 0; k < bgProbes; k++)
            {
                // Match the workload distribution so popular items are queried more often.
                int bgVal = 101 - (int)bgZipf.getZipfValue(0.3, 100);
                int visitedCount = 0;
                int h = nfSearch(getRndNode(), bgVal, 8, false, ref visitedCount);
                if (h > 0) bgSuccess++;
                bgHits += h;
                bgVisited += visitedCount;
            }
            int holderDegree = holder == null ? -1 : holder.Degree;
            // discriminating test: does placement at fixed degree matter?
            // The degree sum of the holder's neighbors proxies neighbor quality.
            long holderNbrDeg = 0;
            // overlap measure: the holder's 2- and 3-hop balls. The union of
            // its neighbors' 2-hop basins equals the holder's 3-hop ball.
            int reach2 = 0, reach3 = 0;
            if (holder != null)
            {
                reach2 = createSubGraph(holder, 2).Count;
                reach3 = createSubGraph(holder, 3).Count;
            }
            if (holder != null)
            {
                Link it2 = holder.NextLink;
                while (it2 != null)
                {
                    if (it2.VertexLink != null) holderNbrDeg += it2.VertexLink.Degree;
                    it2 = it2.NextLink;
                }
            }
            double holderTrend = holder == null ? 0 : holder.TrendScore;
            int holderTrending = holder != null && holder.Persistent ? 1 : 0;
            // degree, success and trend state of contents 1..K-1 in a multi TBP run
            StringBuilder extra = new StringBuilder();
            if (Trend != null && Trend.TbpVals.Length > 1)
            {
                for (int k = 1; k < Trend.TbpVals.Length; k++)
                {
                    Node h = getHolder(Trend.TbpVals[k]);
                    int succ = 0;
                    for (int p = 0; p < probes; p++)
                    {
                        int vc = 0;
                        if (nfSearch(getRndNode(), Trend.TbpVals[k], 8, false, ref vc) > 0)
                            succ++;
                    }
                    extra.Append(",").Append(h == null ? -1 : h.Degree)
                         .Append(",").Append(succ)
                         .Append(",").Append(h != null && h.Persistent ? 1 : 0);
                }
            }
            // CSV her zaman nokta ondalikli yazilmali (locale'den bagimsiz)
            StringBuilder line = new StringBuilder();
            line.Append(simTime).Append(",").Append(N).Append(",").Append(cnt).Append(",").Append(holderDegree);
            for (int t = 0; t < ttls.Length; t++)
                line.Append(",").Append(success[t]).Append(",").Append(visitedTotal[t] / probes);
            line.Append(",").Append(holderTrend.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            line.Append(",").Append(bgSuccess).Append(",").Append(bgHits).Append(",").Append(bgVisited / bgProbes);
            line.Append(",").Append(lastWindowRewireNodes).Append(",").Append(lastWindowRewireJoins).Append(",").Append(countEdges()).Append(",").Append(holderTrending).Append(",").Append(holderNbrDeg).Append(",").Append(holder==null?0:holder.LastWindowHits).Append(",").Append(holder==null?0:holder.LastWindowTraversals).Append(",").Append(reach2).Append(",").Append(reach3).Append(",").Append(lastWindowSwaps).Append(",").Append(holder==null?0:holder.EntryRule);
            line.Append(extra);
            MetricsWriter.WriteLine(line.ToString());
            MetricsWriter.Flush();
            Console.WriteLine("t=" + simTime + "  N=" + N + "  holderDeg=" + holderDegree +
                "  probe(ttl4/6/8/12)=" + success[0] + "/" + success[1] + "/" + success[2] + "/" + success[3] + " of " + probes);
        }

        public int normalizedFlooding(Node begin, int val, int ttl, ref int nodeCount)
        {
            //int nodeCount = 1;
            int hits = 0;
            ArrayList neighbours = new ArrayList();
            ArrayList newNeighbours = new ArrayList();
            ArrayList visited = new ArrayList();
            //int id;
            //Node begin;
            //do
            //{
            //    id = rnd.Next(0, N - 1);
            //    //id = 7;
            //    //Console.WriteLine("id for normalized flooding...:" + id);
            //    begin = search(id);
            //} while (begin == null);
            if (begin.Item.Values.Contains(val))
            {
                hits++;
            }
            visited.Add(begin);
            //Node previous = begin;
            Link iterator;
            neighbours.Add(begin);
            newNeighbours.Add(begin);
            newNeighbours.Add(new Node(-1));
             
            //visited.Add(begin);
            for (int i = 0; i < ttl; i++)
            {
                neighbours = copyList(newNeighbours);
                //for (int l = 0; l < neighbours.Count; l++)
                //{
                //    Console.WriteLine(((Node)neighbours[l]).NodeID);
                //}
                newNeighbours.Clear();
                for (int j = 0; j < neighbours.Count; j=j+2)
                {
                    ArrayList toBeSent = new ArrayList();
                    begin = (Node)neighbours[j];
                    toBeSent = selectNeighbours(begin, (Node)neighbours[j+1]);
                    //nodeCount += toBeSent.Count; 
                    iterator = begin.NextLink;
                    int counter = 0;
                    while (iterator != null)
                    {
                        
                        if (toBeSent.Contains(counter)&&  iterator.Id != ((Node)neighbours[j+1]).NodeID)
                        {
                            
                            Node temp = iterator.VertexLink;
                            
                            if (!newNeighbours.Contains(temp))
                            {
                                newNeighbours.Add(temp);
                                newNeighbours.Add(begin);
                                //visited.Add(temp);
                                if (!visited.Contains(temp))
                                {
                                    visited.Add(temp);
                                    if (temp.Item.Values.Contains(val))
                                    {
                                        hits++;
                                    }
                                }
                            }
                            
                            
                        }
                        counter++;
                        iterator = iterator.NextLink;
                        
                    }
                    //for (int k = 0; k < newNeighbours.Count; k++)
                    //{
                    //    Console.WriteLine("newneighbours of " + ((Node)neighbours[j]).NodeID + "  " + ((Node)newNeighbours[k]).NodeID);
                    //}
                    //Console.WriteLine("done");
                      
                }
                //previous = (Node)visited[i];
   
            }//for times ttl
            //Console.WriteLine("Node Count..:"+nodeCount);
            nodeCount+=  visited.Count;
            return hits;
        }
        //selects neighbour for a normalized flooding
        private ArrayList selectNeighbours(Node current, Node parent)
        { 
            ArrayList toBeSent = new ArrayList();
            int degree = current.Degree;
            int starting = 0;
            if (parent.NodeID != -1)
            {
                starting = 1;
                Link iterator = current.NextLink;
                Link prev = iterator;
                if (iterator.Id != parent.NodeID)
                {
                    current.NextLink = createLink(parent.NodeID);
                    current.NextLink.NextLink = iterator;
                    while (iterator.Id != parent.NodeID)
                    {
                        prev = iterator;
                        iterator = iterator.NextLink;
                    }
                    prev.NextLink = iterator.NextLink;
                }
            }
            
            int eligible = degree - starting;
            int forward = Math.Min(ForwardFanout, eligible);
            if (eligible > forward)//select at most ForwardFanout neighbours randomly
            {
                while (toBeSent.Count < forward)
                {
                    int selectedNeighbour = rnd.Next(starting, degree);
                    if (!toBeSent.Contains(selectedNeighbour))
                        toBeSent.Add(selectedNeighbour);
                }
            }
            else
            {
               
                for (int k =starting; k < degree; k++)
                {
                    toBeSent.Add(k);
                }
            }
            return toBeSent;
        }


        //copies an ArraYlist into another one
        private ArrayList copyList(ArrayList list)
        {
            ArrayList copy = new ArrayList();
            for (int i = 0; i < list.Count; i++)
            {
                copy.Add(list[i]);
            }
            return copy;
        }

        private ArrayList copyListSingles(ArrayList list)
        {
             
            ArrayList copy = new ArrayList();
            for (int i = 0; i < list.Count; i=i+2)
            {
                copy.Add(list[i]);
            }

            return copy;
        }
        public void countDegrees()
        {
            int[] degrees = new int[maxDegree+1];
            Node iterator = nodeHead;
            //Console.WriteLine(iterator.Degree);
            while (iterator != null)
            {
                if (iterator.Degree>maxDegree)
                    Console.WriteLine("highhhhhhhhhhhhhhhhhhhhhhhhhhhh"+iterator.NodeID);
                degrees[iterator.Degree]++;
                iterator = iterator.NextNode;
            }
            int total = 0;
            for (int i = 0; i < degrees.Length; i++)
            {
                if (degrees[i]!=0)
                Console.WriteLine("degree..:"+i+ " number of nodes...:   "+ degrees[i]);
                total += degrees[i];
            }
            Console.WriteLine(total);
        }
    }
}
