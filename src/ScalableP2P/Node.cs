using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScalableP2P
{
    class Node
    {
        
        //int maximumValue=100;
        static Zipf zipf = new Zipf(0.3, 100);
        static Zipf zipf2 = new Zipf(0.3, 100);

        Item item;
        Link nextLink;
        int nodeID;
        Node nextNode;
        int degree;
        double shortEwma;
        double longEwma;
        double meanTraversals;
        double trendScore;
        double baselineShare;
        double cusum;
        int windowHits;
        int windowTraversals;
        bool prevTrigger1;
        bool prevTrigger2;
        bool trending;
        bool hotLevel;
        int hotStreak;
        bool ewmaInitialized;
        static Random rnd = new Random(100003);

        public int Degree
        {
            get { return degree; }
            set { degree = value; }
        }
        internal Item Item
        {
            get { return item; }
            set { item = value; }
        }
        public int NodeID
        {
            get { return nodeID; }
            set { nodeID = value; }
        }
        public Node NextNode
        {
            get { return nextNode; }
            set { nextNode = value; }
        }
        public Link NextLink
        {
            get { return nextLink; }
            set { nextLink = value; }
        }

        public Node(int id)
        {
            item = new Item();
            nodeID = id;
            nextNode = null;
            nextLink = null;
            //degree = 3;
        }

        public double TrendScore
        {
            get { return trendScore; }
        }

        // Measurement only. These fields retain the window values before reset
        // and do not affect any simulator decision.
        int lastWindowHits, lastWindowTraversals;
        public int LastWindowHits { get { return lastWindowHits; } }
        // Entry rule record: 0 none, 1 k sigma, 2 CUSUM, 3 level rule.
        int entryRule = 0;
        public int EntryRule { get { return entryRule; } }
        public int LastWindowTraversals { get { return lastWindowTraversals; } }

        // A node remains persistent while its trend or level rule is active.
        public bool Persistent
        {
            get { return trending || hotLevel; }
        }

        // Reset all random streams from the graph seed.
        public static void resetSeeds(int seed)
        {
            zipf = new Zipf(0.3, 100, 10007 + seed);
            zipf2 = new Zipf(0.3, 100, 20011 + seed);
            rnd = new Random(100003 + seed);
        }

        // Count each traversing query and each local content hit. The observed
        // hit share is the ratio of hits to traversals.
        public void observeQuery(int val)
        {
            windowTraversals++;
            if (item.Values.Contains(val))
                windowHits++;
        }

        // At the end of a window, the difference between the short and long
        // hit share EWMAs supplies the upward signal. The entry threshold uses
        // the binomial variance Var(share) = p(1-p)/q with the long mean and
        // traversal EWMA substituted for p and q.
        // Var(S-L) = (a_s/(2-a_s) + a_l/(2-a_l)) * Var(pay) = 0.121 * Var(pay).
        // A window triggers when D reaches max(k sigmaD, shareFloor) and has a
        // fresh hit. Persistence uses a run rule over the recent windows. The
        // frozen long mean becomes the baseline at entry, and the mode closes
        // when the short mean returns to the baseline region. Initializing both
        // EWMAs from the first observed window prevents a startup trigger.
        // Oracle baseline: force trend mode using external information.
        public void forceTrending()
        {
            trending = true;
            baselineShare = 0;
        }

        // A positive fixedThreshold replaces self calibration with one absolute
        // threshold and disables CUSUM. ruleMask controls the k sigma, CUSUM,
        // and level rules in bits 0, 1, and 2. The default enables all three.
        public void updateTrendWindow(double sigmaK, double shareFloor, int persistWindows, double cusumTheta, double fixedThreshold, double hotShare, int ruleMask)
        {
            if (windowTraversals > 0)
            {
                double share = (double)windowHits / windowTraversals;
                if (!ewmaInitialized)
                {
                    shortEwma = share;
                    longEwma = share;
                    meanTraversals = windowTraversals;
                    ewmaInitialized = true;
                }
                else
                {
                    bool freshEvidence = windowHits >= 1;
                    double prevLong = longEwma;
                    shortEwma = 0.2 * share + 0.8 * shortEwma;
                    longEwma = 0.02 * share + 0.98 * longEwma;
                    meanTraversals = 0.05 * windowTraversals + 0.95 * meanTraversals;
                    if (!trending)
                    {
                        trendScore = Math.Max(0, shortEwma - longEwma);
                        double shareVar = longEwma * (1 - longEwma) / Math.Max(meanTraversals, 1);
                        double sigmaD = Math.Sqrt(0.121 * shareVar);
                        // Rule 1 (fast path): a fresh hit in the window AND
                        // the k-sigma / materiality limit exceeded
                        double limit = fixedThreshold > 0
                            ? fixedThreshold
                            : Math.Max(sigmaK * sigmaD, shareFloor);
                        bool triggered = ((ruleMask & 1) != 0) && freshEvidence && trendScore >= limit;
                        int recent = (triggered ? 1 : 0) + (prevTrigger1 ? 1 : 0) + (prevTrigger2 ? 1 : 0);
                        // Rule 2 (safety net): a Bernoulli CUSUM accumulates
                        // every persistent rise of the share above shareFloor
                        // over time; it catches slow or irregularly spaced
                        // trends independently of window quantization
                        cusum = Math.Max(0, cusum + windowHits - windowTraversals * (prevLong + shareFloor));
                        bool cusumEntry = ((ruleMask & 2) != 0) && fixedThreshold <= 0 && cusum >= cusumTheta;
                        if ((triggered && recent >= persistWindows) || cusumEntry)
                        {
                            if (entryRule == 0) entryRule = (triggered && recent >= persistWindows) ? 1 : 2;
                            trending = true;
                            // baseline: the persistent demand level before the trend
                            baselineShare = prevLong;
                            prevTrigger1 = false;
                            prevTrigger2 = false;
                            cusum = 0;
                        }
                        else
                        {
                            prevTrigger2 = prevTrigger1;
                            prevTrigger1 = triggered;
                        }
                    }
                    else
                    {
                        trendScore = Math.Max(0, shortEwma - baselineShare);
                        if (shortEwma < baselineShare + shareFloor)
                        {
                            trending = false;
                            trendScore = Math.Max(0, shortEwma - longEwma);
                        }
                    }
                    // Level rule (dynamic popularity): the derivative signal
                    // never fires on nodes born while demand is already high
                    // (a replica rejoining after a crash, a sudden flash
                    // crowd, a holder joining hot content late), because the
                    // warm start learns the high demand as its baseline. A
                    // share that STAYS above the hotShare watermark also
                    // calls for adaptation: that level, several times the
                    // share of even the most popular ordinary content, is
                    // the point at which demand itself earns the node a more
                    // central position. Exit hysteresis spans the band from
                    // hotShare down to shareFloor.
                    if (((ruleMask & 4) != 0) && freshEvidence && shortEwma >= hotShare)
                        hotStreak++;
                    else if (shortEwma < hotShare - shareFloor)
                    {
                        hotStreak = 0;
                        hotLevel = false;
                    }
                    if (hotStreak >= persistWindows) { if (!hotLevel && entryRule == 0) entryRule = 3; hotLevel = true; }
                    if (hotLevel && !trending)
                        trendScore = Math.Max(trendScore, shortEwma - (hotShare - shareFloor));
                }
            }
            lastWindowHits = windowHits;
            lastWindowTraversals = windowTraversals;
            windowHits = 0;
            windowTraversals = 0;
        }

        public void addItem(object value)
        {
            item.addItem(value);
        }
        public void addItemsRandomly()
        {

            //int limit = rnd.Next(maximumItem);

            int limit = (int)zipf2.getZipfValue(0.3, 100);
            //Console.Write(limit+" ");
            limit = 1;
            for (int i = 0; i <limit ; i++)
            {
                int val = (int)zipf.getZipfValue(0.3, 100);
                //Console.Write(val + " ");
                //int val = (int)rnd.Next(100);
                item.addItem(val); 
            }
        }
    }
}
