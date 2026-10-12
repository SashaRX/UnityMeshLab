using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class WindowPreferencesTests
    {
        readonly Dictionary<string, string> original = new Dictionary<string, string>();

        [SetUp]
        public void IsolatePreferences()
        {
            foreach (string section in new[] { "Hub", "RemeshBake", "RemeshPreview", "Fill.remesh_bake", "Fixture", "TexturePacker" })
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
                viewport.Up = MeshViewport3D.UpAxis.Z;
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
                Assert.That(viewport.Up, Is.EqualTo(MeshViewport3D.UpAxis.Z));
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
        public void TexturePackerTabRestoresSettingsAndReleasesThumbnailsOnSwitch()
        {
            var window = ScriptableObject.CreateInstance<UvToolHub>();
            try
            {
                var tool = window.FindTool<TextureChannelPackerTool>();
                Assert.That(tool, Is.Not.Null, "Packer must be discovered as a Mesh Lab tab");
                Assert.That(tool, Is.InstanceOf<IUvToolFullWindow>());
                Call(window, "SelectToolById", tool.ToolId);
                var panel = Get<TextureChannelPackerPanel>(tool, "panel");
                Assert.That(typeof(EditorWindow).IsAssignableFrom(panel.GetType()), Is.False);
                Set(panel, "followFbx", false);
                Set(panel, "textureName", "SavedPackerName");
                Set(panel, "widthIndex", 2);
                Set(panel, "heightIndex", 1);
                Set(panel, "square", false);
                Call(panel, "AddSource", "fixture", new TexturePackingImage(1, 1, new[] { new Color32(1, 2, 3, 4) }));
                var sources = (System.Collections.IDictionary)panel.GetType().GetField("loaded", Private).GetValue(panel);
                var source = sources["fixture"];
                var thumbnail = (Texture2D)source.GetType().GetField("thumbnail").GetValue(source);
                var tools = Get<List<IUvTool>>(window, "tools");
                var other = tools.Find(t => t != tool);
                Assert.That(other, Is.Not.Null);
                Call(window, "SelectToolById", other.ToolId);
                Assert.That(panel == null, Is.True, "Inactive tab must destroy its owned panel");
                Assert.That(thumbnail == null, Is.True, "Inactive tab must release its GPU thumbnail");
                Assert.That(Get<TextureChannelPackerPanel>(tool, "panel"), Is.Null);
                Call(window, "SelectToolById", tool.ToolId);
                panel = Get<TextureChannelPackerPanel>(tool, "panel");
                Assert.That(Get<string>(panel, "textureName"), Is.EqualTo("SavedPackerName"));
                Assert.That(Get<int>(panel, "widthIndex"), Is.EqualTo(2));
                Assert.That(Get<int>(panel, "heightIndex"), Is.EqualTo(1));
                Assert.That(Get<bool>(panel, "square"), Is.False);
                tool.OnActivate(null, null);
                Assert.That(Get<TextureChannelPackerPanel>(tool, "panel"), Is.SameAs(panel), "Repeated activation must not duplicate subscriptions");
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [UnityTest]
        public IEnumerator LegacyTexturePackerWindowMigratesIntoHubTab()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("Opening native editor windows requires a graphics device; run this test without -nographics.");
            var hub = EditorWindow.GetWindow<UvToolHub>();
            var legacy = EditorWindow.GetWindow<TextureChannelPackerWindow>();
            try
            {
                for (int i = 0; i < 10 && legacy; i++) yield return null;
                Assert.That(legacy == null, Is.True, "Restoring a legacy layout must close the old window");
                var tool = hub.FindTool<TextureChannelPackerTool>();
                Assert.That(tool, Is.Not.Null);
                Assert.That(Get<TextureChannelPackerPanel>(tool, "panel"), Is.Not.Null, "Migration must activate the hub tab");
            }
            finally
            {
                if (legacy) UnityEngine.Object.DestroyImmediate(legacy);
                if (hub) UnityEngine.Object.DestroyImmediate(hub);
            }
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
                "{\"toolId\":\"removed_tool\",\"leftWidth\":-1,\"rightWidth\":9999,\"zoom\":-5,\"shading\":999,\"upAxis\":999}");
            var window = ScriptableObject.CreateInstance<UvToolHub>();
            try {
                Assert.That(Get<float>(window, "sideW"), Is.EqualTo(220));
                Assert.That(Get<float>(window, "rightSideW"), Is.EqualTo(700));
                Assert.That(Get<UvCanvasView>(window, "canvas").Zoom, Is.EqualTo(.01f));
                Assert.That(Get<MeshViewport3D>(window, "viewport").Mode, Is.EqualTo(MeshViewport3D.Shading.Shaded));
                Assert.That(Get<MeshViewport3D>(window, "viewport").Up, Is.EqualTo(MeshViewport3D.UpAxis.Y));
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
