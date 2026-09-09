using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScalableP2P
{
    // Published configuration and completion writer for the same-band
    // exposure ablation. The constants are the exact settings behind the
    // released ablation results; the completion marker records the run
    // parameters and per file digests so a rerun can be checked against
    // the released per graph data.
    internal static class ExposureAblationConfig
    {
        internal const string Experiment = "exposure-ablation-v1";
        internal const string ImplementationVersion = "exposure-ablation-v1";

        internal const int Fanout = 7;
        internal const int GrowthArrivals = 20000;
        internal const int ExpectedLiveNodes = 20011;
        internal const int DegreeCutoff = 50;
        internal const int RelayCount = 50;
        internal const int CalibrationProbes = 400;
        internal const int CalibrationVisitBudget = 2000;
        internal const int OutcomeProbes = 2000;
        internal const int TargetVisitBudget = 2000;
        internal const int BackgroundProbes = 500;
        internal const int BackgroundVisitBudget = 100;
        internal const int CandidateRootCount = 16;
        internal const int CandidateHopLimit = 2;
        internal const double UtilityBandFraction = 0.95;
        internal const int TargetValue = 1000000007;

        internal static void WriteCompletion(
            string outputDirectory,
            Graph.ExposureAblationRunEvidence evidence)
        {
            string completionPath = Path.Combine(
                outputDirectory, "COMPLETED.json");
            string partialPath = completionPath + ".partial";
            if (File.Exists(completionPath) || File.Exists(partialPath))
                throw new IOException(
                    "Refusing to overwrite an exposure ablation completion marker.");

            string[] csvNames = new string[] {
                "summary.csv", "candidates.csv", "selection.csv",
                "probes.csv", "degrees.csv"
            };
            foreach (string name in csvNames)
                if (!File.Exists(Path.Combine(outputDirectory, name)))
                    throw new InvalidOperationException(
                        "Cannot complete an exposure ablation run with a missing file: " +
                        name);

            using (FileStream stream = new FileStream(
                partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None))
            using (Utf8JsonWriter writer = new Utf8JsonWriter(
                stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "schemaVersion", "exposure-ablation-completion-v1");
                writer.WriteString("experiment", Experiment);
                writer.WriteString(
                    "implementationVersion", ImplementationVersion);
                writer.WriteNumber("seed", evidence.Seed);
                writer.WriteString("family", evidence.Family);

                writer.WritePropertyName("parameters");
                writer.WriteStartObject();
                writer.WriteNumber("fanout", Fanout);
                writer.WriteNumber("growthArrivals", GrowthArrivals);
                writer.WriteNumber("expectedLiveNodes", ExpectedLiveNodes);
                writer.WriteNumber("degreeCutoff", DegreeCutoff);
                writer.WriteNumber("relayCount", RelayCount);
                writer.WriteNumber("calibrationProbes", CalibrationProbes);
                writer.WriteNumber(
                    "calibrationVisitBudget", CalibrationVisitBudget);
                writer.WriteNumber("outcomeProbes", OutcomeProbes);
                writer.WriteNumber("targetVisitBudget", TargetVisitBudget);
                writer.WriteNumber("backgroundProbes", BackgroundProbes);
                writer.WriteNumber(
                    "backgroundVisitBudget", BackgroundVisitBudget);
                writer.WriteNumber("candidateRootCount", CandidateRootCount);
                writer.WriteNumber("candidateHopLimit", CandidateHopLimit);
                writer.WriteNumber(
                    "utilityBandFraction", UtilityBandFraction);
                writer.WriteNumber("targetValue", TargetValue);
                writer.WriteEndObject();

                writer.WritePropertyName("expectedArms");
                writer.WriteStartArray();
                writer.WriteStringValue("routeband");
                writer.WriteStringValue("bandforwarding");
                writer.WriteEndArray();

                writer.WritePropertyName("files");
                writer.WriteStartObject();
                foreach (string name in csvNames)
                {
                    string path = Path.Combine(outputDirectory, name);
                    writer.WritePropertyName(name);
                    writer.WriteStartObject();
                    writer.WriteNumber("bytes", new FileInfo(path).Length);
                    writer.WriteString("sha256", Sha256File(path));
                    writer.WriteNumber("dataRows", CountDataRows(path));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();

                writer.WriteString("baseHash", evidence.BaseHash);
                writer.WritePropertyName("armHashes");
                writer.WriteStartObject();
                writer.WriteString(
                    "routeband", evidence.ArmHashes["routeband"]);
                writer.WriteString(
                    "bandforwarding", evidence.ArmHashes["bandforwarding"]);
                writer.WriteEndObject();
                writer.WritePropertyName("postArmRestoreHashes");
                writer.WriteStartObject();
                writer.WriteString(
                    "routeband", evidence.RestoreHashes["routeband"]);
                writer.WriteString(
                    "bandforwarding", evidence.RestoreHashes["bandforwarding"]);
                writer.WriteEndObject();
                writer.WriteBoolean("completed", true);
                writer.WriteEndObject();
            }
            File.Move(partialPath, completionPath);
        }

        internal static string Sha256File(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(stream))
                    .ToLowerInvariant();
        }

        private static int CountDataRows(string path)
        {
            int lines = 0;
            using (StreamReader reader = new StreamReader(path))
                while (reader.ReadLine() != null) lines++;
            if (lines < 1)
                throw new InvalidOperationException(
                    "Output CSV is empty: " + path);
            return lines - 1;
        }
    }
}
