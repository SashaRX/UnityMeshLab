// RemeshGuardTests.cs — the guards the review added to the remesh libraries: the
// overlap scan finds every pair at any atlas size, the BVH depth fits the GPU
// traversal stack, and the final-normal pass names its missing inputs.
using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class RemeshGuardTests
    {
        // A random atlas: small triangles, some deliberately stacked so overlaps exist.
        static RemeshNative.Geometry RandomAtlas(int faces, int seed, float size = .02f)
        {
            var random = new System.Random(seed);
            var uv = new Vector2[faces * 3]; var indices = new int[faces * 3]; var charts = new int[faces * 3];
            for (int f = 0; f < faces; ++f)
            {
                var origin = new Vector2((float)random.NextDouble() * (1 - size), (float)random.NextDouble() * (1 - size));
                float scale = size * (.3f + .7f * (float)random.NextDouble());
                for (int k = 0; k < 3; ++k)
                {
                    int i = f * 3 + k;
                    indices[i] = i; charts[i] = f % 7;
                    uv[i] = origin + new Vector2((float)random.NextDouble(), (float)random.NextDouble()) * scale;
                }
            }
            return new RemeshNative.Geometry { uv = uv, indices = indices, charts = charts, chartCount = 7,
                positions = new Vector3[faces * 3], normals = new Vector3[faces * 3], tangents = new Vector4[faces * 3] };
        }

        // Non-overlapping triangles that tile the atlas, touching along their edges.
        static RemeshNative.Geometry TiledAtlas(int perSide)
        {
            int faces = perSide * perSide * 2;
            var uv = new Vector2[faces * 3]; var indices = new int[faces * 3]; var charts = new int[faces * 3];
            float step = 1f / perSide; int i = 0;
            for (int y = 0; y < perSide; ++y)
                for (int x = 0; x < perSide; ++x)
                {
                    // Corners from the grid indices, so neighbours share them bit-exactly.
                    var a = new Vector2(x * step, y * step); var b = new Vector2((x + 1) * step, y * step);
                    var c = new Vector2(x * step, (y + 1) * step); var d = new Vector2((x + 1) * step, (y + 1) * step);
                    foreach (var corner in new[] { a, b, d, a, d, c }) { uv[i] = corner; indices[i] = i; charts[i] = y; ++i; }
                }
            return new RemeshNative.Geometry { uv = uv, indices = indices, charts = charts, chartCount = perSide,
                positions = new Vector3[faces * 3], normals = new Vector3[faces * 3], tangents = new Vector4[faces * 3] };
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void OverlapScanFindsExactlyTheBruteForcePairs(int seed)
        {
            var atlas = RandomAtlas(700, seed);
            var report = UvAtlasDiagnostics.Measure(atlas, CancellationToken.None, collectConflicts: true);
            Assert.That(report.complete, Is.True);

            var brute = new List<(int a, int b)>();
            var test = new UvAtlasDiagnostics.IntersectionTest();
            int faces = atlas.indices.Length / 3;
            for (int a = 0; a < faces; ++a)
                for (int b = a + 1; b < faces; ++b)
                    if (test.Overlaps(atlas, a, b)) brute.Add((a, b));

            Assert.That(brute.Count, Is.GreaterThan(0), "the fixture must contain overlaps for the comparison to mean anything");
            Assert.That(report.pairs, Is.EqualTo(brute.Count));
            CollectionAssert.AreEqual(brute, report.conflicts, "every pair once, in face order");
        }

        [Test]
        public void OverlapScanCompletesOnALargeTiledAtlas()
        {
            var atlas = TiledAtlas(160); // 51,200 faces; the former x-sweep ran out of budget far below this
            var report = UvAtlasDiagnostics.Measure(atlas, CancellationToken.None);
            Assert.That(report.complete, Is.True, "a packed atlas must be certified whatever its face count");
            Assert.That(report.pairs, Is.EqualTo(0), "edge-sharing neighbours are not overlaps");
            Assert.That(report.comparisons, Is.LessThan(64L * atlas.indices.Length / 3), "a few comparisons per face, not N·√N");
        }

        [Test]
        public void OverlapScanHonoursItsBudget()
        {
            var atlas = RandomAtlas(400, 11);
            var report = UvAtlasDiagnostics.Measure(atlas, CancellationToken.None, comparisonBudget: 10);
            Assert.That(report.complete, Is.False);
            Assert.That(report.comparisons, Is.EqualTo(11), "the scan stops on the first comparison past the budget");
        }

        [Test]
        public void BvhDepthFitsTheGpuTraversalStack()
        {
            var one = new TriangleBvh(new[] { Vector3.zero, Vector3.right, Vector3.up }, new[] { 0, 1, 2 });
            Assert.That(one.Depth, Is.EqualTo(0));

            var random = new System.Random(5);
            int faces = 60000;
            var positions = new Vector3[faces * 3]; var indices = new int[faces * 3];
            for (int i = 0; i < positions.Length; ++i)
            {
                var centre = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * 10f;
                positions[i] = centre + new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * .05f;
                indices[i] = i;
            }
            var soup = new TriangleBvh(positions, indices);
            Assert.That(soup.Depth + 2, Is.LessThanOrEqualTo(GpuBvh.TraversalStack),
                "a binned-SAH tree over a large soup stays within the fixed GPU stack");
        }

        [Test]
        public void FinalNormalsNameTheirMissingInputs()
        {
            var geometry = new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up }, indices = new[] { 0, 1, 2 },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, charts = new[] { 0, 0, 0 }, chartCount = 1,
                normals = null, tangents = null };
            var error = Assert.Throws<InvalidOperationException>(() => RemeshNormals.ApplyFinal(geometry, new RemeshSettings(), CancellationToken.None));
            Assert.That(error.Message, Does.Contain("normals"));
        }
    }
}
