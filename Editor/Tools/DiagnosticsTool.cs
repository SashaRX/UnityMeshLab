// DiagnosticsTool.cs — the Diagnostics tab: parameter sweep and multi-model benchmark
// (run through the UV2 Transfer tab's pipeline), report rebuild, log filters, the
// hierarchical probe and the FBX metrics export. Shown only with Project Settings ▸
// Mesh Lab ▸ Developer ▸ Show Debug UI; the production tabs stay clean.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public sealed class DiagnosticsTool : IUvTool, IUvToolDebugOnly
    {
        UvToolContext ctx;
        Action requestRepaint;

        TestSuiteAsset sweepSuite;
        bool foldBench = true, foldLogFilters = true, foldReports = true;

        public string ToolName => "Diagnostics";
        public string ToolId => "diagnostics";
        public int ToolOrder => 90;
        public Action RequestRepaint { set => requestRepaint = value; }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas) { this.ctx = ctx; }
        public void OnDeactivate() { }
        public void OnRefresh() { }
        public void OnDrawToolbarExtra() { }
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() { yield break; }
        public void OnSceneGUI(SceneView sv) { }

        public void OnDrawSidebar()
        {
            DebugUi.Banner();
            EditorGUILayout.Space(6);
            DrawBench();
            EditorGUILayout.Space(6);
            DebugUi.LogFilters(ref foldLogFilters);
            EditorGUILayout.Space(6);
            DrawReports();
        }

        // The sweep and the benchmark run the UV2 Transfer tab's pipeline; that tab is the
        // host (context, symmetry mode, working copies, the pipeline itself).
        static IBenchmarkHost FindHost()
        {
            var hubs = Resources.FindObjectsOfTypeAll<UvToolHub>();
            return hubs.Length > 0 ? hubs[0].FindTool<LightmapTransferTool>() as IBenchmarkHost : null;
        }

        void DrawBench()
        {
            foldBench = EditorGUILayout.Foldout(foldBench, "Parameter sweep & benchmark", true);
            if (!foldBench) return;
            var host = FindHost();
            if (host == null)
            {
                EditorGUILayout.HelpBox("The UV2 Transfer tab was not found; it hosts the pipeline the sweep runs.", MessageType.Warning);
                return;
            }
            sweepSuite = (TestSuiteAsset)EditorGUILayout.ObjectField("Sweep suite", sweepSuite, typeof(TestSuiteAsset), false);
            int cells = 0;
            string sweepError = null;
            if (sweepSuite != null && sweepSuite.sweep != null && ctx != null)
                SweepRunner.TryValidate(sweepSuite.sweep, ctx, host.SymmetrySplitMode, out cells, out sweepError);
            if (!string.IsNullOrEmpty(sweepError))
                EditorGUILayout.HelpBox(sweepError, MessageType.Error);
            int caseCount = sweepSuite != null && sweepSuite.cases != null ? sweepSuite.cases.Count : 0;
            if (ctx == null || ctx.LodGroup == null)
                EditorGUILayout.HelpBox("Select a LODGroup for Run Sweep; Run Benchmark spawns the suite's own models.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(sweepSuite == null || cells == 0 || ctx == null || ctx.LodGroup == null))
                {
                    if (GUILayout.Button($"Run Sweep ({cells})", GUILayout.Height(22)))
                        SweepRunner.Run(host, sweepSuite.sweep);
                }
                using (new EditorGUI.DisabledScope(sweepSuite == null || caseCount == 0))
                {
                    if (GUILayout.Button(new GUIContent($"Run Benchmark ({caseCount} cases)",
                            "Every TestSuiteAsset.cases[] model is spawned and every technique in suite.techniques " +
                            "(legacyXatlasSweep, hierarchicalProbe, hierarchicalRepack, stageDSweep) runs on it. " +
                            "Artefacts: BenchmarkReports/bench_<ts>/<idx>_<label>/{legacy,hier}/."), GUILayout.Height(22)))
                        BenchmarkRunner.Run(host, sweepSuite);
                }
                if (GUILayout.Button(new GUIContent("Rebuild Report",
                        "Pick a BenchmarkReports/ folder and rebuild summary.csv / winner.json / index.html " +
                        "from the per-cell CSVs already on disk. Use this after a mid-sweep Unity crash."), GUILayout.Height(22)))
                    RebuildReport();
            }
        }

        static void RebuildReport()
        {
            string defaultDir = SweepRunner.ReportsRoot();
            if (!System.IO.Directory.Exists(defaultDir))
                defaultDir = System.IO.Directory.GetParent(defaultDir)?.FullName ?? defaultDir;
            string picked = EditorUtility.OpenFolderPanel("Pick BenchmarkReports/ folder to rebuild", defaultDir, "");
            if (string.IsNullOrEmpty(picked)) return;

            string outDir;
            try { outDir = BenchmarkRunner.RebuildReport(picked); }
            catch (Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark, $"[Sweep] Rebuild threw: {ex.Message}");
                EditorUtility.DisplayDialog("Rebuild Sweep Report", $"Rebuild failed: {ex.Message}", "OK");
                return;
            }
            if (string.IsNullOrEmpty(outDir))
            {
                EditorUtility.DisplayDialog("Rebuild Sweep Report",
                    "No matching CSVs were found in:\n" + picked + "\n\nLook for files named *_sweep_resR_padS_bdrB_arapA_stretchT_*.csv.", "OK");
                return;
            }
            EditorUtility.DisplayDialog("Rebuild Sweep Report",
                "Recovery report written to:\n" + outDir + "\n\nOpen index.html in a browser for the per-run gallery.", "OK");
        }

        void DrawReports()
        {
            foldReports = EditorGUILayout.Foldout(foldReports, "Reports", true);
            if (!foldReports) return;
            using (new EditorGUI.DisabledScope(ctx == null || ctx.LodGroup == null))
            {
                if (GUILayout.Button(new GUIContent("Hierarchical probe (current LODGroup)",
                        "Per-face stay/promote classification of every fine-LOD face against the deepest LOD; hier_probe.csv under BenchmarkReports/."), GUILayout.Height(22)))
                {
                    try
                    {
                        string path = HierarchicalDiag.ProbeLodGroup(ctx.LodGroup);
                        UvtLog.Info(UvtLog.Category.Benchmark, "[Probe] " + path);
                    }
                    catch (Exception ex) { UvtLog.Error(UvtLog.Category.Benchmark, "[Probe] " + ex.Message); }
                }
            }
            if (GUILayout.Button(new GUIContent("Export FBX metrics (scene LODGroup)", "Source-FBX characterization + UV snapshot PNGs for the selected LODGroup."), GUILayout.Height(22)))
                FbxMetricsExporter.ExportForSceneLodGroup();
            if (GUILayout.Button(new GUIContent("Export FBX metrics (selected assets)", "The same for every .fbx selected in the Project window."), GUILayout.Height(22)))
                FbxMetricsExporter.ExportForSelection();
            string reports = SweepRunner.ReportsRoot();
            using (new EditorGUI.DisabledScope(!System.IO.Directory.Exists(reports)))
            {
                if (GUILayout.Button("Open BenchmarkReports folder", GUILayout.Height(22)))
                    EditorUtility.RevealInFinder(reports);
            }
            requestRepaint?.Invoke();
        }
    }
}
