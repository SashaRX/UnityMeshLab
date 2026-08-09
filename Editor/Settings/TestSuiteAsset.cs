// TestSuiteAsset.cs — ScriptableObject registry of benchmark test cases for the
// UV2 lightmap transfer pipeline. Used to drive repeatable manual runs across
// a fixed set of LODGroups + expected metric ranges. See TRANSFER_BENCHMARK.md.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    // No [CreateAssetMenu] here — the asset is a developer/benchmarking
    // artefact and shouldn't pollute the Assets ▸ Create menu for production
    // users. The custom MenuItem below gates the entry behind
    // MeshLabProjectSettings.showDebugUI so it only appears when the user
    // has explicitly opted into the debug UI.
    public class TestSuiteAsset : ScriptableObject
    {
        const string CreateMenuPath = "Assets/Create/Mesh Lab/Sweep Test Suite";

        [MenuItem(CreateMenuPath, true)]
        static bool CreateAsset_Validate() => MeshLabProjectSettings.Instance.showDebugUI;

        [MenuItem(CreateMenuPath, priority = 600)]
        static void CreateAsset()
        {
            var asset = CreateInstance<TestSuiteAsset>();
            // Resolve target folder: selected Project-window folder, or the
            // selected asset's parent folder, or Assets/.
            string folder = "Assets";
            var sel = Selection.activeObject;
            if (sel != null)
            {
                string p = AssetDatabase.GetAssetPath(sel);
                if (!string.IsNullOrEmpty(p))
                    folder = AssetDatabase.IsValidFolder(p) ? p : Path.GetDirectoryName(p);
            }
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/UvTransferTestSuite.asset");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
        }


        [System.Serializable]
        public class ExpectedRange
        {
            [Tooltip("BenchmarkRecorder metric name (e.g. invertedCount, overlapShellPairs, " +
                     "shellsRejected, symSplitFallbackCount, topologyCapHit).")]
            public string metric = "";
            public float min = 0f;
            public float max = 0f;
        }

        [System.Serializable]
        public class TestCase
        {
            [Tooltip("Short human label for this case; appears in CSV/JSON runLabel column.")]
            public string label = "";

            [Tooltip("Source FBX asset. The custom inspector's Open button will look for the " +
                     "first LODGroup or MeshRenderer under this asset and wire it into the tool.")]
            public Object fbxAsset;

            [Tooltip("Optional hierarchy path inside the FBX prefab (e.g. 'Root/LOD_root') — " +
                     "if empty, the first LODGroup found is used.")]
            public string lodGroupPath = "";

            [Tooltip("Go/Stop metric expectations for this case. Informational — used in " +
                     "TRANSFER_BENCHMARK.md reviews, not enforced automatically.")]
            public List<ExpectedRange> expectations = new List<ExpectedRange>();

            [TextArea(2, 6)]
            [Tooltip("Free-form notes about this test case (known regressions, history, etc).")]
            public string notes = "";
        }

        [Tooltip("Ordered list of benchmark cases. Labels are used as the runLabel column in " +
                 "BenchmarkRecorder output so different models can be compared in one CSV.")]
        public List<TestCase> cases = new List<TestCase>();

        [Tooltip("Free-form description of this suite — what it's for, when it was updated.")]
        [TextArea(2, 6)]
        public string description = "";

        /// <summary>
        /// Parameter sweep configuration — drives ExecSweep to run the Full Pipeline across
        /// the cartesian product of the listed atlas resolutions / paddings. One CSV + JSON
        /// per cell, each with a runLabel of sweep_res{R}_pad{S}_bdr{B}.
        /// </summary>
        [System.Serializable]
        public class SweepMatrix
        {
            [Tooltip("Atlas resolutions (pixels) to sweep. Each resolution is combined with every " +
                     "shell padding and border padding value below.")]
            public int[] atlasResolutions = { 256, 512, 2048 };

            [Tooltip("Shell padding values (pixels) to sweep.")]
            public int[] shellPaddingPxVariants = { 2, 4, 8, 32 };

            [Tooltip("Border padding values (pixels) to sweep. Leave as {0} if you don't want " +
                     "to vary it — a single-value array still produces N×M×1 combinations.")]
            public int[] borderPaddingPxVariants = { 0 };

            [Tooltip("ARAP stretched-shell reparameterization variants. 0 = OFF; >0 = ON with that many " +
                     "local-global iterations. Default {0} (ARAP off — opt in by adding e.g. 50 or 100 to " +
                     "this list). Allowed range is 0..200; each entry spawns a separate sweep cell.")]
            public int[] arapIterationsVariants = { 0, 50 };

            [Tooltip("Sander L² stretch threshold variants used to gate ARAP. 1.0 = isometric; 1.5 = typical " +
                     "artist unwrap; 2.0 = noticeably stretched. Keep this short — every entry multiplies the " +
                     "grid size.")]
            public float[] stretchThresholdVariants = { 1.5f };

            [Tooltip("xatlas internal-oversample multiplier. Pack runs at internalRes = resolution × oversample, " +
                     "then UV2 is scaled back. Higher = fewer sub-pixel shells (less Stage B density amplification, " +
                     "see EXPERIMENTS.md a218a2b) at the cost of pack time. Defaults match RepackOptions.Default " +
                     "(4×). Use {1} to disable, {1,2,4} to compare. Each entry multiplies the grid size.")]
            public int[] internalOversampleVariants = { 4 };

            [Tooltip("SymSplit threshold mode variants. LegacyFixed = compiled constants for UV_NEAR / POS_FAR; " +
                     "Adaptive = thresholds derived per-shell from mesh / UV scale (proposal in EXPERIMENTS.md " +
                     "2026-04-15). Keep both entries to A/B them, or use one to pin a baseline.")]
            public SymmetrySplitShells.ThresholdMode[] symSplitThresholdModeVariants =
                { SymmetrySplitShells.ThresholdMode.LegacyFixed };

            [Tooltip("Call ResetPipelineState between sweep cells so each cell starts from the " +
                     "unmodified FBX meshes. Disable only for debugging a single cell.")]
            public bool resetBetweenRuns = true;
        }

        [Tooltip("Parameter sweep driven by LightmapTransferTool.ExecSweep (Run Sweep button).")]
        public SweepMatrix sweep = new SweepMatrix();

        /// <summary>
        /// Which analysis techniques to run per case during the unified
        /// benchmark. Each enabled technique writes its own artefact(s)
        /// into the per-case directory <c>bench_&lt;ts&gt;/&lt;idx&gt;_&lt;label&gt;/</c>.
        /// </summary>
        [System.Serializable]
        public class BenchTechniques
        {
            [Tooltip("Legacy xatlas parameter sweep (atlas res × shell pad × border pad × ARAP × stretch × oversample × symSplit). Output: legacy_sweep/* CSVs + winner/summary aggregate.")]
            public bool legacyXatlasSweep = true;

            [Tooltip("Probe v3 — per-face stay/promote classification on every fine-LOD face vs the deepest LOD. Output: hier_probe.csv.")]
            public bool hierarchicalProbe = true;

            [Tooltip("Hierarchical Repack dry-run — per-vertex projection classifier, atlas layout. Output: hier_repack.csv (+ atlas PNG when available).")]
            public bool hierarchicalRepack = true;

            [Tooltip("Stage D cascade-threshold sweep — rebuilds each case across the " +
                     "cascadeMatchFrac × cascadeMinHits grid below and emits per-cell group " +
                     "iso-view PNGs (lod{N}_groups_mf{F}_mh{H}.png) + stage_d_sweep.csv. " +
                     "Comparison-only (no auto-winner): pick the knee by eyeballing the PNG " +
                     "grid + join/new ratios. Off by default — opt in; each cell is a full " +
                     "HierarchicalRepack build so the grid multiplies dry-run time.")]
            public bool stageDSweep = false;

            [Tooltip("Stage D sweep: cascade matchedFrac values. A finer shell joins its parent " +
                     "group when the modal deeper-shell hit fraction is >= this. Lower = more " +
                     "merging; higher = more fresh groups. Each entry × cascadeMinHitsVariants " +
                     "is one cell.")]
            public float[] cascadeMatchFracVariants = { 0.35f, 0.5f, 0.65f };

            [Tooltip("Stage D sweep: minimum total sample hits before a finer shell's modal vote " +
                     "is trusted to join a parent group. Below = fresh group regardless of fraction.")]
            public int[] cascadeMinHitsVariants = { 2, 4, 8 };

            [Tooltip("Stage D sweep: also write a per-cell group iso-view PNG for every cell × LOD " +
                     "(lod{N}_groups_mf{F}_mh{H}.png). Off by default — the first sweep showed these " +
                     "are near-identical across cells on the major surfaces (variation is sub-pixel " +
                     "trim only), so they're suppressed as noise; stage_d_sweep.csv is the real " +
                     "output and lod{N}_groups.png (default thresholds) is the canonical visual. " +
                     "Enable only for a deep visual dive.")]
            public bool stageDSweepEmitPngs = false;
        }

        [Tooltip("Techniques to run per case during 'Run Benchmark'. All artefacts land under one bench_<stamp>/<case>/ directory so a single button gives a complete pipeline snapshot.")]
        public BenchTechniques techniques = new BenchTechniques();
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(TestSuiteAsset))]
    sealed class TestSuiteAssetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var asset = (TestSuiteAsset)target;
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Quick actions", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Open a case to select the FBX LODGroup in the scene; wire it into " +
                "LightmapTransferTool manually from there (no automatic scene spawn).",
                MessageType.Info);

            for (int i = 0; i < asset.cases.Count; i++)
            {
                var tc = asset.cases[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{i}: {tc.label}", GUILayout.MinWidth(120));
                    using (new EditorGUI.DisabledScope(tc.fbxAsset == null))
                    {
                        if (GUILayout.Button("Ping FBX", GUILayout.Width(80)))
                            EditorGUIUtility.PingObject(tc.fbxAsset);
                    }
                }
            }
        }
    }
#endif
}
