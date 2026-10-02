// UvTopologyTests.cs — the UV layout topology helpers on small hand-built layouts.
using System.Collections.Generic;
using NUnit.Framework;
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
