// BenchmarkSweep.cs — Parameter-grid driver for the UV2 transfer pipeline.
// Iterates the cartesian product of (atlasRes × per-shell-aspect × ARAP),
// invokes the full pipeline per cell, then scores each cell and writes a
// summary.csv + winner.json alongside the per-run artefacts produced by
// BenchmarkRecorder. See TRANSFER_BENCHMARK.md for the metric definitions.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Driver for parameter sweep: runs Full Pipeline N times across a grid of
    /// (resolution × per-shell-aspect × ARAP) combinations, then picks the
    /// best config by a weighted score and writes a summary.csv + winner.json.
    /// </summary>
    internal static class BenchmarkSweep
    {
        // Grid axes — hard-coded for this iteration. Future: configurable via
        // a TestSuiteAsset entry parallel to the existing sweep matrix.
        static readonly int[]  kResolutions  = { 256, 512, 1024 };
        static readonly bool[] kPerShellAsp  = { false, true };
        static readonly bool[] kArapEnabled  = { false, true };

        // Score weights — see Score() for the formula and rationale.
        const float kWeightUtilization = 100f;
        const float kPenaltySliver     = -50f;
        const float kPenaltyOverlap    = -10f;
        const float kPenaltyMs         = -0.001f;
        const float kPenaltyResolution = -10f;

        internal struct RunSummary
        {
            public int    atlasRes;
            public bool   perShellAspect;
            public bool   arapEnabled;
            public string csvPath;
            public string jsonPath;
            public int    totalSlivers;      // sum(inverted+stretched+zeroArea+oob) across target LODs
            public int    overlapShellPairs; // sum across target LODs
            public float  meanAtlasUtilization;
            public long   totalMs;            // sum(pipeline+repack+transfer+validate)
            public float  score;
            public bool   hadFailure;
        }

        /// <summary>
        /// Runs the sweep synchronously. <paramref name="execPipeline"/> must be a
        /// delegate that invokes the project's full pipeline using the current
        /// <see cref="UvToolContext"/> state — typically a thin wrapper around
        /// ExecFullPipeline(label). The pipeline is responsible for opening its
        /// own BenchmarkRecorder session per call; this driver only mutates
        /// ctx-fields and reads back the resulting per-cell CSV via
        /// <see cref="BenchmarkRecorder.LastWrittenCsvPath"/>.
        ///
        /// Original ctx field values are restored on exit (in finally), even if
        /// the user cancels the progress bar or the pipeline throws.
        /// </summary>
        internal static void Run(UvToolContext ctx, Action<string> execPipeline)
        {
            if (ctx == null || execPipeline == null) return;

            // Snapshot ctx fields we will mutate — restored unconditionally below.
            int   origRes        = ctx.AtlasResolution;
            bool  origPerShell   = ctx.PerShellAspectNormalize;
            bool  origArap       = ctx.ReparameterizeRibbons;

            int total = kResolutions.Length * kPerShellAsp.Length * kArapEnabled.Length;
            var summaries = new List<RunSummary>(total);

            // Create a dedicated subdirectory so per-cell CSVs are easy to
            // attribute to the sweep run (and easy to delete in bulk).
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string reportsDir  = Path.Combine(projectRoot, "BenchmarkReports");
            Directory.CreateDirectory(reportsDir);
            string sweepStamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string sweepDir   = Path.Combine(reportsDir, $"sweep_{sweepStamp}");
            Directory.CreateDirectory(sweepDir);

            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[Sweep] Starting sweep ({total} cells) → {sweepDir}");

            int done = 0;
            bool cancelled = false;
            try
            {
                foreach (int res in kResolutions)
                {
                    if (cancelled) break;
                    foreach (bool perShell in kPerShellAsp)
                    {
                        if (cancelled) break;
                        foreach (bool arap in kArapEnabled)
                        {
                            int idx = done + 1;
                            string progressMsg = $"Sweep run {idx}/{total}: res={res}, aspect={(perShell ? 1 : 0)}, arap={(arap ? 1 : 0)}";
                            if (EditorUtility.DisplayCancelableProgressBar(
                                    "Benchmark Sweep", progressMsg,
                                    (float)done / Mathf.Max(1, total)))
                            {
                                UvtLog.Warn(UvtLog.Category.Benchmark, "[Sweep] Cancelled by user.");
                                cancelled = true;
                                break;
                            }

                            // Mutate ctx and invoke the pipeline. ExecFullPipeline
                            // opens its own BenchmarkRecorder session and writes
                            // a fresh CSV on Dispose; we read that CSV back via
                            // LastWrittenCsvPath after the call returns.
                            ctx.AtlasResolution         = res;
                            ctx.PerShellAspectNormalize = perShell;
                            ctx.ReparameterizeRibbons   = arap;

                            string label = $"sweep_res{res}_psa{(perShell ? 1 : 0)}_arap{(arap ? 1 : 0)}";
                            BenchmarkRecorder.LastWrittenCsvPath = null;
                            var summary = new RunSummary
                            {
                                atlasRes       = res,
                                perShellAspect = perShell,
                                arapEnabled    = arap,
                            };

                            try
                            {
                                execPipeline(label);
                            }
                            catch (Exception ex)
                            {
                                UvtLog.Error(UvtLog.Category.Benchmark,
                                    $"[Sweep] Cell {idx}/{total} threw: {ex.Message}");
                                summary.hadFailure = true;
                            }

                            string csvPath = BenchmarkRecorder.LastWrittenCsvPath;
                            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                            {
                                UvtLog.Warn(UvtLog.Category.Benchmark,
                                    $"[Sweep] Cell {idx}/{total} produced no CSV (label={label})");
                                summary.hadFailure = true;
                            }
                            else
                            {
                                summary.csvPath  = csvPath;
                                summary.jsonPath = Path.ChangeExtension(csvPath, ".json");
                                try
                                {
                                    AggregateRun(csvPath, ref summary);
                                }
                                catch (Exception ex)
                                {
                                    UvtLog.Error(UvtLog.Category.Benchmark,
                                        $"[Sweep] Failed to aggregate '{csvPath}': {ex.Message}");
                                    summary.hadFailure = true;
                                }
                            }

                            summary.score = Score(summary);
                            summaries.Add(summary);
                            done++;

                            UvtLog.Info(UvtLog.Category.Benchmark,
                                $"[Sweep] Cell {idx}/{total} done: " +
                                $"slivers={summary.totalSlivers}, overlap={summary.overlapShellPairs}, " +
                                $"util={summary.meanAtlasUtilization:F3}, ms={summary.totalMs}, " +
                                $"score={summary.score:F2}");
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                // Restore ctx fields — these mutations are otherwise visible
                // in the UI after the sweep ends and could confuse the user.
                ctx.AtlasResolution         = origRes;
                ctx.PerShellAspectNormalize = origPerShell;
                ctx.ReparameterizeRibbons   = origArap;
            }

            if (summaries.Count == 0)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark, "[Sweep] No cells completed; skipping summary.");
                return;
            }

            // Pick the winner by score (failed runs land at -∞ and never win).
            int bestIdx = 0;
            for (int i = 1; i < summaries.Count; i++)
                if (summaries[i].score > summaries[bestIdx].score) bestIdx = i;

            try
            {
                WriteSummaryCsv(Path.Combine(sweepDir, "summary.csv"), summaries, sweepDir);
                WriteWinnerJson(Path.Combine(sweepDir, "winner.json"), summaries, bestIdx);
            }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] Failed to write summary: {ex.Message}");
            }

            var w = summaries[bestIdx];
            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[Sweep] Winner: res={w.atlasRes}, perShellAspect={w.perShellAspect}, " +
                $"arap={w.arapEnabled} (score={w.score:F2})");
        }

        /// <summary>
        /// Weighted score: higher is better.
        /// <code>
        ///   score =   100 * atlasUtilization (mean across LODs)
        ///           - 50  * totalSlivers
        ///           - 10  * overlapShellPairs
        ///           - 0.001 * totalMs   (per-millisecond penalty)
        ///           - 10  * log2(atlasRes / 256)   (prefer lower resolution if quality equal)
        /// </code>
        /// Failed runs (hadFailure=true) get -∞ so they never win the sweep.
        /// </summary>
        static float Score(RunSummary r)
        {
            if (r.hadFailure) return float.NegativeInfinity;
            float resPenalty = 0f;
            if (r.atlasRes > 0)
                resPenalty = kPenaltyResolution * Mathf.Log(Mathf.Max(1f, r.atlasRes / 256f), 2f);
            return kWeightUtilization * r.meanAtlasUtilization
                 + kPenaltySliver     * r.totalSlivers
                 + kPenaltyOverlap    * r.overlapShellPairs
                 + kPenaltyMs         * r.totalMs
                 + resPenalty;
        }

        /// <summary>
        /// Parse a per-run CSV emitted by <see cref="BenchmarkRecorder"/> and
        /// aggregate target-LOD metrics into the summary. Column lookup is
        /// header-driven so the parser stays robust against future column
        /// reordering or insertion.
        /// </summary>
        static void AggregateRun(string csvPath, ref RunSummary summary)
        {
            var lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2)
            {
                summary.hadFailure = true;
                return;
            }
            var header = lines[0].Split(',');
            int idx(string col)
            {
                for (int i = 0; i < header.Length; i++)
                    if (string.Equals(header[i], col, StringComparison.Ordinal))
                        return i;
                return -1;
            }
            int iIsSource    = idx("isSourceLod");
            int iInverted    = idx("invertedCount");
            int iStretched   = idx("stretchedCount");
            int iZero        = idx("zeroAreaCount");
            int iOob         = idx("oobCount");
            int iOverlap     = idx("overlapShellPairs");
            int iAtlasUtil   = idx("atlasUtilization");
            int iPipeline    = idx("pipelineMs");
            int iRepack      = idx("repackMs");
            int iTransfer    = idx("transferMs");
            int iValidate    = idx("validateMs");

            int slivers = 0, overlap = 0;
            int utilCount = 0;
            float utilSum = 0f;
            long totalMs = 0;
            bool stageRead = false;

            var inv = CultureInfo.InvariantCulture;
            for (int li = 1; li < lines.Length; li++)
            {
                var line = lines[li];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var c = line.Split(',');
                if (c.Length < header.Length) continue;

                bool isSource = iIsSource >= 0 && c[iIsSource] == "1";
                // Only target-LOD rows carry meaningful validation. The
                // source LOD's validation report is the post-repack
                // self-check and would double-count slivers in the
                // score otherwise.
                if (!isSource)
                {
                    slivers += SafeInt(c, iInverted)  + SafeInt(c, iStretched)
                             + SafeInt(c, iZero)     + SafeInt(c, iOob);
                    overlap += SafeInt(c, iOverlap);
                }

                if (iAtlasUtil >= 0 && float.TryParse(c[iAtlasUtil],
                        NumberStyles.Float, inv, out float util) && util > 0f)
                {
                    utilSum += util;
                    utilCount++;
                }

                // Stage timings are session-level and repeat on every row;
                // read them once from the first non-empty record.
                if (!stageRead)
                {
                    totalMs = SafeLong(c, iPipeline) + SafeLong(c, iRepack)
                            + SafeLong(c, iTransfer) + SafeLong(c, iValidate);
                    stageRead = true;
                }
            }

            summary.totalSlivers         = slivers;
            summary.overlapShellPairs    = overlap;
            summary.meanAtlasUtilization = utilCount > 0 ? utilSum / utilCount : 0f;
            summary.totalMs              = totalMs;
        }

        static int  SafeInt (string[] c, int i)
            => (i >= 0 && i < c.Length && int.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) ? v : 0;
        static long SafeLong(string[] c, int i)
            => (i >= 0 && i < c.Length && long.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) ? v : 0;

        static void WriteSummaryCsv(string path, List<RunSummary> runs, string sweepDir)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("atlasRes,perShellAspect,arapEnabled,totalSlivers,overlapShellPairs," +
                          "meanAtlasUtilization,totalMs,score,csvPath");
            foreach (var r in runs)
            {
                // Make csvPath relative to the sweep directory when possible —
                // keeps the summary readable when the project is moved.
                string rel = r.csvPath ?? "";
                if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(sweepDir))
                {
                    string parent = Directory.GetParent(sweepDir)?.FullName;
                    if (!string.IsNullOrEmpty(parent) && rel.StartsWith(parent, StringComparison.Ordinal))
                        rel = rel.Substring(parent.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                sb.Append(r.atlasRes.ToString(inv)).Append(',');
                sb.Append(r.perShellAspect ? '1' : '0').Append(',');
                sb.Append(r.arapEnabled    ? '1' : '0').Append(',');
                sb.Append(r.totalSlivers.ToString(inv)).Append(',');
                sb.Append(r.overlapShellPairs.ToString(inv)).Append(',');
                sb.Append(r.meanAtlasUtilization.ToString("R", inv)).Append(',');
                sb.Append(r.totalMs.ToString(inv)).Append(',');
                sb.Append(r.score.ToString("R", inv)).Append(',');
                sb.Append(Csv(rel));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            UvtLog.Info(UvtLog.Category.Benchmark, $"[Sweep] summary → {path}");
        }

        static void WriteWinnerJson(string path, List<RunSummary> runs, int bestIdx)
        {
            var inv = CultureInfo.InvariantCulture;
            var w = runs[bestIdx];

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"winner\": {\n");
            sb.Append("    \"atlasRes\": ").Append(w.atlasRes.ToString(inv)).Append(",\n");
            sb.Append("    \"perShellAspect\": ").Append(w.perShellAspect ? "true" : "false").Append(",\n");
            sb.Append("    \"arapEnabled\": ").Append(w.arapEnabled ? "true" : "false").Append(",\n");
            sb.Append("    \"score\": ").Append(w.score.ToString("R", inv)).Append(",\n");
            sb.Append("    \"totalSlivers\": ").Append(w.totalSlivers.ToString(inv)).Append(",\n");
            sb.Append("    \"overlapShellPairs\": ").Append(w.overlapShellPairs.ToString(inv)).Append(",\n");
            sb.Append("    \"meanAtlasUtilization\": ").Append(w.meanAtlasUtilization.ToString("R", inv)).Append(",\n");
            sb.Append("    \"totalMs\": ").Append(w.totalMs.ToString(inv)).Append(",\n");
            sb.Append("    \"csvPath\": ").Append(JsonString(w.csvPath ?? "")).Append("\n");
            sb.Append("  },\n");

            sb.Append("  \"recommendation\": ").Append(JsonString(BuildRecommendation(runs, bestIdx))).Append(",\n");

            sb.Append("  \"scoring\": {\n");
            sb.Append("    \"atlasUtilizationWeight\": ").Append(kWeightUtilization.ToString("R", inv)).Append(",\n");
            sb.Append("    \"sliverPenalty\": ").Append(kPenaltySliver.ToString("R", inv)).Append(",\n");
            sb.Append("    \"overlapPenalty\": ").Append(kPenaltyOverlap.ToString("R", inv)).Append(",\n");
            sb.Append("    \"msPenalty\": ").Append(kPenaltyMs.ToString("R", inv)).Append(",\n");
            sb.Append("    \"resolutionPenalty\": ").Append(kPenaltyResolution.ToString("R", inv)).Append("\n");
            sb.Append("  },\n");

            sb.Append("  \"runs\": [\n");
            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                sb.Append("    {");
                sb.Append("\"atlasRes\": ").Append(r.atlasRes.ToString(inv)).Append(", ");
                sb.Append("\"perShellAspect\": ").Append(r.perShellAspect ? "true" : "false").Append(", ");
                sb.Append("\"arapEnabled\": ").Append(r.arapEnabled ? "true" : "false").Append(", ");
                sb.Append("\"totalSlivers\": ").Append(r.totalSlivers.ToString(inv)).Append(", ");
                sb.Append("\"overlapShellPairs\": ").Append(r.overlapShellPairs.ToString(inv)).Append(", ");
                sb.Append("\"meanAtlasUtilization\": ").Append(r.meanAtlasUtilization.ToString("R", inv)).Append(", ");
                sb.Append("\"totalMs\": ").Append(r.totalMs.ToString(inv)).Append(", ");
                sb.Append("\"score\": ").Append(r.score.ToString("R", inv)).Append(", ");
                sb.Append("\"hadFailure\": ").Append(r.hadFailure ? "true" : "false").Append(", ");
                sb.Append("\"csvPath\": ").Append(JsonString(r.csvPath ?? ""));
                sb.Append("}");
                if (i < runs.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ]\n");
            sb.Append("}\n");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            UvtLog.Info(UvtLog.Category.Benchmark, $"[Sweep] winner → {path}");
        }

        /// <summary>
        /// Build a short English summary contrasting the winner against the
        /// alternatives along each grid axis. Reads as: "use this resolution,
        /// per-shell-aspect helped/didn't, ARAP helped/didn't".
        /// </summary>
        static string BuildRecommendation(List<RunSummary> runs, int bestIdx)
        {
            var w = runs[bestIdx];
            var sb = new StringBuilder();
            sb.Append("Use ").Append(w.atlasRes).Append(" resolution");
            sb.Append(" with per-shell aspect normalize ");
            sb.Append(w.perShellAspect ? "ON" : "OFF");
            sb.Append(" and ARAP ribbon re-parameterization ");
            sb.Append(w.arapEnabled ? "ON" : "OFF");
            sb.Append('.');

            // Compare against the same config with the toggle flipped, if present.
            bool? aspectHelped = ComparePair(runs, w, flipAspect: true);
            bool? arapHelped   = ComparePair(runs, w, flipAspect: false);
            if (aspectHelped.HasValue)
                sb.Append(" Per-shell aspect ")
                  .Append(aspectHelped.Value ? "improved" : "did not improve")
                  .Append(" results on this asset.");
            if (arapHelped.HasValue)
                sb.Append(" ARAP ")
                  .Append(arapHelped.Value ? "improved" : "did not improve")
                  .Append(" results on this asset.");
            return sb.ToString();
        }

        /// <summary>
        /// Looks up the run that shares the winner's resolution and the
        /// non-flipped flag, then reports whether the winner's enabled flag
        /// out-scored its disabled counterpart. Returns null if the pair is
        /// not in the grid.
        /// </summary>
        static bool? ComparePair(List<RunSummary> runs, RunSummary w, bool flipAspect)
        {
            foreach (var r in runs)
            {
                if (r.atlasRes != w.atlasRes) continue;
                if (flipAspect)
                {
                    if (r.arapEnabled != w.arapEnabled) continue;
                    if (r.perShellAspect == w.perShellAspect) continue;
                }
                else
                {
                    if (r.perShellAspect != w.perShellAspect) continue;
                    if (r.arapEnabled == w.arapEnabled) continue;
                }
                // r is the same-axis counterpart with the queried flag flipped.
                bool winnerOn = flipAspect ? w.perShellAspect : w.arapEnabled;
                bool winnerBetter = w.score > r.score;
                return winnerOn ? winnerBetter : !winnerBetter;
            }
            return null;
        }

        // ── Helpers ──
        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static string JsonString(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
