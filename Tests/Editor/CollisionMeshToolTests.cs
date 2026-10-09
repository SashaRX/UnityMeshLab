using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class CollisionMeshToolTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly List<Object> owned = new List<Object>();
        readonly List<string> assets = new List<string>();
        CollisionMeshTool tool;
        GameObject root;

        static void Call(object target, string name) => target.GetType().GetMethod(name, Private).Invoke(target, null);
        static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);

        [TearDown]
        public void Cleanup()
        {
            tool?.OnDeactivate();
            tool = null;
            foreach (var obj in owned) if (obj) Object.DestroyImmediate(obj);
            owned.Clear();
            foreach (var path in assets) AssetDatabase.DeleteAsset(path);
            assets.Clear();
            Undo.ClearAll();
        }

        Mesh Tetrahedron()
        {
            var mesh = new Mesh { name = "Chair_LOD0" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
            mesh.triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
            mesh.RecalculateBounds();
            owned.Add(mesh);
            return mesh;
        }

        Transform Prepare(bool convex, int hullCount = 1)
        {
            root = new GameObject("Chair") { layer = 7 };
            owned.Add(root);
            root.transform.SetPositionAndRotation(new Vector3(3, 2, 1), Quaternion.Euler(15, 40, 0));
            root.transform.localScale = new Vector3(2, 1, 3);
            var parent = new GameObject("Nested").transform;
            parent.SetParent(root.transform, false);
            parent.localPosition = new Vector3(2, 3, 4);
            parent.localScale = new Vector3(-2, 3, 1);
            parent.localRotation = Quaternion.Euler(20, 0, 30);
            var source = new GameObject("Chair_LOD0").transform;
            source.SetParent(parent, false);
            source.localPosition = Vector3.one;
            source.localRotation = Quaternion.Euler(10, 35, 0);
            source.localScale = new Vector3(1, 2, 1);
            var renderer = source.gameObject.AddComponent<MeshRenderer>();
            var group = root.AddComponent<LODGroup>();
            var ctx = new UvToolContext { LodGroup = group };
            ctx.MeshEntries.Add(new MeshEntry { renderer = renderer, originalMesh = Tetrahedron(), include = true, lodIndex = 0 });
            tool = new CollisionMeshTool();
            tool.OnActivate(ctx, null);
            var mode = typeof(CollisionMeshTool).GetField("generatedMode", Private);
            mode.SetValue(tool, Enum.ToObject(mode.FieldType, convex ? 1 : 0));
            var info = typeof(CollisionMeshTool).GetNestedType("GeneratedCollisionInfo", BindingFlags.NonPublic);
            var result = Activator.CreateInstance(info);
            info.GetField("meshName").SetValue(result, "Chair_LOD0");
            info.GetField("sourceTransform").SetValue(result, source);
            info.GetField("hullCount").SetValue(result, hullCount);
            ((IList)Field<object>(tool, "lastResults")).Add(result);
            for (int h = 0; h < hullCount; ++h)
            {
                var mesh = Object.Instantiate(ctx.MeshEntries[0].originalMesh);
                mesh.name += "_hull" + h;
                Field<List<Mesh>>(tool, "generatedMeshes").Add(mesh);
            }
            return source;
        }

        [Test]
        public void PreviewContainsEveryHullInItsRendererFrameAndReleasesItsCopies()
        {
            var source = Prepare(true, 2);
            var items = new List<MeshViewport3D.Item>();
            Assert.IsTrue(tool.Get3DContent(items));
            Assert.AreEqual(2, items.Count);
            foreach (var item in items)
            {
                Assert.AreEqual(source.localToWorldMatrix, item.matrix);
                Assert.AreEqual(item.mesh.vertexCount, item.mesh.normals.Length);
            }
            Assert.IsEmpty(Field<List<Mesh>>(tool, "generatedMeshes")[0].normals, "preview normals must not enter collision data");
            tool.OnRefresh();
            Assert.IsTrue(items[0].mesh == null);
            items.Clear();
            Assert.IsFalse(tool.Get3DContent(items));
        }

        [UnityTest]
        public IEnumerator ClearingResultsReleasesViewportWiresAndRibbons()
        {
            Prepare(true);
            var items = new List<MeshViewport3D.Item>();
            Assert.IsTrue(tool.Get3DContent(items));
            var generated = Field<List<Mesh>>(tool, "generatedMeshes")[0];
            var preview = items[0].mesh;
            using var viewport = new MeshViewport3D();
            var ribbons = Field<PreviewLines>(viewport, "lineRibbons");
            var sources = new[] { generated, preview };
            var wires = new Mesh[sources.Length];
            var lineMeshes = new Mesh[sources.Length];
            double deadline = EditorApplication.timeSinceStartup + 10;
            for (int i = 0; i < sources.Length; ++i)
            {
                while (!wires[i] && EditorApplication.timeSinceStartup < deadline)
                { wires[i] = viewport.WireOf(sources[i]); yield return null; }
                Assert.IsTrue(wires[i]);
                while (!lineMeshes[i] && EditorApplication.timeSinceStartup < deadline)
                { lineMeshes[i] = ribbons.Get(wires[i]); yield return null; }
                Assert.IsTrue(lineMeshes[i]);
            }
            Call(tool, "DestroyGeneratedMeshes");
            for (int i = 0; i < sources.Length; ++i)
            {
                Assert.IsFalse(sources[i]);
                Assert.IsFalse(wires[i], "wire caches must be released before their collision source dies");
                Assert.IsFalse(lineMeshes[i], "uploaded wire ribbons must be released too");
            }
            Assert.AreEqual(0, Field<IDictionary>(viewport, "wireCache").Count);
            Assert.AreEqual(0, Field<IDictionary>(ribbons, "entries").Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AppliedColliderPreservesNestedMirroredGeometryLayerAndUndo(bool convex)
        {
            var source = Prepare(convex);
            var generated = Field<List<Mesh>>(tool, "generatedMeshes")[0];
            var originalVertices = generated.vertices;
            Call(tool, "ApplyToScene");
            var collider = root.GetComponentInChildren<MeshCollider>();
            Assert.IsNotNull(collider);
            assets.Add(AssetDatabase.GetAssetPath(collider.sharedMesh));
            Assert.AreEqual(convex, collider.convex);
            Assert.AreEqual(root.layer, collider.gameObject.layer);
            Assert.AreEqual(convex ? "Chair_COL_Hull0" : "Chair_COL", collider.name);
            Assert.AreEqual(collider.name, collider.sharedMesh.name);
            Call(tool, "ApplyToScene");
            Assert.AreEqual(1, root.GetComponentsInChildren<MeshCollider>().Length, "applying again must not duplicate colliders");
            var vertices = collider.sharedMesh.vertices;
            for (int i = 0; i < vertices.Length; ++i)
                Assert.Less(Vector3.Distance(source.TransformPoint(originalVertices[i]), collider.transform.TransformPoint(vertices[i])), .0001f);
            Assert.AreEqual(generated.triangles[1], collider.sharedMesh.triangles[2], "mirroring reverses winding");
            Assert.AreEqual(originalVertices, generated.vertices, "Apply must preserve source-local sidecar data");
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.IsNull(root.GetComponentInChildren<MeshCollider>());
            Undo.PerformRedo();
            collider = root.GetComponentInChildren<MeshCollider>();
            Assert.IsNotNull(collider);
            Assert.IsNotNull(collider.sharedMesh);
            string previousAsset = AssetDatabase.GetAssetPath(collider.sharedMesh);
            Call(tool, "RemoveFromScene");
            Call(tool, "ApplyToScene");
            collider = root.GetComponentInChildren<MeshCollider>();
            string reappliedAsset = AssetDatabase.GetAssetPath(collider.sharedMesh);
            assets.Add(reappliedAsset);
            Assert.AreNotEqual(previousAsset, reappliedAsset, "the previous persistent asset must not be overwritten");
            Assert.AreEqual(collider.name, collider.sharedMesh.name, "a unique asset filename must not change the canonical mesh name");
            tool.OnDeactivate();
            Assert.IsNotNull(collider.sharedMesh, "clearing preview must not destroy applied assets");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemovalDeletesContainerOnceAndKeepsColorNamedObjects(bool nested)
        {
            Prepare(true);
            var color = new GameObject("Chair_COLOR");
            color.transform.SetParent(root.transform, false);
            var container = new GameObject("Chair_COL");
            var parent = root.transform;
            if (nested)
            {
                parent = new GameObject("Structure").transform;
                parent.SetParent(root.transform, false);
                var child = new GameObject("Deep").transform;
                child.SetParent(parent, false);
                parent = child;
            }
            container.transform.SetParent(parent, false);
            var hull = new GameObject("Chair_COL_Hull0");
            hull.transform.SetParent(container.transform, false);
            Call(tool, "ApplyToScene");
            foreach (var collider in root.GetComponentsInChildren<MeshCollider>())
                assets.Add(AssetDatabase.GetAssetPath(collider.sharedMesh));
            Assert.IsNull(root.GetComponentInChildren<MeshCollider>(), "an existing collision container must prevent a duplicate batch");
            Call(tool, "RemoveFromScene");
            Assert.IsTrue(container == null);
            Assert.IsTrue(hull == null);
            Assert.IsTrue(color != null);
            Undo.PerformUndo();
            Assert.IsNotNull(parent.Find("Chair_COL/Chair_COL_Hull0"));
        }

        [Test]
        public void ConvexGenerationHonorsUnityTriangleBudgetEvenWhenCallerRequests255Vertices()
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            owned.Add(sphere);
            var mesh = Object.Instantiate(sphere.GetComponent<MeshFilter>().sharedMesh);
            owned.Add(mesh);
            var settings = CollisionMeshBuilder.ConvexDecompSettings.Default;
            settings.maxVertsPerHull = 255;
            settings.maxHulls = 1;
            settings.resolution = 10000;
            var result = CollisionMeshBuilder.BuildConvexDecomposition(mesh, settings);
            if (result.hulls != null) owned.AddRange(result.hulls);
            Assert.IsTrue(result.ok, result.error);
            Assert.IsNotEmpty(result.hulls);
            foreach (var hull in result.hulls)
            {
                Assert.LessOrEqual(hull.vertexCount, 128);
                Assert.LessOrEqual(hull.triangles.Length / 3, 255);
            }
        }

        [Test]
        public void SimplificationIgnoresSurfaceAttributesVertexSeamsAndMaterialBoundaries()
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            owned.Add(sphere);
            var source = sphere.GetComponent<MeshFilter>().sharedMesh;
            var positions = source.vertices;
            var triangles = source.triangles;
            var plain = new Mesh { name = "Plain" };
            owned.Add(plain);
            plain.vertices = positions;
            plain.triangles = triangles;

            // Same shape, but every position has two vertices with conflicting
            // surface attributes, and two materials divide the triangle stream.
            var decorated = new Mesh { name = "Decorated" };
            owned.Add(decorated);
            var splitPositions = new Vector3[positions.Length * 2];
            var normals = new Vector3[splitPositions.Length];
            var colors = new Color32[splitPositions.Length];
            var uv = new Vector2[splitPositions.Length];
            for (int v = 0; v < splitPositions.Length; ++v)
            {
                splitPositions[v] = positions[v / 2];
                normals[v] = v % 2 == 0 ? Vector3.up : Vector3.down;
                colors[v] = v % 2 == 0 ? new Color32(255, 0, 0, 255) : new Color32(0, 255, 0, 0);
                uv[v] = new Vector2(v, -v);
            }
            decorated.vertices = splitPositions;
            decorated.normals = normals;
            decorated.colors32 = colors;
            for (int ch = 0; ch < 8; ++ch) decorated.SetUVs(ch, new List<Vector2>(uv));
            var splitTriangles = new int[triangles.Length];
            for (int i = 0; i < triangles.Length; ++i) splitTriangles[i] = triangles[i] * 2 + (i / 3) % 2;
            int half = triangles.Length / 6 * 3;
            decorated.subMeshCount = 2;
            var first = new int[half];
            var second = new int[triangles.Length - half];
            Array.Copy(splitTriangles, first, first.Length);
            Array.Copy(splitTriangles, half, second, 0, second.Length);
            decorated.SetTriangles(first, 0);
            decorated.SetTriangles(second, 1);

            var baseline = CollisionMeshBuilder.BuildSimplified(plain, .25f, .02f);
            if (baseline.mesh) owned.Add(baseline.mesh);
            var result = CollisionMeshBuilder.BuildSimplified(decorated, .25f, .02f);
            if (result.mesh) owned.Add(result.mesh);
            Assert.IsTrue(baseline.ok, baseline.error);
            Assert.IsTrue(result.ok, result.error);
            Assert.Less(result.resultTriCount, result.sourceTriCount);
            CollectionAssert.AreEqual(baseline.mesh.vertices, result.mesh.vertices);
            CollectionAssert.AreEqual(baseline.mesh.triangles, result.mesh.triangles);
            Assert.AreEqual(baseline.resultError, result.resultError);
            Assert.AreEqual(1, result.mesh.subMeshCount);
            Assert.IsEmpty(result.mesh.normals);
            Assert.IsEmpty(result.mesh.colors32);
            for (int ch = 0; ch < 8; ++ch)
            {
                var outputUv = new List<Vector2>();
                result.mesh.GetUVs(ch, outputUv);
                Assert.IsEmpty(outputUv);
            }
            CollectionAssert.AreEqual(splitPositions, decorated.vertices);
            CollectionAssert.AreEqual(normals, decorated.normals);
            CollectionAssert.AreEqual(colors, decorated.colors32);
            CollectionAssert.AreEqual(uv, decorated.uv);
            CollectionAssert.AreEqual(splitTriangles, decorated.triangles);
            Assert.AreEqual(2, decorated.subMeshCount);
        }

        [Test]
        public void GenerationReadsMeshesWithReadWriteDisabled()
        {
            var mesh = Tetrahedron();
            mesh.UploadMeshData(true);
            Assert.IsFalse(mesh.isReadable);
            var settings = CollisionMeshBuilder.ConvexDecompSettings.Default;
            settings.resolution = 10000;
            var result = CollisionMeshBuilder.BuildConvexDecomposition(mesh, settings);
            owned.AddRange(result.hulls);
            Assert.IsTrue(result.ok, result.error);
            Assert.IsNotEmpty(result.hulls);
            var simplified = CollisionMeshBuilder.BuildSimplified(mesh, .5f, .1f);
            if (simplified.mesh) owned.Add(simplified.mesh);
            Assert.IsTrue(simplified.ok, simplified.error);
            Assert.IsFalse(mesh.isReadable, "generation must not alter source readability");
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [TestCase(false, 0, true)]
        [TestCase(true, 0, false)]
        [TestCase(true, 2, false)]
        [TestCase(true, 2, true)]
        public void SidecarKeepsSourceLocalGeometryAndRebuiltExportUsesCapturedFrames(bool workingCopy, int sourceLod, bool convex)
        {
            var source = Prepare(true, 2);
            var ctx = Field<UvToolContext>(tool, "ctx");
            ctx.SourceLodIndex = sourceLod;
            ctx.MeshEntries[0].lodIndex = sourceLod;
            string sourceName = "Chair_LOD" + sourceLod;
            source.name = sourceName;
            ctx.MeshEntries[0].originalMesh.name = sourceName;
            source.gameObject.AddComponent<MeshFilter>().sharedMesh = ctx.MeshEntries[0].originalMesh;
            if (!AssetDatabase.IsValidFolder("Assets/UnityMeshLab")) AssetDatabase.CreateFolder("Assets", "UnityMeshLab");
            string fbxPath = "Assets/UnityMeshLab/CollisionTest-" + Guid.NewGuid().ToString("N") + ".fbx";
            assets.Add(fbxPath);
            assets.Add(SidecarStore.PathFor(fbxPath));
            var exportRoot = Object.Instantiate(root);
            owned.Add(exportRoot);
            var baked = new List<Mesh>();
            FbxExport.NormalizeExportHierarchy(exportRoot, baked);
            owned.AddRange(baked);
            FbxExport.Write(fbxPath, exportRoot, normalizedTransforms: true);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                if (asset is Mesh imported) { ctx.MeshEntries[0].fbxMesh = imported; break; }
            Assert.IsNotNull(ctx.MeshEntries[0].fbxMesh);
            Assert.AreEqual(sourceName, ctx.MeshEntries[0].fbxMesh.name);
            if (workingCopy)
            {
                ctx.MeshEntries[0].originalMesh.name = sourceName + "_wc";
                var selectedMode = typeof(CollisionMeshTool).GetField("mode", Private);
                selectedMode.SetValue(tool, Enum.ToObject(selectedMode.FieldType, convex ? 1 : 0));
                typeof(CollisionMeshTool).GetField("convexResolution", Private).SetValue(tool, 10000);
                typeof(CollisionMeshTool).GetField("simplifyTargetRatio", Private).SetValue(tool, 1f);
                Call(tool, "ExecuteGenerate");
                Assert.IsNotEmpty(Field<List<Mesh>>(tool, "generatedMeshes"));
            }
            // The rebuilt FBX starts from the imported hierarchy, whose mesh names
            // do not carry the working-copy suffix introduced by Optimize / weld.
            source.GetComponent<MeshFilter>().sharedMesh = ctx.MeshEntries[0].fbxMesh;
            var generated = Field<List<Mesh>>(tool, "generatedMeshes");
            var originalVertices = generated[0].vertices;
            Call(tool, "SaveToSidecar");
            var saved = SidecarStore.Load(fbxPath);
            Assert.AreEqual(sourceName, saved.collisionEntries[0].meshGroupKey);
            Assert.IsNotNull(saved.collisionEntries[0].sourceFingerprint);
            var roundTrip = SidecarStore.CollisionMeshes(fbxPath);
            Assert.AreEqual(1, roundTrip.Count);
            owned.AddRange(roundTrip[0].meshes);
            Assert.AreEqual(convex, roundTrip[0].isConvex);
            Assert.AreEqual(generated.Count, roundTrip[0].meshes.Count);
            for (int h = 0; h < generated.Count; ++h)
            {
                CollectionAssert.AreEqual(generated[h].vertices, roundTrip[0].meshes[h].vertices, "the document save needs source-local geometry");
                CollectionAssert.AreEqual(generated[h].triangles, roundTrip[0].meshes[h].triangles);
            }
            var frames = FbxExport.CollisionSourceFrames(root);
            var sink = new List<Mesh>();
            Assert.AreEqual(generated.Count, FbxExport.InjectCollisionMeshes(exportRoot, roundTrip, sink, frames));
            for (int h = 0; h < generated.Count; ++h)
            {
                var mesh = roundTrip[0].meshes[h];
                var vertices = mesh.vertices;
                var localVertices = generated[h].vertices;
                for (int i = 0; i < vertices.Length; ++i)
                    Assert.Less(Vector3.Distance(source.TransformPoint(localVertices[i]) - root.transform.position, vertices[i]), .0001f);
                Assert.AreEqual(generated[h].triangles[1], mesh.triangles[2], "normalized export reverses mirrored winding");
            }
            Assert.AreEqual(originalVertices, generated[0].vertices);
            Call(tool, "SaveToSidecar");
            Assert.AreEqual(1, saved.collisionEntries.Count, "saving again replaces the same group");
        }
#endif

        [Test]
        public void InvalidGeometryFailsBeforeNativeCalls()
        {
            var mesh = Tetrahedron();
            var vertices = mesh.vertices;
            vertices[0] = new Vector3(float.NaN, 0, 0);
            mesh.SetVertices(vertices);
            var result = CollisionMeshBuilder.BuildConvexDecomposition(mesh, CollisionMeshBuilder.ConvexDecompSettings.Default);
            Assert.IsFalse(result.ok);
            StringAssert.Contains("non-finite", result.error);
            Assert.IsEmpty(result.hulls);
            Assert.IsFalse(CollisionMeshBuilder.BuildSimplified(mesh, .1f, .5f).ok);
        }
    }
}
