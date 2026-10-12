using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ToolAssetSaveTests
    {
        const string Folder = "Assets/MeshLabToolSaveTests";
        readonly List<Object> owned = new List<Object>();
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);

        Mesh Triangle(string name)
        {
            var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.uv2 = new[] { new Vector2(.1f,.1f), new Vector2(.3f,.1f), new Vector2(.1f,.3f) };
            mesh.colors32 = Enumerable.Repeat(new Color32(20,40,60,255), 3).ToArray();
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); owned.Add(mesh); return mesh;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var item in owned) if (item != null && !EditorUtility.IsPersistent(item)) Object.DestroyImmediate(item);
            owned.Clear();
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        [TestCase(typeof(UvPackHierarchyTool))]
        [TestCase(typeof(VertexColorBakingTool))]
        [TestCase(typeof(PrefabBuilderTool))]
        [TestCase(typeof(RemeshBakeTool))]
        [TestCase(typeof(FoliageNormalsTool))]
        [TestCase(typeof(CollisionMeshTool))]
        public void PrivateResultsUseBothSharedSaveActions(Type tool)
        {
            Assert.IsTrue(typeof(IUvToolFbxExport).IsAssignableFrom(tool));
            Assert.IsTrue(typeof(IUvToolMeshExport).IsAssignableFrom(tool));
        }

        [Test]
        public void MeshSaveSelectsPrivateResultAndPreservesReverseSnapshot()
        {
            var context = new UvToolContext(); context.PipeSettings.savePath = Folder;
            var unrelated = Triangle("Unrelated");
            context.MeshEntries.Add(new MeshEntry { originalMesh = unrelated });
            var result = Triangle("Private");
            var entry = new MeshEntry { originalMesh = result, reverseTransferJson = "{}" };
            context.Assets.SaveToolMeshes(new[] { entry, new MeshEntry { originalMesh = unrelated, include = false } });
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Mesh>(Folder + "/Private.asset"));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Mesh>(Folder + "/Unrelated.asset"));
            Assert.AreSame(unrelated, context.MeshEntries.Single().originalMesh);
            Assert.AreEqual("{}", entry.reverseTransferJson);
        }

        [Test]
        public void VertexAoSaveUsesComputedValuesWithoutMutatingTheSource()
        {
            var context = new UvToolContext(); var seed = Triangle("Body");
            context.MeshEntries.Add(new MeshEntry { originalMesh = seed, fbxMesh = seed });
            var tool = new VertexColorBakingTool(); Set(tool, "ctx", context);
            Set(tool, "channelType", 0); Set(tool, "channelComp", 0);
            Set(tool, "bakedFinalAO", new Dictionary<Mesh, float[]> { [seed] = new[] { .25f, .5f, .75f } });
            var clones = new List<Mesh>(); var before = seed.colors32;
            try
            {
                var output = tool.SaveEntries(clones).Single().originalMesh;
                Assert.AreNotSame(seed, output);
                Assert.That(output.colors32[0].r, Is.EqualTo(64).Within(1));
                Assert.AreEqual(40, output.colors32[0].g);
                CollectionAssert.AreEqual(before, seed.colors32);
            }
            finally { foreach (var clone in clones) Object.DestroyImmediate(clone); }
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        (string path, GameObject instance, MeshEntry entry) Source(string name)
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "MeshLabToolSaveTests");
            var root = new GameObject(name); owned.Add(root);
            root.AddComponent<MeshFilter>().sharedMesh = Triangle(name);
            root.AddComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard"));
            owned.Add(root.GetComponent<MeshRenderer>().sharedMaterial);
            string path = Folder + "/" + name + ".fbx";
            FbxExport.Write(path, root);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = true; importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.generateSecondaryUV = false; importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.Import; importer.SaveAndReimport();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path)); owned.Add(instance);
            var filter = instance.GetComponentInChildren<MeshFilter>();
            return (path, instance, new MeshEntry { fbxMesh = filter.sharedMesh, originalMesh = filter.sharedMesh,
                meshFilter = filter, renderer = filter.GetComponent<MeshRenderer>() });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void HierarchyUvSaveUsesPackedCopyAndLeavesOtherChannelsAndContextIntact(bool overwrite)
        {
            var source = Source("Body"); var imported = source.entry.fbxMesh;
            var packed = Object.Instantiate(imported); owned.Add(packed);
            packed.uv2 = new[] { new Vector2(.2f,.4f), new Vector2(.6f,.4f), new Vector2(.2f,.8f) };
            var context = new UvToolContext(); var unrelated = Triangle("Unrelated");
            context.MeshEntries.Add(new MeshEntry { originalMesh = unrelated });
            var tool = new UvPackHierarchyTool(); Set(tool, "ctx", context);
            Set(tool, "hierarchyEntries", new List<MeshEntry> { source.entry });
            Get<Dictionary<Mesh,Mesh>>(tool,"packedMeshes")[imported] = packed;
            Get<Dictionary<Mesh,RepackResult>>(tool,"packedResults")[imported] = new RepackResult { ok = true };
            Assert.IsTrue(tool.CanExportFbx);
            var output = tool.SaveEntries(); Assert.AreSame(packed, output.Single().originalMesh);
            string variant = Folder + "/variant.fbx"; var before = File.ReadAllBytes(source.path);
            string guid = AssetDatabase.AssetPathToGUID(source.path);
            var expectedUv = imported.uv; var expectedColors = imported.colors32;
            int previewRestores = 0; context.Assets.BeforeWrite = () => previewRestores++;
            int requested = 0;
            context.Assets.ToolExportPathChooser = (path, intent, replace) =>
            {
                Assert.AreEqual(source.path, path); Assert.AreEqual(FbxExportIntent.UV1, intent);
                requested++; return replace ? "" : variant;
            };
            tool.ExportFbx(overwrite);
            Assert.AreEqual(1, requested); Assert.AreEqual(1, previewRestores);
            string target = overwrite ? source.path : variant;
            var fresh = AssetDatabase.LoadAssetAtPath<GameObject>(target).GetComponentInChildren<MeshFilter>().sharedMesh;
            CollectionAssert.AreEqual(packed.uv2, fresh.uv2);
            CollectionAssert.AreEqual(expectedUv, fresh.uv); CollectionAssert.AreEqual(expectedColors, fresh.colors32);
            Assert.AreSame(unrelated, context.MeshEntries.Single().originalMesh);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(source.path));
            if (overwrite) Assert.AreSame(fresh, source.entry.meshFilter.sharedMesh);
            else CollectionAssert.AreEqual(before, File.ReadAllBytes(source.path));
        }

        [Test]
        public void CancelledPrivateSaveDoesNotTouchSourceOrWorkingCopy()
        {
            var source = Source("Cancelled"); var clone = Object.Instantiate(source.entry.originalMesh); owned.Add(clone);
            clone.uv2 = Enumerable.Repeat(Vector2.one, clone.vertexCount).ToArray();
            var before = File.ReadAllBytes(source.path);
            var context = new UvToolContext();
            Assert.IsEmpty(context.Assets.ExportToolEntries(new[] { source.entry.PreviewCopy(clone) }, FbxExportIntent.UV1, true,
                (path,intent,replace) => null));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(source.path)); Assert.IsTrue(clone != null);
        }

        [TestCase(0, 0, FbxExportIntent.VertexColors)]
        [TestCase(2, 1, FbxExportIntent.UV1)]
        public void VertexToolExportsTheActualBakeWithoutApply(int type, int component, FbxExportIntent expectedIntent)
        {
            var source = Source("VertexOutput"); var seed = source.entry.originalMesh;
            var colors = seed.colors32; var uv0 = seed.uv;
            var context = new UvToolContext(); context.MeshEntries.Add(source.entry);
            var tool = new VertexColorBakingTool(); Set(tool, "ctx", context);
            Set(tool, "channelType", type); Set(tool, "channelComp", component);
            Set(tool, "bakedFinalAO", new Dictionary<Mesh,float[]> { [seed] = new[] { .25f,.5f,.75f } });
            context.Assets.ToolExportPathChooser = (path,intent,overwrite) =>
            { Assert.AreEqual(expectedIntent,intent); return ""; };
            tool.ExportFbx(true);
            var fresh = AssetDatabase.LoadAssetAtPath<GameObject>(source.path).GetComponentInChildren<MeshFilter>().sharedMesh;
            CollectionAssert.AreEqual(uv0, fresh.uv);
            if (type == 0)
            {
                Assert.That(fresh.colors32[0].r, Is.EqualTo(64).Within(1)); Assert.AreEqual(colors[0].g,fresh.colors32[0].g);
            }
            else
            {
                Assert.AreEqual(.25f,fresh.uv2[0].y); CollectionAssert.AreEqual(colors,fresh.colors32);
            }
            Assert.AreSame(fresh,source.entry.meshFilter.sharedMesh);
        }

        [Test]
        public void CollisionSaveGroupsResultsByTheirOwnFbx()
        {
            var first = Source("First_LOD0"); var second = Source("Second_LOD0");
            var root = new GameObject("CollisionRoot"); owned.Add(root);
            first.instance.transform.SetParent(root.transform); second.instance.transform.SetParent(root.transform);
            var lod = root.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(.1f, new[] { first.entry.renderer, second.entry.renderer }) });
            var context = new UvToolContext { LodGroup = lod };
            context.MeshEntries.Add(first.entry); context.MeshEntries.Add(second.entry);
            var tool = new CollisionMeshTool(); Set(tool,"ctx",context);
            var hulls = Get<List<Mesh>>(tool,"generatedMeshes"); hulls.Add(Triangle("First_COL")); hulls.Add(Triangle("Second_COL"));
            var results = (System.Collections.IList)tool.GetType().GetField("lastResults",Fields).GetValue(tool);
            var resultType = tool.GetType().GetNestedType("GeneratedCollisionInfo",BindingFlags.NonPublic);
            foreach (var entry in new[] { first.entry,second.entry })
            {
                var result = Activator.CreateInstance(resultType);
                resultType.GetField("meshName").SetValue(result,entry.fbxMesh.name);
                resultType.GetField("sourceTransform").SetValue(result,entry.renderer.transform);
                resultType.GetField("hullCount").SetValue(result,1); results.Add(result);
            }
            Assert.IsTrue((bool)tool.GetType().GetMethod("SaveToSidecar",Fields).Invoke(tool,null));
            foreach (string path in new[] { first.path,second.path })
            {
                var sidecar = SidecarStore.Load(path);
                Assert.AreEqual(1,sidecar.collisionEntries.Count);
                Assert.AreEqual(Path.GetFileNameWithoutExtension(path),sidecar.collisionEntries[0].sourceMeshName);
            }
        }

        [UnityTest]
        public IEnumerator RemeshCaptureKeepsOriginalFbxWhenSceneMeshChanges()
        {
            var first = Source("CapturedSource"); var second = Source("LaterSelection");
            var root = new GameObject("ManualCapture"); owned.Add(root);
            var filter = root.AddComponent<MeshFilter>(); filter.sharedMesh = first.entry.fbxMesh;
            root.AddComponent<MeshRenderer>().sharedMaterial = first.entry.renderer.sharedMaterial;
            using var pipeline = new RemeshPipeline();
            var settings = new RemeshSettings { sourceShape = RemeshShape.BoundingBox, minPartSize = 0, minRodVoxels = 0 };
            var run = pipeline.Run(root, settings, RemeshPipeline.Stage.Prepare, RemeshPipeline.Stage.Prepare);
            while (!run.IsCompleted) yield return null;
            Assert.IsTrue(run.Result, pipeline.Status);
            Assert.AreEqual(first.path, pipeline.CapturedFbxPath);
            filter.sharedMesh = second.entry.fbxMesh;
            Assert.AreEqual(second.path, MeshAssetOperations.ResolveModelFbx(root));
            Assert.AreEqual(first.path, pipeline.CapturedFbxPath, "Overwrite must use the captured FBX rather than the changed scene mesh.");
        }

        [Test]
        public void RemeshOverwriteWritesBakedGeometryMapsAndMaterialsAndKeepsGuid()
        {
            var source = Source("RemeshSource"); string guid = AssetDatabase.AssetPathToGUID(source.path);
            var manual = new GameObject("Unpacked reference"); owned.Add(manual);
            var manualFilter = manual.AddComponent<MeshFilter>(); manualFilter.sharedMesh = source.entry.fbxMesh;
            Assert.AreEqual(source.path, RemeshBakeTool.SourceFbx(source.instance));
            using var pipeline = new RemeshPipeline();
            var result = Triangle("Result"); result.vertices = new[] { Vector3.zero, Vector3.right * 2, Vector3.up * 2 }; result.RecalculateBounds();
            var pixels = Enumerable.Repeat(new Color32(180,100,50,255), 16).ToArray();
            var maps = new RemeshBaker.Maps { size = 4, color = pixels, normal = pixels, metal = pixels, ao = pixels,
                emission = Enumerable.Repeat(Color.black,16).ToArray() };
            ((List<RemeshPipeline.Node>)pipeline.Nodes).Add(new RemeshPipeline.Node { name = "BakedBody", mesh = result, maps = maps });
            typeof(RemeshPipeline).GetProperty("ResultName").SetValue(pipeline, "BakedBody");
            RemeshExporter.Export(pipeline, new RemeshSettings(), Folder, source.path);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(source.path));
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(source.path);
            var filter = model.GetComponentInChildren<MeshFilter>();
            Assert.AreEqual(3, filter.sharedMesh.vertexCount);
            Assert.That(filter.sharedMesh.bounds.size.magnitude, Is.EqualTo(result.bounds.size.magnitude).Within(.001f));
            var material = filter.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.IsTrue(EditorUtility.IsPersistent(material)); Assert.IsNotNull(material.mainTexture);
            Assert.IsNotNull(manualFilter.sharedMesh, "Stable sub-asset name must keep unpacked scene references valid.");
            Assert.That(manualFilter.sharedMesh.bounds.size.magnitude, Is.EqualTo(result.bounds.size.magnitude).Within(.001f));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/BakedBody_Remesh/BakedBody.prefab"));
        }

        [Test]
        public void WholeModelWriterFailureKeepsTheOriginalFbxAndGuid()
        {
            var source = Source("Rollback"); var before = File.ReadAllBytes(source.path);
            string guid = AssetDatabase.AssetPathToGUID(source.path);
            Assert.Throws<InvalidOperationException>(() => FbxExport.ReplaceAtomically(source.path, path =>
            { File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); throw new InvalidOperationException("failed writer"); }));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(source.path));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(source.path));
        }
#endif
    }
}
