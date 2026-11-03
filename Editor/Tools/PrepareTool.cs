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
        public void OnSceneGUI(SceneView sv) { /* The steps edit through the runner, not the scene view. */ }

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
                for (int s = 0; s < result.Steps.Count; s++) DrawStepRow($"{index}/{s}", s, result.Steps[s]);
            }
        }

        void DrawStepRow(string key, int index, PrepareStepResult step)
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
            foreach (var line in step.Report.Lines.OrderByDescending(l => l.level)) DrawLine(line);
        }

        void DrawLine(PrepareLine line)
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
