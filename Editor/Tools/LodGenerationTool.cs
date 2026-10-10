// LodGenerationTool.cs — LOD generation via meshoptimizer simplification.
// Preserves UV2 lightmap coordinates with configurable weights.
//
// Workflow: UV2 Transfer (Analyze → Weld → Repack → Transfer) → LOD Gen → Overwrite FBX (from UV2 Transfer tab)

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    [MeshLabTool("lod_generation", MeshLabLibraries.Simplification, MeshLabLibraries.Hierarchy, MeshLabLibraries.Assets)]
    public class LodGenerationTool : IUvTool
    {
        UvToolContext ctx;
        UvCanvasView canvas;
        Action requestRepaint;

        public string ToolName  => "LOD Gen";
        public string ToolId    => "lod_generation";
        public int    ToolOrder => 30;

        public Action RequestRepaint { set => requestRepaint = value; }

        // ── Settings ──
        int generateLodCount = 2;
        float[] generateLodRatios = { 1f/3, 1f/9, 1f/27, 1f/81 };
        bool generateBudgetPriority = true;
        bool generateCorrectAttributes = true;
        bool generatePreserveHardEdges = true;
        float generateReductionStep = 3;
        float generateTargetError = 0.2f;
        float generateUv2Weight = 20f;
        float generateNormalWeight = 1f;
        float generateColorWeight = 1f;
        LodReductionMode generateReductionMode;
        float generateMaxNormalAngle = 15f;
        float generateMaxColorError = 0.02f;
        bool generateValidateColors = true;
        bool generateRelaxFarLods = true;
        int generateRelaxFromLod = 2;
        float generateFarTargetError = .3f, generateFarMaxColorError = .1f, generateFarMaxNormalAngle = 30;
        float generateFarColorWeightScale = .25f, generateFarNormalWeightScale = .5f;
        int generateQuality = 1;
        string lastGenerationError;
        bool generateLockBorder = false;
        bool generatePruneParts;
        int generatePruneFromLod = 2, partScreenHeight = 1080;
        float partMaxPixels = 2, partMaxArea = .02f, partMaxTriangles = .2f;
        int partPreviewLod = 2;
        readonly Dictionary<Mesh,LodSmallParts.Protection> partProtection = new Dictionary<Mesh,LodSmallParts.Protection>();

        // ── Results ──
        List<GeneratedLodInfo> lastResults = new List<GeneratedLodInfo>();
        List<GameObject> generatedObjects => ctx.GeneratedLodObjects;
        int cachedLodSelectionId = -1;
        int cachedRendererSelectionId = -1;
        List<(GameObject go, int lodIndex, int rendererCount, int triangleCount)> cachedDetectedLods =
            new List<(GameObject, int, int, int)>();
        bool cachedSelectionHasRenderers;


        struct GeneratedLodInfo
        {
            public string meshName;
            public int simplifiedTris;
            public int lodLevel;
            public float targetRatio;
            public float actualRatio;
            public int targetTris;
            public bool targetNotReached;
            public int removedLoops, blockedLoops, sourceQuads;
            public float sourceDistance, normalError, colorError;
            public Vector4 colorMax, colorRms;
            public int evaluatedCandidates, selectedCandidate;
            public int nativeProbes;
            public float sourceDistanceRms, normalRms, silhouetteMean, silhouetteMax, selectionScore;
            public LodAttributeCorrection.Report attributeCorrection;
            public string reductionNote;
            public float allowedColorError, targetError;
            public int removedParts, removedPartTris;
            public float removedPartAreaFraction, removedPartMaxPixels;
            public bool budgetPriority, qualityLimitsExceeded;
        }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            if (this.ctx != ctx) partProtection.Clear();
            this.ctx = ctx;
            this.canvas = canvas;
        }

        public void OnDeactivate() { SceneView.RepaintAll(); }

        void ValidateGeneratedObjects()
        {
            for (int i = generatedObjects.Count - 1; i >= 0; i--)
                if (generatedObjects[i] == null)
                    generatedObjects.RemoveAt(i);
            if (generatedObjects.Count == 0)
                lastResults.Clear();
        }

        public void OnRefresh()
        {
            var sourceMeshes = new HashSet<Mesh>(ctx.MeshEntries.Where(e => e.include && e.lodIndex == ctx.SourceLodIndex)
                .Select(e => e.repackedMesh ?? e.originalMesh));
            foreach (var mesh in partProtection.Keys.Where(m => !sourceMeshes.Contains(m)).ToArray()) partProtection.Remove(mesh);
            lastGenerationError = null;
            ValidateGeneratedObjects();
            lastResults.Clear();
            cachedLodSelectionId = -1;
            cachedRendererSelectionId = -1;
            cachedDetectedLods.Clear();
            cachedSelectionHasRenderers = false;
        }

        public void OnDrawSidebar()
        {
            ValidateGeneratedObjects();
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("LOD Generation", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawDetectAndCreate(ctx);
            if (ctx.LodGroup == null) return;

            DrawWorkflowHint();
            DrawExistingLodTable();
            DrawSettingsPanel(ctx);
            DrawResultsAndClear();
        }

        // ── Detect / create LODGroup section ──
        // Used by both the standalone LOD Gen tab AND Prefab Builder's right
        // panel (when no LODGroup is selected, both surfaces should show the
        // "Create LODGroup" affordance instead of an empty Settings panel).
        internal void DrawDetectAndCreate(UvToolContext sharedCtx)
        {
            if (sharedCtx == null) return;
            ctx = sharedCtx;
            if (sharedCtx.LodGroup != null) return;

            var selected = Selection.activeGameObject;
            var siblings = FindLodSiblings(selected);
            if (siblings != null && siblings.Count > 0)
            {
                RefreshDetectedLodCache(selected, siblings);
                EditorGUILayout.HelpBox("LOD objects detected — create a LODGroup to continue.", MessageType.Info);
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Detected LODs", EditorStyles.boldLabel);
                foreach (var (go, lodIndex, rendererCount, triangleCount) in cachedDetectedLods)
                {
                    EditorGUILayout.LabelField(
                        $"  LOD{lodIndex}: {go.name}  ({rendererCount} renderer{(rendererCount != 1 ? "s" : "")}, {triangleCount:N0} tris)",
                        EditorStyles.miniLabel);
                }
                EditorGUILayout.Space(6);
                var bgc = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button("Create LODGroup", GUILayout.Height(28)))
                    CreateLodGroup(siblings);
                GUI.backgroundColor = bgc;
            }
            else if (LodGroupUtility.HasAmbiguousLodChains(selected))
            {
                EditorGUILayout.HelpBox("Multiple LOD chains detected. Select a mesh from the chain to create its LODGroup.", MessageType.Info);
            }
            else if (selected != null && SelectionHasRenderers(selected))
            {
                EditorGUILayout.HelpBox(
                    "No LOD naming detected, but child renderers found.\n" +
                    "Create a LODGroup with all renderers as LOD0.",
                    MessageType.Info);
                EditorGUILayout.Space(6);
                var bgc = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button("Add LOD Group", GUILayout.Height(28)))
                {
                    var lodGroup = CreateLodGroupFromRenderers(selected);
                    if (lodGroup != null)
                    {
                        ctx.Refresh(lodGroup);
                        requestRepaint?.Invoke();
                        UvtLog.Info($"[LOD Gen] Created LODGroup on '{selected.name}' with all renderers as LOD0.");
                    }
                }
                GUI.backgroundColor = bgc;
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Assign a LODGroup in the UV2 Transfer tab first.\n" +
                    "Or select a GameObject with a LOD suffix (e.g. MyObject_LOD0) to auto-detect LOD siblings.",
                    MessageType.Info);
            }
        }

        void DrawWorkflowHint()
        {
            bool hasRepack = ctx.MeshEntries.Any(e => e.repackedMesh != null);
            if (!hasRepack)
            {
                EditorGUILayout.HelpBox(
                    "Workflow:\n" +
                    "1. UV2 Transfer: Analyze → Weld → Repack → Transfer\n" +
                    "2. LOD Gen: Generate new LODs\n" +
                    "3. UV2 Transfer: Overwrite Source FBX (saves everything)",
                    MessageType.Info);
            }
        }

        void DrawExistingLodTable()
        {
            int sourceTris = 0;
            EditorGUILayout.LabelField("Existing LODs", EditorStyles.boldLabel);
            for (int li = 0; li < ctx.LodCount; li++)
            {
                var ee = ctx.ForLod(li);
                if (ee.Count == 0) continue;
                int lodTris = 0, lodVerts = 0;
                foreach (var e in ee)
                {
                    Mesh m = e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                    if (m == null) continue;
                    lodTris += GetTriangleCount(m);
                    lodVerts += m.vertexCount;
                }
                if (li == ctx.SourceLodIndex) sourceTris = lodTris;
                bool isSrc = li == ctx.SourceLodIndex;
                string prefix = isSrc ? "► " : "  ";
                float pct = sourceTris > 0 ? (float)lodTris / sourceTris * 100f : 100f;
                EditorGUILayout.LabelField(
                    $"{prefix}LOD{li}: {lodTris:N0} tris  {lodVerts:N0} verts  ({pct:F0}%)",
                    isSrc ? EditorStyles.boldLabel : EditorStyles.miniLabel);
            }
        }

        // ── Fine-tuning settings panel — only the simplifier weights, no
        // count/ratios/Generate. Surfaced in Prefab Builder's right sidebar
        // where LOD count is controlled by the Hierarchy "+ Add LOD" pending
        // model, so the count + Generate flow there would just duplicate
        // existing affordances. The standalone LOD Gen tab keeps the full
        // panel via DrawSettingsPanel.
        internal void DrawSimplifierSettingsPanel()
        {
            EditorGUILayout.LabelField("Simplifier weights", EditorStyles.miniBoldLabel);
            generateTargetError  = EditorGUILayout.Slider("Target Error",  generateTargetError,  0.001f, 0.5f);
            generateUv2Weight    = EditorGUILayout.Slider("UV2 Weight",    generateUv2Weight,    0f,     500f);
            generateNormalWeight = EditorGUILayout.Slider("Normal Weight", generateNormalWeight, 0f,     10f);
            generateColorWeight = EditorGUILayout.Slider("Color Weight", generateColorWeight, 0f, 10f);
            generateLockBorder   = EditorGUILayout.Toggle("Lock Border",   generateLockBorder);

            if (generateTargetError < 0.1f && generateUv2Weight > 50f)
                EditorGUILayout.HelpBox(
                    "Low Target Error + High UV2 Weight may prevent reaching target polygon count. " +
                    "Try: Target Error 0.1–0.3, UV2 Weight 10–30, Lock Border OFF.",
                    MessageType.Warning);

        }

        void DrawFarLodSettings(int startLod)
        {
            generateRelaxFarLods = EditorGUILayout.Toggle("Relax Far LODs", generateRelaxFarLods);
            if (generateRelaxFarLods)
            {
                generateRelaxFromLod = EditorGUILayout.IntSlider("Relax From LOD",generateRelaxFromLod,startLod,startLod+generateLodCount);
                generateFarTargetError = EditorGUILayout.Slider("Far Target Error",Mathf.Max(generateFarTargetError,generateTargetError),generateTargetError,.5f);
                generateFarNormalWeightScale = EditorGUILayout.Slider("Far Normal Weight Scale",generateFarNormalWeightScale,0,1);
                generateFarColorWeightScale = EditorGUILayout.Slider("Far Color Weight Scale",generateFarColorWeightScale,0,1);
                if (generateValidateColors)
                    generateFarMaxColorError = EditorGUILayout.Slider("Far Max Color Error",Mathf.Max(generateFarMaxColorError,generateMaxColorError),generateMaxColorError,1);
                if (generateReductionMode != LodReductionMode.Triangles)
                    generateFarMaxNormalAngle = EditorGUILayout.Slider("Far Max Normal Angle",Mathf.Max(generateFarMaxNormalAngle,generateMaxNormalAngle),generateMaxNormalAngle,90);
                EditorGUILayout.HelpBox("Later LODs gradually use looser limits and lower attribute weights. Earlier levels keep the base settings. Every level starts from LOD0; the preview shows its effective settings.",MessageType.Info);
                var preview = BuildGenerationOptions(startLod);
                for (int i = 0; i < generateLodCount; i++)
                {
                    var level = LodPipelineOps.ForLevel(preview,i);
                    EditorGUILayout.LabelField($"  LOD{startLod+i}: error {level.targetError:G3}, color limit {(generateValidateColors ? level.maxColorError.ToString("G3") : "off")}, color weight {level.colorWeight:G3}",EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "Per-LOD ratio is taken from the Hierarchy row's quality slider " +
                "(or the pending insert's slider) when Apply Changes commits.",
                MessageType.None);
        }

        // Read-only accessor for the simplifier settings configured via the
        // sliders above. Prefab Builder's pending-insert / regenerate path
        // calls this and overrides targetRatio with the row's per-LOD value.
        internal MeshSimplifier.SimplifySettings GetSimplifierSettings(float targetRatio)
        {
            return new MeshSimplifier.SimplifySettings
            {
                targetRatio  = targetRatio,
                targetError  = generateTargetError,
                uv2Weight    = generateUv2Weight,
                normalWeight = generateNormalWeight,
                colorWeight  = generateColorWeight,
                lockBorder   = generateLockBorder,
                uvChannel    = 1,
            };
        }

        // ── Settings panel ──
        // Renders the LOD-count slider, per-target ratio sliders, simplifier
        // weights, and the Generate button. Designed for embedding into the
        // standalone LOD Gen tab AND Prefab Builder's right column.
        // The caller passes the active context so the tool's own ctx
        // reference is retargeted before the Generate action fires.
        internal void DrawSettingsPanel(UvToolContext sharedCtx)
        {
            if (sharedCtx == null) return;
            ctx = sharedCtx;
            if (ctx.LodGroup == null)
            {
                DrawDetectAndCreate(ctx);
                return;
            }
            GetGenerationBaseline(out int sourceTris, out int startLod, out float lastRatio);
            DrawSettingsPanel(sourceTris, startLod, lastRatio);
        }

        // Generate replaces the objects owned by this context. They must not
        // change the next run's level numbers or clamp its ratio sliders.
        internal void GetGenerationBaseline(out int sourceTris, out int startLod, out float lastRatio)
        {
            sourceTris = 0;
            int lastExistingLod = -1, lastLodTris = 0;
            var generated = new HashSet<GameObject>(generatedObjects);
            for (int li = 0; li < ctx.LodCount; li++)
            {
                int tris = 0;
                bool hasExistingMesh = false;
                foreach (var e in ctx.ForLod(li))
                {
                    if (e.renderer != null && generated.Contains(e.renderer.gameObject)) continue;
                    Mesh m = e.repackedMesh ?? e.originalMesh ?? e.fbxMesh;
                    if (m == null) continue;
                    hasExistingMesh = true;
                    tris += GetTriangleCount(m);
                }
                if (!hasExistingMesh) continue;
                lastExistingLod = li;
                lastLodTris = tris;
                if (li == ctx.SourceLodIndex) sourceTris = tris;
            }
            startLod = lastExistingLod + 1;
            lastRatio = sourceTris > 0 && lastExistingLod > ctx.SourceLodIndex
                ? (float)lastLodTris / sourceTris : 1f;
        }

        void DrawSettingsPanel(int sourceTris, int startLod, float lastRatio)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"Generate LOD{startLod}+", EditorStyles.boldLabel);

            generateLodCount = EditorGUILayout.IntSlider("Count", generateLodCount, 1, 4);
            generateReductionStep = EditorGUILayout.Slider("Reduction per LOD",generateReductionStep,2,8);
            if (GUILayout.Button($"Set {generateReductionStep:F1}× reduction each level"))
                generateLodRatios = SteppedRatios(lastRatio,generateReductionStep,4);

            if (lastRatio < 1f)
            {
                for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
                {
                    if (generateLodRatios[i] >= lastRatio && lastRatio > 0.01f)
                        generateLodRatios[i] = lastRatio / Mathf.Pow(generateReductionStep, i + 1);
                }
            }

            for (int i = 0; i < generateLodCount && i < generateLodRatios.Length; i++)
            {
                float maxRatio = i == 0 ? lastRatio * 0.99f : generateLodRatios[i - 1] * 0.99f;
                if (maxRatio < 0.001f) maxRatio = 0.001f;
                if (generateLodRatios[i] > maxRatio) generateLodRatios[i] = maxRatio / generateReductionStep;

                int targetLod = startLod + i;
                generateLodRatios[i] = EditorGUILayout.Slider(
                    $"  LOD{targetLod}", generateLodRatios[i], 0.001f, maxRatio);
                int estTris = Mathf.RoundToInt(sourceTris * generateLodRatios[i]);
                EditorGUILayout.LabelField($"      target ≈ {estTris:N0} tris ({generateLodRatios[i] * 100f:F0}% of source)", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(generateBudgetPriority ? "      budget first; errors reported after reduction" : "      actual depends on error / attribute limits", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            generateTargetError = EditorGUILayout.Slider("Target Error", generateTargetError, 0.001f, 0.5f);
            generateReductionMode = (LodReductionMode)EditorGUILayout.Popup("Reduction", (int)generateReductionMode,
                new[] { "Triangles", "Full loops from source", "Full loops + triangles" });
            if (generateReductionMode != LodReductionMode.FullLoops)
                generateBudgetPriority = EditorGUILayout.Toggle("Prioritize Triangle Budget",generateBudgetPriority);
            else EditorGUILayout.HelpBox("Full Loops can stop above the budget. Choose Triangles or Full Loops + Triangles for budget priority.",MessageType.Info);
            generateUv2Weight = EditorGUILayout.Slider("UV2 Weight", generateUv2Weight, 0f, 500f);
            generateNormalWeight = EditorGUILayout.Slider("Normal Weight", generateNormalWeight, 0f, 10f);
            generateColorWeight = EditorGUILayout.Slider("Color Weight", generateColorWeight, 0f, 10f);
            generateLockBorder = EditorGUILayout.Toggle("Lock Border", generateLockBorder);
            generatePreserveHardEdges = EditorGUILayout.Toggle(new GUIContent("Preserve Hard Edges", "Keep authored normal creases and their incident triangles; lock boundaries of the remaining patches. This strict constraint can prevent reaching the triangle budget."),generatePreserveHardEdges);
            generateValidateColors = EditorGUILayout.Toggle("Validate Vertex Colors", generateValidateColors);
            if (generateValidateColors)
                generateMaxColorError = EditorGUILayout.Slider("Max Color Error", generateMaxColorError, 0.001f, 1f);
            if (generateReductionMode != LodReductionMode.Triangles || generateValidateColors || generateBudgetPriority)
                generateQuality = EditorGUILayout.Popup("Quality", generateQuality, new[] { "Fast (1 variant)", "Balanced (3 variants)", "High (5 variants)" });
            if (generateBudgetPriority && generateReductionMode != LodReductionMode.FullLoops)
                generateCorrectAttributes = EditorGUILayout.Toggle(new GUIContent("Correct Normals / RGBA", "Fit surface fields against LOD0 after selecting the triangle candidate. Accept each channel only when RMS improves and maximum error does not increase."),generateCorrectAttributes);
            if (generateBudgetPriority && generateReductionMode != LodReductionMode.FullLoops)
                EditorGUILayout.HelpBox("Compares the selected number of quality variants at the same requested triangle budget. Ranks six-view silhouette and area RMS geometry/normals/UV/RGBA against source; limits report quality loss. Native costs/error can relax; Lock Border stays active.",MessageType.Info);
            else if (generateReductionMode == LodReductionMode.Triangles && generateValidateColors)
                EditorGUILayout.HelpBox("Validates sampled RGBA against LOD0. Tries stronger color weights, then safer triangle budgets when the requested budget fails.", MessageType.Info);
            if (generateReductionMode != LodReductionMode.Triangles)
            {
                generateMaxNormalAngle = EditorGUILayout.Slider("Max Normal Angle", generateMaxNormalAngle, 0.1f, 90f);
                EditorGUILayout.HelpBox(
                    "Reads original FBX quads and current working attributes. Removes complete regular loops; protects seams, materials and creases. " +
                    "Error is sampled against LOD0, relative to its bounds diagonal. Lock Border blocks open loops. " +
                    (generateReductionMode == LodReductionMode.LoopsThenTriangles ? "The final triangle stage can change quad flow." : "Stops when no complete loop meets the limits."), MessageType.Info);
            }

            if (generateTargetError < 0.1f && generateUv2Weight > 50f)
                EditorGUILayout.HelpBox(
                    "Low Target Error + High UV2 Weight may prevent reaching target polygon count. " +
                    "Try: Target Error 0.1–0.3, UV2 Weight 10–30, Lock Border OFF.",
                    MessageType.Warning);

            EditorGUILayout.Space(6);

            DrawFarLodSettings(startLod);
            DrawSmallPartsSettings();
            var bg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.7f, .4f, .95f);
            if (GUILayout.Button($"Generate LOD{startLod}–LOD{startLod + generateLodCount - 1}", GUILayout.Height(30)))
                ExecGenerateLods(startLod);
            GUI.backgroundColor = bg;
        }

        void DrawResultsAndClear()
        {
            if (!string.IsNullOrEmpty(lastGenerationError)) EditorGUILayout.HelpBox(lastGenerationError, MessageType.Warning);
            if (lastResults.Count == 0 && generatedObjects.Count == 0) return;

            if (lastResults.Count > 0)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Generated", EditorStyles.boldLabel);
                foreach (var r in lastResults)
                {
                    float pct = r.actualRatio * 100f;
                    string warn = r.targetNotReached ? " ⚠" : "";
                    EditorGUILayout.LabelField(
                        $"  LOD{r.lodLevel}: {r.meshName} — {r.simplifiedTris:N0} tris ({pct:F0}%){warn}",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"      target ≈ {r.targetTris:N0} tris ({r.targetRatio:P1}), got {r.actualRatio:P1}",
                        EditorStyles.miniLabel);
                    if (r.sourceQuads > 0)
                        EditorGUILayout.LabelField($"      {r.sourceQuads:N0} source quads; {r.removedLoops} loops removed, {r.blockedLoops} rejected attempts; " +
                            $"distance {r.sourceDistance:P3}, normal {r.normalError:F2}°, color {r.colorError:G3}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"      RGBA max {FormatChannels(r.colorMax)}; area RMS {FormatChannels(r.colorRms)}", EditorStyles.miniLabel);
                    if (r.budgetPriority)
                    {
                        EditorGUILayout.LabelField($"      measured distance {r.sourceDistance:P3}, normal {r.normalError:F1}°",EditorStyles.miniLabel);
                        EditorGUILayout.LabelField($"      area RMS distance {r.sourceDistanceRms:P3}, normal {r.normalRms:F1}°; silhouette mean/max {r.silhouetteMean:P1}/{r.silhouetteMax:P1}",EditorStyles.miniLabel);
                        EditorGUILayout.LabelField($"      relative selection score {r.selectionScore:G4}; native probes {r.nativeProbes}",EditorStyles.miniLabel);
                        if (r.attributeCorrection != null)
                        {
                            EditorGUILayout.LabelField($"      correction: normals {(r.attributeCorrection.normalsAccepted ? "improved" : "kept")}, RGBA {(r.attributeCorrection.colorsAccepted ? "improved" : "kept")}",EditorStyles.miniLabel);
                            EditorGUILayout.LabelField($"      normal RMS {r.attributeCorrection.normalRmsBefore:F2}° → {r.attributeCorrection.normalRmsAfter:F2}°",EditorStyles.miniLabel);
                        }
                    }
                    if (r.qualityLimitsExceeded)
                        EditorGUILayout.HelpBox("Requested attribute tolerance exceeded. Triangle budget has priority; review the generated model.",MessageType.Warning);
                    if (r.removedParts > 0)
                        EditorGUILayout.LabelField($"      Removed {r.removedParts} parts / {r.removedPartTris:N0} source tris; " +
                            $"area {r.removedPartAreaFraction:P2}; estimated ≤{r.removedPartMaxPixels:F2} px",EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"      Target Error {r.targetError:G3}; RGBA limit {(r.allowedColorError > 0 ? r.allowedColorError.ToString("G3") : "off")}",EditorStyles.miniLabel);
                    if (r.evaluatedCandidates > 1)
                        EditorGUILayout.LabelField($"      selected variant {r.selectedCandidate} of {r.evaluatedCandidates}", EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(r.reductionNote)) EditorGUILayout.HelpBox(r.reductionNote, MessageType.Info);
                }
                if (lastResults.Any(r => r.targetNotReached))
                    EditorGUILayout.HelpBox(
                        "Target triangle count was not reached. Different LOD ratios can produce the same count " +
                        "when simplification is limited by Target Error, UV2 / Normal Weight, locked borders or mesh topology. " +
                        "Full-loop mode also requires a complete valid loop within its shape and attribute limits. " +
                        "Adjust the limits or select another reduction mode to allow more reduction.",
                        MessageType.Warning);
            }

            EditorGUILayout.Space(4);
            var bgClear = GUI.backgroundColor;
            GUI.backgroundColor = new Color(.9f, .3f, .3f);
            string clearLabel = lastResults.Count > 0 ? "Clear Results" : "Clear Generated LODs";
            if (GUILayout.Button(clearLabel, GUILayout.Height(20)))
                ClearGeneratedLods();
            GUI.backgroundColor = bgClear;
        }


        internal void ClearGeneratedLods()
        {
            LodGroupUtility.ClearGeneratedLods(ctx);
            lastResults.Clear();
            requestRepaint?.Invoke();
        }

        void ExecGenerateLods(int startLod)
        {
            if (ctx.LodGroup == null) return;
            lastGenerationError = null;
            // Refuse missing / changed source topology before replacing generated objects.
            if (!LodPipelineOps.TryPrepareSources(ctx, generateReductionMode, out var sources, out lastGenerationError))
            { UvtLog.Warn($"[GenerateLOD] {lastGenerationError}"); requestRepaint?.Invoke(); return; }
            if (!LodPipelineOps.TryPrepareParts(ctx,PartSettings(),out var parts,out lastGenerationError))
            { UvtLog.Warn($"[GenerateLOD] {lastGenerationError}"); requestRepaint?.Invoke(); return; }
            if (generatedObjects.Count > 0)
            {
                GetGenerationBaseline(out _, out startLod, out _);
            }
            lastResults.Clear();

            var opts = BuildGenerationOptions(startLod);

            var result = LodPipelineOps.Generate(ctx, startLod, opts, sources,parts, replaceGenerated: true);
            if (!result.ok)
            {
                lastGenerationError = result.error;
                if (result.cancelled) UvtLog.Warn($"[GenerateLOD] {result.error}");
                else UvtLog.Error($"[GenerateLOD] {result.error}");
                requestRepaint?.Invoke();
                return;
            }

            foreach (var info in result.perLod)
            {
                lastResults.Add(new GeneratedLodInfo
                {
                    meshName = info.meshName,
                    simplifiedTris = info.simplifiedTris,
                    lodLevel = info.lodLevel,
                    targetRatio = info.targetRatio,
                    actualRatio = info.actualRatio,
                    targetTris = info.targetTris,
                    targetNotReached = info.targetNotReached,
                    removedLoops = info.removedLoops,
                    blockedLoops = info.blockedLoops,
                    sourceQuads = info.sourceQuads,
                    sourceDistance = info.sourceDistance,
                    normalError = info.normalError,
                    colorError = info.colorError,
                    colorMax = info.colorMax,
                    colorRms = info.colorRms,
                    evaluatedCandidates = info.evaluatedCandidates,
                    selectedCandidate = info.selectedCandidate,
                    nativeProbes = info.nativeProbes,sourceDistanceRms = info.sourceDistanceRms,normalRms = info.normalRms,
                    silhouetteMean = info.silhouetteMean,silhouetteMax = info.silhouetteMax,selectionScore = info.selectionScore,
                    attributeCorrection = info.attributeCorrection,
                    reductionNote = info.reductionNote,
                    allowedColorError = info.allowedColorError, targetError = info.targetError,
                    removedParts = info.removedParts, removedPartTris = info.removedPartTris,
                    removedPartAreaFraction = info.removedPartAreaFraction, removedPartMaxPixels = info.removedPartMaxPixels,
                    budgetPriority = info.budgetPriority, qualityLimitsExceeded = info.qualityLimitsExceeded
                });
            }
            requestRepaint?.Invoke();
        }

        LodPipelineOps.Options BuildGenerationOptions(int startLod)
        {
            var opts = new LodPipelineOps.Options
            {
                count = generateLodCount,
                ratios = generateLodRatios,
                targetError = generateTargetError,
                uv2Weight = generateUv2Weight,
                normalWeight = generateNormalWeight,
                colorWeight = generateColorWeight,
                reductionMode = generateReductionMode,
                maxNormalAngle = generateMaxNormalAngle,
                maxColorError = generateMaxColorError,
                skipColorValidation = !generateValidateColors,
                candidateCount = generateQuality == 0 ? 1 : generateQuality == 1 ? 3 : 5,
                lockBorder = generateLockBorder,
                progressiveScaleInLightmap = false,
                smallParts = PartSettings(),
                prioritizeTriangleBudget = generateBudgetPriority && generateReductionMode != LodReductionMode.FullLoops,
                correctSurfaceAttributes = generateCorrectAttributes && generateBudgetPriority && generateReductionMode != LodReductionMode.FullLoops,
                preserveHardEdges = generatePreserveHardEdges
            };
            if (generateRelaxFarLods)
                opts = LodPipelineOps.RelaxFarLods(opts,startLod,generateRelaxFromLod,new LodPipelineOps.LevelQuality {
                    targetError = generateFarTargetError, maxNormalAngle = generateFarMaxNormalAngle,
                    maxColorError = generateFarMaxColorError, normalWeight = generateNormalWeight*generateFarNormalWeightScale,
                    colorWeight = generateColorWeight*generateFarColorWeightScale
                });
            return opts;
        }

        internal static float[] SteppedRatios(float sourceRatio,float reduction,int count)
        {
            var ratios = new float[count];
            for (int i = 0; i < count; i++) ratios[i] = sourceRatio/Mathf.Pow(reduction,i+1);
            return ratios;
        }

        public void OnDrawToolbarExtra() { }

        static string FormatChannels(Vector4 values) => $"({values.x:G3}, {values.y:G3}, {values.z:G3}, {values.w:G3})";
        public void OnDrawStatusBar() { }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { }

        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes()
        {
            yield return new UvCanvasView.FillModeEntry { name = "Shells" };
        }

        public void OnSceneGUI(SceneView sv)
        {
            if (!generatePruneParts || ctx?.LodGroup == null) return;
            using var scope = new Handles.DrawingScope(Handles.color);
            foreach (var entry in ctx.MeshEntries)
            {
                var mesh = entry.repackedMesh ?? entry.originalMesh;
                if (!entry.include || entry.lodIndex != ctx.SourceLodIndex || !entry.renderer || !mesh ||
                    !partProtection.TryGetValue(mesh,out var protection) || !protection.analysis.Matches(mesh)) continue;
                var plan = PreviewParts(entry,protection.analysis);
                using var transformScope = new Handles.DrawingScope(entry.renderer.localToWorldMatrix);
                foreach (var part in protection.analysis.parts)
                {
                    if (!plan.removed.Contains(part.id)) continue;
                    Handles.color = new Color(1,.35f,.1f);
                    Handles.DrawWireCube(part.bounds.center,part.bounds.size);
                    Handles.Label(part.bounds.center,$"Remove part {part.id}");
                }
            }
        }

        LodSmallParts.Settings PartSettings() => new LodSmallParts.Settings {
            enabled = generatePruneParts, firstLod = generatePruneFromLod, screenHeight = partScreenHeight,
            maxPixels = partMaxPixels, maxAreaFraction = partMaxArea, maxTriangleFraction = partMaxTriangles,
            protection = partProtection
        };

        LodSmallParts.Plan PreviewParts(MeshEntry entry,LodSmallParts.Analysis analysis)
            => PreviewParts(entry,analysis,out _,out _);

        LodSmallParts.Plan PreviewParts(MeshEntry entry,LodSmallParts.Analysis analysis,out float size,out float height)
        {
            var lods = new List<LOD>(ctx.LodGroup.GetLODs());
            LodGroupUtility.NormalizeSingleLodTransitionForGeneration(lods,1);
            height = lods.Count > 0 ? lods[0].screenRelativeTransitionHeight : .5f;
            for (int lod = 1; lod < partPreviewLod; lod++)
                height = lod < lods.Count ? lods[lod].screenRelativeTransitionHeight : height*.5f;
            var scale = ctx.LodGroup.transform.lossyScale;
            size = ctx.LodGroup.size*Mathf.Max(Mathf.Abs(scale.x),Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
            return LodSmallParts.Select(analysis,PartSettings(),partPreviewLod,height,
                entry.renderer ? entry.renderer.localToWorldMatrix : Matrix4x4.identity,size);
        }

        void DrawSmallPartsSettings()
        {
            generatePruneParts = EditorGUILayout.Toggle("Remove Small Parts",generatePruneParts);
            if (!generatePruneParts) return;
            EditorGUI.BeginChangeCheck();
            generatePruneFromLod = EditorGUILayout.IntSlider("Remove From LOD",generatePruneFromLod,1,4);
            partMaxPixels = EditorGUILayout.Slider("Max Part Size (px)",partMaxPixels,.25f,32);
            partScreenHeight = Mathf.Max(1,EditorGUILayout.IntField("Reference Screen Height",partScreenHeight));
            partMaxArea = EditorGUILayout.Slider("Max Removed Area",partMaxArea,0,.2f);
            partMaxTriangles = EditorGUILayout.Slider("Max Removed Triangles",partMaxTriangles,0,.5f);
            partPreviewLod = EditorGUILayout.IntSlider("Preview LOD",partPreviewLod,1,4);
            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
            EditorGUILayout.HelpBox("Whole disconnected parts only. Size is estimated at the LOD entry distance; main components, bounds extrema and deforming meshes are protected. Orange Scene View boxes show planned removals. Keep a part to protect it. Surface errors exclude explicitly removed parts.",MessageType.Info);
            if (GUILayout.Button("Analyze Small Parts"))
            {
                var previewParts = new Dictionary<Mesh,LodSmallParts.Protection>();
                var imports = new Dictionary<string,object>();
                UvProgress.Begin("Analyze small LOD parts",cancelable:true);
                try
                {
                    foreach (var entry in ctx.MeshEntries)
                    {
                        var mesh = entry.repackedMesh ?? entry.originalMesh;
                        if (!entry.include || entry.lodIndex != ctx.SourceLodIndex || !mesh || previewParts.ContainsKey(mesh)) continue;
                        if (UvProgress.CancelRequested) throw new OperationCanceledException("Small-part analysis cancelled.");
                        UvProgress.Report(0,mesh.name);
                        var analyzed = new LodSmallParts.Protection { analysis = LodSmallParts.Analyze(entry,mesh,imports,() => UvProgress.CancelRequested) };
                        if (partProtection.TryGetValue(mesh,out var previous) && previous.analysis.Matches(mesh) && previous.analysis.SameParts(analyzed.analysis))
                            analyzed.ids.UnionWith(previous.ids);
                        previewParts.Add(mesh,analyzed);
                    }
                    partProtection.Clear();
                    foreach (var item in previewParts) partProtection.Add(item.Key,item.Value);
                }
                catch (Exception ex) { lastGenerationError = ex.Message; }
                finally { UvProgress.End(); SceneView.RepaintAll(); }
            }
            var drawn = new HashSet<Mesh>();
            foreach (var entry in ctx.MeshEntries)
            {
                var mesh = entry.repackedMesh ?? entry.originalMesh;
                if (!entry.include || entry.lodIndex != ctx.SourceLodIndex || !mesh || !drawn.Add(mesh) || !partProtection.TryGetValue(mesh,out var protection)) continue;
                if (!protection.analysis.Matches(mesh)) { EditorGUILayout.HelpBox(mesh.name+": geometry changed; analyze again.",MessageType.Warning); continue; }
                var plan = PreviewParts(entry,protection.analysis,out float size,out float height);
                EditorGUILayout.LabelField($"{mesh.name}: {protection.analysis.parts.Length} parts; remove {plan.removed.Count} / {plan.triangles} tris",EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(protection.analysis.connectivity,EditorStyles.wordWrappedMiniLabel);
                foreach (var part in protection.analysis.parts)
                {
                    float pixels = LodSmallParts.Pixels(part,entry.renderer ? entry.renderer.localToWorldMatrix : Matrix4x4.identity,size,height,partScreenHeight);
                    string description = $"{part.triangles} tris; {pixels:F2} px; {part.area/protection.analysis.area:P2} area";
                    if (part.protectedReason != null)
                    {
                        EditorGUILayout.LabelField($"Part {part.id} — {part.protectedReason}",EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.LabelField(description,EditorStyles.wordWrappedMiniLabel);
                        continue;
                    }
                    bool keep = protection.ids.Contains(part.id);
                    bool updated = EditorGUILayout.ToggleLeft($"Keep part {part.id}"+(plan.removed.Contains(part.id) ? " — remove" : " — retained"),keep);
                    EditorGUILayout.LabelField(description,EditorStyles.wordWrappedMiniLabel);
                    if (updated == keep) continue;
                    if (updated) protection.ids.Add(part.id); else protection.ids.Remove(part.id);
                    SceneView.RepaintAll();
                }
            }
        }

        // ── Auto-detect LOD siblings ──

        // LODGroup supports at most eight levels; reject name-derived indices outside that range.
        /// <summary>
        /// Given a GameObject whose name ends with a LOD suffix (e.g. Gazebo_LOD0),
        /// find all sibling GameObjects under the same parent that share the same
        /// base name but with different LOD indices. Returns null if the name doesn't
        /// match the LOD pattern.
        /// </summary>
        internal static List<(GameObject go, int lodIndex)> FindLodSiblings(GameObject go) => LodGroupUtility.FindLodSiblings(go);
        internal static LODGroup CreateLodGroupStatic(List<(GameObject go, int lodIndex)> siblings) => LodGroupUtility.CreateLodGroupStatic(siblings);
        internal static void NormalizeSingleLodTransitionForGeneration(List<LOD> lods, int startLod) => LodGroupUtility.NormalizeSingleLodTransitionForGeneration(lods, startLod);
        internal static LODGroup CreateLodGroupFromRenderers(GameObject root) => LodGroupUtility.CreateLodGroupFromRenderers(root);

        void CreateLodGroup(List<(GameObject go, int lodIndex)> siblings)
        {
            var lodGroup = CreateLodGroupStatic(siblings);
            ctx.Refresh(lodGroup);
            requestRepaint?.Invoke();

            UvtLog.Info($"[LOD Gen] Created LODGroup on '{lodGroup.gameObject.name}' with {siblings.Count} LODs.");
        }

        void RefreshDetectedLodCache(GameObject selected, List<(GameObject go, int lodIndex)> siblings)
        {
            int selectionId = selected != null ? selected.GetInstanceID() : -1;
            if (selectionId == cachedLodSelectionId && cachedDetectedLods.Count == siblings.Count)
                return;

            cachedLodSelectionId = selectionId;
            cachedDetectedLods.Clear();
            foreach (var (go, lodIndex) in siblings)
            {
                var renderers = go.GetComponentsInChildren<Renderer>();
                int tris = 0;
                foreach (var r in renderers)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    tris += GetTriangleCount(mf != null ? mf.sharedMesh : null);
                }
                cachedDetectedLods.Add((go, lodIndex, renderers.Length, tris));
            }
        }

        bool SelectionHasRenderers(GameObject selected)
        {
            int selectionId = selected != null ? selected.GetInstanceID() : -1;
            if (selectionId != cachedRendererSelectionId)
            {
                cachedRendererSelectionId = selectionId;
                cachedSelectionHasRenderers = selected != null && selected.GetComponentInChildren<Renderer>() != null;
            }
            return cachedSelectionHasRenderers;
        }

        static int GetTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            return LodMeshData.TriangleCount(mesh);
        }
    }
}
