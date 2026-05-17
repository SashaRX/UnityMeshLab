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
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEditor;
// Disambiguate types both namespaces expose so the file compiles cleanly:
//   UnityEditor.PackageInfo (legacy Asset Store metadata) vs UnityEditor.PackageManager.PackageInfo (UPM)
//   UnityEngine.CompressionLevel (texture compression) vs System.IO.Compression.CompressionLevel (zip)
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using CompressionLevel = System.IO.Compression.CompressionLevel;

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
        // Visual-defect weights. duplicate UV2 pairs are the silent killer —
        // two distinct instances baking onto the same atlas region. Weighted
        // as heavy as a sliver because the user-visible effect is comparable.
        // force3D overlaps are explicit bleeding (already warned via
        // shellsOverlapFixed). composite-broken and severe-mismatch are
        // matching-quality signals — lighter penalty.
        const float kPenaltyDupUv2     = -50f;
        const float kPenaltyForce3DOL  = -30f;
        const float kPenaltyCompBroken = -10f;
        const float kPenaltySevere     = -20f;

        /// <summary>
        /// Snapshot of the ctx fields that distinguish one sweep cell from
        /// another. Passed alongside each per-cell CSV into the aggregator so
        /// the summary rows can carry the full configuration (the CSV itself
        /// doesn't track every field in a stable column).
        /// </summary>
        internal struct CellConfig
        {
            public int   atlasRes;
            public int   shellPad;
            public int   borderPad;
            // Stretched-shell ARAP: enabled flag + iteration count + Sander L²
            // gate threshold. Replaces the old per-shell-aspect axis, which
            // applied a flawed affine transform that couldn't fix bad
            // parameterization (only redistributing vertices via ARAP can).
            public bool  arapEnabled;
            public int   arapIterations;
            public float stretchThreshold;
            // xatlas internal-oversample. internalRes = resolution × oversample.
            // 1 = native resolution, 4 = current default. See EXPERIMENTS.md
            // a218a2b — affects Stage B density amplification.
            public int   internalOversample;
            // SymSplit threshold mode (LegacyFixed vs Adaptive). Serialised
            // separately from arapEnabled because it gates a different
            // pre-pack stage.
            public SymmetrySplitShells.ThresholdMode symSplitMode;
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
            // Visual-defect counters, summed across target LODs.
            public int    uv2DuplicatePairs;
            public int    force3DOverlapCount;   // sum of shellsOverlapFixed
            public int    compositeBrokenCount;
            public int    severeMismatchCount;
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
            => WriteAggregateReport(csvPaths, configs, sweepDir: null);

        /// <summary>
        /// Same as the two-argument overload, but accepts a pre-created
        /// <paramref name="sweepDir"/> so a long sweep can keep rewriting the
        /// same summary.csv / winner.json / index.html after every successful
        /// cell. When <paramref name="sweepDir"/> is null or empty, a new
        /// sweep_&lt;timestamp&gt;/ folder is created under BenchmarkReports/
        /// (legacy behavior). The minimum-2-cells gate is bypassed when an
        /// explicit sweepDir is provided so incremental writes work from the
        /// very first completed cell.
        /// </summary>
        internal static void WriteAggregateReport(List<string> csvPaths, List<CellConfig> configs, string sweepDir)
        {
            if (csvPaths == null || configs == null) return;
            int n = Math.Min(csvPaths.Count, configs.Count);
            bool explicitDir = !string.IsNullOrEmpty(sweepDir);
            if (n < 2 && !explicitDir)
            {
                UvtLog.Info(UvtLog.Category.Benchmark,
                    "[Sweep] Skipping aggregate report — fewer than 2 cells completed.");
                return;
            }
            if (n < 1) return;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string defaultReportsDir = Path.Combine(projectRoot, "BenchmarkReports");
            if (!explicitDir)
            {
                Directory.CreateDirectory(defaultReportsDir);
                string sweepStamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                sweepDir = Path.Combine(defaultReportsDir, $"sweep_{sweepStamp}");
            }
            Directory.CreateDirectory(sweepDir);
            // Per-cell PNG thumbnail dirs now live INSIDE sweepDir alongside
            // the per-cell CSVs (LightmapTransferTool sets
            // BenchmarkRecorder.OutputDirectoryOverride = sweepDir before
            // running cells). Previously they were emitted as siblings of
            // sweepDir under BenchmarkReports/; index.html linked via
            // "../<base>_png/<file>". With the new layout the thumbnails
            // are children of sweepDir, so BuildThumbsCell looks inside it
            // and the relative link is just "<base>_png/<file>".
            string reportsDir = sweepDir;

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
            // If every cell failed, there is no valid winner — `bestIdx` would
            // otherwise just default to the first failed run and produce a
            // misleading winner.json. Detect that case and emit summary.csv
            // only, with no winner artefact.
            int bestIdx = 0;
            for (int i = 1; i < summaries.Count; i++)
                if (summaries[i].score > summaries[bestIdx].score) bestIdx = i;
            bool anyValid = summaries.Count > 0
                         && !float.IsNegativeInfinity(summaries[bestIdx].score);

            try
            {
                WriteSummaryCsv(Path.Combine(sweepDir, "summary.csv"), summaries, sweepDir);
                if (anyValid)
                {
                    WriteWinnerJson(Path.Combine(sweepDir, "winner.json"), summaries, bestIdx);
                    WriteGalleryHtml(Path.Combine(sweepDir, "index.html"), summaries, bestIdx,
                        BuildRecommendation(summaries, bestIdx), reportsDir);
                }
                else
                {
                    // Best-effort: delete stale winner/index from earlier
                    // partial runs into the same sweepDir so downstream
                    // tooling doesn't pick them up.
                    try
                    {
                        string winnerPath = Path.Combine(sweepDir, "winner.json");
                        if (File.Exists(winnerPath)) File.Delete(winnerPath);
                        string idxPath = Path.Combine(sweepDir, "index.html");
                        if (File.Exists(idxPath)) File.Delete(idxPath);
                    }
                    catch { /* non-fatal */ }
                }
            }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] Failed to write summary: {ex.Message}");
                return;
            }

            if (!anyValid)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[Sweep] All {summaries.Count} cell(s) failed — no winner selected. " +
                    $"See {Path.Combine(sweepDir, "summary.csv")} for per-cell details.");
                return;
            }

            var w = summaries[bestIdx];
            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[Sweep] Winner: res={w.config.atlasRes}, pad={w.config.shellPad}, bdr={w.config.borderPad}, " +
                $"arap={(w.config.arapEnabled ? w.config.arapIterations : 0)}, " +
                $"stretchThr={w.config.stretchThreshold:F2}, os={w.config.internalOversample}, " +
                $"sym={w.config.symSplitMode} (score={w.score:F2})");
        }

        /// <summary>
        /// Weighted score: higher is better.
        /// <code>
        ///   score =   100 * atlasUtilization (mean across LODs)
        ///           - 50  * totalSlivers
        ///           - 10  * overlapShellPairs
        ///           - 50  * uv2DuplicatePairs       (silent symmetric-copy bleeding)
        ///           - 30  * force3DOverlapCount     (explicit bleeding warned by Transfer)
        ///           - 10  * compositeBrokenCount    (matching-quality signal)
        ///           - 20  * severeMismatchCount     (Phase 2 picked wrong source)
        ///           - 0.001 * totalMs               (per-millisecond penalty)
        ///           - 10  * log2(atlasRes / 256)    (prefer lower resolution if quality equal)
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
                 + kPenaltyDupUv2     * r.uv2DuplicatePairs
                 + kPenaltyForce3DOL  * r.force3DOverlapCount
                 + kPenaltyCompBroken * r.compositeBrokenCount
                 + kPenaltySevere     * r.severeMismatchCount
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
            var header = ParseCsvRow(lines[0]);
            int idx(string col)
            {
                for (int i = 0; i < header.Count; i++)
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
            int iShellsMatch = idx("shellsMatched");
            int iVertsXfer   = idx("verticesTransferred");
            int iDupUv2      = idx("uv2DuplicatePairs");
            int iForce3DOL   = idx("shellsOverlapFixed");
            int iCompBroken  = idx("compositeBrokenCount");
            int iSevere      = idx("severeMismatchCount");

            int slivers = 0, overlap = 0;
            int dupUv2 = 0, force3DOL = 0, compBroken = 0, severe = 0;
            int utilCount = 0;
            float utilSum = 0f;
            long totalMs = 0;
            bool stageRead = false;
            bool hadFailure = false;
            int targetRowCount = 0;

            var inv = CultureInfo.InvariantCulture;
            for (int li = 1; li < lines.Length; li++)
            {
                var line = lines[li];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var c = ParseCsvRow(line);
                if (c.Count < header.Count) continue;

                bool isSource = iIsSource >= 0 && c[iIsSource] == "1";
                // Only target-LOD rows carry meaningful validation. The
                // source LOD's validation report is the post-repack
                // self-check and would double-count slivers in the
                // score otherwise.
                if (!isSource)
                {
                    targetRowCount++;
                    slivers += SafeInt(c, iInverted)  + SafeInt(c, iStretched)
                             + SafeInt(c, iZero)     + SafeInt(c, iOob);
                    overlap += SafeInt(c, iOverlap);
                    dupUv2     += SafeInt(c, iDupUv2);
                    force3DOL  += SafeInt(c, iForce3DOL);
                    compBroken += SafeInt(c, iCompBroken);
                    severe     += SafeInt(c, iSevere);

                    // A target-LOD row with zero shells matched AND zero
                    // vertices transferred means the transfer never ran (or
                    // ran but produced nothing). Without this guard the run
                    // looks "clean" (0 slivers, 0 overlaps) and can win the
                    // sweep despite a broken transfer.
                    if (iShellsMatch >= 0 && iVertsXfer >= 0
                        && SafeInt(c, iShellsMatch) == 0
                        && SafeInt(c, iVertsXfer) == 0)
                    {
                        hadFailure = true;
                    }
                }

                // atlasUtilization: count every successfully parsed numeric
                // value, including 0. Dropping zeros (e.g. degenerate or
                // failed target outputs) used to inflate the mean by
                // omitting bad rows while keeping good ones — that promoted
                // partially-broken cells to winner. Skip only missing or
                // unparseable values.
                if (iAtlasUtil >= 0 && iAtlasUtil < c.Count
                    && float.TryParse(c[iAtlasUtil],
                        NumberStyles.Float, inv, out float util))
                {
                    utilSum += util;
                    utilCount++;
                }

                // Stage timings are session-level and repeat on every row;
                // read them once from the first non-empty record. pipelineMs
                // is the outermost wall-clock around ExecFullPipeline and
                // already contains repackMs + transferMs + validateMs, so
                // summing all four would triple-count inner work and
                // penalise sweep cells against the time-weighted score. For
                // standalone Repack/Transfer rows (no pipeline wrapper)
                // pipelineMs is 0, so fall back to the sum of inner stages.
                if (!stageRead)
                {
                    long pipe = SafeLong(c, iPipeline);
                    long inner = SafeLong(c, iRepack)
                               + SafeLong(c, iTransfer)
                               + SafeLong(c, iValidate);
                    totalMs = pipe > 0 ? pipe : inner;
                    stageRead = true;
                }
            }

            summary.totalSlivers         = slivers;
            summary.overlapShellPairs    = overlap;
            summary.uv2DuplicatePairs    = dupUv2;
            summary.force3DOverlapCount  = force3DOL;
            summary.compositeBrokenCount = compBroken;
            summary.severeMismatchCount  = severe;
            summary.meanAtlasUtilization = utilCount > 0 ? utilSum / utilCount : 0f;
            summary.totalMs              = totalMs;
            // No target-LOD rows means transfer never produced anything —
            // either the model is single-LOD or all target rows were
            // dropped. Either way the failure check inside the !isSource
            // branch never fires, so the run would otherwise pass scoring
            // with zeros across the board. Mark it failed explicitly.
            if (targetRowCount == 0) hadFailure = true;
            summary.hadFailure           = hadFailure;
            return summary;
        }

        /// <summary>
        /// Minimal CSV row parser that respects double-quoted fields (with
        /// <c>""</c> as an escaped quote inside a quoted field). Required
        /// because <see cref="BenchmarkRecorder"/> wraps values containing
        /// commas in quotes — a naive <c>line.Split(',')</c> would shift
        /// columns whenever a renderer name contains a comma, silently
        /// corrupting the winner picked by the aggregator.
        /// </summary>
        static List<string> ParseCsvRow(string line)
        {
            var cells = new List<string>();
            if (line == null) { cells.Add(""); return cells; }
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                    else if (c == '"' && sb.Length == 0) inQuotes = true;
                    else sb.Append(c);
                }
            }
            cells.Add(sb.ToString());
            return cells;
        }

        static int  SafeInt (List<string> c, int i)
            => (i >= 0 && i < c.Count && int.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) ? v : 0;
        static long SafeLong(List<string> c, int i)
            => (i >= 0 && i < c.Count && long.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) ? v : 0;

        internal static void WriteSummaryCsv(string path, List<RunSummary> runs, string sweepDir)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("atlasRes,shellPad,borderPad,arapEnabled,arapIterations,stretchThreshold," +
                          "internalOversample,symSplitMode," +
                          "totalSlivers,overlapShellPairs," +
                          "uv2DuplicatePairs,force3DOverlapCount,compositeBrokenCount,severeMismatchCount," +
                          "meanAtlasUtilization,totalMs,score,csvPath");
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
                sb.Append(r.config.arapEnabled ? '1' : '0').Append(',');
                sb.Append(r.config.arapIterations.ToString(inv)).Append(',');
                sb.Append(r.config.stretchThreshold.ToString("R", inv)).Append(',');
                sb.Append(r.config.internalOversample.ToString(inv)).Append(',');
                sb.Append(r.config.symSplitMode.ToString()).Append(',');
                sb.Append(r.totalSlivers.ToString(inv)).Append(',');
                sb.Append(r.overlapShellPairs.ToString(inv)).Append(',');
                sb.Append(r.uv2DuplicatePairs.ToString(inv)).Append(',');
                sb.Append(r.force3DOverlapCount.ToString(inv)).Append(',');
                sb.Append(r.compositeBrokenCount.ToString(inv)).Append(',');
                sb.Append(r.severeMismatchCount.ToString(inv)).Append(',');
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
            sb.Append("    \"arapEnabled\": ").Append(w.config.arapEnabled ? "true" : "false").Append(",\n");
            sb.Append("    \"arapIterations\": ").Append(w.config.arapIterations.ToString(inv)).Append(",\n");
            sb.Append("    \"stretchThreshold\": ").Append(w.config.stretchThreshold.ToString("R", inv)).Append(",\n");
            sb.Append("    \"internalOversample\": ").Append(w.config.internalOversample.ToString(inv)).Append(",\n");
            sb.Append("    \"symSplitMode\": ").Append(JsonString(w.config.symSplitMode.ToString())).Append(",\n");
            sb.Append("    \"score\": ").Append(JsonFloat(w.score, inv)).Append(",\n");
            sb.Append("    \"totalSlivers\": ").Append(w.totalSlivers.ToString(inv)).Append(",\n");
            sb.Append("    \"overlapShellPairs\": ").Append(w.overlapShellPairs.ToString(inv)).Append(",\n");
            sb.Append("    \"uv2DuplicatePairs\": ").Append(w.uv2DuplicatePairs.ToString(inv)).Append(",\n");
            sb.Append("    \"force3DOverlapCount\": ").Append(w.force3DOverlapCount.ToString(inv)).Append(",\n");
            sb.Append("    \"compositeBrokenCount\": ").Append(w.compositeBrokenCount.ToString(inv)).Append(",\n");
            sb.Append("    \"severeMismatchCount\": ").Append(w.severeMismatchCount.ToString(inv)).Append(",\n");
            sb.Append("    \"meanAtlasUtilization\": ").Append(JsonFloat(w.meanAtlasUtilization, inv)).Append(",\n");
            sb.Append("    \"totalMs\": ").Append(w.totalMs.ToString(inv)).Append(",\n");
            sb.Append("    \"csvPath\": ").Append(JsonString(w.csvPath ?? "")).Append("\n");
            sb.Append("  },\n");

            sb.Append("  \"recommendation\": ").Append(JsonString(BuildRecommendation(runs, bestIdx))).Append(",\n");

            sb.Append("  \"scoring\": {\n");
            sb.Append("    \"atlasUtilizationWeight\": ").Append(kWeightUtilization.ToString("R", inv)).Append(",\n");
            sb.Append("    \"sliverPenalty\": ").Append(kPenaltySliver.ToString("R", inv)).Append(",\n");
            sb.Append("    \"overlapPenalty\": ").Append(kPenaltyOverlap.ToString("R", inv)).Append(",\n");
            sb.Append("    \"dupUv2Penalty\": ").Append(kPenaltyDupUv2.ToString("R", inv)).Append(",\n");
            sb.Append("    \"force3DOverlapPenalty\": ").Append(kPenaltyForce3DOL.ToString("R", inv)).Append(",\n");
            sb.Append("    \"compositeBrokenPenalty\": ").Append(kPenaltyCompBroken.ToString("R", inv)).Append(",\n");
            sb.Append("    \"severeMismatchPenalty\": ").Append(kPenaltySevere.ToString("R", inv)).Append(",\n");
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
                sb.Append("\"arapEnabled\": ").Append(r.config.arapEnabled ? "true" : "false").Append(", ");
                sb.Append("\"arapIterations\": ").Append(r.config.arapIterations.ToString(inv)).Append(", ");
                sb.Append("\"stretchThreshold\": ").Append(r.config.stretchThreshold.ToString("R", inv)).Append(", ");
                sb.Append("\"internalOversample\": ").Append(r.config.internalOversample.ToString(inv)).Append(", ");
                sb.Append("\"symSplitMode\": ").Append(JsonString(r.config.symSplitMode.ToString())).Append(", ");
                sb.Append("\"totalSlivers\": ").Append(r.totalSlivers.ToString(inv)).Append(", ");
                sb.Append("\"overlapShellPairs\": ").Append(r.overlapShellPairs.ToString(inv)).Append(", ");
                sb.Append("\"uv2DuplicatePairs\": ").Append(r.uv2DuplicatePairs.ToString(inv)).Append(", ");
                sb.Append("\"force3DOverlapCount\": ").Append(r.force3DOverlapCount.ToString(inv)).Append(", ");
                sb.Append("\"compositeBrokenCount\": ").Append(r.compositeBrokenCount.ToString(inv)).Append(", ");
                sb.Append("\"severeMismatchCount\": ").Append(r.severeMismatchCount.ToString(inv)).Append(", ");
                sb.Append("\"meanAtlasUtilization\": ").Append(JsonFloat(r.meanAtlasUtilization, inv)).Append(", ");
                sb.Append("\"totalMs\": ").Append(r.totalMs.ToString(inv)).Append(", ");
                sb.Append("\"score\": ").Append(JsonFloat(r.score, inv)).Append(", ");
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
                              $"arap={(w.config.arapEnabled ? w.config.arapIterations : 0)}, " +
                              $"stretchThr={w.config.stretchThreshold.ToString("F2", inv)}, " +
                              $"os={w.config.internalOversample}, sym={w.config.symSplitMode}";
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
            sb.Append("        <th onclick=\"sortBy(4)\">arapEnabled</th>\n");
            sb.Append("        <th onclick=\"sortBy(5)\">arapIters</th>\n");
            sb.Append("        <th onclick=\"sortBy(6)\">stretchThr</th>\n");
            sb.Append("        <th onclick=\"sortBy(7)\" title=\"xatlas internal-oversample (1/2/4)\">os</th>\n");
            sb.Append("        <th onclick=\"sortBy(8)\" title=\"SymSplit threshold mode (LegacyFixed/Adaptive)\">symMode</th>\n");
            sb.Append("        <th onclick=\"sortBy(9)\">slivers</th>\n");
            sb.Append("        <th onclick=\"sortBy(10)\">overlap</th>\n");
            sb.Append("        <th onclick=\"sortBy(11)\" title=\"target shells with identical UV2 fingerprint\">dupUV2</th>\n");
            sb.Append("        <th onclick=\"sortBy(12)\" title=\"force3D shells overlapping non-fallback (bleeding)\">f3DOL</th>\n");
            sb.Append("        <th onclick=\"sortBy(13)\" title=\"composite spatially broken targets (Phase 3 fallback)\">compBr</th>\n");
            sb.Append("        <th onclick=\"sortBy(14)\" title=\"target shells with matchDist > 10% mesh diagonal\">severe</th>\n");
            sb.Append("        <th onclick=\"sortBy(15)\">atlas%</th>\n");
            sb.Append("        <th onclick=\"sortBy(16)\">ms</th>\n");
            sb.Append("        <th onclick=\"sortBy(17)\">score</th>\n");
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
                sb.Append("        <td>").Append(r.config.arapEnabled    ? "1" : "0").Append("</td>\n");
                sb.Append("        <td>").Append(r.config.arapIterations.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.config.stretchThreshold.ToString("F2", inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.config.internalOversample.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(HtmlEscape(r.config.symSplitMode.ToString())).Append("</td>\n");
                sb.Append("        <td>").Append(r.totalSlivers.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.overlapShellPairs.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.uv2DuplicatePairs.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.force3DOverlapCount.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.compositeBrokenCount.ToString(inv)).Append("</td>\n");
                sb.Append("        <td>").Append(r.severeMismatchCount.ToString(inv)).Append("</td>\n");
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
                // PNG dirs now live inside sweepDir alongside index.html,
                // so the relative link is just "<base>_png/<file>" with no
                // "../" prefix (see WriteAggregateReport comment).
                string rel = pngDirName + "/" + fileName;
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
        /// stretched-shell ARAP helped/didn't at this threshold".
        /// </summary>
        static string BuildRecommendation(List<RunSummary> runs, int bestIdx)
        {
            var w = runs[bestIdx];
            var sb = new StringBuilder();
            sb.Append("Use ").Append(w.config.atlasRes).Append(" resolution");
            sb.Append(", shellPad=").Append(w.config.shellPad);
            sb.Append(", borderPad=").Append(w.config.borderPad);
            sb.Append(" with stretched-shell ARAP ");
            sb.Append(w.config.arapEnabled
                ? $"ON ({w.config.arapIterations} iters, L²>{w.config.stretchThreshold.ToString("F2", CultureInfo.InvariantCulture)})"
                : "OFF");
            sb.Append('.');

            // Compare against the same config with ARAP toggled.
            bool? arapHelped = ComparePair(runs, w);
            if (arapHelped.HasValue)
                sb.Append(" Stretched-shell ARAP ")
                  .Append(arapHelped.Value ? "improved" : "did not improve")
                  .Append(" results on this asset.");
            return sb.ToString();
        }

        /// <summary>
        /// Looks up the run that shares the winner's resolution/padding and
        /// stretch threshold but has ARAP toggled the other way, then reports
        /// whether the winner's enabled flag out-scored its disabled counterpart.
        /// Returns null if the pair is not in the grid.
        /// </summary>
        static bool? ComparePair(List<RunSummary> runs, RunSummary w)
        {
            foreach (var r in runs)
            {
                if (r.config.atlasRes  != w.config.atlasRes)  continue;
                if (r.config.shellPad  != w.config.shellPad)  continue;
                if (r.config.borderPad != w.config.borderPad) continue;
                // All non-ARAP axes must match exactly so the score delta
                // isolates the ARAP toggle. Without this guard the "ARAP
                // improved / did not improve" sentence in winner.json can be
                // computed against a run that also differs in oversample or
                // symSplit mode — a confounded comparison.
                if (r.config.internalOversample != w.config.internalOversample) continue;
                if (r.config.symSplitMode       != w.config.symSplitMode)       continue;
                // Stretch threshold is irrelevant when ARAP is off — match the
                // winner's threshold on the ON side and ignore it on the OFF
                // side. Either way, the pair is "this config with ARAP toggled".
                if (w.config.arapEnabled && r.config.arapEnabled &&
                    !Mathf.Approximately(r.config.stretchThreshold, w.config.stretchThreshold))
                    continue;
                if (r.config.arapEnabled == w.config.arapEnabled) continue;

                bool winnerOn = w.config.arapEnabled;
                bool winnerBetter = w.score > r.score;
                return winnerOn ? winnerBetter : !winnerBetter;
            }
            return null;
        }

        /// <summary>
        /// Recovery utility: reconstructs a sweep_recovered_&lt;timestamp&gt;/
        /// report from the per-cell CSVs already present in a
        /// <c>BenchmarkReports/</c> directory after a mid-sweep Unity crash
        /// destroyed the in-memory aligned lists. The method:
        /// <list type="number">
        /// <item>scans non-recursively for <c>*.csv</c> files at the root,</item>
        /// <item>parses <c>res{R}_pad{S}_bdr{B}_psa{P}_arap{A}</c> tokens from
        ///   each filename and skips any CSV that doesn't match (so existing
        ///   sweep_*/summary.csv files are ignored),</item>
        /// <item>clusters the matched CSVs by file mtime — each cluster has
        ///   gaps no larger than <c>kRecoveryGapSeconds</c> between adjacent
        ///   files when sorted by time,</item>
        /// <item>picks the largest cluster as the crashed sweep,</item>
        /// <item>writes summary.csv / winner.json / index.html into a new
        ///   <c>sweep_recovered_&lt;UTCstamp&gt;/</c> sibling folder.</item>
        /// </list>
        /// Returns the absolute path of the created sweep folder, or null on
        /// failure (no matching CSVs, I/O error, etc.).
        /// </summary>
        internal static string RebuildFromExistingCsvs(string benchmarkReportsRoot)
        {
            if (string.IsNullOrEmpty(benchmarkReportsRoot) || !Directory.Exists(benchmarkReportsRoot))
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[Sweep] Recovery: directory not found: {benchmarkReportsRoot}");
                return null;
            }

            string[] csvFiles;
            try { csvFiles = Directory.GetFiles(benchmarkReportsRoot, "*.csv", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark,
                    $"[Sweep] Recovery: failed to enumerate CSVs: {ex.Message}");
                return null;
            }
            if (csvFiles == null || csvFiles.Length == 0)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[Sweep] Recovery: no CSVs found in {benchmarkReportsRoot}");
                return null;
            }

            // The stretch threshold is encoded as "<int>p<2-digit>" in the
            // label (e.g. 1.50 → "1p50") because Sanitize() collapses '.' to
            // '_'. We split it back into a float here. The optional
            // `(?:_asp[01])?` tail keeps old per-cell CSVs from the era of the
            // removed global-aspect normalize pass parseable. The optional
            // `_os(\d+)_sym(legacy|adaptive)` tail is the new axis pair added
            // alongside arapIterations / stretchThreshold; pre-existing CSVs
            // without these tokens default to oversample=4 (current default)
            // and symMode=LegacyFixed (current default).
            var rx = new System.Text.RegularExpressions.Regex(
                @"_sweep_res(\d+)_pad(\d+)_bdr(\d+)_arap(\d+)_stretch(\d+)p(\d+)" +
                @"(?:_asp[01])?(?:_os(\d+))?(?:_sym(legacy|adaptive))?_",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            var matched = new List<(string path, CellConfig cfg, DateTime mtime)>();
            foreach (string csv in csvFiles)
            {
                string name = Path.GetFileName(csv);
                var m = rx.Match(name);
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int res)) continue;
                if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pad)) continue;
                if (!int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bdr)) continue;
                if (!int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int arapIters)) continue;
                // Reassemble float "<int>p<frac>" → <int>.<frac>
                string stretchStr = m.Groups[5].Value + "." + m.Groups[6].Value;
                if (!float.TryParse(stretchStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float stretchThr))
                    stretchThr = 1.5f;
                int oversample = 4;
                if (m.Groups[7].Success
                    && int.TryParse(m.Groups[7].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedOs)
                    && parsedOs > 0)
                {
                    oversample = parsedOs;
                }
                var symMode = SymmetrySplitShells.ThresholdMode.LegacyFixed;
                if (m.Groups[8].Success && m.Groups[8].Value == "adaptive")
                    symMode = SymmetrySplitShells.ThresholdMode.Adaptive;

                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(csv); }
                catch { mtime = DateTime.UtcNow; }

                matched.Add((csv, new CellConfig
                {
                    atlasRes           = res,
                    shellPad           = pad,
                    borderPad          = bdr,
                    arapEnabled        = arapIters > 0,
                    arapIterations     = arapIters,
                    stretchThreshold   = stretchThr,
                    internalOversample = oversample,
                    symSplitMode       = symMode,
                }, mtime));
            }

            if (matched.Count == 0)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[Sweep] Recovery: no CSV matched the sweep_resR_padS_bdrB_arapA_stretchT pattern in {benchmarkReportsRoot}");
                return null;
            }

            // Cluster by mtime gaps. Files belonging to a single sweep are
            // written back-to-back; a gap larger than kRecoveryGapSeconds is
            // treated as a sweep boundary.
            matched.Sort((a, b) => a.mtime.CompareTo(b.mtime));
            var clusters = new List<List<(string path, CellConfig cfg, DateTime mtime)>>();
            var current  = new List<(string path, CellConfig cfg, DateTime mtime)> { matched[0] };
            for (int i = 1; i < matched.Count; i++)
            {
                double gap = (matched[i].mtime - matched[i - 1].mtime).TotalSeconds;
                if (gap > kRecoveryGapSeconds)
                {
                    clusters.Add(current);
                    current = new List<(string path, CellConfig cfg, DateTime mtime)>();
                }
                current.Add(matched[i]);
            }
            clusters.Add(current);

            // Largest cluster = the sweep the user wants to recover.
            int bestC = 0;
            for (int i = 1; i < clusters.Count; i++)
                if (clusters[i].Count > clusters[bestC].Count) bestC = i;
            var chosen = clusters[bestC];

            var csvPaths = new List<string>(chosen.Count);
            var configs  = new List<CellConfig>(chosen.Count);
            foreach (var t in chosen) { csvPaths.Add(t.path); configs.Add(t.cfg); }

            string recoveryStamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string sweepDir = Path.Combine(benchmarkReportsRoot, $"sweep_recovered_{recoveryStamp}");
            try { Directory.CreateDirectory(sweepDir); }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark,
                    $"[Sweep] Recovery: failed to create {sweepDir}: {ex.Message}");
                return null;
            }

            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[Sweep] Recovery: rebuilding {chosen.Count} cells (from {clusters.Count} cluster(s), " +
                $"largest selected) into {sweepDir}");

            try
            {
                WriteAggregateReport(csvPaths, configs, sweepDir);
            }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark,
                    $"[Sweep] Recovery: aggregate write failed: {ex.Message}");
                return null;
            }
            return sweepDir;
        }

        // Files belonging to a single sweep are written back-to-back by the
        // pipeline. 5 minutes is generous: even a slow cell finishes well under
        // that, but it's large enough to absorb GC pauses, AssetDatabase
        // refreshes, or progress-bar idle gaps between cells.
        const double kRecoveryGapSeconds = 300.0;

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

        /// <summary>
        /// Format a float for JSON output. <c>NaN</c> / <c>±Infinity</c> are
        /// not valid JSON numbers, so emit <c>"null"</c> for those cases —
        /// failed sweep runs land on <c>float.NegativeInfinity</c> via
        /// <see cref="Score"/>, and a downstream parser would otherwise fail.
        /// </summary>
        static string JsonFloat(float v, CultureInfo inv)
            => (float.IsNaN(v) || float.IsInfinity(v)) ? "null" : v.ToString("R", inv);

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

        // ─────────────────────────────────────────────────────────────
        //  Provenance manifest + ZIP archive
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Identifying metadata recorded alongside a sweep so a CSV looked at
        /// six months from now is still traceable: which package version, which
        /// commit, which Unity, which host, which matrix produced it.
        /// </summary>
        internal struct SweepManifest
        {
            public string sweepDir;          // absolute path
            public string sweepStamp;        // 20260514_113158_083 — matches dir name
            public string packageName;
            public string packageVersion;
            public string gitSha;
            public string gitBranch;
            public bool   gitDirty;
            public string unityVersion;
            public string platform;
            public string hostUser;
            public string hostMachine;
            public string hostOs;
            public string processor;
            public int    cellCount;
            public int    caseCount;
            public string sweepLabel;        // single-model lodGroup name OR multi-case suite name
            public TestSuiteAsset.SweepMatrix matrix;
            public List<string> caseLabels;  // null for single-model run
        }

        /// <summary>
        /// Write <c>manifest.json</c> into the sweep directory with package /
        /// Unity / host metadata so old sweep archives can be diff'd against
        /// new ones without ambiguity about what produced them.
        /// </summary>
        internal static void WriteManifest(string sweepDir, SweepManifest m)
        {
            if (string.IsNullOrEmpty(sweepDir) || !Directory.Exists(sweepDir)) return;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"sweepStamp\": ").Append(JsonString(m.sweepStamp ?? "")).Append(",\n");
            sb.Append("  \"sweepLabel\": ").Append(JsonString(m.sweepLabel ?? "")).Append(",\n");
            sb.Append("  \"createdUtc\": ").Append(JsonString(DateTime.UtcNow.ToString("o", inv))).Append(",\n");

            sb.Append("  \"package\": {\n");
            sb.Append("    \"name\": ").Append(JsonString(m.packageName ?? "")).Append(",\n");
            sb.Append("    \"version\": ").Append(JsonString(m.packageVersion ?? "")).Append(",\n");
            sb.Append("    \"gitSha\": ").Append(JsonString(m.gitSha ?? "")).Append(",\n");
            sb.Append("    \"gitBranch\": ").Append(JsonString(m.gitBranch ?? "")).Append(",\n");
            sb.Append("    \"gitDirty\": ").Append(m.gitDirty ? "true" : "false").Append("\n");
            sb.Append("  },\n");

            sb.Append("  \"unity\": {\n");
            sb.Append("    \"version\": ").Append(JsonString(m.unityVersion ?? "")).Append(",\n");
            sb.Append("    \"platform\": ").Append(JsonString(m.platform ?? "")).Append("\n");
            sb.Append("  },\n");

            sb.Append("  \"host\": {\n");
            sb.Append("    \"user\": ").Append(JsonString(m.hostUser ?? "")).Append(",\n");
            sb.Append("    \"machine\": ").Append(JsonString(m.hostMachine ?? "")).Append(",\n");
            sb.Append("    \"os\": ").Append(JsonString(m.hostOs ?? "")).Append(",\n");
            sb.Append("    \"processor\": ").Append(JsonString(m.processor ?? "")).Append("\n");
            sb.Append("  },\n");

            sb.Append("  \"sweep\": {\n");
            sb.Append("    \"cellCount\": ").Append(m.cellCount.ToString(inv)).Append(",\n");
            sb.Append("    \"caseCount\": ").Append(m.caseCount.ToString(inv));
            if (m.caseLabels != null && m.caseLabels.Count > 0)
            {
                sb.Append(",\n    \"caseLabels\": [");
                for (int i = 0; i < m.caseLabels.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(JsonString(m.caseLabels[i] ?? ""));
                }
                sb.Append("]");
            }
            if (m.matrix != null)
            {
                sb.Append(",\n    \"matrix\": {\n");
                AppendIntArray(sb, "      ", "atlasResolutions",          m.matrix.atlasResolutions);          sb.Append(",\n");
                AppendIntArray(sb, "      ", "shellPaddingPxVariants",    m.matrix.shellPaddingPxVariants);    sb.Append(",\n");
                AppendIntArray(sb, "      ", "borderPaddingPxVariants",   m.matrix.borderPaddingPxVariants);   sb.Append(",\n");
                AppendIntArray(sb, "      ", "arapIterationsVariants",    m.matrix.arapIterationsVariants);    sb.Append(",\n");
                AppendFloatArray(sb, "      ", "stretchThresholdVariants",m.matrix.stretchThresholdVariants);  sb.Append(",\n");
                AppendIntArray(sb, "      ", "internalOversampleVariants",m.matrix.internalOversampleVariants);sb.Append(",\n");
                sb.Append("      \"symSplitThresholdModeVariants\": [");
                if (m.matrix.symSplitThresholdModeVariants != null)
                {
                    for (int i = 0; i < m.matrix.symSplitThresholdModeVariants.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(JsonString(m.matrix.symSplitThresholdModeVariants[i].ToString()));
                    }
                }
                sb.Append("]\n    }");
            }
            sb.Append(",\n    \"scoringWeights\": {\n");
            sb.Append("      \"atlasUtilization\": ").Append(kWeightUtilization.ToString("R", inv)).Append(",\n");
            sb.Append("      \"sliver\": ").Append(kPenaltySliver.ToString("R", inv)).Append(",\n");
            sb.Append("      \"overlap\": ").Append(kPenaltyOverlap.ToString("R", inv)).Append(",\n");
            sb.Append("      \"dupUv2\": ").Append(kPenaltyDupUv2.ToString("R", inv)).Append(",\n");
            sb.Append("      \"force3DOverlap\": ").Append(kPenaltyForce3DOL.ToString("R", inv)).Append(",\n");
            sb.Append("      \"compositeBroken\": ").Append(kPenaltyCompBroken.ToString("R", inv)).Append(",\n");
            sb.Append("      \"severeMismatch\": ").Append(kPenaltySevere.ToString("R", inv)).Append(",\n");
            sb.Append("      \"ms\": ").Append(kPenaltyMs.ToString("R", inv)).Append(",\n");
            sb.Append("      \"resolution\": ").Append(kPenaltyResolution.ToString("R", inv)).Append("\n");
            sb.Append("    }\n");
            sb.Append("  }\n");
            sb.Append("}\n");

            string path = Path.Combine(sweepDir, "manifest.json");
            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                UvtLog.Info(UvtLog.Category.Benchmark, $"[Sweep] manifest → {path}");
            }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark, $"[Sweep] manifest write failed: {ex.Message}");
            }
        }

        static void AppendIntArray(StringBuilder sb, string indent, string key, int[] arr)
        {
            sb.Append(indent).Append('"').Append(key).Append("\": [");
            if (arr != null)
            {
                var inv = CultureInfo.InvariantCulture;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(arr[i].ToString(inv));
                }
            }
            sb.Append("]");
        }
        static void AppendFloatArray(StringBuilder sb, string indent, string key, float[] arr)
        {
            sb.Append(indent).Append('"').Append(key).Append("\": [");
            if (arr != null)
            {
                var inv = CultureInfo.InvariantCulture;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(arr[i].ToString("R", inv));
                }
            }
            sb.Append("]");
        }

        /// <summary>
        /// Resolve UPM package + git provenance for the current build. Falls
        /// back gracefully when git is unavailable (e.g. asset-store install or
        /// CI runner without .git) so sweep continues to write a manifest with
        /// whatever fields could be determined.
        /// </summary>
        internal static (string pkgName, string pkgVersion, string gitSha, string gitBranch, bool gitDirty)
            ResolvePackageProvenance()
        {
            string pkgName = "", pkgVersion = "";
            try
            {
                var info = PackageInfo.FindForAssembly(typeof(BenchmarkSweep).Assembly);
                if (info != null) { pkgName = info.name ?? ""; pkgVersion = info.version ?? ""; }
            }
            catch (Exception ex)
            {
                UvtLog.Verbose(UvtLog.Category.Benchmark, $"[Sweep] PackageInfo failed: {ex.Message}");
            }

            // Best-effort git provenance. Runs `git rev-parse HEAD` etc. via
            // System.Diagnostics.Process. Anything that throws → blank field;
            // we never want manifest writing to take down a sweep.
            string sha = TryRunGit("rev-parse HEAD");
            string branch = TryRunGit("rev-parse --abbrev-ref HEAD");
            string status = TryRunGit("status --porcelain");
            bool dirty = !string.IsNullOrEmpty(status);

            return (pkgName, pkgVersion, sha, branch, dirty);
        }

        static string TryRunGit(string args)
        {
            try
            {
                string repoRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                // Resolve the package's actual source dir — manifests should
                // reflect the package commit, not the consuming project. For
                // local file: installs this is the package folder; for git URL
                // installs Library/PackageCache/<name>@<hash> doesn't have .git
                // so commands return blank, which is fine.
                string pkgDir = null;
                try
                {
                    var info = PackageInfo.FindForAssembly(typeof(BenchmarkSweep).Assembly);
                    if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                        pkgDir = info.resolvedPath;
                }
                catch { /* fall through */ }
                string workDir = !string.IsNullOrEmpty(pkgDir) ? pkgDir : repoRoot;

                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    WorkingDirectory  = workDir,
                    UseShellExecute   = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow    = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return "";
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                return (stdout ?? "").Trim();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Snapshot the just-written sweep directory into a single .zip under
        /// <c>BenchmarkReports/Archive/&lt;sweepDirName&gt;.zip</c> so old runs
        /// stay organised even after dozens of new sweeps accumulate. The zip
        /// is non-destructive — the source directory is left in place so the
        /// operator can keep iterating; an external sync target (Drive, Dropbox)
        /// can mirror the Archive/ folder. Returns the zip path or null on
        /// failure. Failures only warn, never throw — losing the archive is
        /// strictly worse than losing the manifest only.
        /// </summary>
        internal static string ArchiveSweep(string sweepDir)
        {
            if (string.IsNullOrEmpty(sweepDir) || !Directory.Exists(sweepDir)) return null;
            try
            {
                string benchRoot = Directory.GetParent(sweepDir)?.FullName;
                if (string.IsNullOrEmpty(benchRoot)) return null;
                string archiveDir = Path.Combine(benchRoot, "Archive");
                Directory.CreateDirectory(archiveDir);
                string zipName = Path.GetFileName(sweepDir) + ".zip";
                string zipPath = Path.Combine(archiveDir, zipName);
                // Overwrite any prior zip for this stamp (sweep dir may have
                // been re-aggregated incrementally during a long run).
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(sweepDir, zipPath, CompressionLevel.Optimal,
                    includeBaseDirectory: true);
                UvtLog.Info(UvtLog.Category.Benchmark, $"[Sweep] archived → {zipPath}");
                return zipPath;
            }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark, $"[Sweep] archive failed: {ex.Message}");
                return null;
            }
        }
    }
}
