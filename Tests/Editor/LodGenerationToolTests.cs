using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class LodGenerationToolTests
    {
        [Test]
        public void NormalizeSingleLodTransitionForGeneration_AutoCreatedGroup_RestoresLod0Transition()
        {
            var lods = new List<LOD> { new LOD(0.01f, new Renderer[0]) };

            LodGenerationTool.NormalizeSingleLodTransitionForGeneration(lods, 1);

            Assert.AreEqual(0.5f, lods[0].screenRelativeTransitionHeight);
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_ExistingTransition_PreservesValue()
        {
            var lods = new List<LOD> { new LOD(0.25f, new Renderer[0]) };

            LodGenerationTool.NormalizeSingleLodTransitionForGeneration(lods, 1);

            Assert.AreEqual(0.25f, lods[0].screenRelativeTransitionHeight);
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_NotAppendingAfterLod0_PreservesValue()
        {
            var lods = new List<LOD> { new LOD(0.01f, new Renderer[0]) };

            LodGenerationTool.NormalizeSingleLodTransitionForGeneration(lods, 2);

            Assert.AreEqual(0.01f, lods[0].screenRelativeTransitionHeight);
        }
    }
}
