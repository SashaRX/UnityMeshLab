using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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

            Assert.That(FindLodSiblings(selected), Is.Null);
        }

        [Test]
        public void FindLodSiblings_IgnoresUnsupportedSiblingIndices()
        {
            var selected = CreateChild("Asset_LOD0");
            CreateChild("Asset_LOD1");
            CreateChild("Asset_LOD999999999999999999999");
            CreateChild("Asset_LOD100000000");

            var siblings = FindLodSiblings(selected);

            Assert.That(siblings, Has.Count.EqualTo(2));
            Assert.That(siblings[0].lodIndex, Is.EqualTo(0));
            Assert.That(siblings[1].lodIndex, Is.EqualTo(1));
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_AutoCreatedGroup_RestoresLod0Transition()
        {
            var lods = new List<LOD> { new LOD(0.01f, new Renderer[0]) };

            NormalizeSingleLodTransitionForGeneration(lods, 1);

            Assert.AreEqual(0.5f, lods[0].screenRelativeTransitionHeight);
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_ExistingTransition_PreservesValue()
        {
            var lods = new List<LOD> { new LOD(0.25f, new Renderer[0]) };

            NormalizeSingleLodTransitionForGeneration(lods, 1);

            Assert.AreEqual(0.25f, lods[0].screenRelativeTransitionHeight);
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_NotAppendingAfterLod0_PreservesValue()
        {
            var lods = new List<LOD> { new LOD(0.01f, new Renderer[0]) };

            NormalizeSingleLodTransitionForGeneration(lods, 2);

            Assert.AreEqual(0.01f, lods[0].screenRelativeTransitionHeight);
        }

        GameObject CreateChild(string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(root.transform);
            return child;
        }

        // The helpers below are internal to the editor assembly, so the test
        // assembly reaches them through reflection like the other suites here.
        static void NormalizeSingleLodTransitionForGeneration(List<LOD> lods, int startLod)
        {
            var method = typeof(LodGenerationTool).GetMethod(
                "NormalizeSingleLodTransitionForGeneration",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method,
                "LodGenerationTool should expose the LOD0 transition normalization as a static helper");

            method.Invoke(null, new object[] { lods, startLod });
        }

        static List<(GameObject go, int lodIndex)> FindLodSiblings(GameObject go)
        {
            var method = typeof(LodGenerationTool).GetMethod(
                "FindLodSiblings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "LodGenerationTool should expose FindLodSiblings as a static helper");

            object result = method.Invoke(null, new object[] { go });
            if (result == null) return null;

            var siblings = new List<(GameObject go, int lodIndex)>();
            foreach (object item in (IEnumerable)result)
            {
                var type = item.GetType();
                siblings.Add((
                    (GameObject)type.GetField("Item1").GetValue(item),
                    (int)type.GetField("Item2").GetValue(item)));
            }
            return siblings;
        }
    }
}
