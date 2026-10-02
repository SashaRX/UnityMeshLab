using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshLabModuleTests
    {
        static readonly Type[] ToolTypes = {
            typeof(LightmapTransferTool), typeof(CleanupTool), typeof(CollisionMeshTool),
            typeof(VertexColorBakingTool), typeof(LodGenerationTool), typeof(PrefabBuilderTool),
            typeof(UvPackHierarchyTool), typeof(RemeshBakeTool), typeof(DiagnosticsTool)
        };

        [TestCaseSource(nameof(ToolTypes))]
        public void DisablingAnyTabPreservesEveryOtherToolAndItsLibraries(Type disabledType)
        {
            var baseline = new MeshLabModuleRegistry().CreateTools(ToolTypes, null);
            Assert.AreEqual(ToolTypes.Length, baseline.Count);
            string disabled = MeshLabModuleRegistry.Metadata(disabledType).Id;
            var registry = new MeshLabModuleRegistry(new[] { disabled });
            var remaining = registry.CreateTools(ToolTypes, null, baseline);
            Assert.AreEqual(ToolTypes.Length - 1, remaining.Count);
            Assert.IsFalse(remaining.Any(t => t.ToolId == disabled));
            foreach (var tool in baseline.Where(t => t.ToolId != disabled))
            {
                Assert.Contains(tool, remaining, "Unrelated tool instances and their state must survive");
                Assert.IsNull(registry.ToolUnavailableReason(tool.GetType()));
            }
            foreach (var library in MeshLabModuleRegistry.Libraries)
                Assert.IsNull(registry.LibraryUnavailableReason(library.Id));
        }

        [Test]
        public void MissingSimplificationDisablesOnlyItsConsumers()
        {
            var registry = new MeshLabModuleRegistry(disabledLibraries: new[] { MeshLabLibraries.Simplification });
            var remaining = registry.CreateTools(ToolTypes, null);
            CollectionAssert.AreEquivalent(new[] { "collision_mesh", "uv1_hierarchy", "vertex_color_baking" }, remaining.Select(t => t.ToolId));
            StringAssert.Contains("meshoptimizer", registry.ToolUnavailableReason(typeof(LodGenerationTool)));
            StringAssert.Contains("meshoptimizer", registry.ToolUnavailableReason(typeof(DiagnosticsTool)));
            CollectionAssert.Contains(registry.RequiredLibraries(typeof(DiagnosticsTool)).ToArray(), MeshLabLibraries.Simplification,
                "Required dependencies remain visible even when disabled");
            CollectionAssert.Contains(registry.RequiredLibraries(typeof(DiagnosticsTool)).ToArray(), MeshLabLibraries.UvTransfer);
        }

        [Test]
        public void MissingLibraryAndCyclesReturnReadableReasons()
        {
            var registry = new MeshLabModuleRegistry(libraries: new[] {
                new MeshLabLibrary("a", "A", "b"), new MeshLabLibrary("b", "B", "a")
            });
            StringAssert.Contains("dependency cycle", registry.LibraryUnavailableReason("a"));
            StringAssert.Contains("Missing library", registry.LibraryUnavailableReason("missing"));
        }

        [Test]
        public void DuplicateToolIdsDoNotDisplaceAnUnrelatedTool()
        {
            var tools = new MeshLabModuleRegistry().CreateTools(new[] {
                typeof(LightmapTransferTool), typeof(LightmapTransferTool), typeof(VertexColorBakingTool)
            }, null);
            Assert.AreEqual(1, tools.Count);
            Assert.IsInstanceOf<VertexColorBakingTool>(tools[0]);
        }

        [Test]
        public void DisabledToolsAreExcludedBeforeConstruction()
        {
            var registry = new MeshLabModuleRegistry(ToolTypes.Select(t => MeshLabModuleRegistry.Metadata(t).Id));
            Assert.IsEmpty(registry.CreateTools(ToolTypes, null));
        }

        [Test]
        public void DiagnosticsHostsTheSharedWorkflowWithoutUvTransferTab()
        {
            var context = new UvToolContext();
            var canvas = new UvCanvasView();
            Action<ShellUvHit> marker = _ => { };
            canvas.OnDoubleClickShell = marker;
            var tool = new DiagnosticsTool();
            try
            {
                tool.OnActivate(context, canvas);
                var workflow = (UvTransferWorkflow)typeof(DiagnosticsTool).GetField("workflow", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tool);
                Assert.AreSame(context, ((ISweepHost)workflow).Context);
                ((IBenchmarkHost)workflow).Bind(null);
                Assert.AreSame(marker, canvas.OnDoubleClickShell, "A headless workflow must not steal the hub canvas callbacks");
                Assert.IsFalse(typeof(IUvTool).IsAssignableFrom(typeof(UvTransferWorkflow)));
            }
            finally { tool.OnDeactivate(); }
        }

        [Test]
        public void DisablingAnotherTabDoesNotDeactivateOrResetTheActiveTool()
        {
            var settings = MeshLabProjectSettings.Instance;
            var previousTools = settings.disabledToolIds;
            var previousLibraries = settings.disabledLibraryIds;
            UvToolHub hub = null;
            try
            {
                settings.disabledToolIds = new List<string>();
                settings.disabledLibraryIds = new List<string>();
                hub = ScriptableObject.CreateInstance<UvToolHub>();
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var tools = (List<IUvTool>)typeof(UvToolHub).GetField("tools", flags).GetValue(hub);
                typeof(UvToolHub).GetMethod("SwitchTool", flags).Invoke(hub, new object[] { tools.FindIndex(t => t is RemeshBakeTool) });
                var active = hub.FindTool<RemeshBakeTool>();
                var canvas = (UvCanvasView)typeof(UvToolHub).GetField("canvas", flags).GetValue(hub);
                Action<ShellUvHit> marker = _ => { };
                canvas.OnDoubleClickShell = marker;
                settings.disabledToolIds.Add("uv2_transfer");
                typeof(UvToolHub).GetMethod("ConfigureModules", flags).Invoke(hub, null);
                Assert.AreSame(active, hub.FindTool<RemeshBakeTool>());
                Assert.AreSame(marker, canvas.OnDoubleClickShell);
                Assert.IsNull(hub.FindTool<LightmapTransferTool>());
            }
            finally
            {
                if (hub != null) UnityEngine.Object.DestroyImmediate(hub);
                settings.disabledToolIds = previousTools;
                settings.disabledLibraryIds = previousLibraries;
            }
        }

        [Test]
        public void SavedAssetsRemainValidAfterWorkingMeshIsDestroyedWithoutAnyTool()
        {
            const string folder = "Assets/MeshLabModuleTest";
            var mesh = new Mesh { name = "Working" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            try
            {
                Assert.IsFalse(AssetDatabase.IsValidFolder(folder));
                AssetDatabase.CreateFolder("Assets", "MeshLabModuleTest");
                var context = new UvToolContext();
                context.PipeSettings.savePath = folder;
                context.MeshEntries.Add(new MeshEntry { originalMesh = mesh, fbxMesh = mesh, include = true });
                context.Assets.SaveAllPublic();
                UnityEngine.Object.DestroyImmediate(mesh);
                var saved = AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/Working.asset");
                Assert.IsNotNull(saved);
                Assert.AreEqual("Working", saved.name);
                Assert.AreEqual(3, saved.vertexCount);
                Assert.AreEqual(3, saved.triangles.Length);
            }
            finally { if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void VariantFbxExportWorksWithoutAnyToolAndPreservesTheSource()
        {
            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter") != null))
                Assert.Ignore("FBX Exporter package is not installed");
            const string folder = "Assets/MeshLabExportModuleTest";
            const string sourcePath = folder + "/Body.fbx";
            const string variantPath = folder + "/Body_red.fbx";
            var mesh = new Mesh { name = "Body" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            mesh.RecalculateNormals();
            var root = new GameObject("Body");
            var filter = root.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            try
            {
                Assert.IsFalse(AssetDatabase.IsValidFolder(folder));
                AssetDatabase.CreateFolder("Assets", "MeshLabExportModuleTest");
                FbxExport.Write(sourcePath, root);
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceUpdate);
                var importer = (ModelImporter)AssetImporter.GetAtPath(sourcePath);
                importer.isReadable = true; importer.SaveAndReimport();
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath).GetComponentInChildren<MeshFilter>().sharedMesh;
                byte[] original = System.IO.File.ReadAllBytes(sourcePath);
                var context = new UvToolContext();
                context.MeshEntries.Add(new MeshEntry { originalMesh = mesh, fbxMesh = source, include = true,
                    renderer = renderer, meshFilter = filter });
                int before = 0, after = 0;
                context.Assets.BeforeWrite = () => before++;
                context.Assets.AfterWrite = () => after++;
                mesh.colors32 = new[] { new Color32(255, 0, 0, 255), new Color32(255, 0, 0, 255), new Color32(255, 0, 0, 255) };
                Assert.IsTrue(context.Assets.ExportVertexColorsToFbxAs(sourcePath, variantPath, context.MeshEntries));
                CollectionAssert.AreEqual(original, System.IO.File.ReadAllBytes(sourcePath));
                Assert.AreSame(mesh, context.MeshEntries[0].originalMesh);
                Assert.AreEqual(1, before); Assert.AreEqual(0, after, "variant export preserves the working context");
                var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath).GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.AreEqual(3, variant.colors32.Length);
                foreach (var color in variant.colors32) Assert.AreEqual(new Color32(255, 0, 0, 255), color);
                context.Assets.ExportIsolatedChannelsToFbx(sourcePath, context.MeshEntries, FbxExportIntent.VertexColors);
                Assert.AreEqual(2, before); Assert.AreEqual(1, after, "source writes notify the current tool through a contract");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void GeometrySimplificationCancelsBeforeCallingNativeCode()
        {
            var settings = new MeshSimplifier.GeometrySettings { targetTriangles = 1, maximumError = .1f };
            Assert.Throws<OperationCanceledException>(() => MeshSimplifier.SimplifyGeometry(
                new[] { Vector3.zero, Vector3.right, Vector3.up }, new[] { 0, 1, 2 }, settings, new CancellationToken(true), out _));
        }

        [Test]
        public void LodLibraryRegistersAndCleansResultsWithoutAnyTool()
        {
            var root = new GameObject("Body");
            var child = new GameObject("Body_LOD0"); child.transform.SetParent(root.transform);
            var source = new Mesh { name = "Body" };
            source.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            source.triangles = new[] { 0, 1, 2 };
            child.AddComponent<MeshFilter>().sharedMesh = source;
            var renderer = child.AddComponent<MeshRenderer>();
            var group = root.AddComponent<LODGroup>(); group.SetLODs(new[] { new LOD(.5f, new[] { renderer }) });
            Mesh generated = null;
            try
            {
                var context = new UvToolContext(); context.Refresh(group);
                var result = LodPipelineOps.Generate(context, 1, new LodPipelineOps.Options { count = 1, ratios = new[] { 1f }, targetError = .01f });
                Assert.IsTrue(result.ok, result.error);
                Assert.AreEqual(1, result.generatedObjects.Count);
                CollectionAssert.AreEqual(result.generatedObjects, context.GeneratedLodObjects);
                generated = result.generatedObjects[0].GetComponent<MeshFilter>().sharedMesh;
                LodGroupUtility.ClearGeneratedLods(context);
                Assert.IsEmpty(context.GeneratedLodObjects);
                Assert.AreEqual(1, context.LodGroup.GetLODs().Length);
                Assert.AreSame(source, child.GetComponent<MeshFilter>().sharedMesh);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (generated != null) UnityEngine.Object.DestroyImmediate(generated);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void GeometrySimplificationWorksWithoutARemeshOrLodTool(int targetTriangles)
        {
            var source = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var settings = new MeshSimplifier.GeometrySettings { targetTriangles = targetTriangles, maximumError = .1f };
            var result = MeshSimplifier.SimplifyGeometry(source, new[] { 0, 1, 2 }, settings, CancellationToken.None, out float error);
            Assert.AreEqual(3, result.indices.Length);
            Assert.AreEqual(0, error);
            CollectionAssert.AreEqual(source, result.positions);
        }
    }
}
