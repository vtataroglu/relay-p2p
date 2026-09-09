using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;

namespace ScalableP2P
{
    class Program
    {
        const string CsvHeader = "step,maxId,removed,holderDegree,s4,v4,s6,v6,s8,v8,s12,v12,holderTrend,bgSucc,bgHits,bgVis,rewireNodes,rewireJoins,edges,holderTrending,holderNbrDeg,hH,hQ,reach2,reach3,swaps,entryRule";

        // 2016 calismasindaki baslangic ag icerigi
        static void seedItems(Graph graf)
        {
            graf.addItemToNode(0, 1); graf.addItemToNode(0, 2);
            graf.addItemToNode(0, 3); graf.addItemToNode(1, 1);
            graf.addItemToNode(1, 2); graf.addItemToNode(1, 3);
            graf.addItemToNode(1, 4); graf.addItemToNode(2, 1);
            graf.addItemToNode(2, 2); graf.addItemToNode(3, 1);
            graf.addItemToNode(3, 2); graf.addItemToNode(4, 1);
            graf.addItemToNode(4, 2); graf.addItemToNode(4, 3);
            graf.addItemToNode(4, 4); graf.addItemToNode(4, 5);
            graf.addItemToNode(4, 6); graf.addItemToNode(4, 7);
            graf.addItemToNode(7, 1); graf.addItemToNode(9, 8);
        }

