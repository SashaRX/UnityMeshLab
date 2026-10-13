using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageProxyRemesherTests
    {
        static RemeshNative.IndexedMesh VoxelSphere(params Vector3[] centers)
        {
            var s = new FoliageNormalsSettings();
            var voxel = new MeshoptVoxelProxyBuilder().Build(FoliageProxyBuilderTests.Spheres(centers), 32, CancellationToken.None);
            return FoliageProxySmoother.Smooth(voxel, s, CancellationToken.None, out _);
        }

        [Test]
        public void RemeshingImprovesVoxelTriangleUniformityAndPreservesClosedSurface()
        {
            var input = VoxelSphere(Vector3.zero); var positions = (Vector3[])input.positions.Clone(); var indices = (int[])input.indices.Clone();
            var result = new FoliageProxyRemesher().Remesh(input, new FoliageNormalsSettings(), CancellationToken.None); var d = result.diagnostics;
            TestContext.WriteLine(d.ToString());
            Assert.Less(d.after.edgeVariation, d.before.edgeVariation * .8);
            Assert.Less(d.after.areaVariation, d.before.areaVariation * .8);
            Assert.Greater(d.after.meanShape, d.before.meanShape);
            Assert.Greater(d.after.minimumAngle, d.before.minimumAngle);
            Assert.Less(d.volumeChange, .05);
            Assert.LessOrEqual(Math.Max(d.maxSurfaceDistance, d.maxReverseDistance), d.targetEdgeLength * .5f);
            Assert.AreEqual(1, FoliageProxyBuilder.Validate(result.mesh, CancellationToken.None, false).euler.Count);
            foreach (var n in result.mesh.normals) { Assert.IsTrue(FoliageTargetSnapshot.Finite(n)); Assert.That(n.magnitude, Is.EqualTo(1).Within(1e-5)); }
            var hits = FoliageNormalTransfer.QueryCpu(new TriangleBvh(result.mesh.positions, result.mesh.indices), input.positions, 1, CancellationToken.None);
            for (int i = 0; i < hits.Length; i++) Assert.Greater(Vector3.Dot(FoliageNormalTransfer.Interpolate(result.mesh, hits[i].triangleIndex, hits[i].barycentric), input.positions[i].normalized), .98f);
            CollectionAssert.AreEqual(positions, input.positions); CollectionAssert.AreEqual(indices, input.indices);
        }

        [TestCase(.6f)] [TestCase(1.5f)]
        public void TargetLengthChangesDensityDeterministically(float multiplier)
        {
            var input = VoxelSphere(Vector3.zero); var before = FoliageProxyRemesher.Measure(input, CancellationToken.None);
            var s = new FoliageNormalsSettings { remeshEdgeLength = (float)before.edgeMean * multiplier };
            var a = new FoliageProxyRemesher().Remesh(input, s, CancellationToken.None);
            var b = new FoliageProxyRemesher().Remesh(input, s, CancellationToken.None);
            CollectionAssert.AreEqual(a.mesh.positions, b.mesh.positions); CollectionAssert.AreEqual(a.mesh.indices, b.mesh.indices); CollectionAssert.AreEqual(a.mesh.normals, b.mesh.normals);
            if (multiplier < 1) Assert.Greater(a.mesh.TriangleCount, input.TriangleCount); else Assert.Less(a.mesh.TriangleCount, input.TriangleCount);
            Assert.That(a.diagnostics.after.edgeMean / s.remeshEdgeLength, Is.InRange(.8, 1.2));
        }

        [Test]
        public void DisconnectedComponentsStayClosedAndDoNotProjectOntoNeighbours()
        {
            var input = VoxelSphere(Vector3.left * 1.2f, Vector3.right * 1.2f);
            var result = new FoliageProxyRemesher().Remesh(input, new FoliageNormalsSettings(), CancellationToken.None);
            var topology = FoliageProxyBuilder.Validate(result.mesh, CancellationToken.None, false);
            Assert.AreEqual(2, topology.euler.Count); foreach (int euler in topology.euler) Assert.AreEqual(2, euler);
            Assert.Less(result.diagnostics.maxReverseDistance, result.diagnostics.targetEdgeLength * .5f);
        }

        [TestCase(false)] [TestCase(true)]
        public void RemeshingRejectsSmallComponentVolumeLossHiddenByLargeComponent(bool smallFirst)
        {
            var large = new FoliagePrimitive { radius = 10 };
            var small = new FoliagePrimitive { center = Vector3.right * 30, radius = .25f };
            var input = FoliagePrimitiveBuilder.Build(smallFirst ? new[] { small, large } : new[] { large, small }, 1, CancellationToken.None);
            var positions = (Vector3[])input.positions.Clone(); var indices = (int[])input.indices.Clone();
            var settings = new FoliageNormalsSettings { remeshEdgeLength = 2, remeshRelaxation = 0 };
            var error = Assert.Throws<InvalidOperationException>(() => new FoliageProxyRemesher().Remesh(input, settings, CancellationToken.None));
            StringAssert.Contains("component volume", error.Message);
            CollectionAssert.AreEqual(positions, input.positions); CollectionAssert.AreEqual(indices, input.indices);
        }

        [TestCase(false)] [TestCase(true)]
        public void RemeshingPreservesEachDifferentSizedComponentAfterCompaction(bool smallFirst)
        {
            var large = new FoliagePrimitive { radius = 1 };
            var small = new FoliagePrimitive { center = Vector3.right * 4, radius = .5f };
            var input = FoliagePrimitiveBuilder.Build(smallFirst ? new[] { small, large } : new[] { large, small }, 1, CancellationToken.None);
            var before = FoliageProxyBuilder.ComponentVolumes(input, FoliageProxyBuilder.Validate(input, CancellationToken.None, false));
            var settings = new FoliageNormalsSettings { remeshEdgeLength = .35f, remeshIterations = 3 };
            var result = new FoliageProxyRemesher().Remesh(input, settings, CancellationToken.None);
            var after = FoliageProxyBuilder.ComponentVolumes(result.mesh, FoliageProxyBuilder.Validate(result.mesh, CancellationToken.None, false));
            var beforeVolumes = new double[before.Count]; before.Values.CopyTo(beforeVolumes, 0); Array.Sort(beforeVolumes);
            var afterVolumes = new double[after.Count]; after.Values.CopyTo(afterVolumes, 0); Array.Sort(afterVolumes);
            Assert.AreEqual(2, afterVolumes.Length);
            for (int i = 0; i < beforeVolumes.Length; i++) Assert.That(afterVolumes[i] / beforeVolumes[i], Is.InRange(.75, 1.25));
            Assert.LessOrEqual(result.diagnostics.maximumComponentVolumeChange, .25);
            Assert.Greater(result.diagnostics.splits, 0); Assert.Greater(result.diagnostics.collapses, 0);
        }

        [Test]
        public void ExactPositionSplitsAreWeldedWithoutModifyingInput()
        {
            var sphere = FoliageProxyBuilderTests.Spheres(Vector3.zero);
            var p = new Vector3[sphere.indices.Length]; var t = new int[p.Length];
            for (int i = 0; i < p.Length; i++) { p[i] = sphere.positions[sphere.indices[i]]; t[i] = i; }
            var split = new RemeshNative.IndexedMesh { positions = p, indices = t };
            var result = new FoliageProxyRemesher().Remesh(split, new FoliageNormalsSettings(), CancellationToken.None);
            Assert.Less(result.mesh.positions.Length, p.Length); Assert.AreEqual(0, FoliageProxyBuilder.Validate(result.mesh, CancellationToken.None, false).boundary.Count);
            for (int i = 0; i < t.Length; i++) Assert.AreEqual(i, t[i]);
        }

        [Test]
        public void BudgetCancellationAndMalformedGeometryFailWithoutUnityAllocations()
        {
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length, objects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            var input = FoliageProxyBuilderTests.Spheres(Vector3.zero); var backend = new FoliageProxyRemesher();
            Assert.Throws<FoliageProxyBudgetException>(() => backend.Remesh(input, new FoliageNormalsSettings { automaticProxyTriangleBudget = false, maximumProxyTriangles = 4 }, CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => backend.Remesh(input, new FoliageNormalsSettings { remeshEdgeLength = .0001f }, CancellationToken.None));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => backend.Remesh(input, new FoliageNormalsSettings(), cancelled.Token));
            input.indices = new[] {0,1,2};
            Assert.Throws<InvalidOperationException>(() => backend.Remesh(input, new FoliageNormalsSettings(), CancellationToken.None));
            Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length); Assert.AreEqual(objects, Resources.FindObjectsOfTypeAll<GameObject>().Length);
        }

        [Test]
        public void OldPreferencesEnableRemeshDefaultsButExplicitDisabledSettingSurvives()
        {
            var settings = new FoliageNormalsSettings { automaticProxyTriangleBudget = false, remeshProxy = false }; string current = JsonUtility.ToJson(settings);
            var restored = FoliageNormalsTool.RestoreSettings(current); Assert.IsFalse(restored.remeshProxy);
            // Removing the newly added fields models the previous saved-settings schema.
            string old = current;
            foreach (string field in new[] {"remeshProxy","remeshEdgeLength","remeshIterations","remeshRelaxation","maximumProxyTriangles"})
                old = System.Text.RegularExpressions.Regex.Replace(old,"\""+field+"\":[^,}]+,?", "");
            old = old.Replace(",}","}"); restored = FoliageNormalsTool.RestoreSettings(old);
            Assert.IsTrue(restored.remeshProxy); Assert.AreEqual(5,restored.remeshIterations); Assert.AreEqual(100000,restored.maximumProxyTriangles);
            Assert.AreEqual(.5f,restored.remeshRelaxation); Assert.AreEqual(settings.sampleSpacing,restored.sampleSpacing);
            Assert.Throws<ArgumentException>(() => FoliageNormalsTool.RestoreSettings(current.Replace("\"remeshProxy\":false","\"remeshProxy\":true").Replace("\"maximumProxyTriangles\":100000","\"maximumProxyTriangles\":0")));
        }

        sealed class SphereBuilder : IFoliageProxyBuilder
        {
            public RemeshNative.IndexedMesh Build(RemeshNative.IndexedMesh primitives, int resolution, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return FoliageProxyBuilderTests.Spheres(Vector3.zero); }
        }

        [UnityTest]
        public IEnumerator RemeshedProxyCpuGpuHitsNormalsAndMissesMatch()
        {
            if (!GpuBvh.Supported) Assert.Ignore("No compute device.");
            var proxy = new FoliageProxyRemesher().Remesh(VoxelSphere(Vector3.zero), new FoliageNormalsSettings(), CancellationToken.None).mesh;
            var directions = MeshGeometry.SphereDirections(512); var points = new Vector3[directions.Length];
            for (int i = 0; i < points.Length; i++) points[i] = directions[i] * (i % 7 == 0 ? 10 : 1.1f);
            var bvh = new TriangleBvh(proxy.positions, proxy.indices); var cpu = FoliageNormalTransfer.QueryCpu(bvh, points, 2, CancellationToken.None);
            using var gpu = GpuBvh.TryCreate(bvh); Assert.IsNotNull(gpu);
            var task = FoliageNormalTransfer.QueryGpu(gpu, bvh, points, 2, CancellationToken.None); yield return FoliageNormalsToolTests.Await(task);
            int misses = 0;
            for (int i = 0; i < points.Length; i++) {
                var a = cpu[i]; var b = task.Result[i]; Assert.AreEqual(a.triangleIndex, b.triangleIndex, "query " + i);
                if (a.triangleIndex < 0) { misses++; continue; }
                Assert.Less((a.barycentric-b.barycentric).magnitude, 1e-4f); Assert.Less((a.point-b.point).magnitude, 1e-4f);
                Assert.That(b.distSq, Is.EqualTo(a.distSq).Within(1e-5));
                Assert.Greater(Vector3.Dot(FoliageNormalTransfer.Interpolate(proxy, a.triangleIndex, a.barycentric), FoliageNormalTransfer.Interpolate(proxy, b.triangleIndex, b.barycentric)), .999f);
            }
            Assert.Greater(misses, 0);
        }

        [UnityTest]
        public IEnumerator FullRemeshPipelinePreservesTransformsAndWorldNormalConversion()
        {
            var points = FoliagePrimitiveBuilder.Template(1).positions; Vector3[] baseline = null;
            for (int variant = 0; variant < 7; variant++) {
                using var fixture = new FoliageNormalTransferTests.Fixture(new[] {points[0],points[1],points[2]});
                var parent = new GameObject("Remesh root"); var grand = new GameObject("Remesh parent");
                using var pipeline = new FoliageNormalsPipeline(new SphereBuilder());
                try {
                    var child = fixture.root.transform; child.SetParent(parent.transform,false);
                    if (variant == 1) parent.transform.localRotation = Quaternion.Euler(90,0,0);
                    if (variant == 2) parent.transform.localRotation = Quaternion.Euler(-90,0,0);
                    if (variant == 3) parent.transform.localScale = Vector3.one*.01f;
                    if (variant == 4) child.localScale = new Vector3(2,.5f,3);
                    if (variant == 5) child.localScale = new Vector3(-2,3,.5f);
                    if (variant == 6) { parent.transform.SetParent(grand.transform,false); grand.transform.localScale = new Vector3(.01f,.02f,-.03f); grand.transform.localRotation = Quaternion.Euler(-90,22,14); child.localRotation = Quaternion.Euler(32,44,5); child.localScale = new Vector3(2,3,1); }
                    child.localPosition = new Vector3(.1f,.2f,.3f);
                    var inverse = (parent.transform.worldToLocalMatrix*child.localToWorldMatrix).inverse;
                    var localPoints = new[] {inverse.MultiplyPoint3x4(points[0]),inverse.MultiplyPoint3x4(points[1]),inverse.MultiplyPoint3x4(points[2])}; fixture.mesh.vertices = localPoints;
                    var transforms = new[] {child,parent.transform,grand.transform};
                    var p = Array.ConvertAll(transforms,t => t.localPosition); var r = Array.ConvertAll(transforms,t => t.localRotation);
                    var scales = Array.ConvertAll(transforms,t => t.localScale); var matrices = Array.ConvertAll(transforms,t => t.localToWorldMatrix);
                    var s = FoliageSurfaceSamplerTests.Settings(); var task = pipeline.Run(parent,s,FoliageNormalsPipeline.Stage.Transfer);
                    yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result,pipeline.Status); Assert.IsNotNull(pipeline.RemeshDiagnostics);
                    var normals = pipeline.Targets[0].working.normals;
                    if (variant == 0) baseline = Array.ConvertAll(normals,n => child.worldToLocalMatrix.transpose.MultiplyVector(n).normalized);
                    for (int i = 0; i < normals.Length; i++) {
                        var actual = child.worldToLocalMatrix.transpose.MultiplyVector(normals[i]).normalized;
                        var expected = parent.transform.worldToLocalMatrix.transpose.MultiplyVector(baseline[i]).normalized;
                        Assert.Greater(Vector3.Dot(actual,expected),.99999f,"transform variant " + variant);
                    }
                    for (int i = 0; i < transforms.Length; i++) { Assert.AreEqual(p[i],transforms[i].localPosition); Assert.AreEqual(r[i],transforms[i].localRotation); Assert.AreEqual(scales[i],transforms[i].localScale); Assert.AreEqual(matrices[i],transforms[i].localToWorldMatrix); }
                    Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh); CollectionAssert.AreEqual(localPoints,fixture.mesh.vertices);
                }
                finally { pipeline.Dispose(); fixture.root.transform.SetParent(null,true); UnityEngine.Object.DestroyImmediate(parent); UnityEngine.Object.DestroyImmediate(grand); }
            }
        }

        [UnityTest]
        public IEnumerator FailedRemeshPreventsApplyAndCanRebuildFromCachedSmooth()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline(new SphereBuilder());
            var s = FoliageSurfaceSamplerTests.Settings(); s.remeshEdgeLength = .001f;
            LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("Proxy remesh triangle budget exceeded"));
            var task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task);
            Assert.IsFalse(task.Result); Assert.IsFalse(pipeline.CanApply); Assert.IsNull(pipeline.RemeshedMesh); Assert.IsNull(pipeline.Targets[0].working);
            var smoothed = pipeline.SmoothedMesh; Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Smooth));
            Assert.Throws<InvalidOperationException>(() => pipeline.Apply()); s.remeshEdgeLength = 0;
            task = pipeline.Run(fixture.root,s,FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task);
            Assert.IsTrue(task.Result,pipeline.Status); Assert.AreSame(smoothed,pipeline.SmoothedMesh); Assert.IsTrue(pipeline.CanApply);
            Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator OversizedEnvelopeIsRejectedBeforePreviewAndSmoothing()
        {
            foreach (bool remesh in new[] { false, true })
            {
                using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline(new SphereBuilder());
                var settings = FoliageSurfaceSamplerTests.Settings(); settings.remeshProxy = remesh; settings.maximumProxyTriangles = 4;
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Proxy triangle budget exceeded"));
                var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Envelope); yield return FoliageNormalsToolTests.Await(task);
                Assert.IsFalse(task.Result); Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Scatter));
                Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Envelope)); Assert.IsNull(pipeline.Envelope); Assert.IsNull(pipeline.EnvelopeMesh);
                Assert.IsNull(pipeline.Smoothed); Assert.IsNull(pipeline.SmoothDiagnostics); Assert.IsNull(pipeline.SmoothedMesh);
                Assert.IsNull(pipeline.RemeshedMesh); Assert.IsNull(pipeline.Targets[0].working); Assert.IsFalse(pipeline.CanApply);
                Assert.AreEqual(4, settings.maximumProxyTriangles, "No automatic budget increase.");
                Assert.AreEqual(64, settings.voxelResolution, "No automatic resolution downgrade.");
                Assert.AreEqual(FoliageProxyBuilderTests.Spheres(Vector3.zero).TriangleCount, pipeline.EnvelopeBudgetExceeded.TriangleCount);
                var samples = pipeline.Samples;
                Assert.IsTrue(pipeline.IncreaseEnvelopeBudget(settings, fixture.root));
                Assert.AreEqual(64, settings.voxelResolution);
                Assert.IsNull(pipeline.EnvelopeBudgetExceeded);
                task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Envelope); yield return FoliageNormalsToolTests.Await(task);
                Assert.IsTrue(task.Result, pipeline.Status); Assert.IsTrue(pipeline.EnvelopeMesh); Assert.AreSame(samples, pipeline.Samples);
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
            }
        }

        [UnityTest]
        public IEnumerator AutomaticBudgetRepairsInvalidLimitsWithoutChangingResolutionOrCachedSamples()
        {
            foreach (int invalidBudget in new[] { 0, int.MaxValue }) {
                using var fixture = new FoliageNormalTransferTests.Fixture();
                using var pipeline = new FoliageNormalsPipeline(new SphereBuilder());
                var settings = FoliageSurfaceSamplerTests.Settings();
                settings.automaticProxyTriangleBudget = true; settings.maximumProxyTriangles = invalidBudget;
                settings.sampleRadiusVariation = .4f; settings.voxelResolution = 128;
                var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Scatter);
                yield return FoliageNormalsToolTests.Await(task);
                Assert.IsTrue(task.Result, pipeline.Status);
                var samples = pipeline.Samples;
                task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Envelope);
                yield return FoliageNormalsToolTests.Await(task);
                Assert.IsTrue(task.Result, pipeline.Status);
                Assert.AreSame(samples, pipeline.Samples);
                Assert.AreEqual(128, settings.voxelResolution);
                Assert.AreEqual(pipeline.Envelope.TriangleCount, settings.maximumProxyTriangles);
                var preview = pipeline.EnvelopeMesh;
                settings.smoothingIterations = 0;
                task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Smooth);
                yield return FoliageNormalsToolTests.Await(task);
                Assert.IsTrue(task.Result, pipeline.Status);
                Assert.AreEqual(pipeline.Envelope.TriangleCount, pipeline.Smoothed.TriangleCount);
                pipeline.InvalidateChanged(settings, fixture.root);
                Assert.AreSame(preview, pipeline.EnvelopeMesh, "Fitting the budget must not invalidate the completed proxy");
                settings.automaticProxyTriangleBudget = false; settings.maximumProxyTriangles = 4;
                pipeline.InvalidateChanged(settings, fixture.root);
                Assert.IsFalse(preview); Assert.AreSame(samples, pipeline.Samples);
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Proxy triangle budget exceeded"));
                task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Envelope);
                yield return FoliageNormalsToolTests.Await(task);
                Assert.IsFalse(task.Result); Assert.IsNull(pipeline.EnvelopeMesh);
                Assert.AreEqual(128, settings.voxelResolution);
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
            }
        }

        [Test]
        public void BudgetAndRadiusPreferencesRoundTripAndLegacyPreferencesMigrateSafely()
        {
            var settings = new FoliageNormalsSettings { automaticProxyTriangleBudget = false,
                maximumProxyTriangles = 750000, sampleRadiusVariation = .6f };
            string json = JsonUtility.ToJson(settings);
            var restored = FoliageNormalsTool.RestoreSettings(json);
            Assert.IsFalse(restored.automaticProxyTriangleBudget); Assert.AreEqual(750000, restored.maximumProxyTriangles);
            Assert.AreEqual(.6f, restored.sampleRadiusVariation);
            var snapshot = restored.Snapshot();
            Assert.IsFalse(snapshot.automaticProxyTriangleBudget); Assert.AreEqual(.6f, snapshot.sampleRadiusVariation);
            foreach (string field in new[] { "automaticProxyTriangleBudget", "sampleRadiusVariation" })
                json = System.Text.RegularExpressions.Regex.Replace(json, "\"" + field + "\":[^,}]+,?", "");
            json = json.Replace(",}", "}").Replace("\"maximumProxyTriangles\":750000", "\"maximumProxyTriangles\":0");
            restored = FoliageNormalsTool.RestoreSettings(json);
            Assert.IsTrue(restored.automaticProxyTriangleBudget); Assert.AreEqual(0, restored.sampleRadiusVariation);
            Assert.DoesNotThrow(restored.Validate);
        }

        sealed class OversizedBuilder : IFoliageProxyBuilder
        {
            public RemeshNative.IndexedMesh Build(RemeshNative.IndexedMesh primitives, int resolution, CancellationToken token) =>
                new RemeshNative.IndexedMesh { indices = new int[(FoliageNormalsSettings.HardProxyTriangleCap + 1) * 3] };
        }

        [UnityTest]
        public IEnumerator EnvelopeBudgetRecoveryRefusesHardCapAndStaleInputs()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture();
            using (var pipeline = new FoliageNormalsPipeline(new OversizedBuilder()))
            {
                var settings = FoliageSurfaceSamplerTests.Settings(); settings.automaticProxyTriangleBudget = true;
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Proxy triangle budget exceeded"));
                var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task);
                Assert.IsFalse(task.Result); Assert.IsFalse(pipeline.CanApply);
                Assert.IsFalse(pipeline.IncreaseEnvelopeBudget(settings, fixture.root));
                Assert.AreEqual(100000, settings.maximumProxyTriangles); Assert.AreEqual(64, settings.voxelResolution);
                Assert.IsNull(pipeline.EnvelopeMesh); Assert.IsNull(pipeline.Smoothed);
            }
            using (var pipeline = new FoliageNormalsPipeline(new SphereBuilder()))
            {
                var settings = FoliageSurfaceSamplerTests.Settings(); settings.maximumProxyTriangles = 4;
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Proxy triangle budget exceeded"));
                var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Envelope);
                yield return FoliageNormalsToolTests.Await(task);
                Assert.IsFalse(task.Result); settings.voxelResolution = 32;
                Assert.IsFalse(pipeline.IncreaseEnvelopeBudget(settings, fixture.root));
                Assert.AreEqual(4, settings.maximumProxyTriangles);
                pipeline.InvalidateChanged(settings, fixture.root);
                Assert.IsNull(pipeline.EnvelopeBudgetExceeded);
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
            }
        }

        [UnityTest]
        public IEnumerator ReducedTriangleBudgetInvalidatesCachedEnvelopeAndDownstreamResults()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline(new SphereBuilder());
            var settings = FoliageSurfaceSamplerTests.Settings(); settings.remeshProxy = false;
            var task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task);
            Assert.IsTrue(task.Result, pipeline.Status); var preview = pipeline.EnvelopeMesh; var samples = pipeline.Samples;
            settings.maximumProxyTriangles = 4; pipeline.InvalidateChanged(settings, fixture.root);
            Assert.IsFalse(preview); Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Envelope)); Assert.AreSame(samples, pipeline.Samples);
            Assert.IsNull(pipeline.SmoothedMesh); Assert.IsNull(pipeline.RemeshedMesh); Assert.IsFalse(pipeline.CanApply);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Proxy triangle budget exceeded"));
            task = pipeline.Run(fixture.root, settings, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task);
            Assert.IsFalse(task.Result); Assert.IsNull(pipeline.EnvelopeMesh); Assert.IsNull(pipeline.SmoothedMesh);
            Assert.Throws<InvalidOperationException>(() => pipeline.Save("Assets")); Assert.Throws<InvalidOperationException>(() => pipeline.Apply());
        }

        sealed class DirectionRemesher : IFoliageProxyRemesher
        {
            public FoliageProxyRemesher.Result Remesh(RemeshNative.IndexedMesh input, FoliageNormalsSettings settings, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); var quality = FoliageProxyRemesher.Measure(input, token);
                var mesh = new RemeshNative.IndexedMesh { positions = input.positions, indices = input.indices, normals = Array.ConvertAll(input.positions, _ => Vector3.right) };
                return new FoliageProxyRemesher.Result { mesh = mesh, diagnostics = new FoliageProxyRemesher.Diagnostics { before = quality, after = quality } };
            }
        }

        [UnityTest]
        public IEnumerator TransferUsesRemeshedNormalsAndSettingsInvalidateOnlyDownstream()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var pipeline = new FoliageNormalsPipeline(new SphereBuilder(), new DirectionRemesher());
            var s = FoliageSurfaceSamplerTests.Settings(); var task = pipeline.Run(fixture.root, s, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            foreach (var n in pipeline.Targets[0].working.normals) Assert.Greater(Vector3.Dot(n, Vector3.right), .99999f);
            var smoothed = pipeline.SmoothedMesh; var samples = pipeline.Samples; var remeshed = pipeline.RemeshedMesh; var working = pipeline.Targets[0].working;
            s.remeshIterations++; pipeline.InvalidateChanged(s, fixture.root);
            Assert.IsTrue(pipeline.Has(FoliageNormalsPipeline.Stage.Smooth)); Assert.IsFalse(pipeline.Has(FoliageNormalsPipeline.Stage.Remesh));
            Assert.AreSame(smoothed, pipeline.SmoothedMesh); Assert.AreSame(samples, pipeline.Samples); Assert.IsFalse(remeshed); Assert.IsFalse(working);
            s.remeshProxy = false; task = pipeline.Run(fixture.root, s, FoliageNormalsPipeline.Stage.Transfer); yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            Assert.AreSame(pipeline.Smoothed, pipeline.Remeshed); Assert.IsNull(pipeline.RemeshDiagnostics);
            var result = pipeline.RemeshedMesh; s.remeshEdgeLength = .2f; pipeline.InvalidateChanged(s, fixture.root); Assert.AreSame(result, pipeline.RemeshedMesh);
            pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture); Assert.IsFalse(result); Assert.IsFalse(smoothed); Assert.IsNull(pipeline.RemeshedMesh);
        }

        sealed class BlockingRemesher : IFoliageProxyRemesher, IDisposable
        {
            internal readonly ManualResetEventSlim entered = new ManualResetEventSlim(), release = new ManualResetEventSlim();
            public FoliageProxyRemesher.Result Remesh(RemeshNative.IndexedMesh input, FoliageNormalsSettings settings, CancellationToken token)
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test remesher was not released."); token.ThrowIfCancellationRequested(); return new FoliageProxyRemesher().Remesh(input, settings, token); }
            public void Dispose() { entered.Dispose(); release.Dispose(); }
        }

        [UnityTest]
        public IEnumerator DisposeDuringRemeshWaitsForWorkerAndFreesPreviews()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); using var backend = new BlockingRemesher();
            using var pipeline = new FoliageNormalsPipeline(new SphereBuilder(), backend);
            var task = pipeline.Run(fixture.root, FoliageSurfaceSamplerTests.Settings(), FoliageNormalsPipeline.Stage.Transfer);
            double end = EditorApplication.timeSinceStartup + 10;
            try {
                while (!backend.entered.IsSet && EditorApplication.timeSinceStartup < end) yield return null;
                Assert.IsTrue(backend.entered.IsSet); var smoothed = pipeline.SmoothedMesh; var original = pipeline.Targets[0].original;
                pipeline.Dispose(); Assert.IsTrue(pipeline.IsRunning); Assert.IsTrue(smoothed); Assert.IsTrue(original); Assert.IsFalse(task.IsCompleted);
                backend.release.Set(); yield return FoliageNormalsToolTests.Await(task);
                Assert.IsFalse(task.Result); Assert.IsFalse(smoothed); Assert.IsFalse(original); Assert.IsNull(pipeline.RemeshedMesh); Assert.AreEqual(0, pipeline.Targets.Count);
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { backend.release.Set(); }
        }
    }
}
