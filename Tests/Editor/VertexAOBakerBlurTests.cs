using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class VertexAOBakerBlurTests
    {
        [Test]
        public void BlurAO3D_SmallNeighborhood_AveragesAllVertices()
        {
            float[] ao = { 0f, 0.5f, 1f };
            var positions = new[] { Vector3.zero, Vector3.zero, Vector3.zero };

            float[] result = VertexAOBaker.BlurAO3D(ao, positions, 1, 1f, 1f);

            Assert.That(result[0], Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(result[1], Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(result[2], Is.EqualTo(0.5f).Within(0.0001f));
        }

        [Test, Timeout(10000)]
        public void BlurAO3D_DenseMesh_CompletesWithinWorkBudget()
        {
            const int vertexCount = 50000;
            var ao = new float[vertexCount];
            var positions = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                ao[i] = i % 2;

            var stopwatch = Stopwatch.StartNew();
            float[] result = VertexAOBaker.BlurAO3D(ao, positions, 10, 1f, 0.01f);

            Assert.That(result, Has.Length.EqualTo(vertexCount));
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(10000));
        }
    }
}