        // runs one configuration with all parameters; the TBP scenario
        // (entry step, ramp start and peak) scales with the network size.
        // crash: >0 disconnects the holder at that step. shape: 0 linear,
        // 1 step, 2 exponential. multi: >1 admits that many concurrent TBP
        // contents in stages. randrw: >0 grants the same join budget per
        // window to that many random nodes. oracle: the holder stays in
        // trend mode from TStart on. fixedthr: >0 replaces self calibration
        // with a fixed absolute threshold.
        static void runOne(string outdir, string name, int seed, double pp, double pd, double pt,
                           bool rewire, double sigmaK, int persist, double shareFloor, double qmax, int nTarget,
                           double cusumTheta, int crash, int shape, int multi, int randrw, bool oracle, double fixedthr,
                           int kc, bool joinerCut, int oracleplace, bool swap, double bassP, double bassQ, int win, int ruleMask, bool reseedBg,
                           bool periodicMetrics = true, Action<Graph> terminalAction = null,
                           Action<Graph> configureGraph = null,
                           Action<Graph> beforeHistogram = null)
        {
            Directory.CreateDirectory(outdir);
            Console.WriteLine("\n================ " + name + "  seed=" + seed +
                "  (Pp=" + pp + " Pd=" + pd + " Pt=" + pt + " rewire=" + rewire +
                " k=" + sigmaK + " c=" + persist + " eps=" + shareFloor + " qmax=" + qmax + " N=" + nTarget +
                " theta=" + cusumTheta + " crash=" + crash + " shape=" + shape + " multi=" + multi +
                " randrw=" + randrw + " oracle=" + oracle + " fixedthr=" + fixedthr + ") ================");
            Node.resetSeeds(seed);
            Graph graf = new Graph(3, seed);
            graf.Pp = pp;
            graf.Pd = pd;
            graf.Pt = pt;
            graf.RewireEnabled = rewire;
            graf.SigmaK = sigmaK;
            graf.PersistWindows = persist;
            graf.ShareFloor = shareFloor;
            graf.CusumTheta = cusumTheta;
            graf.HolderCrashStep = crash;
            graf.RandomRewireNodes = randrw;
            graf.OracleDetector = oracle;
            graf.FixedThreshold = fixedthr;
            graf.maxDegree = kc;
            graf.CutoffAppliesToJoiner = joinerCut;
            graf.OraclePlacement = oracleplace;
            graf.SwapEnabled = swap;
            graf.WindowSize = win;
            graf.RuleMask = ruleMask;
            if (reseedBg) graf.reseedBackgroundProbes(seed);
            if (configureGraph != null) configureGraph(graf);
            TrendModel tm = new TrendModel(777 + seed);
            tm.Shape = shape;
            int items = Math.Max(1, multi);
            tm.TbpVals = new int[items];
            tm.TStarts = new int[items];
            tm.TPeaks = new int[items];
            tm.QMaxes = new double[items];
            graf.TbpEntrySteps = new int[items];
            for (int k = 0; k < items; k++)
            {
                tm.TbpVals[k] = 1000 + k;
                if (items > 1 && shape == 3)
                {
                    // consecutive damped pulses: tests whether nonoverlapping
                    // rise and decay cycles build up stale hubs over a long horizon
                    tm.TStarts[k] = (int)((2200L + 2400L * k) * nTarget / 10000);
                    tm.TPeaks[k] = tm.TStarts[k] + (int)(1500L * nTarget / 10000);
                    graf.TbpEntrySteps[k] = (int)((1120L + 1680L * k) * nTarget / 10000);
                }
                else
                {
                    // staged entries and ramps; identical to the old values for one content
                    tm.TStarts[k] = (int)((items > 1 ? 3000L + 600L * k : 2500L) * nTarget / 10000);
                    tm.TPeaks[k] = tm.TStarts[k] + (int)(4500L * nTarget / 10000);
                    graf.TbpEntrySteps[k] = (int)((2000L + (items > 1 ? 400L * k : 0)) * nTarget / 10000);
                }
                tm.QMaxes[k] = qmax;
            }
            if (shape == 4)
            {
                tm.Pcoef = new double[items]; tm.Qcoef = new double[items];
                for (int k = 0; k < items; k++) { tm.Pcoef[k] = bassP; tm.Qcoef[k] = bassQ; }
                tm.ValidateBass();
                Console.WriteLine("Bass: p=" + bassP + " q=" + bassQ + " beta=" + (bassP+bassQ) +
                    " beta_w=" + ((bassP+bassQ)*200.0/tm.StepsPerBassUnit) +
                    " SPBU=" + tm.StepsPerBassUnit + " t_inf_step=" + tm.InflectionStep(0).ToString("0.0"));
            }
            graf.Trend = tm;
            graf.initialGraph();
            seedItems(graf);
            string stem = Path.Combine(outdir, name + "-s" + seed);
            if (periodicMetrics)
            {
                using (StreamWriter writer = new StreamWriter(stem + ".csv"))
                {
                    StringBuilder header = new StringBuilder(CsvHeader);
                    for (int k = 1; k < items; k++)
                        header.Append(",deg").Append(k).Append(",s8i").Append(k).Append(",trending").Append(k);
                    writer.WriteLine(header.ToString());
                    graf.MetricsWriter = writer;
                    graf.grow(nTarget);
                    graf.MetricsWriter = null;
                }
            }
            else graf.grow(nTarget);
            if (beforeHistogram != null) beforeHistogram(graf);
            graf.writeDegreeHistogram(stem + "-degrees.csv");
            if (terminalAction != null) terminalAction(graf);
        }

        static void runNeutralPathAware(
            string outdir, int experimentSeed, int fanout, int nTarget,
            int kc, int calibrationProbes, int outcomeProbes,
            int visitBudget, bool uniformLocal, bool confirmOnly,
            int backgroundProbes, int backgroundVisitBudget,
            bool dausOnly = false)
        {
            Directory.CreateDirectory(outdir);
            string outputPath = Path.Combine(outdir,
                "path-aware-m" + fanout + "-s" + experimentSeed + ".csv");
            string outputStem = Path.GetFileNameWithoutExtension(outputPath);
            string degreePath = Path.Combine(outdir,
                "path-aware-neutral-s" + experimentSeed + "-degrees.csv");
            string[] expectedOutputs = new string[] {
                outputPath,
                Path.Combine(outdir, outputStem + "-probes.csv"),
                Path.Combine(outdir, outputStem + "-candidates.csv"),
                Path.Combine(outdir, outputStem + "-selection.csv"),
                degreePath
            };
            if (expectedOutputs.Any(path =>
                    File.Exists(path) || File.Exists(path + ".partial")))
                throw new IOException(
                    "Refusing to overwrite a complete or partial path aware result.");

            Node.resetSeeds(experimentSeed);
            Graph graph = new Graph(3, experimentSeed);
            graph.Pp = 100;
            graph.Pd = 0;
            graph.Pt = 0;
            graph.RewireEnabled = false;
            graph.QueryEvery = int.MaxValue;
            graph.maxDegree = kc;
            graph.CutoffAppliesToJoiner = true;
            graph.UniformLocalAttachment = uniformLocal;
            graph.Trend = null;
            graph.TbpEntrySteps = new int[] { int.MaxValue };
            graph.initialGraph();
            seedItems(graph);
            graph.grow(nTarget);
            graph.RunPathAwareRelayExperiment(
                outputPath, experimentSeed, fanout, calibrationProbes,
                outcomeProbes, visitBudget, nTarget + 11,
                backgroundProbes, backgroundVisitBudget, confirmOnly, dausOnly);
            string degreePartial = degreePath + ".partial";
            graph.writeDegreeHistogram(degreePartial);
            File.Move(degreePartial, degreePath);
        }

