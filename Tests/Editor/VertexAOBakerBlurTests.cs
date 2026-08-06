using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class VertexAOBakerBlurTests
    {
        [Test]
        public void BlurAO_LargeCoincidentVertexGroup_RemainsBounded()
        {
            const int vertexCount = 2048;
            var ao = new float[vertexCount];
            var positions = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                ao[i] = 0.5f;

            float[] result = VertexAOBaker.BlurAO(
                ao, new int[0], vertexCount, 1, 1f, positions);

            Assert.AreEqual(vertexCount, result.Length);
            for (int i = 0; i < result.Length; i++)
                Assert.AreEqual(0.5f, result[i], 0.00001f);
        }

        [Test]
        public void BlurAO_OrdinarySeamGroup_StillAveragesAllDuplicates()
        {
            var ao = new[] { 0f, 0.5f, 1f };
            var positions = new[] { Vector3.zero, Vector3.zero, Vector3.zero };

            float[] result = VertexAOBaker.BlurAO(
                ao, new int[0], ao.Length, 1, 1f, positions);

            Assert.AreEqual(0.75f, result[0], 0.00001f);
            Assert.AreEqual(0.5f, result[1], 0.00001f);
            Assert.AreEqual(0.25f, result[2], 0.00001f);
        }
    }
}
