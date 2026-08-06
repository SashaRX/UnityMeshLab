using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class LodGenerationToolTests
    {
        GameObject root;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Root");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
        }

        [TestCase("Asset_LOD999999999999999999999")]
        [TestCase("Asset_LOD8")]
        [TestCase("Asset_LOD100000000")]
        public void FindLodSiblings_RejectsUnsupportedIndices(string name)
        {
            var selected = CreateChild(name);

            Assert.That(LodGenerationTool.FindLodSiblings(selected), Is.Null);
        }

        [Test]
        public void FindLodSiblings_IgnoresUnsupportedSiblingIndices()
        {
            var selected = CreateChild("Asset_LOD0");
            CreateChild("Asset_LOD1");
            CreateChild("Asset_LOD999999999999999999999");
            CreateChild("Asset_LOD100000000");

            var siblings = LodGenerationTool.FindLodSiblings(selected);

            Assert.That(siblings, Has.Count.EqualTo(2));
            Assert.That(siblings[0].lodIndex, Is.EqualTo(0));
            Assert.That(siblings[1].lodIndex, Is.EqualTo(1));
        }

        GameObject CreateChild(string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(root.transform);
            return child;
        }
    }
}
