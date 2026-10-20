using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvPointJunctionRecoveryTests
    {
        /// <summary>Two isometric patches can overlap despite passing the stretch-only early exit.</summary>
        [TestCase(false)] [TestCase(true)]
        public void RepackSeparatesPointBranchesWithoutStretchingTheMain(bool mirrored)
        {
            var positions = Positions();
            int[] indices = { 0, 1, 2, 0, 2, 3, 0, 4, 5 };
            var uv = Coordinates(mirrored);
            var shell = UvShellExtractor.Extract(uv, indices).Single();
            var before = Measure(shell, indices, uv);
            Assert.Greater(before.pairs, 0);
            var flat = uv.SelectMany(v => new[] { v.x, v.y }).ToArray();
            XatlasRepack.ReparameterizeStretchedShells(RepackOptions.Default, new List<UvShell> { shell }, indices, positions, flat);
            var result = Enumerable.Range(0, uv.Length).Select(i => new Vector2(flat[i * 2], flat[i * 2 + 1])).ToArray();
            CollectionAssert.AreEqual(uv.Take(4).ToArray(), result.Take(4).ToArray());
            var after = Measure(shell, indices, result);
            Assert.IsTrue(after.complete);
            Assert.AreEqual(0, after.pairs);
            Assert.AreEqual(0, after.degenerateFaces + after.invalidFaces);
            for (int f = 0; f < indices.Length; f += 3)
            {
                int a = indices[f], b = indices[f + 1], c = indices[f + 2];
                Assert.That(ReverseUvTransfer.TriangleAnisotropy(positions[a], positions[b], positions[c],
                    result[a], result[b], result[c]), Is.EqualTo(1).Within(1e-6));
                float oldArea = Cross(uv[a], uv[b], uv[c]);
                float newArea = Cross(result[a], result[b], result[c]);
                Assert.That(newArea, Is.EqualTo(oldArea).Within(1e-6));
            }
        }

        /// <summary>Clean charts and refused or cancelled attempts leave caller-owned UVs intact.</summary>
        [TestCase("clean")] [TestCase("budget")] [TestCase("search-budget")]
        [TestCase("cancel")] [TestCase("nonmanifold")] [TestCase("invalid")]
        public void RefusalIsAtomic(string mode)
        {
            var positions = Positions();
            int[] indices = mode == "nonmanifold" ? new[] { 0, 1, 2, 0, 2, 3, 0, 2, 4, 0, 4, 5 }
                : new[] { 0, 1, 2, 0, 2, 3, 0, 4, 5 };
            var uv = Coordinates(false);
            if (mode == "clean") { uv[4] *= -1; uv[5] *= -1; }
            if (mode == "invalid") uv[4] = new Vector2(float.NaN, 0);
            var shell = UvShellExtractor.Extract(uv, indices).Single();
            var original = Enumerable.Range(0, uv.Length).ToDictionary(v => v, v => uv[v]);
            using var cancellation = new CancellationTokenSource();
            if (mode == "cancel") cancellation.Cancel();
            if (mode == "cancel") Assert.Throws<OperationCanceledException>(() =>
                UvPointJunctionRecovery.TryRecover(shell, indices, positions, original, cancellation.Token));
            else
            {
                long budget = mode == "budget" ? 0 : 200000;
                if (mode == "search-budget") budget = Measure(shell, indices, uv).comparisons;
                Assert.IsNull(UvPointJunctionRecovery.TryRecover(shell, indices, positions, original, cancellation.Token, budget));
            }
            for (int i = 0; i < uv.Length; ++i)
            {
                Assert.AreEqual(BitConverter.SingleToInt32Bits(uv[i].x), BitConverter.SingleToInt32Bits(original[i].x));
                Assert.AreEqual(BitConverter.SingleToInt32Bits(uv[i].y), BitConverter.SingleToInt32Bits(original[i].y));
            }
        }

        /// <summary>A second anchor duplicated across vertex streams must prevent a branch rotation.</summary>
        [Test]
        public void SharedPointAliasesRemainAnchored()
        {
            var positions = Positions(); positions[4] = positions[1];
            var uv = Coordinates(false); uv[4] = uv[1];
            int[] indices = { 0, 1, 2, 0, 2, 3, 0, 4, 5 };
            var shell = UvShellExtractor.Extract(uv, indices).Single();
            var original = Enumerable.Range(0, uv.Length).ToDictionary(v => v, v => uv[v]);
            Assert.Greater(Measure(shell, indices, uv).pairs, 0);
            Assert.IsNull(UvPointJunctionRecovery.TryRecover(shell, indices, positions, original, default));
            CollectionAssert.AreEqual(uv, Enumerable.Range(0, uv.Length).Select(v => original[v]).ToArray());
        }

        /// <summary>Numerical contact slivers stay reported without rotating an otherwise usable branch.</summary>
        [TestCase(1e-8f, false)] [TestCase(.01f, true)]
        public void RotationRequiresContactBeyondCoordinateUncertainty(float penetration, bool recover)
        {
            var positions = Positions();
            var uv = Coordinates(false);
            uv[4] = new Vector2(-.5f, 0); uv[5] = new Vector2(penetration, .5f);
            int[] indices = { 0, 1, 2, 0, 2, 3, 0, 4, 5 };
            var shell = UvShellExtractor.Extract(uv, indices).Single();
            var original = Enumerable.Range(0, uv.Length).ToDictionary(v => v, v => uv[v]);
            Assert.Greater(Measure(shell, indices, uv).pairs, 0);
            var candidate = UvPointJunctionRecovery.TryRecover(shell, indices, positions, original, default);
            Assert.AreEqual(recover, candidate != null);
            CollectionAssert.AreEqual(uv, Enumerable.Range(0, uv.Length).Select(v => original[v]).ToArray());
            if (candidate != null) Assert.AreEqual(0, Measure(shell, indices,
                Enumerable.Range(0, uv.Length).Select(v => candidate[v]).ToArray()).pairs);
        }

        /// <summary>A branch entirely covered by the main patch cannot be repaired by reducing only the number of intersected triangles.</summary>
        [Test]
        public void CoveredBranchMustRemainUnchangedWhenNoRotationCanSeparateIt()
        {
            var uv = new[] { Vector2.zero, new Vector2(1, -1), Vector2.one, new Vector2(-1, 1),
                new Vector2(-1, -1), new Vector2(.5f, 0), new Vector2(0, .5f) };
            var positions = uv.Select(p => new Vector3(p.x, p.y, 0)).ToArray();
            int[] indices = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1, 0, 5, 6 };
            var shell = UvShellExtractor.Extract(uv, indices).Single();
            var original = Enumerable.Range(0, uv.Length).ToDictionary(v => v, v => uv[v]);
            Assert.Greater(Measure(shell, indices, uv).pairs, 0);
            Assert.IsNull(UvPointJunctionRecovery.TryRecover(shell, indices, positions, original, default));
            CollectionAssert.AreEqual(uv, Enumerable.Range(0, uv.Length).Select(v => original[v]).ToArray());
        }

        /// <summary>Builds a square and a smaller triangle sharing only the origin.</summary>
        static Vector3[] Positions() => new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up,
            Vector3.up, Vector3.left * .5f, Vector3.down * .5f };

        /// <summary>Places the small triangle inside the square with unchanged intrinsic scale.</summary>
        static Vector2[] Coordinates(bool mirrored) => new[] { Vector2.zero, Vector2.right, Vector2.one,
            Vector2.up, Vector2.right * .5f, Vector2.up * .5f }.Select(p => mirrored ? new Vector2(-p.x, p.y) : p).ToArray();

        /// <summary>Runs the same complete chart contact audit used by recovery.</summary>
        static UvAtlasDiagnostics.Report Measure(UvShell shell, int[] indices, Vector2[] uv) =>
            UvAtlasDiagnostics.Measure(UvAtlasDiagnostics.ShellGeometry(shell.faceIndices, indices, v => uv[v]), default);

        /// <summary>Signed double area for the exactly representable procedural coordinates.</summary>
        static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }
}
