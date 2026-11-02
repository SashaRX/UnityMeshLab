using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshNamingTagsTests
    {
        // Words and surfaces are space-joined; an empty string is none.
        [TestCase("University_Bench_Straight _Seat_Wood_COL", null, "University_Bench_Straight _Seat_Wood",
            "University Bench Straight Seat Wood", "Wood", -1, true, -1)]
        [TestCase("University_Bench_Straight _Base_Concrete_COL_Hull2", null, "University_Bench_Straight _Base_Concrete",
            "University Bench Straight Base Concrete", "Concrete", -1, true, 2)]
        [TestCase("University_Bench_Straight _Backrest_Wood_LOD1", null, "University_Bench_Straight _Backrest_Wood",
            "University Bench Straight Backrest Wood", "Wood", 1, false, -1)]
        [TestCase("University_Bench_Straight _A", null, "University_Bench_Straight _A", "University Bench Straight A", "", -1, false, -1)]
        [TestCase("SM_University_TrashCan", "SM", "University_TrashCan", "University Trash Can", "", -1, false, -1)]
        [TestCase("sk_Hero_LOD0", "SK", "Hero", "Hero", "", 0, false, -1)]
        [TestCase("SM_Table_Wood_Metal_LOD2", "SM", "Table_Wood_Metal", "Table Wood Metal", "Wood Metal", 2, false, -1)]
        [TestCase("SM_Crate_Wood_COL_Hull0", "SM", "Crate_Wood", "Crate Wood", "Wood", -1, true, 0)]
        [TestCase("M_Wood_Trim", null, "M_Wood_Trim", "M Wood Trim", "Wood", -1, false, -1)]
        [TestCase("M_SovietBuilding_BaseAtlas_01", null, "M_SovietBuilding_BaseAtlas_01", "M Soviet Building Base Atlas 01", "", -1, false, -1)]
        [TestCase("WoodenCrate_Collider_01", null, "WoodenCrate", "Wooden Crate", "Wood", -1, true, -1)]
        [TestCase("Window_Glasses_LOD1_COL", null, "Window_Glasses", "Window Glasses", "Glass", 1, true, -1)]
        [TestCase("Wall-Tile Concrete_COL_Box", null, "Wall-Tile Concrete", "Wall Tile Concrete", "Tile Concrete", -1, true, -1)]
        [TestCase("Textile_Sofa_COLOR", null, "Textile_Sofa_COLOR", "Textile Sofa COLOR", "", -1, false, -1)]
        [TestCase("SMG_Rifle", null, "SMG_Rifle", "SMG Rifle", "", -1, false, -1)]
        [TestCase("SM_", null, "SM_", "SM", "", -1, false, -1)]
        public void ParseTags_ReadsTheNameTable(string name, string classPrefix, string body, string words, string surfaces,
            int lod, bool collision, int hull)
        {
            var tags = MeshNaming.ParseTags(name);
            Assert.AreEqual(name, tags.Name);
            Assert.AreEqual(classPrefix, tags.ClassPrefix, "class prefix");
            Assert.AreEqual(body, tags.Body, "body");
            Assert.AreEqual(words, string.Join(" ", tags.Words), "words");
            Assert.AreEqual(surfaces, string.Join(" ", tags.Surfaces), "surfaces");
            Assert.AreEqual(surfaces.Length == 0 ? null : surfaces.Split(' ')[0], tags.Surface, "first surface");
            Assert.AreEqual(lod, tags.Lod, "LOD");
            Assert.AreEqual(collision, tags.Collision, "collision");
            Assert.AreEqual(collision, MeshNaming.IsCollision(name), "agrees with IsCollision");
            Assert.AreEqual(hull, tags.Hull, "hull");
        }

        [Test]
        public void ParseTags_ReadsPipelineSuffixesAndTheInstanceNumber()
        {
            var tags = MeshNaming.ParseTags("Seat_Wood_LOD0_wc_repack.001");
            CollectionAssert.AreEqual(new[] {"wc", "repack"}, tags.PipelineTags);
            Assert.AreEqual(".001", tags.Instance);
            Assert.AreEqual(0, tags.Lod);
            Assert.AreEqual("Seat_Wood", tags.Body);
            Assert.AreEqual("Wood", tags.Surface);

            tags = MeshNaming.ParseTags("Seat_COL_Hull3.002");
            Assert.IsTrue(tags.Collision); Assert.AreEqual(3, tags.Hull); Assert.AreEqual(".002", tags.Instance);
            Assert.AreEqual("Seat", tags.Body); CollectionAssert.IsEmpty(tags.PipelineTags);
        }

        [Test]
        public void ParseTags_ReadsAgainstTheGivenVocabulary()
        {
            var vocabulary = new MeshNaming.Vocabulary(new[] {"PRP"}, new[] {("stone", "Stone"), ("brick", "Stone")});
            var tags = MeshNaming.ParseTags("prp_Wall_Bricks_LOD0", vocabulary);
            Assert.AreEqual("PRP", tags.ClassPrefix); Assert.AreEqual("Wall_Bricks", tags.Body);
            CollectionAssert.AreEqual(new[] {"Stone"}, tags.Surfaces);

            tags = MeshNaming.ParseTags("SM_Wall_Wood", vocabulary);
            Assert.IsNull(tags.ClassPrefix, "SM is not in this vocabulary"); Assert.AreEqual("SM_Wall_Wood", tags.Body);
            CollectionAssert.IsEmpty(tags.Surfaces, "nor is wood");
        }

        [TestCase("")]
        [TestCase(null)]
        public void ParseTags_ReadsNothingFromNoName(string name)
        {
            var tags = MeshNaming.ParseTags(name);
            Assert.AreEqual("", tags.Name); Assert.AreEqual("", tags.Body); Assert.IsNull(tags.ClassPrefix);
            Assert.AreEqual(-1, tags.Lod); Assert.IsFalse(tags.Collision); Assert.AreEqual(-1, tags.Hull);
            CollectionAssert.IsEmpty(tags.Words); CollectionAssert.IsEmpty(tags.Surfaces);
            CollectionAssert.IsEmpty(tags.PipelineTags); Assert.IsNull(tags.Instance); Assert.IsNull(tags.Surface);
        }
    }
}
