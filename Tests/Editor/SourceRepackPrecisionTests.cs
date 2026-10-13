using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class SourceRepackPrecisionTests
    {
        static Mesh Quad(float uvSize)
        {
            return new Mesh {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up, Vector3.up },
                uv = new[] { Vector2.zero, new Vector2(uvSize, 0), Vector2.one * uvSize, new Vector2(0, uvSize) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
        }
        static RepackOptions Options(float density)
        {
            var options = RepackOptions.Default;
            options.resolution = 128; options.padding = 2; options.blockAlign = false;
            options.internalOversample = 1; options.normalizeTexelDensity = false;
            options.reparameterizeStretchedShells = false; options.texelsPerUnit = density;
            return options;
        }
        static void RequireNative()
        {
            try { XatlasNative.xatlasCreate(); XatlasNative.xatlasDestroy(); }
            catch (DllNotFoundException) { Assert.Ignore("xatlas native plugin unavailable"); }
            catch (EntryPointNotFoundException) { Assert.Ignore("xatlas native entry point unavailable"); }
        }

        [TestCase(0f)]
        [TestCase(40000f)]
        public void NativeRepackKeepsSmallValidFaces(float density)
        {
            RequireNative(); var mesh = Quad(.0001f); var originalUv = mesh.uv;
            var originalPositions = mesh.vertices; var originalIndices = mesh.triangles;
            try {
                var result = XatlasRepack.RepackSingle(mesh, Options(density));
                Assert.IsTrue(result.ok, result.error);
                Assert.AreEqual(0, result.orphanVertices, "Small positive-area triangles must reach the atlas.");
                Assert.AreEqual(0, result.snappedVertices);
                var quality = TransferUvQuality.Measure(mesh, mesh.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(0, quality.degenerateFaces); Assert.AreEqual(0, quality.overlapPairs);
                Assert.That(quality.worstAnisotropy, Is.LessThan(1.01));
                CollectionAssert.AreEqual(originalUv, mesh.uv);
                CollectionAssert.AreEqual(originalPositions, mesh.vertices);
                CollectionAssert.AreEqual(originalIndices, mesh.triangles);
            } finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SharedAtlasKeepsExplicitDensityForMixedUvScales()
        {
            RequireNative(); var actual = new[] { Quad(.002f), Quad(.0001f) };
            var reference = new[] { Quad(.002f), Quad(.0001f) };
            var originals = new[] { actual[0].uv, actual[1].uv };
            try {
                // The independent native oracle uses equivalent UV units and density.
                foreach (var mesh in reference) {
                    var uv = mesh.uv; for (int i = 0; i < uv.Length; ++i) uv[i] *= 32;
                    mesh.uv = uv;
                }
                var expected = XatlasRepack.RepackMulti(reference, Options(40000f / 32));
                var results = XatlasRepack.RepackMulti(actual, Options(40000));
                for (int m = 0; m < actual.Length; ++m) {
                    Assert.IsTrue(expected[m].ok, expected[m].error); Assert.IsTrue(results[m].ok, results[m].error);
                    Assert.AreEqual(0, results[m].orphanVertices);
                    Assert.AreEqual(expected[m].atlasWidth, results[m].atlasWidth);
                    Assert.AreEqual(expected[m].atlasHeight, results[m].atlasHeight);
                    var uv = actual[m].uv2; var baseline = reference[m].uv2;
                    for (int i = 0; i < uv.Length; ++i) Assert.That(Vector2.Distance(uv[i], baseline[i]), Is.LessThan(1e-6));
                    CollectionAssert.AreEqual(originals[m], actual[m].uv);
                }
            } finally {
                foreach (var mesh in actual) Object.DestroyImmediate(mesh);
                foreach (var mesh in reference) Object.DestroyImmediate(mesh);
            }
        }

    }
}
