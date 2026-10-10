// UvChartMergeTests.cs — the post-unwrap chart merge on small hand-built layouts.
// The pure merge core runs without the native plugin; the end-to-end test follows
// the RemeshBakeTests probe-and-ignore pattern.

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvChartMergeTests
    {
        const float Density = 0.2f;

        // ── builders ────────────────────────────────────────────────────────

        static Vector2 Rotate(Vector2 p, float degrees)
        {
            float r = Mathf.Deg2Rad * degrees;
            float c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(c * p.x - s * p.y, s * p.x + c * p.y);
        }

        // Tangent of the plane P(u,v) = (u, 0, v): T = (1,0,0), true bitangent
        // (0,0,1), N = (0,1,0) ⇒ handedness w = −1. A UV rotation by θ turns it into
        // (cosθ, 0, −sinθ) — the identity the merge must preserve end-to-end.
        static Vector4 PlaneTangent(float uvRotationDegrees)
            => new Vector4(Mathf.Cos(Mathf.Deg2Rad * uvRotationDegrees), 0f, -Mathf.Sin(Mathf.Deg2Rad * uvRotationDegrees), -1f);

        // Two unit squares in XZ sharing the x=1 edge, seam vertices duplicated
        // index-space (as every producer here does). Chart 1's UVs sit at Density
        // scale, rotated by `chart1Rotation`, scaled by `chart1Scale`, shifted by
        // `chart1Offset`; its authored tangents match that frame.
        static RemeshNative.Geometry TwoPatchGeometry(float chart1Rotation = 0f, float chart1Scale = 1f,
            Vector2 chart1Offset = default, Vector2[] chart1UvOverride = null)
        {
            var positions = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), // chart 0
                new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(2, 0, 1), new Vector3(2, 0, 0), // chart 1
            };
            var indices = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7 };
            var uvNatural = new[] {
                new Vector2(0, 0), new Vector2(Density, 0), new Vector2(Density, Density), new Vector2(0, Density),
                new Vector2(Density, 0), new Vector2(Density, Density), new Vector2(2 * Density, Density), new Vector2(2 * Density, 0),
            };
            var uv = new Vector2[8];
            for (int i = 0; i < 8; ++i)
                uv[i] = i < 4 ? uvNatural[i]
                    : chart1UvOverride != null ? chart1UvOverride[i - 4]
                    : chart1Scale * Rotate(uvNatural[i], chart1Rotation) + chart1Offset;
            var tangents = new Vector4[8];
            var normals = new Vector3[8];
            var charts = new int[8];
            for (int i = 0; i < 8; ++i)
            {
                tangents[i] = i < 4 ? PlaneTangent(0f) : PlaneTangent(chart1Rotation);
                normals[i] = Vector3.up;
                charts[i] = i < 4 ? 0 : 1;
            }
            return new RemeshNative.Geometry {
                positions = positions, normals = normals, uv = uv, tangents = tangents,
                indices = indices, charts = charts, chartCount = 2, draftUv = false,
            };
        }

        // Three unit squares in a row. Chart 1 is subdivided along X (8 vertices) so it has
        // more vertices than chart 0 — absorbing chart 0 moves fewer vertices, the merge
        // keeps chart 1's id and the surviving ids {1, 2} leave a hole at 0. Chart 2
        // carries `chart2Transform` (default: a ×3 density mismatch no merge may pass).
        static RemeshNative.Geometry ThreePatchGeometry(Func<Vector2, Vector2> chart2Transform = null)
        {
            var positions = new List<Vector3>(17);
            var uv = new List<Vector2>(17);
            var charts = new List<int>(17);
            var indices = new List<int>();
            void AddQuad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Vector2 uvBl, Vector2 uvBr, Vector2 uvTr, Vector2 uvTl, int chart)
            {
                int b = positions.Count;
                positions.AddRange(new[] { bl, br, tr, tl });
                uv.AddRange(new[] { uvBl, uvBr, uvTr, uvTl });
                charts.AddRange(new[] { chart, chart, chart, chart });
                indices.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 }); // (bl,tr,br),(bl,tl,tr) — CW in UV
            }
            // Chart 1's seam edges have the same endpoints as charts 0 and 2;
            // subdivision along Z would create T-junctions rather than shared edges.
            Vector2 u0 = new Vector2(0, 0), u1 = new Vector2(Density, 0), u2 = new Vector2(Density, Density), u3 = new Vector2(0, Density);
            AddQuad(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), u0, u1, u2, u3, 0);
            for (int c = 0; c < 2; ++c)
                {
                    float x0 = 1 + 0.5f * c, x1 = x0 + 0.5f, z0 = 0, z1 = 1;
                    Vector2 v0 = new Vector2(Density + 0.1f * c, 0);
                    AddQuad(new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1),
                        v0, v0 + new Vector2(0.1f, 0), v0 + new Vector2(0.1f, Density), v0 + new Vector2(0, Density), 1);
                }
            Func<Vector2, Vector2> transform = chart2Transform ?? (p => p * 3f);
            AddQuad(new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 0, 1), new Vector3(2, 0, 1),
                transform(new Vector2(2 * Density, 0)), transform(new Vector2(3 * Density, 0)),
                transform(new Vector2(3 * Density, Density)), transform(new Vector2(2 * Density, Density)), 2);
            var tangents = new Vector4[positions.Count];
            var normals = new Vector3[positions.Count];
            for (int i = 0; i < positions.Count; ++i)
            {
                tangents[i] = PlaneTangent(0f);
                normals[i] = Vector3.up;
            }
            return new RemeshNative.Geometry {
                positions = positions.ToArray(), normals = normals, uv = uv.ToArray(), tangents = tangents,
                indices = indices.ToArray(), charts = charts.ToArray(), chartCount = 3, draftUv = false,
            };
        }

        // Chart 0 is an L of three quads; chart 1 fills the missing corner, so the seam
        // bends around three non-collinear welded slots — the shape the residual gate
        // can actually see.
        static RemeshNative.Geometry LSeamGeometry(float chart1Shear)
        {
            var positions = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), // chart 0 quads
                new Vector3(2, 0, 0), new Vector3(2, 0, 1), new Vector3(0, 0, 2), new Vector3(1, 0, 2),
                new Vector3(1, 0, 1), new Vector3(2, 0, 1), new Vector3(2, 0, 2), new Vector3(1, 0, 2), // chart 1 (split copies)
            };
            var indices = new[] {
                0, 2, 1, 0, 3, 2,      // [0,1]²
                1, 5, 4, 1, 2, 5,      // [1,2]×[0,1]
                3, 7, 2, 3, 6, 7,      // [0,1]×[1,2]
                8, 10, 9, 8, 11, 10,   // [1,2]² — chart 1
            };
            var uv = new Vector2[12];
            for (int i = 0; i < 12; ++i)
                uv[i] = new Vector2(positions[i].x, positions[i].z) * Density;
            for (int i = 8; i < 12; ++i)
                uv[i] = new Vector2(uv[i].x + chart1Shear * uv[i].y, uv[i].y);
            var tangents = new Vector4[12];
            var normals = new Vector3[12];
            var charts = new int[12];
            for (int i = 0; i < 12; ++i)
            {
                tangents[i] = PlaneTangent(0f);
                normals[i] = Vector3.up;
                charts[i] = i < 8 ? 0 : 1;
            }
            return new RemeshNative.Geometry {
                positions = positions, normals = normals, uv = uv, tangents = tangents,
                indices = indices, charts = charts, chartCount = 2, draftUv = false,
            };
        }

        // ── settings wiring ─────────────────────────────────────────────────

        [Test]
        public void MergeChartsToggleMarksOnlyTheUnwrapStageStale()
        {
            var settings = new RemeshSettings { chartMaxCost = 9, mergeCharts = false };
            foreach (RemeshPipeline.Stage stage in new[] { RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Simplify,
                RemeshPipeline.Stage.Unwrap, RemeshPipeline.Stage.Bake })
            {
                string before = RemeshPipeline.Key(stage, settings, null);
                settings.mergeCharts = true;
                Assert.AreEqual(stage == RemeshPipeline.Stage.Unwrap, before != RemeshPipeline.Key(stage, settings, null), stage.ToString());
                settings.mergeCharts = false;
            }
            Assert.AreEqual(9, settings.chartMaxCost);
            // No JSON migration while the experiment runs: an absent field stays off.
            string json = JsonUtility.ToJson(settings);
            Assert.IsFalse(RemeshSettings.FromSavedJson(json.Replace("\"mergeCharts\":false,", "")).mergeCharts);
        }

        // ── similarity fit ──────────────────────────────────────────────────

        [Test]
        public void BroadStrategyKeepsTheStretchAlreadyRecoveredByNarrowMerging()
        {
            var original = new UvChartQuality(381, 306, 1.03514, 5.02693, true);
            var narrow = new UvChartQuality(113, 56, 1.03929, 3.1215, true);
            var broad = new UvChartQuality(83, 47, 1.09305, 5.38553, true);
            var packing = UvPackingQuality.Measure(TwoPatchGeometry(), default);
            Assert.IsTrue(broad.Improves(narrow, original), "the old baseline would permit this regression");
            Assert.IsFalse(UvChartMerge.PreferBroad(narrow, broad, packing, packing));
            var bounded = new UvChartQuality(100, 50, 1.05, 3.5, true);
            Assert.IsTrue(UvChartMerge.PreferBroad(narrow, bounded, packing, packing));
            Assert.IsFalse(UvChartMerge.PreferBroad(narrow, bounded, packing, default), "packing still has to be valid");
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void CancellationDuringStrategySelectionPreservesOriginalGeometry(int cancelStage)
        {
            if (cancelStage > 1) RequireRemeshNative();
            var g = TwoPatchGeometry(chart1Offset: new Vector2(.4f, .3f));
            var uv = (Vector2[])g.uv.Clone(); var charts = (int[])g.charts.Clone(); var tangents = (Vector4[])g.tangents.Clone();
            var positions = g.positions; var indices = g.indices; var normals = g.normals;
            var level = UvtLog.Current; var categories = UvtLog.EnabledCategories;
            bool hadLevel = UnityEditor.EditorPrefs.HasKey("UnityMeshLab_LogLevel");
            bool hadCategories = UnityEditor.EditorPrefs.HasKey("UnityMeshLab_LogCategoryMask");
            using (var cancelled = new CancellationTokenSource())
            {
                bool sawCancellationPoint = false, narrowCompleted = false;
                int candidates = 0;
                void OnLog(string message, string stack, LogType kind)
                {
                    if (message.Contains("[UV] merge-strategy-candidate:")) narrowCompleted = true;
                    if (message.Contains("[UV] merge-candidate:")) ++candidates;
                    if (cancelStage < 3 && message.Contains("[UV] merge-candidate:") && candidates == cancelStage ||
                        cancelStage == 3 && message.Contains("[UV] merge-strategy:") ||
                        cancelStage == 4 && message.Contains("[UV] merge-checkpoint:"))
                    { sawCancellationPoint = true; cancelled.Cancel(); }
                }
                try
                {
                    UvtLog.Current = UvtLog.Level.Info; UvtLog.EnabledCategories |= UvtLog.Category.RemeshDiag;
                    Application.logMessageReceived += OnLog;
                    var quality = UvChartQuality.Measure(g, CancellationToken.None);
                    Assert.Throws<OperationCanceledException>(() => UvChartMerge.Apply(g, quality, new RemeshSettings(), cancelled.Token));
                    Assert.IsTrue(sawCancellationPoint, "cancel at a deterministic production checkpoint");
                    if (cancelStage == 2 || cancelStage == 3) Assert.IsTrue(narrowCompleted, "narrow strategy completed before cancellation");
                    Assert.AreEqual(2, g.chartCount); CollectionAssert.AreEqual(uv, g.uv); CollectionAssert.AreEqual(charts, g.charts);
                    CollectionAssert.AreEqual(tangents, g.tangents); Assert.AreSame(positions, g.positions); Assert.AreSame(indices, g.indices); Assert.AreSame(normals, g.normals);
                }
                finally
                {
                    Application.logMessageReceived -= OnLog;
                    UvtLog.Current = level; UvtLog.EnabledCategories = categories;
                    if (!hadLevel) UnityEditor.EditorPrefs.DeleteKey("UnityMeshLab_LogLevel");
                    if (!hadCategories) UnityEditor.EditorPrefs.DeleteKey("UnityMeshLab_LogCategoryMask");
                }
            }
        }

        [Test]
        public void SimilarityFitRecoversSeededRotationScaleAndTranslation()
        {
            var random = new System.Random(1234);
            var from = new Vector2[6];
            for (int i = 0; i < from.Length; ++i)
                from[i] = new Vector2((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f);
            const float angle = 0.7f, scale = 1.3f;
            var offset = new Vector2(0.4f, -0.9f);
            var to = new Vector2[from.Length];
            for (int i = 0; i < from.Length; ++i) to[i] = scale * Rotate(from[i], angle * Mathf.Rad2Deg) + offset;

            Assert.IsTrue(UvChartMerge.FitSimilarity(from, to, out float cos, out float sin, out float fitScale, out Vector2 fitOffset, out float residual));
            Assert.That(fitScale, Is.EqualTo(scale).Within(1e-4));
            Assert.That(Mathf.Atan2(sin, cos), Is.EqualTo(angle).Within(1e-4));
            Assert.That((fitOffset - offset).magnitude, Is.LessThan(1e-4));
            Assert.That(residual, Is.LessThan(1e-4));
        }

        // ── merge outcomes ──────────────────────────────────────────────────

        [Test]
        public void AdjacentPatchesMergeIntoOneChartWithBitExactSeam()
        {
            var geometry = TwoPatchGeometry(30f, 1f, new Vector2(0.5f, 0.5f));
            var mergedCharts = new HashSet<int>();
            int merged = UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, mergedCharts);

            Assert.AreEqual(1, merged);
            Assert.AreEqual(1, geometry.chartCount);
            // The seam copies carry the acceptor's UV values verbatim — xatlas
            // reconnects by UV colocalization, near-equal would split again.
            Assert.AreEqual(geometry.uv[1].x, geometry.uv[4].x);
            Assert.AreEqual(geometry.uv[1].y, geometry.uv[4].y);
            Assert.AreEqual(geometry.uv[2].x, geometry.uv[5].x);
            Assert.AreEqual(geometry.uv[2].y, geometry.uv[5].y);
            Assert.IsTrue(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
            // The merge moves UVs only; tangents are rebuilt afterwards from the final
            // layout (Apply does it after the pack), so the authored frames survive
            // MergeCharts untouched.
            for (int v = 0; v < 8; ++v)
                Assert.That(geometry.tangents[v], Is.EqualTo(v < 4 ? PlaneTangent(0f) : PlaneTangent(30f)).Within(1e-4));
            // Either direction can win by a smaller residual. The surviving chart's
            // frame is authoritative; both patches must agree after the rebuild.
            var expected = geometry.uv[0] == Vector2.zero ? PlaneTangent(0f) : PlaneTangent(30f);
            UvChartMerge.RebuildChartTangents(geometry, mergedCharts, CancellationToken.None);
            for (int v = 0; v < 8; ++v)
                Assert.That((geometry.tangents[v] - expected).magnitude, Is.LessThan(1e-4));
        }

        [Test]
        public void MergingNonNeighborIdsLeavesNoHolesAfterCompaction()
        {
            var geometry = ThreePatchGeometry(); // chart 2 fails the density gate on purpose
            int merged = UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, new HashSet<int>());

            Assert.GreaterOrEqual(merged, 1);
            Assert.AreEqual(2, geometry.chartCount);
            var present = new HashSet<int>(geometry.charts);
            CollectionAssert.AreEqual(new[] { 0, 1 }, present, "chart ids must be compacted to 0..N-1");
            Assert.IsTrue(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
        }

        [Test]
        public void SharedWeldedEdge_DoesNotExemptFoldedTriangles()
        {
            // Both triangles have the same UV winding and lie on the same side of
            // their seam. The 3D faces lie on opposite sides. Shared welded edges
            // must not exempt a positive-area overlap, in either merge direction.
            var geometry = new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.forward,
                    Vector3.zero, Vector3.right, Vector3.back },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up,
                    Vector2.zero, Vector2.right, Vector2.up },
                indices = new[] { 0, 1, 2, 3, 4, 5 }, charts = new[] { 0, 0, 0, 1, 1, 1 }, chartCount = 2
            };
            int merged = UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, new HashSet<int>());

            Assert.AreEqual(0, merged);
            Assert.AreEqual(2, geometry.chartCount);
            Assert.IsTrue(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
        }

        [Test]
        public void OppositeChartWinding_IsAlignedBeforeSeamFit()
        {
            var geometry = TwoPatchGeometry(chart1UvOverride: new[] {
                new Vector2(-Density, 0), new Vector2(-Density, Density),
                new Vector2(-2 * Density, Density), new Vector2(-2 * Density, 0)
            });
            Assert.IsTrue(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
            Assert.AreEqual(1, UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, new HashSet<int>()));
            Assert.IsTrue(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
            var report = UvAtlasDiagnostics.Measure(geometry, CancellationToken.None);
            Assert.IsTrue(report.complete);
            Assert.AreEqual(0, report.pairs);
        }

        [Test]
        public void TangentDuplicatesOnSeam_AreAllSnappedBitExactly()
        {
            var geometry = TwoPatchGeometry(33.7f, 1f, new Vector2(.3f, .7f));
            // The second mover face uses another normal/tangent copy of its seam corner.
            Array.Resize(ref geometry.positions, 9); geometry.positions[8] = geometry.positions[4];
            Array.Resize(ref geometry.uv, 9); geometry.uv[8] = geometry.uv[4];
            Array.Resize(ref geometry.charts, 9); geometry.charts[8] = 1;
            Array.Resize(ref geometry.normals, 9); geometry.normals[8] = geometry.normals[4];
            Array.Resize(ref geometry.tangents, 9); geometry.tangents[8] = geometry.tangents[4];
            geometry.indices[9] = 8;
            Assert.AreEqual(1, UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, new HashSet<int>()));
            Assert.AreEqual(geometry.uv[1], geometry.uv[4]);
            Assert.AreEqual(geometry.uv[1], geometry.uv[8]);
            Assert.AreEqual(0, UvAtlasDiagnostics.Measure(geometry, CancellationToken.None).pairs);
        }

        [Test]
        public void DensityMismatchBeyondTwoTimesRejectsTheMerge()
        {
            var geometry = TwoPatchGeometry(0f, 3f);
            Assert.AreEqual(0, UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, new HashSet<int>()));
            Assert.AreEqual(2, geometry.chartCount);
        }

        [Test]
        public void BentSeamShearBeyondResidualGateRejectsTheMerge()
        {
            var geometry = LSeamGeometry(0.5f);
            Assert.AreEqual(0, UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, new HashSet<int>()));
            Assert.AreEqual(2, geometry.chartCount);
        }

        [Test]
        public void BentSeamShearWithinResidualGateMerges()
        {
            var geometry = LSeamGeometry(0.02f);
            Assert.AreEqual(1, UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, new HashSet<int>()));
            Assert.AreEqual(1, geometry.chartCount);
            Assert.IsTrue(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
        }

        [Test]
        public void UserIslandLimitsBoundMerging()
        {
            var geometry = TwoPatchGeometry(); // combined 3D area 2, merged border 6
            var limits = new RemeshSettings { maxChartArea = 1.5f };
            Assert.AreEqual(0, UvChartMerge.MergeCharts(geometry, limits, CancellationToken.None, new HashSet<int>()));

            geometry = TwoPatchGeometry();
            limits = new RemeshSettings { maxChartBoundary = 5.5f };
            Assert.AreEqual(0, UvChartMerge.MergeCharts(geometry, limits, CancellationToken.None, new HashSet<int>()));

            geometry = TwoPatchGeometry();
            limits = new RemeshSettings { maxChartArea = 2f, maxChartBoundary = 6f };
            Assert.AreEqual(1, UvChartMerge.MergeCharts(geometry, limits, CancellationToken.None, new HashSet<int>()));
        }

        [Test]
        public void MergeChartsIsDeterministic()
        {
            var settings = new RemeshSettings();
            var first = TwoPatchGeometry(33.7f, 1f, new Vector2(0.3f, 0.7f));
            var second = TwoPatchGeometry(33.7f, 1f, new Vector2(0.3f, 0.7f));
            int firstMerged = UvChartMerge.MergeCharts(first, settings, CancellationToken.None, new HashSet<int>());
            int secondMerged = UvChartMerge.MergeCharts(second, settings, CancellationToken.None, new HashSet<int>());

            Assert.AreEqual(firstMerged, secondMerged);
            Assert.AreEqual(first.chartCount, second.chartCount);
            Assert.AreEqual(first.charts, second.charts);
            Assert.AreEqual(first.uv.Length, second.uv.Length);
            for (int i = 0; i < first.uv.Length; ++i)
            {
                Assert.AreEqual(first.uv[i].x, second.uv[i].x);
                Assert.AreEqual(first.uv[i].y, second.uv[i].y);
            }
        }

        // ── tangent rebuild ─────────────────────────────────────────────────

        [TestCase(1f)]
        [TestCase(.002f)]
        [TestCase(.000001f)]
        [TestCase(1000000f)]
        public void RebuiltTangentsFollowTheFinalAtlasLayoutIncludingSnappedSeams(float scale)
        {
            // The chart is laid out with a per-vertex snap displacement baked in (the
            // mover's seam copies carry the acceptor's values, its interior keeps the
            // fitted similarity) — a state no rotated stale tangent represents, but the
            // rebuild must resolve to the natural frame of the final UVs.
            var geometry = TwoPatchGeometry(30f, 1f, new Vector2(0.5f, 0.5f));
            var mergedCharts = new HashSet<int>();
            UvChartMerge.MergeCharts(geometry, new RemeshSettings(), CancellationToken.None, mergedCharts);
            CollectionAssert.AreEquivalent(new[] { 0 }, mergedCharts, "the merged chart id is reported compacted");
            var expected = geometry.uv[0] == Vector2.zero ? PlaneTangent(0f) : PlaneTangent(30f);
            for (int v = 0; v < geometry.positions.Length; ++v) geometry.positions[v] *= scale;
            for (int v = 0; v < 8; ++v)
                geometry.tangents[v] = PlaneTangent(174f); // garbage in
            UvChartMerge.RebuildChartTangents(geometry, mergedCharts, CancellationToken.None);
            for (int v = 0; v < 8; ++v)
                Assert.That((geometry.tangents[v] - expected).magnitude, Is.LessThan(1e-3), "vertex " + v);
        }

        // ── end-to-end with the native unwrap ───────────────────────────────

        [Test]
        public void RebuiltTangentsJoinIdenticalCopiesOfARemovedSeam()
        {
            var geometry = TwoPatchGeometry(chart1UvOverride: new[] {
                new Vector2(Density, 0), new Vector2(Density, Density),
                new Vector2(2 * Density, 2 * Density), new Vector2(2 * Density, Density) });
            Array.Fill(geometry.charts, 0);
            UvChartMerge.RebuildChartTangents(geometry, new HashSet<int> { 0 }, CancellationToken.None);
            Assert.That(Vector4.Distance(geometry.tangents[1], geometry.tangents[4]), Is.LessThan(1e-6), "same position, normal and final UV need one frame");
            Assert.That(Vector4.Distance(geometry.tangents[2], geometry.tangents[5]), Is.LessThan(1e-6));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RebuiltTangentsPreserveUvAndNormalDiscontinuities(bool hardNormal)
        {
            var geometry = TwoPatchGeometry(chart1UvOverride: new[] {
                new Vector2(Density, 0), new Vector2(Density, Density),
                new Vector2(2 * Density, 2 * Density), new Vector2(2 * Density, Density) });
            if (hardNormal) {
                Array.Fill(geometry.charts, 0);
                for (int i = 4; i < 8; ++i) geometry.normals[i] = new Vector3(0, 1, 1).normalized;
            }
            UvChartMerge.RebuildChartTangents(geometry, new HashSet<int> { 0, 1 }, CancellationToken.None);
            Assert.That(Vector4.Distance(geometry.tangents[1], geometry.tangents[4]), Is.GreaterThan(.2f), "distinct charts or shading normals must keep independent frames");
        }

        [Test]
        public void NativeChartMergeReducesIslandsOnFragmentedUnwrap()
        {
            RequireRemeshNative();
            const int cells = 12, stride = cells + 1;
            var p = new Vector3[stride * stride];
            var t = new int[cells * cells * 6];
            for (int y = 0; y <= cells; ++y)
                for (int x = 0; x <= cells; ++x)
                {
                    float u = x / (float)cells, v = y / (float)cells;
                    p[y * stride + x] = new Vector3(u, .2f * Mathf.Sin(u * Mathf.PI * 2) * Mathf.Sin(v * Mathf.PI * 2), v);
                }
            for (int y = 0; y < cells; ++y)
                for (int x = 0; x < cells; ++x)
                {
                    int a = y * stride + x, f = (y * cells + x) * 6;
                    t[f] = a; t[f + 1] = a + stride; t[f + 2] = a + 1;
                    t[f + 3] = a + 1; t[f + 4] = a + stride; t[f + 5] = a + stride + 1;
                }
            var input = new RemeshNative.IndexedMesh { positions = p, indices = t };
            var settings = new RemeshSettings { textureResolution = 512, chartMaxCost = .1f,
                chartStraightness = 0, reduceUvFragmentation = false, mergeCharts = false };
            var baseline = RemeshNative.Unwrap(input, settings, CancellationToken.None);
            settings.mergeCharts = true;
            var result = RemeshNative.Unwrap(input, settings, CancellationToken.None);

            Assert.That(result.chartCount, Is.LessThanOrEqualTo(baseline.chartCount));
            Assert.AreEqual(baseline.chartCount, result.originalChartCount);
            Assert.IsTrue(UvChartQuality.Measure(result, CancellationToken.None).valid);
            var atlas = UvAtlasDiagnostics.Measure(result, CancellationToken.None);
            Assert.IsTrue(atlas.complete);
            Assert.AreEqual(0, atlas.pairs, "final atlas must be overlap-free including shared-edge neighbours");
            Assert.AreEqual(baseline.indices.Length, result.indices.Length);
            for (int i = 0; i < result.indices.Length; ++i)
                Assert.AreEqual(baseline.positions[baseline.indices[i]], result.positions[result.indices[i]], "every source corner stays in place");
            foreach (var uv in result.uv)
            {
                Assert.That(uv.x, Is.InRange(0f, 1f));
                Assert.That(uv.y, Is.InRange(0f, 1f));
            }
            var mesh = new Mesh();
            try
            {
                mesh.vertices = result.positions; mesh.triangles = result.indices; mesh.uv = result.uv;
                var report = TransferValidator.Validate(mesh, result.uv);
                TransferValidator.DetectUv2Overlaps(mesh, result.uv, report);
                Assert.AreEqual(0, report.zeroAreaCount);
                Assert.AreEqual(0, report.oobCount);
                Assert.AreEqual(0, report.overlapShellPairs + report.overlapSameSrcPairs);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        static void RequireRemeshNative()
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException error) when (error.InnerException is DllNotFoundException || error.InnerException is EntryPointNotFoundException || error.InnerException is BadImageFormatException)
            {
                Assert.Ignore("Remesh native plugin unavailable: " + error.Message);
            }
        }

        [Test]
        public void MergeAttachesToUnrolledCylinderWithoutClosingItsExistingCut()
        {
            var ring = new[] { Vector3.right, Vector3.forward, Vector3.left, Vector3.back, Vector3.right };
            var positions = new List<Vector3>(); var uv = new List<Vector2>();
            var indices = new List<int>(); var charts = new List<int>();
            float width = Mathf.Sqrt(2) * Density;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float x, float y, int chart)
            {
                int first = positions.Count;
                positions.AddRange(new[] { a, b, c, d });
                uv.AddRange(new[] { new Vector2(x, y), new Vector2(x + width, y),
                    new Vector2(x + width, y + Density), new Vector2(x, y + Density) });
                charts.AddRange(new[] { chart, chart, chart, chart });
                indices.AddRange(new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
            }
            for (int side = 0; side < 4; ++side)
                Quad(ring[side], ring[side + 1], ring[side + 1] + Vector3.up, ring[side] + Vector3.up, side * width, 0, 0);
            Vector3 outward = (ring[1] + ring[2]).normalized;
            Quad(ring[1] + Vector3.up, ring[2] + Vector3.up,
                ring[2] + Vector3.up + outward, ring[1] + Vector3.up + outward, width, Density, 1);
            var g = new RemeshNative.Geometry { positions = positions.ToArray(), uv = uv.ToArray(),
                indices = indices.ToArray(), charts = charts.ToArray(), chartCount = 2 };
            var before = (Vector2[])g.uv.Clone();
            Assert.AreEqual(g.positions[0], g.positions[13]);
            Assert.AreNotEqual(g.uv[0], g.uv[13], "the cylinder is cut open in UV");
            Assert.AreEqual(1, UvChartMerge.MergeCharts(g, new RemeshSettings(), CancellationToken.None, new HashSet<int>()));
            Assert.AreEqual(1, g.chartCount);
            for (int v = 0; v < 16; ++v) Assert.AreEqual(before[v], g.uv[v], "the existing cut and cylinder parameterization stay intact");
            Assert.AreEqual(g.uv[7], g.uv[16]); Assert.AreEqual(g.uv[6], g.uv[17]);
            var scan = UvAtlasDiagnostics.Measure(g, CancellationToken.None);
            Assert.IsTrue(scan.complete); Assert.AreEqual(0, scan.pairs);
            Assert.IsTrue(UvChartQuality.Measure(g, CancellationToken.None).valid);
        }
    }
}
