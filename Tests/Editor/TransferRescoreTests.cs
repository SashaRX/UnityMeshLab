using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class TransferRescoreTests
    {
        [TestCase(.001f)]
        [TestCase(1f)]
        [TestCase(1000f)]
        public void DisplacedMaterialUvCannotRematchAnExactSurfaceToADistantChart(float scale)
        {
            WithMeshes(scale, false, (source, target) => {
                var trace = new TransferMatchTrace();
                GroupedShellTransfer.TransferWithDiagnostics(target, source, null, null, 256, 256, trace);
                Assert.AreEqual(0, trace.shells[0].initialSource);
                Assert.AreEqual(0, trace.shells[0].afterRescoreSource,
                    "UV0 coverage must not override an exact geometric donor with a remote surface.");
            });
        }

        [TestCase(.01f)]
        [TestCase(1f)]
        [TestCase(1000f)]
        public void DisplacedMaterialUvProjectsOntoItsGeometricDonor(float scale)
        {
            WithMeshes(scale, false, (source, target) => {
                var result = GroupedShellTransfer.TransferWithDiagnostics(target, source, null, null, 256, 256, new TransferMatchTrace());
                for (int i = 0; i < target.vertexCount; ++i)
                    Assert.That(Vector2.Distance(result.uv2[i], source.uv2[i]), Is.LessThan(1e-5f));
                AssertValid(target, result.uv2);
            });
        }

        [Test]
        public void OverlappingGeometricDonorCanRescoreWithoutReplacingSurfaceDistanceWithCentroidDistance()
        {
            WithMeshes(1, true, (source, target) => {
                var trace = new TransferMatchTrace();
                var result = GroupedShellTransfer.TransferWithDiagnostics(target, source, null, null, 256, 256, trace);
                Assert.AreEqual(0, trace.shells[0].initialSource);
                Assert.AreEqual(1, trace.shells[0].afterRescoreSource);
                Assert.That(trace.shells[0].finalSurfaceDistanceSquared, Is.LessThan(1e-10f),
                    "The target lies on the donor even though their chart centroids differ.");
                for (int i = 0; i < target.vertexCount; ++i)
                    Assert.That(Vector2.Distance(result.uv2[i], (Vector2)target.vertices[i] * .2f + new Vector2(.55f, .05f)), Is.LessThan(1e-5f));
                AssertValid(target, result.uv2);
            });
        }

        static void AssertValid(Mesh target, Vector2[] uv)
        {
            var quality = TransferUvQuality.Measure(target, uv, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, quality.degenerateFaces + quality.invalidFaces + quality.overlapPairs);
        }

        static void WithMeshes(float scale, bool overlappingDonor, System.Action<Mesh, Mesh> check)
        {
            var quad = new[] {Vector3.zero, Vector3.right, new Vector3(1,1), Vector3.up};
            var uv = new[] {Vector2.zero, Vector2.right, Vector2.one, Vector2.up};
            var faces = new[] {0,1,2,0,2,3};
            var source = new Mesh {
                vertices = quad.Concat(quad.Select(p => overlappingDonor ? p * 2 : p + Vector3.right * 10)).Select(p => p * scale).ToArray(),
                triangles = faces.Concat(faces.Select(i => i + 4)).ToArray(),
                uv = uv.Concat(uv.Select(p => p * (overlappingDonor ? 2 : 1) + Vector2.right * 3)).ToArray(),
                uv2 = uv.Select(p => p * .4f + Vector2.one * .05f)
                    .Concat(uv.Select(p => p * .4f + new Vector2(.55f,.05f))).ToArray()
            };
            var target = new Mesh {vertices = quad.Select(p => p * scale).ToArray(), triangles = faces,
                uv = uv.Select(p => p + Vector2.right * 3).ToArray()};
            try
            {
                source.RecalculateNormals(); target.RecalculateNormals();
                var sourceBytes = TransferMeshSnapshot.Capture(source);
                var targetBytes = TransferMeshSnapshot.Capture(target);
                check(source, target);
                CollectionAssert.AreEqual(sourceBytes,TransferMeshSnapshot.Capture(source));
                CollectionAssert.AreEqual(targetBytes,TransferMeshSnapshot.Capture(target));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(target); }
        }
    }
}
