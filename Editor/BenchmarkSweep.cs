// BenchmarkSweep.cs — Post-sweep aggregator for the UV2 transfer pipeline.
// Given a list of per-cell CSVs (produced by BenchmarkRecorder during a
// LightmapTransferTool.ExecSweep run) and the matching CellConfig snapshots,
// reads each CSV, scores the cell, and writes a summary.csv + winner.json
// into a sweep_<timestamp>/ subdirectory of BenchmarkReports/.
// See TRANSFER_BENCHMARK.md for the metric definitions.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Post-run aggregator: consumes per-cell CSVs written by
    /// <see cref="BenchmarkRecorder"/> during a sweep, picks a winner by a
    /// weighted score, and writes summary.csv + winner.json into a dedicated
    /// sweep_<timestamp>/ subdirectory.
    /// </summary>
    internal static class BenchmarkSweep
    {
        // Score weights — see Score() for the formula and rationale.
        const float kWeightUtilization = 100f;
        const float kPenaltySliver     = -50f;
        const float kPenaltyOverlap    = -10f;
        const float kPenaltyMs         = -0.001f;
        const float kPenaltyResolution = -10f;

        /// <summary>
        /// Snapshot of the ctx fields that distinguish one sweep cell from
        /// another. Passed alongside each per-cell CSV into the aggregator so
        /// the summary rows can carry the full configuration (the CSV itself
        /// doesn't track every field in a stable column).
        /// </summary>
        internal struct CellConfig
        {
            public int  atlasRes;
            public int  shellPad;
            public int  borderPad;
            public bool perShellAspect;
            public bool arapEnabled;
            public int  arapIterations;
        }

        internal struct RunSummary
        {
            public CellConfig config;
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
        /// Aggregate a list of per-cell CSVs (with their matching CellConfig
        /// snapshots) into a sweep_<timestamp>/summary.csv + winner.json under
        /// BenchmarkReports/. <paramref name="csvPaths"/> and
        /// <paramref name="configs"/> must be the same length and aligned by
        /// index. Entries with a null/missing CSV are recorded as failed cells.
        /// No-op when fewer than two cells are supplied.
        /// </summary>
        internal static void WriteAggregateReport(List<string> csvPaths, List<CellConfig> configs)
        {
            if (csvPaths == null || configs == null) return;
            int n = Math.Min(csvPaths.Count, configs.Count);
            if (n < 2)
            {
                UvtLog.Info(UvtLog.Category.Benchmark,
                    "[Sweep] Skipping aggregate report — fewer than 2 cells completed.");
                return;
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string reportsDir  = Path.Combine(projectRoot, "BenchmarkReports");
            Directory.CreateDirectory(reportsDir);
            string sweepStamp  = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string sweepDir    = Path.Combine(reportsDir, $"sweep_{sweepStamp}");
            Directory.CreateDirectory(sweepDir);

            var summaries = new List<RunSummary>(n);
            for (int i = 0; i < n; i++)
            {
                string csvPath = csvPaths[i];
                var summary = new RunSummary { config = configs[i] };

                if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                {
                    summary.hadFailure = true;
                }
                else
                {
                    summary.csvPath  = csvPath;
                    summary.jsonPath = Path.ChangeExtension(csvPath, ".json");
                    try
                    {
                        summary = AggregateRun(csvPath, configs[i]);
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
            }

            // Pick the winner by score (failed runs land at -∞ and never win).
            int bestIdx = 0;
            for (int i = 1; i < summaries.Count; i++)
                if (summaries[i].score > summaries[bestIdx].score) bestIdx = i;

            try
            {
                WriteSummaryCsv(Path.Combine(sweepDir, "summary.csv"), summaries, sweepDir);
                WriteWinnerJson(Path.Combine(sweepDir, "winner.json"), summaries, bestIdx);
                WriteGalleryHtml(Path.Combine(sweepDir, "index.html"), summaries, bestIdx,
                    BuildRecommendation(summaries, bestIdx), reportsDir);
            }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] Failed to write summary: {ex.Message}");
                return;
            }

            var w = summaries[bestIdx];
            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[Sweep] Winner: res={w.config.atlasRes}, pad={w.config.shellPad}, bdr={w.config.borderPad}, " +
                $"perShellAspect={w.config.perShellAspect}, arap={(w.config.arapEnabled ? w.config.arapIterations : 0)} " +
                $"(score={w.score:F2})");
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
        internal static float Score(RunSummary r)
        {
            if (r.hadFailure) return float.NegativeInfinity;
            float resPenalty = 0f;
            if (r.config.atlasRes > 0)
                resPenalty = kPenaltyResolution * Mathf.Log(Mathf.Max(1f, r.config.atlasRes / 256f), 2f);
            return kWeightUtilization * r.meanAtlasUtilization
                 + kPenaltySliver     * r.totalSlivers
                 + kPenaltyOverlap    * r.overlapShellPairs
                 + kPenaltyMs         * r.totalMs
                 + resPenalty;
        }

        /// <summary>
        /// Parse a per-run CSV emitted by <see cref="BenchmarkRecorder"/> and
        /// aggregate target-LOD metrics into a <see cref="RunSummary"/>. Column
        /// lookup is header-driven so the parser stays robust against future
        /// column reordering or insertion. Throws on I/O errors; returns a
        /// summary with <c>hadFailure=true</c> when the CSV has no data rows.
        /// </summary>
        internal static RunSummary AggregateRun(string csvPath, CellConfig cfg)
        {
            var summary = new RunSummary
            {
                config   = cfg,
                csvPath  = csvPath,
                jsonPath = Path.ChangeExtension(csvPath, ".json"),
            };

            var lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2)
            {
                summary.hadFailure = true;
                return summary;
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
            return summary;
        }

        static int  SafeInt (string[] c, int i)
            => (i >= 0 && i < c.Length && int.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) ? v : 0;
        static long SafeLong(string[] c, int i)
            => (i >= 0 && i < c.Length && long.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) ? v : 0;

        internal static void WriteSummaryCsv(string path, List<RunSummary> runs, string sweepDir)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("atlasRes,shellPad,borderPad,perShellAspect,arapEnabled,arapIterations," +
                          "totalSlivers,overlapShellPairs,meanAtlasUtilization,totalMs,score,csvPath");
            foreach (var r in runs)
            {
                // Make csvPath relative to BenchmarkReports/ when possible —
                // keeps the summary readable when the project is moved.
                string rel = r.csvPath ?? "";
                if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(sweepDir))
                {
                    string parent = Directory.GetParent(sweepDir)?.FullName;
                    if (!string.IsNullOrEmpty(parent) && rel.StartsWith(parent, StringComparison.Ordinal))
                        rel = rel.Substring(parent.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                sb.Append(r.config.atlasRes.ToString(inv)).Append(',');
                sb.Append(r.config.shellPad.ToString(inv)).Append(',');
                sb.Append(r.config.borderPad.ToString(inv)).Append(',');
                sb.Append(r.config.perShellAspect ? '1' : '0').Append(',');
                sb.Append(r.config.arapEnabled    ? '1' : '0').Append(',');
                sb.Append(r.config.arapIterations.ToString(inv)).Append(',');
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

        internal static void WriteWinnerJson(string path, List<RunSummary> runs, int bestIdx)
        {
            var inv = CultureInfo.InvariantCulture;
            var w = runs[bestIdx];

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"winner\": {\n");
            sb.Append("    \"atlasRes\": ").Append(w.config.atlasRes.ToString(inv)).Append(",\n");
            sb.Append("    \"shellPad\": ").Append(w.config.shellPad.ToString(inv)).Append(",\n");
            sb.Append("    \"borderPad\": ").Append(w.config.borderPad.ToString(inv)).Append(",\n");
            sb.Append("    \"perShellAspect\": ").Append(w.config.perShellAspect ? "true" : "false").Append(",\n");
            sb.Append("    \"arapEnabled\": ").Append(w.config.arapEnabled ? "true" : "false").Append(",\n");
            sb.Append("    \"arapIterations\": ").Append(w.config.arapIterations.ToString(inv)).Append(",\n");
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
                sb.Append("\"atlasRes\": ").Append(r.config.atlasRes.ToString(inv)).Append(", ");
                sb.Append("\"shellPad\": ").Append(r.config.shellPad.ToString(inv)).Append(", ");
                sb.Append("\"borderPad\": ").Append(r.config.borderPad.ToString(inv)).Append(", ");
                sb.Append("\"perShellAspect\": ").Append(r.config.perShellAspect ? "true" : "false").Append(", ");
                sb.Append("\"arapEnabled\": ").Append(r.config.arapEnabled ? "true" : "false").Append(", ");
                sb.Append("\"arapIterations\": ").Append(r.config.arapIterations.ToString(inv)).Append(", ");
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
        /// Emit a self-contained <c>index.html</c> gallery into the sweep
        /// directory. One sortable row per run with per-LOD UV2 thumbnails
        /// linking back into <c>BenchmarkReports/&lt;csvBase&gt;_png/</c>. The
        /// winner row is highlighted via a <c>winner</c> CSS class. The file
        /// is UTF-8 (no BOM) and uses no external dependencies.
        /// </summary>
        internal static void WriteGalleryHtml(string path, List<RunSummary> runs, int bestIdx,
            string recommendation, string benchmarkReportsRoot)
        {
            var inv = CultureInfo.InvariantCulture;
            string sweepName = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            string isoStamp  = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", inv);

            string winnerLabel = "—";
            string winnerScore = "—";
            if (bestIdx >= 0 && bestIdx < runs.Count)
            {
                var w = runs[bestIdx];
                winnerLabel = $"res={w.config.atlasRes}, pad={w.config.shellPad}, bdr={w.config.borderPad}, " +
                              $"psa={(w.config.perShellAspect ? 1 : 0)}, " +
                              $"arap={(w.config.arapEnabled ? w.config.arapIterations : 0)}";
                winnerScore = w.score.ToString("F2", inv);
            }

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n");
            sb.Append("<html>\n<head>\n");
            sb.Append("  <meta charset=\"utf-8\">\n");
            sb.Append("  <title>Sweep: ").Append(HtmlEscape(sweepName))
              .Append(" (").Append(runs.Count.ToString(inv)).Append(" cells)</title>\n");
            sb.Append("  <style>\n");
            sb.Append("    body { font-family: sans-serif; margin: 16px; background: #1e1e1e; color: #ddd; }\n");
            sb.Append("    h1 { color: #fff; }\n");
            sb.Append("    .winner { background: #2a4a2a; }\n");
            sb.Append("    table { border-collapse: collapse; margin: 16px 0; }\n");
            sb.Append("    th, td { border: 1px solid #444; padding: 6px 10px; text-align: right; }\n");
            sb.Append("    th { background: #333; cursor: pointer; user-select: none; }\n");
            sb.Append("    th:hover { background: #444; }\n");
            sb.Append("    td.label { text-align: left; font-family: monospace; }\n");
            sb.Append("    .thumbs { display: flex; gap: 4px; flex-wrap: wrap; }\n");
            sb.Append("    .thumb { display: block; }\n");
            sb.Append("    .thumb img { width: 96px; height: 96px; object-fit: contain; background: #000; border: 1px solid #333; }\n");
            sb.Append("    .thumb-label { font-size: 10px; text-align: center; color: #888; }\n");
            sb.Append("  </style>\n");
            sb.Append("</head>\n<body>\n");
            sb.Append("  <h1>Sweep: ").Append(HtmlEscape(sweepName)).Append("</h1>\n");
            sb.Append("  <p>Generated: ").Append(HtmlEscape(isoStamp))
              .Append(". Runs: ").Append(runs.Count.ToString(inv))
              .Append(". Best: ").Append(HtmlEscape(winnerLabel))
              .Append(" (score ").Append(HtmlEscape(winnerScore)).Append(").</p>\n");

            sb.Append("  <h2>Recommendation</h2>\n");
            sb.Append("  <p>").Append(HtmlEscape(recommendation ?? "")).Append("</p>\n");

            sb.Append("  <h2>Per-run metrics</h2>\n");
            sb.Append("  <table id=\"runs\">\n");
            sb.Append("    <thead>\n");
            sb.Append("      <tr>\n");
            sb.Append("        <th onclick=\"sortBy(0)\">#</th>\n");
            sb.Append("        <th onclick=\"sortBy(1)\">atlasRes</th>\n");
            sb.Append("        <th onclick=\"sortBy(2)\">shellPad</th>\n");
            sb.Append("        <th onclick=\"sortBy(3)\">borderPad</th>\n");
            sb.Append("        <th onclick=\"sortBy(4)\">perShellAspect</th>\n");
            sb.Append("        <th onclick=\"sortBy(5)\">arapEnabled</th>\n");
            sb.Append("        <th onclick=\"sortBy(6)\">arapIters</th>\n");
            sb.Append("        <th onclick=\"sortBy(7)\">slivers</th>\n");
            sb.Append("        <th onclick=\"sortBy(8)\">overlap</th>\n");
            sb.Append("        <th onclick=\"sortBy(9)\">atlas%</th>\n");
            sb.Append("        <th onclick=\"sortBy(10)\">ms</th>\n");
            sb.Append("        <th onclick=\"sortBy(11)\">score</th>\n");
            sb.Append("        <th>UV2 thumbs</th>\n");
            sb.Append("      </tr>\n");
            sb.Append("    </thead>\n");
            sb.Append("    <tbody>\n");

            for (int i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                string cls = (i == bestIdx) ? " class=\"winner\"" : "";
                sb.Append("      <tr").Append(cls).Append(">\n");
                sb.Append("        <td>").Append((i + 1).ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.config.atlasRes.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.config.shellPad.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.config.borderPad.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.config.perShellAspect ? "1" : "0").Append("</td>\n");
                sb.Append("        <td>").Append(r.config.arapEnabled    ? "1" : "0").Append("</td>\n");
                sb.Append("        <td>").Append(r.config.arapIterations.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.totalSlivers.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.overlapShellPairs.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append((r.meanAtlasUtilization * 100f).ToString("F2", inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.totalMs.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.score.ToString("F2", inv)).Append("</td>\n");
                sb.Append("        <td class=\"label\">").Append(BuildThumbsCell(r.csvPath, benchmarkReportsRoot)).Append("</td>\n");
                sb.Append("      </tr>\n");
            }

            sb.Append("    </tbody>\n");
            sb.Append("  </table>\n");

            sb.Append("  <script>\n");
            sb.Append("    function sortBy(col) {\n");
            sb.Append("      const tbl = document.getElementById('runs');\n");
            sb.Append("      const rows = Array.from(tbl.tBodies[0].rows);\n");
            sb.Append("      const asc = tbl._sortCol !== col || tbl._sortAsc === false;\n");
            sb.Append("      rows.sort((a, b) => {\n");
            sb.Append("        let av = a.cells[col].textContent;\n");
            sb.Append("        let bv = b.cells[col].textContent;\n");
            sb.Append("        const af = parseFloat(av), bf = parseFloat(bv);\n");
            sb.Append("        if (!isNaN(af) && !isNaN(bf)) return asc ? af - bf : bf - af;\n");
            sb.Append("        return asc ? av.localeCompare(bv) : bv.localeCompare(av);\n");
            sb.Append("      });\n");
            sb.Append("      tbl._sortCol = col; tbl._sortAsc = asc;\n");
            sb.Append("      const tb = tbl.tBodies[0];\n");
            sb.Append("      rows.forEach(r => tb.appendChild(r));\n");
            sb.Append("    }\n");
            sb.Append("  </script>\n");
            sb.Append("</body>\n</html>\n");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            UvtLog.Info(UvtLog.Category.Benchmark, $"[Sweep] gallery → {path}");
        }

        /// <summary>
        /// Resolve the sibling <c>&lt;csvBase&gt;_png/</c> directory for a
        /// run's CSV and emit the inner HTML for the "UV2 thumbs" cell —
        /// one anchored thumbnail per PNG, sorted by file name so LOD0 lands
        /// before LOD1, LOD2, … Returns <c>&lt;em&gt;(no PNG)&lt;/em&gt;</c>
        /// when the directory is missing or empty.
        /// </summary>
        static string BuildThumbsCell(string csvPath, string benchmarkReportsRoot)
        {
            if (string.IsNullOrEmpty(csvPath)) return "<em>(no PNG)</em>";
            string csvBase = Path.GetFileNameWithoutExtension(csvPath);
            if (string.IsNullOrEmpty(csvBase) || string.IsNullOrEmpty(benchmarkReportsRoot))
                return "<em>(no PNG)</em>";

            string pngDirName = csvBase + "_png";
            string pngDirAbs  = Path.Combine(benchmarkReportsRoot, pngDirName);
            if (!Directory.Exists(pngDirAbs)) return "<em>(no PNG)</em>";

            string[] pngs;
            try { pngs = Directory.GetFiles(pngDirAbs, "*.png"); }
            catch { return "<em>(no PNG)</em>"; }
            if (pngs == null || pngs.Length == 0) return "<em>(no PNG)</em>";

            Array.Sort(pngs, StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("<div class=\"thumbs\">");
            foreach (string pngAbs in pngs)
            {
                string fileName = Path.GetFileName(pngAbs);
                // Sweep dir is sibling of the PNG dir under BenchmarkReports/,
                // so "../<base>_png/<file>" is the stable relative link.
                string rel = "../" + pngDirName + "/" + fileName;
                string label = ExtractLodLabel(fileName);
                sb.Append("<div class=\"thumb\">");
                sb.Append("<a href=\"").Append(HtmlEscape(rel)).Append("\" target=\"_blank\">");
                sb.Append("<img src=\"").Append(HtmlEscape(rel)).Append("\" alt=\"")
                  .Append(HtmlEscape(fileName)).Append("\">");
                sb.Append("</a>");
                sb.Append("<div class=\"thumb-label\">").Append(HtmlEscape(label)).Append("</div>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>
        /// Pull the "LOD&lt;N&gt;" token out of a PNG file name like
        /// <c>Wooden_Box_Long_LOD0_uv2.png</c>. Falls back to the whole base
        /// name when the convention doesn't match.
        /// </summary>
        static string ExtractLodLabel(string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(baseName)) return fileName;
            int lodIdx = baseName.IndexOf("LOD", StringComparison.Ordinal);
            if (lodIdx < 0) return baseName;
            int end = lodIdx + 3;
            while (end < baseName.Length && char.IsDigit(baseName[end])) end++;
            if (end == lodIdx + 3) return baseName;
            return baseName.Substring(lodIdx, end - lodIdx);
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
            sb.Append("Use ").Append(w.config.atlasRes).Append(" resolution");
            sb.Append(", shellPad=").Append(w.config.shellPad);
            sb.Append(", borderPad=").Append(w.config.borderPad);
            sb.Append(" with per-shell aspect normalize ");
            sb.Append(w.config.perShellAspect ? "ON" : "OFF");
            sb.Append(" and ARAP ribbon re-parameterization ");
            sb.Append(w.config.arapEnabled
                ? $"ON ({w.config.arapIterations} iters)"
                : "OFF");
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
        /// Looks up the run that shares the winner's resolution/padding and
        /// the non-flipped flag, then reports whether the winner's enabled
        /// flag out-scored its disabled counterpart. Returns null if the pair
        /// is not in the grid.
        /// </summary>
        static bool? ComparePair(List<RunSummary> runs, RunSummary w, bool flipAspect)
        {
            foreach (var r in runs)
            {
                if (r.config.atlasRes  != w.config.atlasRes)  continue;
                if (r.config.shellPad  != w.config.shellPad)  continue;
                if (r.config.borderPad != w.config.borderPad) continue;
                if (flipAspect)
                {
                    if (r.config.arapEnabled    != w.config.arapEnabled)    continue;
                    if (r.config.arapIterations != w.config.arapIterations) continue;
                    if (r.config.perShellAspect == w.config.perShellAspect) continue;
                }
                else
                {
                    if (r.config.perShellAspect != w.config.perShellAspect) continue;
                    // For ARAP, treat any enabled/disabled flip as the pair —
                    // varying iter-counts are separate cells but still count
                    // as "ARAP on" for the on/off recommendation comparison.
                    if (r.config.arapEnabled == w.config.arapEnabled) continue;
                }
                bool winnerOn = flipAspect ? w.config.perShellAspect : w.config.arapEnabled;
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

        /// <summary>
        /// Minimal HTML escape covering the four characters that can break
        /// attribute values or text content in our generated index.html
        /// (<c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c>). Sufficient because
        /// the gallery is self-contained and we never embed user-supplied
        /// scripts.
        /// </summary>
        static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;");  break;
                    case '<': sb.Append("&lt;");   break;
                    case '>': sb.Append("&gt;");   break;
                    case '"': sb.Append("&quot;"); break;
                    default:  sb.Append(c);        break;
                }
            }
            return sb.ToString();
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
