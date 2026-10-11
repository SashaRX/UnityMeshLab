using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageNormalsToolTests
    {
        sealed class TestProxy : IFoliageProxyBuilder
        {
            public RemeshNative.IndexedMesh Build(RemeshNative.IndexedMesh primitives, int resolution, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return FoliageProxyBuilderTests.Spheres(Vector3.zero); }
        }

        sealed class BlockingProxy : IFoliageProxyBuilder, IDisposable
        {
            internal readonly ManualResetEventSlim entered = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim release = new ManualResetEventSlim();
            public RemeshNative.IndexedMesh Build(RemeshNative.IndexedMesh primitives, int resolution, CancellationToken token)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test worker was not released.");
                token.ThrowIfCancellationRequested(); return FoliageProxyBuilderTests.Spheres(Vector3.zero);
            }
            public void Dispose() { entered.Dispose(); release.Dispose(); }
        }

        internal static IEnumerator Await(Task task)
        {
            double end = EditorApplication.timeSinceStartup + 90;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < end) yield return null;
            Assert.IsTrue(task.IsCompleted,"Task did not finish while Unity kept ticking."); Assert.IsFalse(task.IsFaulted,task.Exception?.ToString());
        }

        [UnityTest]
        public IEnumerator DefaultScatterPreservesDenseLeafIslandsAndSourceMesh()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture();
            var (positions, indices) = FoliageSurfaceSamplerTests.DenseLeafGeometry();
            fixture.mesh.Clear(); fixture.mesh.vertices = positions; fixture.mesh.triangles = indices;
            fixture.mesh.RecalculateNormals(); fixture.mesh.RecalculateBounds();
            var original = FoliageTargetSnapshot.ContentFingerprint(fixture.mesh);
            using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            var settings = new FoliageNormalsSettings();
            var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Scatter);
            yield return Await(task);
            Assert.IsTrue(task.Result, pipeline.Status);
            Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Scatter));
            Assert.AreEqual(3, pipeline.Samples.islands);
            Assert.Greater(pipeline.Samples.retainedForIslandMinimum, 0);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, new HashSet<int>(pipeline.Samples.sampleIslands));
            Assert.AreEqual(pipeline.Samples.samples.Length, pipeline.Primitives.Length);
            Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreEqual(original, FoliageTargetSnapshot.ContentFingerprint(fixture.mesh));
        }

        [UnityTest]
        public IEnumerator InPlaceMeshEditsInvalidateCaptureAndCannotOverwriteCurrentData()
        {
            for (int channel = 0; channel < 7; channel++)
            {
                using var fixture = new FoliageNormalTransferTests.Fixture();
                fixture.mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                fixture.mesh.colors = new[] { Color.red, Color.green, Color.blue, Color.white };
                fixture.mesh.tangents = Array.ConvertAll(fixture.mesh.vertices, _ => new Vector4(1,0,0,1));
                using var pipeline = new FoliageNormalsPipeline(new TestProxy());
                var settings = FoliageSurfaceSamplerTests.Settings(); settings.recalculateTangents = false;
                var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                Assert.IsTrue(pipeline.CanApply);
                switch (channel)
                {
                    case 0: var positions = fixture.mesh.vertices; positions[0] += Vector3.right * .1f; fixture.mesh.vertices = positions; break;
                    case 1: fixture.mesh.SetTriangles(new[] { 2,1,0 }, 0); break;
                    case 2: var uv = fixture.mesh.uv; uv[0] = Vector2.one; fixture.mesh.uv = uv; break;
                    case 3: var colors = fixture.mesh.colors; colors[0] = Color.black; fixture.mesh.colors = colors; break;
                    case 4: var normals = fixture.mesh.normals; normals[0] = Vector3.up; fixture.mesh.normals = normals; break;
                    case 5: var tangents = fixture.mesh.tangents; tangents[0] = new Vector4(0,1,0,-1); fixture.mesh.tangents = tangents; break;
                    default: fixture.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 20); break;
                }
                var current = FoliageTargetSnapshot.ContentFingerprint(fixture.mesh);
                Assert.IsFalse(pipeline.CaptureCurrent(), "Edited channel " + channel);
                Assert.IsFalse(pipeline.CanApply);
                Assert.Throws<InvalidOperationException>(() => pipeline.Apply());
                Assert.Throws<InvalidOperationException>(() => pipeline.Save("Assets"));
                Assert.Throws<InvalidOperationException>(() => pipeline.SaveFbx("Assets"));
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreEqual(current, FoliageTargetSnapshot.ContentFingerprint(fixture.mesh));
                pipeline.InvalidateChanged(settings, fixture.root);
                Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Capture));
                Assert.AreEqual(0, pipeline.Targets.Count);
            }
        }

        [UnityTest]
        public IEnumerator PreviewEligibilityIsCachedButActionsAndChannelEventsCheckCurrentData()
        {
            const string preference = "MeshLab.FoliageNormals.Settings";
            string previous = EditorPrefs.HasKey(preference) ? EditorPrefs.GetString(preference) : null;
            using var fixture = new FoliageNormalTransferTests.Fixture();
            var tool = new FoliageNormalsTool();
            try
            {
                tool.settings = FoliageSurfaceSamplerTests.Settings();
                tool.settings.normalSource = FoliageNormalSource.Sphere;
                tool.settings.recalculateTangents = false;
                tool.SetRoot(fixture.root);
                tool.OnActivate(null, null);
                var task = tool.pipeline.Run(fixture.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
                Assert.IsTrue(tool.CanApplyPreview);
                fixture.mesh.colors = new[] { Color.red, Color.green, Color.blue, Color.white };
                for (int draw = 0; draw < 20; draw++) Assert.IsTrue(tool.CanApplyPreview, "Drawing must reuse eligibility until an event invalidates it.");
                Assert.Throws<InvalidOperationException>(() => tool.pipeline.Apply());
                Assert.Throws<InvalidOperationException>(() => tool.pipeline.Save("Assets"));
                Assert.Throws<InvalidOperationException>(() => tool.pipeline.SaveFbx("Assets"));
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                var current = FoliageTargetSnapshot.ContentFingerprint(fixture.mesh);
                int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length, repaintCount = 0;
                tool.RequestRepaint = () => repaintCount++;
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Build and transfer a current result first"));
                Assert.DoesNotThrow(() => tool.ApplyToScene());
                Assert.IsFalse(tool.CanApplyPreview, "An Apply error must invalidate stale UI eligibility.");
                Assert.AreEqual(1, repaintCount);
                Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length);
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreEqual(current, FoliageTargetSnapshot.ContentFingerprint(fixture.mesh));
                VertexChannels.RaiseChanged(fixture.mesh);
                Assert.IsFalse(tool.CanApplyPreview);
                Assert.IsFalse(tool.pipeline.Has(FoliageNormalsPipeline.Stage.Capture));
                var rebuilt = tool.pipeline.Run(fixture.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return Await(rebuilt); Assert.IsTrue(rebuilt.Result, tool.pipeline.Status);
                Assert.IsTrue(tool.CanApplyPreview);
                tool.pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
                Assert.IsFalse(tool.CanApplyPreview);
                var repeated = tool.pipeline.Run(fixture.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return Await(repeated); Assert.IsTrue(repeated.Result, tool.pipeline.Status);
                Assert.IsTrue(tool.CanApplyPreview);
                tool.OnDeactivate();
                Assert.IsFalse(tool.CanApplyPreview);
            }
            finally
            {
                tool.OnDeactivate();
                if (previous == null) EditorPrefs.DeleteKey(preference); else EditorPrefs.SetString(preference, previous);
            }
        }

        [UnityTest]
        public IEnumerator RemovedMeshInvalidatesCaptureWithoutHashingMissingData()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture();
            using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            var settings = FoliageSurfaceSamplerTests.Settings();
            var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return Await(task);
            Assert.IsTrue(task.Result, pipeline.Status);
            var filter = fixture.root.GetComponent<MeshFilter>();
            filter.sharedMesh = null;
            Assert.IsFalse(pipeline.CaptureCurrent());
            Assert.IsFalse(pipeline.CanApply);
            pipeline.InvalidateChanged(settings, fixture.root);
            Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Capture));
            Assert.IsNull(filter.sharedMesh);
        }

        [UnityTest]
        public IEnumerator ChangedEligibleRendererSetInvalidatesCapture()
        {
            for (int change = 0; change < 3; change++)
            {
                using var fixture = new FoliageNormalTransferTests.Fixture();
                var child = new GameObject("New leaves"); child.transform.SetParent(fixture.root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = fixture.mesh;
                var renderer = child.AddComponent<MeshRenderer>(); renderer.enabled = false;
                var leaf = new Material(Shader.Find("Standard")); var trunk = new Material(Shader.Find("Standard"));
                using var pipeline = new FoliageNormalsPipeline(new TestProxy());
                try
                {
                    var settings = FoliageSurfaceSamplerTests.Settings(); settings.recalculateTangents = false;
                    if (change == 2)
                    {
                        settings.selection = FoliageFaceSelection.SelectedMaterials; settings.materials = new[] { leaf };
                        fixture.renderer.sharedMaterials = new[] { leaf, leaf };
                        renderer.sharedMaterials = new[] { trunk, trunk }; renderer.enabled = true;
                    }
                    var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer);
                    yield return Await(task); Assert.IsTrue(task.Result, pipeline.Status); Assert.IsTrue(pipeline.CanApply);
                    Assert.AreEqual(1, pipeline.Targets.Count);
                    if (change == 0) Object.DestroyImmediate(child);
                    if (change == 0)
                    {
                        child = new GameObject("Added leaves"); child.transform.SetParent(fixture.root.transform, false);
                        child.AddComponent<MeshFilter>().sharedMesh = fixture.mesh; child.AddComponent<MeshRenderer>();
                    }
                    else if (change == 1) renderer.enabled = true;
                    else renderer.sharedMaterials = new[] { leaf, leaf };
                    Assert.IsFalse(pipeline.CaptureCurrent(), "Renderer change " + change);
                    Assert.IsFalse(pipeline.CanApply); Assert.Throws<InvalidOperationException>(() => pipeline.Apply());
                    pipeline.InvalidateChanged(settings, fixture.root);
                    Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Capture));
                }
                finally { Object.DestroyImmediate(leaf); Object.DestroyImmediate(trunk); }
            }
        }

        [UnityTest]
        public IEnumerator IgnoredNewRendererDoesNotInvalidateExplicitSelection()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture();
            using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            var settings = FoliageSurfaceSamplerTests.Settings(); settings.filterRenderers = true; settings.renderers = new[] { fixture.renderer };
            var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            var child = new GameObject("Ignored"); child.transform.SetParent(fixture.root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = fixture.mesh; child.AddComponent<MeshRenderer>();
            Assert.IsTrue(pipeline.CanApply);
        }

        [UnityTest]
        public IEnumerator SharedSourceMeshesSaveToDistinctPersistentAssets()
        {
            string folder = "Assets/FoliageSave_" + Guid.NewGuid().ToString("N");
            using var fixture = new FoliageNormalTransferTests.Fixture();
            var child = new GameObject("Second leaves"); child.transform.SetParent(fixture.root.transform, false);
            child.transform.localPosition = Vector3.right * .5f;
            child.AddComponent<MeshFilter>().sharedMesh = fixture.mesh; child.AddComponent<MeshRenderer>();
            using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            try
            {
                var task = pipeline.Run(fixture.root, FoliageSurfaceSamplerTests.Settings(), FoliageNormalsPipeline.Stage.Transfer);
                yield return Await(task); Assert.IsTrue(task.Result, pipeline.Status); Assert.AreEqual(2, pipeline.Targets.Count);
                var paths = pipeline.Save(folder);
                Assert.AreEqual(2, paths.Length); Assert.AreNotEqual(paths[0], paths[1]);
                for (int i = 0; i < paths.Length; i++)
                {
                    var saved = AssetDatabase.LoadAssetAtPath<Mesh>(paths[i]); Assert.IsTrue(saved);
                    CollectionAssert.AreEqual(pipeline.Targets[i].working.normals, saved.normals);
                    CollectionAssert.AreEqual(fixture.mesh.vertices, saved.vertices);
                }
                var again = pipeline.Save(folder);
                Assert.AreNotEqual(again[0], again[1]);
                foreach (string path in again) CollectionAssert.DoesNotContain(paths, path);
                Assert.AreEqual(4, AssetDatabase.FindAssets("t:Mesh", new[] { folder }).Length);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [UnityTest]
        public IEnumerator PrefabModeApplyIsBlockedWhilePersistentSaveRemainsAvailable()
        {
            string folder = "Assets/FoliagePrefab_" + Guid.NewGuid().ToString("N");
            using var fixture = new FoliageNormalTransferTests.Fixture();
            using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            try
            {
                MeshAssetOperations.EnsureOutputFolder(folder);
                var savedSource = Object.Instantiate(fixture.mesh); AssetDatabase.CreateAsset(savedSource, folder + "/Source.asset");
                fixture.root.GetComponent<MeshFilter>().sharedMesh = savedSource;
                string prefab = folder + "/Tree.prefab"; PrefabUtility.SaveAsPrefabAsset(fixture.root, prefab);
                var stage = PrefabStageUtility.OpenPrefab(prefab); Assert.IsNotNull(stage);
                var root = stage.prefabContentsRoot; var source = root.GetComponent<MeshFilter>().sharedMesh;
                var task = pipeline.Run(root, FoliageSurfaceSamplerTests.Settings(), FoliageNormalsPipeline.Stage.Transfer);
                yield return Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                Assert.IsTrue(pipeline.CanApply); Assert.IsTrue(pipeline.InPrefabMode); Assert.IsFalse(pipeline.CanApplyToScene);
                Assert.Throws<InvalidOperationException>(() => pipeline.Apply());
                Assert.AreSame(source, root.GetComponent<MeshFilter>().sharedMesh);
                var paths = pipeline.Save(folder); Assert.IsTrue(EditorUtility.IsPersistent(AssetDatabase.LoadAssetAtPath<Mesh>(paths[0])));
            }
            finally { pipeline.Dispose(); StageUtility.GoToMainStage(); Undo.ClearAll(); AssetDatabase.DeleteAsset(folder); }
        }

        [UnityTest]
        public IEnumerator ApplyUndoRedoAndClearNeverMutateSourceOrTransforms()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture();
            using var pipeline = new FoliageNormalsPipeline(new TestProxy()); var s = FoliageSurfaceSamplerTests.Settings(); s.smoothingIterations = 1;
            var position = fixture.root.transform.localPosition; var rotation = fixture.root.transform.localRotation; var scale = fixture.root.transform.localScale; var world = fixture.root.transform.localToWorldMatrix;
            var normals = fixture.mesh.normals; var positions = fixture.mesh.vertices;
            var task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status); Assert.IsTrue(pipeline.CanApply);
            Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
            pipeline.Apply(); Undo.FlushUndoRecordObjects(); var applied = fixture.root.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreNotSame(fixture.mesh,applied); Assert.AreEqual(HideFlags.None,applied.hideFlags);
            Undo.PerformUndo(); Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
            Undo.PerformRedo(); Assert.IsTrue(fixture.root.GetComponent<MeshFilter>().sharedMesh);
            pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
            Assert.IsTrue(fixture.root.GetComponent<MeshFilter>().sharedMesh,"Scene result must outlive preview disposal and Undo/Redo");
            Assert.AreEqual(position,fixture.root.transform.localPosition); Assert.AreEqual(rotation,fixture.root.transform.localRotation); Assert.AreEqual(scale,fixture.root.transform.localScale); Assert.AreEqual(world,fixture.root.transform.localToWorldMatrix);
            CollectionAssert.AreEqual(normals,fixture.mesh.normals); CollectionAssert.AreEqual(positions,fixture.mesh.vertices);
            Undo.ClearAll(); Object.DestroyImmediate(fixture.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator LightmapPreviewCapturePreservesSourceUvsMaterialsAndSavedAppliedMeshes()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var preferences = new Dictionary<string, string>();
            foreach (string key in new[] { MeshLabWindowPreferences.Key("Hub"), MeshLabWindowPreferences.Key("Fill.foliage_normals"), "MeshLab.FoliageNormals.Settings" })
                preferences.Add(key, EditorPrefs.HasKey(key) ? EditorPrefs.GetString(key) : null);
            var lightmaps = LightmapSettings.lightmaps;
            var lightmap = new Texture2D(2, 2); var material = new Material(Shader.Find("Unlit/Color"));
            try
            {
                LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = lightmap } };
                foreach (var selection in new[] { FoliageFaceSelection.AllTriangleSubmeshes, FoliageFaceSelection.SelectedMaterials })
                {
                    using var fixture = new FoliageNormalTransferTests.Fixture(new[] { Vector3.zero, Vector3.right, Vector3.up });
                    using var pipeline = new FoliageNormalsPipeline(new TestProxy());
                    var uv0 = new[] { new Vector2(.1f, .2f), new Vector2(.7f, .3f), new Vector2(.4f, .8f) };
                    var uv1 = new[] { Vector2.zero, Vector2.right, Vector2.up };
                    fixture.mesh.uv = uv0; fixture.mesh.uv2 = uv1; fixture.renderer.sharedMaterial = material;
                    fixture.renderer.lightmapIndex = 0; fixture.renderer.lightmapScaleOffset = new Vector4(.5f, .75f, .1f, .2f);
                    string folder = "Assets/__FoliageLightmapPreview_" + Guid.NewGuid().ToString("N");
                    UvToolHub hub = null;
                    try
                    {
                        hub = ScriptableObject.CreateInstance<UvToolHub>();
                        typeof(UvToolHub).GetMethod("SelectToolById", flags).Invoke(hub, new object[] { "foliage_normals" });
                        var context = (UvToolContext)typeof(UvToolHub).GetField("ctx", flags).GetValue(hub); context.RefreshStandalone(fixture.renderer);
                        typeof(UvToolHub).GetMethod("ApplyPreviewMode", flags).Invoke(hub, new object[] { UvCanvasView.PreviewMode.Lightmap });
                        var preview = fixture.root.GetComponent<MeshFilter>().sharedMesh;
                        Assert.AreNotSame(fixture.mesh, preview); StringAssert.EndsWith("_LmPreview", preview.name);
                        CollectionAssert.AreNotEqual(uv0, preview.uv); Assert.AreNotSame(material, fixture.renderer.sharedMaterial);
                        var settings = FoliageSurfaceSamplerTests.Settings(); settings.selection = selection; settings.materials = new[] { material };
                        var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return Await(task);
                        Assert.IsTrue(task.Result, pipeline.Status); Assert.IsTrue(pipeline.CanApply); Assert.IsFalse(preview);
                        Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh); Assert.AreSame(material, fixture.renderer.sharedMaterial);
                        var canvas = (UvCanvasView)typeof(UvToolHub).GetField("canvas", flags).GetValue(hub);
                        Assert.AreEqual(UvCanvasView.PreviewMode.Off, canvas.CurrentPreviewMode);
                        CollectionAssert.AreEqual(uv0, pipeline.Targets[0].working.uv); CollectionAssert.AreEqual(uv1, pipeline.Targets[0].working.uv2);
                        var paths = pipeline.Save(folder); Assert.AreEqual(1, paths.Length);
                        var saved = AssetDatabase.LoadAssetAtPath<Mesh>(paths[0]); CollectionAssert.AreEqual(uv0, saved.uv); CollectionAssert.AreEqual(uv1, saved.uv2);
                        pipeline.Apply(); Undo.FlushUndoRecordObjects(); var applied = fixture.root.GetComponent<MeshFilter>().sharedMesh;
                        CollectionAssert.AreEqual(uv0, applied.uv); CollectionAssert.AreEqual(uv1, applied.uv2);
                        Undo.PerformUndo(); Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                        Undo.PerformRedo(); Assert.IsTrue(fixture.root.GetComponent<MeshFilter>().sharedMesh);
                        Object.DestroyImmediate(hub); hub = null;
                        CollectionAssert.AreEqual(uv0, fixture.root.GetComponent<MeshFilter>().sharedMesh.uv);
                        Assert.AreSame(material, fixture.renderer.sharedMaterial); CollectionAssert.AreEqual(uv0, fixture.mesh.uv); CollectionAssert.AreEqual(uv1, fixture.mesh.uv2);
                    }
                    finally
                    {
                        if (hub) Object.DestroyImmediate(hub);
                        Undo.ClearAll(); var applied = fixture.root.GetComponent<MeshFilter>().sharedMesh;
                        if (applied && applied != fixture.mesh) Object.DestroyImmediate(applied);
                        AssetDatabase.DeleteAsset(folder);
                    }
                }
            }
            finally
            {
                LightmapSettings.lightmaps = lightmaps; Object.DestroyImmediate(lightmap); Object.DestroyImmediate(material);
                foreach (var pair in preferences) { if (pair.Value == null) EditorPrefs.DeleteKey(pair.Key); else EditorPrefs.SetString(pair.Key, pair.Value); }
            }
        }

        [UnityTest]
        public IEnumerator HubUndoRedoAndWindowClosePreserveAppliedSceneMesh()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(new[] {Vector3.zero,Vector3.right,Vector3.up});
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var preferences = new Dictionary<string,string>();
            foreach (string key in new[] {MeshLabWindowPreferences.Key("Hub"),MeshLabWindowPreferences.Key("Fill.foliage_normals"),"MeshLab.FoliageNormals.Settings"})
                preferences.Add(key,EditorPrefs.HasKey(key) ? EditorPrefs.GetString(key) : null);
            UvToolHub hub = null;
            try {
                hub = ScriptableObject.CreateInstance<UvToolHub>();
                typeof(UvToolHub).GetMethod("SelectToolById",flags).Invoke(hub,new object[] {"foliage_normals"});
                var context = (UvToolContext)typeof(UvToolHub).GetField("ctx",flags).GetValue(hub); context.RefreshStandalone(fixture.renderer);
                var tool = hub.FindTool<FoliageNormalsTool>(); Assert.IsNotNull(tool); tool.SetRoot(fixture.root); tool.settings = FoliageSurfaceSamplerTests.Settings(); tool.settings.voxelResolution = 32;
                var task = tool.pipeline.Run(fixture.root,tool.settings,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,tool.pipeline.Status);
                tool.pipeline.Apply(); Undo.FlushUndoRecordObjects(); var normals = fixture.root.GetComponent<MeshFilter>().sharedMesh.normals;
                Undo.PerformUndo(); Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
                Undo.PerformRedo(); Assert.AreNotEqual(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
                CollectionAssert.AreEqual(normals,fixture.root.GetComponent<MeshFilter>().sharedMesh.normals);
                Object.DestroyImmediate(hub); hub = null;
                Assert.IsTrue(fixture.root.GetComponent<MeshFilter>().sharedMesh); Assert.AreNotEqual(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
                CollectionAssert.AreEqual(normals,fixture.root.GetComponent<MeshFilter>().sharedMesh.normals);
            }
            finally {
                if (hub) Object.DestroyImmediate(hub);
                Undo.ClearAll(); var applied = fixture.root.GetComponent<MeshFilter>().sharedMesh; if (applied && applied != fixture.mesh) Object.DestroyImmediate(applied);
                foreach (var pair in preferences) { if (pair.Value == null) EditorPrefs.DeleteKey(pair.Key); else EditorPrefs.SetString(pair.Key,pair.Value); }
            }
        }

        [UnityTest]
        public IEnumerator RebuildInvalidatesOnlyDownstreamAndDisposeWaitsForWorker()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var s = FoliageSurfaceSamplerTests.Settings();
            using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            var task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status);
            var before = pipeline.EnvelopeMesh; var sample = pipeline.Samples; var result = pipeline.Targets[0].working;
            s.transferBlend = .5f; pipeline.InvalidateChanged(s,fixture.root);
            Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Smooth)); Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Transfer)); Assert.IsFalse(result);
            s.smoothingIterations++; pipeline.InvalidateChanged(s,fixture.root);
            Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Envelope)); Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Smooth)); Assert.AreSame(before,pipeline.EnvelopeMesh);
            s.voxelResolution++; pipeline.InvalidateChanged(s,fixture.root);
            Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Scatter)); Assert.AreSame(sample,pipeline.Samples); Assert.IsFalse(before);
            task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); var overlap = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer);
            Assert.IsFalse(overlap.Result,"Only one run per pipeline"); pipeline.Dispose(); yield return Await(task);
            Assert.IsFalse(task.Result); Assert.IsFalse(pipeline.IsRunning); Assert.AreEqual(0,pipeline.Targets.Count); Assert.IsNull(pipeline.EnvelopeMesh);
        }

        [UnityTest]
        public IEnumerator DisposeDuringEnvelopeWaitsForInFlightWorkerAndReleasesAllPreviews()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var backend = new BlockingProxy();
            using var pipeline = new FoliageNormalsPipeline(backend);
            var task = pipeline.Run(fixture.root,FoliageSurfaceSamplerTests.Settings(),FoliageNormalsPipeline.Stage.Envelope);
            double end = EditorApplication.timeSinceStartup + 10;
            try {
                while (!backend.entered.IsSet && EditorApplication.timeSinceStartup < end) yield return null;
                Assert.IsTrue(backend.entered.IsSet); var original = pipeline.Targets[0].original;
                pipeline.Dispose(); Assert.IsTrue(pipeline.IsRunning); Assert.IsFalse(task.IsCompleted);
                Assert.IsTrue(original,"Dispose cannot destroy captured data while the worker runs.");
                backend.release.Set(); yield return Await(task);
                Assert.IsFalse(task.Result); Assert.IsFalse(original); Assert.IsFalse(pipeline.IsRunning);
                Assert.AreEqual(0,pipeline.Targets.Count); Assert.IsNull(pipeline.EnvelopeMesh); Assert.IsNull(pipeline.SmoothedMesh);
                Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { backend.release.Set(); }
        }

        [UnityTest]
        public IEnumerator ZeroTransfersFailAndStaleTransformPreventsApply()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            fixture.mesh.vertices = Array.ConvertAll(fixture.mesh.vertices,p => p*.3f);
            var s = FoliageSurfaceSamplerTests.Settings(); s.maxTransferDistance = .000001f;
            LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("No normals transferred"));
            var task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task);
            Assert.IsFalse(task.Result); Assert.IsFalse(pipeline.CanApply); Assert.IsNull(pipeline.Targets[0].working);
            s.maxTransferDistance = 0; task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task);
            Assert.IsTrue(task.Result,pipeline.Status); Assert.IsTrue(pipeline.CanApply);
            fixture.root.transform.localScale = new Vector3(2,1,1); Assert.IsFalse(pipeline.CanApply);
            Assert.Throws<InvalidOperationException>(() => pipeline.Apply()); Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator PipelineWorldNormalsAndAllTransformsSurviveSevenCaptureVariants()
        {
            var capturePoints = FoliagePrimitiveBuilder.Template(1).positions;
            for (int variant = 0; variant < 7; variant++) {
                using var fixture = new FoliageNormalTransferTests.Fixture(new[] {capturePoints[0],capturePoints[1],capturePoints[2]});
                var parent = new GameObject("Capture root"); var grand = new GameObject("Nested parent");
                using var pipeline = new FoliageNormalsPipeline(new TestProxy());
                try {
                    var child = fixture.root.transform; child.SetParent(parent.transform,false);
                    if (variant == 1) parent.transform.localRotation = Quaternion.Euler(90,0,0);
                    if (variant == 2) parent.transform.localRotation = Quaternion.Euler(-90,0,0);
                    if (variant == 3) parent.transform.localScale = Vector3.one*.01f;
                    if (variant == 4) child.localScale = new Vector3(2,.5f,3);
                    if (variant == 5) child.localScale = new Vector3(-2,3,.5f);
                    if (variant == 6) { parent.transform.SetParent(grand.transform,false); grand.transform.localScale = new Vector3(.01f,.02f,-.03f); grand.transform.localRotation = Quaternion.Euler(-90,22,14); child.localRotation = Quaternion.Euler(32,44,5); child.localScale = new Vector3(2,3,1); }
                    child.localPosition = new Vector3(.1f,.2f,.3f);
                    var inverse = (parent.transform.worldToLocalMatrix*child.localToWorldMatrix).inverse;
                    var sourcePoints = new[] {inverse.MultiplyPoint3x4(capturePoints[0]),inverse.MultiplyPoint3x4(capturePoints[1]),inverse.MultiplyPoint3x4(capturePoints[2])};
                    fixture.mesh.vertices = sourcePoints;
                    var transforms = new[] {child,parent.transform,grand.transform};
                    var positions = Array.ConvertAll(transforms,t => t.localPosition); var rotations = Array.ConvertAll(transforms,t => t.localRotation);
                    var scales = Array.ConvertAll(transforms,t => t.localScale); var worlds = Array.ConvertAll(transforms,t => t.localToWorldMatrix);
                    // Pin the matrix conversion against authored icosphere normals; remesh
                    // quality and the actual final proxy are verified separately below.
                    var s = FoliageSurfaceSamplerTests.Settings(); s.smoothingIterations = 0; s.remeshProxy = false;
                    var task = pipeline.Run(parent,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status);
                    var normals = pipeline.Targets[0].working.normals;
                    for (int i = 0; i < 3; i++) {
                        var actual = child.worldToLocalMatrix.transpose.MultiplyVector(normals[i]).normalized;
                        var expected = parent.transform.worldToLocalMatrix.transpose.MultiplyVector(capturePoints[i].normalized).normalized;
                        Assert.Greater(Vector3.Dot(actual,expected),.999f,"variant " + variant);
                    }
                    for (int i = 0; i < transforms.Length; i++) {
                        Assert.AreEqual(positions[i],transforms[i].localPosition); Assert.AreEqual(rotations[i],transforms[i].localRotation);
                        Assert.AreEqual(scales[i],transforms[i].localScale); Assert.AreEqual(worlds[i],transforms[i].localToWorldMatrix);
                    }
                    Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh); CollectionAssert.AreEqual(sourcePoints,fixture.mesh.vertices);
                }
                finally { pipeline.Dispose(); fixture.root.transform.SetParent(null,true); Object.DestroyImmediate(parent); Object.DestroyImmediate(grand); }
            }
        }

        [UnityTest]
        public IEnumerator FiltersExcludeCollisionDisabledAndNonTriangleSubmeshesButIncludeLodTargets()
        {
            var root = new GameObject("Filters"); var children = new List<FoliageNormalTransferTests.Fixture>();
            using var pipeline = new FoliageNormalsPipeline(); var s = FoliageSurfaceSamplerTests.Settings();
            try {
                foreach (string name in new[] {"Leaves_LOD0","Leaves_LOD1","Leaves_COL","Disabled","Lines"}) {
                    var f = new FoliageNormalTransferTests.Fixture(); f.root.name = name; f.root.transform.SetParent(root.transform,false); children.Add(f);
                    if (name == "Disabled") f.renderer.enabled = false;
                    if (name == "Lines") { f.mesh.subMeshCount = 1; f.mesh.SetIndices(new[] {0,1},MeshTopology.Lines,0); }
                }
                var task = pipeline.Run(root,s,FoliageNormalsPipeline.Stage.Capture); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status); Assert.AreEqual(2,pipeline.Targets.Count); Assert.AreEqual(1,pipeline.CaptureSourceCount);
                s.lod0Only = false; task = pipeline.Run(root,s,FoliageNormalsPipeline.Stage.Capture); yield return Await(task); Assert.IsTrue(task.Result); Assert.AreEqual(2,pipeline.Targets.Count); Assert.AreEqual(2,pipeline.CaptureSourceCount);
                root.SetActive(false);
                LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("No selected static foliage triangles"));
                task = pipeline.Run(root,s,FoliageNormalsPipeline.Stage.Capture); yield return Await(task);
                Assert.IsFalse(task.Result); Assert.AreEqual(0,pipeline.Targets.Count);
            }
            finally { pipeline.Dispose(); foreach (var f in children) f.Dispose(); Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator ExplicitEmptyRendererSelectionCannotProcessAllRenderers()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline();
            var s = FoliageSurfaceSamplerTests.Settings(); s.filterRenderers = true;
            LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("No selected static foliage triangles"));
            var task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Capture); yield return Await(task);
            Assert.IsFalse(task.Result); Assert.AreEqual(0,pipeline.Targets.Count); Assert.IsFalse(pipeline.CanApply);
            Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator SavedAssetsPreserveUnreadableSourceAndRepeatedRunsDoNotAccumulateMeshes()
        {
            string folder = "Assets/FoliageTest_" + Guid.NewGuid().ToString("N");
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline(new TestProxy());
            var s = FoliageSurfaceSamplerTests.Settings();
            try {
                Assert.IsTrue(MeshAssetOperations.EnsureOutputFolder(folder));
                string sourcePath = folder + "/Source.fbx";
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                FbxExport.Write(sourcePath,fixture.root);
#else
                Assert.Ignore("FBX exporter needed for the imported Read/Write-disabled fixture.");
#endif
                AssetDatabase.ImportAsset(sourcePath,ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(sourcePath); importer.isReadable = false; importer.importNormals = ModelImporterNormals.Import; importer.SaveAndReimport();
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath); var sourceCopy = model.GetComponentInChildren<MeshFilter>().sharedMesh;
                byte[] sourceBytes = File.ReadAllBytes(sourcePath); int sourceId = sourceCopy.GetInstanceID();
                fixture.root.GetComponent<MeshFilter>().sharedMesh = sourceCopy;
                var task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status);
                Assert.IsFalse(sourceCopy.isReadable); Assert.AreEqual(sourceId,fixture.root.GetComponent<MeshFilter>().sharedMesh.GetInstanceID());
                var paths = pipeline.Save(folder); Assert.AreEqual(1,paths.Length); var saved = AssetDatabase.LoadAssetAtPath<Mesh>(paths[0]);
                Assert.IsTrue(saved); StringAssert.EndsWith("_FoliageNormals.asset",paths[0]);
                CollectionAssert.AreEqual(pipeline.Targets[0].localPositions,saved.vertices); Assert.AreEqual(pipeline.Targets[0].original.subMeshCount,saved.subMeshCount);
                Assert.IsFalse(((ModelImporter)AssetImporter.GetAtPath(sourcePath)).isReadable);
                var old = pipeline.Targets[0].working; pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture); Assert.IsFalse(old);
                task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status);
                Assert.AreEqual(1,pipeline.Targets.Count); Assert.AreEqual(sourceId,fixture.root.GetComponent<MeshFilter>().sharedMesh.GetInstanceID());
                CollectionAssert.AreEqual(sourceBytes,File.ReadAllBytes(sourcePath));
            }
            finally { pipeline.Dispose(); AssetDatabase.DeleteAsset(folder); }
        }

        [UnityTest]
        public IEnumerator SyntheticCrownOneClickProducesClosedSmoothVolumeAndCpuGpuMetrics()
        {
            const int cards = 96; var crown = CrownGeometry(cards); var p = crown.positions; var t = crown.indices;
            using var fixture = new FoliageNormalTransferTests.Fixture(p); fixture.mesh.subMeshCount = 1; fixture.mesh.triangles = t;
            // Authored card normals face along each card plane; the transferred normals follow the outer envelope.
            fixture.mesh.normals = MeshGeometry.AveragedNormals(p,t);
            using var pipeline = new FoliageNormalsPipeline(); var s = new FoliageNormalsSettings { sampleSpacing = .1f, voxelResolution = 32, gpuProjection = false, recalculateTangents = false };
            var task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status);
            Assert.GreaterOrEqual(pipeline.TransferDiagnostics.transferred,p.Length*.99f); Assert.AreEqual(0,pipeline.TransferDiagnostics.misses);
            Assert.IsTrue(pipeline.CanApply); FoliageProxyBuilder.Validate(pipeline.Remeshed,CancellationToken.None,false);
            Assert.IsNotNull(pipeline.RemeshDiagnostics); Assert.Less(pipeline.RemeshDiagnostics.after.edgeVariation, pipeline.RemeshDiagnostics.before.edgeVariation);
            CollectionAssert.AreEqual(p,fixture.mesh.vertices); Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
            var before = pipeline.Targets[0].working.normals; var samples = (Vector3[])pipeline.Samples.samples.Clone(); var proxyPositions = (Vector3[])pipeline.Remeshed.positions.Clone();
            var proxyIndices = (int[])pipeline.Remeshed.indices.Clone();
            string cpu = pipeline.TransferDiagnostics.ToString();
            string output = Environment.GetEnvironmentVariable("MESHLAB_FOLIAGE_OUTPUT");
            if (!string.IsNullOrEmpty(output)) {
                Directory.CreateDirectory(output);
                RenderPreview(pipeline.EnvelopeMesh,Matrix4x4.identity,Path.Combine(output,"crown-voxel.png"),false);
                RenderPreview(pipeline.SmoothedMesh,Matrix4x4.identity,Path.Combine(output,"crown-smoothed.png"),false);
                RenderPreview(pipeline.SmoothedMesh,Matrix4x4.identity,Path.Combine(output,"crown-smoothed-wire.png"),false,true);
                RenderPreview(pipeline.RemeshedMesh,Matrix4x4.identity,Path.Combine(output,"crown-remeshed.png"),false);
                RenderPreview(pipeline.RemeshedMesh,Matrix4x4.identity,Path.Combine(output,"crown-remeshed-wire.png"),false,true);
                RenderPreview(fixture.mesh,Matrix4x4.identity,Path.Combine(output,"crown-before-normals.png"),true);
                RenderPreview(pipeline.Targets[0].working,Matrix4x4.identity,Path.Combine(output,"crown-after-normals.png"),true);
            }
            s.gpuProjection = GpuBvh.Supported; pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
            task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return Await(task); Assert.IsTrue(task.Result,pipeline.Status);
            CollectionAssert.AreEqual(samples,pipeline.Samples.samples); CollectionAssert.AreEqual(proxyPositions,pipeline.Remeshed.positions); CollectionAssert.AreEqual(proxyIndices,pipeline.Remeshed.indices);
            var after = pipeline.Targets[0].working.normals; for (int i = 0; i < before.Length; i++) Assert.Greater(Vector3.Dot(before[i],after[i]),.999f);
            if (!string.IsNullOrEmpty(output)) File.WriteAllText(Path.Combine(output,"metrics.txt"),$"cards={cards}, selected faces={pipeline.Samples.faces}, primitives={pipeline.Primitives.Length}, proxy triangles={pipeline.Remeshed.TriangleCount}, components={pipeline.ProxyComponents}, smooth volume change={pipeline.SmoothDiagnostics.VolumeChange:P2}\n{pipeline.RemeshDiagnostics}\n{cpu}\n{pipeline.TransferDiagnostics}\n");
        }

        internal static RemeshNative.IndexedMesh CrownGeometry(int cards)
        {
            var directions = MeshGeometry.SphereDirections(cards); var p = new Vector3[cards*4]; var t = new int[cards*6];
            for (int i = 0; i < cards; i++) {
                var n = directions[i]; var cardNormal = new Vector3(Mathf.Sin(i*2.399f),.3f,Mathf.Cos(i*2.399f)).normalized;
                var a = Vector3.Cross(cardNormal,Vector3.up).normalized * .12f;
                var b = Vector3.Cross(cardNormal,a).normalized * .18f; var center = n * .85f;
                p[i*4] = center-a-b; p[i*4+1] = center+a-b; p[i*4+2] = center+a+b; p[i*4+3] = center-a+b;
                int k = i*6,v=i*4; t[k]=v; t[k+1]=v+1; t[k+2]=v+2; t[k+3]=v; t[k+4]=v+2; t[k+5]=v+3;
            }
            return new RemeshNative.IndexedMesh { positions = p, indices = t };
        }

        // Same preview shader and normal encoding used by MeshViewport3D; offscreen rendering makes batch EditMode evidence reproducible.
        internal static void RenderPreview(Mesh source, Matrix4x4 matrix, string path, bool normals, bool wire = false)
        {
            var utility = new PreviewRenderUtility(); var material = new Material(Shader.Find("Hidden/MeshLab/RemeshPreview"));
            var mesh = Object.Instantiate(source); var previous = RenderTexture.active; var texture = new Texture2D(768,768,TextureFormat.RGBA32,false);
            Mesh ribbons = null; Material lineMaterial = null;
            try {
                if (normals) { mesh.colors32 = MeshViewport3D.EncodeColors(mesh,MeshViewport3D.Shading.Normals); material.SetFloat("_UseVertexColor",1); material.SetFloat("_Lit",0); }
                else { material.SetFloat("_UseVertexColor",0); material.SetFloat("_Lit",1); }
                material.SetColor("_Color",Color.white); var bounds = source.bounds; float distance = Math.Max(bounds.size.magnitude,1)*1.5f;
                utility.camera.transform.rotation = Quaternion.Euler(15,25,0); utility.camera.transform.position = bounds.center - utility.camera.transform.forward*distance;
                utility.camera.nearClipPlane = .01f; utility.camera.farClipPlane = 100; utility.camera.fieldOfView = 35;
                utility.camera.clearFlags = CameraClearFlags.SolidColor; utility.camera.backgroundColor = new Color(.08f,.1f,.14f);
                utility.lights[0].intensity = 1.2f; utility.lights[0].transform.rotation = Quaternion.Euler(35,-35,0);
                utility.lights[1].intensity = .5f; utility.ambientColor = Color.gray;
                if (wire) {
                    var edges = new SortedSet<(int,int)>(); var triangles = mesh.triangles;
                    for (int k = 0; k < triangles.Length; k += 3) for (int e = 0; e < 3; e++) {
                        int a = triangles[k+e], b = triangles[k+(e+1)%3]; edges.Add(a < b ? (a,b) : (b,a));
                    }
                    var pairs = new List<int>(); foreach (var edge in edges) { pairs.Add(edge.Item1); pairs.Add(edge.Item2); }
                    ribbons = PreviewLines.Build(mesh.vertices,pairs,null).Upload();
                    lineMaterial = new Material(Shader.Find("Hidden/MeshLab/PreviewLines")); lineMaterial.SetColor("_Color",new Color(.05f,.05f,.05f));
                }
                utility.BeginPreview(new Rect(0,0,768,768),GUIStyle.none);
                var lineBlock = new MaterialPropertyBlock();
                if (ribbons) lineBlock.SetVector("_ViewportSize",new Vector4(utility.camera.targetTexture.width,utility.camera.targetTexture.height,0,0));
                Texture image;
                try { for (int i = 0; i < mesh.subMeshCount; i++) utility.DrawMesh(mesh,matrix,material,i); if (ribbons) utility.DrawMesh(ribbons,matrix,lineMaterial,0,lineBlock); utility.Render(true); }
                finally { image = utility.EndPreview(); }
                var rt = RenderTexture.GetTemporary(768,768,24);
                try { Graphics.Blit(image,rt); RenderTexture.active = rt; texture.ReadPixels(new Rect(0,0,768,768),0,0); texture.Apply(); File.WriteAllBytes(path,texture.EncodeToPNG()); }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
            }
            finally { utility.Cleanup(); Object.DestroyImmediate(material); Object.DestroyImmediate(mesh); Object.DestroyImmediate(texture); if (ribbons) Object.DestroyImmediate(ribbons); if (lineMaterial) Object.DestroyImmediate(lineMaterial); }
        }
    }
}
