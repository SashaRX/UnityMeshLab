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

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Collects per-mesh metrics during a pipeline run and writes CSV + JSON on Dispose.
    /// Construct via <see cref="NewRun"/>; wrap the run in <c>using(...)</c>.
    /// </summary>
    public sealed class BenchmarkRecorder : IDisposable
    {
        public static BenchmarkRecorder Current { get; private set; }

        /// <summary>
        /// Absolute path of the most recent CSV written by <see cref="WriteArtefacts"/>.
        /// Used by <see cref="BenchmarkSweep"/> to locate per-cell artefacts after a
        /// nested run finishes (the sweep driver does not own the recorder session,
        /// so it can't get the path from Current — which has already been cleared).
        /// </summary>
        public static string LastWrittenCsvPath { get; private set; }

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
        int symSplitFallbackAt0 = -1;
        int symSplitTotalAt0    = -1;

        // Session config snapshot
        readonly string symSplitMode;
        readonly bool   repackPerMesh;
        readonly bool   splitTargets;
        // Resolved (post auto-compute) atlas resolution. The constructor seeds
        // it with ctx.AtlasResolution; ExecRepackCore overrides it via
        // SetResolvedAtlasResolution after MeshAreaHelper.ComputeAutoResolution
        // so AutoFromTexelDensity runs record the value xatlas actually packed
        // at, not the user-facing setting.
        int    atlasResolution;
        readonly int    shellPad;
        readonly int    borderPad;
        readonly int    sourceLodIndex;
        // Pre-pack params snapshot — captured from UvToolContext so each cell
        // of a parameter sweep stamps its CSV/JSON with the values that
        // produced the run, regardless of subsequent context mutations.
        readonly bool   arapEnabled;
        readonly int    arapIterations;
        readonly float  stretchThreshold;
        // TODO: capture actualAtlasWidth/actualAtlasHeight from RepackResult.
        // Currently RepackResult is consumed inside ExecRepackCore and not
        // surfaced on MeshEntry. Threading it through would require a new
        // field on MeshEntry — out of scope for this iteration.

        // Per-mesh records (one row per recorded mesh)
        readonly List<RunRecord> records = new List<RunRecord>();
        const int MaxPngSnapshots = 32;
        int pngSnapshotsCaptured;
        int pngSnapshotsSkipped;

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
            arapEnabled      = ctx?.ReparameterizeStretchedShells ?? false;
            arapIterations   = ctx?.ArapIterations ?? 0;
            stretchThreshold = ctx?.StretchThreshold ?? 0f;
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

        /// <summary>
        /// Update the recorded atlas resolution to the value the repack stage
        /// actually used. Needed when <see cref="ResolutionMode.AutoFromTexelDensity"/>
        /// computes a resolution at runtime that differs from
        /// <c>ctx.AtlasResolution</c> — the CSV/JSON would otherwise report
        /// the stale UI setting and break sweep aggregations that key off
        /// <c>atlasRes</c>.
        /// </summary>
        public void SetResolvedAtlasResolution(int resolved)
        {
            if (resolved > 0) atlasResolution = resolved;
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

            // UV2 + triangles feed two consumers with different budgets:
            //   * atlasUtilization, a scored metric — must be read for every row,
            //     otherwise rows past the PNG cap record 0 and BenchmarkSweep
            //     (which weights utilization x100) picks a winner based on which
            //     meshes happened to be recorded first.
            //   * the per-mesh PNG dump, which is diagnostic and stays capped.
            // Reading is bounded by the same mesh-size sanity limits either way.
            bool withinPngLimits = snapshotMesh != null && IsPngSnapshotWithinLimits(snapshotMesh);
            Vector2[] uv2Data = null;
            int[] trisData = null;
            if (withinPngLimits)
            {
                var list = new System.Collections.Generic.List<Vector2>();
                snapshotMesh.GetUVs(1, list);
                if (list.Count > 0)
                {
                    uv2Data = list.ToArray();
                    trisData = snapshotMesh.triangles;
                }
            }

            // Only a mesh that actually yielded UV2 data consumes the snapshot
            // budget; a mesh without UV2 has no PNG to draw and must not starve
            // later meshes that do. "Skipped" keeps its old meaning: rejected by
            // the size limits, or denied a PNG slot despite having UV2.
            bool retainPng = uv2Data != null && pngSnapshotsCaptured < MaxPngSnapshots;
            if (retainPng) pngSnapshotsCaptured++;
            else if (snapshotMesh != null && (uv2Data != null || !withinPngLimits)) pngSnapshotsSkipped++;

            // Validation report can be stale: a mesh that failed transfer in
            // a later sweep cell would otherwise carry the previous cell's
            // ValidationReport into this row. Gate validation fields on
            // "transfer actually happened this run" (= tr != null).
            var v = (tr != null) ? vr : null;
            // SymSplit counters are session-level (delta from session start).
            // Writing them on every row would cause SUM aggregations to scale
            // by mesh count; pin them to the first row of the session.
            bool firstRow = records.Count == 0;

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

                invertedCount       = v?.invertedCount       ?? 0,
                stretchedCount      = v?.stretchedCount      ?? 0,
                zeroAreaCount       = v?.zeroAreaCount       ?? 0,
                oobCount            = v?.oobCount            ?? 0,
                cleanCount          = v?.cleanCount          ?? 0,
                overlapShellPairs   = v?.overlapShellPairs   ?? 0,
                overlapTriangleCount= v?.overlapTriangleCount?? 0,
                overlapSameSrcPairs = v?.overlapSameSrcPairs ?? 0,
                texelDensityBadCount= v?.texelDensityBadCount?? 0,
                texelDensityMedian  = v?.texelDensityMedian  ?? 0f,

                // Run-level — written only on the first row of the session so
                // SUM aggregations don't multiply them by mesh count.
                symSplitFallbackCount = firstRow ? (SymmetrySplitShells.LastFallbackCount - symSplitFallbackAt0) : 0,
                symSplitTotalCount    = firstRow ? (SymmetrySplitShells.LastTotalSplitCount - symSplitTotalAt0)    : 0,
                // Topology counters are captured per-target by Transfer() into
                // TransferResult. The static Last* fields are unreliable here —
                // they reflect only the last processed mesh in a multi-target run.
                topologyIterations    = tr?.topologyIterations ?? 0,
                topologyFixed         = tr?.topologyFixed      ?? 0,
                topologyCapHit        = tr?.topologyCapHit     ?? false,

                uv2Snapshot       = retainPng ? uv2Data : null,
                trianglesSnapshot = retainPng ? trisData : null,
            };

            // atlasUtilization = sum of |triangle area| in UV2 space — true
            // chart coverage of [0,1]² (1.0 = full atlas, bin-packing
            // typically lands at 0.55-0.85). Uses the same triangle-sum
            // helper RepackSingle/Multi log so the metric is consistent with
            // the per-mesh log line. The previous bbox-based version dropped
            // any UV with sqrMagnitude near zero (excluding legitimate verts
            // at the atlas origin) and reported bbox area instead of true
            // coverage, so layouts touching (0,0) under-reported.
            if (uv2Data != null && uv2Data.Length > 0 && trisData != null)
            {
                rec.atlasUtilization = (float)XatlasRepack.ComputeUv2CoverageFraction(uv2Data, trisData);
            }
            records.Add(rec);
        }

        static bool IsPngSnapshotWithinLimits(Mesh mesh)
        {
            if (mesh.vertexCount > UvPngWriter.MaxUvCount) return false;

            ulong indexCount = 0;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                indexCount += mesh.GetIndexCount(subMesh);
                if (indexCount > UvPngWriter.MaxTriangleIndexCount) return false;
            }
            return indexCount >= 3;
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

            // Millisecond-precision timestamp — second-level collided when an
            // operator rerun the same mode/label within ~1 second (scripted
            // sweeps or quick UI clicks). Adding ms ensures every run writes
            // to its own file even at sub-second cadence.
            string stamp = startedAtUtc.ToString("yyyyMMdd_HHmmss_fff");
            string fileBase = $"{stamp}_{lodGroupName}_{runLabel}_{Sanitize(modeTag)}";
            string csvPath = Path.Combine(dir, fileBase + ".csv");
            string jsonPath = Path.Combine(dir, fileBase + ".json");

            File.WriteAllText(csvPath,  BuildCsv(),  Encoding.UTF8);
            File.WriteAllText(jsonPath, BuildJson(), Encoding.UTF8);
            // Publish path so external orchestrators (e.g. BenchmarkSweep) can
            // locate the artefacts of the most recently finished session.
            LastWrittenCsvPath = csvPath;

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
                $"saved {records.Count} rec(s){(pngCount > 0 ? $" + {pngCount} PNG" : "")}" +
                $"{(pngSnapshotsSkipped > 0 ? $" ({pngSnapshotsSkipped} PNG skipped by safety limits)" : "")} → {csvPath}");
        }

        string BuildCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("timestamp,runLabel,lodGroup,symSplitMode,repackPerMesh,splitTargets," +
                "atlasRes,shellPad,borderPad," +
                "arapEnabled,arapIterations,stretchThreshold," +
                "sourceLod," +
                "rendererName,meshGroupKey,lodIndex,isSourceLod," +
                "shellsMatched,shellsUnmatched,shellsTransform,shellsInterpolation,shellsMerged," +
                "shellsRejected,shellsOverlapFixed,dedupConflicts,fragmentsMerged,consistencyCorrected," +
                "verticesTransferred,verticesTotal," +
                "invertedCount,stretchedCount,zeroAreaCount,oobCount,cleanCount," +
                "overlapShellPairs,overlapTriangleCount,overlapSameSrcPairs," +
                "texelDensityBadCount,texelDensityMedian," +
                "symSplitFallbackCount,symSplitTotalCount," +
                "topologyIterations,topologyFixed,topologyCapHit,atlasUtilization," +
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
                sb.Append(arapEnabled ? '1' : '0').Append(',');
                sb.Append(arapIterations.ToString(inv)).Append(',');
                sb.Append(stretchThreshold.ToString("R", inv)).Append(',');
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
                sb.Append(r.atlasUtilization.ToString("R", inv)).Append(',');
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
            AppendJsonKv(sb, "arapEnabled",      arapEnabled);      sb.Append(",\n");
            AppendJsonKv(sb, "arapIterations",   arapIterations);   sb.Append(",\n");
            AppendJsonKv(sb, "stretchThreshold", stretchThreshold); sb.Append(",\n");
            AppendJsonKv(sb, "sourceLodIndex", sourceLodIndex); sb.Append(",\n");
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
                AppendJsonKv(sb, "topologyCapHit",       r.topologyCapHit);       sb.Append(", ");
                AppendJsonKv(sb, "atlasUtilization",     r.atlasUtilization);
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

        // Imported asset names are user-controlled, so escaping covers both
        // RFC 4180 quoting and spreadsheet formula neutralisation.
        static string Csv(string s) => CsvUtil.Escape(s);

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

            /// <summary>UV2 bbox area in [0,1] space; 1.0 = full atlas, 0.25 = quarter-filled.</summary>
            public float atlasUtilization;

            // Snapshot of the result UV2 channel for post-run PNG rendering.
            // Not written to CSV/JSON — consumed only by WriteArtefacts.
            public Vector2[] uv2Snapshot;
            public int[]    trianglesSnapshot;
        }
    }
}
