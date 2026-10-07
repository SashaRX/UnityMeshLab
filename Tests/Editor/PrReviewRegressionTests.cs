using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrReviewRegressionTests
    {
        readonly List<Object> owned = new List<Object>();
        const string Scratch = "Assets/__MeshLabReviewTests";

        Mesh Plane(string name = "Plane")
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        GameObject Root(string name = "Review") { var root = new GameObject(name); owned.Add(root); return root; }

        [TearDown]
        public void Cleanup()
        {
            foreach (var item in owned) if (item) Object.DestroyImmediate(item);
            owned.Clear();
            if (AssetDatabase.IsValidFolder(Scratch)) AssetDatabase.DeleteAsset(Scratch);
        }

        [Test]
        public void StaticPreviewResourcesAreDestroyedOnReloadAndRecreatedOnDemand()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var root = Root(); var source = Plane();
            var mf = root.AddComponent<MeshFilter>(); mf.sharedMesh = source;
            var renderer = root.AddComponent<MeshRenderer>();
            var originalMaterial = new Material(Shader.Find("Hidden/Internal-Colored")); owned.Add(originalMaterial);
            renderer.sharedMaterial = originalMaterial;
            try {
                for (int cycle = 0; cycle < 3; ++cycle) {
                    CheckerTexturePreview.Apply(new List<(Renderer, Mesh)> { (renderer, source) });
                    var checker = CheckerTexturePreview.GetCheckerTexture();
                    var material = renderer.sharedMaterial;
                    var pngMaterial = (Material)typeof(UvPngWriter).GetMethod("GetMat", flags).Invoke(null, null);
                    typeof(PreviewSafetyGuard).GetMethod("OnBeforeAssemblyReload", flags).Invoke(null, null);
                    Assert.IsTrue(checker == null); Assert.IsTrue(material == null); Assert.IsTrue(pngMaterial == null);
                    Assert.AreSame(source, mf.sharedMesh); Assert.AreSame(originalMaterial, renderer.sharedMaterial);
                    ShellColorModelPreview.Apply(new List<(Renderer, Mesh, int[])> { (renderer, source, new[] { 0, 1 }) },
                        new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255) });
                    var shellMesh = mf.sharedMesh; var shellMaterial = renderer.sharedMaterial;
                    PreviewSafetyGuard.ReleaseResources(); PreviewSafetyGuard.ReleaseResources();
                    Assert.IsTrue(shellMesh == null); Assert.IsTrue(shellMaterial == null);
                    Assert.AreSame(source, mf.sharedMesh); Assert.AreSame(originalMaterial, renderer.sharedMaterial);
                }
            }
            finally { PreviewSafetyGuard.ReleaseResources(); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ToolPreviewTeardownDestroysItsOwnClonesEvenAfterSceneChanges(bool vertexBake, bool deleted)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var source = Plane();
            for (int cycle = 0; cycle < 4; ++cycle) {
                var root = Root(); var mf = root.AddComponent<MeshFilter>(); mf.sharedMesh = source;
                var renderer = root.AddComponent<MeshRenderer>();
                var originalMaterial = new Material(Shader.Find("Hidden/Internal-Colored")); owned.Add(originalMaterial);
                renderer.sharedMaterial = originalMaterial;
                var ctx = new UvToolContext();
                ctx.MeshEntries.Add(new MeshEntry { originalMesh = source, fbxMesh = source, meshFilter = mf, renderer = renderer, include = true });
                Action teardown;
                if (vertexBake) {
                    var tool = new VertexColorBakingTool(); tool.OnActivate(ctx, null);
                    typeof(VertexColorBakingTool).GetField("bakedFinalAO", flags).SetValue(tool,
                        new Dictionary<Mesh, float[]> { { source, new[] { 1f, 1f, 1f, 1f } } });
                    typeof(VertexColorBakingTool).GetMethod("ActivatePreview", flags).Invoke(tool, null);
                    teardown = tool.OnDeactivate;
                } else {
                    var tool = new PrefabBuilderTool(); tool.OnActivate(ctx, null);
                    var preview = (PrefabBuilderPreview)typeof(PrefabBuilderTool).GetField("preview", flags).GetValue(tool);
                    preview.ActivateVertexColorPreview(ctx);
                    teardown = tool.OnDeactivate;
                }
                var clone = mf.sharedMesh; var material = renderer.sharedMaterial;
                Assert.AreNotSame(source, clone); Assert.AreNotSame(originalMaterial, material);
                var replacement = Plane("External replacement");
                try {
                    if (deleted) Object.DestroyImmediate(root);
                    else mf.sharedMesh = replacement;
                    teardown(); teardown();
                    Assert.IsTrue(clone == null); Assert.IsTrue(material == null);
                    Assert.IsTrue(source != null); Assert.IsTrue(replacement != null); Assert.IsTrue(originalMaterial != null);
                    if (!deleted) Assert.AreSame(source, mf.sharedMesh);
                }
                finally { teardown(); }
            }
        }

        [Test]
        public void SaveOutputCreatesMissingParentsAndReusesTheirGuids()
        {
            string path = Scratch + "/MeshLab/Output";
            Assert.IsTrue(MeshAssetOperations.EnsureOutputFolder(path));
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.IsTrue(MeshAssetOperations.EnsureOutputFolder(path.Replace('/', '\\') + "\\"));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
            Assert.IsTrue(AssetDatabase.IsValidFolder(Scratch + "/MeshLab"));
        }

        [Test]
        public void SaveOutputRejectsInvalidPathsAndAFileInPlaceOfTheParent()
        {
            foreach (string path in new[] { "Packages/Output", Scratch + "/../Output", Scratch + "//Output", "Assets/./Output" })
                Assert.IsFalse(MeshAssetOperations.EnsureOutputFolder(path));
            Assert.IsFalse(AssetDatabase.IsValidFolder(Scratch));
            Assert.IsTrue(MeshAssetOperations.EnsureOutputFolder(Scratch));
            string pathToMesh = Scratch + "/Parent.asset";
            var saved = Object.Instantiate(Plane());
            AssetDatabase.CreateAsset(saved, pathToMesh);
            string guid = AssetDatabase.AssetPathToGUID(pathToMesh);
            Assert.IsFalse(MeshAssetOperations.EnsureOutputFolder(pathToMesh + "/Output"));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(pathToMesh));
            Assert.IsFalse(AssetDatabase.IsValidFolder(pathToMesh + " 1"));
        }

        [Test]
        public void PickingRebuildsAfterSameSizePositionAndIndexEdits()
        {
            var mesh = Plane();
            var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) };
            using (var inspection = new MeshInspection()) {
                Assert.IsTrue(inspection.Pick(items, new Vector3(.1f, .2f, -1), Vector3.forward, out _));
                var vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; ++i) vertices[i] += Vector3.right * 10;
                mesh.vertices = vertices;
                Assert.IsFalse(inspection.Pick(items, new Vector3(.1f, .2f, -1), Vector3.forward, out _));
                mesh.triangles = new[] { 0, 2, 3 };
                Assert.IsTrue(inspection.Pick(items, new Vector3(10.1f, .2f, -1), Vector3.forward, out _));
            }
        }

        [Test]
        public void WireDeduplicatesSharedSubmeshEdges()
        {
            var mesh = Plane();
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
            Assert.That(MeshViewport3D.EdgeIndices(mesh), Has.Count.EqualTo(10));
            using (var inspection = new MeshInspection()) {
                var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) };
                Assert.IsTrue(inspection.Pick(items, new Vector3(.1f, .8f, -2), Vector3.forward, out _),
                    "Picking must retain triangle indices from every submesh: the hit point lies only in submesh 1's triangle");
            }
        }

        [Test]
        public void UvChannelSelectionSkipsEmptyEntriesAndIncludesDisplayedExcludedEntries()
        {
            var mesh = Plane();
            mesh.SetUVs(7, new List<Vector2>(mesh.uv)); mesh.uv = null;
            var ctx = new UvToolContext { PreviewUvChannel = 0 };
            var canvas = new UvCanvasView { EntriesOverride = new List<MeshEntry> { new MeshEntry(), new MeshEntry { originalMesh = mesh, include = false } } };
            Assert.IsTrue(canvas.EnsurePreviewChannel(ctx));
            Assert.AreEqual(7, ctx.PreviewUvChannel);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator RenderingCopyRetainsCanonicalEntryAndCurrentPipelineState()
        {
            var authored = Plane(); var readable = Plane(); var repacked = Plane();
            readable.uv2 = readable.uv;
            var entry = new MeshEntry { originalMesh = authored };
            var ctx = new UvToolContext { PreviewUvChannel = 1 };
            ctx.MeshEntries.Add(entry);
            var canvas = new UvCanvasView { EntriesOverride = new List<MeshEntry> { entry }, SpotMode = true };
            try {
                canvas.DisplayMeshes[entry] = readable;
                Assert.AreSame(readable, canvas.DisplayMesh(ctx, entry));
                canvas.UpdateUvSpot(ctx, new Vector2(.1f, .2f), select: true);
                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 15;
                while (!canvas.HasSelectedShell && UnityEditor.EditorApplication.timeSinceStartup < deadline) {
                    canvas.PollPreviewJobs(); yield return null;
                }
                Assert.IsTrue(canvas.HasSelectedShell, "A pending click resolves when the readable preview snapshot is ready");
                Assert.AreSame(entry, canvas.SelectedShell.meshEntry);
                Assert.IsNotNull(canvas.SelectedShellDebug, "Spot details use the displayed readable mesh");
                entry.repackedMesh = repacked;
                canvas.DisplayMeshes.Clear();
                Assert.AreSame(repacked, canvas.DisplayMesh(ctx, entry));
            }
            finally { canvas.Cleanup(); }
        }

        [Test]
        public void LightmapVertexUsesDisplayedScaleAndOffsetOnlyInLightmapMode()
        {
            var renderer = Root().AddComponent<MeshRenderer>();
            renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = new Vector4(.25f, .5f, .4f, .1f);
            var entry = new MeshEntry { renderer = renderer };
            var ctx = new UvToolContext { PreviewUvChannel = 1 };
            var canvas = new UvCanvasView { CurrentPreviewMode = UvCanvasView.PreviewMode.Lightmap };
            Assert.AreEqual(new Vector2(.525f, .35f), canvas.DisplayUv(ctx, entry, Vector2.one * .5f));
            canvas.CurrentPreviewMode = UvCanvasView.PreviewMode.Off;
            Assert.AreEqual(Vector2.one * .5f, canvas.DisplayUv(ctx, entry, Vector2.one * .5f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AttributeLayerHonorsFillVisibility(bool hidden)
        {
            var mesh = Plane();
            var canvas = new UvCanvasView { FillHidden = hidden, ShowBorder = false, InspectionShading = MeshViewport3D.Shading.Normals };
            var pixels = new Texture2D(32, 32); owned.Add(pixels);
            RenderTexture target = null;
            var previous = RenderTexture.active;
            try {
                canvas.Init();
                target = canvas.RenderUvLayer(new UvToolContext { PreviewUvChannel = 0 }, mesh, new MeshEntry(), null, 32, false);
                Assert.IsNotNull(target);
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); pixels.Apply();
                Assert.That(pixels.GetPixel(8, 8).a, hidden ? Is.EqualTo(0).Within(.01) : Is.GreaterThan(0));
            }
            finally { RenderTexture.active = previous; if (target) { target.Release(); Object.DestroyImmediate(target); } canvas.Cleanup(); }
        }

        [Test]
        public void MixedLodChainsRequireSelectionOfOneChain()
        {
            var container = Root();
            var chair = Root("Chair_LOD0"); chair.transform.SetParent(container.transform);
            var table = Root("Table_LOD1"); table.transform.SetParent(container.transform);
            Assert.IsNull(LodGroupUtility.FindLodSiblings(container));
            Assert.IsTrue(LodGroupUtility.HasAmbiguousLodChains(container));
            var siblings = LodGroupUtility.FindLodSiblings(chair);
            Assert.That(siblings, Has.Count.EqualTo(1)); Assert.AreSame(chair, siblings[0].go);
        }

        [Test]
        public void ClearingGeneratedLodsRecordsPrefabOverride()
        {
            AssetDatabase.CreateFolder("Assets", "__MeshLabReviewTests");
            var root = Root();
            var renderer = root.AddComponent<MeshRenderer>();
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(.5f, new Renderer[] { renderer }) });
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, Scratch + "/Review.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab); owned.Add(instance);
            var generated = new GameObject("Review_LOD1"); generated.transform.SetParent(instance.transform);
            var generatedRenderer = generated.AddComponent<MeshRenderer>();
            var instanceGroup = instance.GetComponent<LODGroup>();
            instanceGroup.SetLODs(new[] { new LOD(.5f, new Renderer[] { instance.GetComponent<MeshRenderer>() }), new LOD(.25f, new Renderer[] { generatedRenderer }) });
            PrefabUtility.RecordPrefabInstancePropertyModifications(instanceGroup);
            var ctx = new UvToolContext { LodGroup = instanceGroup }; ctx.GeneratedLodObjects.Add(generated);
            LodGroupUtility.ClearGeneratedLods(ctx);
            Assert.That(instanceGroup.GetLODs(), Has.Length.EqualTo(1));
            var modifications = PrefabUtility.GetPropertyModifications(instance);
            Assert.IsFalse(Array.Exists(modifications, item => item.propertyPath == "m_LODs.Array.size" && item.value == "2"), "The removed LOD must not remain serialized as a prefab override");
        }

        [Test]
        public void ImportFailureAlwaysClearsItsBypassWithoutClearingOtherPaths()
        {
            const string path = "Assets/Failed.fbx", other = "Assets/Other.fbx";
            Uv2AssetPostprocessor.bypassPaths.Add(other);
            try {
                Assert.Throws<IOException>(() => FbxExport.ReimportWithoutSidecars(path, () => throw new IOException("injected")));
                Assert.IsFalse(Uv2AssetPostprocessor.bypassPaths.Contains(path));
                Assert.IsTrue(Uv2AssetPostprocessor.bypassPaths.Contains(other));
            }
            finally { Uv2AssetPostprocessor.bypassPaths.Remove(other); }
        }

        [Test]
        public void NarrowSnapshotsUseComputedChannelsAndAuthoredMeshName()
        {
            var source = Plane("Authored"); var result = Plane("Result");
            result.uv2 = new[] { Vector2.one, Vector2.zero, Vector2.right, Vector2.up };
            var snapshots = FbxExport.BuildChannelSnapshots(new[] { new MeshEntry { originalMesh = source, fbxMesh = source, repackedMesh = result } }, FbxExportIntent.UV1);
            CollectionAssert.AreEqual(result.uv2, snapshots["Authored"].uvs[1]);
            Assert.IsNull(snapshots["Authored"].uvs[0]); Assert.That(source.uv2, Is.Empty);
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [TestCase(false)]
        [TestCase(true)]
        public void BackupRestoresMetadataAndOnlyRevertsAnIncompleteWrite(bool written)
        {
            string directory = Path.Combine(Path.GetTempPath(), "MeshLab-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string path = Path.Combine(directory, "Source.fbx");
                File.WriteAllText(path, "partial or completed export"); File.WriteAllText(path + ".meta", "locked metadata");
                File.WriteAllText(Path.Combine(directory, "Backup.bak"), "original"); File.WriteAllText(Path.Combine(directory, "Backup.meta.bak"), "original metadata");
                Assert.IsTrue(MeshAssetOperations.RestoreHierarchyBackup(directory, "Backup", path, null, written));
                Assert.AreEqual(written ? "partial or completed export" : "original", File.ReadAllText(path));
                Assert.AreEqual("original metadata", File.ReadAllText(path + ".meta"));
            }
            finally { Directory.Delete(directory, true); }
        }

#endif

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void FailedHierarchyExportRestoresFilesReadabilityAndDoesNotRegisterReplay(bool overwrite, bool missingPrefab)
        {
            AssetDatabase.CreateFolder("Assets", "__MeshLabReviewTests");
            string path = Scratch + "/Review.fbx";
            var root = Root(); var mesh = Plane("Review");
            root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            FbxExport.Write(path, root); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false;
            FbxExport.ReimportWithoutSidecars(path, importer.SaveAndReimport);
            byte[] original = File.ReadAllBytes(path), metadata = File.ReadAllBytes(path + ".meta");
            string directory = Path.Combine(Path.GetTempPath(), "MeshLab-export-review-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                File.WriteAllBytes(Path.Combine(directory, "Backup.bak"), original);
                File.WriteAllBytes(Path.Combine(directory, "Backup.meta.bak"), metadata);
                var ctx = new UvToolContext { StandaloneMesh = true };
                var operations = ctx.Assets;
                if (missingPrefab) operations.HierarchyPrefabLoader = _ => null;
                operations.HierarchyWriter = (output, _) => {
                    if (overwrite) File.WriteAllText(output, "injected partial write");
                    throw new IOException("injected exporter failure");
                };
                var batch = new MeshAssetOperations.HierarchyExportBatch();
                var entries = new List<(MeshEntry, Mesh)> { (new MeshEntry { originalMesh = mesh, fbxMesh = mesh }, mesh) };
                ExpectError("[FBX Export] Export failed", () => Assert.IsFalse(operations.ExportHierarchyGroupPrepared(
                    path, entries, overwrite, batch, path, directory, Path.GetFullPath(path), "Backup")));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                if (overwrite) CollectionAssert.AreEqual(metadata, File.ReadAllBytes(path + ".meta"));
                Assert.IsFalse(((ModelImporter)AssetImporter.GetAtPath(path)).isReadable);
                Assert.IsEmpty(batch.OverwrittenFbxPaths); Assert.IsEmpty(batch.TransientReplayEntriesByPath);
                Assert.IsFalse(Uv2AssetPostprocessor.transientReplayPaths.Contains(path));
                Assert.IsFalse(Uv2AssetPostprocessor.bypassPaths.Contains(path));
            }
            finally { Directory.Delete(directory, true); }
        }
#endif

        static void ExpectError(string expected, Action action)
        {
            // The licence-free compile references omit LogAssert; Unity's real runner supplies it.
            var logAssert = Type.GetType("UnityEngine.TestTools.LogAssert, UnityEngine.TestRunner", true);
            var ignore = logAssert.GetProperty("ignoreFailingMessages", BindingFlags.Public | BindingFlags.Static);
            bool previous = (bool)ignore.GetValue(null);
            var errors = new List<string>();
            Application.LogCallback capture = (message, trace, type) => { if (type == LogType.Error || type == LogType.Exception) errors.Add(message); };
            Application.logMessageReceived += capture;
            try { ignore.SetValue(null, true); action(); }
            finally { Application.logMessageReceived -= capture; ignore.SetValue(null, previous); }
            Assert.That(errors, Has.Count.EqualTo(1)); StringAssert.Contains(expected, errors[0]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GpuWatchdogReportsErrorsButKeepsCancellationTerminal(bool cancel)
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("GPU readback unavailable");
            var mesh = Plane(); int errors = 0, completions = 0;
            var job = VertexAOBaker.StartGPUBake(new List<(Mesh, Matrix4x4)> { (mesh, Matrix4x4.identity) },
                new VertexAOSettings { sampleCount = 1 }, _ => completions++, _ => errors++);
            Assert.IsNotNull(job);
            try {
                if (cancel) job.Cancel();
                var watchdog = job.GetType().GetMethod("FailStalled", BindingFlags.Instance | BindingFlags.NonPublic);
                ExpectError("[Vertex AO] GPU bake stalled", () => watchdog.Invoke(job, new object[] { "injected readback" }));
                Assert.IsFalse(job.IsRunning); Assert.AreEqual(cancel ? 0 : 1, errors); Assert.AreEqual(0, completions);
            }
            finally { job.Cancel(); }
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [TestCase(false)]
        [TestCase(true)]
        public void FailedMetadataRestoreKeepsBackupsAndCompletedExports(bool written)
        {
            string directory = Path.Combine(Path.GetTempPath(), "MeshLab-restore-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string path = Path.Combine(directory, "Source.fbx");
                File.WriteAllText(path, "exported"); Directory.CreateDirectory(path + ".meta");
                File.WriteAllText(Path.Combine(directory, "Backup.bak"), "original"); File.WriteAllText(Path.Combine(directory, "Backup.meta.bak"), "metadata");
                ExpectError("Backup restore failed", () => Assert.IsFalse(MeshAssetOperations.RestoreHierarchyBackup(directory, "Backup", path, null, written)));
                Assert.AreEqual(written ? "exported" : "original", File.ReadAllText(path));
                Assert.IsTrue(File.Exists(Path.Combine(directory, "Backup.bak"))); Assert.IsTrue(File.Exists(Path.Combine(directory, "Backup.meta.bak")));
            }
            finally { Directory.Delete(directory, true); }
        }
#endif

        [UnityTest]
        public System.Collections.IEnumerator CancellationDuringNativePackSkipsOtherVariantsAndReleasesSession()
        {
            var root = Root(); var mesh = Plane(); var lods = new LOD[2];
            for (int li = 0; li < 2; ++li) {
                var child = Root("Plane_LOD" + li); child.transform.SetParent(root.transform);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                lods[li] = new LOD(.5f / (li + 1), new Renderer[] { child.AddComponent<MeshRenderer>() });
            }
            var group = root.AddComponent<LODGroup>(); group.SetLODs(lods);
            var options = HierarchicalRepack.Options.Default; options.atlasResolutionPx = 64;
            UvProgress.Begin("Review in-flight cancellation", true);
            try {
                var task = HierarchicalRepack.BuildAsync(group, options);
                Assert.IsFalse(task.IsCompleted, "Pack must have yielded before cancellation");
                UvProgress.RequestCancel();
                while (!task.IsCompleted) yield return null;
                var result = task.GetAwaiter().GetResult();
                Assert.AreEqual("Cancelled", result.error);
                Assert.IsNull(result.proxyUv2Raw); Assert.IsNull(result.proxyUv2Auto); Assert.IsNull(result.fineClassicalUv2);
                Assert.IsTrue(XatlasRepack.TryAcquireNativeSession()); XatlasRepack.ReleaseNativeSession();
            }
            finally { UvProgress.Cancel(); }
            Assert.IsFalse(UvProgress.CancelRequested, "Cancellation must not leak into the next operation");
        }

        [Test]
        public void HierarchyCancellationSkipsEveryRemainingStage()
        {
            var root = Root(); var mesh = Plane(); var lods = new LOD[2];
            for (int li = 0; li < 2; ++li) {
                var child = Root("Plane_LOD" + li); child.transform.SetParent(root.transform);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                lods[li] = new LOD(.5f / (li + 1), new Renderer[] { child.AddComponent<MeshRenderer>() });
            }
            var group = root.AddComponent<LODGroup>(); group.SetLODs(lods);
            UvProgress.Begin("Review cancellation", true);
            try {
                UvProgress.RequestCancel();
                var result = HierarchicalRepack.Build(group, HierarchicalRepack.Options.Default);
                Assert.AreEqual("Cancelled", result.error);
                Assert.IsNull(result.proxyUv2Clean); Assert.IsNull(result.proxyUv2Auto); Assert.IsNull(result.domainAtlasUv);
            }
            finally { UvProgress.Cancel(); }
        }

        [TestCase("PackDomainChartsAsync")]
        [TestCase("AutoUnwrapDeepMeshAsync")]
        public void HierarchicalNativeEntryRejectsAnOccupiedGlobalAtlas(string methodName)
        {
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            var mesh = Plane(); var root = Root();
            try {
                var method = typeof(HierarchicalRepack).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
                object[] args;
                HierarchicalRepack.Result result = null;
                if (methodName == "AutoUnwrapDeepMeshAsync") args = new object[] { mesh, root.transform, HierarchicalRepack.Options.Default, false };
                else {
                    root.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = root.AddComponent<MeshRenderer>();
                    var group = root.AddComponent<LODGroup>();
                    group.SetLODs(new[] { new LOD(.5f, new Renderer[] { renderer }) });
                    result = new HierarchicalRepack.Result {
                        groups = new[] { new HierarchicalRepack.LightingDomainGroup { groupId = 0, canonicalLod = 0, canonicalShellId = 0 } },
                        perLodShells = new[] { new[] { new HierarchicalRepack.Shell3D { faceIndices = new List<int> { 0, 1 }, totalArea = 1 } } }
                    };
                    args = new object[] { group, HierarchicalRepack.Options.Default, 1f, result, false };
                }
                var task = (System.Threading.Tasks.Task)method.Invoke(null, args);
                if (result == null) Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
                else {
                    task.GetAwaiter().GetResult();
                    StringAssert.Contains("already in progress", result.error);
                    Assert.IsNull(result.domainAtlasUv);
                }
                Assert.IsFalse(XatlasRepack.TryAcquireNativeSession(), "A rejected/no-op caller must not release another operation's lease");
            }
            finally { XatlasRepack.ReleaseNativeSession(); }
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession()); XatlasRepack.ReleaseNativeSession();
        }

        [Test]
        public void SkinnedPreviewAppliesNonUniformRendererScaleOnce()
        {
            var root = Root(); root.transform.localScale = new Vector3(2, 3, 4);
            var mesh = Plane(); mesh.bindposes = new[] { Matrix4x4.identity };
            var weights = new BoneWeight[4]; for (int i = 0; i < weights.Length; ++i) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
            mesh.boneWeights = weights;
            var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.bones = new[] { root.transform }; skin.rootBone = root.transform;
            var material = new Material(Shader.Find("Standard")); owned.Add(material); skin.sharedMaterial = material;
            var tool = new RemeshBakeTool();
            try {
                tool.SetSource(root); var items = new List<MeshViewport3D.Item>(); Assert.IsTrue(tool.Get3DContent(items));
                var captured = RemeshSource.Capture(root, false);
                Assert.That((captured.positions[1] - mesh.vertices[1]).magnitude, Is.LessThan(1e-5f), "Capture and preview must apply renderer scale once");
                Assert.That((items[0].matrix.MultiplyPoint3x4(items[0].mesh.vertices[1]) - root.transform.localToWorldMatrix.MultiplyPoint3x4(mesh.vertices[1])).magnitude, Is.LessThan(1e-5f));
            }
            finally { tool.ClearSourcePreview(); }
        }
    }
}
