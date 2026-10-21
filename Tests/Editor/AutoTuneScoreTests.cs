using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class AutoTuneScoreTests
    {
        static TransferUvQuality Quality(int degenerate = 0, int invalid = 0, int pairs = 0, double area = 0, bool complete = true,
            double worst = 1, int outOfBounds = 0)
            => new TransferUvQuality { degenerateFaces = degenerate, invalidFaces = invalid, overlapPairs = pairs,
                overlapPairArea = area, overlapScanComplete = complete, worstAnisotropy = worst, outOfBoundsVertices = outOfBounds };

        static AutoTuneScore Score(TransferUvQuality quality, int rejected = 0, int force3D = 0, float coverage = 1,
            int failed = 0, int geometryDegenerate = 0)
            => AutoTuneScore.From(new[] { (quality, geometryDegenerate) }, failed, rejected, force3D, coverage);

        /// <summary>The previous counter preferred an attempt with collapsed faces whenever it had fewer force3D overlaps.</summary>
        [Test]
        public void CollapsedFacesOutrankForce3DOverlapDetections()
        {
            var clean = Score(Quality(), force3D: 3);
            var collapsed = Score(Quality(degenerate: 2), force3D: 0);
            Assert.IsTrue(clean.IsBetterThan(collapsed));
            Assert.IsFalse(collapsed.IsBetterThan(clean));
        }

        /// <summary>Overlap area decides; a subdivided attempt with more but smaller pairs can win.</summary>
        [Test]
        public void OverlapAreaRanksAttemptsInsteadOfPairCount()
        {
            var fewLarge = Score(Quality(pairs: 2, area: 1e-3));
            var manySmall = Score(Quality(pairs: 9, area: 1e-5));
            Assert.IsTrue(manySmall.IsBetterThan(fewLarge));
        }

        /// <summary>Equal attempts and float noise never replace the current best.</summary>
        [Test]
        public void EqualOrNoiseLevelScoresAreNotImprovements()
        {
            var a = Score(Quality(pairs: 3, area: 2.0e-4));
            var b = Score(Quality(pairs: 3, area: 2.0e-4 * (1 - 1e-9)));
            Assert.IsFalse(a.IsBetterThan(a));
            Assert.IsFalse(b.IsBetterThan(a));
            Assert.IsFalse(a.IsBetterThan(b));
        }

        /// <summary>Failures and unfinished scans rank below any measured defect count.</summary>
        [Test]
        public void FailedTargetsAndIncompleteScansRankWorst()
        {
            var defective = Score(Quality(degenerate: 50, pairs: 40, area: .1));
            Assert.IsTrue(defective.IsBetterThan(Score(Quality(), failed: 1)));
            Assert.IsTrue(defective.IsBetterThan(Score(Quality(complete: false))));
            Assert.IsTrue(defective.IsBetterThan(AutoTuneScore.Worst));
            Assert.IsFalse(AutoTuneScore.Worst.IsBetterThan(defective));
        }

        /// <summary>A perfect attempt has no measured defect, not merely no rejected or force3D shells.</summary>
        [Test]
        public void PerfectRequiresNoMeasuredDefect()
        {
            Assert.IsTrue(Score(Quality()).Perfect);
            Assert.IsFalse(Score(Quality(degenerate: 1)).Perfect);
            Assert.IsFalse(Score(Quality(invalid: 1)).Perfect);
            Assert.IsFalse(Score(Quality(pairs: 1, area: 1e-9)).Perfect);
            Assert.IsFalse(Score(Quality(complete: false)).Perfect);
            Assert.IsFalse(Score(Quality(), coverage: .98f).Perfect);
        }

        /// <summary>A stretched face or a vertex outside [0,1] is still a defect: the search must not stop there.</summary>
        [Test]
        public void PerfectRequiresNoStretchedFaceOrOutOfBoundsVertex()
        {
            Assert.IsTrue(Score(Quality(worst: AutoTuneScore.PerfectAnisotropy)).Perfect);
            Assert.IsFalse(Score(Quality(worst: 50)).Perfect);
            Assert.IsFalse(Score(Quality(outOfBounds: 1)).Perfect);
            var targets = new[] { (Quality(worst: 2), 0), (Quality(worst: 9), 0) };
            Assert.AreEqual(9d, AutoTuneScore.From(targets, 0, 0, 0, 1).worstAnisotropy, "The worst target counts.");
        }

        /// <summary>Bounds and stretch only break ties after every earlier criterion, coverage included.</summary>
        [Test]
        public void StretchAndBoundsBreakTiesAfterTheEarlierCriteria()
        {
            Assert.IsTrue(Score(Quality(worst: 50)).IsBetterThan(Score(Quality(degenerate: 1))),
                "A stretched attempt still beats one with a collapsed face.");
            Assert.IsTrue(Score(Quality(worst: 50), coverage: 1).IsBetterThan(Score(Quality(), coverage: .9f)));
            Assert.IsTrue(Score(Quality(worst: 50)).IsBetterThan(Score(Quality(outOfBounds: 1))));
            Assert.IsTrue(Score(Quality(worst: 5)).IsBetterThan(Score(Quality(worst: 50))));
            Assert.IsFalse(Score(Quality(worst: 49.9)).IsBetterThan(Score(Quality(worst: 50))),
                "Within 1% the worst face is the same defect.");
            Assert.IsTrue(Score(Quality(worst: 50)).IsBetterThan(AutoTuneScore.Worst));
        }

        /// <summary>Zero-area 3D faces are present in every attempt; they do not count as collapsed UVs.</summary>
        [Test]
        public void GeometryDegenerateFacesDoNotBlockAPerfectResult()
        {
            Assert.IsTrue(Score(Quality(degenerate: 3), geometryDegenerate: 3).Perfect);
            Assert.AreEqual(1, Score(Quality(degenerate: 4), geometryDegenerate: 3).collapsedFaces);
        }

        /// <summary>The geometry count uses Measure's own predicate: a collinear face counts, a UV collapse does not.</summary>
        [Test]
        public void GeometryDegenerateCountMatchesTheQualityMeasure()
        {
            var mesh = new UnityEngine.Mesh();
            try
            {
                mesh.vertices = new[] { UnityEngine.Vector3.zero, UnityEngine.Vector3.right, UnityEngine.Vector3.up,
                    UnityEngine.Vector3.right * 2 };
                mesh.triangles = new[] { 0, 1, 2, 0, 1, 3 };
                var uv = new[] { UnityEngine.Vector2.zero, UnityEngine.Vector2.zero, UnityEngine.Vector2.zero, UnityEngine.Vector2.one };
                var quality = TransferUvQuality.Measure(mesh, uv, UnityEngine.Vector2.one, UnityEngine.Matrix4x4.identity);
                Assert.AreEqual(2, quality.degenerateFaces, "One UV collapse and one collinear 3D face.");
                Assert.AreEqual(1, AutoTuneScore.GeometryDegenerateFaces(mesh, UnityEngine.Matrix4x4.identity));
                Assert.AreEqual(1, AutoTuneScore.From(new[] { (quality, 1) }, 0, 0, 0, 1).collapsedFaces);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        /// <summary>Coverage remains the final tie-break, as before.</summary>
        [Test]
        public void CoverageBreaksOtherwiseEqualScores()
        {
            Assert.IsTrue(Score(Quality(), coverage: .995f).IsBetterThan(Score(Quality(), coverage: .99f)));
        }
    }
}
