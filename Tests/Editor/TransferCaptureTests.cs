using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class TransferCaptureTests
    {
        static Mesh Quad()
        {
            var mesh = new Mesh { name = "Rectangular texture UV" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up, Vector3.up };
            mesh.uv = new[] { Vector2.zero, Vector2.right, new Vector2(1, .5f), new Vector2(0, .5f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            return mesh;
        }

        [Test]
        public void ExactUvComparison_DetectsSingleBitChangesAndSignedZero()
        {
            var value = new Vector2(.1f, .2f);
            var changed = value;
            changed.x = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value.x) ^ 1);
            Assert.IsFalse(TransferCaseReplay.SameUvBits(value, changed));
            Assert.IsTrue(TransferCaseReplay.SameUvBits(value, value));
            Assert.IsFalse(TransferCaseReplay.SameUvBits(Vector2.zero, new Vector2(BitConverter.Int32BitsToSingle(int.MinValue), 0)));
        }

        [Test]
        public void Capture_RecordsActualSettingsOfTheDerivedTransferTool()
        {
            var context = new UvToolContext { CaptureNextTransfer = true };
            var workflow = new LightmapTransferTool { SymmetrySplitMode = SymmetrySplitShells.ThresholdMode.Adaptive };
            typeof(UvTransferWorkflow).GetField("stageRunRepack", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(workflow, false);
            var capture = TransferCaseCapture.Begin(context, workflow, "Settings fixture");
            try {
                Assert.IsNotNull(capture);
                var settings = capture.Data.stages[0].settings;
                Assert.AreEqual("False", settings.Find(setting => setting.owner == "workflow" && setting.name == "stageRunRepack").value);
                Assert.AreEqual("Adaptive", settings.Find(setting => setting.owner == "workflow" && setting.name == "SymmetrySplitMode").value);
            }
            finally {
                capture?.Finish(true);
                if (capture != null && Directory.Exists(capture.Folder)) Directory.Delete(capture.Folder, true);
            }
        }

        [Test]
        public void Snapshot_PreservesFloatBitsUvDimensionsAndSubmeshes()
        {
            var mesh = Quad(); Mesh restored = null;
            try {
                mesh.SetUVs(3, new List<Vector3> { new Vector3(.123456789f, 1, 2), Vector3.one, Vector3.zero, Vector3.up });
                mesh.SetUVs(7, new List<Vector4> { new Vector4(1, 2, 3, 4), Vector4.one, Vector4.zero, new Vector4(-1, 0, .0000001f, 2) });
                mesh.subMeshCount = 2; mesh.SetTriangles(new[] { 0, 1, 2 }, 0); mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
                byte[] bytes = TransferMeshSnapshot.Capture(mesh);
                restored = TransferMeshSnapshot.Restore(bytes);
                CollectionAssert.AreEqual(bytes, TransferMeshSnapshot.Capture(restored));
                Assert.AreEqual(2, restored.subMeshCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); if (restored) UnityEngine.Object.DestroyImmediate(restored); }
        }

        [Test]
        public void Snapshot_RejectsTruncationAndUnsupportedVersion()
        {
            var mesh = Quad();
            try {
                var bytes = TransferMeshSnapshot.Capture(mesh);
                var truncated = new byte[bytes.Length - 3]; Array.Copy(bytes, truncated, truncated.Length);
                Assert.Catch<IOException>(() => TransferMeshSnapshot.Restore(truncated));
                bytes[4] = 99;
                Assert.Throws<InvalidDataException>(() => TransferMeshSnapshot.Restore(bytes));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase("../outside")]
        [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        public void Replay_RejectsInvalidMeshNames(string name)
            => Assert.Throws<InvalidDataException>(() => TransferCaseReplay.Verify(Path.GetTempPath(), name));

        [Test]
        public void Quality_DetectsAxisStretchEvenWhenUvAreaIsUnchanged()
        {
            var mesh = Quad();
            try {
                var raw = TransferUvQuality.Measure(mesh, mesh.uv, Vector2.one, Matrix4x4.identity);
                var pixels = TransferUvQuality.Measure(mesh, mesh.uv, new Vector2(Mathf.Sqrt(.5f), Mathf.Sqrt(2)), Matrix4x4.identity);
                Assert.AreEqual(2, raw.worstAnisotropy, 1e-5);
                Assert.AreEqual(1, pixels.worstAnisotropy, 1e-5);
                Assert.AreEqual(0, raw.overlapPairs);
                Assert.IsTrue(raw.overlapScanComplete);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Quality_CountsPositiveAreaOverlapWithinTheSameShell()
        {
            var mesh = Quad();
            try {
                mesh.triangles = new[] { 0, 1, 2, 0, 1, 2 };
                var quality = TransferUvQuality.Measure(mesh, mesh.uv, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(1, quality.overlapPairs);
                Assert.Greater(quality.overlapPairArea, 0);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Metric_IncludesTilingAndDeclinesConflictingMaterials()
        {
            var go = new GameObject("Texture metrics");
            var texture = new Texture2D(1024, 2048);
            var material = new Material(Shader.Find("Unlit/Texture"));
            var other = new Material(material);
            try {
                var renderer = go.AddComponent<MeshRenderer>();
                material.mainTexture = texture; material.mainTextureScale = new Vector2(2, 1);
                renderer.sharedMaterial = material;
                var metric = SourceTextureUvMetric.Resolve(renderer);
                Assert.AreEqual(Vector2.one, metric.uvScale);
                other.mainTexture = texture; other.mainTextureScale = Vector2.one;
                renderer.sharedMaterials = new[] { material, other };
                metric = SourceTextureUvMetric.Resolve(renderer);
                Assert.IsTrue(metric.conflictingAspects);
                Assert.AreEqual(Vector2.one, metric.uvScale);
            }
            finally {
                UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(other); UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [TestCase(true, 1f)]
        [TestCase(false, 2f)]
        public void Workflow_RectangularTextureProducesSquareMetricUv2WithoutChangingUv0(bool correction, float expectedStretch)
        {
            var source = Quad(); var target = Quad();
            var go = new GameObject("Source");
            var texture = new Texture2D(1024, 2048);
            var material = new Material(Shader.Find("Unlit/Texture"));
            var context = new UvToolContext(); var canvas = new UvCanvasView(); var workflow = new UvTransferWorkflow();
            MeshEntry entry = null;
            try {
                try { XatlasNative.xatlasCreate(); XatlasNative.xatlasDestroy(); }
                catch (DllNotFoundException) { Assert.Ignore("xatlas native plugin unavailable."); }
                var renderer = go.AddComponent<MeshRenderer>(); material.mainTexture = texture; renderer.sharedMaterial = material;
                var primary = new List<Vector3> { Vector3.zero, Vector3.right, new Vector3(1, .5f, 7), new Vector3(0, .5f, -3) };
                source.SetUVs(0, primary);
                entry = new MeshEntry { renderer = renderer, originalMesh = source, fbxMesh = source, meshGroupKey = "Source" };
                workflow.OnActivate(context, canvas);
                context.MeshEntries.Add(entry); context.RepackResolutionMode = ResolutionMode.Manual; context.AtlasResolution = 128;
                context.ShellPaddingPx = 2; context.BorderPaddingPx = 0; context.NormalizeTexelDensity = false;
                context.ReparameterizeStretchedShells = false; context.CorrectSourceTextureAspect = correction;
                var original = source.uv;
                var method = typeof(UvTransferWorkflow).GetMethod("ExecRepackCoreImpl", BindingFlags.Instance | BindingFlags.NonPublic);
                ((Task)method.Invoke(workflow, new object[] { new List<MeshEntry> { entry }, false })).GetAwaiter().GetResult();
                Assert.IsNotNull(entry.repackedMesh);
                CollectionAssert.AreEqual(original, source.uv);
                CollectionAssert.AreEqual(original, entry.repackedMesh.uv);
                var preserved = new List<Vector3>(); entry.repackedMesh.GetUVs(0, preserved);
                CollectionAssert.AreEqual(primary, preserved);
                var packed = TransferUvQuality.Measure(entry.repackedMesh, entry.repackedMesh.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(expectedStretch, packed.worstAnisotropy, .05);
                var result = GroupedShellTransfer.Transfer(target, entry.repackedMesh, sourceAtlasWidth: (int)entry.repackedAtlasWidth,
                    sourceAtlasHeight: (int)entry.repackedAtlasHeight);
                Assert.IsNotNull(result.uv2);
                var transferred = TransferUvQuality.Measure(target, result.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(expectedStretch, transferred.worstAnisotropy, .05);
            }
            finally {
                workflow.OnDeactivate(); canvas.Cleanup();
                if (entry?.repackedMesh) UnityEngine.Object.DestroyImmediate(entry.repackedMesh);
                UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [UnityTest]
        public IEnumerator Capture_ReplaysExactInputsAndCrossLodHintsTwice()
        {
            var sourceMesh = Quad(); var targetMesh = Quad(); var context = new UvToolContext();
            TransferCaseCapture capture = null; Mesh output = null;
            try {
                sourceMesh.uv2 = new[] { new Vector2(.1f, .1f), new Vector2(.3f, .1f), new Vector2(.3f, .3f), new Vector2(.1f, .3f) };
                var source = new MeshEntry { originalMesh = sourceMesh, fbxMesh = sourceMesh, repackedMesh = sourceMesh, repackedAtlasWidth = 128, repackedAtlasHeight = 128 };
                var target = new MeshEntry { originalMesh = targetMesh, fbxMesh = targetMesh, lodIndex = 1 };
                context.MeshEntries.Add(source); context.MeshEntries.Add(target); context.CaptureNextTransfer = true;
                capture = TransferCaseCapture.Begin(context, null, "Test exact capture");
                Assert.IsNotNull(capture);
                var noTrace = GroupedShellTransfer.Transfer(targetMesh, sourceMesh, sourceAtlasWidth: 128, sourceAtlasHeight: 128);
                var pair = capture.BeforePair(source, target, sourceMesh, targetMesh, null, null);
                var traced = GroupedShellTransfer.TransferWithDiagnostics(targetMesh, sourceMesh, null, null, 128, 128, pair.trace);
                Assert.AreEqual(TransferMeshSnapshot.UvHash(noTrace.uv2), TransferMeshSnapshot.UvHash(traced.uv2));
                CollectionAssert.AreEqual(noTrace.targetShellToSourceShell, traced.targetShellToSourceShell);
                output = UnityEngine.Object.Instantiate(targetMesh); output.uv2 = traced.uv2; target.transferredMesh = output;
                capture.AfterPair(pair, target, traced);
                target.lodIndex = 2;
                var next = capture.BeforePair(source, target, sourceMesh, targetMesh, traced.overlapHints, traced.matchHints);
                var second = GroupedShellTransfer.TransferWithDiagnostics(targetMesh, sourceMesh, next.overlapHints, next.matchHints, 128, 128, next.trace);
                output.uv2 = second.uv2; capture.AfterPair(next, target, second); capture.Finish(true);
                var task = TransferCaseReplay.Replay(Path.Combine(capture.Folder, "manifest.json"));
                while (!task.IsCompleted) yield return null;
                var report = task.GetAwaiter().GetResult();
                Assert.IsTrue(report.complete, report.error); Assert.AreEqual(2, report.pairs.Count);
                foreach (var replay in report.pairs) {
                    Assert.IsNull(replay.error); Assert.IsTrue(replay.baselineEqual); Assert.IsTrue(replay.repeatEqual);
                    Assert.AreEqual(0, replay.changedMappings); Assert.Greater(replay.trace.shells.Count, 0);
                }
                Assert.Greater(next.matchHints.Count, 0);
                string blob = Path.Combine(capture.Folder, "meshes", pair.sourceMesh + ".bin");
                var bytes = File.ReadAllBytes(blob); bytes[bytes.Length - 1] ^= 1; File.WriteAllBytes(blob, bytes);
                Assert.Throws<InvalidDataException>(() => TransferCaseReplay.Verify(capture.Folder, pair.sourceMesh));
            }
            finally {
                capture?.Finish(true);
                if (output) UnityEngine.Object.DestroyImmediate(output);
                UnityEngine.Object.DestroyImmediate(sourceMesh); UnityEngine.Object.DestroyImmediate(targetMesh);
                if (capture != null && Directory.Exists(capture.Folder)) Directory.Delete(capture.Folder, true);
            }
        }
    }
}
