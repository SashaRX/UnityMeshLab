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

            // The two coincident vertices are seam-connected (missing normals are
            // treated as "unavailable", not as a zero vector), so their AO swaps.
            // The isolated vertex has no neighbors and keeps its value.
            Assert.That(result[0], Is.EqualTo(1f).Within(0.0001f));
            Assert.That(result[1], Is.EqualTo(0f).Within(0.0001f));
            Assert.That(result[2], Is.EqualTo(0.5f).Within(0.0001f));
        }
    }
}
