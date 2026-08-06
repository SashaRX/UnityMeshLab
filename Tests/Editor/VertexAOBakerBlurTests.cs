// VertexAOBakerBlurTests.cs — regression coverage for optional mesh attributes.

using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class VertexAOBakerBlurTests
    {
        [Test]
        public void BlurAO_EmptyNormalsWithDuplicatePositions_DoesNotThrow()
        {
            var ao = new[] { 0f, 1f, 0.5f };
            var positions = new[]
            {
                Vector3.zero,
                Vector3.zero,
                Vector3.right,
            };

            float[] result = null;
            Assert.DoesNotThrow(() => result = VertexAOBaker.BlurAO(
                ao, new int[0], ao.Length, 1, 1f,
                positions, new Vector3[0], null,
                false, true));

            Assert.AreEqual(0.5f, result[0]);
            Assert.AreEqual(0.5f, result[1]);
            Assert.AreEqual(0.5f, result[2]);
        }
    }
}
