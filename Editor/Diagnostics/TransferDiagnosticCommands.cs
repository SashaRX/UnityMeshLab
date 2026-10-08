using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Menu entry points can also be invoked through Unity MCP execute_menu_item.</summary>
    internal static class TransferDiagnosticCommands
    {
        static bool replayRunning;
        const string Menu = "Tools/Mesh Lab/Diagnostics/";

        internal static UvToolHub FindHub()
        {
            foreach (var hub in Resources.FindObjectsOfTypeAll<UvToolHub>())
                if (hub.DiagnosticContext != null && hub.DiagnosticContext.MeshEntries.Count > 0) return hub;
            throw new InvalidOperationException("Open Mesh Lab and select a LODGroup first.");
        }

        [MenuItem(Menu + "Capture Current Transfer State")]
        internal static void CaptureCurrent()
        {
            try { TransferCaseCapture.CaptureCurrent(FindHub().DiagnosticContext); }
            catch (Exception error) { UvtLog.Error(UvtLog.Category.Benchmark, "[TransferCapture] " + error.Message); }
        }

        [MenuItem(Menu + "Capture Next Transfer Run")]
        static void Arm()
        {
            try {
                FindHub().DiagnosticContext.CaptureNextTransfer = true;
                UvtLog.Info(UvtLog.Category.Benchmark, "[TransferCapture] Armed for the next Full Pipeline or Transfer run.");
            }
            catch (Exception error) { UvtLog.Error(UvtLog.Category.Benchmark, "[TransferCapture] " + error.Message); }
        }

        [MenuItem(Menu + "Run Captured Full Pipeline")]
        internal static void RunCapturedPipeline()
        {
            try {
                var workflow = FindHub().DiagnosticWorkflow;
                if (workflow == null) throw new InvalidOperationException("Enable the UV Transfer library first.");
                workflow.RunCapturedPipeline();
            }
            catch (Exception error) { UvtLog.Error(UvtLog.Category.Benchmark, "[TransferCapture] " + error.Message); }
        }

        [MenuItem(Menu + "Replay Last Transfer Capture")]
        internal static void ReplayLast() => _ = Replay(TransferCaseCapture.LastManifest);

        [MenuItem(Menu + "Replay Transfer Capture File")]
        static void ReplayFile()
        {
            string path = EditorUtility.OpenFilePanel("Transfer capture manifest", SweepRunner.ReportsRoot(), "json");
            if (!string.IsNullOrEmpty(path)) _ = Replay(path);
        }

        internal static async Task Replay(string path)
        {
            if (replayRunning) return;
            replayRunning = true;
            try { await TransferCaseReplay.Replay(path); }
            catch (Exception error) { UvtLog.Error(UvtLog.Category.Benchmark, "[TransferReplay] " + error.Message); }
            finally { replayRunning = false; }
        }

        internal static void Draw(UvToolContext ctx)
        {
            EditorGUILayout.LabelField("Transfer capture & replay", EditorStyles.boldLabel);
            if (ctx == null) return;
            using (new EditorGUI.DisabledScope(UvProgress.IsActive)) {
                ctx.CaptureNextTransfer = EditorGUILayout.ToggleLeft("Capture next Full Pipeline / Transfer run", ctx.CaptureNextTransfer);
                using (new EditorGUI.DisabledScope(ctx.MeshEntries.Count == 0))
                    if (GUILayout.Button("Capture current state")) TransferCaseCapture.CaptureCurrent(ctx);
                using (new EditorGUI.DisabledScope(!File.Exists(TransferCaseCapture.LastManifest)))
                    if (GUILayout.Button("Replay last capture (twice)")) ReplayLast();
            }
            EditorGUILayout.HelpBox("Next-run capture records exact mesh inputs and cross-LOD hints. Replay compares UV2 and shell matching without changing the scene.", MessageType.Info);
        }
    }
}
