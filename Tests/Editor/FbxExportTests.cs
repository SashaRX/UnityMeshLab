// FbxExportTests.cs — the FBX export mechanics and the sidecar store, without an FBX:
// naming rules, UV channel carry-over, the AO target mapping, the LOD-rebuild
// hierarchy passes on a scratch hierarchy, and collision entry validation.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using Autodesk.Fbx;
using UnityEditor;
using System.IO;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public class FbxExportTests
    {
        readonly List<Object> scratch = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in scratch)
                if (o != null) Object.DestroyImmediate(o);
            scratch.Clear();
        }

        Mesh Quad(string name, int extraUvChannel = -1)
        {
            var m = new Mesh { name = name };
            m.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            if (extraUvChannel >= 0)
                m.SetUVs(extraUvChannel, new List<Vector2> { new Vector2(.1f, .2f), new Vector2(.3f, .4f), new Vector2(.5f, .6f), new Vector2(.7f, .8f) });
            scratch.Add(m);
            return m;
        }

        GameObject Node(string name, Transform parent, Mesh mesh = null, bool renderer = false)
        {
            var go = new GameObject(name);
            scratch.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            if (mesh != null) go.AddComponent<MeshFilter>().sharedMesh = mesh;
            if (renderer) go.AddComponent<MeshRenderer>();
            return go;
        }

        static List<Vector2> Uv(Mesh m, int channel) { var l = new List<Vector2>(); m.GetUVs(channel, l); return l; }

        // ── names ──

        [TestCase("Scene", true)]
        [TestCase("Geometry", true)]
        [TestCase("Combined Mesh (root: Chair)", true)]
        [TestCase("", true)]
        [TestCase(null, true)]
        [TestCase("Chair_LOD0", false)]
        public void GenericMeshNamesAreTheDccLeftovers(string name, bool generic)
            => Assert.AreEqual(generic, FbxExport.IsGenericMeshName(name));

        [TestCase("Lit", true)]
        [TestCase("No Name", true)]
        [TestCase("Default-Material", true)]
        [TestCase(null, true)]
        [TestCase("Wood", false)]
        public void PlaceholderMaterialNamesAreTheImporterDefaults(string name, bool placeholder)
            => Assert.AreEqual(placeholder, FbxExport.IsPlaceholderMaterialName(name));

        [Test]
        public void ExportNameFallsBackFromFbxToWorkingToRendererAndSkipsHiddenNames()
        {
            var renderer = Node("Node", null, renderer: true).GetComponent<MeshRenderer>();
            var result = Quad("Result");
            Assert.AreEqual("Fbx", FbxExport.ResolveExportMeshName(new MeshEntry { fbxMesh = Quad("Fbx"), originalMesh = Quad("Work") }, result));
            Assert.AreEqual("Work", FbxExport.ResolveExportMeshName(new MeshEntry { originalMesh = Quad("Work") }, result));
            Assert.AreEqual("Result", FbxExport.ResolveExportMeshName(new MeshEntry(), result));
            Assert.AreEqual("Node", FbxExport.ResolveExportMeshName(new MeshEntry { originalMesh = Quad("Hidden/Preview"), renderer = renderer }, result));
            Assert.AreEqual("Mesh", FbxExport.ResolveExportMeshName(null, null));
        }

        // ── UV channels ──

        [Test]
        public void PreserveUvChannelsCopiesOnlyMissingNonZeroChannels()
        {
            var source = Quad("S", extraUvChannel: 2);
            source.SetUVs(3, new List<Vector2> { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero });
            source.SetUVs(4, new List<Vector3> { Vector3.one, Vector3.one, Vector3.one, Vector3.one });
            var export = Quad("E");
            export.uv = new[] { Vector2.one, Vector2.one, Vector2.one, Vector2.one };

            FbxExport.PreserveUvChannels(export, source);

            Assert.AreEqual(Vector2.one, Uv(export, 0)[0], "an existing channel is never overwritten");
            Assert.AreEqual(new Vector2(.3f, .4f), Uv(export, 2)[1], "a missing channel with data is copied");
            Assert.IsFalse(export.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord3), "an all-zero channel is not worth carrying");
            Assert.AreEqual(3, export.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord4), "channels keep their width");
        }

        [Test]
        public void OverwriteAndMergeTakeExactlyWhatTheyAreAskedFor()
        {
            var source = Quad("S", extraUvChannel: 1);
            var export = Quad("E");
            export.SetUVs(1, new List<Vector2> { Vector2.one, Vector2.one, Vector2.one, Vector2.one });

            FbxExport.OverwriteUvChannel(export, source, 1);
            Assert.AreEqual(new Vector2(.5f, .6f), Uv(export, 1)[2]);

            var donor = Quad("D", extraUvChannel: 2);
            export.SetUVs(2, new List<Vector2> { Vector2.one, Vector2.one, Vector2.one, Vector2.one });
            FbxExport.MergeUvComponentFromDonor(export, donor, 2, 1);
            Assert.AreEqual(new Vector2(1f, .4f), Uv(export, 2)[1], "only the Y component comes from the donor");

            Assert.IsTrue(FbxExport.HasUvChannelData(donor, 2));
            Assert.IsFalse(FbxExport.HasUvChannelData(donor, 5));
        }

        [Test]
        public void UvDonorPrefersTheWorkingMeshesOverTheResult()
        {
            var result = Quad("R", extraUvChannel: 2);
            var entry = new MeshEntry { fbxMesh = Quad("F", extraUvChannel: 2), originalMesh = Quad("O") };
            Assert.AreSame(entry.fbxMesh, FbxExport.SelectUvDonor(entry, result, 2), "the working mesh lacks UV2, the FBX mesh has it");
            Assert.AreSame(result, FbxExport.SelectUvDonor(new MeshEntry(), result, 2));
            Assert.IsNull(FbxExport.SelectUvDonor(entry, result, 6));
        }

        [Test]
        public void AoUvTargetMapsChannelsAndRefusesUv1()
        {
            Assert.IsFalse(SidecarStore.AoUvTarget.From(null).IsSet);
            Assert.IsFalse(SidecarStore.AoUvTarget.From(AOTargetChannel.VertexColorA).IsSet, "AO in a colour channel is not a UV target");
            Assert.IsFalse(SidecarStore.AoUvTarget.From(AOTargetChannel.UV1_X).IsSet, "UV1 is the lightmap channel");
            var t = SidecarStore.AoUvTarget.From(AOTargetChannel.UV2_Y);
            Assert.AreEqual(2, t.channel);
            Assert.AreEqual(1, t.component);
        }

        // ── hierarchy passes ──

        [Test]
        public void PruneStaleChildrenKeepsCollisionContainersCollidersAndValidNames()
        {
            var root = Node("Root", null);
            Node("A", root.transform, Quad("A"));
            Node("B", root.transform, Quad("B"));
            Node("X_COL", root.transform, Quad("X_COL"));
            Node("Container", root.transform);
            var withCollider = Node("C", root.transform, Quad("C"));
            withCollider.AddComponent<MeshCollider>();
            Node("D", root.transform, Quad("Renamed_A")); // node name differs from mesh name

            int pruned = FbxExport.PruneStaleChildren(root, new HashSet<string> { "A", "Renamed_A" });

            Assert.AreEqual(1, pruned);
            Assert.IsNull(root.transform.Find("B"));
            foreach (var kept in new[] { "A", "X_COL", "Container", "C", "D" })
                Assert.IsNotNull(root.transform.Find(kept), kept);
        }

        [Test]
        public void NormalizeExportHierarchyRenamesRenumbersAndBakesTransforms()
        {
            var root = Node("Chair", null);
            root.transform.localPosition = new Vector3(1, 2, 3);
            Node("Chair", root.transform, Quad("Chair"));
            Node("Chair_LOD2", root.transform, Quad("Chair_LOD2"));
            var far = Node("Chair_LOD5", root.transform, Quad("Chair_LOD5"));
            far.transform.localScale = new Vector3(2, 2, 2);
            var sink = new List<Mesh>();

            var renames = FbxExport.NormalizeExportHierarchy(root, sink);
            scratch.AddRange(sink);

            Assert.AreEqual(Vector3.zero, root.transform.localPosition, "root pivot is reset");
            Assert.AreEqual("Chair_LOD0", renames["Chair"]);
            Assert.AreEqual("Chair_LOD1", renames["Chair_LOD2"]);
            Assert.AreEqual("Chair_LOD2", renames["Chair_LOD5"]);
            Assert.AreEqual(Vector3.one, far.transform.localScale, "the scaled node is reset…");
            Assert.AreEqual(1, sink.Count, "…onto a copy of its mesh");
            Assert.AreEqual(new Vector3(2, 0, 0), far.GetComponent<MeshFilter>().sharedMesh.vertices[1], "with the scale baked in");
        }

        [Test]
        public void NormalizeHierarchyPreservesWorldGeometryThroughNestedAndMirroredTransforms()
        {
            var root = Node("Body", null, Quad("RootMesh"));
            root.transform.SetPositionAndRotation(new Vector3(7, 2, -3), Quaternion.Euler(12, 35, -8));
            root.transform.localScale = new Vector3(-.01f, .02f, .03f);
            var container = Node("Container", root.transform);
            container.transform.localPosition = new Vector3(2, 3, 4);
            container.transform.localRotation = Quaternion.Euler(20, -15, 40);
            var shared = Quad("Shared");
            var child = Node("Body_LOD0", container.transform, shared);
            child.transform.localScale = new Vector3(2, 1, 3);
            var sibling = Node("Body_LOD1", container.transform, shared);
            sibling.transform.localPosition = new Vector3(-1, 2, 0);
            var filters = root.GetComponentsInChildren<MeshFilter>();
            var expected = new List<Vector3[]>();
            foreach (var filter in filters) {
                var v = filter.sharedMesh.vertices;
                for (int i = 0; i < v.Length; ++i) v[i] = filter.transform.TransformPoint(v[i]) - root.transform.position;
                expected.Add(v);
            }
            var sink = new List<Mesh>();
            FbxExport.NormalizeExportHierarchy(root, sink);
            scratch.AddRange(sink);
            for (int f = 0; f < filters.Length; ++f) {
                var v = filters[f].sharedMesh.vertices;
                for (int i = 0; i < v.Length; ++i)
                    Assert.That((filters[f].transform.TransformPoint(v[i]) - expected[f][i]).magnitude, Is.LessThan(1e-6f));
                Assert.AreEqual(new[] { 0, 2, 1, 0, 3, 2 }, filters[f].sharedMesh.triangles);
            }
            foreach (var t in root.GetComponentsInChildren<Transform>()) {
                Assert.AreEqual(Vector3.zero, t.localPosition);
                Assert.AreEqual(Quaternion.identity, t.localRotation);
                Assert.AreEqual(Vector3.one, t.localScale);
            }
            Assert.AreEqual(Vector3.right, shared.vertices[1], "shared source buffers are untouched");
            Assert.AreNotSame(child.GetComponent<MeshFilter>().sharedMesh, sibling.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void NormalizeHierarchyRejectsSkinBeforeChangingTransforms()
        {
            var root = Node("Animated", null);
            root.transform.localScale = Vector3.one * .01f;
            root.AddComponent<SkinnedMeshRenderer>().sharedMesh = Quad("Skin");
            Assert.Throws<System.InvalidOperationException>(() => FbxExport.NormalizeExportHierarchy(root, new List<Mesh>()));
            Assert.AreEqual(Vector3.one * .01f, root.transform.localScale);
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [TestCase(false)]
        [TestCase(true)]
        public void NormalizedFbxHasMeterUnitsIdentityNodesAndPreservesSizeOnReimport(bool atomic)
        {
            string folderName = "MeshLabFbxNormalize_" + System.Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try {
                var root = Node("Normalized", null);
                root.transform.localRotation = Quaternion.Euler(18, 42, -9);
                root.transform.localScale = new Vector3(.01f, .02f, .03f);
                var mesh = Quad("Surface");
                mesh.RecalculateNormals(); mesh.RecalculateTangents();
                var child = Node("Normalized_LOD0", root.transform, mesh, renderer: true);
                child.transform.localPosition = new Vector3(3, 2, 1);
                var matrix = child.transform.localToWorldMatrix;
                var expected = new Bounds(matrix.MultiplyPoint3x4(mesh.vertices[0]), Vector3.zero);
                foreach (var v in mesh.vertices) expected.Encapsulate(matrix.MultiplyPoint3x4(v));
                var expectedNormal = matrix.inverse.transpose.MultiplyVector(mesh.normals[0]).normalized;
                Vector3 expectedTangent = matrix.MultiplyVector(mesh.tangents[0]);
                expectedTangent = (expectedTangent - expectedNormal * Vector3.Dot(expectedNormal, expectedTangent)).normalized;
                var expectedBitangent = Vector3.Cross(expectedNormal, expectedTangent) * mesh.tangents[0].w;
                var sink = new List<Mesh>();
                FbxExport.NormalizeExportHierarchy(root, sink);
                scratch.AddRange(sink);
                string path = folder + "/normalized.fbx";
                if (atomic) FbxExport.WriteAtomic(path, root, normalizedTransforms: true);
                else FbxExport.Write(path, root, normalizedTransforms: true);

                using (var manager = FbxManager.Create()) {
                    var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                    manager.SetIOSettings(io);
                    var scene = FbxScene.Create(manager, "Check");
                    using (var importer = FbxImporter.Create(manager, "Read")) {
                        Assert.IsTrue(importer.Initialize(Path.GetFullPath(path), -1, io));
                        Assert.IsTrue(importer.Import(scene));
                    }
                    Assert.AreEqual(100, scene.GetGlobalSettings().GetSystemUnit().GetScaleFactor());
                    Assert.AreEqual(FbxAxisSystem.Max, scene.GetGlobalSettings().GetAxisSystem());
                    var nodes = new Stack<FbxNode>(); nodes.Push(scene.GetRootNode());
                    while (nodes.Count > 0) {
                        var node = nodes.Pop();
                        var p = node.LclTranslation.Get(); var r = node.LclRotation.Get(); var s = node.LclScaling.Get();
                        var pre = node.GetPreRotation(FbxNode.EPivotSet.eSourcePivot);
                        var post = node.GetPostRotation(FbxNode.EPivotSet.eSourcePivot);
                        for (int axis = 0; axis < 3; ++axis) {
                            Assert.AreEqual(0, p[axis], 1e-6); Assert.AreEqual(0, r[axis], 1e-6);
                            Assert.AreEqual(1, s[axis], 1e-6);
                            Assert.AreEqual(0, pre[axis], 1e-6); Assert.AreEqual(0, post[axis], 1e-6);
                        }
                        for (int i = 0; i < node.GetChildCount(); ++i) nodes.Push(node.GetChild(i));
                    }
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var settings = (ModelImporter)AssetImporter.GetAtPath(path);
                settings.isReadable = true; settings.bakeAxisConversion = true;
                settings.importNormals = ModelImporterNormals.Import;
                settings.importTangents = ModelImporterTangents.Import;
                settings.SaveAndReimport();
                var imported = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var importedFilter = imported.GetComponentInChildren<MeshFilter>();
                var importedVertices = importedFilter.sharedMesh.vertices;
                var bounds = new Bounds(importedFilter.transform.TransformPoint(importedVertices[0]), Vector3.zero);
                foreach (var v in importedVertices) bounds.Encapsulate(importedFilter.transform.TransformPoint(v));
                Assert.That((bounds.center - expected.center).magnitude, Is.LessThan(1e-5f),
                    $"expected {expected.center:F6}, imported {bounds.center:F6}, matrix {importedFilter.transform.localToWorldMatrix}, first vertex {importedVertices[0]:F6}");
                Assert.That((bounds.size - expected.size).magnitude, Is.LessThan(1e-5f));
                foreach (var normal in importedFilter.sharedMesh.normals)
                    Assert.That(Vector3.Dot(importedFilter.transform.localToWorldMatrix.inverse.transpose.MultiplyVector(normal).normalized,
                        expectedNormal), Is.GreaterThan(.9999f));
                var normals = importedFilter.sharedMesh.normals;
                var tangents = importedFilter.sharedMesh.tangents;
                Assert.AreEqual(normals.Length, tangents.Length);
                for (int i = 0; i < tangents.Length; ++i) {
                    Vector3 t = importedFilter.transform.localToWorldMatrix.MultiplyVector(tangents[i]).normalized;
                    var n = importedFilter.transform.localToWorldMatrix.inverse.transpose.MultiplyVector(normals[i]).normalized;
                    var b = Vector3.Cross(n, t) * tangents[i].w;
                    Assert.That(Vector3.Dot(t, expectedTangent), Is.GreaterThan(.9999f));
                    Assert.That(Vector3.Dot(b, expectedBitangent), Is.GreaterThan(.9999f));
                }
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void FailedNormalizedAtomicWriteLeavesExistingFbxBytesAndMetaUntouched()
        {
            string folderName = "MeshLabFbxAtomic_" + System.Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try {
                var root = Node("Atomic", null, Quad("Atomic"), renderer: true);
                string path = folder + "/atomic.fbx";
                FbxExport.Write(path, root, normalizedTransforms: true);
                AssetDatabase.ImportAsset(path);
                var original = File.ReadAllBytes(path);
                var meta = File.ReadAllBytes(path + ".meta");
                root.transform.localScale = Vector3.one * 2;
                Assert.Throws<IOException>(() => FbxExport.WriteAtomic(path, root, normalizedTransforms: true));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
                Assert.IsFalse(File.Exists(path + ".tmp"));
                Assert.IsFalse(File.Exists(path + ".tmp.normalized.tmp"));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void NormalizedFbxRetainsEmbeddedTexturesAfterDeletingTheOriginalMap()
        {
            string folderName = "MeshLabFbxEmbedded_" + System.Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try {
                var texture = new Texture2D(2, 2);
                scratch.Add(texture);
                texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
                texture.Apply();
                string mapPath = folder + "/original.png";
                File.WriteAllBytes(mapPath, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(mapPath);
                var material = new Material(Shader.Find("Standard"));
                scratch.Add(material);
                material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(mapPath);
                var root = Node("Embedded", null, Quad("Embedded"), renderer: true);
                root.GetComponent<MeshRenderer>().sharedMaterial = material;
                string path = folder + "/embedded.fbx";
                FbxExport.Write(path, root, embedTextures: true, normalizedTransforms: true);
                AssetDatabase.DeleteAsset(mapPath);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.CreateFolder(folder, "Extracted");
                Assert.IsTrue(((ModelImporter)AssetImporter.GetAtPath(path)).ExtractTextures(folder + "/Extracted"));
                AssetDatabase.Refresh();
                Assert.IsNotEmpty(AssetDatabase.FindAssets("t:Texture2D", new[] { folder + "/Extracted" }),
                    "the converted file carries the map independently of its source path");
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
#endif

        [Test]
        public void CollisionPlacementRefusesAmbiguousSourcesAndTracksAllTemporaryMeshes()
        {
            var root = Node("Root", null);
            Node("First_LOD0", root.transform, Quad("Twin_LOD0"));
            var second = Node("Second_LOD0", root.transform, Quad("Twin_LOD0"));
            second.transform.localPosition = Vector3.right;
            var frames = FbxExport.CollisionSourceFrames(root);
            Assert.IsFalse(frames.ContainsKey("Twin_LOD0"));
            var data = new List<(string, List<Mesh>, bool)> {
                ("Twin_LOD0", new List<Mesh> { Quad("Twin_COL_Hull0"), Quad("Twin_COL_Hull1") }, true),
                ("Other", new List<Mesh> { Quad("Other_COL") }, false)
            };
            var sink = new List<Mesh>();
            Assert.Throws<System.InvalidOperationException>(() => FbxExport.InjectCollisionMeshes(root, data, sink, frames));
            Assert.AreEqual(3, sink.Count, "the caller owns all sidecar meshes even when the first placement fails");
        }

        [Test]
        public void InjectCollisionMeshesReplacesExistingColChildren()
        {
            var root = Node("Root", null);
            Node("Old_COL", root.transform, Quad("Old_COL"));
            var data = new List<(string, List<Mesh>, bool)>
            {
                ("Box", new List<Mesh> { Quad("Box_COL") }, false),
                ("Rock", new List<Mesh> { Quad("Rock_COL_Hull0"), Quad("Rock_COL_Hull1") }, true),
            };
            var sink = new List<Mesh>();

            int count = FbxExport.InjectCollisionMeshes(root, data, sink);

            Assert.AreEqual(3, count);
            Assert.AreEqual(3, sink.Count);
            Assert.IsNull(root.transform.Find("Old_COL"));
            Assert.IsNotNull(root.transform.Find("Box_COL").GetComponent<MeshFilter>());
            var container = root.transform.Find("Rock_COL");
            Assert.IsNull(container.GetComponent<MeshFilter>());
            Assert.AreEqual(2, container.childCount);
            Assert.AreEqual("Rock_COL_Hull1", container.GetChild(1).name);
        }

        [Test]
        public void TrimMaterialArraysCutsToTheSubmeshCount()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            var mat = new Material(shader); scratch.Add(mat);
            var go = Node("A", null, Quad("A"), renderer: true);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { mat, mat, mat };

            FbxExport.TrimMaterialArrays(go);

            Assert.AreEqual(1, mr.sharedMaterials.Length);
        }

        [Test]
        public void RendererSettingsCopyLeavesMaterialsAloneWhenAsked()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            var a = new Material(shader); scratch.Add(a);
            var b = new Material(shader); scratch.Add(b);
            var src = Node("S", null, renderer: true).GetComponent<MeshRenderer>();
            var dst = Node("D", null, renderer: true).GetComponent<MeshRenderer>();
            src.sharedMaterial = a;
            src.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            src.scaleInLightmap = 0.25f;
            dst.sharedMaterial = b;

            RendererSettings.Copy(src, dst, includeMaterials: false);
            Assert.AreSame(b, dst.sharedMaterial);
            Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, dst.shadowCastingMode);
            Assert.AreEqual(0.25f, dst.scaleInLightmap);

            RendererSettings.Copy(src, dst);
            Assert.AreSame(a, dst.sharedMaterial);
        }

        // ── sidecar ──

        [TestCase("Assets/A/Chair.fbx", true)]
        [TestCase("Assets/A/Chair.FBX", true)]
        [TestCase("Assets/A/Chair.asset", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void FbxPathsEndInFbxInAnyCase(string path, bool isFbx)
            => Assert.AreEqual(isFbx, SidecarStore.IsFbxPath(path));

        [Test]
        public void CollisionEntryValidationAcceptsBothIndexEncodings()
        {
            // Two hulls of three vertices: hull 0 at offset 0, hull 1 at offset 3.
            var entry = new CollisionMeshEntry
            {
                meshGroupKey = "Rock", mode = 1,
                allPositions = new Vector3[6],
                positionOffsets = new[] { 0, 3 },
                triangleOffsets = new[] { 0, 3 },
                allTriangles = new[] { 0, 1, 2, 3, 4, 5 }, // hull 1 indexes the flattened array
            };
            Assert.IsTrue(SidecarStore.TryValidateCollisionEntry(entry, out var global, out _));
            Assert.IsTrue(global[1], "indices 3..5 address the flattened array");

            entry.allTriangles = new[] { 0, 1, 2, 0, 1, 2 }; // legacy: hull 1 indexes its own vertices
            Assert.IsTrue(SidecarStore.TryValidateCollisionEntry(entry, out global, out _));
            Assert.IsFalse(global[1], "indices 0..2 can only be local to hull 1");

            entry.allTriangles = new[] { 0, 1, 2, 7, 8, 9 };
            Assert.IsFalse(SidecarStore.TryValidateCollisionEntry(entry, out _, out string error));
            StringAssert.Contains("out-of-range", error);

            entry.triangleOffsets = new[] { 0 };
            Assert.IsFalse(SidecarStore.TryValidateCollisionEntry(entry, out _, out error));
            StringAssert.Contains("mismatched", error);
        }
    }
}
