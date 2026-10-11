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

        [TestCase((int)FoliageNormalSource.Sphere, false)] [TestCase((int)FoliageNormalSource.Sphere, true)]
        [TestCase((int)FoliageNormalSource.Hemisphere, false)] [TestCase((int)FoliageNormalSource.Hemisphere, true)]
        public void InactiveRadiusDoesNotBlockShapeBuildAfterChangingFitMode(int sourceValue, bool autoFit)
        {
            var points = new[] { Vector3.left, Vector3.right, Vector3.up };
            foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                var settings = new FoliageNormalsSettings { normalSource = (FoliageNormalSource)sourceValue,
                    autoFitProxy = autoFit, proxyRadius = autoFit ? invalid : 2, proxyRadiusScale = autoFit ? 1 : invalid };
                Assert.DoesNotThrow(settings.Validate);
                var shape = FoliageShapeProxyBuilder.Build(points, new[] { 0,1,2 }, FoliageProxyBuilder.BoundsOf(points), settings, CancellationToken.None);
                Assert.Greater(shape.radius, 0); Assert.IsTrue(FoliageTargetSnapshot.Finite(shape.radius));
                foreach (var p in shape.mesh.positions) Assert.IsTrue(FoliageTargetSnapshot.Finite(p));
            }
        }

        [TestCase((int)FoliageNormalSource.Sphere, false)] [TestCase((int)FoliageNormalSource.Sphere, true)]
        [TestCase((int)FoliageNormalSource.Hemisphere, false)] [TestCase((int)FoliageNormalSource.Hemisphere, true)]
        public void ActiveRadiusStillRejectsInvalidValues(int sourceValue, bool autoFit)
        {
            foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                var settings = new FoliageNormalsSettings { normalSource = (FoliageNormalSource)sourceValue,
                    autoFitProxy = autoFit, proxyRadius = autoFit ? 1 : invalid, proxyRadiusScale = autoFit ? invalid : 1 };
                Assert.Throws<ArgumentException>(settings.Validate);
            }
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
            var settings = new FoliageNormalsSettings { normalSource = FoliageNormalSource.Sphere, autoFitProxy = false };
            settings.proxyRadius = 0; Assert.Throws<ArgumentException>(() => settings.Validate());
            settings.proxyRadius = 1; settings.proxySegments = 17; Assert.Throws<ArgumentException>(() => settings.Validate());
            settings.proxySegments = 64; settings.proxyCenter = new Vector3(float.NaN,0,0); Assert.Throws<ArgumentException>(() => settings.Validate());
            settings.proxyCenter = Vector3.zero; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => FoliageShapeProxyBuilder.Build(Array.Empty<Vector3>(), Array.Empty<int>(), default, settings, cancellation.Token));
        }

        static Vector3[] BoxCorners(Bounds bounds, Quaternion rotation)
        {
            var points = new Vector3[8];
            for (int i = 0; i < points.Length; i++)
                points[i] = rotation * new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z);
            return points;
        }

        [TestCase((int)FoliageNormalSource.Sphere, 0)]
        [TestCase((int)FoliageNormalSource.Hemisphere, 0)]
        [TestCase((int)FoliageNormalSource.Hemisphere, 90)]
        [TestCase((int)FoliageNormalSource.Hemisphere, 35)]
        public void BoundingBoxFitsEachAxisWithOutwardEllipsoidNormalsAndAnOpenHemisphere(int sourceValue, int angle)
        {
            var source = (FoliageNormalSource)sourceValue;
            var rotation = Quaternion.Euler(angle, 0, 0);
            var bounds = new Bounds(new Vector3(10, 20, -5), new Vector3(4, 6, 8));
            var points = BoxCorners(bounds, rotation);
            var settings = new FoliageNormalsSettings { normalSource = source, proxyFit = FoliageProxyFit.ObjectBoundingBox,
                proxyRotation = new Vector3(angle, 0, 0) };
            var shape = FoliageShapeProxyBuilder.Build(points, new[] { 0, 1, 2 }, default, settings, CancellationToken.None);
            var expectedRadii = new Vector3(2, source == FoliageNormalSource.Hemisphere ? 6 : 3, 4);
            var expectedCenter = rotation * (bounds.center + (source == FoliageNormalSource.Hemisphere ? Vector3.down * 3 : Vector3.zero));
            Assert.Less((shape.radii - expectedRadii).magnitude, 1e-4);
            Assert.Less((shape.center - expectedCenter).magnitude, 1e-4);
            var inverse = Quaternion.Inverse(rotation);
            var hits = FoliageNormalTransfer.QueryCpu(new TriangleBvh(shape.mesh.positions, shape.mesh.indices), shape.mesh.positions, 1, CancellationToken.None);
            for (int i = 0; i < shape.mesh.positions.Length; i++)
            {
                var p = inverse * (shape.mesh.positions[i] - shape.center);
                Assert.That(p.x * p.x / 4 + p.y * p.y / (expectedRadii.y * expectedRadii.y) + p.z * p.z / 16, Is.EqualTo(1).Within(1e-4));
                var expected = rotation * new Vector3(p.x / 4, p.y / (expectedRadii.y * expectedRadii.y), p.z / 16).normalized;
                Assert.Greater(Vector3.Dot(expected, shape.mesh.normals[i]), .99999f);
                Assert.Greater(Vector3.Dot(expected, FoliageNormalTransfer.Interpolate(shape.mesh, hits[i].triangleIndex, hits[i].barycentric)), .9999f);
                if (source == FoliageNormalSource.Hemisphere) Assert.GreaterOrEqual(p.y, -1e-4);
            }
            var proxyBounds = FoliageProxyBuilder.BoundsOf(Array.ConvertAll(shape.mesh.positions, p => inverse * p));
            Assert.Less((proxyBounds.center - bounds.center).magnitude, 1e-4);
            Assert.Less((proxyBounds.size - bounds.size).magnitude, 1e-4);
            var topology = RemeshTopology.Inspect(shape.mesh.positions, shape.mesh.indices);
            Assert.IsTrue(topology.Valid, topology.Description);
            Assert.AreEqual(source == FoliageNormalSource.Hemisphere ? settings.proxySegments : 0, topology.boundary.Count);
        }

        [TestCase((int)FoliageNormalSource.Sphere)] [TestCase((int)FoliageNormalSource.Hemisphere)]
        public void BoundingBoxSizeMultiplierAndCenterOffsetPreserveTheFittedAxes(int sourceValue)
        {
            var points = BoxCorners(new Bounds(new Vector3(1, 3, 2), new Vector3(4, 6, 8)), Quaternion.identity);
            var settings = new FoliageNormalsSettings { normalSource = (FoliageNormalSource)sourceValue, proxyFit = FoliageProxyFit.ObjectBoundingBox };
            var original = FoliageShapeProxyBuilder.Build(points, new[] { 0, 1, 2 }, default, settings, CancellationToken.None);
            settings.proxyRadiusScale = 1.5f; settings.proxyCenter = new Vector3(3, 4, 5);
            var moved = FoliageShapeProxyBuilder.Build(points, new[] { 4, 5, 6 }, default, settings.Snapshot(), CancellationToken.None);
            Assert.AreEqual(original.radii * 1.5f, moved.radii);
            Assert.AreEqual(original.center + settings.proxyCenter, moved.center);
        }

        [TestCase((int)FoliageNormalSource.Sphere)] [TestCase((int)FoliageNormalSource.Hemisphere)]
        public void BoundingBoxSupportsPlanarCardsButRejectsEmptyOrPointBounds(int sourceValue)
        {
            var settings = new FoliageNormalsSettings { normalSource = (FoliageNormalSource)sourceValue, proxyFit = FoliageProxyFit.ObjectBoundingBox };
            var shape = FoliageShapeProxyBuilder.Build(new[] { Vector3.zero, Vector3.right, Vector3.up }, new[] { 0, 1, 2 }, default, settings, CancellationToken.None);
            Assert.Greater(shape.radii.z, 0);
            Assert.IsTrue(RemeshTopology.Inspect(shape.mesh.positions, shape.mesh.indices).Valid);
            Assert.Throws<ArgumentException>(() => FoliageShapeProxyBuilder.Build(Array.Empty<Vector3>(), Array.Empty<int>(), default, settings, CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => FoliageShapeProxyBuilder.Build(new[] { Vector3.one }, new[] { 0 }, default, settings, CancellationToken.None));
        }

        [Test]
        public void FitModeSurvivesSettingsSnapshotsAndJsonWhileOldSettingsKeepSelectedFit()
        {
            var settings = new FoliageNormalsSettings { normalSource = FoliageNormalSource.Sphere, proxyFit = FoliageProxyFit.ObjectBoundingBox };
            Assert.AreEqual(settings.proxyFit, settings.Snapshot().proxyFit);
            Assert.AreEqual(settings.proxyFit, JsonUtility.FromJson<FoliageNormalsSettings>(JsonUtility.ToJson(settings)).proxyFit);
            Assert.AreEqual(FoliageProxyFit.SelectedFoliage, JsonUtility.FromJson<FoliageNormalsSettings>("{\"normalSource\":1,\"autoFitProxy\":true}").proxyFit);
            settings.proxyFit = (FoliageProxyFit)99; Assert.Throws<ArgumentException>(settings.Validate);
            settings.autoFitProxy = false; Assert.DoesNotThrow(settings.Validate);
        }

        [TestCase((int)FoliageNormalSource.Sphere, 32)]
        [TestCase((int)FoliageNormalSource.Hemisphere, 32)]
        [TestCase((int)FoliageNormalSource.Sphere, 16384)]
        [TestCase((int)FoliageNormalSource.Hemisphere, 16384)]
        public void SmallScaledPlanarFitsKeepDistinctSidesAtTranslatedCenters(int sourceValue, float coordinate)
        {
            var points = new[] { new Vector3(0, 0, coordinate), new Vector3(1, 0, coordinate), new Vector3(0, 1, coordinate) };
            var original = (Vector3[])points.Clone();
            var settings = new FoliageNormalsSettings { normalSource = (FoliageNormalSource)sourceValue,
                proxyFit = FoliageProxyFit.ObjectBoundingBox, proxyRadiusScale = .01f, proxySegments = 16 };
            var shape = FoliageShapeProxyBuilder.Build(points, new[] { 0, 1, 2 }, default, settings, CancellationToken.None);
            var bounds = FoliageProxyBuilder.BoundsOf(shape.mesh.positions);
            Assert.GreaterOrEqual(shape.radii.z, .0001f);
            Assert.Less(bounds.min.z, shape.center.z); Assert.Greater(bounds.max.z, shape.center.z);
            Assert.IsTrue(RemeshTopology.Inspect(shape.mesh.positions, shape.mesh.indices).Valid);
            var bvh = new TriangleBvh(shape.mesh.positions, shape.mesh.indices);
            var query = new[] { shape.center + Vector3.forward * shape.radii.z, shape.center - Vector3.forward * shape.radii.z };
            var hits = FoliageNormalTransfer.QueryCpu(bvh, query, 1, CancellationToken.None);
            Assert.Greater(Vector3.Dot(Vector3.forward, FoliageNormalTransfer.Interpolate(shape.mesh, hits[0].triangleIndex, hits[0].barycentric)), .99f);
            Assert.Greater(Vector3.Dot(Vector3.back, FoliageNormalTransfer.Interpolate(shape.mesh, hits[1].triangleIndex, hits[1].barycentric)), .99f);
            CollectionAssert.AreEqual(original, points);
        }

        [UnityTest]
        public IEnumerator BoundingBoxIncludesExcludedSubmeshesButOnlyTransfersSelectedFacesOnEveryLod()
        {
            foreach (var source in new[] { FoliageNormalSource.Sphere, FoliageNormalSource.Hemisphere })
            {
                var box = new Bounds(new Vector3(1, 3, 2), new Vector3(4, 6, 8));
                using var lod0 = new FoliageNormalTransferTests.Fixture(BoxCorners(box, Quaternion.identity));
                var recipientPoints = BoxCorners(box, Quaternion.identity); recipientPoints[3] = Vector3.one * 200;
                using var lod1 = new FoliageNormalTransferTests.Fixture(recipientPoints);
                using var other = new FoliageNormalTransferTests.Fixture(BoxCorners(new Bounds(Vector3.one * 1000, Vector3.one * 1000), Quaternion.identity));
                var parent = new GameObject("BBox LOD tree");
                var leafMaterial = new Material(Shader.Find("Standard"));
                var excludedMaterial = new Material(Shader.Find("Standard"));
                using var pipeline = new FoliageNormalsPipeline(new CountingVoxelBackend());
                try
                {
                    lod0.renderer.sharedMaterials = new[] { leafMaterial, excludedMaterial };
                    lod1.renderer.sharedMaterials = new[] { leafMaterial, excludedMaterial };
                    lod0.root.name = lod0.mesh.name = "Tree_LOD0"; lod1.root.name = lod1.mesh.name = "Tree_LOD1";
                    lod0.root.transform.SetParent(parent.transform, false); lod1.root.transform.SetParent(parent.transform, false);
                    other.root.transform.SetParent(parent.transform, false);
                    parent.AddComponent<LODGroup>().SetLODs(new[] { new LOD(.6f, new Renderer[] { lod0.renderer }), new LOD(.2f, new Renderer[] { lod1.renderer }) });
                    lod0.root.transform.localRotation = Quaternion.Euler(0, 90, 0); lod0.root.transform.localScale = new Vector3(-2, 3, .5f);
                    lod0.root.transform.localPosition = new Vector3(7, 8, 9);
                    lod1.root.transform.localPosition = lod0.root.transform.localPosition;
                    lod1.root.transform.localRotation = lod0.root.transform.localRotation;
                    lod1.root.transform.localScale = lod0.root.transform.localScale;
                    parent.transform.localScale = new Vector3(-.01f, .02f, .03f); parent.transform.localRotation = Quaternion.Euler(20, 30, 40);
                    var matrix = parent.transform.worldToLocalMatrix * lod0.renderer.localToWorldMatrix;
                    var expected = FoliageProxyBuilder.BoundsOf(Array.ConvertAll(lod0.mesh.vertices, matrix.MultiplyPoint3x4));
                    lod0.mesh.UploadMeshData(true);
                    var before = FoliageTargetSnapshot.ContentFingerprint(lod0.mesh);
                    var settings = new FoliageNormalsSettings { normalSource = source, proxyFit = FoliageProxyFit.ObjectBoundingBox,
                        selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 0 },
                        filterRenderers = true, renderers = new[] { lod0.renderer }, gpuProjection = false, recalculateTangents = false };
                    var task = pipeline.Run(parent, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task);
                    Assert.IsTrue(task.Result, pipeline.Status); Assert.AreEqual(2, pipeline.Targets.Count); Assert.AreEqual(1, pipeline.CaptureSourceCount);
                    Assert.AreEqual(6, pipeline.TransferDiagnostics.transferred); Assert.AreEqual(0, pipeline.TransferDiagnostics.misses);
                    Assert.Less((pipeline.EnvelopeMesh.bounds.center - expected.center).magnitude, 1e-4);
                    Assert.Less((pipeline.EnvelopeMesh.bounds.size - expected.size).magnitude, 1e-4);
                    foreach (var target in pipeline.Targets)
                    {
                        bool changed = false;
                        var transferred = target.working.GetIndices(0); var normals = target.working.normals;
                        for (int i = 0; i < transferred.Length; i++)
                            if (Vector3.Dot(target.localNormals[target.indices[0][i]], normals[transferred[i]]) < .99f) changed = true;
                        Assert.IsTrue(changed, "Each selected LOD receives the fitted proxy normals.");
                        CollectionAssert.AreEqual(target.indices[1], target.working.GetIndices(1));
                        foreach (int i in target.indices[1]) Assert.AreEqual(target.localNormals[i], target.working.normals[i]);
                        Assert.AreSame(target.source, target.filter.sharedMesh);
                    }
                    Assert.AreEqual(before, FoliageTargetSnapshot.ContentFingerprint(lod0.mesh));
                    Assert.AreSame(other.mesh, other.renderer.GetComponent<MeshFilter>().sharedMesh);
                    var shape = pipeline.EnvelopeMesh; var captured = pipeline.Targets[0];
                    settings.proxyFit = FoliageProxyFit.SelectedFoliage; pipeline.InvalidateChanged(settings, parent);
                    Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Capture)); Assert.AreSame(captured, pipeline.Targets[0]); Assert.IsFalse(shape);
                }
                finally
                {
                    pipeline.Dispose(); lod0.root.transform.SetParent(null, false); lod1.root.transform.SetParent(null, false); other.root.transform.SetParent(null, false);
                    Object.DestroyImmediate(parent);
                    Object.DestroyImmediate(leafMaterial); Object.DestroyImmediate(excludedMaterial);
                }
            }
        }

        [UnityTest]
        public IEnumerator BoundingBoxAutoReachCoversFarProxyWhileExplicitDistanceRemainsLimited()
        {
            foreach (var source in new[] { FoliageNormalSource.Sphere, FoliageNormalSource.Hemisphere })
                foreach (bool gpu in new[] { false, true })
                {
                    if (gpu && !GpuBvh.Supported) continue;
                    var positions = new Vector3[11]; positions[1] = Vector3.right * .02f; positions[2] = Vector3.up * .02f;
                    Array.Copy(BoxCorners(new Bounds(Vector3.zero, Vector3.one * 200), Quaternion.identity), 0, positions, 3, 8);
                    using var leaf = new FoliageNormalTransferTests.Fixture(positions);
                    leaf.mesh.SetTriangles(new[] { 3, 4, 5, 4, 6, 5, 7, 9, 8, 8, 9, 10 }, 1);
                    using var pipeline = new FoliageNormalsPipeline(new CountingVoxelBackend());
                    var settings = new FoliageNormalsSettings { normalSource = source, proxyFit = FoliageProxyFit.ObjectBoundingBox,
                        selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 0 },
                        gpuProjection = gpu, recalculateTangents = false };
                    var task = pipeline.Run(leaf.root, settings, FoliageNormalsPipeline.Stage.Transfer);
                    yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                    Assert.Less(pipeline.CaptureBounds.size.magnitude * 2, 1, "The selected capture alone cannot reach the large proxy.");
                    Assert.AreEqual(3, pipeline.TransferDiagnostics.transferred); Assert.AreEqual(0, pipeline.TransferDiagnostics.misses);
                    Assert.AreEqual(gpu, pipeline.TransferDiagnostics.gpu);
                    var target = pipeline.Targets[0]; CollectionAssert.AreEqual(positions, target.working.vertices);
                    CollectionAssert.AreEqual(target.indices[1], target.working.GetIndices(1));
                    foreach (int vertex in target.indices[1]) Assert.AreEqual(target.localNormals[vertex], target.working.normals[vertex]);
                    settings.maxTransferDistance = .1f; pipeline.InvalidateChanged(settings, leaf.root);
                    LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("No normals transferred"));
                    task = pipeline.Run(leaf.root, settings, FoliageNormalsPipeline.Stage.Transfer);
                    yield return FoliageNormalsToolTests.Await(task); Assert.IsFalse(task.Result);
                    Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Transfer)); Assert.IsFalse(target.working);
                    Assert.AreSame(leaf.mesh, leaf.root.GetComponent<MeshFilter>().sharedMesh);
                }
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
              foreach (var fit in new[] { FoliageProxyFit.SelectedFoliage, FoliageProxyFit.ObjectBoundingBox }) {
                var settings = new FoliageNormalsSettings { normalSource = source, proxyFit = fit };
                var shape = fit == FoliageProxyFit.SelectedFoliage ? Shape(source) : FoliageShapeProxyBuilder.Build(
                    BoxCorners(new Bounds(Vector3.zero, new Vector3(4, 6, 8)), Quaternion.identity), new[] { 0, 1, 2 }, default, settings, CancellationToken.None);
                var mesh = shape.mesh; var bvh = new TriangleBvh(mesh.positions, mesh.indices);
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
}
