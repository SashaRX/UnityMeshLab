// BakeHealthTests.cs — the bake health report judged from counters alone: the summary
// line, the scale check, the zero-normal / fallback warnings and the tilt rule that
// stays quiet on a proxy or a heavy decimation.
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class BakeHealthTests
    {
        // A healthy bake: 1000 covered texels, nothing fell back, nothing missed, a flat map.
        static RemeshBaker.Maps Healthy() => new RemeshBaker.Maps
        {
            size = 64, covered = 1000, weldedPositions = 120, splitCopies = 8, cageSides = 1,
            maxReachRatio = 1.2f, meanTiltDeg = 8f, maxTiltDeg = 40f, facingFilter = true,
        };

        // A unit cube's corners: bounds diagonal √3, scaled by `scale`.
        static Vector3[] Cube(float scale)
        {
            var pts = new Vector3[8];
            for (int i = 0; i < 8; i++)
                pts[i] = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * scale;
            return pts;
        }

        static float Diag => Mathf.Sqrt(3f);

        [Test]
        public void HealthyBakeHasNoWarningsAndASummaryNamingTheCounters()
        {
            var r = BakeHealth.Build("Wall", Healthy(), Diag, 12000, Cube(1f), 6000, RemeshShape.LOD0);
            Assert.IsEmpty(r.Warnings);
            Assert.AreEqual("Wall", r.node);
            Assert.AreEqual(1f, r.scaleRatio, 1e-4f);
            Assert.AreEqual(Diag, r.targetDiagonal, 1e-4f);
            Assert.IsFalse(r.heavyReduction, "half the faces is a mild decimation");
            StringAssert.Contains("120 welded positions", r.Summary);
            StringAssert.Contains("8 split copies", r.Summary);
            StringAssert.Contains("front-face filter on", r.Summary);
            StringAssert.Contains("ratio 1.00", r.Summary);
        }

        [Test]
        public void ScaleMismatchWarnsOnceWithTheRatio()
        {
            var r = BakeHealth.Build("n", Healthy(), Diag, 100, Cube(1.5f), 100, RemeshShape.LOD0);
            Assert.AreEqual(1.5f, r.scaleRatio, 1e-4f);
            Assert.AreEqual(1, r.Warnings.Count);
            StringAssert.Contains("ratio is 1.50", r.Warnings[0]);

            var ok = BakeHealth.Build("n", Healthy(), Diag, 100, Cube(1.05f), 100, RemeshShape.LOD0);
            Assert.IsEmpty(ok.Warnings, "5% is within the tolerance");
        }

        [Test]
        public void ZeroNormalsWarnAndHideTheFallbackWarning()
        {
            var m = Healthy(); m.zeroNormals = 3; m.rayFallbacks = 999;
            var r = BakeHealth.Build("n", m, Diag, 100, Cube(1f), 100, RemeshShape.LOD0);
            Assert.AreEqual(1, r.Warnings.Count);
            StringAssert.Contains("3 result vertices have zero normals", r.Warnings[0]);
        }

        [Test]
        public void MostSamplesFallingBackWarns()
        {
            var m = Healthy(); m.rayFallbacks = 801;
            var r = BakeHealth.Build("n", m, Diag, 100, Cube(1f), 100, RemeshShape.LOD0);
            Assert.AreEqual(1, r.Warnings.Count);
            StringAssert.Contains("fell back to nearest-point search", r.Warnings[0]);

            var under = Healthy(); under.rayFallbacks = 800;
            Assert.IsEmpty(BakeHealth.Build("n", under, Diag, 100, Cube(1f), 100, RemeshShape.LOD0).Warnings,
                "the rule is strictly more than four fifths");
            var none = Healthy(); none.covered = 0; none.rayFallbacks = 5;
            Assert.IsEmpty(BakeHealth.Build("n", none, Diag, 100, Cube(1f), 100, RemeshShape.LOD0).Warnings,
                "no covered texels: nothing to judge");
        }

        [Test]
        public void LoudTiltWarnsOnAMildDecimationOnly()
        {
            var m = Healthy(); m.loudTexels = 60; m.meanTiltDeg = 35f;
            var mild = BakeHealth.Build("n", m, Diag, 1000, Cube(1f), 500, RemeshShape.LOD0);
            Assert.IsFalse(mild.heavyReduction);
            Assert.AreEqual(1, mild.Warnings.Count);
            StringAssert.Contains("6.0% of texels lean >45°", mild.Warnings[0]);

            var heavy = BakeHealth.Build("n", m, Diag, 1000, Cube(1f), 199, RemeshShape.LOD0);
            Assert.IsTrue(heavy.heavyReduction, "under a fifth of the faces");
            Assert.IsEmpty(heavy.Warnings);

            var proxy = BakeHealth.Build("n", m, Diag, 1000, Cube(1f), 500, RemeshShape.BoundingBox);
            Assert.IsTrue(proxy.heavyReduction, "a proxy shape is a heavy reduction by definition");
            Assert.IsEmpty(proxy.Warnings);

            var quiet = Healthy(); quiet.loudTexels = 60; quiet.meanTiltDeg = 20f;
            Assert.IsEmpty(BakeHealth.Build("n", quiet, Diag, 1000, Cube(1f), 500, RemeshShape.LOD0).Warnings,
                "loud texels with a low mean tilt is fine detail, not saturation");
        }

        [Test]
        public void EmptyTargetGivesAZeroDiagonalAndAScaleWarning()
        {
            var r = BakeHealth.Build("n", Healthy(), Diag, 100, new Vector3[0], 0, RemeshShape.LOD0);
            Assert.AreEqual(0f, r.targetDiagonal);
            Assert.AreEqual(0f, r.scaleRatio);
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("ratio is 0.00")));
        }
    }
}
