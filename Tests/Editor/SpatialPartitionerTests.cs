using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class SpatialPartitionerTests
    {
        [Test]
        public void PartitionShells_FoldedFacesSharingVertex_DetectsOverlap()
        {
            var uv = BuildFullBoundsUvs(4);
            uv[7] = uv[1]; uv[8] = uv[2];
            var triangles = new[]
            {
                0, 1, 2,
                0, 3, 4,
                0, 5, 6,
                0, 7, 8
            };

            var result = PartitionSingleShell(uv, triangles);

            Assert.IsTrue(result.hasOverlap,
                "Sharing a vertex does not make a positive-area fold legal");
        }

        [Test]
        public void PartitionShells_NonAdjacentFaceInSharedGridCells_DetectsOverlap()
        {
            var uv = BuildFullBoundsUvs(4);
            uv[7] = uv[1]; uv[8] = uv[2];
            var triangles = new[]
            {
                0, 1, 2,
                0, 3, 4,
                0, 5, 6,
                9, 7, 8
            };

            var result = PartitionSingleShell(uv, triangles);

            Assert.IsTrue(result.hasOverlap,
                "Disconnected faces with a positive-area intersection must be treated as UV overlap");
        }

        [Test]
        public void PartitionShells_DegenerateTriangleSharingVertex_DoesNotOverlap()
        {
            var uv = BuildFullBoundsUvs(4);
            var triangles = new[]
            {
                0, 1, 2,
                0, 3, 4,
                0, 5, 6,
                0, 7, 7  // degenerate: two of its edges are the same vertex pair
            };

            var result = PartitionSingleShell(uv, triangles);

            Assert.IsFalse(result.hasOverlap,
                "Zero-area triangles and shared edges do not have positive overlap area");
        }

        static Vector2[] BuildFullBoundsUvs(int faceCount)
        {
            var uv = new Vector2[faceCount * 2 + 2];
            uv[0] = new Vector2(.5f, .5f);
            var ring = new[] { new Vector2(1, .5f), new Vector2(.5f, 1), new Vector2(0, .5f), new Vector2(.5f, 0) };
            for (int i = 0; i < faceCount; i++)
            {
                uv[i * 2 + 1] = ring[i % 4];
                uv[i * 2 + 2] = ring[(i + 1) % 4];
            }
            uv[9] = uv[0];
            return uv;
        }

        static SpatialPartitioner.ShellPartitionResult PartitionSingleShell(
            Vector2[] uv, int[] triangles)
        {
            var shell = new UvShell
            {
                shellId = 0,
                boundsMin = Vector2.zero,
                boundsMax = Vector2.one,
                faceIndices = new List<int> { 0, 1, 2, 3 }
            };
            var vertices = new Vector3[uv.Length];

            return SpatialPartitioner.PartitionShells(
                new List<UvShell> { shell }, uv, triangles, vertices)[0];
        }
    }
}
