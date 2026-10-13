using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageNormalRelaxationTests
    {
        static FoliageNormalsSettings Settings() => new FoliageNormalsSettings
        {
            relaxTransferredNormals = true, normalRelaxIterations = 1, normalRelaxStrength = 1,
            gpuProjection = false, selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 0 }
        };

        static readonly Vector3[] Directions = { Vector3.right, Vector3.up, Vector3.forward };

        static FoliageNormalTransfer.Result Resolve(FoliageTargetSnapshot target, FoliageNormalsSettings settings, int miss = -1)
        {
            var proxy = FoliageNormalTransferTests.Plane(); proxy.normals = Directions;
            var selected = Enumerable.Range(0, target.selectedVertices.Length).Where(v => target.selectedVertices[v]).ToArray();
            var hits = selected.Select(v => new TriangleBvh.HitResult
            {
                triangleIndex = v == miss ? -1 : 0, distSq = 0, point = Vector3.zero, barycentric = Directions[v % 3]
            }).ToArray();
            return FoliageNormalTransfer.Resolve(target, proxy, selected, hits, settings, 10, CancellationToken.None);
        }

        [Test]
        public void OnePassAveragesDirectionsWithoutChangingGeometryAndIsDeterministic()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var settings = Settings();
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var fingerprint = FoliageTargetSnapshot.ContentFingerprint(fixture.mesh);
            var result = Resolve(target, settings); var repeated = Resolve(target, settings);
            var expected = (Vector3.right + Vector3.up + Vector3.forward).normalized;
            foreach (int v in target.selectedIndices) Assert.Greater(Vector3.Dot(expected, result.normals[v]), .99999f);
            CollectionAssert.AreEqual(result.normals, repeated.normals);
            Assert.AreEqual(3, result.diagnostics.transferred); Assert.AreEqual(0, result.diagnostics.misses);
            Assert.AreEqual(fingerprint, FoliageTargetSnapshot.ContentFingerprint(fixture.mesh));
            CollectionAssert.AreEqual(fixture.mesh.vertices, target.localPositions);
        }

        [Test]
        public void StrengthAndAdditionalPassesReduceAngularVariation()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var settings = Settings(); settings.normalRelaxStrength = .25f;
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var once = Resolve(target, settings).normals;
            var expected = Vector3.Lerp(Vector3.right, Vector3.one.normalized, .25f).normalized;
            Assert.Greater(Vector3.Dot(expected, once[0]), .99999f);
            settings.normalRelaxIterations = 5;
            var repeated = Resolve(target, settings).normals;
            Assert.Greater(Vector3.Dot(repeated[0], repeated[1]), Vector3.Dot(once[0], once[1]));
            foreach (var direction in repeated) Assert.That(direction.magnitude, Is.EqualTo(1).Within(1e-5));
        }

        [TestCase("disabled")] [TestCase("strength")] [TestCase("iterations")] [TestCase("blend")]
        public void DisabledOrZeroSettingsRetainTheUnrelaxedOutputExactly(string mode)
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var settings = Settings();
            if (mode == "blend") settings.transferBlend = 0;
            settings.relaxTransferredNormals = false;
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var before = Resolve(target, settings).normals;
            settings.relaxTransferredNormals = mode != "disabled";
            if (mode == "strength") settings.normalRelaxStrength = 0;
            if (mode == "iterations") settings.normalRelaxIterations = 0;
            CollectionAssert.AreEqual(before, Resolve(target, settings).normals);
        }

        [Test]
        public void MissesAndExcludedFacesDoNotContributeAndBoundaryCopiesKeepOriginalChannels()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var settings = Settings();
            fixture.mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            fixture.mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 4).ToArray();
            fixture.mesh.colors32 = new[] { new Color32(1, 2, 3, 4), new Color32(5, 6, 7, 8), new Color32(9, 10, 11, 12), new Color32(13, 14, 15, 16) };
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var result = Resolve(target, settings, 1);
            Assert.AreEqual(Vector3.forward, result.normals[1]); Assert.AreEqual(Vector3.forward, result.normals[3]);
            var expected = (Vector3.right + Vector3.forward).normalized;
            Assert.Greater(Vector3.Dot(expected, result.normals[0]), .99999f);
            Assert.Greater(Vector3.Dot(expected, result.normals[2]), .99999f);
            Assert.AreEqual(1, result.diagnostics.misses); Assert.AreEqual(2, result.diagnostics.transferred);
            var output = target.BuildResult(result.normals, true, CancellationToken.None, out int duplicated);
            try
            {
                Assert.AreEqual(2, duplicated);
                CollectionAssert.AreEqual(fixture.mesh.GetIndices(1), output.GetIndices(1));
                foreach (int v in output.GetIndices(1))
                {
                    Assert.AreEqual(fixture.mesh.normals[v], output.normals[v]);
                    Assert.AreEqual(fixture.mesh.tangents[v], output.tangents[v]);
                }
                var selected = output.GetIndices(0);
                for (int corner = 0; corner < selected.Length; corner++)
                {
                    int v = selected[corner];
                    Assert.AreEqual(fixture.mesh.vertices[corner], output.vertices[v]);
                    Assert.AreEqual(fixture.mesh.uv[corner], output.uv[v]);
                    Assert.AreEqual(fixture.mesh.colors32[corner], output.colors32[v]);
                    Assert.Greater(Vector3.Dot(result.normals[corner], output.normals[v]), .99999f);
                    Assert.That(Vector3.Dot((Vector3)output.tangents[v], output.normals[v]), Is.EqualTo(0).Within(1e-5));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(output); }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void RelaxationUsesCaptureSpaceUnderNonuniformMirroredAndShearedTransforms(int variant)
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var settings = Settings();
            fixture.root.transform.localScale = variant == 0 ? new Vector3(2, .5f, 3) : new Vector3(-2, 3, .5f);
            fixture.root.transform.localRotation = Quaternion.Euler(32, 44, 5);
            var captureFrame = variant == 2 ? Matrix4x4.TRS(Vector3.one, Quaternion.Euler(-90, 22, 14), new Vector3(.01f, .02f, -.03f)).inverse : Matrix4x4.identity;
            using var target = new FoliageTargetSnapshot(fixture.renderer, captureFrame, settings, CancellationToken.None);
            var result = Resolve(target, settings);
            foreach (int v in target.selectedIndices)
            {
                var actual = target.normalLocalToCapture.MultiplyVector(result.normals[v]).normalized;
                Assert.Greater(Vector3.Dot(Vector3.one.normalized, actual), .99999f);
            }
            Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void DisconnectedFacesDoNotMixAndDuplicateEdgesDoNotBiasDirections()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one * 10, Vector3.right * 11, Vector3.up * 11 });
            fixture.mesh.subMeshCount = 1; fixture.mesh.SetTriangles(new[] { 0, 1, 2, 0, 1, 2, 3, 4, 5 }, 0);
            var settings = Settings();
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var normals = new[] { Vector3.right, Vector3.up, Vector3.forward, Vector3.left, Vector3.left, Vector3.left };
            FoliageNormalRelaxation.Apply(target, normals, Enumerable.Repeat(true, 6).ToArray(), settings, CancellationToken.None);
            for (int v = 0; v < 3; v++) Assert.Greater(Vector3.Dot(Vector3.one.normalized, normals[v]), .99999f);
            for (int v = 3; v < 6; v++) Assert.AreEqual(Vector3.left, normals[v]);
        }

        [Test]
        public void OppositeDirectionsKeepTheirValidFrameAndHemisphereAlignmentRemainsEnabled()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var settings = Settings(); settings.normalRelaxIterations = 100;
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var opposite = new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.forward };
            var before = (Vector3[])opposite.Clone();
            FoliageNormalRelaxation.Apply(target, opposite, new[] { true, true, false, false }, settings, CancellationToken.None);
            CollectionAssert.AreEqual(before, opposite);
            settings.alignToOriginalHemisphere = true;
            var proxy = FoliageNormalTransferTests.Plane(); proxy.normals = Enumerable.Repeat(Vector3.back, 3).ToArray();
            var hits = FoliageNormalTransfer.QueryCpu(new TriangleBvh(proxy.positions, proxy.indices), target.positions.Take(3).ToArray(), 2, CancellationToken.None);
            var aligned = FoliageNormalTransfer.Resolve(target, proxy, new[] { 0, 1, 2 }, hits, settings, 2, CancellationToken.None);
            foreach (var direction in aligned.normals) Assert.GreaterOrEqual(Vector3.Dot(direction, Vector3.forward), 0);
        }

        [Test]
        public void CancellationStopsBeforePublishingNormals()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture(); var settings = Settings();
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var normals = (Vector3[])target.localNormals.Clone(); var before = (Vector3[])normals.Clone();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => FoliageNormalRelaxation.Apply(target, normals, new[] { true, true, true, false }, settings, cancellation.Token));
            CollectionAssert.AreEqual(before, normals);
        }

        [Test]
        public void PreferencesRetainDefaultsAndValidateOnlyActiveRelaxSettings()
        {
            var legacy = FoliageNormalsTool.RestoreSettings("{}");
            Assert.IsFalse(legacy.relaxTransferredNormals); Assert.AreEqual(3, legacy.normalRelaxIterations); Assert.AreEqual(.5f, legacy.normalRelaxStrength);
            var settings = Settings(); settings.normalRelaxIterations = 9; settings.normalRelaxStrength = .7f;
            var restored = FoliageNormalsTool.RestoreSettings(JsonUtility.ToJson(settings)); var snapshot = restored.Snapshot();
            Assert.IsTrue(snapshot.relaxTransferredNormals); Assert.AreEqual(9, snapshot.normalRelaxIterations); Assert.AreEqual(.7f, snapshot.normalRelaxStrength);
            settings.normalRelaxIterations = -1; Assert.Throws<ArgumentException>(settings.Validate);
            settings.normalRelaxIterations = 101; Assert.Throws<ArgumentException>(settings.Validate);
            settings.normalRelaxIterations = 1; settings.normalRelaxStrength = float.NaN; Assert.Throws<ArgumentException>(settings.Validate);
            settings.normalRelaxStrength = 1.1f; Assert.Throws<ArgumentException>(settings.Validate);
            settings.relaxTransferredNormals = false; Assert.DoesNotThrow(settings.Validate);
        }
    }
}
