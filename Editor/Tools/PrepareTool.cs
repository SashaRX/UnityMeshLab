// PrepareTool.cs — the Prepare tab: the prepare chain (PrepareRunner) over the models, prefabs
// and scene objects selected in the Project window or a scene. The chain's steps head the tab.
// The Chain page lists the selection with the recipe each target takes, and a card per step:
// what it does, and whether it runs this time. That is a run's choice: the recipes stay as they
// are, and Edit Recipes opens them in Project Settings. Preview plans the chain and changes
// nothing. Apply makes the changes, an Error in a plan blocking it. The Result page shows a
// block per target and a row per step, its report's lines under it.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [MeshLabTool("prepare", MeshLabLibraries.Hierarchy, MeshLabLibraries.Assets)]
    public sealed class PrepareTool : IUvTool, IUvToolFullWindow
    {
        enum Page { Chain, Result }

        static readonly GUIContent[] Pages =
        {
            new GUIContent("Chain", "The selection, the recipe each takes and the steps of this run."),
            new GUIContent("Result", "What the last Preview or Apply found or changed, per target and step."),
        };

        const string OffThisRun = "off for this run";
        const float StepWidth = 110, LineIndent = 30, LevelWidth = 70;

        readonly HashSet<string> offThisRun = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, bool> openRows = new Dictionary<string, bool>(StringComparer.Ordinal);
        List<(PrepareTarget target, PrepareChain chain)> selection = new List<(PrepareTarget, PrepareChain)>();
        List<PrepareResult> results = new List<PrepareResult>();
        IReadOnlyList<IPrepareStep> chainSteps;
        string resultTitle;
        Page page;
        Vector2 scroll;
        Dictionary<PrepareLevel, GUIStyle> levelStyles;
        GUIStyle stepBox;

        public string ToolName => "Prepare";
        public string ToolId => "prepare";
        public int ToolOrder => 46;
        public Action RequestRepaint { get; set; }

        public void OnActivate(UvToolContext ctx, UvCanvasView canvas)
        {
            Listen(OnSelectionChanged, true);
            ReadSelection();
        }

        public void OnDeactivate()
        {
            Listen(OnSelectionChanged, false);
        }

        public void OnRefresh() { /* The tab follows the selection, not the LODGroup context. */ }
        public void OnDrawSidebar() { /* Controls occupy the full window body. */ }
        public void OnDrawToolbarExtra() { /* No mesh canvas toolbar. */ }
        public void OnDrawStatusBar() { /* Results are shown on the Result page. */ }
        public void OnDrawCanvasOverlay(UvCanvasView canvas, float cx, float cy, float sz) { /* No mesh canvas. */ }
        public IEnumerable<UvCanvasView.FillModeEntry> GetFillModes() => Array.Empty<UvCanvasView.FillModeEntry>();
        // The steps edit through the runner; the scene view only shows a finding asked for (Show in Scene).
        public void OnSceneGUI(SceneView sv)
        {
            if (Event.current.type == EventType.Repaint) DrawFinding();
        }

        // The tab follows the selection and the recipes; their events are static.
        static void Listen(Action handler, bool on)
        {
            if (on)
            {
                Selection.selectionChanged += handler;
                MeshLabProjectSettings.Changed += handler;
            }
            else
            {
                Selection.selectionChanged -= handler;
                MeshLabProjectSettings.Changed -= handler;
            }
        }

        // A run starts after the GUI event that asked for it, outside its layout.
        static void Later(Action action) => EditorApplication.delayCall += () => action();

        void OnSelectionChanged()
        {
            ReadSelection();
            RequestRepaint?.Invoke();
        }

        // The selected targets and their chains; the chain's steps (names, purposes) from the first, else a recipe's.
        void ReadSelection()
        {
            selection = PrepareTarget.FromSelection().Select(t => (t, PrepareRunner.Chain(t))).ToList();
            chainSteps = selection.Count > 0 ? selection[0].chain.Steps : new PrepareRecipe { colliderSurfaces = false }.Steps();
        }

        public void OnDrawWindowContent()
        {
            InitStyles();
            EditorGUILayout.Space(4);
            DrawChainHeader();
            EditorGUILayout.Space(4);
            page = (Page)GUILayout.Toolbar((int)page, Pages);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (page == Page.Chain) DrawChainPage();
            else DrawResultPage();
            EditorGUILayout.EndScrollView();
        }

        // ── Header: what is selected → each step of the chain ──

        void DrawChainHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(SelectionName(), "What the chain runs on: the selection in the Project window or a scene."), stepBox);
                for (int i = 0; i < chainSteps.Count; i++)
                {
                    var step = chainSteps[i];
                    GUILayout.Label("→", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    using (new EditorGUI.DisabledScope(offThisRun.Contains(step.Name)))
                        GUILayout.Label(new GUIContent($"{i + 1} {step.Name}", step.Purpose), stepBox);
                }
                GUILayout.FlexibleSpace();
            }
        }

        string SelectionName()
        {
            if (selection.Count == 0) return "Nothing selected";
            var kinds = selection.GroupBy(s => s.target.Kind).Select(g => KindCount(g.Key, g.Count()));
            return string.Join(", ", kinds);
        }

        static string KindCount(PrepareTargetKind kind, int count)
        {
            switch (kind)
            {
                case PrepareTargetKind.ModelAsset: return $"{count} FBX";
                case PrepareTargetKind.PrefabAsset: return MeshValidation.Plural(count, "prefab", "prefabs");
                default: return MeshValidation.Plural(count, "scene object", "scene objects");
            }
        }

        static string KindName(PrepareTargetKind kind)
        {
            switch (kind)
            {
                case PrepareTargetKind.ModelAsset: return "FBX";
                case PrepareTargetKind.PrefabAsset: return "Prefab";
                default: return "Scene object";
            }
        }

        // ── Chain page: the selection, a card per step, Preview / Apply ──

        void DrawChainPage()
        {
            EditorGUILayout.Space(4);
            DrawSelection();
            EditorGUILayout.Space(6);
            for (int i = 0; i < chainSteps.Count; i++) DrawCard(i, chainSteps[i]);
            EditorGUILayout.Space(6);
            DrawButtons();
        }

        void DrawSelection()
        {
            EditorGUILayout.LabelField("Selected", EditorStyles.boldLabel);
            if (selection.Count == 0)
            {
                EditorGUILayout.HelpBox("Select FBX models, prefabs or scene objects in the Project window or a scene.", MessageType.Info);
                return;
            }
            foreach (var (target, chain) in selection)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(KindName(target.Kind), EditorStyles.miniLabel, GUILayout.Width(80));
                    if (GUILayout.Button(new GUIContent(target.Name, target.Display), EditorStyles.label)) Ping(target);
                    GUILayout.FlexibleSpace();
                    var recipe = new GUIContent(chain.Recipe != null ? chain.Recipe.name : "no recipe", chain.Match);
                    GUILayout.Label(recipe, chain.Recipe != null ? EditorStyles.miniBoldLabel : levelStyles[PrepareLevel.Unchanged]);
                }
            }
        }

        static void Ping(PrepareTarget target)
        {
            var obj = target.SceneObject != null ? target.SceneObject : AssetDatabase.LoadMainAssetAtPath(target.AssetPath);
            if (obj != null) EditorGUIUtility.PingObject(obj);
        }

        void DrawCard(int index, IPrepareStep step)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool on = !offThisRun.Contains(step.Name);
                bool next = EditorGUILayout.ToggleLeft(new GUIContent($"{index + 1}  {step.Name}",
                    "Run this step this time; the recipes are unchanged."), on, EditorStyles.boldLabel);
                if (next != on)
                {
                    if (next) offThisRun.Remove(step.Name);
                    else offThisRun.Add(step.Name);
                }
                EditorGUILayout.LabelField(step.Purpose, EditorStyles.wordWrappedMiniLabel);
                if (selection.Count > 0)
                    EditorGUILayout.LabelField(next ? InRecipes(index) : "Off for this run.", EditorStyles.miniLabel);
            }
        }

        // "On in the recipe of all 3", "On for 2 of 3 (off in their recipe or no recipe for the rest)".
        string InRecipes(int index)
        {
            int on = selection.Count(s => index < s.chain.Steps.Count && !(s.chain.Steps[index] is SkippedStep));
            if (on == selection.Count) return selection.Count == 1 ? "On in the recipe." : $"On in the recipe of all {selection.Count}.";
            if (on == 0) return "Off: off in the recipe, or no recipe matches.";
            return $"On for {on} of {selection.Count}; off in the recipe of the rest, or no recipe matches.";
        }

        void DrawButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(selection.Count == 0))
            {
                var color = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.6f, .75f, .9f);
                if (GUILayout.Button(new GUIContent("Preview", "Plan every step and change nothing."), GUILayout.Height(24)))
                    Later(() => Run(false));
                GUI.backgroundColor = new Color(.4f, .8f, .4f);
                if (GUILayout.Button(new GUIContent("Apply", "Make the changes; an error in any step's plan blocks them."), GUILayout.Height(24)))
                    Later(() => Run(true));
                GUI.backgroundColor = color;
            }
            if (GUILayout.Button(new GUIContent("Edit Recipes…", "Open the recipes in Project Settings ▸ Mesh Lab."), GUILayout.Height(20)))
                SettingsService.OpenProjectSettings("Project/Mesh Lab");
        }

        void Run(bool apply)
        {
            ReadSelection();
            var runs = new List<PrepareResult>();
            try
            {
                for (int i = 0; i < selection.Count; i++)
                {
                    var (target, chain) = selection[i];
                    if (EditorUtility.DisplayCancelableProgressBar(apply ? "Prepare: Apply" : "Prepare: Preview", target.Display, (float)i / selection.Count))
                        break;
                    var run = chain.Without(offThisRun, OffThisRun);
                    var result = apply ? PrepareRunner.Apply(target, run) : PrepareRunner.Preview(target, run);
                    result.Log();
                    runs.Add(result);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            results = runs;
            openRows.Clear();
            HideFinding();   // the run may have changed the mesh it was drawn from
            resultTitle = $"{(apply ? "Apply" : "Preview")} at {DateTime.Now:HH:mm:ss}";
            page = Page.Result;
            ReadSelection();
            RequestRepaint?.Invoke();
        }

        // ── Result page: a block per target, a row per step, its lines under it ──

        void DrawResultPage()
        {
            EditorGUILayout.Space(4);
            if (results.Count == 0)
            {
                EditorGUILayout.HelpBox("Preview or Apply the chain to see what it finds and changes.", MessageType.Info);
                return;
            }
            EditorGUILayout.LabelField(resultTitle, ResultCounts(), EditorStyles.boldLabel);
            for (int r = 0; r < results.Count; r++) DrawResult(r, results[r]);
        }

        string ResultCounts()
        {
            int blocked = results.Count(r => r.Refused != null);
            int changed = results.Count(r => r.Changes);
            return $"{MeshValidation.Plural(results.Count, "target", "targets")}, {changed} with changes, {blocked} not applied";
        }

        void DrawResult(int index, PrepareResult result)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("●", levelStyles[result.Level], GUILayout.Width(14));
                    if (GUILayout.Button(new GUIContent(result.Target.Name, result.Target.Display), EditorStyles.boldLabel)) Ping(result.Target);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(result.State(), result.Refused != null ? levelStyles[PrepareLevel.Error] : EditorStyles.miniLabel);
                }
                if (result.Recipe != null) EditorGUILayout.LabelField(result.Recipe, EditorStyles.wordWrappedMiniLabel);
                for (int s = 0; s < result.Steps.Count; s++) DrawStepRow($"{index}/{s}", s, result.Steps[s], result.Target);
            }
        }

        void DrawStepRow(string key, int index, PrepareStepResult step, PrepareTarget target)
        {
            bool hasLines = step.Skipped == null && step.Report.Lines.Count > 0;
            if (!openRows.TryGetValue(key, out bool open)) open = step.Report.Level >= PrepareLevel.Warning;
            using (new EditorGUILayout.HorizontalScope())
            {
                var style = step.Skipped != null ? levelStyles[PrepareLevel.Unchanged] : levelStyles[step.Report.Level];
                GUILayout.Label("●", style, GUILayout.Width(14));
                string title = $"{index + 1}  {step.Step.Name}";
                var rect = GUILayoutUtility.GetRect(StepWidth, EditorGUIUtility.singleLineHeight, GUILayout.Width(StepWidth));
                if (hasLines) open = EditorGUI.Foldout(rect, open, title, true);
                else EditorGUI.LabelField(new Rect(rect.x + 13, rect.y, rect.width - 13, rect.height), title);
                GUILayout.Label(step.Summary, EditorStyles.wordWrappedMiniLabel);
            }
            openRows[key] = open;
            if (!hasLines || !open) return;
            foreach (var line in step.Report.Lines.OrderByDescending(l => l.level)) DrawLine(line, target);
        }

        // A warning or an error says, under its message, why it matters and how it is fixed
        // (PrepareHelp); a mesh finding has a button that draws its faces, edges or vertices on
        // the model in the Scene view and frames them (FindingGeometry).
        void DrawHint(PrepareLine line, PrepareTarget target)
        {
            if (line.level < PrepareLevel.Warning) return;
            bool hasHint = PrepareHelp.TryGet(line.check, out var hint);
            bool draws = FindingGeometry.Draws(line.check);
            if (!hasHint && !draws) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(LineIndent + LevelWidth + 4);
                GUILayout.Label(hasHint ? hint.Why : "", EditorStyles.wordWrappedMiniLabel);
                if (line.check == LightmapCheck && GUILayout.Button(new GUIContent("Fix: clamp UV",
                        "Clamp the model's lightmap UVs that lie outside [0,1] onto the unit square's edge, in its FBX, now, and preview again. "
                        + "However far out they lie: far out, their faces are squashed on the edge and a repack is the better fix. "
                        + "The file's change has no Undo."), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    Later(() => ClampLightmap(target));
                if (!draws) return;
                // A subject is a path within its target: two instances of one model have the same.
                string key = TargetKey(target) + "|" + line.subject + "|" + line.check;
                bool shown = shownKey == key;
                if (GUILayout.Button(new GUIContent(shown ? "Hide" : "Show in Scene",
                        "Draw the faces, edges or vertices this finding is about on the model, and frame them in the Scene view."),
                        EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    if (shown) Later(HideFinding);
                    else Later(() => ShowFinding(key, target, line));
                }
            }
        }

        const string LightmapCheck = "Lightmap UV";

        // The Fix step's clamp for this one target, asked for by hand: whatever the recipe's limit
        // and whether or not the recipe has the step on. The preview follows, on the fixed model.
        void ClampLightmap(PrepareTarget target)
        {
            var step = new FixStep(float.MaxValue);
            var context = new PrepareContext(target, target.SceneObject, target.Kind == PrepareTargetKind.SceneObject);
            string skipped = step.SkipReason(context);
            if (skipped != null)
            {
                UvtLog.Warn($"[{PrepareRunner.Tag}] {target.Display}: the lightmap UVs were not clamped: {skipped}");
                return;
            }
            var report = new PrepareReport(target.Display);
            try
            {
                EditorUtility.DisplayProgressBar("Prepare: Fix", "Clamping lightmap UVs into [0,1]…", .5f);
                step.Apply(context, report);
            }
            finally { EditorUtility.ClearProgressBar(); }
            report.Log(FixStep.StepName);
            PreviewAgain();
        }

        // The results' own targets previewed again, whatever is selected now: the row a fix was
        // asked from shows the fixed model. A scene object deleted since keeps its last result.
        void PreviewAgain()
        {
            results = results.Select(result =>
            {
                var target = result.Target;
                if (target.Kind == PrepareTargetKind.SceneObject && target.SceneObject == null) return result;
                var preview = PrepareRunner.Preview(target, PrepareRunner.Chain(target).Without(offThisRun, OffThisRun));
                preview.Log();
                return preview;
            }).ToList();
            HideFinding();
            resultTitle = $"Preview at {DateTime.Now:HH:mm:ss}";
            RequestRepaint?.Invoke();
        }

        static string TargetKey(PrepareTarget target)
            => target.SceneObject != null ? "scene:" + target.SceneObject.GetInstanceID() : "asset:" + target.AssetPath;

        // ── A finding drawn on the model ──

        string shownKey;
        Transform shownNode;
        FindingShape shownShape;
        string shownLabel;
        const int MaxDrawn = 4000;
        static readonly Color FindingFill = new Color(1f, .15f, .15f, .45f), FindingLine = new Color(1f, .9f, .1f, 1f);

        void HideFinding()
        {
            shownKey = null; shownNode = null; shownShape = null; shownLabel = null;
            SceneView.RepaintAll();
            RequestRepaint?.Invoke();
        }

        // Selects the node the finding is about and, when it is in a scene, draws the finding on it.
        void ShowFinding(string key, PrepareTarget target, PrepareLine line)
        {
            HideFinding();
            var node = SubjectNode(target, line.subject);
            if (node == null) return;
            Selection.activeObject = node;
            EditorGUIUtility.PingObject(node);
            if (!node.scene.IsValid())
            {
                UvtLog.Info($"[{PrepareRunner.Tag}] {line.subject}: the finding is drawn on a scene object; place the model in a scene and prepare that object to see it");
                return;
            }
            var mesh = SubjectMesh(node, line.subject, out var skinned);
            if (mesh == null) return;
            var requirements = PrepareRunner.Chain(target).Recipe?.requirements;
            var readable = MeshAccess.Readable(mesh, out bool isCopy);
            FindingShape shape;
            try { shape = FindingGeometry.Build(line.check, readable, requirements); }
            finally { if (isCopy) UnityEngine.Object.DestroyImmediate(readable); }
            if (shape == null || shape.IsEmpty)
            {
                UvtLog.Info($"[{PrepareRunner.Tag}] {line.subject}: nothing of '{line.check}' is left on the mesh to draw; run Preview again");
                return;
            }
            if (skinned != null) Pose(shape, skinned);
            shownKey = key; shownNode = node.transform; shownShape = shape;
            shownLabel = $"{line.check}: {line.message}";
            Frame(shownNode, shape);
            SceneView.RepaintAll();
            RequestRepaint?.Invoke();
        }

        // The mesh a finding is about: the node's render mesh or a collider's (a collision mesh is
        // checked too), the one the subject names when the node carries several.
        static Mesh SubjectMesh(GameObject node, string subject, out SkinnedMeshRenderer skinned)
        {
            skinned = null;
            var meshes = new List<Mesh>();
            if (node.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) meshes.Add(filter.sharedMesh);
            if (node.TryGetComponent(out SkinnedMeshRenderer renderer) && renderer.sharedMesh != null) meshes.Add(renderer.sharedMesh);
            meshes.AddRange(node.GetComponents<MeshCollider>().Select(c => c.sharedMesh).Where(m => m != null));
            if (meshes.Count == 0) return null;
            // 'path' (mesh 'Name'): the second quoted name; without it the mesh is named as its node.
            var quoted = (subject ?? "").Split('\'');
            string name = quoted.Length > 3 ? quoted[3] : node.name;
            var mesh = meshes.FirstOrDefault(m => m.name == name) ?? meshes[0];
            if (renderer != null && renderer.sharedMesh == mesh) skinned = renderer;
            return mesh;
        }

        // A skinned mesh is drawn where it is rendered: its vertices in the current pose, not in
        // the bind pose its shared mesh holds. The bake keeps the vertex order.
        static void Pose(FindingShape shape, SkinnedMeshRenderer skinned)
        {
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                skinned.BakeMesh(baked, true);
                if (baked.vertexCount == shape.positions.Length) shape.positions = baked.vertices;
            }
            finally { UnityEngine.Object.DestroyImmediate(baked); }
        }

        static void Frame(Transform node, FindingShape shape)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) return;
            bool any = false;
            var bounds = new Bounds();
            foreach (int i in shape.Vertices())
            {
                var p = node.TransformPoint(shape.positions[i]);
                if (any) bounds.Encapsulate(p);
                else { bounds = new Bounds(p, Vector3.zero); any = true; }
            }
            if (!any) return;
            // A single vertex or a sliver frames to nothing: keep some of the model around it.
            float room = Mathf.Max(bounds.size.magnitude, .25f);
            bounds.Expand(room);
            view.Frame(bounds, false);
        }

        void DrawFinding()
        {
            if (shownShape == null) return;
            if (shownNode == null) { HideFinding(); return; }
            var shape = shownShape;
            var zTest = Handles.zTest;
            var color = Handles.color;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            Vector3 P(int i) => shownNode.TransformPoint(shape.positions[i]);
            Handles.color = FindingFill;
            for (int t = 0; t + 2 < shape.triangles.Count && t < MaxDrawn * 3; t += 3)
                Handles.DrawAAConvexPolygon(P(shape.triangles[t]), P(shape.triangles[t + 1]), P(shape.triangles[t + 2]));
            Handles.color = FindingLine;
            for (int t = 0; t + 2 < shape.triangles.Count && t < MaxDrawn * 3; t += 3)
                Handles.DrawAAPolyLine(2f, P(shape.triangles[t]), P(shape.triangles[t + 1]), P(shape.triangles[t + 2]), P(shape.triangles[t]));
            for (int e = 0; e + 1 < shape.edges.Count && e < MaxDrawn * 2; e += 2)
                Handles.DrawAAPolyLine(4f, P(shape.edges[e]), P(shape.edges[e + 1]));
            for (int k = 0; k < shape.points.Count && k < MaxDrawn; ++k)
            {
                var p = P(shape.points[k]);
                Handles.DotHandleCap(0, p, Quaternion.identity, HandleUtility.GetHandleSize(p) * .04f, EventType.Repaint);
            }
            Handles.Label(shownNode.position, shownLabel, EditorStyles.whiteBoldLabel);
            Handles.color = color;
            Handles.zTest = zTest;
        }

        static GameObject SubjectNode(PrepareTarget target, string subject)
        {
            // A prefab's own hierarchy, not its model's: a node may be the prefab's alone.
            string asset = target.Kind == PrepareTargetKind.PrefabAsset ? target.AssetPath : target.ModelPath ?? target.AssetPath;
            var root = target.SceneObject != null ? target.SceneObject : AssetDatabase.LoadAssetAtPath<GameObject>(asset ?? "");
            if (root == null) return null;
            string path = PrepareHelp.SubjectPath(subject);
            if (string.IsNullOrEmpty(path) || path == root.name) return root;
            var node = root.transform.Find(path);
            // A part (a LOD chain's subject) is named by its group key: its first node.
            if (node == null)
                node = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != root.transform && MeshNaming.GroupKey(t.name) == path);
            return node != null ? node.gameObject : root;
        }

        void DrawLine(PrepareLine line, PrepareTarget target)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(LineIndent);
                GUILayout.Label(line.level.ToString(), levelStyles[line.level], GUILayout.Width(LevelWidth));
                GUILayout.Label($"{line.subject} — {line.check}: {line.message}", EditorStyles.wordWrappedMiniLabel);
            }
            foreach (string detail in line.details)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(LineIndent + LevelWidth + 4);
                    GUILayout.Label(detail, EditorStyles.wordWrappedMiniLabel);
                }
            }
            DrawHint(line, target);
        }

        // Styles are built inside OnGUI, where EditorStyles exist.
        void InitStyles()
        {
            if (levelStyles != null) return;
            bool dark = EditorGUIUtility.isProSkin;
            levelStyles = new Dictionary<PrepareLevel, GUIStyle>
            {
                [PrepareLevel.Unchanged] = Colored(dark ? new Color(.6f, .6f, .6f) : new Color(.4f, .4f, .4f)),
                [PrepareLevel.Changed] = Colored(dark ? new Color(.45f, .8f, .45f) : new Color(.1f, .5f, .1f)),
                [PrepareLevel.Warning] = Colored(dark ? new Color(.95f, .75f, .3f) : new Color(.65f, .45f, 0f)),
                [PrepareLevel.Error] = Colored(dark ? new Color(.95f, .4f, .4f) : new Color(.75f, .1f, .1f)),
            };
            stepBox = new GUIStyle(EditorStyles.helpBox) { alignment = TextAnchor.MiddleCenter, fontSize = EditorStyles.miniLabel.fontSize + 1 };
        }

        static GUIStyle Colored(Color color)
        {
            var style = new GUIStyle(EditorStyles.miniBoldLabel);
            style.normal.textColor = color;
            return style;
        }
    }
}
