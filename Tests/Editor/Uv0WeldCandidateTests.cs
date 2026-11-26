using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    // The Cleanup scan offers Batch Weld for a mesh the weld changes, and for no other.
    public sealed class Uv0WeldCandidateTests
    {
        static Mesh Build(Vector3[] positions, Vector2[] uv, int[] triangles)
        {
            var mesh = new Mesh { vertices = positions, uv = uv, triangles = triangles };
            var normals = new Vector3[positions.Length];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            return mesh;
        }

        [Test]
        public void ATriangleWithNoUvAreaIsNoWeldCandidate()
        {
            // The second triangle's UVs lie on one line; no two vertices share pos, UV and normal.
            var mesh = Build(
                new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
                new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(2, 2) },
                new[] { 0, 2, 1, 0, 3, 2 });
            try
            {
                var report = Uv0Analyzer.Analyze(mesh);
                Assert.Greater(report.degenerateTriangles, 0, "The case needs a triangle with no UV0 area.");
                Assert.IsFalse(report.HasFalseSeams);
                Assert.IsFalse(Uv0Analyzer.WeldInPlace(mesh), "A weld leaves such a mesh as it is.");
                Assert.AreEqual(4, mesh.vertexCount);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AFalseSeamIsAWeldCandidateTheWeldRemoves()
        {
            // Vertices 3 and 4 repeat 0 and 2: the same position, UV and normal on other indices.
            var mesh = Build(
                new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1),
                        new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
                new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1),
                        new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1) },
                new[] { 0, 2, 1, 3, 5, 4 });
            try
            {
                Assert.IsTrue(Uv0Analyzer.Analyze(mesh).HasFalseSeams);
                Assert.IsTrue(Uv0Analyzer.WeldInPlace(mesh));
                Assert.AreEqual(4, mesh.vertexCount);
                Assert.IsFalse(Uv0Analyzer.Analyze(mesh).HasFalseSeams, "After the weld the scan offers no weld again.");
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
