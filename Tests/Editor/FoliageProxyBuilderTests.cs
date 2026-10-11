using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageProxyBuilderTests
    {
        internal static RemeshNative.IndexedMesh Spheres(params Vector3[] centers)
        {
            var spheres = Array.ConvertAll(centers, c => new FoliagePrimitive { center = c, radius = 1 });
            return FoliagePrimitiveBuilder.Build(spheres, 1, CancellationToken.None);
        }

        [Test]
        public void PrimitiveBoundsIndicesBudgetAndUInt32WithoutSceneObjects()
        {
            int before = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length;
            var sphere = Spheres(Vector3.one * 2);
            Assert.That((FoliageProxyBuilder.BoundsOf(sphere.positions).size - Vector3.one * 2).magnitude, Is.LessThan(1e-6));
            foreach (int i in sphere.indices) Assert.That(i, Is.InRange(0, sphere.positions.Length - 1));
            var many = new FoliagePrimitive[1600]; for (int i = 0; i < many.Length; i++) many[i] = new FoliagePrimitive { radius = 1, center = Vector3.right * i };
            var geometry = FoliagePrimitiveBuilder.Build(many, 1, CancellationToken.None);
            var mesh = FoliageNormalsPipeline.PreviewMesh("Many primitives", geometry);
            try { Assert.AreEqual(IndexFormat.UInt32, mesh.indexFormat); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
            Assert.AreEqual(before, UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length);
            Assert.Throws<InvalidOperationException>(() => FoliagePrimitiveBuilder.Build(new FoliagePrimitive[50000], 1, CancellationToken.None));
        }

        [TestCase(1, 0f)] [TestCase(1, 1f)] [TestCase(2, 4f)]
        public void ExistingVoxelBackendMatchesSphereUnion(int components, float separation)
        {
            RemeshNative.CheckAvailable();
            var primitives = separation == 0 ? Spheres(Vector3.zero) : Spheres(Vector3.left * separation / 2, Vector3.right * separation / 2);
            var result = new MeshoptVoxelProxyBuilder().Build(primitives, 64, CancellationToken.None);
            var topology = FoliageProxyBuilder.Validate(result, CancellationToken.None, false);
            Assert.IsTrue(topology.Valid); Assert.AreEqual(0, topology.boundary.Count); Assert.AreEqual(components, topology.euler.Count);
            foreach (int e in topology.euler) Assert.AreEqual(2, e, "Each connected sphere union has a single outer shell without overlap interiors");
            Assert.Greater(FoliageProxyBuilder.SignedVolume(result.positions, result.indices), 0);
            var expected = new Bounds(Vector3.zero, new Vector3(separation + 2, 2, 2));
            var actual = FoliageProxyBuilder.BoundsOf(result.positions); float tolerance = (separation + 2) / 64 * 1.5f;
            for (int k = 0; k < 3; k++) { Assert.That(actual.min[k], Is.EqualTo(expected.min[k]).Within(tolerance)); Assert.That(actual.max[k], Is.EqualTo(expected.max[k]).Within(tolerance)); }
            if (separation == 1) {
                // The midpoint is deep inside the union: no extracted face may survive there.
                var nearest = new TriangleBvh(result.positions, result.indices).FindNearest(Vector3.zero);
                Assert.Greater(nearest.distSq, .4f);
            }
        }

        [Test]
        public void VoxelAndSmoothingCancellationDoesNotPublishUnityMeshes()
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => new MeshoptVoxelProxyBuilder().Build(Spheres(Vector3.zero), 64, cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => FoliageProxySmoother.Smooth(Spheres(Vector3.zero), new FoliageNormalsSettings(), cancelled.Token, out _));
        }

        [Test]
        public void TaubinKeepsTopologyVolumeAndReducesHighFrequencyNoise()
        {
            var input = Spheres(Vector3.zero); input.normals = MeshGeometry.AveragedNormals(input.positions, input.indices);
            for (int i = 0; i < input.positions.Length; i++) input.positions[i] *= (i % 2 == 0 ? 1.05f : .95f);
            var s = new FoliageNormalsSettings { smoothingIterations = 3 };
            double before = Noise(input);
            var result = FoliageProxySmoother.Smooth(input, s, CancellationToken.None, out var d);
            CollectionAssert.AreEqual(input.indices, result.indices); Assert.AreEqual(input.positions.Length, result.positions.Length);
            Assert.AreEqual(0, RemeshTopology.Inspect(result.positions, result.indices).boundary.Count);
            Assert.That(d.afterVolume / d.beforeVolume, Is.InRange(.7, 1.3)); Assert.Less(Noise(result), before);
            foreach (var n in result.normals) Assert.That(n.magnitude, Is.EqualTo(1).Within(1e-5));
        }

        static double Noise(RemeshNative.IndexedMesh mesh)
        {
            double mean = 0, variance = 0;
            foreach (var p in mesh.positions) mean += p.magnitude; mean /= mesh.positions.Length;
            foreach (var p in mesh.positions) variance += Math.Pow(p.magnitude - mean, 2);
            return variance / mesh.positions.Length;
        }
    }
}
