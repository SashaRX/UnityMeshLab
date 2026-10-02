// BenchmarkRunner.cs — the multi-model benchmark as a library: every case of a
// TestSuiteAsset spawned from its FBX, its LODGroup bound on the host, every enabled
// technique run into one per-case directory (legacy/ for the parameter sweep, hier/
// for the hierarchical probe, repack dry-run and Stage D sweep), the spawn destroyed
// and the operator's own LODGroup restored whatever happens.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>A sweep host that can also be pointed at another LODGroup for the duration of a case.</summary>
    internal interface IBenchmarkHost : ISweepHost
    {
        /// <summary>Binds the host (context and tab) to <paramref name="lodGroup"/>; null unbinds.</summary>
        void Bind(LODGroup lodGroup);
    }

    internal static class BenchmarkRunner
    {
        /// <summary>
        /// Runs every case of <paramref name="suite"/> with every technique in
        /// `suite.techniques`. Artefacts land under
        /// `BenchmarkReports/bench_&lt;stamp&gt;/&lt;idx&gt;_&lt;label&gt;/{legacy,hier}/`.
        /// Returns the number of cases that produced anything.
        /// </summary>
        internal static int Run(IBenchmarkHost host, TestSuiteAsset suite)
        {
            var ctx = host?.Context;
            if (ctx == null || suite == null || suite.sweep == null) return 0;
            if (suite.cases == null || suite.cases.Count == 0)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark, "[Bench] Suite has no cases — nothing to do.");
                return 0;
            }
            var tech = suite.techniques ?? new TestSuiteAsset.BenchTechniques();
            if (!tech.legacyXatlasSweep && !tech.hierarchicalProbe && !tech.hierarchicalRepack && !tech.stageDSweep)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark, "[Bench] All techniques disabled in suite.techniques — nothing to do.");
                return 0;
            }

            // The operator's LODGroup comes back at the end. Working copies on it are put
            // back first (AGENTS.md LODGroup-lifecycle invariant): a Refresh with repacked
            // meshes still assigned would adopt them as the baseline.
            var origLodGroup = ctx.LodGroup;
            if (origLodGroup != null) host.ResetWorkingCopies();

            string runStamp = SweepRunner.Stamp();
            string benchRunDir = Path.Combine(SweepRunner.ReportsRoot(), $"bench_{runStamp}");
            Directory.CreateDirectory(benchRunDir);

            int caseCount = suite.cases.Count;
            int doneCases = 0;
            bool cancelled = false;
            UvProgress.Begin($"Benchmark ({caseCount} models)", cancelable: true);
            try
            {
                for (int ci = 0; ci < caseCount; ci++)
                {
                    if (UvProgress.CancelRequested) { cancelled = true; break; }
                    var tc = suite.cases[ci];
                    if (tc == null || tc.fbxAsset == null) { UvtLog.Warn(UvtLog.Category.Benchmark, $"[Bench] Case {ci}: null FBX, skipping."); continue; }
                    string fbxPath = AssetDatabase.GetAssetPath(tc.fbxAsset);
                    if (string.IsNullOrEmpty(fbxPath)) { UvtLog.Warn(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{tc.label}': asset has no project path, skipping."); continue; }
                    var prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                    if (prefabRoot == null) { UvtLog.Warn(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{tc.label}': '{fbxPath}' is not a GameObject prefab, skipping."); continue; }

                    UvProgress.Report((float)ci / caseCount, $"case {ci + 1}/{caseCount}: {tc.label} ({Path.GetFileName(fbxPath)})");

                    GameObject spawned = null;
                    try
                    {
                        spawned = (GameObject)PrefabUtility.InstantiatePrefab(prefabRoot);
                        if (spawned == null) { UvtLog.Error(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{tc.label}': InstantiatePrefab returned null, skipping."); continue; }
                        spawned.name = $"[Bench] {tc.label}";
                        // A transient spawn must not dirty the scene or survive a save.
                        spawned.hideFlags = HideFlags.DontSave;

                        var lg = FindLodGroup(spawned, tc.lodGroupPath);
                        if (lg == null) { UvtLog.Warn(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{tc.label}': no LODGroup found under '{fbxPath}', skipping."); continue; }
                        host.Bind(lg);

                        // Case index prefix keeps two labels that sanitize alike apart.
                        string safeLabel = SweepRunner.SanitizeForPath(string.IsNullOrEmpty(tc.label) ? lg.name : tc.label);
                        string caseDir = Path.Combine(benchRunDir, $"{ci:D2}_{safeLabel}");
                        Directory.CreateDirectory(caseDir);
                        if (RunTechniques(host, lg, tech, suite.sweep, caseDir, ci, tc.label)) doneCases++;
                    }
                    catch (Exception ex)
                    {
                        UvtLog.Error(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{tc.label}' threw: {ex.Message}");
                    }
                    finally
                    {
                        if (spawned != null)
                        {
                            // Put the working copies back and unbind BEFORE the spawn goes, or
                            // the last cell's temporary meshes leak (they are clones, not
                            // children of the spawn). IsChildOf covers a LODGroup on a
                            // descendant and on the root itself.
                            if (ctx.LodGroup != null && ctx.LodGroup.transform.IsChildOf(spawned.transform))
                            {
                                host.ResetWorkingCopies();
                                host.Bind(null);
                            }
                            UnityEngine.Object.DestroyImmediate(spawned);
                        }
                    }
                    if (UvProgress.CancelRequested) { cancelled = true; break; }
                }
            }
            finally
            {
                if (cancelled) UvProgress.Cancel(); else UvProgress.End();
                host.Bind(origLodGroup);
                string techList = string.Join("+", new[]
                {
                    tech.legacyXatlasSweep ? "legacy" : null, tech.hierarchicalProbe ? "probe" : null,
                    tech.hierarchicalRepack ? "repack" : null, tech.stageDSweep ? "stageDsweep" : null,
                }.Where(s => s != null));
                UvtLog.Info(UvtLog.Category.Benchmark,
                    $"[Bench] complete: {doneCases}/{caseCount} cases [{techList}]" + (cancelled ? " (cancelled)" : "") +
                    $". Per-case dirs: BenchmarkReports/bench_{runStamp}/<idx>_<label>/{{hier,legacy}}/");
            }
            return doneCases;
        }

        /// <summary>The LODGroup at <paramref name="path"/> under the spawn, else the first one found.</summary>
        internal static LODGroup FindLodGroup(GameObject spawned, string path)
        {
            LODGroup lg = null;
            if (!string.IsNullOrEmpty(path))
            {
                var t = spawned.transform.Find(path);
                if (t != null) lg = t.GetComponent<LODGroup>();
            }
            return lg ?? spawned.GetComponentInChildren<LODGroup>(true);
        }

        // One case, every enabled technique into its own subfolder; true when any ran.
        static bool RunTechniques(IBenchmarkHost host, LODGroup lg, TestSuiteAsset.BenchTechniques tech, TestSuiteAsset.SweepMatrix sweep, string caseDir, int ci, string label)
        {
            bool didAnything = false;
            string hierDir = Path.Combine(caseDir, "hier");
            string legacyDir = Path.Combine(caseDir, "legacy");

            if (tech.legacyXatlasSweep)
            {
                Directory.CreateDirectory(legacyDir);
                string prevOverride = BenchmarkRecorder.OutputDirectoryOverride;
                BenchmarkRecorder.OutputDirectoryOverride = legacyDir;
                try { SweepRunner.Run(host, sweep, legacyDir); }
                finally { BenchmarkRecorder.OutputDirectoryOverride = prevOverride; }
                didAnything = true;
            }
            if (tech.hierarchicalProbe)
            {
                try
                {
                    Directory.CreateDirectory(hierDir);
                    HierarchicalDiag.ProbeLodGroup(lg, hierDir);
                    didAnything = true;
                }
                catch (Exception ex) { UvtLog.Error(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{label}' probe threw: {ex.Message}"); }
            }
            if (tech.hierarchicalRepack)
            {
                try
                {
                    Directory.CreateDirectory(hierDir);
                    var result = HierarchicalRepack.BuildAndWriteForCase(lg, HierarchicalRepack.Options.Default, hierDir);
                    if (!string.IsNullOrEmpty(result.error)) UvtLog.Warn(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{label}' repack: {result.error}");
                    else didAnything = true;
                }
                catch (Exception ex) { UvtLog.Error(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{label}' repack threw: {ex.Message}"); }
            }
            if (tech.stageDSweep)
            {
                try
                {
                    Directory.CreateDirectory(hierDir);
                    HierarchicalRepack.BuildStageDSweep(lg, HierarchicalRepack.Options.Default,
                        tech.cascadeMatchFracVariants, tech.cascadeMinHitsVariants, hierDir, tech.stageDSweepEmitPngs);
                    didAnything = true;
                }
                catch (Exception ex) { UvtLog.Error(UvtLog.Category.Benchmark, $"[Bench] Case {ci} '{label}' Stage D sweep threw: {ex.Message}"); }
            }
            return didAnything;
        }

        /// <summary>
        /// Rebuilds summary.csv / winner.json / index.html of <paramref name="reportsDir"/>
        /// from the per-cell CSVs still on disk (after a mid-sweep crash). Returns the
        /// output directory, null when no cell CSV was found; throws on a failed rebuild.
        /// </summary>
        internal static string RebuildReport(string reportsDir) => BenchmarkSweep.RebuildFromExistingCsvs(reportsDir);
    }
}
