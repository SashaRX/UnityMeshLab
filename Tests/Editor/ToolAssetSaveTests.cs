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

        internal static bool SaveCollisionSidecarFixture(CollisionMeshTool tool)
        {
            var staged = tool.PrepareSidecars();
            if (staged == null) return false;
            try
            {
                foreach (var pair in staged)
                {
                    var data = SidecarStore.LoadOrCreate(pair.Key);
                    data.collisionEntries = new List<CollisionMeshEntry>(pair.Value.collisionEntries);
                    SidecarStore.Save(data);
                }
                return staged.Count > 0;
            }
            finally { foreach (var data in staged.Values) Object.DestroyImmediate(data); }
        }

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

        [Test]
        public void CollisionExportRequiresGeneratedResultsAndCannotFallBackAfterClear()
        {
            var context = new UvToolContext();
            context.MeshEntries.Add(new MeshEntry { originalMesh = Triangle("Unrelated") });
            var tool = new CollisionMeshTool(); Set(tool, "ctx", context);
            int writes = 0; context.Assets.BeforeWrite = () => writes++;
            Assert.IsFalse(tool.CanExportFbx);
            tool.ExportFbx(true); tool.ExportFbx(false); Assert.AreEqual(0, writes);
            var generated = Get<List<Mesh>>(tool, "generatedMeshes");
            generated.Add(Triangle("Collision")); Assert.IsFalse(tool.CanExportFbx, "Incomplete collision metadata must also refuse export.");
            generated.Clear(); Assert.IsFalse(tool.CanExportFbx);
            tool.ExportFbx(true); Assert.AreEqual(0, writes);
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [Test]
        public void ModelReplacementInvalidatesOnlyUvSidecarBeforeFailingFirstImport()
        {
            var source = Source("Replacement");
            var data = SidecarStore.LoadOrCreate(source.path);
            string meshName = source.entry.fbxMesh.name;
            data.Set(meshName, new[] { Vector2.zero, Vector2.right, Vector2.up });
            data.toolSettings = new ToolSettings();
            SidecarStore.Save(data);
            var before = File.ReadAllBytes(source.path);
            int committed = 0, imports = 0;
            var replacement = new GameObject("NewGeometry"); owned.Add(replacement);
            replacement.AddComponent<MeshFilter>().sharedMesh = Triangle("Changed");
            replacement.AddComponent<MeshRenderer>().sharedMaterial = source.instance.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            Assert.Throws<IOException>(() => MeshAssetOperations.WriteModelResult(source.path, replacement,
                Array.Empty<Material>(), false, () => committed++, (path, options) => {
                    ++imports;
                    Assert.IsNull(SidecarStore.Load(path).Find(meshName));
                    throw new IOException("Injected first import failure");
                }));
            Assert.AreEqual(1, committed); Assert.AreEqual(1, imports);
            CollectionAssert.AreNotEqual(before, File.ReadAllBytes(source.path));
            Assert.IsEmpty(SidecarStore.Load(source.path).entries);
            Assert.IsNotNull(SidecarStore.Load(source.path).toolSettings);
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public void ContextRefreshRestoresOnlyMatchingReverseProvenance(bool lod, bool staleUv)
        {
            var source = Source("ReverseHistory"); var mesh = source.entry.fbxMesh;
            mesh.uv2 = mesh.uv;
            var entry = source.entry; entry.reverseTransferJson = "{\"schema\":2}";
            Assert.IsTrue(SidecarStore.TryBuildEntry(entry, mesh, false, SidecarStore.AoUvTarget.None, out var saved));
            var data = SidecarStore.LoadOrCreate(source.path); data.Set(saved); SidecarStore.Save(data);
            if (staleUv) mesh.uv2 = mesh.uv.Select(uv => uv * .5f).ToArray();
            var context = new UvToolContext();
            if (lod)
            {
                var group = source.instance.AddComponent<LODGroup>();
                group.SetLODs(new[] { new LOD(.5f, new[] { source.entry.renderer }) });
                context.Refresh(group);
            }
            else context.RefreshStandalone((MeshRenderer)source.entry.renderer);
            Assert.AreEqual(staleUv ? null : entry.reverseTransferJson, context.MeshEntries.Single().reverseTransferJson);
            Assert.AreEqual("{\"schema\":2}", data.entries.Single().reverseTransferJson);
        }

        (string path, GameObject instance, MeshEntry entry) Source(string name, bool nested = false)
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "MeshLabToolSaveTests");
            var root = new GameObject(nested ? name + "_Model" : name); owned.Add(root);
            var geometry = nested ? new GameObject(name) : root;
            if (nested) geometry.transform.SetParent(root.transform);
            geometry.AddComponent<MeshFilter>().sharedMesh = Triangle(name);
            geometry.AddComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard"));
            owned.Add(geometry.GetComponent<MeshRenderer>().sharedMaterial);
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

        [TestCase(false)]
        [TestCase(true)]
        public void SolidColorSaveRequiresCurrentBake(bool hierarchy)
        {
            var source = Source("SolidColor");
            var context = new UvToolContext(); context.MeshEntries.Add(source.entry);
            var tool = new VertexColorBakingTool(); Set(tool, "ctx", context);
            Set(tool, "bakeKind", VertexColorBakingTool.BakeKind.SolidColor);
            Set(tool, "hierarchyMode", hierarchy);
            if (hierarchy) Set(tool, "hierarchyEntries", new List<MeshEntry> { source.entry });
            Assert.IsFalse(tool.CanSaveMeshAssets); Assert.IsFalse(tool.CanExportFbx);
            var clones = new List<Mesh>();
            try
            {
                Assert.IsEmpty(tool.SaveEntries(clones));
                tool.GetType().GetMethod("ExecuteBakeFirstVariant", Fields).Invoke(tool, null);
                Assert.IsTrue(tool.CanSaveMeshAssets); Assert.IsTrue(tool.CanExportFbx);
                var red = tool.SaveEntries(clones).Single().originalMesh;
                Assert.That(red.colors32, Is.All.EqualTo(new Color32(255, 0, 0, 255)));
                var variants = Get<List<VariantExportPipeline.Variant>>(tool, "variants");
                variants[0] = new VariantExportPipeline.Variant { color = Color.green, suffix = "Green" };
                Assert.IsFalse(tool.CanSaveMeshAssets); Assert.IsFalse(tool.CanExportFbx);
                Assert.IsEmpty(tool.SaveEntries(clones), "Direct save also refuses a changed swatch.");
                tool.GetType().GetMethod("ExecuteBakeFirstVariant", Fields).Invoke(tool, null);
                Assert.IsTrue(tool.CanSaveMeshAssets); Assert.IsTrue(tool.CanExportFbx);
                var green = tool.SaveEntries(clones).Single().originalMesh;
                Assert.That(green.colors32, Is.All.EqualTo(new Color32(0, 255, 0, 255)));
                VertexChannels.FillColors(source.entry.originalMesh, Color.blue, "External color edit");
                Assert.IsFalse(tool.CanSaveMeshAssets); Assert.IsFalse(tool.CanExportFbx);
                Assert.IsEmpty(tool.SaveEntries(clones), "A changed mesh invalidates the solid bake.");
            }
            finally { foreach (var clone in clones) Object.DestroyImmediate(clone); }
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
            Assert.IsTrue(SaveCollisionSidecarFixture(tool));
            foreach (string path in new[] { first.path,second.path })
            {
                var sidecar = SidecarStore.Load(path);
                Assert.AreEqual(1,sidecar.collisionEntries.Count);
                Assert.AreEqual(Path.GetFileNameWithoutExtension(path),sidecar.collisionEntries[0].sourceMeshName);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CollisionBatchRefusesInvalidLaterGroupBeforeChangingAnySidecar(bool existingSidecars)
        {
            var sources = new[] { Source("First_LOD0"), Source("Invalid_LOD0"), Source("Last_LOD0") };
            var root = new GameObject("CollisionRoot"); owned.Add(root);
            foreach (var source in sources) source.instance.transform.SetParent(root.transform);
            var lod = root.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(.1f, sources.Select(source => source.entry.renderer).ToArray()) });
            var context = new UvToolContext { LodGroup = lod };
            context.MeshEntries.AddRange(sources.Select(source => source.entry));
            PrefabUtility.UnpackPrefabInstance(sources[1].instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var tool = new CollisionMeshTool(); Set(tool, "ctx", context);
            var before = new Dictionary<string, byte[]>();
            foreach (var source in sources)
            {
                if (existingSidecars)
                {
                    var data = SidecarStore.LoadOrCreate(source.path);
                    data.collisionEntries.Add(new CollisionMeshEntry { meshGroupKey = "Existing", sourceMeshName = "Existing" });
                    SidecarStore.Save(data);
                    foreach (var path in new[] { SidecarStore.PathFor(source.path), SidecarStore.PathFor(source.path) + ".meta" })
                        before[path] = File.ReadAllBytes(path);
                }
                before[source.path] = File.ReadAllBytes(source.path);
                before[source.path + ".meta"] = File.ReadAllBytes(source.path + ".meta");
                AddCollisionResult(tool, source.entry, source.path == sources[1].path ? "MissingSource" : source.entry.fbxMesh.name);
            }
            int writes = 0; context.Assets.BeforeWrite = () => writes++;
            context.Assets.ToolExportPathChooser = (path, intent, overwrite) => throw new InvalidOperationException("No FBX write expected.");
            // A manually edited source has neither prefab identity nor a matching
            // imported FBX node. The earlier and later groups are valid.
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Cannot identify source instance 'MissingSource'.*sidecar was not changed"));
            tool.ExportFbx(true);
            Assert.AreEqual(0, writes);
            foreach (var file in before) CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key), file.Key);
            foreach (var source in sources)
            {
                var sidecar = SidecarStore.Load(source.path);
                if (existingSidecars) Assert.AreEqual("Existing", sidecar.collisionEntries.Single().meshGroupKey);
                else Assert.IsNull(sidecar, "A refused batch must not create an earlier or later sidecar.");
            }
        }

        void AddCollisionResult(CollisionMeshTool tool, MeshEntry entry, string meshName)
        {
            Get<List<Mesh>>(tool, "generatedMeshes").Add(Triangle(meshName + "_COL"));
            var results = (IList)tool.GetType().GetField("lastResults", Fields).GetValue(tool);
            var resultType = tool.GetType().GetNestedType("GeneratedCollisionInfo", BindingFlags.NonPublic);
            var result = Activator.CreateInstance(resultType);
            resultType.GetField("meshName").SetValue(result, meshName);
            resultType.GetField("sourceTransform").SetValue(result, entry.renderer.transform);
            resultType.GetField("hullCount").SetValue(result, 1);
            results.Add(result);
        }

        (CollisionMeshTool tool, UvToolContext context) CollisionTool(MeshEntry entry, GameObject instance)
        {
            var lod = instance.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(.1f, new[] { entry.renderer }) });
            var context = new UvToolContext { LodGroup = lod };
            context.MeshEntries.Add(entry);
            var tool = new CollisionMeshTool(); Set(tool, "ctx", context);
            return (tool, context);
        }

        Dictionary<string, byte[]> SaveFileSnapshot(string path)
        {
            var snapshot = new Dictionary<string, byte[]>();
            foreach (string file in new[] { path, path + ".meta", SidecarStore.PathFor(path), SidecarStore.PathFor(path) + ".meta" })
                if (File.Exists(file)) snapshot.Add(file, File.ReadAllBytes(file));
            return snapshot;
        }

        static void AssertFilesUnchanged(Dictionary<string, byte[]> before)
        {
            foreach (var file in before) CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key), file.Key);
        }

        [Test]
        public void CollisionExportRequiresGeneratedResultsAndDisablesAfterClear()
        {
            var source = Source("Availability_LOD0");
            var (tool, context) = CollisionTool(source.entry, source.instance);
            int requested = 0;
            context.Assets.ToolExportPathChooser = (path, intent, overwrite) => { requested++; return null; };
            Assert.IsFalse(tool.CanExportFbx);
            tool.ExportFbx(true);
            Assert.AreEqual(0, requested);
            AddCollisionResult(tool, source.entry, source.entry.fbxMesh.name);
            Assert.IsTrue(tool.CanExportFbx);
            tool.OnRefresh();
            Assert.IsFalse(tool.CanExportFbx);
            tool.ExportFbx(true);
            Assert.AreEqual(0, requested);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void CancelledCollisionExportLeavesFbxSidecarAndPendingUvUntouched(bool existingSidecar, bool overwrite)
        {
            var source = Source("CancelledCollision_LOD0");
            var (tool, context) = CollisionTool(source.entry, source.instance);
            if (existingSidecar)
            {
                var data = SidecarStore.LoadOrCreate(source.path);
                data.collisionEntries.Add(new CollisionMeshEntry { meshGroupKey = "Existing", sourceMeshName = "Existing" });
                SidecarStore.Save(data);
            }
            var pending = Object.Instantiate(source.entry.originalMesh); owned.Add(pending);
            pending.uv2 = Enumerable.Repeat(Vector2.one, pending.vertexCount).ToArray();
            source.entry.transferredMesh = pending;
            AddCollisionResult(tool, source.entry, source.entry.fbxMesh.name);
            var before = SaveFileSnapshot(source.path);
            var beforeMesh = source.entry.meshFilter.sharedMesh;
            int requested = 0;
            context.Assets.ToolExportPathChooser = (path, intent, replace) =>
            {
                Assert.AreEqual(FbxExportIntent.Collision, intent); Assert.AreEqual(overwrite, replace);
                requested++; return null;
            };
            tool.ExportFbx(overwrite);
            Assert.AreEqual(1, requested); AssertFilesUnchanged(before);
            Assert.AreSame(pending, source.entry.transferredMesh);
            Assert.AreSame(beforeMesh, source.entry.meshFilter.sharedMesh);
            Assert.IsTrue(tool.CanExportFbx, "Cancel retains the generated result for retry.");
            if (!existingSidecar) Assert.IsNull(SidecarStore.Load(source.path));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedCollisionWriteDoesNotCommitSidecar(bool existingSidecar)
        {
            var source = Source("FailedCollision_LOD0", nested: true);
            var (tool, context) = CollisionTool(source.entry, source.instance);
            if (existingSidecar)
            {
                AddCollisionResult(tool, source.entry, source.entry.fbxMesh.name);
                Assert.IsTrue(SaveCollisionSidecarFixture(tool));
                Get<List<Mesh>>(tool, "generatedMeshes").Single().vertices = new[] { Vector3.zero, Vector3.right * 2, Vector3.up * 2 };
            }
            else AddCollisionResult(tool, source.entry, source.entry.fbxMesh.name);
            var before = SaveFileSnapshot(source.path);
            // An existing FBX file cannot also be the parent directory of the output.
            context.Assets.ToolExportPathChooser = (path, intent, overwrite) => path + "/invalid.fbx";
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("\\[FBX Export\\].*was not written:"));
            tool.ExportFbx(false);
            AssertFilesUnchanged(before);
            Assert.IsTrue(tool.CanExportFbx);
            if (!existingSidecar) Assert.IsNull(SidecarStore.Load(source.path));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void CollisionExportCommitsOnlyCollisionAndOnlyToWrittenFile(bool overwrite, bool previewWorkingMesh)
        {
            var source = Source("CollisionOnly_LOD0", nested: true);
            var (tool, context) = CollisionTool(source.entry, source.instance);
            var pending = Object.Instantiate(source.entry.originalMesh); owned.Add(pending);
            pending.uv2 = Enumerable.Repeat(Vector2.one, pending.vertexCount).ToArray();
            source.entry.transferredMesh = pending;
            if (previewWorkingMesh) source.entry.meshFilter.sharedMesh = pending;
            var originalUv = source.entry.originalMesh.uv2;
            var originalVertices = source.entry.originalMesh.vertices;
            string meshName = source.entry.originalMesh.name;
            var guid = AssetDatabase.AssetPathToGUID(source.path);
            AddCollisionResult(tool, source.entry, source.entry.fbxMesh.name);
            var before = SaveFileSnapshot(source.path);
            string target = overwrite ? source.path : Folder + "/CollisionVariant.fbx";
            context.Assets.ToolExportPathChooser = (path, intent, replace) =>
            { Assert.AreEqual(FbxExportIntent.Collision, intent); return target; };
            tool.ExportFbx(overwrite);
            var fresh = AssetDatabase.LoadAllAssetsAtPath(target).OfType<Mesh>().Single(mesh => mesh.name == meshName);
            CollectionAssert.AreEqual(originalVertices, fresh.vertices);
            CollectionAssert.AreEqual(originalUv, fresh.uv2, "Collision export must not save pending Reverse/Forward UV work.");
            Assert.AreSame(pending, source.entry.transferredMesh);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(source.path));
            Assert.AreEqual(1, SidecarStore.Load(target).collisionEntries.Count);
            Assert.IsTrue(AssetDatabase.LoadAllAssetsAtPath(target).OfType<Mesh>().Any(mesh => MeshNaming.IsCollision(mesh.name)));
            if (previewWorkingMesh) Assert.AreSame(pending, source.entry.meshFilter.sharedMesh);
            else if (overwrite) Assert.AreSame(fresh, source.entry.meshFilter.sharedMesh);
            if (!overwrite) { AssertFilesUnchanged(before); Assert.IsNull(SidecarStore.Load(source.path)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CollisionBatchCommitsOnlyTheSuccessfulFile(bool cancelFirst)
        {
            var first = Source("BatchFirst_LOD0", nested: true);
            var second = Source("BatchSecond_LOD0", nested: true);
            var root = new GameObject("CollisionRoot"); owned.Add(root);
            first.instance.transform.SetParent(root.transform); second.instance.transform.SetParent(root.transform);
            var lod = root.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(.1f, new[] { first.entry.renderer, second.entry.renderer }) });
            var context = new UvToolContext { LodGroup = lod };
            context.MeshEntries.Add(first.entry); context.MeshEntries.Add(second.entry);
            var tool = new CollisionMeshTool(); Set(tool, "ctx", context);
            AddCollisionResult(tool, first.entry, first.entry.fbxMesh.name);
            AddCollisionResult(tool, second.entry, second.entry.fbxMesh.name);
            string cancelled = cancelFirst ? first.path : second.path;
            string saved = cancelFirst ? second.path : first.path;
            var before = SaveFileSnapshot(cancelled);
            context.Assets.ToolExportPathChooser = (path, intent, overwrite) => path == cancelled ? null : "";
            tool.ExportFbx(true);
            AssertFilesUnchanged(before); Assert.IsNull(SidecarStore.Load(cancelled));
            Assert.AreEqual(1, SidecarStore.Load(saved).collisionEntries.Count);
            Assert.IsTrue(AssetDatabase.LoadAllAssetsAtPath(saved).OfType<Mesh>().Any(mesh => MeshNaming.IsCollision(mesh.name)));
            Assert.IsTrue(tool.CanExportFbx, "The skipped result remains available for retry.");
        }

        [Test]
        public void PrivateOverwriteRelinksUnselectedInactiveAndSkinnedInstancesOfTheSameFbx()
        {
            var source = Source("SharedBody");
            var imported = source.entry.fbxMesh;
            var extra = new GameObject("Arbitrary scene name"); owned.Add(extra); extra.SetActive(false);
            var filter = extra.AddComponent<MeshFilter>(); filter.sharedMesh = imported;
            var skin = extra.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = imported;
            var collider = extra.AddComponent<MeshCollider>(); collider.sharedMesh = imported;
            var unrelated = Source("OtherBody");
            var unrelatedMesh = unrelated.entry.meshFilter.sharedMesh;
            var working = new GameObject("Unsaved sibling"); owned.Add(working);
            var workingFilter = working.AddComponent<MeshFilter>(); workingFilter.sharedMesh = Triangle("SharedBody");
            var pending = workingFilter.sharedMesh;
            var context = new UvToolContext();
            context.MeshEntries.Add(new MeshEntry { fbxMesh = imported, originalMesh = pending, meshFilter = workingFilter, include = false });
            var packed = Object.Instantiate(imported); owned.Add(packed);
            packed.uv2 = new[] { new Vector2(.2f, .3f), new Vector2(.6f, .3f), new Vector2(.2f, .7f) };
            Assert.AreEqual(1, context.Assets.ExportToolEntries(new[] { source.entry.PreviewCopy(packed) }, FbxExportIntent.UV1, true,
                (path, intent, overwrite) => "").Count);
            var fresh = AssetDatabase.LoadAllAssetsAtPath(source.path).OfType<Mesh>().Single();
            Assert.AreSame(fresh, source.entry.meshFilter.sharedMesh);
            Assert.AreSame(fresh, filter.sharedMesh); Assert.AreSame(fresh, skin.sharedMesh); Assert.AreSame(fresh, collider.sharedMesh);
            Assert.AreSame(unrelatedMesh, unrelated.entry.meshFilter.sharedMesh);
            Assert.AreSame(pending, workingFilter.sharedMesh);
            CollectionAssert.AreEqual(packed.uv2, fresh.uv2);
        }

        [Test]
        public void RelinkUsesNamesCapturedBeforeReferencesBecomeMissingAndSupportsUndo()
        {
            var source = Source("RememberedMesh");
            var extra = new GameObject("Name differs from FBX mesh"); owned.Add(extra);
            var filter = extra.AddComponent<MeshFilter>(); filter.sharedMesh = source.entry.fbxMesh;
            var skin = extra.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = source.entry.fbxMesh;
            var collider = extra.AddComponent<MeshCollider>(); collider.sharedMesh = source.entry.fbxMesh;
            var bindings = FbxExport.CaptureSceneMeshReferences(source.path, Array.Empty<MeshEntry>());
            filter.sharedMesh = null; skin.sharedMesh = null; collider.sharedMesh = null;
            bindings.Relink(source.path);
            Assert.AreSame(source.entry.fbxMesh, filter.sharedMesh);
            Assert.AreSame(source.entry.fbxMesh, skin.sharedMesh); Assert.AreSame(source.entry.fbxMesh, collider.sharedMesh);
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.IsNull(filter.sharedMesh); Assert.IsNull(skin.sharedMesh); Assert.IsNull(collider.sharedMesh);
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
