using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>
    /// Weld and symmetry-split passes edit triangulated indices. A Keep Quads import
    /// has four indices per quad, so slicing the edited triangles by the native index
    /// count moved faces onto their neighbours' corners and triangulated every submesh.
    /// </summary>
    public class SubmeshTopologyTests
    {
        // Two quads with duplicated corners along their shared edge (a false UV seam),
        // then a separate triangle: 8 quad indices are not a multiple of three.
        static Mesh QuadsAndTriangle()
        {
            var mesh = new Mesh { name = "Keep Quads import" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up,
                Vector3.right, new Vector3(2, 0, 0), new Vector3(2, 1, 0), new Vector3(1, 1, 0),
                new Vector3(5, 0, 0), new Vector3(6, 0, 0), new Vector3(5, 1, 0) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up,
                Vector2.right, new Vector2(2, 0), new Vector2(2, 1), Vector2.one,
                new Vector2(5, 5), new Vector2(6, 5), new Vector2(5, 6) };
            mesh.subMeshCount = 2;
            mesh.SetIndices(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, MeshTopology.Quads, 0);
            mesh.SetIndices(new[] { 8, 9, 10 }, MeshTopology.Triangles, 1);
            mesh.RecalculateNormals();
            return mesh;
        }

        static void AssertSameCorners(Mesh expected, Mesh actual, int submesh)
        {
            Assert.AreEqual(expected.GetTopology(submesh), actual.GetTopology(submesh));
            var a = expected.GetIndices(submesh); var b = actual.GetIndices(submesh);
            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; ++i)
            {
                Assert.AreEqual(expected.vertices[a[i]], actual.vertices[b[i]], $"submesh {submesh} corner {i} position");
                Assert.AreEqual(expected.uv[a[i]], actual.uv[b[i]], $"submesh {submesh} corner {i} UV0");
            }
        }

        [Test]
        public void UvEdgeWeldKeepsQuadSubmeshesAndTheirFaces()
        {
            var mesh = QuadsAndTriangle(); Mesh welded = null;
            try
            {
                welded = Uv0Analyzer.UvEdgeWeld(mesh);
                Assert.AreNotSame(mesh, welded, "The false seam must weld.");
                Assert.AreEqual(9, welded.vertexCount);
                Assert.AreEqual(2, welded.subMeshCount);
                AssertSameCorners(mesh, welded, 0);
                AssertSameCorners(mesh, welded, 1);
            }
            finally
            {
                if (welded != mesh) Object.DestroyImmediate(welded);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void WeldInPlaceKeepsQuadSubmeshesAndTheirFaces()
        {
            var mesh = QuadsAndTriangle(); var original = Object.Instantiate(mesh);
            try
            {
                Assert.IsTrue(Uv0Analyzer.WeldInPlace(mesh), "The false seam must weld.");
                Assert.AreEqual(9, mesh.vertexCount);
                AssertSameCorners(original, mesh, 0);
                AssertSameCorners(original, mesh, 1);
            }
            finally { Object.DestroyImmediate(mesh); Object.DestroyImmediate(original); }
        }

        [Test]
        public void SeparatedQuadHalvesBecomeTrianglesOfTheSameSubmesh()
        {
            var mesh = QuadsAndTriangle();
            try
            {
                var topology = SubmeshTopology.Capture(mesh);
                Assert.AreEqual(15, topology.Triangles.Length, "Two quads give four triangles, then the triangle submesh.");
                CollectionAssert.AreEqual(new[] { 8, 9, 10 }, topology.Triangles.Skip(12));
                // A per-vertex remap keeps both halves of every quad together.
                var shifted = new int[topology.Triangles.Length];
                for (int i = 0; i < shifted.Length; ++i) shifted[i] = topology.Triangles[i] == 4 ? 1 : topology.Triangles[i];
                topology.Apply(mesh, shifted);
                Assert.AreEqual(MeshTopology.Quads, mesh.GetTopology(0));
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 1, 5, 6, 7 }, mesh.GetIndices(0));
                CollectionAssert.AreEqual(new[] { 8, 9, 10 }, mesh.GetIndices(1));
                // A split that moves one half of a quad onto other vertices leaves only triangles.
                var split = (int[])shifted.Clone(); split[3] = 10; split[4] = 9; split[5] = 8;
                topology.Apply(mesh, split);
                Assert.AreEqual(MeshTopology.Triangles, mesh.GetTopology(0));
                CollectionAssert.AreEqual(split.Take(12), mesh.GetIndices(0));
                Assert.AreEqual(MeshTopology.Triangles, mesh.GetTopology(1));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void LineSubmeshesAreNotEditedThroughTriangles()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            try
            {
                mesh.SetIndices(new[] { 0, 1, 1, 2 }, MeshTopology.Lines, 0);
                Assert.IsNull(SubmeshTopology.Capture(mesh));
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
