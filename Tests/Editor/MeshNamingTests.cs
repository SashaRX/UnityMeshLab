using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshNamingTests
    {
        [TestCase("Wall_LOD0", "Wall", 0)]
        [TestCase("Wall-LOD1", "Wall", 1)]
        [TestCase("Wall LOD2", "Wall", 2)]
        [TestCase("Wall_lod7", "Wall", 7)]
        [TestCase("Gate_01_LOD3", "Gate_01", 3)]
        public void TryParseLod_AcceptsTheRepoSuffixForms(string name, string expectedBase, int expectedLod)
        {
            Assert.IsTrue(MeshNaming.TryParseLod(name, out string baseName, out int lod));
            Assert.AreEqual(expectedBase, baseName); Assert.AreEqual(expectedLod, lod);
            Assert.AreEqual(expectedLod, MeshNaming.LodIndex(name));
            Assert.IsTrue(MeshNaming.HasLodSuffix(name));
        }

        [TestCase("Wall_LOD8")]
        [TestCase("Wall_LOD100000000")]
        [TestCase("Wall_LOD999999999999999999999")]
        public void TryParseLod_RejectsIndicesTheLodGroupCannotHold(string name)
        {
            Assert.IsFalse(MeshNaming.TryParseLod(name, out _, out int lod)); Assert.AreEqual(-1, lod);
            Assert.IsTrue(MeshNaming.HasLodSuffix(name), "the suffix is still a LOD suffix");
        }

        [TestCase("Wall")]
        [TestCase("WallLOD1")]
        [TestCase("LOD1")]
        [TestCase("Wall_LOD")]
        [TestCase("")]
        [TestCase(null)]
        public void TryParseLod_RejectsNamesWithoutASeparatedSuffix(string name)
        {
            Assert.IsFalse(MeshNaming.TryParseLod(name, out _, out _));
            Assert.IsFalse(MeshNaming.HasLodSuffix(name));
            Assert.AreEqual(name, MeshNaming.StripLod(name));
        }

        [Test]
        public void SplitLodSuffix_KeepsTheSuffixTextVerbatim()
        {
            Assert.AreEqual("Wall", MeshNaming.SplitLodSuffix("Wall-LOD1", out string suffix)); Assert.AreEqual("-LOD1", suffix);
            Assert.AreEqual("Wall", MeshNaming.SplitLodSuffix("Wall_lod0", out suffix)); Assert.AreEqual("_lod0", suffix);
            Assert.AreEqual("Wall", MeshNaming.SplitLodSuffix("Wall", out suffix)); Assert.AreEqual("", suffix);
            Assert.AreEqual("Wall_LOD2", MeshNaming.LodName("Wall", 2));
        }

        [TestCase("Wall_COL", true)]
        [TestCase("Wall_col", true)]
        [TestCase("Wall_COL_Hull3", true)]
        [TestCase("Wall_COL_Box", true)]
        [TestCase("Wall_Collider", true)]
        [TestCase("Wall_Collision", true)]
        [TestCase("Wall_COLOR", false)]
        [TestCase("Wall_COLLECTION", false)]
        [TestCase("Collider", false)]
        [TestCase("Wall", false)]
        public void IsCollision_FollowsTheSuffixRule(string name, bool expected)
        {
            Assert.AreEqual(expected, MeshNaming.IsCollision(name));
            Assert.AreEqual(expected ? "Wall" : name, MeshNaming.StripCollision(name));
        }

        [TestCase("Wall_LOD0", true)]
        [TestCase("Wall_COL", true)]
        [TestCase("Wall_Collider", true)]
        [TestCase("Wall", false)]
        [TestCase("Wall_COLOR", true)]   // the hygiene check is deliberately broad: COL\w*
        public void HasLodOrCollisionSuffix_MatchesTheHygieneRule(string name, bool expected)
            => Assert.AreEqual(expected, MeshNaming.HasLodOrCollisionSuffix(name));

        [TestCase("Wall_LOD2", "Wall")]
        [TestCase("Wall_COL", "Wall")]
        [TestCase("Wall_COL_Hull2", "Wall")]
        [TestCase("Wall_LOD1_COL", "Wall")]
        [TestCase("Wall-Collider", "Wall")]
        [TestCase("Wall_COL_Box", "Wall_COL_Box")]   // only Hull{N} is a group suffix; other COL_* forms keep the key
        [TestCase("Wall", "Wall")]
        public void GroupKey_PairsLodAndCollisionNodesOfOneAsset(string name, string expected)
            => Assert.AreEqual(expected, MeshNaming.GroupKey(name));

        [TestCase("Seat_LOD0.001", "Seat.001")]
        [TestCase("Seat_LOD2.002", "Seat.002")]
        [TestCase("Seat_COL_Hull2.001", "Seat.001")]
        [TestCase("Seat_LOD1.abc", "Seat_LOD1.abc")]
        public void GroupKey_PreservesNumericInstanceSuffixWhileRemovingLod(string name, string expected)
            => Assert.AreEqual(expected, UvToolContext.ExtractGroupKey(name));

        [TestCase("Chair_wc_repack_LOD0", "Chair")]
        [TestCase("Chair_LOD1_optimized", "Chair")]
        [TestCase("Chair_uvTransfer", "Chair")]
        [TestCase("Chair", "Chair")]
        public void StripPipelineSuffixes_RemovesEveryTransferSuffix(string name, string expected)
            => Assert.AreEqual(expected, MeshNaming.StripPipelineSuffixes(name));
    }
}
