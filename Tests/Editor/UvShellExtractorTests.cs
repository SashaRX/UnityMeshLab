// UvShellExtractorTests.cs — coverage for UV-shell extraction and overlap-group detection.
// Smoke tests + the critical contract that ExtractWithOverlap correctly identifies
// stacked tile-like shells (the case that drove the group-aware repack architecture).

using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvShellExtractorTests
    {
        // Build a flat quad (two triangles) at the given UV bbox.
        static (Vector2[] uv, int[] tris, int vertOffset) BuildQuad(float u0, float v0, float u1, float v1, int baseVertex)
        {
            var uv = new Vector2[]
            {
                new Vector2(u0, v0),
                new Vector2(u1, v0),
                new Vector2(u1, v1),
                new Vector2(u0, v1),
            };
            var tris = new int[]
            {
                baseVertex + 0, baseVertex + 1, baseVertex + 2,
                baseVertex + 0, baseVertex + 2, baseVertex + 3,
            };
            return (uv, tris, baseVertex + 4);
        }

        static (Vector2[] uv, int[] tris) Combine(params (Vector2[] uv, int[] tris, int _)[] quads)
        {
            var uvList = new List<Vector2>();
            var triList = new List<int>();
            foreach (var q in quads)
            {
                uvList.AddRange(q.uv);
                triList.AddRange(q.tris);
            }
            return (uvList.ToArray(), triList.ToArray());
        }

        [Test]
        public void Extract_TwoDisjointQuads_FindsTwoShells()
        {
            // Two quads in entirely separate UV regions
            var q1 = BuildQuad(0.0f, 0.0f, 0.3f, 0.3f, 0);
            var q2 = BuildQuad(0.6f, 0.6f, 0.9f, 0.9f, q1._);
            var (uv, tris) = Combine(q1, q2);

            var shells = UvShellExtractor.Extract(uv, tris);

            Assert.AreEqual(2, shells.Count, "Disjoint UV patches should produce 2 shells");
            Assert.AreEqual(4, shells[0].vertexIndices.Count);
            Assert.AreEqual(4, shells[1].vertexIndices.Count);
        }

        [Test]
        public void FindOverlapGroups_StackedTiles_DetectsSingleGroup()
        {
            // 4 quads stacked in the same UV region — the canonical tile pattern
            var q1 = BuildQuad(0.1f, 0.1f, 0.4f, 0.4f, 0);
            var q2 = BuildQuad(0.1f, 0.1f, 0.4f, 0.4f, q1._);
            var q3 = BuildQuad(0.1f, 0.1f, 0.4f, 0.4f, q2._);
            var q4 = BuildQuad(0.1f, 0.1f, 0.4f, 0.4f, q3._);
            var (uv, tris) = Combine(q1, q2, q3, q4);

            var shells = UvShellExtractor.Extract(uv, tris);
            Assert.AreEqual(4, shells.Count, "Four topologically-disjoint stacked tiles should be 4 shells");

            var groups = UvShellExtractor.FindOverlapGroups(shells);
            Assert.AreEqual(1, groups.Count, "All four overlapping tiles should form ONE overlap group");
            Assert.AreEqual(4, groups[0].Count, "The group should contain all four tile shells");
        }

        [Test]
        public void FindOverlapGroups_DisjointPlusTiles_SeparatesGroups()
        {
            // 3 stacked tiles + 1 isolated patch
            var t1 = BuildQuad(0.1f, 0.1f, 0.4f, 0.4f, 0);
            var t2 = BuildQuad(0.1f, 0.1f, 0.4f, 0.4f, t1._);
            var t3 = BuildQuad(0.1f, 0.1f, 0.4f, 0.4f, t2._);
            var iso = BuildQuad(0.7f, 0.7f, 0.95f, 0.95f, t3._);
            var (uv, tris) = Combine(t1, t2, t3, iso);

            var shells = UvShellExtractor.Extract(uv, tris);
            Assert.AreEqual(4, shells.Count);

            var groups = UvShellExtractor.FindOverlapGroups(shells);
            Assert.AreEqual(1, groups.Count, "Only the three stacked tiles form a group; the isolated patch is standalone");
            Assert.AreEqual(3, groups[0].Count);
        }

        [Test]
        public void CountAabbOverlaps_StackedTiles_ReturnsExpectedPairs()
        {
            // N stacked tiles → C(N,2) overlap pairs
            const int N = 5;
            var quads = new List<(Vector2[] uv, int[] tris, int _)>();
            int baseV = 0;
            for (int i = 0; i < N; i++)
            {
                var q = BuildQuad(0.2f, 0.2f, 0.5f, 0.5f, baseV);
                quads.Add(q);
                baseV = q._;
            }
            var (uv, tris) = Combine(quads.ToArray());
            var shells = UvShellExtractor.Extract(uv, tris);

            int pairs = UvShellExtractor.CountAabbOverlaps(shells);
            Assert.AreEqual(N * (N - 1) / 2, pairs, "Stacked tiles produce all-pairs overlap count");
        }

        [Test]
        public void BuildPerFaceShellIds_AssignsConsistentIdsPerShell()
        {
            var q1 = BuildQuad(0.0f, 0.0f, 0.3f, 0.3f, 0);
            var q2 = BuildQuad(0.5f, 0.5f, 0.8f, 0.8f, q1._);
            var (uv, tris) = Combine(q1, q2);

            List<UvShell> shells;
            List<List<int>> overlapGroups;
            uint[] faceShellIds = UvShellExtractor.BuildPerFaceShellIds(uv, tris, out shells, out overlapGroups);

            Assert.AreEqual(4, faceShellIds.Length, "2 quads × 2 tris each = 4 faces");
            // Faces 0/1 belong to one shell, 2/3 to the other
            Assert.AreEqual(faceShellIds[0], faceShellIds[1], "Faces of the same quad must share shellId");
            Assert.AreEqual(faceShellIds[2], faceShellIds[3], "Faces of the same quad must share shellId");
            Assert.AreNotEqual(faceShellIds[0], faceShellIds[2], "Faces of different shells must have different shellIds");
            Assert.AreEqual(0, overlapGroups.Count, "Disjoint quads — no overlap groups");
        }
    }
}
