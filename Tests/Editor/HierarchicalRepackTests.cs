using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class HierarchicalRepackTests
    {
        [Test]
        public void CanonicalFrame_PreservesIslandShapeAndNormalizesArea()
        {
            var shell = new HierarchicalRepack.Shell3D { faceIndices = new List<int> { 0 }, totalArea = 4f };
            var uv = new[] { Vector2.zero, new Vector2(2, 0), new Vector2(0, 1) };
            Assert.IsTrue(HierarchicalRepack.TryCanonicalUvFrame(shell, new[] { 0, 1, 2 }, uv, out var center, out float scale));
            Assert.That(center.x, Is.EqualTo(2f / 3f).Within(1e-6));
            Assert.That(center.y, Is.EqualTo(1f / 3f).Within(1e-6));
            Assert.That(scale, Is.EqualTo(2f).Within(1e-6));
            CollectionAssert.AreEqual(new[] { Vector2.zero, new Vector2(2, 0), new Vector2(0, 1) }, uv);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Placement_RecoversAffineAndKeepsDegenerateAxis(bool degenerateV)
        {
            var input = new HierarchicalRepack.DomainPackInput(1);
            input.pendingScale[0] = 1;
            var fit = new HierarchicalRepack.DomainPlacementFit(1);
            var coords = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1) };
            for (int i = 0; i < coords.Length; i++)
            {
                float v = degenerateV ? 0 : coords[i].y;
                input.inputVertGroup.Add(0); input.inputVertU.Add(coords[i].x); input.inputVertV.Add(v);
                fit.Add(i, new Vector2(coords[i].x * .25f + .5f, v * .25f + .75f), input);
            }
            var placement = fit.Placement(0, input, 32f, 64);
            Assert.IsTrue(placement.valid);
            Assert.That(placement.su, Is.EqualTo(.25f).Within(1e-6));
            Assert.That(placement.sv, Is.EqualTo(.25f).Within(1e-6));
            Assert.That(placement.ou, Is.EqualTo(.5f).Within(1e-6));
            Assert.That(placement.ov, Is.EqualTo(.75f).Within(1e-6));
        }

        [Test]
        public void Placement_PointChartUsesPackDensityAndMissingGroupStaysInvalid()
        {
            var input = new HierarchicalRepack.DomainPackInput(2);
            input.pendingScale[0] = 1;
            input.inputVertGroup.Add(0); input.inputVertU.Add(0); input.inputVertV.Add(0);
            var fit = new HierarchicalRepack.DomainPlacementFit(2);
            fit.Add(0, new Vector2(.4f, .7f), input);
            var placement = fit.Placement(0, input, 32f, 64);
            Assert.IsTrue(placement.valid);
            Assert.That(placement.su, Is.EqualTo(.5f).Within(1e-6));
            Assert.That(placement.sv, Is.EqualTo(.5f).Within(1e-6));
            Assert.That(placement.ou, Is.EqualTo(.4f).Within(1e-6));
            Assert.That(placement.ov, Is.EqualTo(.7f).Within(1e-6));
            Assert.IsFalse(fit.Placement(1, input, 32f, 64).valid);
        }

        [Test]
        public void Build_PacksSharedDomainWithoutChangingAuthoredMeshes()
        {
            RequireNativeXatlas();
            var root = new GameObject("HierarchyRefactorTest");
            var mesh = new Mesh { name = "SharedPlane" };
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                mesh.RecalculateNormals();
                var lods = new LOD[2];
                for (int li = 0; li < lods.Length; li++)
                {
                    var child = new GameObject("Plane_LOD" + li);
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    lods[li] = new LOD(.5f / (li + 1), new Renderer[] { child.AddComponent<MeshRenderer>() });
                }
                var group = root.AddComponent<LODGroup>();
                group.SetLODs(lods);
                var options = HierarchicalRepack.Options.Default;
                options.atlasResolutionPx = 64;
                options.interDomainPaddingPx = 1;
                options.proxySampleDensity = 64;
                var result = HierarchicalRepack.Build(group, options);
                Assert.IsNull(result.error);
                Assert.That(result.domainPlacements, Has.Length.EqualTo(1));
                Assert.IsTrue(result.domainPlacements[0].valid);
                Assert.That(result.domainAtlasUv, Is.Not.Empty);
                Assert.That(result.finalUv2[0], Is.Not.Empty);
                Assert.That(result.finalUv2[1], Is.Not.Empty);
                CollectionAssert.AreEqual(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, mesh.uv);
                Assert.That(mesh.uv2, Is.Empty);
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>()) Assert.AreSame(mesh, filter.sharedMesh);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
        }

        static void RequireNativeXatlas()
        {
            try { XatlasNative.xatlasCreate(); XatlasNative.xatlasDestroy(); }
            catch (DllNotFoundException) { Assert.Ignore("xatlas native plugin not available"); }
            catch (EntryPointNotFoundException) { Assert.Ignore("xatlas native plugin not available"); }
        }
    }
}
