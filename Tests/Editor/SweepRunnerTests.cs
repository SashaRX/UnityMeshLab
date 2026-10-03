// SweepRunnerTests.cs — the sweep matrix resolved, validated and enumerated, without
// running a pipeline.
using System.Linq;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class SweepRunnerTests
    {
        static UvToolContext Context()
        {
            var ctx = new UvToolContext { AtlasResolution = 1024, ShellPaddingPx = 4, BorderPaddingPx = 1, StretchThreshold = 1.5f, InternalOversample = 2 };
            ctx.ReparameterizeStretchedShells = true;
            ctx.ArapIterations = 25;
            return ctx;
        }

        [Test]
        public void EmptyAxesFallBackToTheContextAndTheHostsSymmetryMode()
        {
            var sm = new TestSuiteAsset.SweepMatrix
            {
                atlasResolutions = new int[0], shellPaddingPxVariants = null, borderPaddingPxVariants = new int[0],
                arapIterationsVariants = new int[0], stretchThresholdVariants = new float[0],
                internalOversampleVariants = new int[0], symSplitThresholdModeVariants = null,
            };
            var axes = SweepRunner.Resolve(sm, Context(), SymmetrySplitShells.ThresholdMode.Adaptive);
            Assert.AreEqual(1, axes.Count);
            var cell = axes.Cells().Single();
            Assert.AreEqual(1024, cell.atlasRes);
            Assert.AreEqual(4, cell.shellPad);
            Assert.AreEqual(1, cell.borderPad);
            Assert.AreEqual(25, cell.arapIterations, "ARAP on: the context's iteration count");
            Assert.AreEqual(1.5f, cell.stretchThreshold);
            Assert.AreEqual(2, cell.oversample);
            Assert.AreEqual(SymmetrySplitShells.ThresholdMode.Adaptive, cell.symMode);
        }

        [Test]
        public void CellsEnumerateResolutionOutermostAndSymmetryInnermost()
        {
            var sm = new TestSuiteAsset.SweepMatrix
            {
                atlasResolutions = new[] { 256, 512 }, shellPaddingPxVariants = new[] { 2 }, borderPaddingPxVariants = new[] { 0 },
                arapIterationsVariants = new[] { 0 }, stretchThresholdVariants = new[] { 1.5f }, internalOversampleVariants = new[] { 1 },
                symSplitThresholdModeVariants = new[] { SymmetrySplitShells.ThresholdMode.LegacyFixed, SymmetrySplitShells.ThresholdMode.Adaptive },
            };
            Assert.IsTrue(SweepRunner.TryValidate(sm, Context(), SymmetrySplitShells.ThresholdMode.LegacyFixed, out int count, out string error), error);
            Assert.AreEqual(4, count);
            var cells = SweepRunner.Resolve(sm, Context(), SymmetrySplitShells.ThresholdMode.LegacyFixed).Cells().ToList();
            Assert.AreEqual(new[] { 256, 256, 512, 512 }, cells.Select(c => c.atlasRes).ToArray());
            Assert.AreEqual(SymmetrySplitShells.ThresholdMode.Adaptive, cells[1].symMode);
            Assert.AreEqual(1, cells[0].oversample, "the validated oversample runs, labels and records as 1");
            Assert.AreEqual("sweep_res256_pad2_bdr0_arap0_stretch1p50_os1_symlegacy", cells[0].Label);
            Assert.AreEqual("sweep_res512_pad2_bdr0_arap0_stretch1p50_os1_symadaptive", cells[3].Label);
            Assert.IsFalse(cells[0].Config.arapEnabled);
            Assert.AreEqual(1, cells[0].Config.internalOversample);
        }

        [Test]
        public void ValidationRejectsOutOfRangeValuesOversizedAxesAndTooManyCells()
        {
            var ctx = Context();
            var sm = new TestSuiteAsset.SweepMatrix { atlasResolutions = new[] { 32 } };
            Assert.IsFalse(SweepRunner.TryValidate(sm, ctx, SymmetrySplitShells.ThresholdMode.LegacyFixed, out _, out string error));
            StringAssert.Contains("atlas resolution 32", error);

            sm = new TestSuiteAsset.SweepMatrix { stretchThresholdVariants = new[] { float.NaN } };
            Assert.IsFalse(SweepRunner.TryValidate(sm, ctx, SymmetrySplitShells.ThresholdMode.LegacyFixed, out _, out error));
            StringAssert.Contains("stretch threshold", error);

            sm = new TestSuiteAsset.SweepMatrix { internalOversampleVariants = new[] { 0 } };
            Assert.IsFalse(SweepRunner.TryValidate(sm, ctx, SymmetrySplitShells.ThresholdMode.LegacyFixed, out _, out error));
            StringAssert.Contains("internal oversample 0", error);

            sm = new TestSuiteAsset.SweepMatrix { shellPaddingPxVariants = Enumerable.Range(0, SweepRunner.MaxValuesPerDimension + 1).ToArray() };
            Assert.IsFalse(SweepRunner.TryValidate(sm, ctx, SymmetrySplitShells.ThresholdMode.LegacyFixed, out _, out error));
            StringAssert.Contains("shell padding has 17 values", error);

            sm = new TestSuiteAsset.SweepMatrix
            {
                atlasResolutions = Enumerable.Range(0, 8).Select(i => 256 + i).ToArray(),
                shellPaddingPxVariants = Enumerable.Range(0, 8).ToArray(),
                borderPaddingPxVariants = Enumerable.Range(0, 8).ToArray(),
            };
            Assert.IsFalse(SweepRunner.TryValidate(sm, ctx, SymmetrySplitShells.ThresholdMode.LegacyFixed, out _, out error));
            StringAssert.Contains($"the maximum is {SweepRunner.MaxCells}", error);

            Assert.IsFalse(SweepRunner.TryValidate(null, ctx, SymmetrySplitShells.ThresholdMode.LegacyFixed, out _, out error));
            StringAssert.Contains("missing", error);
        }

        [TestCase("Chair A", "Chair_A")]
        [TestCase("Chair/A", "Chair_A")]
        [TestCase("ok-name_1", "ok-name_1")]
        [TestCase("", "case")]
        [TestCase(null, "case")]
        public void PathSlugsKeepLettersDigitsDashAndUnderscore(string input, string slug)
            => Assert.AreEqual(slug, SweepRunner.SanitizeForPath(input));
    }
}
