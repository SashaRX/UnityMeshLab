using System.Collections.Generic;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests.Editor
{
    /// <summary>Keep-hierarchy remesh: the renderer collection shared by the weld capture and the
    /// per-node scope, and the settings plumbing. Native remeshing itself is covered by the
    /// Native~ ctest battery and the DllNotFoundException-skipping stage tests.</summary>
    public sealed class RemeshHierarchyTests
    {
        [UnityTest]
        public IEnumerator CagePreviewDefersWorkAndKeepsLatestDistance()
        {
            var mesh = TriangleMesh();
            using (var preview = new RemeshPreview())
            try {
                var data = new RemeshPreview.Data { geometry = new RemeshNative.Geometry {
                    positions = mesh.vertices, indices = mesh.triangles, normals = mesh.normals },
                    cageDistance = .1f, cageSmoothing = 2f };
                var outerField = typeof(RemeshPreview).GetField("cageOuter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var innerField = typeof(RemeshPreview).GetField("cageInner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                int repaints = 0; preview.RequestRepaint = () => ++repaints;
                preview.PrepareCage(mesh, data);
                Assert.IsNull(outerField.GetValue(preview), "Drawing must not synchronously build a cage");
                data.cageDistance = .2f; preview.PrepareCage(mesh, data);
                Mesh outer = null, inner = null;
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!outer && EditorApplication.timeSinceStartup < deadline) {
                    yield return null;
                    outer = (Mesh)outerField.GetValue(preview); inner = (Mesh)innerField.GetValue(preview);
                }
                Assert.IsTrue(outer); Assert.IsTrue(inner);
                Assert.AreEqual(1, repaints);
                Assert.That(outer.vertices[0].z, Is.EqualTo(.2f).Within(1e-6f));
                Assert.That(inner.vertices[0].z, Is.EqualTo(-.2f).Within(1e-6f));
                Assert.AreEqual(6, outer.GetIndexCount(0));
                preview.Invalidate();
                Assert.IsFalse(outer); Assert.IsFalse(inner);
                preview.PrepareCage(mesh, data);
                preview.Dispose();
                for (int i = 0; i < 5; ++i) yield return null;
                Assert.IsNull(outerField.GetValue(preview));
                Assert.AreEqual(1, repaints, "Disposed cage work cannot upload after the tool closes");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SkinnedSourcePreviewKeepsSkinAttributesAndReleasesItsPoseMesh()
        {
            var root = new GameObject("skin source preview");
            var mesh = new Mesh { name = "skin source" };
            var tool = new RemeshBakeTool();
            Mesh posed = null;
            try {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                mesh.bindposes = new[] { Matrix4x4.identity };
                mesh.boneWeights = new[] {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                    new BoneWeight { boneIndex0 = 0, weight0 = 1 } };
                var skin = root.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh; skin.bones = new[] { root.transform }; skin.rootBone = root.transform;
                tool.SetSource(root);
                var entries = new List<MeshEntry>(); var items = new List<MeshViewport3D.Item>();
                Assert.IsTrue(tool.GetUvContent(entries)); Assert.IsTrue(tool.Get3DContent(items));
                Assert.AreEqual(1, entries.Count);
                posed = entries[0].originalMesh;
                Assert.AreNotSame(mesh, posed); Assert.AreSame(posed, items[0].mesh);
                using (var inspection = new MeshInspection())
                    Assert.IsTrue(MeshInspection.Supports(posed, MeshViewport3D.Shading.BoneWeights), inspection.Report(posed));
                Assert.IsNotNull(MeshViewport3D.EncodeColors(posed, MeshViewport3D.Shading.BoneIndices));
                CollectionAssert.AreEqual(mesh.uv, posed.uv);
                tool.ClearSourcePreview();
                Assert.IsFalse(posed, "owned pose mesh is destroyed");
                Assert.IsTrue(mesh, "the original skin mesh remains intact");
                Assert.AreSame(mesh, skin.sharedMesh);
            }
            finally { tool.ClearSourcePreview(); Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SourceUvPreviewIsAvailableBeforeRemeshingAndFollowsSourceRoot()
        {
            var root = new GameObject("source UV hierarchy");
            var first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var other = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var original = first.GetComponent<MeshFilter>().sharedMesh;
            var mesh = Object.Instantiate(original);
            var tool = new RemeshBakeTool();
            try {
                first.transform.SetParent(root.transform); second.transform.SetParent(root.transform);
                first.transform.localRotation = Quaternion.Euler(45, 25, 10);
                second.transform.localPosition = Vector3.right * 2;
                mesh.uv2 = mesh.uv;
                mesh.SetUVs(7, new List<Vector2>(mesh.uv));
                first.GetComponent<MeshFilter>().sharedMesh = mesh;
                tool.SetSource(root);
                var entries = new List<MeshEntry>(); var items = new List<MeshViewport3D.Item>();
                Assert.IsTrue(tool.GetUvContent(entries));
                Assert.IsTrue(tool.Get3DContent(items));
                Assert.AreEqual(2, entries.Count); Assert.AreEqual(2, items.Count);
                var canvas = new UvCanvasView { EntriesOverride = entries };
                var context = new UvToolContext();
                Assert.IsTrue(canvas.HasPreviewChannel(context, 0));
                Assert.IsTrue(canvas.HasPreviewChannel(context, 1));
                Assert.IsTrue(canvas.HasPreviewChannel(context, 7));
                for (int i = 0; i < entries.Count; ++i) Assert.AreSame(entries[i].originalMesh, items[i].mesh);
                var index = items.FindIndex(item => item.mesh == mesh);
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                Assert.AreEqual(first.transform.localToWorldMatrix, items[index].matrix);
                Assert.AreSame(mesh, first.GetComponent<MeshFilter>().sharedMesh);
                // Switching the source field must replace the preview instead of
                // showing meshes inherited from the hub or the previous root.
                tool.SetSource(other); entries.Clear(); items.Clear();
                Assert.IsTrue(tool.GetUvContent(entries)); Assert.IsTrue(tool.Get3DContent(items));
                Assert.AreEqual(1, entries.Count); Assert.AreEqual(1, items.Count);
                Assert.AreSame(other.GetComponent<MeshFilter>().sharedMesh, entries[0].originalMesh);
                tool.ClearSourcePreview();
                Assert.IsTrue(mesh, "a readable original remains owned by its source");
            }
            finally {
                tool.ClearSourcePreview();
                Object.DestroyImmediate(root); Object.DestroyImmediate(other); Object.DestroyImmediate(mesh);
            }
        }

        [UnityTest]
        public IEnumerator RemeshToolUsesSameStageMeshForUvAnd3D()
        {
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var tool = new RemeshBakeTool();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var pipeline = (RemeshPipeline)typeof(RemeshBakeTool).GetField("pipeline", flags).GetValue(tool);
            var preview = (RemeshPreview)typeof(RemeshBakeTool).GetField("previews", flags).GetValue(tool);
            try {
                var settings = new RemeshSettings { sourceShape = RemeshShape.BoundingBox,
                    simplify = false, textureResolution = 64, bakeSamples = 1, minPartSize = 0, minRodVoxels = 0 };
                var run = pipeline.Run(source, settings, RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Bake);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status);
                foreach (RemeshPreview.Stage stage in System.Enum.GetValues(typeof(RemeshPreview.Stage))) {
                    preview.Show(stage);
                    var entries = new List<MeshEntry>(); var items = new List<MeshViewport3D.Item>();
                    Assert.IsTrue(tool.GetUvContent(entries));
                    Assert.IsTrue(tool.Get3DContent(items));
                    Assert.AreEqual(1, entries.Count);
                    Assert.AreSame(items[0].mesh, entries[0].originalMesh, stage.ToString());
                    var context = new UvToolContext { PreviewUvChannel = 1 };
                    var canvas = new UvCanvasView { EntriesOverride = entries };
                    canvas.EnsurePreviewChannel(context);
                    bool hasUv = canvas.HasPreviewChannel(context, 0);
                    Assert.IsTrue(hasUv, "Every stage supplies UV0, including temporary planar UVs");
                    Assert.AreEqual(items[0].mesh.vertexCount, items[0].mesh.normals.Length);
                    Assert.AreEqual(stage == RemeshPreview.Stage.Remesh || stage == RemeshPreview.Stage.Simplified,
                        entries[0].draftUv, "Draft UV provenance follows the displayed stage mesh");
                    if (hasUv) {
                        Assert.IsTrue(canvas.HasPreviewChannel(context, context.PreviewUvChannel));
                        if (stage == RemeshPreview.Stage.Source) {
                            Assert.AreSame(source.GetComponent<MeshFilter>().sharedMesh, entries[0].originalMesh);
                            CollectionAssert.AreEqual(source.GetComponent<MeshFilter>().sharedMesh.uv2, entries[0].originalMesh.uv2);
                        }
                        else Assert.AreEqual(0, context.PreviewUvChannel);
                    }
                    if (stage == RemeshPreview.Stage.Remesh || stage == RemeshPreview.Stage.Simplified)
                        Assert.IsNull(entries[0].previewTexture, "temporary planar UVs must not display the baked atlas map");
                }
            }
            finally {
                tool.ClearSourcePreview(); preview.Dispose(); pipeline.Dispose(); Object.DestroyImmediate(source);
            }
        }

        [UnityTest]
        public IEnumerator RotatedSourceKeepsOrientationInPreviewAndSavedPrefab()
        {
            const string folder = "Assets/MeshLabOrientationRegression";
            Assert.IsFalse(AssetDatabase.IsValidFolder(folder), "test folder must be unused");
            AssetDatabase.CreateFolder("Assets", "MeshLabOrientationRegression");
            var parent = new GameObject("RotatedParent");
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.transform.SetParent(parent.transform, false);
            parent.transform.rotation = Quaternion.Euler(0, 25, 15);
            parent.transform.localScale = Vector3.one * 2;
            var expectedRotation = Quaternion.identity;
            try {
                for (int mode = 0; mode < 3; ++mode) {
                    source.name = "Orientation" + mode;
                    source.transform.localRotation = Quaternion.Euler(-90, 0, 0);
                    source.transform.localScale = Vector3.one * .01f;
                    expectedRotation = source.transform.rotation;
                    var settings = new RemeshSettings { sourceShape = RemeshShape.BoundingBox,
                        keepHierarchy = mode == 2, normalizeSize = mode != 0,
                        simplify = false, textureResolution = 64, bakeSamples = 1,
                        minPartSize = 0, minRodVoxels = 0 };
                    using (var pipeline = new RemeshPipeline())
                    using (var preview = new RemeshPreview()) {
                        var run = pipeline.Run(source, settings, RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Bake);
                        while (!run.IsCompleted) yield return null;
                        Assert.IsTrue(run.Result, pipeline.Status);
                        Assert.IsTrue(UvTopology.HasUv(pipeline.SourceMesh, 0), "captured source preview retains UV0");
                        Assert.That(Quaternion.Angle(expectedRotation, pipeline.RootRotation), Is.LessThan(.01f));
                        var data = new RemeshPreview.Data { spaceToWorld = pipeline.Primary.spaceToWorld };
                        data.meshes[0] = pipeline.SourceMesh; data.meshes[1] = pipeline.VoxelMesh;
                        data.meshes[2] = pipeline.SimplifiedMesh; data.meshes[3] = pipeline.ResultMesh;
                        foreach (RemeshPreview.Stage stage in System.Enum.GetValues(typeof(RemeshPreview.Stage))) {
                            preview.Show(stage);
                            var items = new List<MeshViewport3D.Item>();
                            Assert.IsTrue(preview.Fill3D(data, items), stage.ToString());
                            Assert.That(Quaternion.Angle(expectedRotation, items[0].matrix.rotation), Is.LessThan(.01f));
                            var vertex = items[0].mesh.vertices[0];
                            Assert.That((items[0].matrix.MultiplyPoint3x4(vertex) -
                                pipeline.Primary.spaceToWorld.MultiplyPoint3x4(vertex)).magnitude, Is.LessThan(1e-6f));
                        }
                        // Saving uses the captured transform even if the source changes later.
                        source.transform.rotation = Quaternion.identity;
                        RemeshExporter.Export(pipeline, settings, folder);
                        string path = folder + "/" + source.name + "_Remesh/" + source.name + ".prefab";
                        var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        Assert.IsNotNull(saved);
                        Assert.That(Quaternion.Angle(mode == 1 ? Quaternion.identity : expectedRotation,
                            saved.transform.localRotation), Is.LessThan(.01f));
                        Assert.That(saved.transform.localPosition, Is.EqualTo(Vector3.zero));
                        if (mode == 1) {
                            Assert.AreEqual(Vector3.one, saved.transform.localScale);
                            var expectedBounds = new Bounds();
                            var matrix = pipeline.Primary.spaceToWorld;
                            matrix.SetColumn(3, new Vector4(0, 0, 0, 1));
                            var originalVertices = pipeline.ResultMesh.vertices;
                            expectedBounds = new Bounds(matrix.MultiplyPoint3x4(originalVertices[0]), Vector3.zero);
                            foreach (var v in originalVertices) expectedBounds.Encapsulate(matrix.MultiplyPoint3x4(v));
                            var filter = saved.GetComponentInChildren<MeshFilter>();
                            var savedVertices = filter.sharedMesh.vertices;
                            var savedBounds = new Bounds(filter.transform.TransformPoint(savedVertices[0]), Vector3.zero);
                            foreach (var v in savedVertices) savedBounds.Encapsulate(filter.transform.TransformPoint(v));
                            Assert.That((savedBounds.size - expectedBounds.size).magnitude, Is.LessThan(1e-5f));
                            Assert.That((savedBounds.center - expectedBounds.center).magnitude, Is.LessThan(1e-5f));
                        }
                    }
                }
            }
            finally {
                Object.DestroyImmediate(parent);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        static Mesh TriangleMesh()
        {
            var mesh = new Mesh { name = "TestTriangle" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            return mesh;
        }

        [Test]
        public void CollectRenderers_AppliesTheCaptureFilters()
        {
            var mesh = TriangleMesh();
            var collisionMesh = TriangleMesh(); collisionMesh.name = "Body_COL";
            var root = new GameObject("Root");
            try {
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform);
                body.AddComponent<MeshFilter>().sharedMesh = mesh;
                body.AddComponent<MeshRenderer>();

                var lod1 = new GameObject("Body_LOD1");
                lod1.transform.SetParent(root.transform);
                lod1.AddComponent<MeshFilter>().sharedMesh = mesh;
                lod1.AddComponent<MeshRenderer>();

                var collision = new GameObject("Body_COL");
                collision.transform.SetParent(root.transform);
                collision.AddComponent<MeshFilter>().sharedMesh = mesh;
                collision.AddComponent<MeshRenderer>();

                var inactive = new GameObject("Dark");
                inactive.transform.SetParent(root.transform);
                inactive.AddComponent<MeshFilter>().sharedMesh = mesh;
                var hidden = inactive.AddComponent<MeshRenderer>();
                hidden.enabled = false;

                // A normally named object carrying a collision mesh asset is filtered
                // by the mesh name, exactly like the weld capture does.
                var hull = new GameObject("Hull");
                hull.transform.SetParent(root.transform);
                hull.AddComponent<MeshFilter>().sharedMesh = collisionMesh;
                hull.AddComponent<MeshRenderer>();

                var collected = RemeshSource.CollectRenderers(root, lod0Only: true);
                Assert.AreEqual(1, collected.Count, "only the enabled LOD0 non-collision renderer is a node");
                Assert.AreEqual(body.transform, collected[0].transform);

                var withLods = RemeshSource.CollectRenderers(root, lod0Only: false);
                Assert.AreEqual(2, withLods.Count, "lod0Only=false still excludes the collision nodes and the disabled renderer");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(collisionMesh); }
        }

        [Test]
        public void BakeScaleIntoMesh_MirrorsWindingAndHandednessAndRefreshesBounds()
        {
            var mesh = TriangleMesh();
            mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            try {
                RemeshExporter.BakeScaleIntoMesh(mesh, new Vector3(-2, 3, 1));
                Assert.AreEqual(new Vector3(-2, 0, 0), mesh.vertices[1]);
                Assert.AreEqual(new Vector3(0, 3, 0), mesh.vertices[2]);
                Assert.AreEqual(new Vector3(-2, 3, 0) * 0.5f, mesh.bounds.center, "bounds follow the scaled vertices");
                Assert.AreEqual(new[] { 0, 2, 1 }, mesh.triangles, "a mirroring scale flips the winding");
                var t = mesh.tangents[0];
                Assert.AreEqual(-1f, t.x, 1e-5f, "tangents take the forward scale");
                Assert.AreEqual(-1f, t.w, "and the handedness flips with the mirror");
                Assert.AreEqual(1f, Mathf.Abs(Vector3.Dot(mesh.normals[0], Vector3.forward)), 1e-5f);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void KeepHierarchy_SurvivesTheSettingsRoundTrip()
        {
            var settings = new RemeshSettings { keepHierarchy = true, voxelResolution = 64 };
            var restored = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(settings));
            Assert.IsTrue(restored.keepHierarchy, "keepHierarchy persists through the EditorPrefs JSON");
            Assert.AreEqual(64, restored.voxelResolution);
            Assert.IsFalse(new RemeshSettings().keepHierarchy, "the weld remains the default");
        }
    }
}
