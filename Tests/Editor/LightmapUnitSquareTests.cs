using System;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class LightmapUnitSquareTests
    {
        [Test]
        public void TheClampMeasuresHowFarTheLightmapLayOutside()
        {
            var uv2 = new[] {new Vector2(.5f, .5f), new Vector2(-.02f, .3f), new Vector2(1.05f, 1.01f), new Vector2(0f, 1f)};
            int clamped = XatlasRepack.ClampUvsToUnit(uv2, out float overshoot);
            Assert.AreEqual(2, clamped, "a vertex counts once, whichever axes leave the square");
            Assert.AreEqual(.05f, overshoot, 1e-6f, "the farthest axis of the farthest vertex");
            CollectionAssert.AreEqual(new[] {new Vector2(.5f, .5f), new Vector2(0f, .3f), new Vector2(1f, 1f), new Vector2(0f, 1f)}, uv2);
        }

        [TestCase(.001f, 256u, false)]    // a quarter texel: rounding
        [TestCase(.0019f, 256u, false)]
        [TestCase(.0021f, 256u, true)]    // past half a texel: faces are squashed
        [TestCase(.0021f, 128u, false)]
        [TestCase(.01f, 32u, false)]
        [TestCase(.01f, 1024u, true)]
        public void TheClampSquashesPastHalfATexel(float overshoot, uint side, bool squashes)
            => Assert.AreEqual(squashes, XatlasRepack.ClampSquashes(overshoot, side));

        [Test]
        public void TransferRefusesAnOwnLightmapOutsideTheUnitSquare()
        {
            var outside = new[] {new Vector2(0f, 0f), new Vector2(1.1f, .2f), new Vector2(.5f, -.3f)};
            var refusal = Assert.Throws<InvalidOperationException>(() => UvTransferWorkflow.RequireOwnUv2InsideUnit(outside, "'Seat' LOD0", 256));
            StringAssert.Contains("'Seat' LOD0", refusal.Message);
            StringAssert.Contains($"U {0f:F3}..{1.1f:F3}, V {-.3f:F3}..{.2f:F3}; 2 vertices, up to {76.8f:F1} px at 256 px", refusal.Message);
            StringAssert.Contains("Turn Repack on", refusal.Message);
            CollectionAssert.AreEqual(new[] {new Vector2(0f, 0f), new Vector2(1.1f, .2f), new Vector2(.5f, -.3f)}, outside, "the check reads only");
        }

        [Test]
        public void TransferTakesAnOwnLightmapThatOnlyRoundsPastTheEdge()
        {
            Assert.DoesNotThrow(() => UvTransferWorkflow.RequireOwnUv2InsideUnit(new[] {Vector2.zero, Vector2.one, new Vector2(1.0001f, -.0001f)}, "a", 256));
            Assert.DoesNotThrow(() => UvTransferWorkflow.RequireOwnUv2InsideUnit(Array.Empty<Vector2>(), "b", 256));
        }
    }
}
