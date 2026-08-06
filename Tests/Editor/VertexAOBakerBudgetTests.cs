using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class VertexAOBakerBudgetTests
    {
        [Test]
        public void BakeMultiMesh_ExcessiveWorkBudget_IsRejectedBeforeBake()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            var inputs = new List<(Mesh mesh, Matrix4x4 transform)>
            {
                (mesh, Matrix4x4.identity)
            };
            var settings = new VertexAOSettings { sampleCount = int.MaxValue };

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                "\\[LightmapUV\\].*\\[Vertex AO\\] Bake refused: resource budget exceeded"));
            var result = VertexAOBaker.BakeMultiMesh(inputs, settings);

            Assert.That(result, Is.Empty);
            Object.DestroyImmediate(mesh);
        }
    }
}
