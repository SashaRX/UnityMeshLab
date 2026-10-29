using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ForwardSurfaceRecoveryTests
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
                    .Concat(uv.Select(p => Vector2.Scale(p, new Vector2(.4f, .2f)) + new Vector2(.55f, .05f))).ToArray(),
                colors = Enumerable.Range(0, 8).Select(i => new Color(i, i * .2f, 0, .5f)).ToArray()
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

        Vector2[] OverlappingUv() => source.uv2.Take(4).Concat(source.uv2.Take(4)).ToArray();

        [Test]
        public void SeamPreparationReplacesOverlappingIsotropicFacesWithValidOrientedDonors()
        {
            var legacy = OverlappingUv();
            var sourceBytes = TransferMeshSnapshot.Capture(source);
            var targetBytes = TransferMeshSnapshot.Capture(target);
            var before = TransferUvQuality.Measure(target, legacy, Vector2.one, Matrix4x4.identity);
            Assert.Greater(before.overlapPairs, 0);
            Assert.That(before.worstAnisotropy, Is.EqualTo(1).Within(1e-5));
            ForwardUvReference reference = null;
            var result = ReverseUvTransfer.PrepareForwardSeamsAsync(source, target, legacy, new int[8], 256, false,
                captureReference: value => reference = value).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.NotNull(output);
            var after = TransferUvQuality.Measure(output, output.uv2, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, after.overlapPairs + after.degenerateFaces + after.invalidFaces);
            Assert.That(after.worstAnisotropy, Is.EqualTo(2).Within(1e-5));
            Assert.IsTrue(reference.Matches(output));
            CollectionAssert.AreEqual(target.triangles.Select(i => target.vertices[i]), output.triangles.Select(i => output.vertices[i]));
            CollectionAssert.AreEqual(target.triangles.Select(i => target.colors[i]), output.triangles.Select(i => output.colors[i]));
            CollectionAssert.AreEqual(target.triangles.Select(i => target.normals[i]), output.triangles.Select(i => output.normals[i]));
            CollectionAssert.AreEqual(target.triangles.Select(i => target.uv[i]), output.triangles.Select(i => output.uv[i]));
            CollectionAssert.AreEqual(sourceBytes, TransferMeshSnapshot.Capture(source));
            CollectionAssert.AreEqual(targetBytes, TransferMeshSnapshot.Capture(target));
            for (int c = 0; c < output.triangles.Length; ++c)
            {
                Assert.That(Vector2.Distance(source.uv2[source.triangles[c]], output.uv2[output.triangles[c]]), Is.LessThan(1e-5));
            }
        }

        [Test]
        public void SharedRecoveryAndReusedMeasuresMatchIndependentPreparation()
        {
            var legacy = OverlappingUv();
            ForwardUvReference independentReference = null, sharedReference = null;
            TransferUvQuality captured = null;
            var profile = new TransferPerformanceProfile();
            var independent = ReverseUvTransfer.PrepareForwardSeamsAsync(source, target, legacy, new int[8], 256, false,
                shareSeamPasses: false, captureReference: value => independentReference = value).GetAwaiter().GetResult();
            Mesh shared = null;
            try
            {
                var legacyQuality = TransferUvQuality.Measure(target, legacy, Vector2.one, Matrix4x4.identity);
                var result = ReverseUvTransfer.PrepareForwardSeamsAsync(source, target, legacy, new int[8], 256, false,
                    performance: profile, captureReference: value => sharedReference = value,
                    legacyQuality: legacyQuality, captureQuality: value => captured = value).GetAwaiter().GetResult();
                shared = result.mesh;
                Assert.NotNull(independent.mesh);
                Assert.NotNull(shared);
                CollectionAssert.AreEqual(TransferMeshSnapshot.Capture(independent.mesh), TransferMeshSnapshot.Capture(shared));
                CollectionAssert.AreEqual(independent.sources, result.sources);
                Assert.IsTrue(independentReference.Matches(shared) && sharedReference.Matches(independent.mesh));
                Assert.AreEqual(JsonUtility.ToJson(TransferUvQuality.Measure(shared, shared.uv2, Vector2.one, Matrix4x4.identity)),
                    JsonUtility.ToJson(captured), "The returned measure is the measure of the returned mesh.");
                long Count(string stage) => profile.samples.Where(s => s.stage == stage).Sum(s => s.count);
                Assert.LessOrEqual(Count("Forward seam quality measures"), Count("Forward seam passes"),
                    "Each prepared mesh is measured once, and the supplied legacy measure is not repeated.");
                Assert.AreEqual(1, Count("Projection donor charts"), "Recovery reuses the donor charts of the seam passes.");
            }
            finally
            {
                if (independent.mesh) Object.DestroyImmediate(independent.mesh);
                if (shared) Object.DestroyImmediate(shared);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnsupportedSurfaceCannotPublishAPartlyRepairedAtlas(bool opposed)
        {
            if (opposed) {
                var indices = target.triangles;
                for (int i = 6; i < indices.Length; i += 3) (indices[i], indices[i + 1]) = (indices[i + 1], indices[i]);
                target.triangles = indices;
            }
            else {
                var positions = target.vertices;
                for (int i = 4; i < positions.Length; ++i) positions[i] += Vector3.forward;
                target.vertices = positions;
            }
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var result = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, OverlappingUv(), new int[8],
                256, false, Matrix4x4.identity).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.IsNull(output);
            Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator AsyncRecoveryMatchesSynchronousRecovery()
        {
            var legacy = OverlappingUv();
            var sync = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, legacy, new int[8],
                256, false, Matrix4x4.identity).GetAwaiter().GetResult();
            output = sync.mesh;
            Assert.NotNull(output);
            var task = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, legacy, new int[8],
                256, true, Matrix4x4.identity);
            while (!task.IsCompleted) yield return null;
            var asyncResult = task.GetAwaiter().GetResult();
            try {
                Assert.NotNull(asyncResult.mesh);
                CollectionAssert.AreEqual(TransferMeshSnapshot.Capture(output), TransferMeshSnapshot.Capture(asyncResult.mesh));
                CollectionAssert.AreEqual(sync.sources, asyncResult.sources);
                Assert.IsTrue(asyncResult.reference.Matches(asyncResult.mesh));
            }
            finally {if (asyncResult.mesh) Object.DestroyImmediate(asyncResult.mesh);}
        }

        [Test]
        public void RecoveryKeepsAHealthyFacesMetricBound()
        {
            var positions = source.vertices;
            var indices = source.triangles;
            var uv = source.uv;
            var lightmap = source.uv2;
            source.vertices = positions.Concat(positions.Take(4).Select(p => p + Vector3.right * 6)).ToArray();
            source.triangles = indices.Concat(indices.Take(6).Select(i => i + 8)).ToArray();
            source.uv = uv.Concat(uv.Take(4).Select(p => p + Vector2.right * 6)).ToArray();
            source.uv2 = lightmap.Concat(uv.Take(4).Select(p => Vector2.Scale(p, new Vector2(.3f, .1f)) + new Vector2(.05f, .55f))).ToArray();
            source.RecalculateNormals();
            Object.DestroyImmediate(target);
            target = Object.Instantiate(source);
            var legacy = OverlappingUv().Concat(uv.Take(4).Select(p => p * .4f + new Vector2(.55f, .55f))).ToArray();
            var result = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, legacy, new int[12],
                256, false, Matrix4x4.identity).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.NotNull(output);
            var actual = output.uv2;
            for (int corner = 12; corner < output.triangles.Length; ++corner)
            {
                Assert.AreEqual(legacy[target.triangles[corner]], actual[output.triangles[corner]], "An unaffected face cannot gain stretch.");
            }
        }

        [Test]
        public void RecoveryBudgetRefusalKeepsThePreparedMesh()
        {
            var legacy = OverlappingUv();
            var before = TransferMeshSnapshot.Capture(target);
            var trace = new TransferMatchTrace();
            ForwardUvReference reference = null;
            var result = ReverseUvTransfer.PrepareForwardSeamsAsync(source, target, legacy, new int[8], 256, false,
                trace: trace, captureReference: value => reference = value, recoveryComparisonBudget: 1).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.NotNull(output, "A refused optional recovery must not destroy the prepared result.");
            Assert.IsTrue(reference.Matches(output));
            Assert.Greater(TransferUvQuality.Measure(output, output.uv2, Vector2.one, Matrix4x4.identity).overlapPairs, 0);
            StringAssert.Contains("Oriented surface recovery:", trace.forwardSeamRefusal);
            StringAssert.Contains("budget", trace.forwardSeamRefusal);
            CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(target));
        }

        [Test]
        public void HealthyAtlasIsLeftUntouched()
        {
            var report = new ReverseUvTransfer.ForwardRescueReport();
            var result = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, source.uv2, new int[8],
                256, false, Matrix4x4.identity, report: report).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.IsNull(output);
            Assert.AreEqual("no defective face", report.refusal);
        }

        Vector2[] CollapsedSecondQuad() => source.uv2.Take(4).Concat(Enumerable.Repeat(Vector2.zero, 4)).ToArray();

        /// <summary>
        /// A skinned target keeps its triangles: the collapsed quad is projected onto
        /// its donor, only UV seams split, the healthy quad and the skinning stay.
        /// </summary>
        [Test]
        public void KeptFacesRescueACollapsedShellWithoutNewFaces()
        {
            target.bindposes = new[] {Matrix4x4.identity, Matrix4x4.identity};
            target.boneWeights = Enumerable.Range(0, 8).Select(i => new BoneWeight {boneIndex0 = i / 4, weight0 = 1}).ToArray();
            var legacy = CollapsedSecondQuad();
            var report = new ReverseUvTransfer.ForwardRescueReport();
            var result = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, legacy, new int[8],
                256, false, Matrix4x4.identity, keepFaces: true, report: report).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.NotNull(output);
            Assert.IsNull(report.refusal);
            Assert.AreEqual((2, 2, 2, 0), (report.defective, report.projected, report.rescued, report.touching));
            Assert.AreEqual("rescued 2 of 2 defective face(s)", report.ToString());
            Assert.AreEqual(0, result.quality.degenerateFaces + result.quality.overlapPairs);
            Assert.IsTrue(result.reference.Matches(output));
            CollectionAssert.AreEqual(target.triangles.Select(i => target.vertices[i]), output.triangles.Select(i => output.vertices[i]),
                "The faces are the target's, corner for corner.");
            CollectionAssert.AreEqual(target.triangles.Select(i => target.boneWeights[i].boneIndex0),
                output.triangles.Select(i => output.boneWeights[i].boneIndex0));
            for (int corner = 0; corner < 6; ++corner)
                Assert.AreEqual(legacy[target.triangles[corner]], output.uv2[output.triangles[corner]], "A healthy face keeps its UVs.");
            for (int corner = 6; corner < 12; ++corner)
                Assert.That(Vector2.Distance(source.uv2[source.triangles[corner]], output.uv2[output.triangles[corner]]), Is.LessThan(1e-5));
        }

        /// <summary>A rescue landing on another face is undone; with nothing left there is no result.</summary>
        [Test]
        public void KeptFacesAreNeverRescuedOntoAnotherFace()
        {
            var positions = target.vertices;
            for (int i = 4; i < 8; ++i) positions[i] = positions[i - 4];
            target.vertices = positions;
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var report = new ReverseUvTransfer.ForwardRescueReport();
            var result = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, CollapsedSecondQuad(), new int[8],
                256, false, Matrix4x4.identity, keepFaces: true, report: report).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.IsNull(output, "The collapsed copy projects onto the healthy quad it duplicates.");
            Assert.AreEqual((2, 2, 0, 2), (report.defective, report.projected, report.rescued, report.touching));
            Assert.AreEqual("no defective face could be rescued", report.refusal);
            StringAssert.EndsWith("rescued 0 of 2 defective face(s) (2 touching another face)", report.ToString(),
                "The log says why the collapsed faces stay.");
            Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length);
        }

        /// <summary>A zero-area triangle, which cleanup leaves out of the projection, keeps its UVs.</summary>
        [Test]
        public void KeptFacesKeepTheZeroAreaTrianglesCleanupDrops()
        {
            target.triangles = target.triangles.Concat(new[] {0, 1, 1}).ToArray();
            var legacy = CollapsedSecondQuad();
            var result = ReverseUvTransfer.RecoverForwardSurfaceAsync(source, target, legacy, new int[8],
                256, false, Matrix4x4.identity, keepFaces: true).GetAwaiter().GetResult();
            output = result.mesh;
            Assert.NotNull(output);
            Assert.IsTrue(UvTransferWorkflow.TrialImproves(result.quality,
                TransferUvQuality.Measure(target, legacy, Vector2.one, Matrix4x4.identity), true),
                "The workflow publishes the rescue although the zero-area triangle stays degenerate.");
            CollectionAssert.AreEqual(target.triangles.Select(i => target.vertices[i]), output.triangles.Select(i => output.vertices[i]));
            for (int corner = 12; corner < 15; ++corner)
                Assert.AreEqual(legacy[target.triangles[corner]], output.uv2[output.triangles[corner]]);
        }

        [Test]
        public void CancelledRecoveryDoesNotAllocateWorkingMeshes()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            Assert.Catch<System.OperationCanceledException>(() => ReverseUvTransfer.RecoverForwardSurfaceAsync(
                source, target, OverlappingUv(), new int[8], 256, false, Matrix4x4.identity, cancellation.Token).GetAwaiter().GetResult());
            Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length);
        }
    }
}
