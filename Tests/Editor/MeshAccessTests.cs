// MeshAccessTests.cs — the readable copy and the matrix bake on meshes the engine lets
// us build in a test (readable); the Read/Write-disabled path needs an import.
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshAccessTests
    {
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
        [TestCase(UnityEngine.Rendering.IndexFormat.UInt16)]
        [TestCase(UnityEngine.Rendering.IndexFormat.UInt32)]
        public void UnreadableMeshLodCopiesOnlyTheActiveIndexRange(UnityEngine.Rendering.IndexFormat format)
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
                copy = MeshAccess.ReadableCopy(source);
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3 }, copy.triangles);
                Assert.AreEqual(4, copy.vertexCount);
                CollectionAssert.AreEqual(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, copy.uv);
            }
            finally { Object.DestroyImmediate(source); if (copy) Object.DestroyImmediate(copy); }
        }
#endif

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
