using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class SpatialPartitionerTests
    {
        [TestCase(1e-9f)] [TestCase(1f)] [TestCase(1e9f)]
        public void PositiveOverlapIsIndependentOfUniformUvScale(float scale)
        {
            var uv=new[] {Vector2.zero,Vector2.right*scale,Vector2.up*scale};
            var original=(Vector2[])uv.Clone();
            var shell=new UvShell {shellId=1,faceIndices=new List<int> {0,1}};
            CollectionAssert.AreEquivalent(new[] {0,1},SpatialPartitioner.DetectOverlap(shell,uv,new[] {0,1,2,0,1,2}));
            CollectionAssert.AreEqual(original,uv);
            Assert.IsEmpty(SpatialPartitioner.DetectOverlap(shell,uv,new[] {0,1,2,0,1,1}));
        }
        [TestCase(0)] [TestCase(1)]
        public void IncompleteOverlapScanCannotCertifySourceShell(int budget)
        {
            var uv=new[] {Vector2.zero,Vector2.right,Vector2.up};
            var triangles=new[] {0,1,2,0,1,2,0,1,2,0,1,2};
            var shell=new UvShell {shellId=9,faceIndices=new List<int> {0,1,2,3}};
            var saved=(Vector2[])uv.Clone(); var savedIndices=(int[])triangles.Clone();
            StringAssert.Contains("scan incomplete",Assert.Throws<System.InvalidOperationException>(()=>
                SpatialPartitioner.DetectOverlap(shell,uv,triangles,budget)).Message);
            CollectionAssert.AreEqual(saved,uv); CollectionAssert.AreEqual(savedIndices,triangles);
        }

        [Test]
        public void MatchPartitionTellsSameFacingPartitionsApartByPosition()
        {
            // Parallel panels (a position split): the normal ties and the nearer centroid decides.
            var panels = new SpatialPartitioner.ShellPartitionResult {
                hasOverlap = true, partitionCount = 2,
                partitionCentroid = new[] { Vector3.zero, Vector3.right * 10 },
                partitionNormal = new[] { Vector3.up, Vector3.up } };
            Assert.AreEqual(0, SpatialPartitioner.MatchPartition(panels, Vector3.right, Vector3.up));
            Assert.AreEqual(1, SpatialPartitioner.MatchPartition(panels, Vector3.right * 9, Vector3.up));
            // Front and back of a strap at one place: the normal still decides.
            var strap = new SpatialPartitioner.ShellPartitionResult {
                hasOverlap = true, partitionCount = 2,
                partitionCentroid = new[] { Vector3.zero, Vector3.down * .01f },
                partitionNormal = new[] { Vector3.up, Vector3.down } };
            Assert.AreEqual(0, SpatialPartitioner.MatchPartition(strap, Vector3.down * .01f, Vector3.up));
            Assert.AreEqual(1, SpatialPartitioner.MatchPartition(strap, Vector3.zero, Vector3.down));
        }

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
