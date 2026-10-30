using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class SharedLightmapFallbackTests
    {
        Mesh source, target, output;

        [SetUp]
        public void Setup()
        {
            var quad = new[] {Vector3.zero, Vector3.right, new Vector3(1, 1), Vector3.up};
            var uv = new[] {Vector2.zero, Vector2.right, Vector2.one, Vector2.up};
            var indices = new[] {0, 1, 2, 0, 2, 3};
            source = new Mesh {
                vertices = quad.Concat(quad.Select(p => p + Vector3.right * 3)).ToArray(),
                triangles = indices.Concat(indices.Select(i => i + 4)).ToArray(),
                uv = uv.Concat(uv.Select(p => p + Vector2.right * 3)).ToArray(),
                uv2 = uv.Select(p => p * .4f + Vector2.one * .05f)
                    .Concat(uv.Select(p => Vector2.Scale(p, new Vector2(.4f, .2f)) + new Vector2(.55f, .05f))).ToArray()
            };
            source.RecalculateNormals();
            target = Object.Instantiate(source);
        }

        [TearDown]
        public void Cleanup()
        {
            if (output) Object.DestroyImmediate(output);
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(target);
        }

        // The second quad left at (0,0), as a rejected shell is.
        Vector2[] RejectedSecondQuad() => source.uv2.Take(4).Concat(Enumerable.Repeat(Vector2.zero, 4)).ToArray();

        static int[] Sources() => new[] {0, 0, 0, 0, -1, -1, -1, -1};

        /// <summary>The rejected quad takes the UV2 of the LOD0 quad under it; the healthy one keeps its own.</summary>
        [Test]
        public void ARejectedShellTakesTheUv2OfTheSurfaceUnderIt()
        {
            var legacy = RejectedSecondQuad();
            var report = new SharedLightmapFallback.Report();
            var result = SharedLightmapFallback.Apply(source, target, legacy, Sources(), report);
            output = result.mesh;
            Assert.NotNull(output);
            Assert.AreEqual((1, 2, 2, 0, 0), (report.shells, report.faces, report.placed, report.withoutSurface, report.atPoint));
            CollectionAssert.AreEqual(target.triangles.Select(i => target.vertices[i]), output.triangles.Select(i => output.vertices[i]),
                "The faces are the target's, corner for corner.");
            for (int corner = 0; corner < 6; ++corner)
                Assert.AreEqual(legacy[target.triangles[corner]], output.uv2[output.triangles[corner]], "A healthy face keeps its UVs.");
            for (int corner = 6; corner < 12; ++corner)
                Assert.That(Vector2.Distance(source.uv2[source.triangles[corner]], output.uv2[output.triangles[corner]]), Is.LessThan(1e-5));
            Assert.IsTrue(result.sources.All(s => s >= 0), "Every vertex records the source shell its UV2 came from.");
        }

        /// <summary>
        /// Under a shared lightmap a face lying on the same LOD0 surface as another reads the
        /// same light: the collapsed copy of a quad lands on it, which the rescue refuses.
        /// </summary>
        [Test]
        public void ACollapsedCopyMayOverlapTheFaceOnTheSameSurface()
        {
            var positions = target.vertices;
            for (int i = 4; i < 8; ++i) positions[i] = positions[i - 4];
            target.vertices = positions;
            var report = new SharedLightmapFallback.Report();
            output = SharedLightmapFallback.Apply(source, target, RejectedSecondQuad(), Sources(), report).mesh;
            Assert.NotNull(output);
            Assert.AreEqual(2, report.placed);
            for (int corner = 6; corner < 12; ++corner)
                Assert.That(Vector2.Distance(output.uv2[output.triangles[corner - 6]], output.uv2[output.triangles[corner]]), Is.LessThan(1e-5));
        }

        /// <summary>A face whose LOD0 surface faces the other way keeps its layout.</summary>
        [Test]
        public void AFaceWithoutAFacingSurfaceIsLeftAlone()
        {
            var triangles = target.triangles;
            for (int t = 6; t < 12; t += 3) (triangles[t + 1], triangles[t + 2]) = (triangles[t + 2], triangles[t + 1]);
            target.triangles = triangles;
            var report = new SharedLightmapFallback.Report();
            output = SharedLightmapFallback.Apply(source, target, RejectedSecondQuad(), Sources(), report).mesh;
            Assert.IsNull(output);
            Assert.AreEqual((2, 0, 2), (report.faces, report.placed, report.withoutSurface));
        }

        /// <summary>
        /// A face far past its chart presses every corner onto one point of the chart's border; it
        /// takes the one point under its centre, still on its own surface, never an extension that
        /// could reach into another chart.
        /// </summary>
        [Test]
        public void AFaceFarPastItsChartTakesThePointUnderItsCentre()
        {
            Object.DestroyImmediate(target);
            target = new Mesh { vertices = new[] {new Vector3(5, 2), new Vector3(6, 2), new Vector3(5, 3)}, triangles = new[] {0, 1, 2} };
            var report = new SharedLightmapFallback.Report();
            output = SharedLightmapFallback.Apply(source, target, new Vector2[3], new[] {-1, -1, -1}, report).mesh;
            Assert.NotNull(output);
            Assert.AreEqual((1, 1), (report.placed, report.atPoint));
            var corner = source.uv2[6];
            foreach (int i in output.triangles) Assert.That(Vector2.Distance(corner, output.uv2[i]), Is.LessThan(1e-5),
                "Every corner reads the LOD0 corner nearest the face, inside its own chart.");
        }

        /// <summary>
        /// On a concave chart a face with every corner on the chart can still span the gap where
        /// another chart is packed; it takes the point under its centre instead.
        /// </summary>
        [Test]
        public void AFaceAcrossAConcaveChartsGapTakesThePointUnderItsCentre()
        {
            Object.DestroyImmediate(source);
            // An L of three unit quads, (0..2)x(0..1) and (0..1)x(1..2), its UV2 the same shape scaled by .25.
            var l = new[] {Vector3.zero, Vector3.right, new Vector3(2, 0), new Vector3(0, 1), new Vector3(1, 1), new Vector3(2, 1),
                new Vector3(0, 2), new Vector3(1, 2)};
            source = new Mesh { vertices = l, triangles = new[] {0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4, 3, 4, 7, 3, 7, 6},
                uv = l.Select(p => (Vector2)p).ToArray(), uv2 = l.Select(p => (Vector2)p * .25f).ToArray() };
            Object.DestroyImmediate(target);
            target = new Mesh { vertices = new[] {new Vector3(.5f, .5f), new Vector3(1.9f, .5f), new Vector3(.5f, 1.9f)}, triangles = new[] {0, 1, 2} };
            var report = new SharedLightmapFallback.Report();
            output = SharedLightmapFallback.Apply(source, target, new Vector2[3], new[] {-1, -1, -1}, report).mesh;
            Assert.NotNull(output);
            Assert.AreEqual((1, 1), (report.placed, report.atPoint), "Its hypotenuse crosses the L's missing corner.");
            var centre = new Vector2(2.9f / 3, 2.9f / 3) * .25f;
            foreach (int i in output.triangles) Assert.That(Vector2.Distance(centre, output.uv2[i]), Is.LessThan(1e-4));
        }

        /// <summary>
        /// A notch narrower than any fixed spacing of sample points still refuses the face: a
        /// border edge of the chart enters its triangle.
        /// </summary>
        [Test]
        public void AFaceOverANarrowNotchTakesThePointUnderItsCentre()
        {
            Object.DestroyImmediate(source);
            // A unit square chart in z = 0, its UV2 the same, with the cell (.12..18) x (.6..1) cut out.
            float[] xs = {0, .12f, .18f, 1}, ys = {0, .6f, 1};
            var grid = ys.SelectMany(y => xs.Select(x => new Vector3(x, y))).ToArray();
            var cells = new List<int>();
            for (int j = 0; j < 2; ++j)
                for (int i = 0; i < 3; ++i)
                {
                    if (i == 1 && j == 1) continue;
                    int a = j * 4 + i, b = a + 1, c = a + 5, d = a + 4;
                    cells.AddRange(new[] {a, b, c, a, c, d});
                }
            source = new Mesh { vertices = grid, triangles = cells.ToArray(),
                uv = grid.Select(p => (Vector2)p).ToArray(), uv2 = grid.Select(p => (Vector2)p).ToArray() };
            Object.DestroyImmediate(target);
            target = new Mesh { vertices = new[] {new Vector3(.02f, .02f), new Vector3(.9f, .02f), new Vector3(.02f, .9f)}, triangles = new[] {0, 1, 2} };
            var report = new SharedLightmapFallback.Report();
            output = SharedLightmapFallback.Apply(source, target, new Vector2[3], new[] {-1, -1, -1}, report).mesh;
            Assert.NotNull(output);
            Assert.AreEqual((1, 1), (report.placed, report.atPoint), "The notch's bottom edge lies inside the face.");
            var centre = Vector2.one * (.94f / 3);
            foreach (int i in output.triangles) Assert.That(Vector2.Distance(centre, output.uv2[i]), Is.LessThan(1e-4));
        }

        /// <summary>
        /// The log counts collapsed faces as the fallback finds them. A shell thinner than the
        /// extent still has UV2 area, so a measure of faces without it counted none before the
        /// fallback and the faces left at one point after it: "collapsed faces 0 → 2".
        /// </summary>
        [Test]
        public void TheCollapsedCountFindsAThinShellTheAreaMeasureMisses()
        {
            var thin = source.uv2.Take(4).Concat(new[] {Vector2.zero, Vector2.right * .1f, new Vector2(.1f, 1e-6f), new Vector2(0, 1e-6f)}).ToArray();
            Assert.AreEqual(0, TransferUvQuality.Measure(target, thin, Vector2.one, Matrix4x4.identity).degenerateFaces);
            Assert.AreEqual(2, SharedLightmapFallback.CollapsedFaceCount(target, thin));
            var report = new SharedLightmapFallback.Report();
            output = SharedLightmapFallback.Apply(source, target, thin, Sources(), report).mesh;
            Assert.NotNull(output);
            Assert.AreEqual((2, 2, 0), (report.faces, report.placed, report.atPoint));
            Assert.AreEqual(0, SharedLightmapFallback.CollapsedFaceCount(output, output.uv2));
        }

        /// <summary>
        /// A face without UV2 area in a shell that is not collapsed (a face placed at one point
        /// beside a healthy one it shares a vertex with) reads one texel and counts too.
        /// </summary>
        [Test]
        public void TheCollapsedCountTakesAFaceWithoutUv2AreaInAHealthyShell()
        {
            var uv2 = source.uv2.ToArray();
            uv2[3] = uv2[0];
            CollectionAssert.IsEmpty(SharedLightmapFallback.CollapsedShellFaces(target.triangles, target.vertices, uv2,
                new SharedLightmapFallback.Report()), "The first quad's other face keeps its shell wide.");
            Assert.AreEqual(1, SharedLightmapFallback.CollapsedFaceCount(target, uv2));
        }

        /// <summary>A layout without a collapsed shell, or a collapsed shell without 3D area, is left alone.</summary>
        [Test]
        public void OnlyShellsWithAreaCollapsedInUv2AreReplaced()
        {
            var report = new SharedLightmapFallback.Report();
            Assert.IsNull(SharedLightmapFallback.Apply(source, target, source.uv2, Sources(), report).mesh);
            Assert.AreEqual(0, report.faces);

            var line = source.uv2.Take(4).Concat(new[] {Vector2.zero, Vector2.right * .1f, Vector2.right * .1f, Vector2.zero}).ToArray();
            CollectionAssert.AreEqual(new[] {2, 3}, SharedLightmapFallback.CollapsedShellFaces(target.triangles, target.vertices, line,
                new SharedLightmapFallback.Report()), "A shell collapsed to a line reads one row of texels.");
            var diagonal = source.uv2.Take(4).Concat(new[] {Vector2.zero, Vector2.one * .05f, Vector2.one * .1f, Vector2.one * .05f}).ToArray();
            CollectionAssert.AreEqual(new[] {2, 3}, SharedLightmapFallback.CollapsedShellFaces(target.triangles, target.vertices, diagonal,
                new SharedLightmapFallback.Report()), "So does a line at any angle.");
            var flat = target.vertices;
            for (int i = 4; i < 8; ++i) flat[i] = flat[4];
            CollectionAssert.IsEmpty(SharedLightmapFallback.CollapsedShellFaces(target.triangles, flat, RejectedSecondQuad(),
                new SharedLightmapFallback.Report()), "A shell without 3D area has nothing to light.");
        }
    }
}
