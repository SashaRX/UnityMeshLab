using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class SpatialPartitionerTests
    {
        [Test]
        public void PartitionShells_FacesSharingGridCellsAndVertex_DoNotOverlap()
        {
            var uv = BuildFullBoundsUvs(4);
            var triangles = new[]
            {
                0, 1, 2,
                0, 3, 4,
                0, 5, 6,
                0, 7, 8
            };

            var result = PartitionSingleShell(uv, triangles);

            Assert.IsFalse(result.hasOverlap,
                "Faces that only meet through a shared vertex must not be treated as UV overlap");
        }

        [Test]
        public void PartitionShells_NonAdjacentFaceInSharedGridCells_DetectsOverlap()
        {
            var uv = BuildFullBoundsUvs(4);
            var triangles = new[]
            {
                0, 1, 2,
                0, 3, 4,
                0, 5, 6,
                9, 7, 8
            };

            var result = PartitionSingleShell(uv, triangles);

            Assert.IsTrue(result.hasOverlap,
                "A face sharing grid cells but no vertex must be treated as UV overlap");
        }

        static Vector2[] BuildFullBoundsUvs(int faceCount)
        {
            var uv = new Vector2[faceCount * 2 + 2];
            uv[0] = Vector2.zero;
            for (int i = 0; i < faceCount; i++)
            {
                uv[i * 2 + 1] = Vector2.right;
                uv[i * 2 + 2] = Vector2.up;
            }
            uv[9] = Vector2.zero;
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
