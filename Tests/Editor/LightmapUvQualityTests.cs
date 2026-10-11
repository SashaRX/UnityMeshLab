using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class LightmapUvQualityTests
    {
        static LightmapUvQuality.Part Rectangle(float width = 1, float height = 1)
            => new LightmapUvQuality.Part { key = "panel", positions = new[] { Vector3.zero, new Vector3(width, 0, 0),
                new Vector3(width, height, 0), new Vector3(0, height, 0) },
                uv = new[] { Vector2.zero, new Vector2(width, 0), new Vector2(width, height), new Vector2(0, height) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 } };

        [TestCase(1f, 1f)] [TestCase(2f, 1f)] [TestCase(1f, .5f)]
        public void FullRectangleDoesNotNeedToFillUnitSquare(float width, float height)
        {
            var r = LightmapUvQuality.Measure(new[] { Rectangle(width, height) });
            Assert.AreEqual(100, r.score); Assert.AreEqual(1, r.fill); Assert.AreEqual(0, r.largestEmptyRectangle);
            Assert.AreEqual(1, r.worstStretch, 1e-6); Assert.AreEqual(-1, r.lodScore);
        }

        [TestCase(.001f)] [TestCase(.2f)] [TestCase(20f)]
        public void GlobalUvSimilarityDoesNotChangeAssessment(float scale)
        {
            var part = Rectangle(2, 1); var baseline = LightmapUvQuality.Measure(new[] { part });
            part.uv = part.uv.Select(v => v * scale + new Vector2(.3f, -.7f)).ToArray();
            var r = LightmapUvQuality.Measure(new[] { part });
            Assert.AreEqual(baseline.score, r.score); Assert.AreEqual(baseline.fill, r.fill, 1e-3);
            Assert.AreEqual(baseline.worstStretch, r.worstStretch, 1e-4);
        }

        [Test]
        public void LargeInternalVoidReducesPackingAndAppearsOnMap()
        {
            var left = Rectangle(); var right = Rectangle();
            right.uv = right.uv.Select(v => v + new Vector2(4, 0)).ToArray();
            var r = LightmapUvQuality.Measure(new[] { left, right });
            Assert.AreEqual(.4, r.fill, .01); Assert.AreEqual(.6, r.largestEmptyRectangle, .01);
            Assert.Less(r.packingScore, 30); Assert.AreEqual(100, r.stretchScore);
        }

        [TestCase(300f)] [TestCase(1000000f)]
        public void CapturedViewerMetricMatchesExportAuditBeforeWorldRounding(float offset)
        {
            var mesh = new Mesh();
            try {
                mesh.vertices = new[] { new Vector3(40.123456f,23.23456f,1),
                    new Vector3(40.234567f,23.45678f,1), new Vector3(40.34567f,23.67891f,1) };
                mesh.triangles = new[] { 0,1,2 };
                mesh.uv2 = new[] { new Vector2(.23f,.47f), new Vector2(.2303f,.4706f), new Vector2(.2306f,.47123f) };
                var before = TransferMeshSnapshot.Capture(mesh);
                var matrix = Matrix4x4.TRS(new Vector3(offset,offset,-offset),Quaternion.Euler(18,27,9),new Vector3(2,.3f,-1));
                var part = UvToolHub.CaptureQualityPart(mesh,null,matrix,1);
                var viewer = LightmapUvQuality.Measure(new[] { part });
                var exported = TransferUvQuality.Measure(mesh,mesh.uv2,Vector2.one,matrix);
                Assert.AreEqual(0,viewer.invalidFaces + viewer.collapsedFaces + viewer.zeroGeometryFaces);
                Assert.AreEqual(exported.worstAnisotropy,viewer.worstStretch,1e-9);
                CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(mesh));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase(0)] [TestCase(1)]
        public void StretchDetectsEitherAxisAndDoesNotConfuseDensityWithShape(int axis)
        {
            var part = Rectangle();
            part.uv = part.uv.Select(v => axis == 0 ? new Vector2(v.x * 8, v.y) : new Vector2(v.x, v.y * 8)).ToArray();
            var r = LightmapUvQuality.Measure(new[] { part });
            Assert.AreEqual(8, r.worstStretch, 1e-6); Assert.AreEqual(1, r.severeArea);
            Assert.AreEqual(100, r.densityScore); Assert.Less(r.stretchScore, 5); Assert.Less(r.score, 70);
        }

        [Test]
        public void TinyNeedleCannotBeHiddenByHealthyMean()
        {
            var big = Rectangle(10, 10); var needle = Rectangle(.001f, .001f);
            needle.uv = needle.uv.Select(v => new Vector2(11 + v.x * 1000, v.y)).ToArray();
            var r = LightmapUvQuality.Measure(new[] { big, needle });
            Assert.Greater(r.stretchScore, 99); Assert.Greater(r.worstStretch, 900); Assert.LessOrEqual(r.score, 39);
        }

        [Test]
        public void UniformDensityVersusDensityDriftIsReportedSeparately()
        {
            var first = Rectangle(); var second = Rectangle();
            second.uv = second.uv.Select(v => v * .1f + new Vector2(2, 0)).ToArray();
            var r = LightmapUvQuality.Measure(new[] { first, second });
            Assert.AreEqual(100, r.stretchScore); Assert.Greater(r.densityCv, .8); Assert.Less(r.densityScore, 25);
        }

        [Test]
        public void LineAndMissingOrNonfiniteUvsPreventGreen()
        {
            var part = Rectangle(); part.uv = part.uv.Select(v => new Vector2(v.x, 0)).ToArray();
            Assert.LessOrEqual(LightmapUvQuality.Measure(new[] { part }).score, 39);
            part.uv = Array.Empty<Vector2>(); Assert.AreEqual(0, LightmapUvQuality.Measure(new[] { part }).score);
            part = Rectangle(); part.uv[0] = new Vector2(float.NaN, 0);
            var r = LightmapUvQuality.Measure(new[] { part });
            Assert.AreEqual(2, r.invalidFaces); Assert.AreEqual(0, r.score);
        }

        [Test]
        public void OverlapUsesUnionOccupancyAndOnlyAuditedOrderedDetailIsAllowed()
        {
            var under = Rectangle(); var over = Rectangle(); over.key = "detail";
            var unexpected = LightmapUvQuality.Measure(new[] { under, over });
            Assert.AreEqual(1, unexpected.fill); Assert.AreEqual(2, unexpected.unexpectedOverlapPairs);
            Assert.LessOrEqual(unexpected.score, 49);
            SetProvenance(under, over);
            var allowed = LightmapUvQuality.Measure(new[] { under, over });
            Assert.AreEqual(2, allowed.allowedOverlapPairs); Assert.AreEqual(0, allowed.unexpectedOverlapPairs);
            Assert.AreEqual(100, allowed.score); Assert.AreEqual(1, allowed.fill);
            over.provenance.surface.faces[0].intentionalOverlap = false;
            Assert.Greater(LightmapUvQuality.Measure(new[] { under, over }).unexpectedOverlapPairs, 0);
        }

        static void SetProvenance(LightmapUvQuality.Part under, LightmapUvQuality.Part over)
        {
            var chain = new[] { new ReverseUvAudit.NodeKey { lod = 1, node = 0, key = under.key },
                new ReverseUvAudit.NodeKey { lod = 1, node = 1, key = over.key } };
            var relations = Enumerable.Range(0, 2).Select(f => new ReverseUvTransfer.OverlapRelation {
                lod = 1, underMesh = 0, underFace = f, overMesh = 1, overFace = f }).ToArray();
            under.provenance = new ReverseUvAudit.ProvenanceEntry { chain = chain, overlaps = relations,
                surface = new ReverseUvTransfer.NodeReport { lod = 1, key = under.key,
                    faces = Enumerable.Range(0, 2).Select(_ => new ReverseUvTransfer.Face { inherited = true, chart = 3 }).ToArray() } };
            over.provenance = new ReverseUvAudit.ProvenanceEntry { chain = chain, overlaps = relations,
                surface = new ReverseUvTransfer.NodeReport { lod = 1, key = over.key,
                    faces = Enumerable.Range(0, 2).Select(_ => new ReverseUvTransfer.Face { inherited = true, chart = 3, layer = 1, intentionalOverlap = true }).ToArray() } };
        }

        [Test]
        public void StaleOrUnorderedProvenanceDoesNotWhitelistOverlap()
        {
            var under = Rectangle(); var over = Rectangle(); over.key = "detail"; SetProvenance(under, over);
            over.provenance.surface.key = "wrong";
            Assert.AreEqual(2, LightmapUvQuality.Measure(new[] { under, over }).unexpectedOverlapPairs);
            over.provenance.surface.key = over.key; over.provenance.surface.faces[0].layer = 0;
            Assert.AreEqual(1, LightmapUvQuality.Measure(new[] { under, over }).unexpectedOverlapPairs);
        }

        [Test]
        public void SharedSeamsAndReversedWholeIslandWindingAreLegal()
        {
            var part = Rectangle(); part.triangles = part.triangles.Reverse().ToArray();
            var r = LightmapUvQuality.Measure(new[] { part }); Assert.AreEqual(100, r.score);
            Assert.AreEqual(0, r.unexpectedOverlapPairs);
        }

        [Test]
        public void LodComparisonChecksSurfaceUvCorrespondenceRatherThanTopologyIndices()
        {
            var donor = Rectangle(); var target = Rectangle(); target.triangles = new[] { 0, 1, 3, 1, 2, 3 };
            var good = LightmapUvQuality.Measure(new[] { target }, new[] { donor });
            Assert.AreEqual(100, good.lodScore); Assert.AreEqual(1, good.lodCoverage, 1e-6);
            target.uv = target.uv.Select(v => v + new Vector2(.2f, 0)).ToArray();
            var bad = LightmapUvQuality.Measure(new[] { target }, new[] { donor });
            Assert.AreEqual(0, bad.lodScore); Assert.Greater(bad.lodMeanError, .1);
            Assert.Less(bad.score, 60, "A different baked layout cannot hide behind good standalone UV shape.");
            Assert.AreEqual(good.packingScore, bad.packingScore); Assert.AreEqual(good.stretchScore, bad.stretchScore);
        }

        [Test]
        public void MissingLodGroupsReduceCoverageAndMissingReferenceIsUnknown()
        {
            var donor = Rectangle(); var first = Rectangle(); var missing = Rectangle(); missing.key = "unmatched";
            missing.uv = missing.uv.Select(v => v + new Vector2(2, 0)).ToArray();
            var r = LightmapUvQuality.Measure(new[] { first, missing }, new[] { donor });
            Assert.AreEqual(.5, r.lodCoverage, 1e-6); Assert.AreEqual(50, r.lodScore);
            Assert.AreEqual(-1, LightmapUvQuality.Measure(new[] { missing }, new[] { donor }).lodScore);
        }

        [Test]
        public void BudgetExhaustionAndCancellationAreExplicit()
        {
            var r = LightmapUvQuality.Measure(new[] { Rectangle() }, rasterBudget: 1, overlapBudget: 0);
            Assert.IsFalse(r.complete); Assert.IsNotEmpty(r.limitation); Assert.LessOrEqual(r.score, 69);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => LightmapUvQuality.Measure(new[] { Rectangle() }, token: cancel.Token));
        }

        [Test]
        public void CaptureUsesSelectedChannelWorldScaleAndKeepsOriginalMeshUntouched()
        {
            var data = Rectangle(2, 1); var mesh = new Mesh { name = "source_LOD1", vertices = data.positions, triangles = data.triangles,
                uv = data.uv, uv2 = data.uv.Select(v => v * .2f).ToArray() };
            try {
                var bytes = TransferMeshSnapshot.Capture(mesh);
                var part = UvToolHub.CaptureQualityPart(mesh, new MeshEntry { fbxMesh = mesh, meshGroupKey = "source" }, Matrix4x4.Scale(new Vector3(2, 1, 1)), 1);
                Assert.AreEqual("source", part.key); Assert.AreEqual("source_LOD1", part.sourceKey);
                Assert.AreEqual(2, LightmapUvQuality.Measure(new[] { part }).worstStretch, 1e-5);
                CollectionAssert.AreEqual(bytes, TransferMeshSnapshot.Capture(mesh));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        static Mesh QuadQualityMesh(bool mixed, int baseVertex)
        {
            var positions = Enumerable.Repeat(new Vector3(100, 100, 0), baseVertex).ToList();
            positions.AddRange(new[] { Vector3.zero, new Vector3(2, 0, 0), new Vector3(2, 1, 0), Vector3.up });
            if (mixed) positions.AddRange(new[] { new Vector3(2, 0, 0), new Vector3(4, 0, 0),
                new Vector3(4, 1, 0), new Vector3(2, 1, 0) });
            positions.Add(new Vector3(100, 100, 0));
            var mesh = new Mesh { name = "KeepQuads", vertices = positions.ToArray(),
                uv = positions.Select(p => new Vector2(p.x, p.y) * .1f).ToArray() };
            mesh.uv2 = mesh.uv;
            mesh.subMeshCount = mixed ? 4 : 3;
            mesh.SetIndices(new[] { 0, 1, 2, 3 }, MeshTopology.Quads, 0, false, baseVertex);
            if (mixed) mesh.SetIndices(new[] { 4, 5, 6, 4, 6, 7 }, MeshTopology.Triangles, 1, false, baseVertex);
            int last = positions.Count - baseVertex - 1;
            mesh.SetIndices(new[] { 0, last }, MeshTopology.Lines, mixed ? 2 : 1, false, baseVertex);
            mesh.SetIndices(new[] { last }, MeshTopology.Points, mixed ? 3 : 2, false, baseVertex);
            mesh.RecalculateBounds();
            return mesh;
        }

        [TestCase(false, 0)] [TestCase(true, 0)]
        [TestCase(false, 3)] [TestCase(true, 3)]
        public void CaptureMeasuresQuadSurfacesAndMixedSubmeshesWithoutChangingSource(bool mixed, int baseVertex)
        {
            var mesh = QuadQualityMesh(mixed, baseVertex);
            try {
                var before = TransferMeshSnapshot.Capture(mesh);
                var transform = Matrix4x4.TRS(new Vector3(7, -2, 3), Quaternion.Euler(70, 9, 0), Vector3.one * 3);
                var part = UvToolHub.CaptureQualityPart(mesh, null, transform, 1);
                var report = LightmapUvQuality.Measure(new[] { part });
                Assert.AreEqual(mixed ? 4 : 2, report.faces, "Every surface face participates, including Keep Quads.");
                Assert.AreEqual(100, report.score);
                Assert.AreEqual(1, report.worstStretch, 1e-5);
                Assert.AreEqual(1, report.fill);
                Assert.AreEqual(new Vector2(mixed ? .4f : .2f, .1f), report.bboxMax,
                    "Lines, points and unused base vertices do not expand the surface UV bbox.");
                CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(mesh));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase(false)] [TestCase(true)]
        public void CaptureFindsStretchOnQuadsEvenWhenTriangleSubmeshesAreHealthy(bool mixed)
        {
            var mesh = QuadQualityMesh(mixed, 0);
            try {
                var uv = mesh.uv;
                for (int i = 0; i < 4; ++i) uv[i].x *= 8;
                mesh.uv = uv;
                var before = TransferMeshSnapshot.Capture(mesh);
                var part = UvToolHub.CaptureQualityPart(mesh, null, Matrix4x4.identity, 0);
                var report = LightmapUvQuality.Measure(new[] { part });
                Assert.AreEqual(mixed ? 4 : 2, report.faces);
                Assert.AreEqual(8, report.worstStretch, 1e-5);
                Assert.Less(report.stretchScore, 20);
                Assert.LessOrEqual(report.score, 69);
                CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(mesh));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator ViewerRecomputesAfterInPlaceUvChangeAndReleasesItsTexture()
        {
            var data = Rectangle(); var mesh = new Mesh { vertices = data.positions, triangles = data.triangles, uv2 = data.uv };
            var hub = ScriptableObject.CreateInstance<UvToolHub>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object Field(string name) => typeof(UvToolHub).GetField(name, flags).GetValue(hub);
            void Tick() => typeof(UvToolHub).GetMethod("OnInspectorUpdate", flags).Invoke(hub, null);
            Texture2D texture = null;
            try {
                var items = (List<MeshViewport3D.Item>)Field("viewportItems");
                var entries = (List<MeshEntry>)Field("viewportEntries");
                var entry = new MeshEntry { originalMesh = mesh, fbxMesh = mesh, meshGroupKey = "panel" };
                items.Add(new MeshViewport3D.Item(mesh, Matrix4x4.identity)); entries.Add(entry);
                hub.DiagnosticContext.MeshEntries.Add(entry);
                Tick();
                for (int i = 0; i < 200 && Field("uvQualityReport") == null; ++i) { yield return null; Tick(); }
                Assert.NotNull(Field("uvQualityReport"));
                Assert.AreEqual(100, ((LightmapUvQuality.Report)Field("uvQualityReport")).score);
                texture = (Texture2D)Field("uvQualityMap"); Assert.IsTrue(texture);
                var originalReport = Field("uvQualityReport");
                mesh.uv2 = data.uv.Select(v => new Vector2(v.x * 8, v.y)).ToArray();
                hub.DiagnosticContext.ClearAllCaches(); Tick();
                Assert.AreNotSame(originalReport, Field("uvQualityReport"), "A stale green badge must disappear as soon as data changes.");
                for (int i = 0; i < 200 && Field("uvQualityReport") == null; ++i) { yield return null; Tick(); }
                var changed = (LightmapUvQuality.Report)Field("uvQualityReport"); Assert.NotNull(changed);
                Assert.AreEqual(8, changed.worstStretch, 1e-6); Assert.IsFalse(texture, "Replaced occupancy textures are released.");
                texture = (Texture2D)Field("uvQualityMap");
                hub.DiagnosticContext.PreviewLod = 1; Tick();
                Assert.AreNotSame(changed, Field("uvQualityReport"), "LOD changes must invalidate the badge too.");
            }
            finally { UnityEngine.Object.DestroyImmediate(hub); UnityEngine.Object.DestroyImmediate(mesh); }
            Assert.IsFalse(texture, "Closing the Viewer releases diagnostic textures and cancels pending work.");
        }

        [UnityTest]
        public IEnumerator ViewerMeasuresTriangleLodAgainstKeepQuadsLod0WithoutChangingSources()
        {
            var donor = QuadQualityMesh(false, 3);
            var target = UnityEngine.Object.Instantiate(donor);
            target.SetIndices(new[] { 0, 1, 3, 1, 2, 3 }, MeshTopology.Triangles, 0, false, 3);
            var donorBefore = TransferMeshSnapshot.Capture(donor);
            var targetBefore = TransferMeshSnapshot.Capture(target);
            var hub = ScriptableObject.CreateInstance<UvToolHub>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object Field(string name) => typeof(UvToolHub).GetField(name, flags).GetValue(hub);
            void Tick() => typeof(UvToolHub).GetMethod("OnInspectorUpdate", flags).Invoke(hub, null);
            try {
                var reference = new MeshEntry { originalMesh = donor, fbxMesh = donor, meshGroupKey = "panel", lodIndex = 0 };
                var entry = new MeshEntry { originalMesh = target, fbxMesh = target, meshGroupKey = "panel", lodIndex = 1 };
                ((List<MeshViewport3D.Item>)Field("viewportItems")).Add(new MeshViewport3D.Item(target, Matrix4x4.identity));
                ((List<MeshEntry>)Field("viewportEntries")).Add(entry);
                hub.DiagnosticContext.MeshEntries.Add(reference);
                hub.DiagnosticContext.MeshEntries.Add(entry);
                hub.DiagnosticContext.PreviewLod = 1;
                Tick();
                for (int i = 0; i < 200 && Field("uvQualityReport") == null; ++i) { yield return null; Tick(); }
                var report = (LightmapUvQuality.Report)Field("uvQualityReport");
                Assert.NotNull(report);
                Assert.AreEqual(100, report.score);
                Assert.AreEqual(100, report.lodScore, "Keep Quads LOD0 must contribute a measurable donor surface.");
                Assert.AreEqual(1, report.lodCoverage, 1e-6);
                CollectionAssert.AreEqual(donorBefore, TransferMeshSnapshot.Capture(donor));
                CollectionAssert.AreEqual(targetBefore, TransferMeshSnapshot.Capture(target));
            }
            finally {
                UnityEngine.Object.DestroyImmediate(hub);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(donor);
            }
        }

        [Test]
        public void FrozenForwardModelsExposeKnownDefectsWithoutChangingTheirOutputs()
        {
            string fixtures = Environment.GetEnvironmentVariable("MESHLAB_QUALITY_SCORE_FIXTURES");
            if (string.IsNullOrEmpty(fixtures)) Assert.Ignore("Optional previous forward benchmark snapshots are not configured.");
            string output = Environment.GetEnvironmentVariable("MESHLAB_QUALITY_SCORE_OUTPUT");
            Assert.IsNotEmpty(output); Directory.CreateDirectory(output);
            int measured = 0;
            foreach (string directory in Directory.GetDirectories(fixtures)) {
                var files = Directory.GetFiles(directory, "*.bin");
                var meshes = files.Select(path => TransferMeshSnapshot.Restore(File.ReadAllBytes(path))).ToArray();
                try {
                    var parts = meshes.Select((mesh, i) => {
                        mesh.name = Path.GetFileNameWithoutExtension(files[i]);
                        return UvToolHub.CaptureQualityPart(mesh, new MeshEntry { meshGroupKey = UvToolContext.ExtractGroupKey(mesh.name) }, Matrix4x4.identity, 1);
                    }).ToArray();
                    var lod0 = parts.Where((p, i) => meshes[i].name.EndsWith("_LOD0", StringComparison.Ordinal)).ToArray();
                    foreach (int lod in new[] { 0, 1, 2 }) {
                        var level = parts.Where((p, i) => meshes[i].name.EndsWith("_LOD" + lod, StringComparison.Ordinal)).ToArray();
                        if (level.Length == 0) continue;
                        var r = LightmapUvQuality.Measure(level, lod == 0 ? null : lod0);
                        File.WriteAllText(Path.Combine(output, Path.GetFileName(directory) + "_LOD" + lod + ".json"), JsonUtility.ToJson(r, true));
                        Assert.IsTrue(r.complete, Path.GetFileName(directory));
                        if (lod > 0 && (directory.Contains("Kitchen") || directory.Contains("kamaz") || lod == 2)) Assert.LessOrEqual(r.score, 39);
                        ++measured;
                    }
                }
                finally { foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh); }
            }
            Assert.GreaterOrEqual(measured, 8); // Cafe 3, Kitchen 2, Kamaz 3 LODs.
        }

        [Serializable] sealed class FrozenLayout
        {
            public int lod; public string key;
            public Vector3[] positions, metricPositions; public Matrix4x4 metricToWorld;
            public Vector2[] uv; public int[] indices;
        }

        [TestCase(false)] [TestCase(true)]
        public void FrozenReverseModelsClassifyOverlapExactlyLikeIndependentAudit(bool overlap)
        {
            string fixtures = Environment.GetEnvironmentVariable("MESHLAB_QUALITY_SCORE_FIXTURES");
            if (string.IsNullOrEmpty(fixtures)) Assert.Ignore("Optional previous reverse benchmark snapshots are not configured.");
            string directory = Path.Combine(fixtures, "junction", "table-improved-" + (overlap ? "overlap" : "exclusive"));
            string auditFile = Directory.GetFiles(directory, "reverse_*.json").Single();
            var audit = JsonUtility.FromJson<ReverseUvAudit.Audit>(File.ReadAllText(auditFile));
            var provenance = ReverseUvAudit.Provenance(audit.report).Select(JsonUtility.FromJson<ReverseUvAudit.ProvenanceEntry>).ToArray();
            var parts = Directory.GetFiles(directory, "lod*.json").Select(path => {
                var layout = JsonUtility.FromJson<FrozenLayout>(File.ReadAllText(path));
                return new LightmapUvQuality.Part { positions = layout.positions, metricPositions = layout.metricPositions,
                    metricToWorld = layout.metricToWorld, uv = layout.uv, triangles = layout.indices,
                    sourceKey = layout.key, key = UvToolContext.ExtractGroupKey(layout.key),
                    provenance = provenance.Single(p => p.surface.lod == layout.lod && p.surface.key == layout.key) };
            }).ToArray();
            string output = Environment.GetEnvironmentVariable("MESHLAB_QUALITY_SCORE_OUTPUT");
            Assert.IsNotEmpty(output); Directory.CreateDirectory(output);
            foreach (var level in audit.levels) {
                var selected = parts.Where(p => p.provenance.surface.lod == level.lod).ToArray();
                var r = LightmapUvQuality.Measure(selected, level.lod == 0 ? null : parts.Where(p => p.provenance.surface.lod == 0).ToArray());
                Assert.IsTrue(r.complete); Assert.AreEqual(0, r.invalidFaces + r.collapsedFaces + r.unexpectedOverlapPairs);
                Assert.AreEqual(level.allowedOverlapPairs, r.allowedOverlapPairs, "Must use the same ordered face ancestry, not a global overlap toggle.");
                File.WriteAllText(Path.Combine(output, "Reverse_Table_" + overlap + "_LOD" + level.lod + ".json"), JsonUtility.ToJson(r, true));
            }
        }
    }
}
