// TestSuiteAsset.cs — ScriptableObject registry of benchmark test cases for the
// UV2 lightmap transfer pipeline. Used to drive repeatable manual runs across
// a fixed set of LODGroups + expected metric ranges. See TRANSFER_BENCHMARK.md.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    [CreateAssetMenu(menuName = "Lightmap UV Tool/Test Suite", fileName = "UvTransferTestSuite")]
    public class TestSuiteAsset : ScriptableObject
    {
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

            [Tooltip("Call ResetPipelineState between sweep cells so each cell starts from the " +
                     "unmodified FBX meshes. Disable only for debugging a single cell.")]
            public bool resetBetweenRuns = true;
        }

        [Tooltip("Parameter sweep driven by LightmapTransferTool.ExecSweep (Run Sweep button).")]
        public SweepMatrix sweep = new SweepMatrix();
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
