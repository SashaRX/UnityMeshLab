// BenchmarkRecorder.cs — Machine-readable metrics capture for transfer pipeline runs.
// Wrapped around ExecFullPipeline / ExecRepack / ExecTransferAll; writes CSV + JSON
// into <projectRoot>/BenchmarkReports/ on Dispose. See TRANSFER_BENCHMARK.md.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Collects per-mesh metrics during a pipeline run and writes CSV + JSON on Dispose.
    /// Construct via <see cref="NewRun"/>; wrap the run in <c>using(...)</c>.
    /// </summary>
    public sealed class BenchmarkRecorder : IDisposable
    {
        public static BenchmarkRecorder Current { get; private set; }

        // Sentinel for nested calls — caller treats it as a scope that does nothing on Dispose.
        sealed class NoOpScope : IDisposable { public static readonly NoOpScope Instance = new NoOpScope(); public void Dispose() { } }

        // ── Session state ──
        readonly string runLabel;
        readonly string lodGroupName;
        readonly string modeTag;
        readonly DateTime startedAtUtc;
        readonly Stopwatch wallClock = Stopwatch.StartNew();

        // Per-stage timings (pipelineMs / repackMs / transferMs / validateMs)
        readonly Dictionary<string, Stopwatch> stageTimers = new Dictionary<string, Stopwatch>();
        readonly Dictionary<string, long> stageAccum = new Dictionary<string, long>();

        // Pipeline-wide metrics
        int preRepackOverlaps   = -1;
        int postRepackOverlaps  = -1;
        int symSplitFallbackAt0 = -1;
        int symSplitTotalAt0    = -1;

        // Session config snapshot
        readonly string symSplitMode;
        readonly bool   repackPerMesh;
        readonly bool   splitTargets;
        readonly int    atlasResolution;
        readonly int    shellPad;
        readonly int    borderPad;
        readonly int    sourceLodIndex;

        // Per-mesh records (one row per recorded mesh)
        readonly List<RunRecord> records = new List<RunRecord>();

        BenchmarkRecorder(UvToolContext ctx, string label, bool splitTargetsFlag,
            SymmetrySplitShells.ThresholdMode symMode)
        {
            runLabel        = string.IsNullOrEmpty(label) ? "run" : Sanitize(label);
            lodGroupName    = ctx?.LodGroup != null ? Sanitize(ctx.LodGroup.name) : "standalone";
            symSplitMode    = symMode.ToString();
            repackPerMesh   = ctx?.RepackPerMesh ?? false;
            splitTargets    = splitTargetsFlag;
            atlasResolution = ctx?.AtlasResolution ?? 0;
            shellPad        = ctx?.ShellPaddingPx ?? 0;
            borderPad       = ctx?.BorderPaddingPx ?? 0;
            sourceLodIndex  = ctx?.SourceLodIndex ?? 0;
            modeTag         = $"{symSplitMode}{(repackPerMesh ? "-perMesh" : "")}{(splitTargets ? "-splitTgt" : "")}";
            startedAtUtc    = DateTime.UtcNow;

            // Reset volatile counters so values in this run only reflect this run.
            SymmetrySplitShells.LastFallbackCount = 0;
            SymmetrySplitShells.LastTotalSplitCount = 0;
            symSplitFallbackAt0 = 0;
            symSplitTotalAt0    = 0;
        }

        /// <summary>
        /// Begin a new recording session. Nested calls are no-ops — the outer session
        /// captures everything and the inner caller gets a scope whose Dispose does nothing.
        /// Always call inside `using (BenchmarkRecorder.NewRun(...)) { ... }`.
        /// </summary>
        public static IDisposable NewRun(UvToolContext ctx, string label,
            bool splitTargets, SymmetrySplitShells.ThresholdMode symMode)
        {
            if (Current != null) return NoOpScope.Instance;
            Current = new BenchmarkRecorder(ctx, label, splitTargets, symMode);
            return Current;
        }

        // ── Stage timing ──
        public void StageBegin(string stage)
        {
            if (!stageTimers.TryGetValue(stage, out var sw))
            {
                sw = new Stopwatch();
                stageTimers[stage] = sw;
            }
            sw.Restart();
        }

        public void StageEnd(string stage)
        {
            if (stageTimers.TryGetValue(stage, out var sw))
            {
                sw.Stop();
                stageAccum.TryGetValue(stage, out var accum);
                stageAccum[stage] = accum + sw.ElapsedMilliseconds;
            }
        }

        // ── Coarse metrics ──
        public void SetPreRepackOverlaps(int count)  => preRepackOverlaps = count;
        public void SetPostRepackOverlaps(int count) => postRepackOverlaps = count;

        /// <summary>
        /// Capture per-mesh record: TransferResult, ValidationReport, and a snapshot
        /// of the volatile SymSplit/Topology counters. Call once per target mesh after
        /// validation has populated <see cref="MeshEntry.validationReport"/>.
        /// Also snapshots UV2 + triangles so WriteArtefacts can dump a PNG per mesh.
        /// </summary>
        public void RecordMesh(MeshEntry entry)
        {
            if (entry == null || entry.renderer == null) return;

            var tr  = entry.shellTransferResult;
            var vr  = entry.validationReport;

            // Pick the mesh whose UV2 reflects the pipeline result:
            //   source LOD  → repackedMesh if present
            //   target LOD  → transferredMesh if present
            // Fall back to originalMesh if neither exists (bare re-run).
            Mesh snapshotMesh =
                (entry.lodIndex == sourceLodIndex ? entry.repackedMesh : entry.transferredMesh)
                ?? entry.originalMesh;

            Vector2[] uv2Snap = null;
            int[] trisSnap = null;
            if (snapshotMesh != null)
            {
                var list = new System.Collections.Generic.List<Vector2>();
                snapshotMesh.GetUVs(1, list);
                if (list.Count > 0)
                {
                    uv2Snap = list.ToArray();
                    trisSnap = snapshotMesh.triangles;
                }
            }

            var rec = new RunRecord
            {
                timestamp       = DateTime.UtcNow,
                rendererName    = entry.renderer.name,
                meshGroupKey    = entry.meshGroupKey ?? "",
                lodIndex        = entry.lodIndex,
                isSourceLod     = entry.lodIndex == sourceLodIndex,
                shellsMatched   = tr?.shellsMatched     ?? 0,
                shellsUnmatched = tr?.shellsUnmatched   ?? 0,
                shellsTransform = tr?.shellsTransform   ?? 0,
                shellsInterpolation = tr?.shellsInterpolation ?? 0,
                shellsMerged    = tr?.shellsMerged      ?? 0,
                shellsRejected  = tr?.shellsRejected    ?? 0,
                shellsOverlapFixed = tr?.shellsOverlapFixed ?? 0,
                dedupConflicts  = tr?.dedupConflicts    ?? 0,
                fragmentsMerged = tr?.fragmentsMerged   ?? 0,
                consistencyCorrected = tr?.consistencyCorrected ?? 0,
                verticesTransferred = tr?.verticesTransferred ?? 0,
                verticesTotal       = tr?.verticesTotal       ?? 0,

                invertedCount       = vr?.invertedCount       ?? 0,
                stretchedCount      = vr?.stretchedCount      ?? 0,
                zeroAreaCount       = vr?.zeroAreaCount       ?? 0,
                oobCount            = vr?.oobCount            ?? 0,
                cleanCount          = vr?.cleanCount          ?? 0,
                overlapShellPairs   = vr?.overlapShellPairs   ?? 0,
                overlapTriangleCount= vr?.overlapTriangleCount?? 0,
                overlapSameSrcPairs = vr?.overlapSameSrcPairs ?? 0,
                texelDensityBadCount= vr?.texelDensityBadCount?? 0,
                texelDensityMedian  = vr?.texelDensityMedian  ?? 0f,

                symSplitFallbackCount = SymmetrySplitShells.LastFallbackCount - symSplitFallbackAt0,
                symSplitTotalCount    = SymmetrySplitShells.LastTotalSplitCount - symSplitTotalAt0,
                topologyIterations    = GroupedShellTransfer.LastTopologyIterations,
                topologyFixed         = GroupedShellTransfer.LastTopologyFixed,
                topologyCapHit        = GroupedShellTransfer.LastTopologyCapHit,

                uv2Snapshot       = uv2Snap,
                trianglesSnapshot = trisSnap,
            };
            records.Add(rec);
        }

        // ── Dispose writes artefacts ──
        public void Dispose()
        {
            if (Current != this) return; // already finalized
            wallClock.Stop();
            try
            {
                WriteArtefacts();
            }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark, $"Failed to write report: {ex.Message}");
            }
            finally
            {
                Current = null;
            }
        }

        void WriteArtefacts()
        {
            // Only emit artefacts for runs that produced per-mesh data.
            // Bare repack/transfer runs without RecordMesh calls aren't worth a file.
            if (records.Count == 0) return;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string dir = Path.Combine(projectRoot, "BenchmarkReports");
            Directory.CreateDirectory(dir);

            string stamp = startedAtUtc.ToString("yyyyMMdd_HHmmss");
            string fileBase = $"{stamp}_{lodGroupName}_{runLabel}_{Sanitize(modeTag)}";
            string csvPath = Path.Combine(dir, fileBase + ".csv");
            string jsonPath = Path.Combine(dir, fileBase + ".json");

            File.WriteAllText(csvPath,  BuildCsv(),  Encoding.UTF8);
            File.WriteAllText(jsonPath, BuildJson(), Encoding.UTF8);

            // Per-mesh UV2 snapshots, one PNG per recorded mesh.
            int pngCount = 0;
            string pngDir = Path.Combine(dir, fileBase + "_png");
            foreach (var r in records)
            {
                if (r.uv2Snapshot == null || r.trianglesSnapshot == null) continue;
                string pngName = Sanitize(r.rendererName) + $"_LOD{r.lodIndex}_uv2.png";
                if (UvPngWriter.Render(Path.Combine(pngDir, pngName),
                        r.uv2Snapshot, r.trianglesSnapshot))
                    pngCount++;
            }

            UvtLog.Info(UvtLog.Category.Benchmark,
                $"saved {records.Count} rec(s){(pngCount > 0 ? $" + {pngCount} PNG" : "")} → {csvPath}");
        }

        string BuildCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("timestamp,runLabel,lodGroup,symSplitMode,repackPerMesh,splitTargets," +
                "atlasRes,shellPad,borderPad,sourceLod," +
                "rendererName,meshGroupKey,lodIndex,isSourceLod," +
                "shellsMatched,shellsUnmatched,shellsTransform,shellsInterpolation,shellsMerged," +
                "shellsRejected,shellsOverlapFixed,dedupConflicts,fragmentsMerged,consistencyCorrected," +
                "verticesTransferred,verticesTotal," +
                "invertedCount,stretchedCount,zeroAreaCount,oobCount,cleanCount," +
                "overlapShellPairs,overlapTriangleCount,overlapSameSrcPairs," +
                "texelDensityBadCount,texelDensityMedian," +
                "symSplitFallbackCount,symSplitTotalCount," +
                "topologyIterations,topologyFixed,topologyCapHit," +
                "preRepackOverlaps,postRepackOverlaps," +
                "pipelineMs,repackMs,transferMs,validateMs");

            long pipelineMs = stageAccum.TryGetValue("pipeline",  out var pm) ? pm : 0;
            long repackMs   = stageAccum.TryGetValue("repack",    out var rm) ? rm : 0;
            long transferMs = stageAccum.TryGetValue("transfer",  out var tm) ? tm : 0;
            long validateMs = stageAccum.TryGetValue("validate",  out var vm) ? vm : 0;

            var inv = CultureInfo.InvariantCulture;
            foreach (var r in records)
            {
                sb.Append(r.timestamp.ToString("o", inv)).Append(',');
                sb.Append(Csv(runLabel)).Append(',');
                sb.Append(Csv(lodGroupName)).Append(',');
                sb.Append(Csv(symSplitMode)).Append(',');
                sb.Append(repackPerMesh ? '1' : '0').Append(',');
                sb.Append(splitTargets  ? '1' : '0').Append(',');
                sb.Append(atlasResolution.ToString(inv)).Append(',');
                sb.Append(shellPad.ToString(inv)).Append(',');
                sb.Append(borderPad.ToString(inv)).Append(',');
                sb.Append(sourceLodIndex.ToString(inv)).Append(',');
                sb.Append(Csv(r.rendererName)).Append(',');
                sb.Append(Csv(r.meshGroupKey)).Append(',');
                sb.Append(r.lodIndex.ToString(inv)).Append(',');
                sb.Append(r.isSourceLod ? '1' : '0').Append(',');
                sb.Append(r.shellsMatched.ToString(inv)).Append(',');
                sb.Append(r.shellsUnmatched.ToString(inv)).Append(',');
                sb.Append(r.shellsTransform.ToString(inv)).Append(',');
                sb.Append(r.shellsInterpolation.ToString(inv)).Append(',');
                sb.Append(r.shellsMerged.ToString(inv)).Append(',');
                sb.Append(r.shellsRejected.ToString(inv)).Append(',');
                sb.Append(r.shellsOverlapFixed.ToString(inv)).Append(',');
                sb.Append(r.dedupConflicts.ToString(inv)).Append(',');
                sb.Append(r.fragmentsMerged.ToString(inv)).Append(',');
                sb.Append(r.consistencyCorrected.ToString(inv)).Append(',');
                sb.Append(r.verticesTransferred.ToString(inv)).Append(',');
                sb.Append(r.verticesTotal.ToString(inv)).Append(',');
                sb.Append(r.invertedCount.ToString(inv)).Append(',');
                sb.Append(r.stretchedCount.ToString(inv)).Append(',');
                sb.Append(r.zeroAreaCount.ToString(inv)).Append(',');
                sb.Append(r.oobCount.ToString(inv)).Append(',');
                sb.Append(r.cleanCount.ToString(inv)).Append(',');
                sb.Append(r.overlapShellPairs.ToString(inv)).Append(',');
                sb.Append(r.overlapTriangleCount.ToString(inv)).Append(',');
                sb.Append(r.overlapSameSrcPairs.ToString(inv)).Append(',');
                sb.Append(r.texelDensityBadCount.ToString(inv)).Append(',');
                sb.Append(r.texelDensityMedian.ToString("R", inv)).Append(',');
                sb.Append(r.symSplitFallbackCount.ToString(inv)).Append(',');
                sb.Append(r.symSplitTotalCount.ToString(inv)).Append(',');
                sb.Append(r.topologyIterations.ToString(inv)).Append(',');
                sb.Append(r.topologyFixed.ToString(inv)).Append(',');
                sb.Append(r.topologyCapHit ? '1' : '0').Append(',');
                sb.Append(preRepackOverlaps.ToString(inv)).Append(',');
                sb.Append(postRepackOverlaps.ToString(inv)).Append(',');
                sb.Append(pipelineMs.ToString(inv)).Append(',');
                sb.Append(repackMs.ToString(inv)).Append(',');
                sb.Append(transferMs.ToString(inv)).Append(',');
                sb.Append(validateMs.ToString(inv));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        string BuildJson()
        {
            long pipelineMs = stageAccum.TryGetValue("pipeline",  out var pm) ? pm : 0;
            long repackMs   = stageAccum.TryGetValue("repack",    out var rm) ? rm : 0;
            long transferMs = stageAccum.TryGetValue("transfer",  out var tm) ? tm : 0;
            long validateMs = stageAccum.TryGetValue("validate",  out var vm) ? vm : 0;

            var sb = new StringBuilder();
            sb.Append("{\n");
            AppendJsonKv(sb, "startedAtUtc", startedAtUtc.ToString("o", CultureInfo.InvariantCulture)); sb.Append(",\n");
            AppendJsonKv(sb, "runLabel",     runLabel);     sb.Append(",\n");
            AppendJsonKv(sb, "lodGroup",     lodGroupName); sb.Append(",\n");
            AppendJsonKv(sb, "symSplitMode", symSplitMode); sb.Append(",\n");
            AppendJsonKv(sb, "repackPerMesh", repackPerMesh); sb.Append(",\n");
            AppendJsonKv(sb, "splitTargets",  splitTargets);  sb.Append(",\n");
            AppendJsonKv(sb, "atlasResolution", atlasResolution); sb.Append(",\n");
            AppendJsonKv(sb, "shellPad",  shellPad);  sb.Append(",\n");
            AppendJsonKv(sb, "borderPad", borderPad); sb.Append(",\n");
            AppendJsonKv(sb, "sourceLodIndex", sourceLodIndex); sb.Append(",\n");
            AppendJsonKv(sb, "preRepackOverlaps",  preRepackOverlaps);  sb.Append(",\n");
            AppendJsonKv(sb, "postRepackOverlaps", postRepackOverlaps); sb.Append(",\n");
            AppendJsonKv(sb, "pipelineMs", pipelineMs); sb.Append(",\n");
            AppendJsonKv(sb, "repackMs",   repackMs);   sb.Append(",\n");
            AppendJsonKv(sb, "transferMs", transferMs); sb.Append(",\n");
            AppendJsonKv(sb, "validateMs", validateMs); sb.Append(",\n");

            sb.Append("  \"records\": [\n");
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                sb.Append("    {");
                AppendJsonKv(sb, "timestamp", r.timestamp.ToString("o", CultureInfo.InvariantCulture)); sb.Append(", ");
                AppendJsonKv(sb, "rendererName", r.rendererName); sb.Append(", ");
                AppendJsonKv(sb, "meshGroupKey", r.meshGroupKey); sb.Append(", ");
                AppendJsonKv(sb, "lodIndex",     r.lodIndex);     sb.Append(", ");
                AppendJsonKv(sb, "isSourceLod",  r.isSourceLod);  sb.Append(", ");
                AppendJsonKv(sb, "shellsMatched",        r.shellsMatched);        sb.Append(", ");
                AppendJsonKv(sb, "shellsUnmatched",      r.shellsUnmatched);      sb.Append(", ");
                AppendJsonKv(sb, "shellsTransform",      r.shellsTransform);      sb.Append(", ");
                AppendJsonKv(sb, "shellsInterpolation",  r.shellsInterpolation);  sb.Append(", ");
                AppendJsonKv(sb, "shellsMerged",         r.shellsMerged);         sb.Append(", ");
                AppendJsonKv(sb, "shellsRejected",       r.shellsRejected);       sb.Append(", ");
                AppendJsonKv(sb, "shellsOverlapFixed",   r.shellsOverlapFixed);   sb.Append(", ");
                AppendJsonKv(sb, "dedupConflicts",       r.dedupConflicts);       sb.Append(", ");
                AppendJsonKv(sb, "fragmentsMerged",      r.fragmentsMerged);      sb.Append(", ");
                AppendJsonKv(sb, "consistencyCorrected", r.consistencyCorrected); sb.Append(", ");
                AppendJsonKv(sb, "verticesTransferred",  r.verticesTransferred);  sb.Append(", ");
                AppendJsonKv(sb, "verticesTotal",        r.verticesTotal);        sb.Append(", ");
                AppendJsonKv(sb, "invertedCount",        r.invertedCount);        sb.Append(", ");
                AppendJsonKv(sb, "stretchedCount",       r.stretchedCount);       sb.Append(", ");
                AppendJsonKv(sb, "zeroAreaCount",        r.zeroAreaCount);        sb.Append(", ");
                AppendJsonKv(sb, "oobCount",             r.oobCount);             sb.Append(", ");
                AppendJsonKv(sb, "cleanCount",           r.cleanCount);           sb.Append(", ");
                AppendJsonKv(sb, "overlapShellPairs",    r.overlapShellPairs);    sb.Append(", ");
                AppendJsonKv(sb, "overlapTriangleCount", r.overlapTriangleCount); sb.Append(", ");
                AppendJsonKv(sb, "overlapSameSrcPairs",  r.overlapSameSrcPairs);  sb.Append(", ");
                AppendJsonKv(sb, "texelDensityBadCount", r.texelDensityBadCount); sb.Append(", ");
                AppendJsonKv(sb, "texelDensityMedian",   r.texelDensityMedian);   sb.Append(", ");
                AppendJsonKv(sb, "symSplitFallbackCount",r.symSplitFallbackCount);sb.Append(", ");
                AppendJsonKv(sb, "symSplitTotalCount",   r.symSplitTotalCount);   sb.Append(", ");
                AppendJsonKv(sb, "topologyIterations",   r.topologyIterations);   sb.Append(", ");
                AppendJsonKv(sb, "topologyFixed",        r.topologyFixed);        sb.Append(", ");
                AppendJsonKv(sb, "topologyCapHit",       r.topologyCapHit);
                sb.Append("}");
                if (i < records.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        // ── Helpers ──
        static void AppendJsonKv(StringBuilder sb, string k, string v)
        {
            sb.Append('"').Append(k).Append("\": ");
            if (v == null) sb.Append("null");
            else { sb.Append('"'); AppendJsonString(sb, v); sb.Append('"'); }
        }
        static void AppendJsonKv(StringBuilder sb, string k, int v)    { sb.Append('"').Append(k).Append("\": ").Append(v.ToString(CultureInfo.InvariantCulture)); }
        static void AppendJsonKv(StringBuilder sb, string k, long v)   { sb.Append('"').Append(k).Append("\": ").Append(v.ToString(CultureInfo.InvariantCulture)); }
        static void AppendJsonKv(StringBuilder sb, string k, float v)  { sb.Append('"').Append(k).Append("\": ").Append(v.ToString("R", CultureInfo.InvariantCulture)); }
        static void AppendJsonKv(StringBuilder sb, string k, bool v)   { sb.Append('"').Append(k).Append("\": ").Append(v ? "true" : "false"); }

        static void AppendJsonString(StringBuilder sb, string s)
        {
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
        }

        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        // ── Record struct ──
        public class RunRecord
        {
            public DateTime timestamp;
            public string   rendererName;
            public string   meshGroupKey;
            public int      lodIndex;
            public bool     isSourceLod;

            public int shellsMatched, shellsUnmatched, shellsTransform, shellsInterpolation, shellsMerged;
            public int shellsRejected, shellsOverlapFixed, dedupConflicts, fragmentsMerged, consistencyCorrected;
            public int verticesTransferred, verticesTotal;

            public int invertedCount, stretchedCount, zeroAreaCount, oobCount, cleanCount;
            public int overlapShellPairs, overlapTriangleCount, overlapSameSrcPairs;
            public int texelDensityBadCount;
            public float texelDensityMedian;

            public int symSplitFallbackCount, symSplitTotalCount;
            public int topologyIterations, topologyFixed;
            public bool topologyCapHit;

            // Snapshot of the result UV2 channel for post-run PNG rendering.
            // Not written to CSV/JSON — consumed only by WriteArtefacts.
            public Vector2[] uv2Snapshot;
            public int[]    trianglesSnapshot;
        }
    }
}