        static void runNeutralMechanism(
            string outdir, int experimentSeed, bool uniformLocal,
            int nTarget, int kc, int probes, int visitBudget,
            int balanceProbes)
        {
            Directory.CreateDirectory(outdir);
            string exactPath = Path.Combine(outdir,
                "neutral-exact-s" + experimentSeed + ".csv");
            string matchedPath = Path.Combine(outdir,
                "neutral-position-s" + experimentSeed + ".csv");
            string degreePath = Path.Combine(outdir,
                "neutral-s" + experimentSeed + "-degrees.csv");
            string rawPath = Path.Combine(outdir,
                "neutral-exact-s" + experimentSeed + "-probes.csv");
            string[] expectedOutputs = new string[] {
                exactPath, rawPath, matchedPath, degreePath
            };
            if (expectedOutputs.Any(path =>
                    File.Exists(path) || File.Exists(path + ".partial")))
                throw new IOException(
                    "Refusing to overwrite a complete or partial neutral mechanism result.");

            Node.resetSeeds(experimentSeed);
            Graph graph = new Graph(3, experimentSeed);
            graph.Pp = 100;
            graph.Pd = 0;
            graph.Pt = 0;
            graph.RewireEnabled = false;
            graph.QueryEvery = int.MaxValue;
            graph.maxDegree = kc;
            graph.CutoffAppliesToJoiner = true;
            graph.UniformLocalAttachment = uniformLocal;
            graph.Trend = null;
            graph.TbpEntrySteps = new int[] { int.MaxValue };
            graph.initialGraph();
            seedItems(graph);
            graph.grow(nTarget);
            graph.RunNeutralMechanismSuite(
                exactPath, matchedPath, experimentSeed, probes,
                visitBudget, balanceProbes, nTarget + 11);
            string degreePartial = degreePath + ".partial";
            graph.writeDegreeHistogram(degreePartial);
            File.Move(degreePartial, degreePath);
        }

