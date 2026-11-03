using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public enum MeshLabFbxFormat { Binary, ASCII }

    class MeshLabProjectSettings : ScriptableObject
    {
        const string AssetPath = "ProjectSettings/MeshLabSettings.asset";

        // ── Repack defaults ──
        public int atlasResolution = 256;
        public int shellPaddingPx  = 2;
        public int borderPaddingPx = 0;
        public bool repackPerMesh;

        // ── Output ──
        public string savePath = "Assets/UnityMeshLab/Output";
        public MeshLabFbxFormat fbxFormat = MeshLabFbxFormat.Binary;
        public bool embedFbxTextures;

        // ── UV2 pipeline ──
        public bool sidecarMode;

        // ── Vertex AO defaults ──
        public int aoChannelType;   // 0=VertexColor, 1-5=UV0-UV4
        public int aoChannelComp;   // 0=R/X, 1=G/Y, 2=B, 3=A, 4=RGB

        // ── Prepare ──
        // What the Colliders step gives each collider's object so a game knows its surface
        // (a component by type name, its asset field, the asset type) and the surface words.
        [SerializeField] internal SurfaceBindingSettings surfaceBinding = new SurfaceBindingSettings();
        // Which steps a target takes and with what settings: the first recipe whose name and
        // folder patterns match it (PrepareRecipe); one for SM_ models to begin with.
        [SerializeField] internal List<PrepareRecipe> recipes = new List<PrepareRecipe> { PrepareRecipe.StaticMeshes() };
        // The recipes' format, so a recipe saved before a field existed gets that field's default
        // once (MigrateRecipes); 1 added Validate and its requirements.
        const int RecipesVersion = 1;
        [SerializeField] int recipesVersion;
        // The importer preset before recipes (1.2.112): read once, moved into a recipe (MoveLegacyPreset).
        [SerializeField] internal UnityEditor.Presets.Preset importerPreset;
        [SerializeField] internal bool presetLodScreenPercentages;

        // ── Developer UI ──
        // When true, the UV2 Transfer tool exposes diagnostic / benchmarking
        // sections (Parameter Sweep, Log Filters, UV0 Analysis & Fix, the
        // SymSplit "Apply to target LODs (advanced)" toggle, Skip SymSplit
        // diagnostic). Off by default — keeps the day-to-day UI minimal for
        // production users. Toggleable per project from Project Settings ▸
        // Mesh Lab ▸ Developer.
        public bool showDebugUI;

        // Additive project-local module settings; stable IDs preserve existing assets.
        public List<string> disabledToolIds = new List<string>();
        public List<string> disabledLibraryIds = new List<string>();
        internal static event Action ModulesChanged;
        // Raised when a setting is changed in Project Settings (the Prepare tab reads its recipes again).
        internal static event Action Changed;


        static MeshLabProjectSettings s_Instance;

        public static MeshLabProjectSettings Instance
        {
            get
            {
                if (s_Instance != null) return s_Instance;
                var objs = UnityEditorInternal.InternalEditorUtility
                    .LoadSerializedFileAndForget(AssetPath);
                if (objs != null && objs.Length > 0)
                    s_Instance = objs[0] as MeshLabProjectSettings;
                bool created = s_Instance == null;
                if (created)
                {
                    s_Instance = CreateInstance<MeshLabProjectSettings>();
                    s_Instance.hideFlags = HideFlags.HideAndDontSave;
                }
                bool migrated = s_Instance.MigrateRecipes();
                bool moved = s_Instance.MoveLegacyPreset();
                if (created || migrated || moved)
                    Save();
                return s_Instance;
            }
        }

        /// <summary>
        /// Brings recipes saved in an older format up to date, once: each recipe from before
        /// Validate has it on with the default requirements (fields its file lacked, which load
        /// unset). Whether anything changed; a recipe's later choice is kept.
        /// </summary>
        internal bool MigrateRecipes()
        {
            if (recipesVersion >= RecipesVersion) return false;
            foreach (var recipe in (recipes ?? new List<PrepareRecipe>()).Where(r => r != null))
            {
                recipe.validate = true;
                recipe.requirements = new ModelRequirements();
            }
            recipesVersion = RecipesVersion;
            return true;
        }

        /// <summary>
        /// Moves the importer preset set before recipes into the first recipe without one, and
        /// clears the old setting; whether there was one to move.
        /// </summary>
        internal bool MoveLegacyPreset()
        {
            if (importerPreset == null) return false;
            var recipe = recipes?.FirstOrDefault(r => r != null && r.importerPreset == null);
            if (recipe != null)
            {
                recipe.importerPreset = importerPreset;
                recipe.presetLodScreenPercentages = presetLodScreenPercentages;
            }
            importerPreset = null;
            presetLodScreenPercentages = false;
            return true;
        }

        public static void Save()
        {
            if (s_Instance == null) return;
            UnityEditorInternal.InternalEditorUtility
                .SaveToSerializedFileAndForget(
                    new UnityEngine.Object[] { s_Instance }, AssetPath, true);
        }

        [SettingsProvider]
        static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Mesh Lab", SettingsScope.Project)
            {
                guiHandler = OnGUI,
                keywords = new HashSet<string>
                {
                    "mesh", "lab", "atlas", "repack", "padding",
                    "uv", "sidecar", "ao", "vertex", "lightmap", "fbx", "binary", "textures",
                    "prepare", "collider", "surface", "preset", "recipe"
                }
            };
        }

        static readonly string[] channelTypeNames = { "Vertex Color", "UV0", "UV1", "UV2", "UV3", "UV4" };
        static readonly string[] colorCompNames   = { "R", "G", "B", "A", "RGB" };
        static readonly string[] uvCompNames      = { "X", "Y" };

        static void OnGUI(string searchContext)
        {
            EditorGUIUtility.labelWidth = 220;
            var inst = Instance;

            EditorGUILayout.Space(8);
            EditorGUI.BeginChangeCheck();

            // ── Developer (top — gates the visibility of debug surfaces ──
            // across the whole package; users look here first when "where
            // did Sweep go?" comes up). ──
            EditorGUILayout.LabelField("Developer", EditorStyles.boldLabel);
            inst.showDebugUI = EditorGUILayout.Toggle(
                new GUIContent("Show Debug UI",
                    "When enabled, the hub shows the Diagnostics tab (parameter "
                    + "sweep, benchmark, log filters, hierarchical probe, FBX metrics) "
                    + "and the UV2 Transfer tool its UV0 Analysis & Fix section and "
                    + "advanced SymSplit / Repack toggles. Also reveals the "
                    + "'Tools ▸ Mesh Lab ▸ Diagnostics ▸ Export FBX Metrics' menu items and the "
                    + "'Assets ▸ Create ▸ Mesh Lab ▸ Sweep Test Suite' action. "
                    + "Off by default for a clean production UI."),
                inst.showDebugUI);

            DrawModules(inst);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Repack Defaults", EditorStyles.boldLabel);
            inst.atlasResolution = EditorGUILayout.IntField("Atlas Resolution", inst.atlasResolution);
            inst.shellPaddingPx  = EditorGUILayout.IntSlider("Shell Padding", inst.shellPaddingPx, 0, 16);
            inst.borderPaddingPx = EditorGUILayout.IntSlider("Border Padding", inst.borderPaddingPx, 0, 16);
            inst.repackPerMesh   = EditorGUILayout.Toggle("Repack Per Mesh", inst.repackPerMesh);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            inst.savePath = EditorGUILayout.TextField("Default Save Path", inst.savePath);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("FBX Export", EditorStyles.boldLabel);
            inst.fbxFormat = (MeshLabFbxFormat)EditorGUILayout.EnumPopup(
                new GUIContent("Format", "Used by FBX overwrite, export and Remesh saves."), inst.fbxFormat);
            using (new EditorGUI.DisabledScope(inst.fbxFormat != MeshLabFbxFormat.Binary))
                inst.embedFbxTextures = EditorGUILayout.Toggle(new GUIContent("Embed textures",
                    "Include texture files inside binary FBX. Off: keep external texture references."), inst.embedFbxTextures);
            if (inst.fbxFormat == MeshLabFbxFormat.ASCII)
                EditorGUILayout.HelpBox("ASCII FBX uses external textures; embedding is disabled.", MessageType.None);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("UV2 Pipeline", EditorStyles.boldLabel);
            inst.sidecarMode = EditorGUILayout.Toggle("Sidecar UV2 Mode", inst.sidecarMode);
            if (!inst.sidecarMode)
                EditorGUILayout.HelpBox(
                    "Persistent replay is OFF. FBX overwrite will use one-shot replay only.",
                    MessageType.None);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Vertex AO Defaults", EditorStyles.boldLabel);
            inst.aoChannelType = EditorGUILayout.Popup("Channel", inst.aoChannelType, channelTypeNames);
            string[] compNames = inst.aoChannelType == 0 ? colorCompNames : uvCompNames;
            if (inst.aoChannelComp >= compNames.Length) inst.aoChannelComp = 0;
            inst.aoChannelComp = EditorGUILayout.Popup("Component", inst.aoChannelComp, compNames);
            string label = channelTypeNames[inst.aoChannelType] + " " + compNames[inst.aoChannelComp];
            EditorGUILayout.LabelField("Default target:", label, EditorStyles.miniLabel);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Prepare: Recipes", EditorStyles.boldLabel);
            DrawRecipes(inst.recipes ?? (inst.recipes = new List<PrepareRecipe>()));

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Prepare: Collider Surfaces", EditorStyles.boldLabel);
            DrawSurfaceBinding(inst.surfaceBinding ?? (inst.surfaceBinding = new SurfaceBindingSettings()));

            if (EditorGUI.EndChangeCheck())
            {
                Save();
                Changed?.Invoke();
            }

            // Log Level — stays per-user in EditorPrefs via UvtLog.
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Logging (per-user)", EditorStyles.boldLabel);
            UvtLog.Current = (UvtLog.Level)EditorGUILayout.EnumPopup("Log Level", UvtLog.Current);

            // Maintenance
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Maintenance", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var bgc = GUI.backgroundColor;
                GUI.backgroundColor = new Color(.7f, .15f, .15f);
                if (GUILayout.Button("Delete All Sidecars", GUILayout.Height(22), GUILayout.MaxWidth(200)))
                {
                    if (EditorUtility.DisplayDialog("Delete All Sidecars",
                        "This will delete every _uv2data.asset in the project.\nContinue?",
                        "Delete", "Cancel"))
                    {
                        UvToolHub.NukeAllSidecarsStatic();
                    }
                }
                GUI.backgroundColor = bgc;

                if (GUILayout.Button(
                        new GUIContent("Reset to defaults",
                            "Reset every Mesh Lab project setting (Repack defaults, Output path, FBX export, "
                            + "Sidecar mode, Vertex AO defaults, prepare recipes and collider surfaces, Show Debug UI) back to its built-in "
                            + "default value. Does NOT touch existing sidecar files or scene state."),
                        GUILayout.Height(22), GUILayout.MaxWidth(180)))
                {
                    if (EditorUtility.DisplayDialog("Reset Mesh Lab Settings",
                        "Reset every project setting to its built-in default?",
                        "Reset", "Cancel"))
                    {
                        ResetToDefaults();
                    }
                }
            }
        }

        static void DrawRecipes(List<PrepareRecipe> recipes)
        {
            EditorGUILayout.LabelField(" ", "A model, prefab or scene object takes the first recipe its name and folder match "
                + "(a prefab or scene object made from a model, also the model's); the report names it, and why each step was skipped.",
                EditorStyles.wordWrappedMiniLabel);
            int remove = -1, up = -1;
            var serialized = new SerializedObject(Instance);
            for (int i = 0; i < recipes.Count; i++)
            {
                var recipe = recipes[i] ?? (recipes[i] = PrepareRecipe.StaticMeshes());
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        recipe.name = EditorGUILayout.TextField(new GUIContent($"{i + 1}. Recipe", "The recipe's name in the report."), recipe.name);
                        using (new EditorGUI.DisabledScope(i == 0))
                            if (GUILayout.Button(new GUIContent("↑", "Try this recipe before the one above."), GUILayout.Width(22))) up = i;
                        if (GUILayout.Button(new GUIContent("−", "Remove the recipe."), GUILayout.Width(22))) remove = i;
                    }
                    DrawRecipe(recipe, serialized.FindProperty($"recipes.Array.data[{i}].requirements"));
                }
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            if (remove >= 0) recipes.RemoveAt(remove);
            else if (up > 0) (recipes[up - 1], recipes[up]) = (recipes[up], recipes[up - 1]);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                if (GUILayout.Button("+ Recipe", GUILayout.Width(120)))
                    recipes.Add(new PrepareRecipe { name = "New recipe", namePattern = "" });
            }
        }

        static void DrawRecipe(PrepareRecipe recipe, SerializedProperty requirements)
        {
            recipe.namePattern = EditorGUILayout.TextField(new GUIContent("Name",
                "The name the model (or the model a prefab or scene object is made from) must have: * any characters, ? one; "
                + "several separated by ';'; empty: any name. Case is ignored."), recipe.namePattern);
            recipe.folderPattern = EditorGUILayout.TextField(new GUIContent("Folder",
                "The folder it must be in, or under (Assets/Art/Props): * part of one folder's name, ** any folders; "
                + "several separated by ';'; empty: any folder."), recipe.folderPattern);
            recipe.runOnImport = EditorGUILayout.Toggle(new GUIContent("Run on Import",
                "Prepare a model this recipe matches by itself the first time it is imported (a file new to the project). "
                + "Off: the recipe runs only from the Prepare tab, Tools ▸ Mesh Lab ▸ Prepare or a batch run."), recipe.runOnImport);

            recipe.validate = EditorGUILayout.ToggleLeft(new GUIContent("1  " + ValidateStep.StepName, new ValidateStep().Purpose), recipe.validate);
            using (new EditorGUI.DisabledScope(!recipe.validate))
            using (new EditorGUI.IndentLevelScope())
            {
                if (requirements != null)
                    EditorGUILayout.PropertyField(requirements, new GUIContent("Requirements",
                        "Each check Off, a Warning (shown) or an Error (blocks Apply), with its thresholds."), true);
            }
            recipe.preset = EditorGUILayout.ToggleLeft(new GUIContent("2  " + PresetStep.StepName, new PresetStep(null).Purpose), recipe.preset);
            using (new EditorGUI.DisabledScope(!recipe.preset))
            using (new EditorGUI.IndentLevelScope())
            {
                recipe.importerPreset = (UnityEditor.Presets.Preset)EditorGUILayout.ObjectField(new GUIContent("Importer Preset",
                    "The model importer preset. Empty: the step is skipped."), recipe.importerPreset, typeof(UnityEditor.Presets.Preset), false);
                recipe.presetLodScreenPercentages = EditorGUILayout.Toggle(new GUIContent("Apply LOD Percentages",
                    "Also apply the preset's LOD screen percentages (off: each model keeps its own)."), recipe.presetLodScreenPercentages);
            }
            recipe.materials = EditorGUILayout.ToggleLeft(new GUIContent("3  " + MaterialStep.StepName, new MaterialStep().Purpose), recipe.materials);
            recipe.colliders = EditorGUILayout.ToggleLeft(new GUIContent("4  " + ColliderStep.StepName, new ColliderStep().Purpose), recipe.colliders);
            using (new EditorGUI.DisabledScope(!recipe.colliders))
            using (new EditorGUI.IndentLevelScope())
            {
                recipe.colliderSurfaces = EditorGUILayout.Toggle(new GUIContent("Surfaces",
                    "Each collider gets its surface (Prepare: Collider Surfaces below)."), recipe.colliderSurfaces);
                recipe.colliderFallbackLod = EditorGUILayout.IntField(new GUIContent("Fallback LOD",
                    "The LOD level a model without _COL nodes collides with; -1: its coarsest."), recipe.colliderFallbackLod);
            }
            recipe.prefab = EditorGUILayout.ToggleLeft(new GUIContent("5  " + PrefabStep.StepName,
                new PrefabStep().Purpose + " Off: a model's own hierarchy is left as it is, so its Colliders step is skipped."), recipe.prefab);
        }

        static void DrawSurfaceBinding(SurfaceBindingSettings binding)
        {
            binding.componentType = EditorGUILayout.TextField(new GUIContent("Component",
                "The component the Colliders step puts on each collider's object so the game knows what it is made of, "
                + "by type name (full or short). Empty: colliders get no surface."), binding.componentType);
            binding.field = EditorGUILayout.TextField(new GUIContent("Field",
                "The component's field (public or serialized private) that takes the surface asset."), binding.field);
            binding.dataType = EditorGUILayout.TextField(new GUIContent("Asset Type",
                "The type of the surface assets; the one named as the surface is picked, the nearest to the model when several are."), binding.dataType);
            string unavailable = new ComponentSurfaceBinding(binding).Unavailable;
            EditorGUILayout.LabelField(" ", unavailable ?? "Each collider gets the surface asset its part's name, materials or model name names.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.LabelField(new GUIContent("Surface words",
                "A word of a name that starts with the keyword names the surface asset on the right (Seat_Wood_COL → Wood)."),
                new GUIContent("keyword → asset name"), EditorStyles.miniLabel);
            int remove = -1;
            for (int i = 0; i < binding.rules.Count; i++)
            {
                var rule = binding.rules[i] ?? (binding.rules[i] = new SurfaceRule());
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUIUtility.labelWidth);
                    rule.keyword = EditorGUILayout.TextField(rule.keyword);
                    GUILayout.Label("→", GUILayout.Width(16));
                    rule.asset = EditorGUILayout.TextField(rule.asset);
                    if (GUILayout.Button("−", GUILayout.Width(22))) remove = i;
                }
            }
            if (remove >= 0) binding.rules.RemoveAt(remove);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                if (GUILayout.Button("+ Surface word", GUILayout.Width(120))) binding.rules.Add(new SurfaceRule("", ""));
            }
        }

        static void DrawModules(MeshLabProjectSettings settings)
        {
            settings.disabledToolIds = settings.disabledToolIds ?? new List<string>();
            settings.disabledLibraryIds = settings.disabledLibraryIds ?? new List<string>();
            var registry = new MeshLabModuleRegistry(settings.disabledToolIds, settings.disabledLibraryIds);
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Tools & required libraries", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            foreach (var type in MeshLabModuleRegistry.ToolTypes().OrderBy(t => t.Name))
            {
                var metadata = MeshLabModuleRegistry.Metadata(type);
                if (metadata == null)
                {
                    EditorGUILayout.HelpBox(type.Name + ": missing MeshLabTool dependency declaration", MessageType.Warning);
                    continue;
                }
                bool enabled = !settings.disabledToolIds.Contains(metadata.Id);
                string required = string.Join(", ", registry.RequiredLibraries(type));
                bool next = EditorGUILayout.ToggleLeft(new GUIContent(type.Name, "Requires: " + required), enabled);
                SetDisabled(settings.disabledToolIds, metadata.Id, !next);
                EditorGUILayout.LabelField("    Requires: " + required, EditorStyles.wordWrappedMiniLabel);
                string reason = registry.ToolUnavailableReason(type);
                if (next && reason != null) EditorGUILayout.HelpBox(reason, MessageType.Warning);
            }
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Shared libraries", EditorStyles.boldLabel);
            foreach (var library in MeshLabModuleRegistry.Libraries)
            {
                bool enabled = !settings.disabledLibraryIds.Contains(library.Id);
                bool next = EditorGUILayout.ToggleLeft(new GUIContent(library.Name,
                    "Required libraries: " + string.Join(", ", library.Requires)), enabled);
                SetDisabled(settings.disabledLibraryIds, library.Id, !next);
            }
            if (EditorGUI.EndChangeCheck()) { Save(); ModulesChanged?.Invoke(); }
        }

        static void SetDisabled(List<string> disabled, string id, bool value)
        {
            if (value) { if (!disabled.Contains(id)) disabled.Add(id); }
            else disabled.Remove(id);
        }

        // Re-stamp every settable field with the value of a fresh instance —
        // single source of truth, no risk of skipping a future field.
        static void ResetToDefaults()
        {
            var inst = Instance;
            var fresh = CreateInstance<MeshLabProjectSettings>();
            try
            {
                inst.atlasResolution = fresh.atlasResolution;
                inst.shellPaddingPx  = fresh.shellPaddingPx;
                inst.borderPaddingPx = fresh.borderPaddingPx;
                inst.repackPerMesh   = fresh.repackPerMesh;
                inst.savePath        = fresh.savePath;
                inst.fbxFormat       = fresh.fbxFormat;
                inst.embedFbxTextures = fresh.embedFbxTextures;
                inst.sidecarMode     = fresh.sidecarMode;
                inst.aoChannelType   = fresh.aoChannelType;
                inst.aoChannelComp   = fresh.aoChannelComp;
                inst.showDebugUI     = fresh.showDebugUI;
                inst.disabledToolIds = fresh.disabledToolIds;
                inst.disabledLibraryIds = fresh.disabledLibraryIds;
                inst.surfaceBinding  = fresh.surfaceBinding;
                inst.recipes         = fresh.recipes;
                ModulesChanged?.Invoke();
                Save();
            }
            finally
            {
                DestroyImmediate(fresh);
            }
        }
    }
}
