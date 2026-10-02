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