        static void runExposureAblation(
            string outdir, int experimentSeed, string family)
        {
            if (family != "popularity" && family != "uniform")
                throw new InvalidOperationException(
                    "Exposure ablation family must be popularity or uniform.");
            string outputDirectory = Path.GetFullPath(outdir);

            if (Directory.Exists(outputDirectory) &&
                Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                throw new IOException(
                    "Refusing to use a nonempty exposure ablation output directory.");
            Directory.CreateDirectory(outputDirectory);

            Node.resetSeeds(experimentSeed);
            Graph graph = new Graph(3, experimentSeed);
            graph.Pp = 100;
            graph.Pd = 0;
            graph.Pt = 0;
            graph.RewireEnabled = false;
            graph.QueryEvery = int.MaxValue;
            graph.maxDegree = ExposureAblationConfig.DegreeCutoff;
            graph.CutoffAppliesToJoiner = true;
            graph.UniformLocalAttachment = family == "uniform";
            graph.Trend = null;
            graph.TbpEntrySteps = new int[] { int.MaxValue };
            graph.initialGraph();
            seedItems(graph);
            graph.grow(ExposureAblationConfig.GrowthArrivals);

            Graph.ExposureAblationRunEvidence evidence =
                graph.RunExposureAblationExperiment(
                    outputDirectory,
                    experimentSeed,
                    ExposureAblationConfig.Fanout,
                    ExposureAblationConfig.CalibrationProbes,
                    ExposureAblationConfig.CalibrationVisitBudget,
                    ExposureAblationConfig.OutcomeProbes,
                    ExposureAblationConfig.TargetVisitBudget,
                    ExposureAblationConfig.RelayCount,
                    ExposureAblationConfig.ExpectedLiveNodes,
                    ExposureAblationConfig.BackgroundProbes,
                    ExposureAblationConfig.BackgroundVisitBudget);

            string degreePath = Path.Combine(outputDirectory, "degrees.csv");
            string degreePartial = degreePath + ".partial";
            if (File.Exists(degreePath) || File.Exists(degreePartial))
                throw new IOException(
                    "Refusing to overwrite the exposure ablation degree histogram.");
            graph.writeDegreeHistogram(degreePartial);
            File.Move(degreePartial, degreePath);
            ExposureAblationConfig.WriteCompletion(
                outputDirectory, evidence);
        }

        // Deterministic simulator checks.
        static void selfTest()
        {
            TrendModel tm = new TrendModel(1);
            tm.Shape = 4; tm.TStarts = new int[]{0}; tm.TPeaks = new int[]{9999};
            tm.QMaxes = new double[]{0.2}; tm.TbpVals = new int[]{1000};
            tm.Pcoef = new double[]{0.01}; tm.Qcoef = new double[]{0.40};
            tm.ValidateBass();
            double tinf = tm.InflectionStep(0);
            double qAt = tm.queryProbability((int)Math.Round(tinf), 0);
            Console.WriteLine("t_inf_step        = " + tinf.ToString("0.00"));
            Console.WriteLine("Q(t_inf)          = " + qAt.ToString("0.000000") + "   (q_max = 0.2)");
            Console.WriteLine("|Q(t_inf)-q_max|  = " + Math.Abs(qAt-0.2).ToString("0.00e+0"));
            bool uni = true; int argmax = -1; double best = -1;
            for (int t = 0; t < 12000; t++)
            { double v = tm.queryProbability(t,0); if (v > best) { best = v; argmax = t; } }
            for (int t = 1; t <= argmax; t++)
                if (tm.queryProbability(t,0) < tm.queryProbability(t-1,0)) uni = false;
            for (int t = argmax+1; t < 12000; t++)
                if (tm.queryProbability(t,0) > tm.queryProbability(t-1,0)) uni = false;
            Console.WriteLine("unimodal          = " + uni + "  (argmax step " + argmax + ")");
            Console.WriteLine("Q(0)              = " + tm.queryProbability(0,0).ToString("0.000000"));
            Console.WriteLine("Q(11999)          = " + tm.queryProbability(11999,0).ToString("0.000000"));
            bool threw = false;
            try { TrendModel bad = new TrendModel(1); bad.Pcoef = new double[]{0.4}; bad.Qcoef = new double[]{0.1}; bad.ValidateBass(); }
            catch (ArgumentException) { threw = true; }
            Console.WriteLine("q<=p hard failure = " + threw);
        }

        static double D(string s)
        {
            return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }

        static void Main(string[] args)
        {
            // Same-band exposure ablation. The only policy difference is
            // whether route exposure participates in the within-band rank.
            // exposure-ablation <outdir> <seed> <popularity|uniform>
            if (args.Length == 4 && args[0] == "exposure-ablation")
            {
                string outdir = args[1];
                int experimentSeed = int.Parse(args[2]);
                string family = args[3];
                runExposureAblation(outdir, experimentSeed, family);
                Console.WriteLine(
                    "Exposure ablation completed: " +
                    Path.GetFullPath(outdir));
                return;
            }

            // Neutral confirmatory mechanism suite. This mode contains no
            // trend state, detector, online rewiring, or outcome based setup.
            // neutral-mechanism <outdir> <seed> [uniformLocal 0/1]
            //                   [nTarget] [kc] [probes] [visitBudget]
            //                   [balanceProbes]
            if (args.Length >= 3 && args[0] == "neutral-mechanism")
            {
                string outdir = Path.GetFullPath(args[1]);
                int experimentSeed = int.Parse(args[2]);
                bool uniformLocal = args.Length > 3 && args[3] == "1";
                int nTarget = args.Length > 4 ? int.Parse(args[4]) : 20000;
                int kc = args.Length > 5 ? int.Parse(args[5]) : 50;
                int probes = args.Length > 6 ? int.Parse(args[6]) : 1000;
                int visitBudget = args.Length > 7 ? int.Parse(args[7]) : 2000;
                int balanceProbes = args.Length > 8 ? int.Parse(args[8]) : 500;
                runNeutralMechanism(
                    outdir, experimentSeed, uniformLocal, nTarget, kc,
                    probes, visitBudget, balanceProbes);
                Console.WriteLine(
                    "Neutral mechanism experiment completed: " + outdir);
                return;
            }

            // Outcome free route traces calibrate a relay set, after which
            // fresh probes evaluate all policies from the same graph prefix.
            // path-aware <outdir> <seed> [fanout] [nTarget] [kc]
            //            [calibrationProbes] [outcomeProbes] [visitBudget]
            //            [uniformLocal 0/1] [confirmOnly 0/1]
            //            [backgroundProbes] [backgroundVisitBudget]
            if (args.Length >= 3 && args[0] == "path-aware")
            {
                string outdir = Path.GetFullPath(args[1]);
                int experimentSeed = int.Parse(args[2]);
                int fanout = args.Length > 3 ? int.Parse(args[3]) : 7;
                int nTarget = args.Length > 4 ? int.Parse(args[4]) : 20000;
                int kc = args.Length > 5 ? int.Parse(args[5]) : 50;
                int calibrationProbes = args.Length > 6 ? int.Parse(args[6]) : 400;
                int outcomeProbes = args.Length > 7 ? int.Parse(args[7]) : 2000;
                int visitBudget = args.Length > 8 ? int.Parse(args[8]) : 2000;
                bool uniformLocal = args.Length > 9 && args[9] == "1";
                bool confirmOnly = args.Length > 10 && args[10] == "1";
                int backgroundProbes = args.Length > 11
                    ? int.Parse(args[11]) : 500;
                int backgroundVisitBudget = args.Length > 12
                    ? int.Parse(args[12]) : 100;
                bool dausOnly = args.Length > 13 && args[13] == "1";
                runNeutralPathAware(
                    outdir, experimentSeed, fanout, nTarget, kc,
                    calibrationProbes, outcomeProbes, visitBudget,
                    uniformLocal, confirmOnly, backgroundProbes,
                    backgroundVisitBudget, dausOnly);
                Console.WriteLine("Neutral path aware experiment completed: " + outdir);
                return;
            }

            if (args.Length >= 1 && args[0] == "sweetspot-selftest")
            {
                Graph.AssertRelayUtilitySelfTest();
                Console.WriteLine("relay-utility selftest: PASS");
                return;
            }

            if (args.Length >= 1 && args[0] == "utility-selftest")
            {
                Graph.AssertUtilityPolicySelfTest();
                Console.WriteLine("utility-policy selftest: PASS");
                return;
            }

            // Position matched degree class test. Candidate classes are matched
            // on pretreatment visit exposure measured with an outcome free stream.
            // position-matched <outdir> <seed> [nTarget] [kc] [probes]
            //                  [visitBudget] [balanceProbes]
            if (args.Length >= 3 && args[0] == "position-matched")
            {
                string outdir = Path.GetFullPath(args[1]);
                int experimentSeed = int.Parse(args[2]);
                int nTarget = args.Length > 3 ? int.Parse(args[3]) : 20000;
                int kc = args.Length > 4 ? int.Parse(args[4]) : 50;
                int probes = args.Length > 5 ? int.Parse(args[5]) : 1000;
                int visitBudget = args.Length > 6 ? int.Parse(args[6]) : 2000;
                int balanceProbes = args.Length > 7 ? int.Parse(args[7]) : 500;
                string outputPath = Path.Combine(outdir,
                    "position-matched-s" + experimentSeed + ".csv");
                runOne(
                    outdir, "position-matched-base", experimentSeed,
                    100, 0, 25000, true, 5.0, 2, 0.02, 0.2, nTarget,
                    6.0, 0, 0, 1, 0, false, 0,
                    kc, true, 0, false, 0.01, 0.40, 200, 7, false,
                    false,
                    delegate(Graph graph)
                    {
                        graph.RunPositionMatchedExperiment(
                            outputPath, experimentSeed, probes, visitBudget,
                            balanceProbes, nTarget + 11);
                    });
                Console.WriteLine("Position matched experiment completed: " + outputPath);
                return;
            }

            // Structural diagnostics only. No search outcome is generated.
            // structure-diagnostics <outdir> <seed> [nTarget] [kc]
            if (args.Length >= 3 && args[0] == "structure-diagnostics")
            {
                string outdir = Path.GetFullPath(args[1]);
                int experimentSeed = int.Parse(args[2]);
                int nTarget = args.Length > 3 ? int.Parse(args[3]) : 20000;
                int kc = args.Length > 4 ? int.Parse(args[4]) : 50;
                string outputPath = Path.Combine(outdir,
                    "structure-s" + experimentSeed + ".csv");
                runOne(
                    outdir, "structure-base", experimentSeed,
                    100, 0, 25000, true, 5.0, 2, 0.02, 0.2, nTarget,
                    6.0, 0, 0, 1, 0, false, 0,
                    kc, true, 0, false, 0.01, 0.40, 200, 7, false,
                    false,
                    delegate(Graph graph)
                    {
                        graph.RunSweetSpotStructuralDiagnostics(outputPath, experimentSeed);
                    });
                Console.WriteLine("Structural diagnostics completed: " + outputPath);
                return;
            }

            // Terminal crossover counterfactual:
            // crossover <outdir> <seed> [nTarget] [kc] [probes] [visitBudget] [reverse 0/1]
            // The common prefix is grown once with periodic measurement probes disabled.
            // All four arms are applied reversibly after growth with a dedicated probe RNG.
            if (args.Length >= 3 && args[0] == "crossover")
            {
                string outdir = Path.GetFullPath(args[1]);
                int experimentSeed = int.Parse(args[2]);
                int nTarget = args.Length > 3 ? int.Parse(args[3]) : 20000;
                int kc = args.Length > 4 ? int.Parse(args[4]) : 50;
                int probes = args.Length > 5 ? int.Parse(args[5]) : 1000;
                int visitBudget = args.Length > 6 ? int.Parse(args[6]) : 4000;
                bool reverse = args.Length > 7 && args[7] == "1";
                string summaryPath = Path.Combine(
                    outdir, "crossover-s" + experimentSeed + ".csv");
                if (File.Exists(summaryPath))
                    throw new IOException("Refusing to overwrite result: " + summaryPath);

                runOne(
                    outdir, "crossover-base", experimentSeed,
                    100, 0, 25000, true, 5.0, 2, 0.02, 0.2, nTarget,
                    6.0, 0, 0, 1, 0, false, 0,
                    kc, true, 0, false, 0.01, 0.40, 200, 7, false,
                    false,
                    delegate(Graph graph)
                    {
                        graph.RunSweetSpotCrossover(
                            summaryPath, experimentSeed, probes, visitBudget,
                            nTarget + 11, reverse);
                    });
                Console.WriteLine("Crossover experiment completed: " + summaryPath);
                return;
            }

            // Frozen marginal estimand: 49 holder edges remain fixed and one
            // edge is replaced by an exact post-degree candidate.
            // marginal-edge <outdir> <seed> [nTarget] [kc] [probes] [visitBudget]
            if (args.Length >= 3 && args[0] == "marginal-edge")
            {
                string outdir = Path.GetFullPath(args[1]);
                int experimentSeed = int.Parse(args[2]);
                int nTarget = args.Length > 3 ? int.Parse(args[3]) : 20000;
                int kc = args.Length > 4 ? int.Parse(args[4]) : 50;
                int probes = args.Length > 5 ? int.Parse(args[5]) : 20000;
                int visitBudget = args.Length > 6 ? int.Parse(args[6]) : 4000;
                string summaryPath = Path.Combine(
                    outdir, "marginal-edge-s" + experimentSeed + ".csv");
                runOne(
                    outdir, "marginal-base", experimentSeed,
                    100, 0, 25000, true, 5.0, 2, 0.02, 0.2, nTarget,
                    6.0, 0, 0, 1, 0, false, 0,
                    kc, true, 0, false, 0.01, 0.40, 200, 7, false,
                    false,
                    delegate(Graph graph)
                    {
                        graph.RunMarginalEdgeExperiment(
                            summaryPath, experimentSeed, probes, visitBudget,
                            nTarget + 11);
                    });
                Console.WriteLine("Marginal-edge experiment completed: " + summaryPath);
                return;
            }

            // original-multiholder <outdir> <seed> <holders>
            //                      [nTarget] [kc] [fanout] [probes] [visitBudget]
            if (args.Length >= 4 && args[0] == "original-multiholder")
            {
                string outdir = Path.GetFullPath(args[1]);
                int experimentSeed = int.Parse(args[2]);
                int holders = int.Parse(args[3]);
                int nTarget = args.Length > 4 ? int.Parse(args[4]) : 20000;
                int kc = args.Length > 5 ? int.Parse(args[5]) : 50;
                int fanout = args.Length > 6 ? int.Parse(args[6]) : 7;
                int probes = args.Length > 7 ? int.Parse(args[7]) : 300;
                int visitBudget = args.Length > 8 ? int.Parse(args[8]) : 2000;
                string outputPath = Path.Combine(outdir,
                    "original-multiholder-h" + holders + "-s" + experimentSeed + ".csv");
                runOne(outdir, "multi-base-h" + holders, experimentSeed,
                    100,0,25000,true,5.0,2,0.02,0.2,nTarget,6.0,0,0,1,0,false,0,
                    kc,true,0,false,0.01,0.40,200,7,false,false,
                    delegate(Graph graph)
                    {
                        graph.RunOriginalMultiHolder(outputPath,experimentSeed,holders,
                            fanout,probes,visitBudget,nTarget+11);
                    });
                Console.WriteLine("Original-family multi-holder completed: " + outputPath);
                return;
            }

            // Deployable utility policy experiment:
            // utility-policy <outdir> <policy> <seed> [nTarget] [kc] [fanout]
            //                [probes] [visitBudget] [backgroundProbes]
            //                [backgroundVisitBudget]
            if (args.Length >= 4 && args[0] == "utility-policy")
            {
                string outdir = Path.GetFullPath(args[1]);
                string policy = args[2].Trim().ToLowerInvariant();
                int experimentSeed = int.Parse(args[3]);
                int nTarget = args.Length > 4 ? int.Parse(args[4]) : 20000;
                int kc = args.Length > 5 ? int.Parse(args[5]) : 50;
                int fanout = args.Length > 6 ? int.Parse(args[6]) : 7;
                int probes = args.Length > 7 ? int.Parse(args[7]) : 1000;
                int visitBudget = args.Length > 8 ? int.Parse(args[8]) : 2000;
                int backgroundProbes = args.Length > 9 ? int.Parse(args[9]) : 500;
                int backgroundVisitBudget = args.Length > 10 ? int.Parse(args[10]) : 100;
                string summaryPath = Path.Combine(
                    outdir, "utility-policy-" + policy + "-s" + experimentSeed + ".csv");
                if (File.Exists(summaryPath))
                    throw new IOException("Refusing to overwrite result: " + summaryPath);

                runOne(
                    outdir, "utility-policy-base-" + policy, experimentSeed,
                    100, 0, 25000, true, 5.0, 2, 0.02, 0.2, nTarget,
                    6.0, 0, 0, 1, 0, true, 0,
                    kc, true, 0, false, 0.01, 0.40, 200, 0, false,
                    false,
                    delegate(Graph graph)
                    {
                        graph.RunUtilityPolicyTerminal(
                            summaryPath, experimentSeed, probes, backgroundProbes,
                            visitBudget, backgroundVisitBudget, nTarget + 11);
                    },
                    delegate(Graph graph)
                    {
                        graph.ConfigureUtilityPolicy(policy, experimentSeed, fanout);
                    },
                    delegate(Graph graph)
                    {
                        graph.PrepareUtilityPolicyTerminal();
                    });
                Console.WriteLine("Utility policy experiment completed: " + summaryPath);
                return;
            }

            // tam parametrik kosu:
            // one <outdir> <name> <seed> <pp> <pd> <pt> <rewire 0/1> [k] [c] [eps]
            //     [qmax] [n] [theta] [crash] [shape] [multi] [randrw] [oracle] [fixedthr]
            if (args.Length >= 8 && args[0] == "one")
            {
                runOne(args[1], args[2],
                       int.Parse(args[3]), D(args[4]), D(args[5]), D(args[6]),
                       args[7] == "1",
                       args.Length > 8 ? D(args[8]) : 5.0,
                       args.Length > 9 ? int.Parse(args[9]) : 2,
                       args.Length > 10 ? D(args[10]) : 0.02,
                       args.Length > 11 ? D(args[11]) : 0.2,
                       args.Length > 12 ? int.Parse(args[12]) : 10000,
                       args.Length > 13 ? D(args[13]) : 6.0,
                       args.Length > 14 ? int.Parse(args[14]) : 0,
                       args.Length > 15 ? int.Parse(args[15]) : 0,
                       args.Length > 16 ? int.Parse(args[16]) : 1,
                       args.Length > 17 ? int.Parse(args[17]) : 0,
                       args.Length > 18 && args[18] == "1",
                       args.Length > 19 ? D(args[19]) : 0,
                       args.Length > 20 ? int.Parse(args[20]) : 10000,
                       args.Length > 21 && args[21] == "1",
                       args.Length > 22 ? int.Parse(args[22]) : 0,
                       args.Length > 23 && args[23] == "1",
                       args.Length > 24 ? D(args[24]) : 0.01,
                       args.Length > 25 ? D(args[25]) : 0.40,
                       args.Length > 26 ? int.Parse(args[26]) : 200,
                       args.Length > 27 ? int.Parse(args[27]) : 7,
                       args.Length > 28 && args[28] == "1");
                return;
            }

            if (args.Length >= 1 && args[0] == "selftest") { selfTest(); return; }

            // default: the three main policies, one seed
            int seed = args.Length > 0 ? int.Parse(args[0]) : 42;
            string resultsDir = Path.GetFullPath("results");
            Console.WriteLine("Results dir: " + resultsDir + "  seed=" + seed);

            // three topology growth policies under the same query workload:
            // degree     : degree based preferential attachment
            // popularity : popularity based (the 2016 baseline)
            // trend      : popularity + trend term + proactive rewiring
            runOne(resultsDir, "degree", seed, 0, 100, 0, false, 5.0, 2, 0.02, 0.2, 10000, 6.0, 0, 0, 1, 0, false, 0, 10000, false, 0, false, 0.01, 0.40, 200, 7, false);
            runOne(resultsDir, "popularity", seed, 100, 0, 0, false, 5.0, 2, 0.02, 0.2, 10000, 6.0, 0, 0, 1, 0, false, 0, 10000, false, 0, false, 0.01, 0.40, 200, 7, false);
            runOne(resultsDir, "trend", seed, 100, 0, 25000, true, 5.0, 2, 0.02, 0.2, 10000, 6.0, 0, 0, 1, 0, false, 0, 10000, false, 0, false, 0.01, 0.40, 200, 7, false);

            Console.WriteLine("\nAll configs completed.");
        }
    }
}
