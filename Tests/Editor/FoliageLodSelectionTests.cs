using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageLodSelectionTests
    {
        sealed class Tree : IDisposable
        {
            internal readonly GameObject root = new GameObject("Tree");
            internal readonly Material leaf = new Material(Shader.Find("Standard"));
            internal readonly Material trunk = new Material(Shader.Find("Standard"));
            internal readonly List<FoliageNormalTransferTests.Fixture> meshes = new List<FoliageNormalTransferTests.Fixture>();
            internal readonly MeshRenderer[] leaves = new MeshRenderer[3];
            internal readonly MeshRenderer[] trunks = new MeshRenderer[3];
            internal readonly LODGroup group;

            internal Tree(bool combined = false, bool namedOnly = false)
            {
                for (int level = 0; level < 3; level++)
                {
                    leaves[level] = Add("Leaves_LOD" + level);
                    trunks[level] = Add("Trunk_LOD" + level);
                    leaves[level].sharedMaterials = combined ? new[] { trunk, leaf } : new[] { leaf, leaf };
                    trunks[level].sharedMaterials = new[] { trunk, trunk };
                }
                if (!namedOnly)
                {
                    group = root.AddComponent<LODGroup>();
                    group.SetLODs(new[] { new LOD(.6f, new Renderer[] { trunks[0], leaves[0] }),
                        new LOD(.3f, new Renderer[] { leaves[1], trunks[1] }), new LOD(.1f, new Renderer[] { trunks[2], leaves[2] }) });
                }
            }

            internal MeshRenderer Add(string name)
            {
                var fixture = new FoliageNormalTransferTests.Fixture();
                fixture.root.name = name; fixture.mesh.name = name;
                fixture.root.transform.SetParent(root.transform, false);
                fixture.mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                fixture.mesh.tangents = Array.ConvertAll(fixture.mesh.vertices, _ => new Vector4(1, 0, 0, 1));
                fixture.mesh.colors32 = new[] { new Color32(1, 2, 3, 4), new Color32(20, 30, 40, 50), new Color32(60, 70, 80, 90), new Color32(100, 110, 120, 130) };
                meshes.Add(fixture);
                return fixture.renderer;
            }

            public void Dispose()
            {
                foreach (var mesh in meshes) mesh.Dispose();
                Object.DestroyImmediate(root); Object.DestroyImmediate(leaf); Object.DestroyImmediate(trunk);
            }
        }

        static FoliageNormalsSettings Settings(MeshRenderer selected) => new FoliageNormalsSettings
        {
            filterRenderers = true, renderers = new[] { selected }, normalSource = FoliageNormalSource.Sphere,
            proxySegments = 16, gpuProjection = false, recalculateTangents = false
        };

        [UnityTest]
        public IEnumerator SelectedMeshAppliesAndSavesEveryLodWithOneUndoAndPreservesOtherParts()
        {
            using var tree = new Tree(); using var other = new Tree(); using var pipeline = new FoliageNormalsPipeline();
            other.root.transform.SetParent(tree.root.transform, false);
            string folder = "Assets/FoliageLods_" + Guid.NewGuid().ToString("N");
            var applied = new List<Mesh>();
            try
            {
                var settings = Settings(tree.leaves[0]);
                var task = pipeline.Run(tree.root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                CollectionAssert.AreEqual(tree.leaves, pipeline.Targets.Select(t => t.renderer));
                Assert.AreEqual(1, pipeline.CaptureSourceCount);
                var paths = pipeline.Save(folder); Assert.AreEqual(3, paths.Length); Assert.AreEqual(3, paths.Distinct().Count());
                for (int level = 0; level < 3; level++)
                {
                    var target = pipeline.Targets[level]; var saved = AssetDatabase.LoadAssetAtPath<Mesh>(paths[level]);
                    Assert.IsTrue(target.working.normals.Where((n, i) => Vector3.Dot(n, target.localNormals[i]) < .99f).Any());
                    CollectionAssert.AreEqual(target.working.normals, saved.normals);
                    CollectionAssert.AreEqual(target.localPositions, saved.vertices);
                }
                pipeline.Apply(); Undo.FlushUndoRecordObjects(); Assert.IsTrue(pipeline.CanApply);
                foreach (var target in pipeline.Targets)
                {
                    applied.Add(target.filter.sharedMesh); Assert.AreNotSame(target.source, target.filter.sharedMesh);
                    CollectionAssert.AreEqual(target.localNormals, target.source.normals);
                }
                foreach (var renderer in tree.trunks.Concat(other.leaves).Concat(other.trunks))
                    Assert.AreSame(renderer.GetComponent<MeshFilter>().sharedMesh, tree.meshes.Concat(other.meshes).Single(f => f.renderer == renderer).mesh);
                pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
                Assert.IsTrue(applied.All(m => m));
                Undo.PerformUndo();
                for (int i = 0; i < tree.leaves.Length; i++) Assert.AreSame(tree.meshes.Single(f => f.renderer == tree.leaves[i]).mesh, tree.leaves[i].GetComponent<MeshFilter>().sharedMesh);
                Undo.PerformRedo();
                for (int i = 0; i < tree.leaves.Length; i++) Assert.AreNotSame(tree.meshes.Single(f => f.renderer == tree.leaves[i]).mesh, tree.leaves[i].GetComponent<MeshFilter>().sharedMesh);
            }
            finally
            {
                pipeline.Dispose(); Undo.ClearAll(); AssetDatabase.DeleteAsset(folder);
                foreach (var mesh in applied) if (mesh) Object.DestroyImmediate(mesh);
                foreach (var renderer in tree.leaves)
                {
                    var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    if (mesh && !tree.meshes.Any(f => f.mesh == mesh)) Object.DestroyImmediate(mesh);
                }
            }
        }

        [UnityTest]
        public IEnumerator SelectedSubmeshFollowsReorderedMaterialSlotsAndPreservesExcludedChannels()
        {
            using var tree = new Tree(true); using var pipeline = new FoliageNormalsPipeline();
            tree.leaves[1].sharedMaterials = new[] { tree.leaf, tree.trunk };
            var settings = Settings(tree.leaves[0]); settings.selection = FoliageFaceSelection.SelectedSubmeshIndices; settings.submeshes = new[] { 1 };
            var fingerprints = tree.meshes.Select(f => FoliageTargetSnapshot.ContentFingerprint(f.mesh)).ToArray();
            var task = pipeline.Run(tree.root, settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            Assert.AreEqual(3, pipeline.Targets.Count);
            for (int level = 0; level < 3; level++)
            {
                var target = pipeline.Targets[level]; int selected = level == 1 ? 0 : 1, excluded = 1 - selected;
                Assert.IsTrue(target.selectedSubmeshes[selected]); Assert.IsFalse(target.selectedSubmeshes[excluded]);
                CollectionAssert.AreEqual(target.original.GetIndices(excluded), target.working.GetIndices(excluded));
                foreach (int vertex in target.original.GetIndices(excluded))
                {
                    Assert.AreEqual(target.localNormals[vertex], target.working.normals[vertex]);
                    Assert.AreEqual(target.original.tangents[vertex], target.working.tangents[vertex]);
                }
                for (int vertex = 0; vertex < target.localPositions.Length; vertex++)
                {
                    Assert.AreEqual(target.localPositions[vertex], target.working.vertices[vertex]);
                    Assert.AreEqual(target.original.uv[vertex], target.working.uv[vertex]);
                    Assert.AreEqual(target.original.colors32[vertex], target.working.colors32[vertex]);
                }
                Assert.IsTrue(target.working.GetIndices(selected).Any(i => Vector3.Dot(target.working.normals[i], Vector3.forward) < .99f));
            }
            CollectionAssert.AreEqual(fingerprints, tree.meshes.Select(f => FoliageTargetSnapshot.ContentFingerprint(f.mesh)));
        }

        [UnityTest]
        public IEnumerator HigherLodChildRootIncludesItsSiblingsAndUsesOnlyLod0ForProxy()
        {
            using var tree = new Tree(); using var pipeline = new FoliageNormalsPipeline();
            tree.leaves[1].transform.localPosition = Vector3.right * 20;
            var settings = Settings(tree.leaves[1]);
            var task = pipeline.Run(tree.leaves[1].gameObject, settings, FoliageNormalsPipeline.Stage.Capture);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            CollectionAssert.AreEqual(tree.leaves, pipeline.Targets.Select(t => t.renderer)); Assert.AreEqual(1, pipeline.CaptureSourceCount);
            Assert.That(pipeline.CaptureBounds.center.x, Is.LessThan(-19));
            settings.lod0Only = false;
            task = pipeline.Run(tree.leaves[1].gameObject, settings, FoliageNormalsPipeline.Stage.Capture);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
            Assert.AreEqual(3, pipeline.CaptureSourceCount); Assert.That(pipeline.CaptureBounds.max.x, Is.GreaterThan(.9f));
        }

        [TestCase(false)] [TestCase(true)]
        public void NamedLodSiblingsExpandSelectionWithinTheirParent(bool childRoot)
        {
            using var tree = new Tree(namedOnly: true); using var other = new Tree(namedOnly: true);
            other.root.transform.SetParent(tree.root.transform, false);
            var targets = FoliageLodSelection.Resolve(childRoot ? tree.leaves[0].gameObject : tree.root, Settings(tree.leaves[0]), CancellationToken.None);
            CollectionAssert.AreEqual(tree.leaves, targets.Select(t => t.renderer)); Assert.AreEqual(1, targets.Count(t => t.proxySource));
        }

        [Test]
        public void SoleRendererPerLodCanMatchWithoutNameSuffixesAndSharedLevelsAreDeduplicated()
        {
            using var tree = new Tree();
            for (int i = 0; i < tree.leaves.Length; i++) { tree.leaves[i].name = "Part " + i; tree.leaves[i].GetComponent<MeshFilter>().sharedMesh.name = "Mesh " + i; }
            tree.group.SetLODs(new[] { new LOD(.6f, new[] { tree.leaves[0] }), new LOD(.3f, new[] { tree.leaves[1] }), new LOD(.1f, new[] { tree.leaves[1] }) });
            var targets = FoliageLodSelection.Resolve(tree.root, Settings(tree.leaves[0]), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { tree.leaves[0], tree.leaves[1] }, targets.Select(t => t.renderer));
        }

        [Test]
        public void DistinctMeshNamesMatchPartsWhenRendererNamesDifferBetweenLods()
        {
            using var tree = new Tree();
            for (int i = 0; i < tree.leaves.Length; i++) tree.leaves[i].name = "Foliage detail " + i;
            var targets = FoliageLodSelection.Resolve(tree.root, Settings(tree.leaves[0]), CancellationToken.None);
            CollectionAssert.AreEqual(tree.leaves, targets.Select(t => t.renderer));
        }

        [TestCase(false)] [TestCase(true)]
        public void DuplicateOriginRendererNamesUseDistinctMeshNamesToPreserveTheSelectedPart(bool selectLeaves)
        {
            using var tree = new Tree();
            tree.leaves[0].name = "Part_LOD0"; tree.trunks[0].name = "Part_LOD0";
            var selected = selectLeaves ? tree.leaves : tree.trunks;
            var excluded = selectLeaves ? tree.trunks : tree.leaves;
            excluded[1].name = "Part_LOD1";
            var targets = FoliageLodSelection.Resolve(tree.root, Settings(selected[0]), CancellationToken.None);
            CollectionAssert.AreEqual(selected, targets.Select(t => t.renderer));
            Assert.IsFalse(targets.Any(t => excluded.Contains(t.renderer)));
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingSelectedMaterialLeavesThatLodUntouched(bool byIndex)
        {
            using var tree = new Tree(true);
            tree.leaves[1].sharedMaterials = new[] { tree.trunk, tree.trunk };
            var settings = Settings(tree.leaves[0]);
            settings.selection = byIndex ? FoliageFaceSelection.SelectedSubmeshIndices : FoliageFaceSelection.SelectedMaterials;
            settings.submeshes = new[] { 1 }; settings.materials = new[] { tree.leaf };
            var targets = FoliageLodSelection.Resolve(tree.root, settings, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { tree.leaves[0], tree.leaves[2] }, targets.Select(t => t.renderer));
        }

        [Test]
        public void ASelectedSingleSubmeshDoesNotIncludeNewMaterialsInLowerLods()
        {
            using var tree = new Tree(true);
            var mesh = tree.leaves[0].GetComponent<MeshFilter>().sharedMesh;
            mesh.subMeshCount = 1; mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            tree.leaves[0].sharedMaterials = new[] { tree.leaf };
            var settings = Settings(tree.leaves[0]); settings.selection = FoliageFaceSelection.SelectedSubmeshIndices; settings.submeshes = new[] { 0 };
            var targets = FoliageLodSelection.Resolve(tree.root, settings, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { true }, targets[0].submeshes);
            CollectionAssert.AreEqual(new[] { false, true }, targets[1].submeshes);
            CollectionAssert.AreEqual(new[] { false, true }, targets[2].submeshes);
        }

        [TestCase(false)] [TestCase(true)]
        public void AmbiguousSubmeshMaterialCannotBroadenTheLodSelection(bool missing)
        {
            using var tree = new Tree(true);
            tree.leaves[0].sharedMaterials = missing ? Array.Empty<Material>() : new[] { tree.leaf, tree.leaf };
            var settings = Settings(tree.leaves[0]); settings.selection = FoliageFaceSelection.SelectedSubmeshIndices; settings.submeshes = new[] { 1 };
            var error = Assert.Throws<InvalidOperationException>(() => FoliageLodSelection.Resolve(tree.root, settings, CancellationToken.None));
            StringAssert.Contains("distinct material references", error.Message);
            foreach (var fixture in tree.meshes) Assert.AreSame(fixture.mesh, fixture.renderer.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void DuplicateLodNamesAreRejectedInsteadOfTouchingAnotherPart()
        {
            using var tree = new Tree(); var duplicate = tree.Add("Leaves_LOD1");
            var lods = tree.group.GetLODs(); lods[1].renderers = new Renderer[] { tree.leaves[1], duplicate, tree.trunks[1] }; tree.group.SetLODs(lods);
            Assert.Throws<InvalidOperationException>(() => FoliageLodSelection.Resolve(tree.root, Settings(tree.leaves[0]), CancellationToken.None));
        }

        [UnityTest]
        public IEnumerator AddedLodRecipientInvalidatesTheCapturedResult()
        {
            using var tree = new Tree(); using var pipeline = new FoliageNormalsPipeline();
            var settings = Settings(tree.leaves[0]); var task = pipeline.Run(tree.root, settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status); Assert.IsTrue(pipeline.CanApply);
            var added = tree.Add("Leaves_LOD3"); added.sharedMaterials = new[] { tree.leaf, tree.leaf };
            tree.group.SetLODs(tree.group.GetLODs().Concat(new[] { new LOD(.05f, new[] { added }) }).ToArray());
            Assert.IsFalse(pipeline.CaptureCurrent()); Assert.Throws<InvalidOperationException>(() => pipeline.Apply());
            Assert.Throws<InvalidOperationException>(() => pipeline.Save("Assets")); Assert.Throws<InvalidOperationException>(() => pipeline.SaveFbx("Assets"));
        }

        [UnityTest]
        public IEnumerator PreviewShowsOneLodWhileTheResultRetainsEveryRecipient()
        {
            using var tree = new Tree(); var tool = new FoliageNormalsTool();
            try
            {
                tool.SetRoot(tree.root); tool.settings = Settings(tree.leaves[0]);
                var task = tool.pipeline.Run(tree.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, tool.pipeline.LodLevels);
                for (int level = 0; level < 3; level++)
                {
                    tool.previewLod = level; var items = new List<MeshViewport3D.Item>(); Assert.IsTrue(tool.Get3DContent(items));
                    Assert.AreEqual(1, items.Count); Assert.AreSame(tool.pipeline.Targets[level].working, items[0].mesh);
                    Assert.AreEqual(3, tool.pipeline.Targets.Count); Assert.IsTrue(tool.CanApplyPreview);
                }
            }
            finally { tool.pipeline.Dispose(); }
        }
    }
}
