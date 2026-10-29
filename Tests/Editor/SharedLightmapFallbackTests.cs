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
            Assert.AreEqual((1, 2, 2, 0, 0), (report.shells, report.faces, report.placed, report.withoutSurface, report.collapsed));
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
        /// The affine extension fixes the plane of a thin LOD0 face in double precision, where
        /// float dot products round its determinant to zero, and refuses a face without a plane.
        /// </summary>
        [Test]
        public void TheAffineExtensionHoldsOnAThinFaceAndRefusesALine()
        {
            Assert.IsTrue(SharedLightmapFallback.Extend(Vector3.zero, Vector3.right, new Vector3(1, .0001f, 0),
                Vector2.zero, Vector2.right, Vector2.one, new Vector3(.5f, .00005f, 0), out var uv));
            Assert.That(Vector2.Distance(new Vector2(.5f, .5f), uv), Is.LessThan(1e-3));
            Assert.IsFalse(SharedLightmapFallback.Extend(Vector3.zero, Vector3.right, Vector3.right * 2,
                Vector2.zero, Vector2.right, Vector2.one, Vector3.one, out _));
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
            var flat = target.vertices;
            for (int i = 4; i < 8; ++i) flat[i] = flat[4];
            CollectionAssert.IsEmpty(SharedLightmapFallback.CollapsedShellFaces(target.triangles, flat, RejectedSecondQuad(),
                new SharedLightmapFallback.Report()), "A shell without 3D area has nothing to light.");
        }
    }
}
