using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageShapeProxyTests
    {
        sealed class CountingVoxelBackend : IFoliageProxyBuilder
        {
            internal int calls;
            public RemeshNative.IndexedMesh Build(RemeshNative.IndexedMesh primitives, int resolution, CancellationToken token)
            { calls++; throw new InvalidOperationException("Analytic proxies must not use the voxel backend."); }
        }

        static FoliageShapeProxyBuilder.Result Shape(FoliageNormalSource source, Vector3 rotation = default)
        {
            var settings = new FoliageNormalsSettings { normalSource = source, autoFitProxy = false, proxyCenter = new Vector3(2,3,4), proxyRadius = 2, proxyRotation = rotation };
            return FoliageShapeProxyBuilder.Build(Array.Empty<Vector3>(), Array.Empty<int>(), default, settings, CancellationToken.None);
        }

        [TestCase((int)FoliageNormalSource.Sphere)] [TestCase((int)FoliageNormalSource.Hemisphere)]
        public void ShapeIsIndexedOutwardAndRadialWithOnlyExpectedBoundary(int sourceValue)
        {
            var source = (FoliageNormalSource)sourceValue;
            int objects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length;
            var shape = Shape(source); var mesh = shape.mesh;
            var topology = RemeshTopology.Inspect(mesh.positions, mesh.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(1, topology.euler.Count);
            Assert.AreEqual(source == FoliageNormalSource.Sphere ? 0 : 64, topology.boundary.Count);
            Assert.AreEqual(source == FoliageNormalSource.Sphere ? 2 : 1, topology.euler[0]);
            var hits = FoliageNormalTransfer.QueryCpu(new TriangleBvh(mesh.positions, mesh.indices), mesh.positions, 1, CancellationToken.None);
            for (int i = 0; i < mesh.positions.Length; i++) {
                var radial = (mesh.positions[i] - shape.center).normalized;
                Assert.That((mesh.positions[i] - shape.center).magnitude, Is.EqualTo(2).Within(1e-5));
                Assert.That(mesh.normals[i].magnitude, Is.EqualTo(1).Within(1e-5));
                Assert.Greater(Vector3.Dot(FoliageNormalTransfer.Interpolate(mesh, hits[i].triangleIndex, hits[i].barycentric), radial), .9999f);
                if (source == FoliageNormalSource.Hemisphere) Assert.GreaterOrEqual(Vector3.Dot(radial, shape.axis), -1e-6f);
            }
            Assert.AreEqual(objects, Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length);
        }

        [Test]
        public void HemisphereHasNoCapEvenAfterRotation()
        {
            var shape = Shape(FoliageNormalSource.Hemisphere, new Vector3(90,0,0));
            var hit = new TriangleBvh(shape.mesh.positions, shape.mesh.indices).FindNearest(shape.center - shape.axis * 3);
            Assert.That(Vector3.Dot(hit.point - shape.center, shape.axis), Is.EqualTo(0).Within(1e-5));
            var normal = FoliageNormalTransfer.Interpolate(shape.mesh, hit.triangleIndex, hit.barycentric);
            Assert.That(Vector3.Dot(normal, shape.axis), Is.EqualTo(0).Within(1e-5), "A flat bottom must never become the normal source.");
        }

        [TestCase((int)FoliageNormalSource.Sphere)] [TestCase((int)FoliageNormalSource.Hemisphere)]
        public void AutoFitUsesOnlySelectedVerticesAndIsDeterministic(int sourceValue)
        {
            var source = (FoliageNormalSource)sourceValue;
            var points = new[] { new Vector3(-2,1,0), new Vector3(2,1,0), new Vector3(0,3,1), new Vector3(100,100,100) };
            int[] selected = {0,1,2}; var bounds = FoliageProxyBuilder.BoundsOf(new[] {points[0],points[1],points[2]});
            var settings = new FoliageNormalsSettings { normalSource = source, proxyRotation = new Vector3(0,0,45) };
            var a = FoliageShapeProxyBuilder.Build(points, selected, bounds, settings, CancellationToken.None);
            var b = FoliageShapeProxyBuilder.Build(points, selected, bounds, settings.Snapshot(), CancellationToken.None);
            CollectionAssert.AreEqual(a.mesh.positions, b.mesh.positions); CollectionAssert.AreEqual(a.mesh.indices, b.mesh.indices);
            Assert.Less(a.radius, 10); float lowest = float.PositiveInfinity, largest = 0;
            foreach (int i in selected) {
                lowest = Math.Min(lowest, Vector3.Dot(points[i] - a.center, a.axis));
                largest = Math.Max(largest, (points[i] - a.center).magnitude);
            }
            Assert.That(a.radius, Is.EqualTo(largest).Within(1e-5));
            if (source == FoliageNormalSource.Hemisphere) Assert.That(lowest, Is.EqualTo(0).Within(1e-5));
            else Assert.AreEqual(bounds.center, a.center);
            settings.proxyRadiusScale = 1.5f;
            var scaled = FoliageShapeProxyBuilder.Build(points, selected, bounds, settings, CancellationToken.None);
            Assert.That(scaled.radius, Is.EqualTo(a.radius * 1.5f).Within(1e-5));
        }

        [Test]
        public void InvalidShapeSettingsAndCancellationPublishNoMesh()
        {
            var settings = new FoliageNormalsSettings { normalSource = FoliageNormalSource.Sphere };
            settings.proxyRadius = 0; Assert.Throws<ArgumentException>(() => settings.Validate());
            settings.proxyRadius = 1; settings.proxySegments = 17; Assert.Throws<ArgumentException>(() => settings.Validate());
            settings.proxySegments = 64; settings.proxyCenter = new Vector3(float.NaN,0,0); Assert.Throws<ArgumentException>(() => settings.Validate());
            settings.proxyCenter = Vector3.zero; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => FoliageShapeProxyBuilder.Build(Array.Empty<Vector3>(), Array.Empty<int>(), default, settings, cancellation.Token));
        }

        [UnityTest]
        public IEnumerator ShapeModesBypassVoxelPreserveExcludedFacesAndSupportUndoRedo()
        {
            foreach (var source in new[] {FoliageNormalSource.Sphere, FoliageNormalSource.Hemisphere}) {
                using var fixture = new FoliageNormalTransferTests.Fixture(); var backend = new CountingVoxelBackend();
                using var pipeline = new FoliageNormalsPipeline(backend);
                var settings = new FoliageNormalsSettings { normalSource = source, selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] {0}, gpuProjection = false };
                var originalNormals = fixture.mesh.normals; var positions = fixture.mesh.vertices;
                fixture.root.transform.localRotation = Quaternion.Euler(-90,20,10); fixture.root.transform.localScale = new Vector3(-.01f,.02f,.03f);
                var transform = fixture.root.transform.localToWorldMatrix;
                try {
                    var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task);
                    Assert.IsTrue(task.Result, pipeline.Status); Assert.AreEqual(0, backend.calls); Assert.IsNull(pipeline.Samples); Assert.IsNull(pipeline.SmoothDiagnostics);
                    Assert.AreEqual(3, pipeline.TransferDiagnostics.transferred); Assert.AreEqual(2, pipeline.TransferDiagnostics.duplicated); Assert.AreEqual(0, pipeline.TransferDiagnostics.misses);
                    var result = pipeline.Targets[0].working; Assert.AreEqual(6, result.vertexCount);
                    CollectionAssert.AreEqual(fixture.mesh.GetIndices(1), result.GetIndices(1));
                    foreach (int i in fixture.mesh.GetIndices(1)) Assert.AreEqual(originalNormals[i], result.normals[i]);
                    Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                    CollectionAssert.AreEqual(positions, fixture.mesh.vertices); Assert.AreEqual(transform, fixture.root.transform.localToWorldMatrix);
                    pipeline.Apply(); Undo.FlushUndoRecordObjects();
                    Undo.PerformUndo(); Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                    Undo.PerformRedo(); Assert.AreEqual(6, fixture.root.GetComponent<MeshFilter>().sharedMesh.vertexCount);
                    Assert.AreEqual(transform, fixture.root.transform.localToWorldMatrix);
                }
                finally { Undo.ClearAll(); var applied = fixture.root.GetComponent<MeshFilter>().sharedMesh; if (applied && applied != fixture.mesh) Object.DestroyImmediate(applied); }
            }
        }

        [UnityTest]
        public IEnumerator ShapeChangesInvalidateOnlyDependentStagesAndRepeatedBuildsReleasePreview()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline(new CountingVoxelBackend());
            var settings = new FoliageNormalsSettings { normalSource = FoliageNormalSource.Sphere, gpuProjection = false };
            var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            var target = pipeline.Targets[0]; var envelope = pipeline.EnvelopeMesh;
            settings.smoothingIterations++; settings.sampleSpacing *= 2; pipeline.InvalidateChanged(settings, fixture.root);
            Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Transfer), "Unused voxel/scatter settings do not invalidate a shape proxy.");
            settings.transferBlend = .5f; pipeline.InvalidateChanged(settings, fixture.root); Assert.AreSame(envelope, pipeline.EnvelopeMesh);
            settings.proxyCenter = Vector3.up; pipeline.InvalidateChanged(settings, fixture.root);
            Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Capture)); Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Scatter)); Assert.IsFalse(envelope);
            task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            var old = pipeline.SmoothedMesh; settings.normalSource = FoliageNormalSource.Hemisphere; pipeline.InvalidateChanged(settings, fixture.root);
            Assert.IsFalse(old); Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Scatter)); Assert.AreSame(target, pipeline.Targets[0]);
            task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            Assert.AreSame(target, pipeline.Targets[0]); pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
            Assert.IsNull(pipeline.ShapeProxy); Assert.IsNull(pipeline.EnvelopeMesh); Assert.IsNull(pipeline.SmoothedMesh);
        }

        [UnityTest]
        public IEnumerator ShapeWorldNormalsSurviveAllSevenCaptureTransformVariants()
        {
            var rotation = new Vector3(15,30,45);
            foreach (var source in new[] {FoliageNormalSource.Sphere, FoliageNormalSource.Hemisphere}) {
                var shape = Shape(source, rotation);
                var points = new[] {shape.mesh.positions[0],shape.mesh.positions[1],shape.mesh.positions[2]};
                for (int variant = 0; variant < 7; variant++) {
                    using var fixture = new FoliageNormalTransferTests.Fixture(points);
                    var parent = new GameObject("Shape capture root"); var grand = new GameObject("Shape nested parent");
                    using var pipeline = new FoliageNormalsPipeline(new CountingVoxelBackend());
                    try {
                        var child = fixture.root.transform; child.SetParent(parent.transform, false);
                        if (variant == 1) parent.transform.localRotation = Quaternion.Euler(90,0,0);
                        if (variant == 2) parent.transform.localRotation = Quaternion.Euler(-90,0,0);
                        if (variant == 3) parent.transform.localScale = Vector3.one * .01f;
                        if (variant == 4) child.localScale = new Vector3(2,.5f,3);
                        if (variant == 5) child.localScale = new Vector3(-2,3,.5f);
                        if (variant == 6) {
                            parent.transform.SetParent(grand.transform, false); grand.transform.localScale = new Vector3(.01f,.02f,-.03f);
                            grand.transform.localRotation = Quaternion.Euler(-90,22,14); child.localRotation = Quaternion.Euler(32,44,5); child.localScale = new Vector3(2,3,1);
                        }
                        child.localPosition = new Vector3(.1f,.2f,.3f);
                        var inverse = (parent.transform.worldToLocalMatrix * child.localToWorldMatrix).inverse;
                        fixture.mesh.vertices = Array.ConvertAll(points, inverse.MultiplyPoint3x4);
                        var transforms = new[] {child,parent.transform,grand.transform};
                        var positions = Array.ConvertAll(transforms, t => t.localPosition); var rotations = Array.ConvertAll(transforms, t => t.localRotation);
                        var scales = Array.ConvertAll(transforms, t => t.localScale); var worlds = Array.ConvertAll(transforms, t => t.localToWorldMatrix);
                        var settings = new FoliageNormalsSettings { normalSource = source, autoFitProxy = false, proxyCenter = shape.center, proxyRadius = shape.radius, proxyRotation = rotation, gpuProjection = false };
                        var task = pipeline.Run(parent, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                        var normals = pipeline.Targets[0].working.normals;
                        for (int i = 0; i < normals.Length; i++) {
                            var actual = child.worldToLocalMatrix.transpose.MultiplyVector(normals[i]).normalized;
                            var expected = parent.transform.worldToLocalMatrix.transpose.MultiplyVector((points[i] - shape.center).normalized).normalized;
                            Assert.Greater(Vector3.Dot(actual, expected), .999f, source + " transform " + variant);
                        }
                        for (int i = 0; i < transforms.Length; i++) {
                            Assert.AreEqual(positions[i], transforms[i].localPosition); Assert.AreEqual(rotations[i], transforms[i].localRotation);
                            Assert.AreEqual(scales[i], transforms[i].localScale); Assert.AreEqual(worlds[i], transforms[i].localToWorldMatrix);
                        }
                        Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                    }
                    finally { pipeline.Dispose(); fixture.root.transform.SetParent(null, true); Object.DestroyImmediate(parent); Object.DestroyImmediate(grand); }
                }
            }
        }

        [UnityTest]
        public IEnumerator SyntheticCrownSphereAndHemisphereTransferAllVerticesWithCpuGpuParity()
        {
            var crown = FoliageNormalsToolTests.CrownGeometry(96);
            using var fixture = new FoliageNormalTransferTests.Fixture(crown.positions);
            fixture.mesh.subMeshCount = 1; fixture.mesh.triangles = crown.indices;
            fixture.mesh.normals = MeshGeometry.AveragedNormals(crown.positions, crown.indices);
            using var pipeline = new FoliageNormalsPipeline(new CountingVoxelBackend());
            var metrics = new List<string>(); string output = Environment.GetEnvironmentVariable("MESHLAB_FOLIAGE_OUTPUT");
            foreach (var source in new[] {FoliageNormalSource.Sphere, FoliageNormalSource.Hemisphere}) {
                var settings = new FoliageNormalsSettings { normalSource = source, gpuProjection = false, recalculateTangents = false };
                var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                Assert.AreEqual(384, pipeline.TransferDiagnostics.transferred); Assert.AreEqual(0, pipeline.TransferDiagnostics.misses);
                metrics.Add(source + " CPU: " + pipeline.TransferDiagnostics); var normals = pipeline.Targets[0].working.normals;
                if (!string.IsNullOrEmpty(output)) {
                    Directory.CreateDirectory(output); string prefix = Path.Combine(output, "crown-" + source.ToString().ToLowerInvariant());
                    FoliageNormalsToolTests.RenderPreview(pipeline.EnvelopeMesh, Matrix4x4.identity, prefix + "-proxy.png", false);
                    FoliageNormalsToolTests.RenderPreview(pipeline.Targets[0].working, Matrix4x4.identity, prefix + "-normals.png", true);
                }
                if (GpuBvh.Supported) {
                    settings.gpuProjection = true; pipeline.InvalidateChanged(settings, fixture.root);
                    task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                    Assert.AreEqual(0, pipeline.TransferDiagnostics.misses); metrics.Add(source + " GPU: " + pipeline.TransferDiagnostics);
                    var gpuNormals = pipeline.Targets[0].working.normals;
                    for (int i = 0; i < normals.Length; i++) Assert.Greater(Vector3.Dot(normals[i], gpuNormals[i]), .999f);
                }
                CollectionAssert.AreEqual(crown.positions, fixture.mesh.vertices); Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
            }
            if (!string.IsNullOrEmpty(output)) File.WriteAllLines(Path.Combine(output, "shapes-metrics.txt"), metrics);
        }

        [UnityTest]
        public IEnumerator ShapeCpuGpuNearestAndNormalSemanticsMatch()
        {
            if (!GpuBvh.Supported) Assert.Ignore("GPU nearest queries unsupported on this device.");
            foreach (var source in new[] {FoliageNormalSource.Sphere, FoliageNormalSource.Hemisphere}) {
                var shape = Shape(source); var mesh = shape.mesh; var bvh = new TriangleBvh(mesh.positions, mesh.indices);
                using var gpu = GpuBvh.TryCreate(bvh); Assert.IsNotNull(gpu);
                var points = new Vector3[100];
                for (int i = 0; i < points.Length; i++) points[i] = mesh.positions[i * (mesh.positions.Length - 1) / (points.Length - 1)] + mesh.normals[i * (mesh.positions.Length - 1) / (points.Length - 1)] * (i % 2 == 0 ? .1f : 3);
                var cpu = FoliageNormalTransfer.QueryCpu(bvh, points, 1, CancellationToken.None);
                var task = FoliageNormalTransfer.QueryGpu(gpu, bvh, points, 1, CancellationToken.None); yield return FoliageNormalsToolTests.Await(task);
                var hits = task.Result;
                for (int i = 0; i < cpu.Length; i++) {
                    Assert.AreEqual(cpu[i].triangleIndex, hits[i].triangleIndex);
                    if (cpu[i].triangleIndex < 0) continue;
                    Assert.Less((cpu[i].barycentric - hits[i].barycentric).magnitude, 1e-4);
                    Assert.Greater(Vector3.Dot(FoliageNormalTransfer.Interpolate(mesh, cpu[i].triangleIndex, cpu[i].barycentric), FoliageNormalTransfer.Interpolate(mesh, hits[i].triangleIndex, hits[i].barycentric)), .9999f);
                }
            }
        }
    }
}
