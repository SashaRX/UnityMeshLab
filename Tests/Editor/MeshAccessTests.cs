// MeshAccessTests.cs — the readable copy and the matrix bake on meshes the engine lets
// us build in a test (readable); the Read/Write-disabled path needs an import.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshAccessTests
    {
        [TestCase(false)] [TestCase(true)]
        public void ReadWriteDisabledModelSnapshotPreservesBuffersWithoutReimporting(bool raw)
        {
            string folder = "Assets/__MeshLabReadOnly_" + System.Guid.NewGuid().ToString("N");
            string path = folder + "/probe.obj";
            Mesh copy = null;
            try {
                AssetDatabase.CreateFolder("Assets",System.IO.Path.GetFileName(folder));
                System.IO.File.WriteAllText(path,"v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n");
                AssetDatabase.ImportAsset(path);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true; importer.SaveAndReimport();
                var original = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<MeshFilter>().sharedMesh;
                var vertices = original.vertices; var normals = original.normals; var uv = original.uv; var indices = original.triangles;
                importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = false; importer.SaveAndReimport();
                original = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.IsFalse(original.isReadable);
                var assetBytes = System.IO.File.ReadAllBytes(path); var metaBytes = System.IO.File.ReadAllBytes(path+".meta");
                MeshAccessImportWatch.Path = path; MeshAccessImportWatch.Count = 0;
                copy = raw ? MeshAccess.Readable(original, out _, preserveVertexLayout: true) : MeshAccess.ReadableCopy(original);
                Assert.AreEqual(0,MeshAccessImportWatch.Count,"Reading an imported mesh must not trigger Bakery or dependent prefab imports.");
                Assert.IsFalse(original.isReadable); Assert.IsFalse(((ModelImporter)AssetImporter.GetAtPath(path)).isReadable);
                CollectionAssert.AreEqual(vertices,copy.vertices); CollectionAssert.AreEqual(normals,copy.normals);
                CollectionAssert.AreEqual(uv,copy.uv); CollectionAssert.AreEqual(indices,copy.triangles);
                CollectionAssert.AreEqual(assetBytes,System.IO.File.ReadAllBytes(path));
                CollectionAssert.AreEqual(metaBytes,System.IO.File.ReadAllBytes(path+".meta"));
            }
            finally {
                MeshAccessImportWatch.Path = null;
                if (copy) Object.DestroyImmediate(copy);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void RawModelSnapshotFallbackRestoresImporterAndPreservesLayout(bool incomplete)
        {
            string folder = "Assets/__MeshLabRawFallback_" + System.Guid.NewGuid().ToString("N");
            string path = folder + "/probe.obj"; Mesh copy = null;
            var empty = new Mesh();
            try {
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
                System.IO.File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n");
                AssetDatabase.ImportAsset(path);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = false; importer.SaveAndReimport();
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<MeshFilter>().sharedMesh;
                var layout = source.GetVertexAttributes();
                var assetBytes = System.IO.File.ReadAllBytes(path); var metaBytes = System.IO.File.ReadAllBytes(path + ".meta");
                MeshAccessImportWatch.Path = path; MeshAccessImportWatch.Count = 0;
                copy = MeshAccess.RawReadableCopy(source, (mesh, destination) => {
                    if (!incomplete) throw new System.NotSupportedException("Simulated unavailable Editor snapshot");
                    using var data = MeshUtility.AcquireReadOnlyMeshData(empty);
                    MeshAccess.ValidateMeshData(mesh, data[0]);
                    return destination;
                });
                Assert.AreEqual(2, MeshAccessImportWatch.Count);
                Assert.IsFalse(((ModelImporter)AssetImporter.GetAtPath(path)).isReadable);
                CollectionAssert.AreEqual(layout, copy.GetVertexAttributes());
                Assert.AreEqual(3, copy.vertexCount); Assert.AreEqual(3, copy.triangles.Length);
                CollectionAssert.AreEqual(assetBytes, System.IO.File.ReadAllBytes(path));
                CollectionAssert.AreEqual(metaBytes, System.IO.File.ReadAllBytes(path + ".meta"));
            }
            finally {
                MeshAccessImportWatch.Path = null;
                if (copy) Object.DestroyImmediate(copy); Object.DestroyImmediate(empty);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        static Mesh QuadTopology()
        {
            var m = new Mesh { name = "Q" };
            m.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
            m.colors = new[] { new Color(0.2f, 0.4f, 0.6f, 1f), Color.white, Color.white, Color.white };
            m.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            m.uv3 = new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
            m.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.SetIndices(new[] { 0, 1, 2, 3 }, MeshTopology.Quads, 0);
            return m;
        }

        [Test]
        public void ReadableCopyFailureDoesNotLeaveAnAllocatedMesh()
        {
            int count = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            for (int attempt = 0; attempt < 5; ++attempt)
                Assert.Throws<System.NullReferenceException>(() => MeshAccess.ReadableCopy(null));
            Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Mesh>().Length);
        }

        [TestCase(false)] [TestCase(true)]
        public void EmptyMeshCopiesWithoutRequestingAMissingPositionChannel(bool upload)
        {
            var source=new Mesh {name="empty source"}; Mesh copy=null;
            try {
                if(upload) source.UploadMeshData(true);
                Assert.AreEqual(0,source.vertexCount);
                Assert.IsFalse(source.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Position));
                copy=MeshAccess.ReadableCopy(source);
                Assert.IsTrue(copy.isReadable); Assert.AreEqual(0,copy.vertexCount);
                Assert.AreEqual(source.subMeshCount,copy.subMeshCount);
            }
            finally { Object.DestroyImmediate(source); if(copy) Object.DestroyImmediate(copy); }
        }

        [Test]
        public void PositionlessNonemptyMeshRefusesWithoutLeakingACopy()
        {
            var source=new Mesh {name="positionless source"};
            try {
                source.SetVertexBufferParams(3,new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Normal));
                int count=Resources.FindObjectsOfTypeAll<Mesh>().Length;
                var error=Assert.Throws<System.InvalidOperationException>(()=>MeshAccess.ReadableCopy(source));
                StringAssert.Contains("Position",error.Message);
                Assert.AreEqual(count,Resources.FindObjectsOfTypeAll<Mesh>().Length);
                Assert.AreEqual(3,source.vertexCount);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CpuAoFailureReleasesUnreadableCopies(bool correction)
        {
            var source = QuadTopology();
            source.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
            source.UploadMeshData(true);
            try {
                // Probe the engine's read-only MeshData path before measuring.
                var readable = MeshAccess.Readable(source, out bool isCopy);
                Assert.IsTrue(isCopy); Object.DestroyImmediate(readable);
                var targets = new System.Collections.Generic.List<(Mesh, Matrix4x4)> { (source, Matrix4x4.identity) };
                var settings = new VertexAOSettings { sampleCount = -1, useGPU = false };
                int count = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                for (int attempt = 0; attempt < 3; ++attempt) {
                    if (correction) {
                        var ao = new System.Collections.Generic.Dictionary<Mesh, float[]> { { source, new[] { 1f, 1f, 1f, 1f } } };
                        Assert.Throws<System.OverflowException>(() => VertexAOBaker.ApplyFaceAreaCorrection(ao, targets, null, settings));
                    } else Assert.Throws<System.OverflowException>(() => VertexAOBaker.BakeMultiMesh(targets, settings));
                    Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Mesh>().Length);
                }
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void CpuAoCancellationReleasesBothBvhAndTargetCopies()
        {
            var source = QuadTopology();
            source.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
            source.UploadMeshData(true);
            try {
                var readable = MeshAccess.Readable(source, out bool isCopy);
                Assert.IsTrue(isCopy); Object.DestroyImmediate(readable);
                var targets = new System.Collections.Generic.List<(Mesh, Matrix4x4)> { (source, Matrix4x4.identity) };
                int count = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                UvProgress.Begin("AO cancellation resource test", cancelable: true);
                UvProgress.RequestCancel();
                VertexAOBaker.BakeMultiMesh(targets, new VertexAOSettings { sampleCount = 8, useGPU = false });
                Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Mesh>().Length);
            }
            finally { UvProgress.End(); Object.DestroyImmediate(source); }
        }

        [Test]
        public void ReadableCopyKeepsTopologyEveryUvChannelAndFloatColours()
        {
            var src = QuadTopology();
            var copy = MeshAccess.ReadableCopy(src);
            try
            {
                Assert.AreEqual(MeshTopology.Quads, copy.GetTopology(0), "a quad submesh is not triangulated");
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, copy.GetIndices(0));
                Assert.AreEqual(4, copy.uv3.Length, "an all-zero channel is still a channel");
                Assert.AreEqual(new Color(0.2f, 0.4f, 0.6f, 1f), copy.colors[0]);
                Assert.AreEqual(4, copy.tangents.Length);
            }
            finally { Object.DestroyImmediate(src); Object.DestroyImmediate(copy); }
        }

#if UNITY_6000_2_OR_NEWER
        [TestCase(UnityEngine.Rendering.IndexFormat.UInt16, false)]
        [TestCase(UnityEngine.Rendering.IndexFormat.UInt32, false)]
        [TestCase(UnityEngine.Rendering.IndexFormat.UInt16, true)]
        [TestCase(UnityEngine.Rendering.IndexFormat.UInt32, true)]
        public void UnreadableMeshLodCopiesOnlyTheActiveIndexRange(UnityEngine.Rendering.IndexFormat format, bool raw)
        {
            var source = QuadTopology();
            Mesh copy = null;
            try {
                source.indexFormat = format;
                source.SetIndices(new[] { 0, 1, 2, 0, 2, 3, 0, 1, 3 }, MeshTopology.Triangles, 0);
                source.lodCount = 2;
                source.SetLods(new[] {
                    new MeshLodRange { indexStart = 0, indexCount = 6 },
                    new MeshLodRange { indexStart = 6, indexCount = 3 }
                }, 0);
                source.UploadMeshData(true);
                Assert.AreEqual(6, source.GetIndexCount(0));
                using (var data = Mesh.AcquireReadOnlyMeshData(source))
                    Assert.AreEqual(9, data[0].GetSubMesh(0).indexCount,
                        "MeshData includes the backing ranges of all mesh LODs.");
                copy = raw ? MeshAccess.Readable(source, out _, preserveVertexLayout: true) : MeshAccess.ReadableCopy(source);
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3 }, copy.triangles);
                Assert.AreEqual(4, copy.vertexCount);
                CollectionAssert.AreEqual(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, copy.uv);
            }
            finally { Object.DestroyImmediate(source); if (copy) Object.DestroyImmediate(copy); }
        }
#endif

        /// <summary>The fingerprint follows stored content: not the name, and not Read/Write for a static mesh.</summary>
        [Test]
        public void FingerprintFollowsContentNotNameOrReadability()
        {
            var source = QuadTopology(); var renamed = Object.Instantiate(source); var unreadable = Object.Instantiate(source);
            try {
                renamed.name = "renamed";
                unreadable.UploadMeshData(true); Assert.IsFalse(unreadable.isReadable);
                var expected = MeshAccess.Fingerprint(source);
                Assert.IsNotNull(expected);
                CollectionAssert.AreEqual(expected, MeshAccess.Fingerprint(renamed));
                CollectionAssert.AreEqual(expected, MeshAccess.Fingerprint(unreadable));
                var uv = renamed.uv; uv[2].x = System.BitConverter.Int32BitsToSingle(System.BitConverter.SingleToInt32Bits(uv[2].x) - 1);
                renamed.uv = uv;
                CollectionAssert.AreNotEqual(expected, MeshAccess.Fingerprint(renamed), "One ulp of UV0 is a content change.");
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(renamed); Object.DestroyImmediate(unreadable); }
        }

        [Test]
        public void MirroredBakeRewindsQuadsAsQuads()
        {
            var m = QuadTopology();
            try
            {
                MeshTransform.BakeMatrix(m, Matrix4x4.Scale(new Vector3(-1, 1, 1)));
                Assert.AreEqual(MeshTopology.Quads, m.GetTopology(0));
                CollectionAssert.AreEqual(new[] { 0, 3, 2, 1 }, m.GetIndices(0), "the quad is re-wound, not split into triangles");
                Assert.AreEqual(-1f, m.vertices[1].x, 1e-6f);
                Assert.AreEqual(-1f, m.tangents[0].w, "handedness flips with the mirror");
            }
            finally { Object.DestroyImmediate(m); }
        }
    }
}

namespace SashaRX.UnityMeshLab.Tests
{
    internal sealed class MeshAccessImportWatch : AssetPostprocessor
    {
        internal static string Path;
        internal static int Count;
        void OnPostprocessModel(GameObject root) { if (assetPath == Path) ++Count; }
    }
}
