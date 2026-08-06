using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class VertexAOBakerBlurTests
    {
        [Test, Timeout(5000)]
        public void BlurAO_DenseCoincidentVertices_CompletesWithBoundedWork()
        {
            const int vertexCount = 2000;
            var ao = new float[vertexCount];
            var positions = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                ao[i] = i % 2;

            var result = VertexAOBaker.BlurAO(
                ao, new int[0], vertexCount, 1, 1f, positions);

            Assert.AreEqual(vertexCount, result.Length);
            Assert.AreNotEqual(ao[0], result[0],
                "Seam blur should remain functional while bounding dense-mesh work.");
        }
    }
}
