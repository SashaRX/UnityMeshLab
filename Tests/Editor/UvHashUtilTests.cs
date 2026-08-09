// UvHashUtilTests.cs — regression coverage for the palette-index folding in
// UvHashUtil.NonNegativeColorKey. Mathf.Abs(int.MinValue) overflows back to
// int.MinValue, so without the explicit fold a shell whose hash landed on that
// one value would index the palette with a negative modulo and throw.

using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvHashUtilTests
    {
        // UvHashUtil is internal (a cross-tool helper), so it is reached through
        // the editor assembly rather than a typeof() — same approach as
        // CleanupToolTests/BenchmarkSweepTests.
        static int NonNegativeColorKey(int colorKey)
        {
            var type = typeof(UvShellExtractor).Assembly.GetType(
                "SashaRX.UnityMeshLab.UvHashUtil");
            Assert.IsNotNull(type, "UvHashUtil not found in the editor assembly");
            var method = type.GetMethod(
                "NonNegativeColorKey", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "UvHashUtil.NonNegativeColorKey not found");
            return (int)method.Invoke(null, new object[] { colorKey });
        }

        // The whole reason the method exists: Mathf.Abs(int.MinValue) == int.MinValue.
        [Test]
        public void NonNegativeColorKey_IntMinValue_FoldsToIntMaxValue()
        {
            Assert.AreEqual(int.MaxValue, NonNegativeColorKey(int.MinValue),
                "int.MinValue must be folded to int.MaxValue, not passed through Mathf.Abs");
        }

        [TestCase(-1, 1)]
        [TestCase(-5, 5)]
        [TestCase(-1234, 1234)]
        [TestCase(int.MinValue + 1, int.MaxValue)]
        public void NonNegativeColorKey_NegativeInput_ReturnsAbsoluteValue(int input, int expected)
        {
            Assert.AreEqual(expected, NonNegativeColorKey(input));
        }

        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(4242, 4242)]
        [TestCase(int.MaxValue, int.MaxValue)]
        public void NonNegativeColorKey_NonNegativeInput_PassesThroughUnchanged(int input, int expected)
        {
            Assert.AreEqual(expected, NonNegativeColorKey(input));
        }

        // Call sites use the result as `key % palette.Length`, so a negative
        // return would index outside the palette. Guard the contract directly.
        [Test]
        public void NonNegativeColorKey_SpreadOfInputs_IsNeverNegative()
        {
            int[] inputs =
            {
                int.MinValue,
                int.MinValue + 1,
                -123456789,
                -2,
                -1,
                0,
                1,
                2,
                123456789,
                int.MaxValue,
            };

            const int paletteLength = 12;
            foreach (int input in inputs)
            {
                int key = NonNegativeColorKey(input);
                Assert.GreaterOrEqual(key, 0,
                    $"NonNegativeColorKey({input}) must not be negative");
                Assert.GreaterOrEqual(key % paletteLength, 0,
                    $"NonNegativeColorKey({input}) must yield a valid palette index");
            }
        }
    }
}
