using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class WindowPreferencesTests
    {
        readonly Dictionary<string, string> original = new Dictionary<string, string>();

        [SetUp]
        public void IsolatePreferences()
        {
            foreach (string section in new[] { "Hub", "RemeshBake", "RemeshPreview", "Fill.remesh_bake", "Fixture" })
                Remember(MeshLabWindowPreferences.Key(section));
            // Tool activation can read and persist its own fill choice during cleanup.
            foreach (var type in MeshLabModuleRegistry.ToolTypes()) {
                var attr = type.GetCustomAttribute<MeshLabToolAttribute>();
                if (attr != null) Remember(MeshLabWindowPreferences.Key("Fill." + attr.Id));
            }
            Remember("MeshLab.RemeshBake.Settings");
        }

        void Remember(string key)
        {
            if (original.ContainsKey(key)) return;
            original.Add(key, EditorPrefs.HasKey(key) ? EditorPrefs.GetString(key) : null);
            EditorPrefs.DeleteKey(key);
        }

        [TearDown]
        public void RestorePreferences()
        {
            foreach (var pair in original)
                if (pair.Value == null) EditorPrefs.DeleteKey(pair.Key);
                else EditorPrefs.SetString(pair.Key, pair.Value);
            original.Clear();
        }

        [Test]
        public void ClosingAndReopeningRestoresLayoutDisplayAndRemeshControls()
        {
            var window = ScriptableObject.CreateInstance<UvToolHub>();
            try {
                Call(window, "SelectToolById", "remesh_bake");
                Set(window, "sideW", 417f); Set(window, "rightSideW", 286f);
                Set(window, "canvas3D", true); Set(window, "sideScroll", new Vector2(0, 137));
                var canvas = Get<UvCanvasView>(window, "canvas");
                canvas.ShowWireframe = false; canvas.ShowBorder = false; canvas.FillAlpha = .61f;
                canvas.ActiveFillModeIndex = canvas.FillModes.FindIndex(mode => mode.name == "None");
                var viewport = Get<MeshViewport3D>(window, "viewport");
                viewport.Mode = MeshViewport3D.Shading.Normals; viewport.ShowGrid = false; viewport.Lit = false;
                var tool = window.FindTool<RemeshBakeTool>();
                Set(tool, "chartFold", true); Get<bool[]>(tool, "folds")[1] = false;
                Set(Get<RemeshPreview>(tool, "previews"), "bumpMap", false);
                // Invoke the real close lifecycle via destruction, not a save helper.
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }

            window = ScriptableObject.CreateInstance<UvToolHub>();
            try {
                Assert.That(Get<int>(window, "activeToolIndex"), Is.EqualTo(Get<List<IUvTool>>(window, "tools").FindIndex(t => t.ToolId == "remesh_bake")));
                Assert.That(Get<float>(window, "sideW"), Is.EqualTo(417));
                Assert.That(Get<float>(window, "rightSideW"), Is.EqualTo(286));
                Assert.That(Get<bool>(window, "canvas3D"), Is.True);
                Assert.That(Get<Vector2>(window, "sideScroll").y, Is.EqualTo(137));
                var canvas = Get<UvCanvasView>(window, "canvas");
                Assert.That(canvas.ShowWireframe, Is.False); Assert.That(canvas.ShowBorder, Is.False);
                Assert.That(canvas.FillAlpha, Is.EqualTo(.61f));
                Assert.That(canvas.FillModes[canvas.ActiveFillModeIndex].name, Is.EqualTo("None"));
                var viewport = Get<MeshViewport3D>(window, "viewport");
                Assert.That(viewport.Mode, Is.EqualTo(MeshViewport3D.Shading.Normals));
                Assert.That(viewport.ShowGrid, Is.False); Assert.That(viewport.Lit, Is.False);
                var tool = window.FindTool<RemeshBakeTool>();
                Assert.That(Get<bool>(tool, "chartFold"), Is.True);
                Assert.That(Get<bool[]>(tool, "folds")[1], Is.False);
                Assert.That(Get<bool>(Get<RemeshPreview>(tool, "previews"), "bumpMap"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Serializable]
        public sealed class Defaults
        {
            public bool enabled = true;
            public float width = 360;
        }

        [Test]
        public void OlderPreferencesRetainDefaultsForNewFields()
        {
            EditorPrefs.SetString(MeshLabWindowPreferences.Key("Fixture"), "{\"width\":420}");
            var settings = MeshLabWindowPreferences.Load<Defaults>("Fixture");
            Assert.That(settings.width, Is.EqualTo(420));
            Assert.That(settings.enabled, Is.True);
        }

        [Test]
        public void ObsoleteToolAndInvalidValuesDoNotBreakWindowLayout()
        {
            EditorPrefs.SetString(MeshLabWindowPreferences.Key("Hub"),
                "{\"toolId\":\"removed_tool\",\"leftWidth\":-1,\"rightWidth\":9999,\"zoom\":-5,\"shading\":999}");
            var window = ScriptableObject.CreateInstance<UvToolHub>();
            try {
                Assert.That(Get<float>(window, "sideW"), Is.EqualTo(220));
                Assert.That(Get<float>(window, "rightSideW"), Is.EqualTo(700));
                Assert.That(Get<UvCanvasView>(window, "canvas").Zoom, Is.EqualTo(.01f));
                Assert.That(Get<MeshViewport3D>(window, "viewport").Mode, Is.EqualTo(MeshViewport3D.Shading.Shaded));
                Assert.That(Get<int>(window, "activeToolIndex"), Is.GreaterThanOrEqualTo(0));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
    }
}
