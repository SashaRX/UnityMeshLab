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
        public void OversizedProxyReportsActualCountAndKeepsCancellationPriority()
        {
            var mesh = new RemeshNative.IndexedMesh { indices = new int[177476 * 3] };
            var error = Assert.Throws<FoliageProxyBudgetException>(() => FoliageProxyBuilder.CheckTriangleBudget(mesh, 100000, CancellationToken.None));
            Assert.AreEqual(177476, error.TriangleCount); Assert.AreEqual(100000, error.Limit);
            StringAssert.Contains(177476.ToString("N0"), error.Message);
            StringAssert.Contains(100000.ToString("N0"), error.Message);
            StringAssert.DoesNotContain("edge length", error.Message);
            Assert.DoesNotThrow(() => FoliageProxyBuilder.CheckTriangleBudget(mesh, error.TriangleCount, CancellationToken.None));
            Assert.Throws<OperationCanceledException>(() => FoliageProxyBuilder.CheckTriangleBudget(mesh, 100000, new CancellationToken(true)));
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

        [TestCase(false)] [TestCase(true)]
        public void SmoothingRejectsSmallComponentCollapseHiddenByLargeComponent(bool smallFirst)
        {
            var large = FoliagePrimitiveBuilder.Build(new[] { new FoliagePrimitive { radius = 10 } }, 1, CancellationToken.None);
            var small = FoliagePrimitiveBuilder.Build(new[] { new FoliagePrimitive { center = Vector3.right * 30, radius = .25f } }, 0, CancellationToken.None);
            var settings = new FoliageNormalsSettings { smoothingIterations = 5 };
            FoliageProxySmoother.Smooth(large, settings, CancellationToken.None, out var largeDiagnostic);
            FoliageProxySmoother.Diagnostics smallDiagnostic = null;
            Assert.Throws<InvalidOperationException>(() => FoliageProxySmoother.Smooth(small, settings, CancellationToken.None, out smallDiagnostic));
            Assert.Less(smallDiagnostic.afterVolume / smallDiagnostic.beforeVolume, .5);
            Assert.Greater((largeDiagnostic.afterVolume + smallDiagnostic.afterVolume) / (largeDiagnostic.beforeVolume + smallDiagnostic.beforeVolume), .5,
                "The previous aggregate guard would accept this loss of a small component.");
            var input = smallFirst ? Join(small, large) : Join(large, small);
            var positions = (Vector3[])input.positions.Clone(); var indices = (int[])input.indices.Clone();
            var error = Assert.Throws<InvalidOperationException>(() => FoliageProxySmoother.Smooth(input, settings, CancellationToken.None, out _));
            StringAssert.Contains("component lost more than half", error.Message);
            CollectionAssert.AreEqual(positions, input.positions); CollectionAssert.AreEqual(indices, input.indices);
        }

        [Test]
        public void SmoothingPreservesVolumeOfEveryDisconnectedComponent()
        {
            var input = Spheres(Vector3.left * 2, Vector3.right * 2);
            var topology = FoliageProxyBuilder.Validate(input, CancellationToken.None, false);
            var before = FoliageProxyBuilder.ComponentVolumes(input, topology);
            var result = FoliageProxySmoother.Smooth(input, new FoliageNormalsSettings { smoothingIterations = 3 }, CancellationToken.None, out _);
            var after = FoliageProxyBuilder.ComponentVolumes(result, topology);
            Assert.AreEqual(2, before.Count); CollectionAssert.AreEqual(input.indices, result.indices);
            foreach (var component in before) Assert.That(after[component.Key] / component.Value, Is.InRange(.7, 1.3));
        }

        [TestCase(false, -.25f)] [TestCase(true, -.25f)]
        [TestCase(false, -.65f)] [TestCase(true, -.65f)]
        public void SmoothingRejectsComponentExpansionIncludingSmallShellsHiddenByAggregateVolume(bool smallFirst, float compensation)
        {
            var large = FoliagePrimitiveBuilder.Build(new[] { new FoliagePrimitive { radius = 10 } }, 1, CancellationToken.None);
            var offset = Vector3.right * 30;
            var small = new RemeshNative.IndexedMesh {
                positions = new[] { offset, offset + Vector3.right * .25f, offset + Vector3.up * .25f, offset + Vector3.forward * .25f },
                indices = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 }
            };
            var input = smallFirst ? Join(small, large) : Join(large, small);
            var positions = (Vector3[])input.positions.Clone(); var indices = (int[])input.indices.Clone();
            var settings = new FoliageNormalsSettings { smoothingIterations = 1, lambda = .001f, mu = compensation };
            FoliageProxySmoother.Diagnostics diagnostics = null;
            var error = Assert.Throws<InvalidOperationException>(() => FoliageProxySmoother.Smooth(input, settings, CancellationToken.None, out diagnostics));
            StringAssert.Contains("component grew more than half", error.Message);
            if (compensation == -.25f) Assert.Less(diagnostics.VolumeChange, .15, "Aggregate warning must not hide an expanded small shell.");
            CollectionAssert.AreEqual(positions, input.positions); CollectionAssert.AreEqual(indices, input.indices);
        }

        [TestCase(false)] [TestCase(true)]
        public void SmoothingEnforcesTriangleBudgetWithRemeshEnabledOrDisabled(bool remesh)
        {
            var input = Spheres(Vector3.zero);
            var settings = new FoliageNormalsSettings { automaticProxyTriangleBudget = false, remeshProxy = remesh, maximumProxyTriangles = input.TriangleCount - 1 };
            var error = Assert.Throws<FoliageProxyBudgetException>(() => FoliageProxySmoother.Smooth(input, settings, CancellationToken.None, out _));
            StringAssert.Contains("Proxy triangle budget exceeded", error.Message);
            settings.maximumProxyTriangles = input.TriangleCount;
            Assert.DoesNotThrow(() => FoliageProxySmoother.Smooth(input, settings, CancellationToken.None, out _));
            settings.maximumProxyTriangles = 0; Assert.Throws<ArgumentException>(() => settings.Validate());
        }

        static RemeshNative.IndexedMesh Join(RemeshNative.IndexedMesh first, RemeshNative.IndexedMesh second)
        {
            var positions = new Vector3[first.positions.Length + second.positions.Length];
            var indices = new int[first.indices.Length + second.indices.Length];
            first.positions.CopyTo(positions, 0); second.positions.CopyTo(positions, first.positions.Length);
            first.indices.CopyTo(indices, 0);
            for (int i = 0; i < second.indices.Length; i++) indices[first.indices.Length + i] = second.indices[i] + first.positions.Length;
            return new RemeshNative.IndexedMesh { positions = positions, indices = indices };
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
