// UvTopologyTests.cs — the UV layout topology helpers on small hand-built layouts.
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvTopologyTests
    {
        // Two triangles sharing the edge 1–2, a third one on its own island.
        //   0 ─ 1      4
        //   │ / │      │ \
        //   3 ─ 2      5 ─ 6
        static readonly Vector2[] Uv =
        {
            new Vector2(0, 1), new Vector2(.4f, 1), new Vector2(.4f, .6f), new Vector2(0, .6f),
            new Vector2(.6f, 1), new Vector2(.6f, .6f), new Vector2(1, .6f),
        };
        static readonly int[] Tri = { 0, 1, 2, 0, 2, 3, 4, 5, 6 };

        static HashSet<(int, int)> Edges(int[] pairs)
        {
            var set = new HashSet<(int, int)>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                set.Add((Mathf.Min(pairs[i], pairs[i + 1]), Mathf.Max(pairs[i], pairs[i + 1])));
            return set;
        }

        [Test]
        public void BoundaryEdgesAreTheOnesASingleFaceUses()
        {
            var edges = Edges(UvTopology.BoundaryEdgePairs(Tri));
            Assert.AreEqual(7, edges.Count);
            Assert.IsFalse(edges.Contains((0, 2)), "the shared diagonal is interior");
            foreach (var e in new[] { (0, 1), (1, 2), (2, 3), (0, 3), (4, 5), (5, 6), (4, 6) })
                Assert.IsTrue(edges.Contains(e), e.ToString());
        }

        [Test]
        public void BoundaryEdgesOfAFaceSubsetIncludeTheCutEdge()
        {
            var edges = Edges(UvTopology.BoundaryEdgePairs(Tri, new List<int> { 0 }));
            CollectionAssert.AreEquivalent(new[] { (0, 1), (1, 2), (0, 2) }, edges);
            Assert.AreEqual(0, UvTopology.BoundaryEdgePairs(null).Length);
            Assert.AreEqual(0, UvTopology.BoundaryEdgePairs(new[] { 7, 7, 7 }).Length, "a degenerate face has no edges");
        }

        [Test]
        public void BoundaryLengthSumsTheIslandOutline()
        {
            // The right island is a right triangle with legs .4 and .4.
            float expected = .4f + .4f + Mathf.Sqrt(.32f);
            Assert.AreEqual(expected, UvTopology.BoundaryLength(Uv, Tri, new List<int> { 2 }), 1e-5f);
            Assert.AreEqual(expected + 1.6f, UvTopology.BoundaryLength(Uv, Tri), 1e-5f, "plus the square's perimeter");
        }

        [Test]
        public void UniqueEdgesListEveryEdgeOnceInFirstSeenOrder()
        {
            var edges = UvTopology.UniqueEdges(Tri);
            Assert.AreEqual(16, edges.Count, "8 distinct edges, two indices each");
            Assert.AreEqual(new[] { 0, 1, 1, 2, 2, 0 }, edges.GetRange(0, 6));
            Assert.IsNull(UvTopology.UniqueEdges(Tri, maxFaces: 2));
        }

        [Test]
        public void PointInTriangleAcceptsEitherWindingAndTheEdges()
        {
            Vector2 a = new Vector2(0, 0), b = new Vector2(1, 0), c = new Vector2(0, 1);
            Assert.IsTrue(UvTopology.PointInTriangle(new Vector2(.2f, .2f), a, b, c));
            Assert.IsTrue(UvTopology.PointInTriangle(new Vector2(.2f, .2f), a, c, b));
            Assert.IsTrue(UvTopology.PointInTriangle(new Vector2(.5f, 0), a, b, c), "on an edge");
            Assert.IsTrue(UvTopology.PointInTriangle(a, a, b, c), "on a vertex");
            Assert.IsFalse(UvTopology.PointInTriangle(new Vector2(.6f, .6f), a, b, c));
        }

        [Test]
        public void ShellVoteTakesTheMajorityThenTheFirstKnown()
        {
            var map = new[] { 1, 1, 2, -1, 3 };
            Assert.AreEqual(1, UvTopology.VoteBestShell(map, 0, 1, 2));
            Assert.AreEqual(2, UvTopology.VoteBestShell(map, 3, 2, 4), "no majority: the first vertex with a shell");
            Assert.AreEqual(-1, UvTopology.VoteBestShell(map, 3, 3, 9), "out of range counts as unknown");
        }

        [Test]
        public void FaceToShellAndShellDataAgreeOnTheTwoIslands()
        {
            var faceToShell = UvTopology.FaceToShell(Uv, Tri);
            Assert.AreEqual(3, faceToShell.Length);
            Assert.AreEqual(faceToShell[0], faceToShell[1], "the square's two faces are one island");
            Assert.AreNotEqual(faceToShell[0], faceToShell[2]);

            var data = UvTopology.BuildShellData(Uv, Tri);
            Assert.AreEqual(2, data.shells.Count);
            Assert.AreEqual(faceToShell[2], data.faceToShell[2]);
            int rightIsland = data.shells.FindIndex(s => s.faceIndices.Contains(2));
            var b = data.shellBounds[rightIsland];
            Assert.AreEqual(new Vector3(.6f, .6f, 0), b.min);
            Assert.AreEqual(new Vector3(1f, 1f, 0), b.max);
            Assert.IsNull(UvTopology.BuildShellData(Uv, new int[0]));
        }

        [Test]
        public void OccupiedTilesFloorTheUvsAndSkipNonFinite()
        {
            var tiles = new HashSet<Vector2Int>();
            UvTopology.OccupiedTiles(new[] { new Vector2(.5f, .5f), new Vector2(1.2f, -.3f), new Vector2(float.NaN, 0) }, tiles);
            CollectionAssert.AreEquivalent(new[] { new Vector2Int(0, 0), new Vector2Int(1, -1) }, tiles);
            UvTopology.OccupiedTiles(new[] { new Vector2(5, 5) }, tiles, u => u.x < 2);
            Assert.AreEqual(2, tiles.Count, "the filter rejected the far tile");
        }

        [TestCase(EventType.Repaint, false)]
        [TestCase(EventType.Layout, false)]
        [TestCase(EventType.Used, false)]
        [TestCase(EventType.MouseMove, true)]
        [TestCase(EventType.MouseDown, true)]
        [TestCase(EventType.MouseDrag, true)]
        public void SpotPickingRunsOnlyForPointerInput(EventType type, bool expected)
            => Assert.AreEqual(expected, UvCanvasView.IsSpotInput(type));

        [Test]
        public void BackgroundBoundaryPreparationPreservesTheSourceTriangles()
        {
            var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.right, Vector3.one, Vector3.up };
            var uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.right, Vector2.one, Vector2.up };
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };
            var original = (int[])triangles.Clone();
            Assert.AreEqual(8, UvTopology.UvBoundaryEdgePairs(vertices, uv, triangles).Length);
            CollectionAssert.AreEqual(original, triangles, "worker must not mutate snapshots used by shell extraction");
            uv[3] = new Vector2(2, 0); uv[4] = new Vector2(3, 1); uv[5] = new Vector2(2, 1);
            Assert.AreEqual(12, UvTopology.UvBoundaryEdgePairs(vertices, uv, triangles).Length);
        }

        static Mesh PreviewMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2 };
            return mesh;
        }

        [UnityTest]
        public IEnumerator PreviewMissReturnsImmediatelyAndPublishesWorkerResultOnUpdate()
        {
            var mesh = PreviewMesh(); var ctx = new UvToolContext(); var canvas = new UvCanvasView();
            try {
                Assert.IsNull(canvas.GetPreviewShellCache(ctx, mesh, 0));
                Assert.IsEmpty(ctx.PreviewShellDataCache, "draw must only enqueue preparation");
                var timeout = Stopwatch.StartNew();
                PreviewShellData data = null;
                while (data == null && timeout.Elapsed.TotalSeconds < 10) {
                    canvas.PollPreviewJobs();
                    data = canvas.GetPreviewShellCache(ctx, mesh, 0);
                    yield return null;
                }
                Assert.IsNotNull(data);
                Assert.AreEqual(1, data.shells.Count);
                Assert.AreEqual(6, canvas.GetPreviewBoundary(ctx, mesh, 0).Length);
                Assert.AreEqual(1, canvas.GetUv0ShellMap(ctx, mesh).descs.Length);
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator InvalidatedWorkerDoesNotPublishAndFreshRequestStillCompletes()
        {
            var mesh = PreviewMesh(); var ctx = new UvToolContext(); var canvas = new UvCanvasView();
            try {
                canvas.GetPreviewShellCache(ctx, mesh, 0); canvas.PollPreviewJobs();
                ctx.ClearAllCaches(); canvas.ClearFrameCaches();
                mesh.uv = new[] { new Vector2(2, 0), new Vector2(3, 0), new Vector2(2, 1) };
                Assert.IsNull(canvas.GetPreviewShellCache(ctx, mesh, 0));
                var timeout = Stopwatch.StartNew();
                PreviewShellData data = null;
                while (data == null && timeout.Elapsed.TotalSeconds < 10) {
                    canvas.PollPreviewJobs(); data = canvas.GetPreviewShellCache(ctx, mesh, 0);
                    if (data != null) Assert.AreEqual(2, data.uvs[0].x, "late result from the old mesh snapshot must be dropped");
                    yield return null;
                }
                Assert.IsNotNull(data);
                canvas.ClearFrameCaches(); ctx.ClearAllCaches();
                canvas.GetPreviewShellCache(ctx, mesh, 0); canvas.PollPreviewJobs();
                canvas.Cleanup();
                yield return null;
                canvas.PollPreviewJobs();
                Assert.IsEmpty(ctx.PreviewShellDataCache, "closed canvas must never publish its worker result");
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void LockBeforeFirstHitStillAllowsPickingAndClearRearmsIt()
        {
            var mesh = PreviewMesh(); var ctx = new UvToolContext { PreviewUvChannel = 0 };
            var entry = new MeshEntry { originalMesh = mesh, include = true };
            var canvas = new UvCanvasView { SpotMode = true, EntriesOverride = new List<MeshEntry> { entry } };
            try {
                ctx.PreviewShellDataCache[(long)mesh.GetInstanceID() << 8] = UvTopology.BuildShellData(mesh.uv, mesh.triangles);
                canvas.SetSelectionLock(true);
                Assert.IsFalse(canvas.SpotSelectionLocked, "Lock cannot freeze an empty hit");
                canvas.UpdateUvSpot(ctx, new Vector2(.1f, .1f), true);
                Assert.IsTrue(canvas.HasHoveredShell); Assert.IsTrue(canvas.HasSelectedShell);
                Assert.IsTrue(canvas.SpotSelectionLocked); Assert.IsNotNull(canvas.SelectedShellDebug);
                var selected = canvas.SelectedShell;
                canvas.UpdateUvSpot(ctx, new Vector2(.8f, .8f), true);
                Assert.AreEqual(selected.uvHit, canvas.SelectedShell.uvHit);
                Assert.AreEqual(selected.uvHit, canvas.CanvasSpotUv, "locked point must not follow the mouse");
                canvas.ClearSpotSelection();
                Assert.IsFalse(canvas.SpotSelectionLocked);
                canvas.UpdateUvSpot(ctx, new Vector2(.2f, .2f), true);
                Assert.AreEqual(new Vector2(.2f, .2f), canvas.SelectedShell.uvHit);
                canvas.ClearHoverState(false);
                Assert.IsFalse(canvas.SpotSelectionLocked, "changing content re-arms Lock instead of disabling picking");
                canvas.ApplySpotHit(true, new ShellUvHit { meshEntry = entry, shellId = -1 }, null);
                Assert.IsFalse(canvas.HasHoveredShell, "pending topology is never a valid hit");
                Assert.IsFalse(canvas.SelectSpotHover());
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void EnablingLockPinsTheHoveredIslandAndUnlockResumesHover()
        {
            var mesh = PreviewMesh(); var ctx = new UvToolContext { PreviewUvChannel = 0 };
            var entry = new MeshEntry { originalMesh = mesh, include = true };
            var canvas = new UvCanvasView { SpotMode = true, EntriesOverride = new List<MeshEntry> { entry } };
            try {
                ctx.PreviewShellDataCache[(long)mesh.GetInstanceID() << 8] = UvTopology.BuildShellData(mesh.uv, mesh.triangles);
                canvas.UpdateUvSpot(ctx, new Vector2(.1f, .1f));
                canvas.SetSelectionLock(true);
                Assert.IsTrue(canvas.HasSelectedShell);
                canvas.ApplySpotHit(false, default, null);
                Assert.IsTrue(canvas.HasHoveredShell); Assert.IsTrue(canvas.HasSelectedShell);
                canvas.SetSelectionLock(false);
                canvas.UpdateUvSpot(ctx, new Vector2(.3f, .2f));
                Assert.AreEqual(new Vector2(.3f, .2f), canvas.HoveredShell.uvHit);
                Assert.AreEqual(new Vector2(.1f, .1f), canvas.SelectedShell.uvHit, "hover does not change the pinned selection");
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator StationaryUvClickIsResolvedWhenBackgroundTopologyBecomesReady()
        {
            var mesh = PreviewMesh(); var ctx = new UvToolContext { PreviewUvChannel = 0 };
            var entry = new MeshEntry { originalMesh = mesh, include = true };
            var canvas = new UvCanvasView { SpotMode = true, EntriesOverride = new List<MeshEntry> { entry } };
            try {
                canvas.SetSelectionLock(true);
                canvas.UpdateUvSpot(ctx, new Vector2(.2f, .2f), true);
                Assert.IsFalse(canvas.HasHoveredShell); Assert.IsFalse(canvas.HasSelectedShell);
                var timeout = Stopwatch.StartNew();
                while (!canvas.HasSelectedShell && timeout.Elapsed.TotalSeconds < 10) {
                    canvas.PollPreviewJobs();
                    yield return null;
                }
                Assert.IsTrue(canvas.HasHoveredShell); Assert.IsTrue(canvas.HasSelectedShell);
                Assert.AreEqual(new Vector2(.2f, .2f), canvas.SelectedShell.uvHit);
                Assert.IsTrue(canvas.SpotSelectionLocked);
                Assert.IsNotNull(canvas.SelectedShellDebug);
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator ThreeDSpotWaitsForWorkerThenReturnsTheActualIslandAndWorldPoint()
        {
            var mesh = PreviewMesh(); var ctx = new UvToolContext { PreviewUvChannel = 0 };
            var entry = new MeshEntry { originalMesh = mesh, include = true };
            var canvas = new UvCanvasView { SpotMode = true, EntriesOverride = new List<MeshEntry> { entry } };
            using (var viewport = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY })
            using (var layer = new UvLayer3D())
            try {
                viewport.Frame(new Bounds(new Vector3(.5f, .5f, 0), new Vector3(1, 1, .1f)));
                typeof(MeshViewport3D).GetField("currentRect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(viewport, new Rect(0, 0, 100, 100));
                var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) };
                var entries = new[] { entry };
                var pointer = new Vector2(45, 55);
                Assert.IsFalse(layer.Pick(viewport, canvas, ctx, pointer, items, entries, out _, out _, out _),
                    "pending shell data must never become a shellId -1 hit");
                var timeout = Stopwatch.StartNew();
                while (canvas.GetPreviewShellCache(ctx, mesh, 0) == null && timeout.Elapsed.TotalSeconds < 10) {
                    canvas.PollPreviewJobs(); yield return null;
                }
                Assert.IsTrue(layer.Pick(viewport, canvas, ctx, pointer, items, entries, out var hit, out var debug, out var world));
                Assert.GreaterOrEqual(hit.shellId, 0); Assert.IsNotNull(debug);
                Assert.AreSame(entry, hit.meshEntry); Assert.AreEqual(hit.shellId, debug.shellId);
                Assert.That(world.z, Is.EqualTo(0).Within(1e-5f));
                Assert.That(world.x, Is.InRange(.2f, .5f)); Assert.That(world.y, Is.InRange(.2f, .5f));
                Assert.That((hit.uvHit - new Vector2(world.x, world.y)).sqrMagnitude, Is.LessThan(1e-8f));
                canvas.SetSelectionLock(true); canvas.ApplySpotHit(true, hit, debug, world);
                Assert.IsTrue(canvas.HoverHitValid); Assert.IsTrue(canvas.SelectSpotHover());
                Assert.IsTrue(canvas.SpotSelectionLocked);
                canvas.ApplySpotHit(false, default, null);
                Assert.IsTrue(canvas.HasSelectedShell); Assert.AreEqual(hit.uvHit, canvas.SelectedShell.uvHit);
            }
            finally { canvas.Cleanup(); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ReadUvReturnsNullForAnEmptyChannel()
        {
            var m = new Mesh();
            try
            {
                m.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                m.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                Assert.AreEqual(3, UvTopology.ReadUv(m, 0).Length);
                Assert.IsNull(UvTopology.ReadUv(m, 1));
                Assert.IsTrue(UvTopology.HasUv(m, 0));
                Assert.IsFalse(UvTopology.HasUv(m, 1));
            }
            finally { Object.DestroyImmediate(m); }
        }
    }
}
