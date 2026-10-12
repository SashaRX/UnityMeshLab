using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System.Linq;
using Autodesk.Fbx;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageFbxExportTests
    {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        string folder;

        [SetUp]
        public void SetUp()
        {
            string name = "FoliageFbx_" + Guid.NewGuid().ToString("N");
            folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(folder);

        string Source(bool coincident, bool binary, bool instanced = false, bool lods = false, string filename = "tree.fbx", bool embedded = false)
        {
            string path = folder + "/" + filename;
            using var manager = FbxManager.Create();
            var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
            manager.SetIOSettings(io);
            var scene = FbxScene.Create(manager, "TreeDocument");
            var data = new FbxMeshData
            {
                controlPoints = new double[] { 0,0,0, 100,0,0, 100,100,0, 0,100,0, 0,0,100 },
                polygonSizes = coincident ? new[] {3,3} : new[] {4,3},
                polygonVertices = coincident ? new[] {0,1,2,0,1,2} : new[] {0,1,2,3,0,1,4},
                polygonMaterials = new[] {0,1}
            };
            int count = data.CornerCount;
            data.normals = new double[count * 3];
            data.tangents = new double[count * 3];
            data.binormals = new double[count * 3];
            data.colors = new double[count * 4];
            for (int c = 0; c < count; c++)
            {
                int cp = data.polygonVertices[c];
                data.normals[c*3+2] = 1; data.tangents[c*3] = 1; data.binormals[c*3+1] = 1;
                data.colors[c*4] = .123456789 + cp*.01; data.colors[c*4+1] = .234567891;
                data.colors[c*4+2] = .345678912; data.colors[c*4+3] = .876543219;
            }
            double[,] uv = {{-.25,.1},{1.25,.1},{1.25,1.1},{-.25,1.1},{.2,.8}};
            for (int channel = 0; channel < 8; channel++)
            {
                var values = new double[count*2];
                for (int c = 0; c < count; c++)
                {
                    int cp = data.polygonVertices[c];
                    values[c*2] = uv[cp,0] + channel*.03123456789;
                    values[c*2+1] = uv[cp,1] + channel*.04345678912;
                }
                data.uvs.Add(values); data.uvNames.Add("map" + channel);
            }
            var group = FbxNode.Create(scene, "AuthoredRoot");
            group.LclRotation.Set(new FbxDouble3(-90, 0, 0));
            scene.GetRootNode().AddChild(group);
            var node = FbxNode.Create(scene, "Tree_LOD0");
            node.LclTranslation.Set(new FbxDouble3(1,2,3));
            node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, "Tree_LOD0", data));
            node.AddMaterial(FbxSurfacePhong.Create(scene,"M_Trunk"));
            node.AddMaterial(FbxSurfacePhong.Create(scene,"M_Leaves"));
            if (embedded)
            {
                string map = folder + "/" + Path.GetFileNameWithoutExtension(filename) + ".png";
                var texture = new Texture2D(2, 2);
                try { File.WriteAllBytes(map, texture.EncodeToPNG()); }
                finally { Object.DestroyImmediate(texture); }
                node.AddMaterial(FbxStructureEdit.NewMaterial(scene, "M_Map", 1, 1, 1, "T_Map", Path.GetFullPath(map), Path.GetFileName(map), null));
                io.SetBoolProp(Globals.EXP_FBX_EMBEDDED, true);
            }
            group.AddChild(node);
            if (instanced) AddInstance(scene, group, node);
            if (lods) AddLods(scene, group, node, data);
            var helper = FbxNode.Create(scene, "KeepHelper");
            helper.LclTranslation.Set(new FbxDouble3(4,5,6));
            group.AddChild(helper);
            using var exporter = FbxExporter.Create(manager, "Write");
            int format = manager.GetIOPluginRegistry().FindWriterIDByDescription(binary ? "FBX binary (*.fbx)" : "FBX ascii (*.fbx)");
            Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.SetFileExportVersion("FBX201400") && exporter.Export(scene));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false; importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.Import; importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.preserveHierarchy = true; importer.generateSecondaryUV = false;
            importer.SaveAndReimport();
            return path;
        }

        static void AddLods(FbxScene scene, FbxNode group, FbxNode source, FbxMeshData data)
        {
            for (int level = 1; level < 3; level++)
            {
                string name = "Tree_LOD" + level;
                var node = FbxNode.Create(scene, name);
                node.LclTranslation.Set(source.LclTranslation.Get());
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, name, data));
                for (int i = 0; i < source.GetMaterialCount(); i++) node.AddMaterial(source.GetMaterial(i));
                group.AddChild(node);
            }
        }

        [UnityTest]
        public IEnumerator SeparateFbxIncludesSelectedSubmeshNormalsInAllLodsAndPreservesSource()
        {
            string source = Source(false, true, lods: true);
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(source);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(model);
            using var pipeline = new FoliageNormalsPipeline();
            try
            {
                var renderer = root.GetComponentsInChildren<MeshRenderer>().Single(r => r.name == "Tree_LOD0");
                var settings = new FoliageNormalsSettings { filterRenderers = true, renderers = new[] { renderer },
                    selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 1 },
                    normalSource = FoliageNormalSource.Sphere, proxySegments = 16, gpuProjection = false, recalculateTangents = true };
                var task = pipeline.Run(root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                Assert.AreEqual(3, pipeline.Targets.Count); Assert.AreEqual(1, pipeline.CaptureSourceCount);
                string output = pipeline.SaveFbx(folder).Single(); CheckDocuments(source, output);
                using var before = FbxSourceDocument.Load(source); using var after = FbxSourceDocument.Load(output);
                foreach (var mesh in before.Meshes)
                {
                    var result = after.Meshes.Single(m => m.GetName() == mesh.GetName());
                    var a = new FbxLayerChannels.Topology(mesh); var b = new FbxLayerChannels.Topology(result);
                    var originalNormals = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(mesh), a);
                    var resultNormals = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(result), b);
                    Assert.IsFalse(originalNormals.SequenceEqual(resultNormals), mesh.GetName() + " must receive the selected foliage normals.");
                }
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta, File.ReadAllBytes(source + ".meta"));
                foreach (var target in pipeline.Targets) Assert.AreSame(target.source, target.filter.sharedMesh);
            }
            finally { pipeline.Dispose(); Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator PreviewOverwriteWritesSelectedShadingInAllLodsWithoutApplyToScene()
        {
            string source = Source(false, true, lods: true);
            string beforePath = folder + "/before.fbx";
            Assert.IsTrue(AssetDatabase.CopyAsset(source, beforePath));
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            string guid = AssetDatabase.AssetPathToGUID(source);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            using var pipeline = new FoliageNormalsPipeline();
            try
            {
                var renderer = root.GetComponentsInChildren<MeshRenderer>().Single(r => r.name == "Tree_LOD0");
                var settings = new FoliageNormalsSettings { filterRenderers = true, renderers = new[] { renderer },
                    selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 1 },
                    normalSource = FoliageNormalSource.Hemisphere, proxySegments = 16, gpuProjection = false, recalculateTangents = true };
                var task = pipeline.Run(root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                Assert.AreEqual(3, pipeline.Targets.Count);
                foreach (var target in pipeline.Targets) Assert.AreSame(target.source, target.filter.sharedMesh, "Preview must not require Apply to Scene.");
                Assert.IsEmpty(pipeline.OverwriteFbx(_ => false));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
                Assert.IsTrue(pipeline.CanApply, "Cancelling overwrite keeps the result available.");
                var paths = pipeline.OverwriteFbx(sources => { CollectionAssert.AreEqual(new[] { source }, sources); return true; });
                CollectionAssert.AreEqual(new[] { source }, paths);
                Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Capture));
                Assert.IsEmpty(pipeline.Targets);
                Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(source));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(source + ".meta"));
                Assert.IsFalse(bytes.SequenceEqual(File.ReadAllBytes(source)), "The source must receive the current transfer result.");
                CheckDocuments(beforePath, source);
                using var before = FbxSourceDocument.Load(beforePath); using var after = FbxSourceDocument.Load(source);
                foreach (var original in before.Meshes)
                {
                    var result = after.Meshes.Single(m => m.GetName() == original.GetName());
                    var a = new FbxLayerChannels.Topology(original); var b = new FbxLayerChannels.Topology(result);
                    Assert.IsFalse(FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(original), a)
                        .SequenceEqual(FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(result), b)), original.GetName());
                }
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
                {
                    Assert.IsTrue(EditorUtility.IsPersistent(filter.sharedMesh));
                    Assert.AreEqual(source, AssetDatabase.GetAssetPath(filter.sharedMesh));
                }
            }
            finally { pipeline.Dispose(); Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator StalePreviewCannotOverwriteOrAskForConfirmation()
        {
            string source = Source(false, true);
            var bytes = File.ReadAllBytes(source);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            using var pipeline = new FoliageNormalsPipeline();
            try
            {
                var settings = new FoliageNormalsSettings { normalSource = FoliageNormalSource.Sphere, proxySegments = 16, gpuProjection = false };
                var task = pipeline.Run(root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                root.transform.position += Vector3.right;
                bool confirmed = false;
                Assert.Throws<InvalidOperationException>(() => pipeline.OverwriteFbx(_ => { confirmed = true; return true; }));
                Assert.IsFalse(confirmed); CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
            }
            finally { pipeline.Dispose(); Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator FailedSourceImportRetainsFbxAndInvalidatesTheOldCapture()
        {
            string source = Source(false, true);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            using var pipeline = new FoliageNormalsPipeline();
            try
            {
                var settings = new FoliageNormalsSettings { normalSource = FoliageNormalSource.Sphere, proxySegments = 16, gpuProjection = false };
                var task = pipeline.Run(root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(".*overwrite did not complete: Injected source import failure.*"));
                Assert.Throws<InvalidOperationException>(() => pipeline.OverwriteFbx(_ => true,
                    (path, options) => throw new InvalidOperationException("Injected source import failure")));
                Assert.IsTrue(File.Exists(source)); Assert.IsTrue(File.Exists(source + ".meta"));
                Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Capture)); Assert.IsEmpty(pipeline.Targets);
                AssetDatabase.ImportAsset(source, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            }
            finally { pipeline.Dispose(); Object.DestroyImmediate(root); }
        }

        [Test]
        public void SelectedShadingCannotOverwriteSourceWithoutExplicitOptIn()
        {
            string source = Source(false, true); var bytes = File.ReadAllBytes(source);
            var selection = new FbxExport.ShadingSelection();
            Assert.Throws<InvalidOperationException>(() => FbxChannelWrite.Write(source, Array.Empty<MeshEntry>(),
                FbxExportIntent.Normals, null, null, shadingSelection: selection));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        }

        static void AddInstance(FbxScene scene, FbxNode group, FbxNode source)
        {
            var instance = FbxNode.Create(scene, "TreeCopy_LOD0");
            instance.LclTranslation.Set(new FbxDouble3(200, 0, 0));
            instance.SetNodeAttribute(source.GetMesh());
            for (int i = 0; i < source.GetMaterialCount(); i++) instance.AddMaterial(source.GetMaterial(i));
            group.AddChild(instance);
        }

        static FoliageTargetSnapshot ShadingTarget(MeshRenderer renderer, Transform root, int submesh = 1)
        {
            var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { submesh } };
            var target = new FoliageTargetSnapshot(renderer, root.worldToLocalMatrix, settings, CancellationToken.None);
            try
            {
                var normals = (Vector3[])target.localNormals.Clone();
                foreach (int i in Enumerable.Range(0, normals.Length).Where(i => target.selectedVertices[i]))
                    normals[i] = new Vector3(.3f, .5f, .8f).normalized;
                target.working = target.BuildResult(normals, true, CancellationToken.None, out _);
                return target;
            }
            catch { target.Dispose(); throw; }
        }

        [Test]
        public void ChannelPreflightDoesNotArmSidecarReplayOrChangeSourceBytes()
        {
            string source = Source(false, true);
            var bytes = File.ReadAllBytes(source);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            try {
                using var target = ShadingTarget(root.GetComponentInChildren<MeshRenderer>(), root.transform);
                target.working.uv2 = Enumerable.Repeat(new Vector2(.23f, .79f), target.working.vertexCount).ToArray();
                var entry = new MeshEntry { fbxMesh = target.source, originalMesh = target.original, transferredMesh = target.working, include = true };
                Assert.IsFalse(Uv2AssetPostprocessor.fbxOverwritePaths.Contains(source));
                Assert.IsTrue(FbxChannelWrite.Write(source, new[] { entry }, FbxExportIntent.UV1, null, null, validationOnly: true));
                Assert.IsFalse(Uv2AssetPostprocessor.fbxOverwritePaths.Contains(source), "A read-only preflight must not arm a later source import.");
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(false)] [TestCase(true)]
        public void MultipleSourceOverwritePreflightsLaterSelectionAndDocumentFailures(bool differentSelections)
        {
            string first = Source(false, true, filename: "first.fbx");
            string later = Source(false, true, instanced: true, filename: "later.fbx");
            var before = new[] { first, later }.Select(path => File.ReadAllBytes(path)).ToArray();
            var meta = new[] { first, later }.Select(path => File.ReadAllBytes(path + ".meta")).ToArray();
            var a = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(first));
            var b = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(later));
            var targets = new List<FoliageTargetSnapshot>();
            try {
                targets.Add(ShadingTarget(a.GetComponentInChildren<MeshRenderer>(), a.transform));
                var renderers = b.GetComponentsInChildren<MeshRenderer>();
                targets.Add(ShadingTarget(renderers[0], b.transform));
                if (differentSelections) targets.Add(ShadingTarget(renderers[1], b.transform, 0));
                int imports = 0;
                Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Overwrite(targets, _ => true,
                    (path, options) => { imports++; AssetDatabase.ImportAsset(path, options); }));
                Assert.AreEqual(0, imports, "No source import may precede validation of the last source.");
                for (int i = 0; i < 2; ++i) {
                    string path = i == 0 ? first : later;
                    CollectionAssert.AreEqual(before[i], File.ReadAllBytes(path), path);
                    CollectionAssert.AreEqual(meta[i], File.ReadAllBytes(path + ".meta"), path);
                }
                foreach (var target in targets) Assert.IsTrue(target.working, "The preview remains available after preflight refusal.");
            }
            finally {
                foreach (var target in targets) target.Dispose();
                Object.DestroyImmediate(a); Object.DestroyImmediate(b);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingInstancedFbxNodeIsRefusedEvenWhenCapturedTwice(bool duplicateFirst)
        {
            string source = Source(false, true, true);
            using (var document = FbxSourceDocument.Load(source)) Assert.AreEqual(2, document.Meshes.Single().GetNodeCount());
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(source);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var second = duplicateFirst ? (GameObject)PrefabUtility.InstantiatePrefab(model) : null;
            var targets = new List<FoliageTargetSnapshot>();
            try
            {
                targets.Add(ShadingTarget(root.GetComponentsInChildren<MeshRenderer>().Single(r => r.name == "Tree_LOD0"), root.transform));
                if (second != null)
                    targets.Add(ShadingTarget(second.GetComponentsInChildren<MeshRenderer>().Single(r => r.name == "Tree_LOD0"), second.transform));
                ExpectInstanceRefusal(targets);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta, File.ReadAllBytes(source + ".meta"));
                Assert.AreEqual(1, Directory.GetFiles(folder, "*.fbx").Length);
                Assert.IsFalse(AssetDatabase.IsValidFolder("Assets/__MeshLabTemp"));
            }
            finally
            {
                foreach (var target in targets) target.Dispose();
                Object.DestroyImmediate(root); if (second != null) Object.DestroyImmediate(second);
            }
        }

        void ExpectInstanceRefusal(IReadOnlyList<FoliageTargetSnapshot> targets)
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(".*is instanced in the FBX.*Nothing was written.*"));
            Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(targets, folder));
        }

        [TestCase(1)] [TestCase(2)]
        public void FailedVariantImportCannotPublishAResultOrChangeTheSource(int failedImport)
        {
            string source = Source(false, true);
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            try
            {
                using var target = ShadingTarget(root.GetComponentInChildren<MeshRenderer>(), root.transform);
                int imports = 0;
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(".*was not written: Injected variant import failure.*"));
                Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(new[] { target }, folder, (path, options) =>
                {
                    if (++imports == failedImport) throw new InvalidOperationException("Injected variant import failure");
                    AssetDatabase.ImportAsset(path, options);
                }));
                Assert.AreEqual(failedImport, imports);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(source + ".meta"));
                Assert.AreEqual(1, Directory.GetFiles(folder, "*.fbx").Length);
                Assert.IsFalse(AssetDatabase.IsValidFolder("Assets/__MeshLabTemp"));
                Assert.IsFalse(Uv2AssetPostprocessor.fbxOverwritePaths.Any(path => path.StartsWith(folder + "/", StringComparison.Ordinal)));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(1, false)] [TestCase(2, false)] [TestCase(3, false)] [TestCase(3, true)]
        public void FailedFoliageVariantsRemoveOnlyTheirNewMedia(int failedImport, bool reuse)
        {
            var settings = MeshLabProjectSettings.Instance;
            var previousFormat = settings.fbxFormat; bool previousEmbed = settings.embedFbxTextures;
            settings.fbxFormat = MeshLabFbxFormat.Binary; settings.embedFbxTextures = false;
            var roots = new List<GameObject>(); var targets = new List<FoliageTargetSnapshot>();
            var sources = new Dictionary<string, byte[]>(); string reused = null; byte[] reusedMeta = null;
            try
            {
                foreach (string filename in new[] { "first.fbx", "second.fbx" })
                {
                    string source = Source(false, true, filename: filename, embedded: true);
                    using (var document = FbxSourceDocument.Load(source)) Assert.IsTrue(document.EmbedsMedia);
                    sources[source] = File.ReadAllBytes(source); sources[source + ".meta"] = File.ReadAllBytes(source + ".meta");
                    var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source)); roots.Add(root);
                    targets.Add(ShadingTarget(root.GetComponentInChildren<MeshRenderer>(), root.transform));
                }
                if (reuse)
                {
                    byte[] bytes = File.ReadAllBytes(folder + "/first.png");
                    string hash;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant().Substring(0, 32);
                    AssetDatabase.CreateFolder(folder, "first_FoliageNormals.fbm");
                    AssetDatabase.CreateFolder(folder + "/first_FoliageNormals.fbm", hash);
                    reused = folder + "/first_FoliageNormals.fbm/" + hash + "/first.png";
                    File.WriteAllBytes(reused, bytes); AssetDatabase.ImportAsset(reused, ImportAssetOptions.ForceSynchronousImport);
                    reusedMeta = File.ReadAllBytes(reused + ".meta");
                }
                int imports = 0;
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(".*was not written: Injected media variant failure.*"));
                Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(targets, folder, (path, options) =>
                {
                    if (++imports == failedImport) throw new InvalidOperationException("Injected media variant failure");
                    AssetDatabase.ImportAsset(path, options);
                }));
                Assert.AreEqual(failedImport, imports);
                foreach (var source in sources) CollectionAssert.AreEqual(source.Value, File.ReadAllBytes(source.Key));
                Assert.AreEqual(2, Directory.GetFiles(folder, "*.fbx").Length, "all committed failed variants are removed");
                foreach (string media in Directory.GetDirectories(folder, "*_FoliageNormals.fbm"))
                {
                    Assert.IsTrue(reuse && media.Replace('\\', '/') == Path.GetDirectoryName(Path.GetDirectoryName(reused)).Replace('\\', '/'), "orphan media folder: " + media);
                    CollectionAssert.AreEqual(new[] { Path.GetFullPath(reused) }, Directory.GetFiles(media, "*.png", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray());
                }
                if (reuse)
                {
                    Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(reused));
                    CollectionAssert.AreEqual(reusedMeta, File.ReadAllBytes(reused + ".meta"));
                }
            }
            finally
            {
                foreach (var target in targets) target.Dispose(); foreach (var root in roots) Object.DestroyImmediate(root);
                settings.fbxFormat = previousFormat; settings.embedFbxTextures = previousEmbed;
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void InstancedFbxRequiresSourceNodeIdentityAndExportsCompatibleCompleteSelection(bool connected)
        {
            string source = Source(false, true, true);
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(source);
            var root = connected ? (GameObject)PrefabUtility.InstantiatePrefab(model) : Object.Instantiate(model);
            var targets = new List<FoliageTargetSnapshot>();
            try
            {
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>()) targets.Add(ShadingTarget(renderer, root.transform));
                Assert.AreEqual(2, targets.Count);
                if (connected)
                {
                    string output = FoliageFbxExport.Save(targets, folder).Single();
                    CheckDocuments(source, output);
                    using var document = FbxSourceDocument.Load(output);
                    Assert.AreEqual(2, document.Meshes.Single().GetNodeCount());
                }
                else
                {
                    ExpectInstanceRefusal(targets);
                    Assert.AreEqual(1, Directory.GetFiles(folder, "*.fbx").Length);
                }
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta, File.ReadAllBytes(source + ".meta"));
            }
            finally
            {
                foreach (var target in targets) { target.Dispose(); }
                Object.DestroyImmediate(root);
            }
        }

        [TestCase(false, true, ModelImporterTangents.Import)]
        [TestCase(false, false, ModelImporterTangents.Import)]
        [TestCase(true, true, ModelImporterTangents.Import)]
        [TestCase(false, true, ModelImporterTangents.CalculateMikk)]
        [TestCase(false, true, ModelImporterTangents.CalculateLegacy)]
        public void SeparateFbxPreservesSourceDocumentAndExcludedNormalsAfterReimport(bool coincident, bool binary, ModelImporterTangents tangentMode)
        {
            string source = Source(coincident, binary);
            if (tangentMode != ModelImporterTangents.Import)
            {
                var settings = (ModelImporter)AssetImporter.GetAtPath(source);
                settings.importTangents = tangentMode; settings.SaveAndReimport();
            }
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            root.transform.localScale = new Vector3(.01f,.02f,-.03f);
            root.transform.localRotation = Quaternion.Euler(90,12,-4);
            var transforms = root.GetComponentsInChildren<Transform>();
            var matrices = transforms.Select(t => t.localToWorldMatrix).ToArray();
            var renderer = root.GetComponentInChildren<MeshRenderer>();
            var originalReference = renderer.GetComponent<MeshFilter>().sharedMesh;
            var materialReferences = renderer.sharedMaterials;
            FoliageTargetSnapshot target = null;
            try
            {
                var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] {1} };
                target = new FoliageTargetSnapshot(renderer, root.transform.worldToLocalMatrix, settings, CancellationToken.None);
                var normals = (Vector3[])target.localNormals.Clone();
                var desired = new Vector3(.3f,.5f,.8f).normalized;
                for (int i = 0; i < normals.Length; i++) if (target.selectedVertices[i]) normals[i] = desired;
                target.working = target.BuildResult(normals, true, CancellationToken.None, out int duplicates);
                if (tangentMode == ModelImporterTangents.CalculateLegacy)
                    Assert.AreEqual(0, duplicates, "Legacy tangents already split this material boundary during import.");
                else Assert.Greater(duplicates,0,"Fixture must exercise selected/excluded vertex splitting.");
                string output = FoliageFbxExport.Save(new[] {target}, folder).Single();
                Assert.That(output, Does.EndWith("_FoliageNormals.fbx")); Assert.AreNotEqual(source, output);
                CheckDocuments(source, output);
                var sourceTag = FbxChannelWrite.ImportTagged(source, false, out _)[target.source.name];
                var resultTag = FbxChannelWrite.ImportTagged(output, false, out _)[target.source.name];
                CollectionAssert.AreEqual(sourceTag.cornerPositions, resultTag.cornerPositions);
                foreach (int corner in sourceTag.submeshCorners[0])
                {
                    Assert.That(Vector3.Dot(sourceTag.normals[corner],resultTag.normals[corner]), Is.GreaterThan(.999999f));
                    Assert.That(Vector3.Dot((Vector3)sourceTag.tangents[corner],(Vector3)resultTag.tangents[corner]), Is.GreaterThan(.999999f));
                    Assert.AreEqual(sourceTag.tangents[corner].w, resultTag.tangents[corner].w);
                }
                foreach (int corner in sourceTag.submeshCorners[1])
                    Assert.That(Vector3.Dot(desired,resultTag.normals[corner]), Is.GreaterThan(.999999f));
                for (int ch = 0; ch < 8; ch++) CollectionAssert.AreEqual(sourceTag.uvs[ch], resultTag.uvs[ch]);
                CollectionAssert.AreEqual(sourceTag.colors, resultTag.colors);
                var variantImporter = (ModelImporter)AssetImporter.GetAtPath(output);
                Assert.AreEqual(ModelImporterNormals.Import, variantImporter.importNormals);
                Assert.AreEqual(tangentMode, variantImporter.importTangents);
                CollectionAssert.AreEqual(bytes,File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta,File.ReadAllBytes(source+".meta"));
                Assert.AreEqual(originalReference,renderer.GetComponent<MeshFilter>().sharedMesh);
                CollectionAssert.AreEqual(materialReferences,renderer.sharedMaterials);
                for (int i = 0; i < transforms.Length; i++) Assert.AreEqual(matrices[i],transforms[i].localToWorldMatrix);
                Assert.IsFalse(AssetDatabase.IsValidFolder("Assets/__MeshLabTemp"));
                Assert.IsFalse(File.Exists(output + ".tmp"));
                string second = FoliageFbxExport.Save(new[] {target},folder).Single();
                Assert.AreNotEqual(output,second); CheckDocuments(source,second);
            }
            finally { target?.Dispose(); Object.DestroyImmediate(root); }
        }

        [TestCase(false)] [TestCase(true)]
        public void TangentsNoneAllowsNormalsOnlyAndRejectsDiscardingRegeneratedFrames(bool regenerate)
        {
            string source = Source(false, true);
            var importer = (ModelImporter)AssetImporter.GetAtPath(source);
            importer.importTangents = ModelImporterTangents.None; importer.SaveAndReimport();
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            var renderer = root.GetComponentInChildren<MeshRenderer>();
            var original = renderer.GetComponent<MeshFilter>().sharedMesh;
            FoliageTargetSnapshot target = null;
            try
            {
                var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 1 } };
                target = new FoliageTargetSnapshot(renderer, root.transform.worldToLocalMatrix, settings, CancellationToken.None);
                Assert.IsFalse(target.original.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent));
                target.working = target.BuildResult(target.localNormals, regenerate, CancellationToken.None, out _);
                Assert.AreEqual(regenerate, target.working.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent));
                if (regenerate)
                {
                    var error = Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(new[] { target }, folder));
                    StringAssert.Contains("Tangents = None", error.Message);
                    Assert.AreEqual(1, Directory.GetFiles(folder, "*.fbx").Length);
                }
                else
                {
                    string output = FoliageFbxExport.Save(new[] { target }, folder).Single();
                    Assert.AreEqual(ModelImporterTangents.None, ((ModelImporter)AssetImporter.GetAtPath(output)).importTangents);
                    Assert.IsFalse(AssetDatabase.LoadAllAssetsAtPath(output).OfType<Mesh>().Single().HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent));
                    CheckDocuments(source, output);
                }
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta, File.ReadAllBytes(source + ".meta"));
                Assert.AreSame(original, renderer.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { target?.Dispose(); Object.DestroyImmediate(root); }
        }

        internal static void CheckDocuments(string source, string output)
        {
            using var before = FbxSourceDocument.Load(source);
            using var after = FbxSourceDocument.Load(output);
            Assert.AreEqual(FbxWriteSettings.FromProject().Binary,after.Binary); Assert.AreEqual(before.Major,after.Major); Assert.AreEqual(before.Minor,after.Minor);
            Assert.AreEqual(before.Meshes.Count,after.Meshes.Count);
            for (int i = 0; i < before.Meshes.Count; i++)
            {
                var a = before.Meshes[i]; var b = after.Meshes[i];
                var ta = new FbxLayerChannels.Topology(a); var tb = new FbxLayerChannels.Topology(b);
                Assert.AreEqual(a.GetName(),b.GetName());
                CollectionAssert.AreEqual(FbxStructureEdit.ControlPoints(a),FbxStructureEdit.ControlPoints(b));
                CollectionAssert.AreEqual(ta.polygonSizes,tb.polygonSizes); CollectionAssert.AreEqual(ta.cornerControlPoint,tb.cornerControlPoint);
                var materials = FbxStructureEdit.PolygonMaterials(a);
                CollectionAssert.AreEqual(materials,FbxStructureEdit.PolygonMaterials(b));
                var ua = FbxLayerChannels.UvElements(a); var ub = FbxLayerChannels.UvElements(b);
                Assert.AreEqual(ua.Count,ub.Count);
                CollectionAssert.AreEqual(before.UvSetNames(a.GetName()),after.UvSetNames(b.GetName()));
                for (int ch = 0; ch < ua.Count; ch++)
                {
                    Assert.AreEqual(ua[ch].GetMappingMode(),ub[ch].GetMappingMode());
                    Assert.AreEqual(ua[ch].GetReferenceMode(),ub[ch].GetReferenceMode());
                    CollectionAssert.AreEqual(FbxLayerChannels.ReadUv(ua[ch],ta),FbxLayerChannels.ReadUv(ub[ch],tb));
                }
                var ca = FbxLayerChannels.ColorElement(a); var cb = FbxLayerChannels.ColorElement(b);
                if (ca != null)
                {
                    Assert.AreEqual(ca.GetMappingMode(),cb.GetMappingMode()); Assert.AreEqual(ca.GetReferenceMode(),cb.GetReferenceMode());
                    CollectionAssert.AreEqual(FbxLayerChannels.ReadColor(ca,ta),FbxLayerChannels.ReadColor(cb,tb));
                }
                var na = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(a),ta);
                var nb = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(b),tb);
                for (int c = 0; c < ta.CornerCount; c++)
                {
                    if (materials[ta.cornerPolygon[c]] != 0) continue;
                    for (int axis = 0; axis < 3; axis++) Assert.AreEqual(na[c*3+axis],nb[c*3+axis],"excluded FBX normal must stay exact");
                }
                CheckExcludedVectors(FbxLayerChannels.TangentElement(a),FbxLayerChannels.TangentElement(b),ta,tb,materials);
                CheckExcludedVectors(FbxLayerChannels.BinormalElement(a),FbxLayerChannels.BinormalElement(b),ta,tb,materials);
            }
            CheckNodes(before.Scene.GetRootNode(),after.Scene.GetRootNode());
        }

        static void CheckExcludedVectors(FbxLayerElementTemplateFbxVector4 a, FbxLayerElementTemplateFbxVector4 b,
            FbxLayerChannels.Topology ta, FbxLayerChannels.Topology tb, int[] materials)
        {
            if (a == null) return;
            Assert.IsNotNull(b);
            var before = FbxLayerChannels.ReadVectors(a,ta); var after = FbxLayerChannels.ReadVectors(b,tb);
            for (int c = 0; c < ta.CornerCount; c++)
            {
                if (materials[ta.cornerPolygon[c]] != 0) continue;
                for (int axis = 0; axis < 3; axis++) Assert.AreEqual(before[c*3+axis],after[c*3+axis],"excluded FBX tangent frame must stay exact");
            }
        }

        static void CheckNodes(FbxNode a, FbxNode b)
        {
            Assert.AreEqual(a.GetName(),b.GetName()); Assert.AreEqual(a.GetChildCount(),b.GetChildCount());
            Assert.AreEqual(a.LclTranslation.Get(),b.LclTranslation.Get());
            Assert.AreEqual(a.LclRotation.Get(),b.LclRotation.Get()); Assert.AreEqual(a.LclScaling.Get(),b.LclScaling.Get());
            Assert.AreEqual(a.GetMaterialCount(),b.GetMaterialCount());
            for (int i = 0; i < a.GetMaterialCount(); i++) Assert.AreEqual(a.GetMaterial(i).GetName(),b.GetMaterial(i).GetName());
            for (int i = 0; i < a.GetChildCount(); i++) CheckNodes(a.GetChild(i),b.GetChild(i));
        }

        [Test]
        public void CalculatedSourceNormalsAreRefusedWithoutChangingSourceOrCreatingAnOutput()
        {
            string source = Source(false,true);
            var importer = (ModelImporter)AssetImporter.GetAtPath(source);
            importer.importNormals = ModelImporterNormals.Calculate; importer.SaveAndReimport();
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source+".meta");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            try
            {
                using var target = new FoliageTargetSnapshot(root.GetComponentInChildren<MeshRenderer>(),root.transform.worldToLocalMatrix,new FoliageNormalsSettings(),CancellationToken.None);
                var error = Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(new[] {target},folder));
                Assert.That(error.Message,Does.Contain("Normals = Import"));
                CollectionAssert.AreEqual(bytes,File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta,File.ReadAllBytes(source+".meta"));
                Assert.AreEqual(1,Directory.GetFiles(folder,"*.fbx").Length);
            }
            finally { Object.DestroyImmediate(root); }
        }
#else
        [Test]
        public void MissingExporterProducesAnExplicitError()
        {
            Assert.IsFalse(FoliageFbxExport.Available);
            Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(Array.Empty<FoliageTargetSnapshot>(),"Assets"));
        }
#endif
    }
}
