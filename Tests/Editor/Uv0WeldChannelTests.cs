using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    // A weld merges a false seam: vertices that agree in every channel the mesh has. A lightmap
    // seam (the same position, UV0 and normal on another UV1) is no false seam, and the welded
    // mesh keeps every channel of the vertices it keeps.
    public sealed class Uv0WeldChannelTests
    {
        // Two triangles; vertices 3 and 4 repeat 0 and 2 in position, UV0 and normal.
        static Mesh Build(Vector2[] uv1)
        {
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1),
                                   new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1),
                             new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 2, 1, 3, 5, 4 },
            };
            var normals = new Vector3[6];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.SetUVs(1, uv1);
            return mesh;
        }

        static readonly Vector2[] SharedLightmap =
            { new Vector2(0, 0), new Vector2(.5f, 0), new Vector2(.5f, .5f), new Vector2(0, 0), new Vector2(.5f, .5f), new Vector2(0, .5f) };

        [Test]
        public void ALightmapSeamIsNoFalseSeam()
        {
            // The second triangle sits elsewhere in the lightmap: its corners are a UV1 seam.
            var mesh = Build(new[] { new Vector2(0, 0), new Vector2(.5f, 0), new Vector2(.5f, .5f),
                                     new Vector2(.6f, .6f), new Vector2(.9f, .9f), new Vector2(.6f, .9f) });
            try
            {
                Assert.AreEqual(0, Uv0Analyzer.Analyze(mesh).falseSeamPairs);
                Assert.IsNull(Uv0Analyzer.GetFalseSeamVertices(mesh));
                Assert.IsFalse(Uv0Analyzer.WeldInPlace(mesh), "A weld must not close a lightmap seam.");
                Assert.AreEqual(6, mesh.vertexCount);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AFalseSeamWeldsAndKeepsEveryChannel()
        {
            var mesh = Build(SharedLightmap);
            var uv2 = new List<Vector3>();
            for (int i = 0; i < 6; i++) uv2.Add(new Vector3(i == 3 ? 0 : i, i == 4 ? 2 : i, 7));
            uv2[3] = uv2[0]; uv2[4] = uv2[2];
            mesh.SetUVs(2, uv2);
            try
            {
                Assert.AreEqual(2, Uv0Analyzer.Analyze(mesh).falseSeamPairs);
                Assert.IsTrue(Uv0Analyzer.WeldInPlace(mesh));
                Assert.AreEqual(4, mesh.vertexCount);

                var lightmap = new List<Vector2>();
                mesh.GetUVs(1, lightmap);
                Assert.AreEqual(4, lightmap.Count, "UV1 survives the weld.");
                Assert.AreEqual(3, mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord2),
                    "UV2 keeps its dimension.");
                var kept = new List<Vector3>();
                mesh.GetUVs(2, kept);
                Assert.AreEqual(4, kept.Count, "UV2 survives the weld.");
                foreach (var v in kept) Assert.AreEqual(7f, v.z, "UV2's third component survives the weld.");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void WeldUv0KeepsALightmapSeamToo()
        {
            var mesh = Build(new[] { new Vector2(0, 0), new Vector2(.5f, 0), new Vector2(.5f, .5f),
                                     new Vector2(.6f, .6f), new Vector2(.9f, .9f), new Vector2(.6f, .9f) });
            Mesh welded = null;
            try
            {
                welded = Uv0Analyzer.WeldUv0(mesh);
                Assert.AreEqual(6, welded.vertexCount);
            }
            finally
            {
                if (welded != null) Object.DestroyImmediate(welded);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
