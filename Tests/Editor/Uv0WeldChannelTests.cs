using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

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
                // Each kept vertex keeps the lightmap coordinate authored at its corner.
                var positions = mesh.vertices;
                var authoredPositions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1),
                                                new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) };
                for (int i = 0; i < positions.Length; i++)
                {
                    int authored = System.Array.IndexOf(authoredPositions, positions[i]);
                    Assert.AreEqual(SharedLightmap[authored], lightmap[i], $"UV1 of vertex {i}.");
                }
                Assert.AreEqual(3, mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord2),
                    "UV2 keeps its dimension.");
                var kept = new List<Vector3>();
                mesh.GetUVs(2, kept);
                Assert.AreEqual(4, kept.Count, "UV2 survives the weld.");
                foreach (var v in kept) Assert.AreEqual(7f, v.z, "UV2's third component survives the weld.");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PackedVertex
        {
            public Vector3 position;
            public Vector3 normal;
            public Vector2 uv0;
            public float uv3;
            public ushort uv4x, uv4y;
        }

        [Test]
        public void AWeldKeepsAScalarAndAHalfUvChannelsLayout()
        {
            var layout = new[]
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float16, 2),
            };
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1),
                                    new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) };
            var uv0 = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1),
                              new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1) };
            // Vertices 3 and 4 repeat 0 and 2 in every channel: a false seam.
            float[] scalar = { .25f, .5f, .75f, .25f, .75f, 1f };
            var vertices = new PackedVertex[6];
            for (int i = 0; i < 6; i++)
                vertices[i] = new PackedVertex
                {
                    position = positions[i], normal = Vector3.up, uv0 = uv0[i], uv3 = scalar[i],
                    uv4x = Mathf.FloatToHalf(scalar[i]), uv4y = Mathf.FloatToHalf(1f - scalar[i]),
                };
            var mesh = new Mesh();
            mesh.SetVertexBufferParams(6, layout);
            mesh.SetVertexBufferData(vertices, 0, 0, 6);
            mesh.triangles = new[] { 0, 2, 1, 3, 5, 4 };
            try
            {
                Assert.IsTrue(Uv0Analyzer.WeldInPlace(mesh));
                Assert.AreEqual(4, mesh.vertexCount);
                Assert.AreEqual(1, mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord3), "The scalar channel stays scalar.");
                Assert.AreEqual(VertexAttributeFormat.Float16, mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord4),
                    "The half channel stays half.");

                var weldedPositions = mesh.vertices;
                var kept = new List<Vector4>();
                mesh.GetUVs(3, kept);
                var half = new List<Vector2>();
                mesh.GetUVs(4, half);
                for (int i = 0; i < weldedPositions.Length; i++)
                {
                    int authored = System.Array.IndexOf(positions, weldedPositions[i]);
                    Assert.AreEqual(scalar[authored], kept[i].x, 1e-6f, $"UV3 of vertex {i}.");
                    Assert.AreEqual(scalar[authored], half[i].x, 1e-3f, $"UV4.x of vertex {i}.");
                }
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
