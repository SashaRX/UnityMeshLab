// SweepRunner.cs — the parameter sweep as a library: the matrix resolved against the
// context's current values, validated and counted, enumerated into labelled cells,
// and run cell by cell through whatever pipeline the host provides, with the
// context snapshotted and restored, per-cell artefacts routed into one sweep
// directory, an incremental aggregate after every cell, and the provenance
// manifest and archive at the end. The transfer tab used to carry all of this;
// any tool with an end-to-end pipeline can host a sweep now.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// What a sweep needs from the tool that hosts it: the context whose settings the
    /// cells vary, the symmetry-split mode the tool keeps outside the context, a way to
    /// put the working meshes back between cells, and the pipeline itself. The host is
    /// also what <see cref="BenchmarkRunner"/> drives per case.
    /// </summary>
    internal interface ISweepHost
    {
        UvToolContext Context { get; }
        SymmetrySplitShells.ThresholdMode SymmetrySplitMode { get; set; }
        /// <summary>Restores the FBX meshes on every renderer and drops the working copies.</summary>
        void ResetWorkingCopies();
        /// <summary>Runs the whole pipeline once, synchronously, recording under <paramref name="runLabel"/>.</summary>
        void RunPipeline(string runLabel);
    }

    internal static class SweepRunner
    {
        internal const int MaxValuesPerDimension = 16;
        internal const int MaxCells = 256;

        /// <summary>One cell of the matrix: the settings one pipeline run gets.</summary>
        internal readonly struct Cell
        {
            public readonly int atlasRes, shellPad, borderPad, arapIterations, oversample;
            public readonly float stretchThreshold;
            public readonly SymmetrySplitShells.ThresholdMode symMode;

            public Cell(int atlasRes, int shellPad, int borderPad, int arapIterations, float stretchThreshold, int oversample, SymmetrySplitShells.ThresholdMode symMode)
            {
                this.atlasRes = atlasRes; this.shellPad = shellPad; this.borderPad = borderPad;
                this.arapIterations = arapIterations; this.stretchThreshold = stretchThreshold;
                // A suite entry of 0 or less cannot run; the clamped value is what executes
                // and what the label and the config record, so they never disagree.
                this.oversample = oversample > 0 ? oversample : 1;
                this.symMode = symMode;
            }

            /// <summary>
            /// `sweep_res{R}_pad{S}_bdr{B}_arap{N}_stretch{T}_os{O}_sym{M}` — the stretch is
            /// written `1p50` (a '.' would be sanitized to '_' and break the recovery regex).
            /// </summary>
            public string Label
            {
                get
                {
                    int hundredths = Mathf.RoundToInt(stretchThreshold * 100f);
                    string stretchTag = $"{hundredths / 100}p{(hundredths % 100):D2}";
                    string symTag = symMode == SymmetrySplitShells.ThresholdMode.LegacyFixed ? "legacy" : "adaptive";
                    return $"sweep_res{atlasRes}_pad{shellPad}_bdr{borderPad}_arap{arapIterations}_stretch{stretchTag}_os{oversample}_sym{symTag}";
                }
            }

            public BenchmarkSweep.CellConfig Config => new BenchmarkSweep.CellConfig
            {
                atlasRes = atlasRes, shellPad = shellPad, borderPad = borderPad,
                arapEnabled = arapIterations > 0, arapIterations = arapIterations,
                stretchThreshold = stretchThreshold, internalOversample = oversample, symSplitMode = symMode,
            };

            public override string ToString()
                => $"res={atlasRes}, shellPad={shellPad}, borderPad={borderPad}, arap={arapIterations}, stretch={stretchThreshold:F2}, oversample={oversample}, symMode={symMode}";
        }

        /// <summary>The matrix's axes with the context's current value standing in for every axis the matrix leaves empty.</summary>
        internal sealed class Axes
        {
            public int[] resolutions, shellPaddings, borderPaddings, arapIterations, oversamples;
            public float[] stretchThresholds;
            public SymmetrySplitShells.ThresholdMode[] symModes;

            public long Count => (long)resolutions.Length * shellPaddings.Length * borderPaddings.Length
                               * arapIterations.Length * stretchThresholds.Length * oversamples.Length * symModes.Length;

            /// <summary>Every cell, resolution outermost, symmetry mode innermost.</summary>
            public IEnumerable<Cell> Cells()
            {
                foreach (int r in resolutions)
                    foreach (int s in shellPaddings)
                        foreach (int b in borderPaddings)
                            foreach (int a in arapIterations)
                                foreach (float t in stretchThresholds)
                                    foreach (int o in oversamples)
                                        foreach (var m in symModes)
                                            yield return new Cell(r, s, b, a, t, o, m);
            }
        }

        internal static Axes Resolve(TestSuiteAsset.SweepMatrix sm, UvToolContext ctx, SymmetrySplitShells.ThresholdMode currentSymMode)
        {
            return new Axes
            {
                resolutions = sm.atlasResolutions?.Length > 0 ? sm.atlasResolutions : new[] { ctx.AtlasResolution },
                shellPaddings = sm.shellPaddingPxVariants?.Length > 0 ? sm.shellPaddingPxVariants : new[] { ctx.ShellPaddingPx },
                borderPaddings = sm.borderPaddingPxVariants?.Length > 0 ? sm.borderPaddingPxVariants : new[] { ctx.BorderPaddingPx },
                arapIterations = sm.arapIterationsVariants?.Length > 0 ? sm.arapIterationsVariants : new[] { ctx.ReparameterizeStretchedShells ? ctx.ArapIterations : 0 },
                stretchThresholds = sm.stretchThresholdVariants?.Length > 0 ? sm.stretchThresholdVariants : new[] { ctx.StretchThreshold },
                oversamples = sm.internalOversampleVariants?.Length > 0 ? sm.internalOversampleVariants : new[] { ctx.InternalOversample },
                symModes = sm.symSplitThresholdModeVariants?.Length > 0 ? sm.symSplitThresholdModeVariants : new[] { currentSymMode },
            };
        }

        /// <summary>
        /// Checks every axis against its allowed range and size and the whole matrix
        /// against <see cref="MaxCells"/>; <paramref name="cellCount"/> is what "Run Sweep (N)" shows.
        /// </summary>
        internal static bool TryValidate(TestSuiteAsset.SweepMatrix sm, UvToolContext ctx, SymmetrySplitShells.ThresholdMode currentSymMode, out int cellCount, out string error)
        {
            cellCount = 0;
            error = null;
            if (sm == null || ctx == null) { error = "Sweep configuration is missing."; return false; }
            var axes = Resolve(sm, ctx, currentSymMode);
            if (!ValidateDimension(axes.resolutions, 64, 4096, "atlas resolution", out error) ||
                !ValidateDimension(axes.shellPaddings, 0, 64, "shell padding", out error) ||
                !ValidateDimension(axes.borderPaddings, 0, 64, "border padding", out error) ||
                !ValidateDimension(axes.arapIterations, 0, 200, "ARAP iterations", out error) ||
                !ValidateDimension(axes.stretchThresholds, 1f, 3f, "stretch threshold", out error) ||
                !ValidateDimension(axes.oversamples, 1, 16, "internal oversample", out error))
                return false;
            if (axes.symModes.Length > MaxValuesPerDimension)
            {
                error = $"symmetry-split threshold mode has {axes.symModes.Length} values; the maximum is {MaxValuesPerDimension}.";
                return false;
            }
            long total = axes.Count;
            if (total > MaxCells) { error = $"Sweep has {total} cells; the maximum is {MaxCells}."; return false; }
            cellCount = (int)total;
            return true;
        }

        static bool ValidateDimension(int[] values, int min, int max, string label, out string error)
        {
            error = null;
            if (values.Length > MaxValuesPerDimension) { error = $"{label} has {values.Length} values; the maximum is {MaxValuesPerDimension}."; return false; }
            foreach (int v in values)
                if (v < min || v > max) { error = $"Invalid {label} {v}; allowed range is {min}..{max}."; return false; }
            return true;
        }

        static bool ValidateDimension(float[] values, float min, float max, string label, out string error)
        {
            error = null;
            if (values.Length > MaxValuesPerDimension) { error = $"{label} has {values.Length} values; the maximum is {MaxValuesPerDimension}."; return false; }
            foreach (float v in values)
                if (float.IsNaN(v) || float.IsInfinity(v) || v < min || v > max) { error = $"Invalid {label} {v}; allowed range is {min}..{max}."; return false; }
            return true;
        }

        // ── directories ──

        /// <summary>`<project>/BenchmarkReports`.</summary>
        internal static string ReportsRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(projectRoot, "BenchmarkReports");
        }

        /// <summary>A UTC stamp with milliseconds, so two runs started in the same second get distinct directories.</summary>
        internal static string Stamp() => DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);

        /// <summary>Letters, digits, '-' and '_' kept, everything else '_'; "case" for an empty name.</summary>
        internal static string SanitizeForPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return "case";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        // ── the run ──

        /// <summary>
        /// Runs the pipeline once per cell of <paramref name="sm"/> on <paramref name="host"/>.
        /// Each cell writes its own CSV/JSON under the sweep directory
        /// (<paramref name="sweepDirOverride"/>, else a new `BenchmarkReports/sweep_&lt;stamp&gt;/`);
        /// the aggregate (summary.csv, winner.json, index.html) is rewritten after every
        /// cell so a crash leaves usable reports, and the manifest and archive are written
        /// at the end. The context's settings and the host's symmetry mode are restored
        /// whatever happens. Returns the cells completed, or -1 when the matrix is invalid.
        /// </summary>
        internal static int Run(ISweepHost host, TestSuiteAsset.SweepMatrix sm, string sweepDirOverride = null)
        {
            var ctx = host?.Context;
            if (ctx == null || ctx.LodGroup == null || sm == null) return 0;
            if (!TryValidate(sm, ctx, host.SymmetrySplitMode, out int total, out string validationError))
            {
                UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] {validationError}");
                EditorUtility.DisplayDialog("Invalid sweep", validationError, "OK");
                return -1;
            }
            var axes = Resolve(sm, ctx, host.SymmetrySplitMode);

            // Snapshot everything a cell writes — restored unconditionally below.
            int origRes = ctx.AtlasResolution, origPad = ctx.ShellPaddingPx, origBdr = ctx.BorderPaddingPx;
            bool origArapOn = ctx.ReparameterizeStretchedShells;
            int origArapIters = ctx.ArapIterations, origOversample = ctx.InternalOversample;
            float origStretch = ctx.StretchThreshold;
            var origSymMode = host.SymmetrySplitMode;
            // The sweep iterates explicit resolutions; AutoFromTexelDensity would overwrite
            // ctx.AtlasResolution every cell and collapse that axis.
            var origResMode = ctx.RepackResolutionMode;
            ctx.RepackResolutionMode = ResolutionMode.Manual;

            var writtenCsvPaths = new List<string>(total);
            var cellConfigs = new List<BenchmarkSweep.CellConfig>(total);

            string sweepDir = !string.IsNullOrEmpty(sweepDirOverride) ? sweepDirOverride : Path.Combine(ReportsRoot(), $"sweep_{Stamp()}");
            try { Directory.CreateDirectory(sweepDir); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark, $"[Sweep] Could not pre-create sweep dir '{sweepDir}': {ex.Message}");
                sweepDir = null;
            }

            // Every per-cell recorder session writes into the sweep directory, next to
            // the aggregate; restored in the same finally as everything else.
            string prevRecorderOverride = BenchmarkRecorder.OutputDirectoryOverride;
            if (!string.IsNullOrEmpty(sweepDir)) BenchmarkRecorder.OutputDirectoryOverride = sweepDir;

            int done = 0;
            bool cancelled = false;
            UvProgress.Begin($"Pipeline Sweep ({total} cells)", cancelable: true);
            try
            {
                foreach (var cell in axes.Cells())
                {
                    UvProgress.Report((float)done / Mathf.Max(1, total), $"cell {done + 1}/{total}: {cell}");
                    if (UvProgress.CancelRequested) { cancelled = true; break; }
                    UvtLog.Verbose(UvtLog.Category.Benchmark, $"[Sweep] cell {done + 1}/{total}: GC heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");

                    Apply(ctx, host, cell);
                    if (sm.resetBetweenRuns) host.ResetWorkingCopies();

                    string csvBefore = BenchmarkRecorder.LastWrittenCsvPath;
                    try { host.RunPipeline(cell.Label); }
                    catch (Exception ex) { UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] Cell {done + 1}/{total} threw: {ex.Message}"); }
                    // The CSV the recorder just wrote; null when the pipeline aborted first.
                    string csvAfter = BenchmarkRecorder.LastWrittenCsvPath;
                    writtenCsvPaths.Add(csvAfter != null && csvAfter != csvBefore ? csvAfter : null);
                    cellConfigs.Add(cell.Config);
                    done++;

                    if (!string.IsNullOrEmpty(sweepDir))
                    {
                        try { BenchmarkSweep.WriteAggregateReport(writtenCsvPaths, cellConfigs, sweepDir); }
                        catch (Exception ex) { UvtLog.Warn(UvtLog.Category.Benchmark, $"[Sweep] Incremental aggregate failed: {ex.Message}"); }
                    }

                    // Release the pipeline's temporary meshes so the native atlas allocator
                    // and the managed heap do not balloon across dozens of cells.
                    try { GC.Collect(); Resources.UnloadUnusedAssets(); }
                    catch (Exception ex) { UvtLog.Verbose(UvtLog.Category.Benchmark, $"[Sweep] Between-cell cleanup hiccup: {ex.Message}"); }
                }
            }
            finally
            {
                BenchmarkRecorder.OutputDirectoryOverride = prevRecorderOverride;
                if (cancelled) UvProgress.Cancel(); else UvProgress.End();
                ctx.AtlasResolution = origRes; ctx.ShellPaddingPx = origPad; ctx.BorderPaddingPx = origBdr;
                ctx.ReparameterizeStretchedShells = origArapOn; ctx.ArapIterations = origArapIters;
                ctx.StretchThreshold = origStretch; ctx.InternalOversample = origOversample;
                ctx.RepackResolutionMode = origResMode;
                host.SymmetrySplitMode = origSymMode;
                SymmetrySplitShells.CurrentThresholdMode = origSymMode;
                UvtLog.Info(UvtLog.Category.Benchmark, $"Sweep complete: {done}/{total} cells{(cancelled ? " (cancelled)" : "")}");

                if (writtenCsvPaths.Count >= 1)
                {
                    try { BenchmarkSweep.WriteAggregateReport(writtenCsvPaths, cellConfigs, sweepDir); }
                    catch (Exception ex) { UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] Aggregate report failed: {ex.Message}"); }
                }
                if (!string.IsNullOrEmpty(sweepDir) && Directory.Exists(sweepDir))
                    WriteManifestAndArchive(sweepDir, sm, ctx.LodGroup != null ? ctx.LodGroup.name : "standalone", writtenCsvPaths.Count);
            }
            return done;
        }

        static void Apply(UvToolContext ctx, ISweepHost host, in Cell cell)
        {
            ctx.AtlasResolution = cell.atlasRes;
            ctx.ShellPaddingPx = cell.shellPad;
            ctx.BorderPaddingPx = cell.borderPad;
            ctx.ReparameterizeStretchedShells = cell.arapIterations > 0;
            if (cell.arapIterations > 0) ctx.ArapIterations = cell.arapIterations;
            ctx.StretchThreshold = cell.stretchThreshold;
            ctx.InternalOversample = cell.oversample;
            host.SymmetrySplitMode = cell.symMode;
            SymmetrySplitShells.CurrentThresholdMode = cell.symMode;
        }

        // Provenance manifest + archive; losing either is logged, never thrown.
        static void WriteManifestAndArchive(string sweepDir, TestSuiteAsset.SweepMatrix sm, string label, int cellCount)
        {
            try
            {
                var prov = BenchmarkSweep.ResolvePackageProvenance();
                string stamp = Path.GetFileName(sweepDir);
                if (stamp != null && stamp.StartsWith("sweep_", StringComparison.Ordinal)) stamp = stamp.Substring("sweep_".Length);
                BenchmarkSweep.WriteManifest(sweepDir, new BenchmarkSweep.SweepManifest
                {
                    sweepDir = sweepDir, sweepStamp = stamp ?? "", sweepLabel = label,
                    packageName = prov.pkgName, packageVersion = prov.pkgVersion,
                    gitSha = prov.gitSha, gitBranch = prov.gitBranch, gitDirty = prov.gitDirty,
                    unityVersion = Application.unityVersion, platform = Application.platform.ToString(),
                    hostUser = Environment.UserName, hostMachine = Environment.MachineName, hostOs = Environment.OSVersion.VersionString,
                    processor = SystemInfo.processorType,
                    cellCount = cellCount, caseCount = 1, matrix = sm, caseLabels = null,
                });
                BenchmarkSweep.ArchiveSweep(sweepDir);
            }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark, $"[Sweep] manifest/archive step failed: {ex.Message}");
            }
        }
    }
}
