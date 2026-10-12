using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageNormalsPreviewTests
    {
        const string Preferences = "MeshLab.FoliageNormals.Settings";
        sealed class ToolFixture : IDisposable
        {
            readonly string preferences = EditorPrefs.HasKey(Preferences) ? EditorPrefs.GetString(Preferences) : null;
            readonly Object selection = Selection.activeObject;
            internal readonly FoliageNormalsTool tool = new FoliageNormalsTool();
            internal ToolFixture()
            {
                tool.settings = new FoliageNormalsSettings { normalSource = FoliageNormalSource.Sphere,
                    proxySegments = 16, gpuProjection = false, recalculateTangents = false };
            }
            public void Dispose()
            {
                tool.OnDeactivate();
                Selection.activeObject = selection;
                if (preferences == null) EditorPrefs.DeleteKey(Preferences); else EditorPrefs.SetString(Preferences, preferences);
            }
        }

        internal static IEnumerator AwaitPreview(FoliageNormalsPreview preview)
        {
            double end = EditorApplication.timeSinceStartup + 15;
            while (preview.IsPending && EditorApplication.timeSinceStartup < end) yield return null;
            Assert.IsFalse(preview.IsPending, "Selection paint must finish without blocking editor updates.");
        }

        [UnityTest]
        public IEnumerator HierarchySelectionChangesTreeWithoutDraggingAndReleasesPreviousPreview()
        {
            using var first = new FoliageNormalTransferTests.Fixture();
            using var second = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture();
            var tool = fixture.tool;
            Selection.activeGameObject = first.root; tool.OnActivate(null, null);
            yield return AwaitPreview(tool.sourcePreview);
            var previous = tool.sourcePreview.Sources(0).Single().mask;
            var task = tool.pipeline.Run(first.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
            tool.preview = FoliageNormalsTool.Preview.BeforeAfter;
            Selection.activeGameObject = second.root;
            for (int i = 0; i < 5 && tool.root != second.root; i++) yield return null;
            Assert.AreSame(second.root, tool.root);
            Assert.AreEqual(FoliageNormalsTool.Preview.SelectionMask, tool.preview, "A new tree cannot retain a stale side-by-side result mode.");
            Assert.IsFalse(previous, "Old selection paint is destroyed when the source changes.");
            Assert.AreEqual(0, tool.pipeline.Targets.Count);
            yield return AwaitPreview(tool.sourcePreview);
            var items = new List<MeshViewport3D.Item>(); tool.Get3DContent(items);
            Assert.AreEqual(1, items.Count);
            Assert.IsTrue(items[0].mesh);
            Assert.AreSame(second.renderer, tool.sourcePreview.Sources(0).Single().renderer);
            Assert.AreSame(first.mesh, first.root.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(second.mesh, second.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator SelectionDuringCaptureCancelsAndShowsLatestTree()
        {
            using var first = new FoliageNormalTransferTests.Fixture();
            using var second = new FoliageNormalTransferTests.Fixture();
            using var third = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture();
            var tool = fixture.tool;
            Selection.activeGameObject = first.root; tool.OnActivate(null, null);
            var task = tool.pipeline.Run(first.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
            Assert.IsTrue(tool.pipeline.IsRunning);
            Selection.activeGameObject = second.root;
            Selection.activeGameObject = third.root;
            yield return FoliageNormalsToolTests.Await(task);
            Assert.IsFalse(task.Result);
            for (int i = 0; i < 5 && tool.root != third.root; i++) yield return null;
            Assert.AreSame(third.root, tool.root);
            yield return AwaitPreview(tool.sourcePreview);
            Assert.AreSame(third.renderer, tool.sourcePreview.Sources(0).Single().renderer);
            Assert.IsFalse(tool.pipeline.Has(FoliageNormalsPipeline.Stage.Transfer));
        }

        [UnityTest]
        public IEnumerator SelectionCanReturnToRunningSourceAndCanBePinnedOrUnsubscribed()
        {
            using var first = new FoliageNormalTransferTests.Fixture();
            using var second = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture();
            var tool = fixture.tool;
            Selection.activeGameObject = first.root; tool.OnActivate(null, null);
            var task = tool.pipeline.Run(first.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
            Selection.activeGameObject = second.root; Selection.activeGameObject = first.root;
            yield return FoliageNormalsToolTests.Await(task);
            for (int i = 0; i < 3; i++) yield return null;
            Assert.AreSame(first.root, tool.root, "Returning to the source cancels the queued replacement.");
            tool.followSelection = false; Selection.activeGameObject = second.root;
            Assert.AreSame(first.root, tool.root);
            tool.followSelection = true; tool.OnDeactivate();
            Selection.activeGameObject = first.root; Selection.activeGameObject = second.root;
            Assert.AreSame(first.root, tool.root, "Inactive tools must release selection subscriptions.");
            Assert.IsFalse(tool.sourcePreview.IsPending);
            Assert.IsEmpty(tool.sourcePreview.Sources(0));
        }

        [UnityTest]
        public IEnumerator SelectingLodChildUsesItsGroupAndNonMeshSelectionKeepsTree()
        {
            using var leaf = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture();
            var root = new GameObject("Tree group"); var light = new GameObject("Light");
            try
            {
                leaf.root.transform.SetParent(root.transform, false);
                root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(.5f, new Renderer[] { leaf.renderer }) });
                light.AddComponent<Light>();
                Selection.activeGameObject = leaf.root; fixture.tool.OnActivate(null, null);
                Assert.AreSame(root, fixture.tool.root);
                Selection.activeGameObject = light; Assert.AreSame(root, fixture.tool.root);
                yield return AwaitPreview(fixture.tool.sourcePreview);
            }
            finally { fixture.tool.OnDeactivate(); Object.DestroyImmediate(root); Object.DestroyImmediate(light); }
        }

        [UnityTest]
        public IEnumerator IneligibleLodChildrenKeepTreeAndDoNotCancelAnActiveCapture()
        {
            using var first = new FoliageNormalTransferTests.Fixture();
            using var member = new FoliageNormalTransferTests.Fixture();
            using var collision = new FoliageNormalTransferTests.Fixture();
            using var disabled = new FoliageNormalTransferTests.Fixture();
            using var noFilter = new FoliageNormalTransferTests.Fixture();
            using var standalone = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture();
            var group = new GameObject("Other tree"); var parent = new GameObject("LOD container");
            var light = new GameObject("Light"); var empty = new GameObject("Helper");
            try
            {
                parent.transform.SetParent(group.transform, false); member.root.transform.SetParent(parent.transform, false);
                foreach (var child in new[] { collision.root, disabled.root, noFilter.root, standalone.root, light, empty })
                    child.transform.SetParent(group.transform, false);
                light.AddComponent<Light>(); collision.root.name = "Tree_COL"; disabled.renderer.enabled = false;
                Object.DestroyImmediate(noFilter.root.GetComponent<MeshFilter>());
                group.AddComponent<LODGroup>().SetLODs(new[] { new LOD(.5f, new Renderer[] { member.renderer }) });
                var tool = fixture.tool; Selection.activeGameObject = first.root; tool.OnActivate(null, null);
                var unsupported = new[] { light, empty, collision.root, disabled.root, noFilter.root };
                foreach (var child in unsupported)
                {
                    Selection.activeGameObject = child; tool.FollowSelection();
                    Assert.AreSame(first.root, tool.root, child.name);
                }
                Selection.activeGameObject = first.root; tool.FollowSelection();
                var task = tool.pipeline.Run(first.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                Assert.IsTrue(tool.pipeline.IsRunning);
                foreach (var child in unsupported)
                {
                    Selection.activeGameObject = child; tool.FollowSelection();
                    Assert.AreSame(first.root, tool.root, child.name);
                }
                yield return FoliageNormalsToolTests.Await(task);
                Assert.IsTrue(task.Result, "Unsupported LOD children must not cancel an active calculation: " + tool.pipeline.Status);
                tool.pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
                task = tool.pipeline.Run(first.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                Selection.activeGameObject = member.root; tool.FollowSelection();
                Selection.activeGameObject = light; tool.FollowSelection();
                yield return FoliageNormalsToolTests.Await(task); Assert.IsFalse(task.Result);
                for (int frame = 0; frame < 5; frame++) yield return null;
                Assert.AreSame(first.root, tool.root, "An unsupported latest LOD child clears a queued replacement.");
                Selection.activeGameObject = standalone.root; tool.FollowSelection();
                Assert.AreSame(standalone.root, tool.root, "An eligible nonmember is followed without promoting unrelated LOD renderers.");
                Selection.activeGameObject = member.root; tool.FollowSelection(); Assert.AreSame(group, tool.root);
                Assert.AreSame(group, FoliageLodSelection.SelectionRoot(parent), "A mesh-less parent containing an eligible LOD member is supported.");
                Assert.AreSame(group, FoliageLodSelection.SelectionRoot(group));
                Assert.AreSame(first.mesh, first.root.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreSame(member.mesh, member.root.GetComponent<MeshFilter>().sharedMesh);
                yield return AwaitPreview(tool.sourcePreview);
            }
            finally { fixture.tool.OnDeactivate(); Object.DestroyImmediate(group); }
        }

        [UnityTest]
        public IEnumerator IneligibleLatestSelectionCancelsTheQueuedTreeReplacement()
        {
            using var first = new FoliageNormalTransferTests.Fixture();
            using var second = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture();
            using var collisionRenderer = new FoliageNormalTransferTests.Fixture();
            using var collisionMesh = new FoliageNormalTransferTests.Fixture();
            using var disabled = new FoliageNormalTransferTests.Fixture();
            using var noFilter = new FoliageNormalTransferTests.Fixture();
            using var noMesh = new FoliageNormalTransferTests.Fixture();
            using var inactive = new FoliageNormalTransferTests.Fixture();
            var light = new GameObject("Light"); var camera = new GameObject("Camera"); var empty = new GameObject("Empty");
            try
            {
                light.AddComponent<Light>(); camera.AddComponent<Camera>();
                collisionRenderer.root.name = "Tree_COL"; collisionMesh.mesh.name = "Tree_COL";
                disabled.renderer.enabled = false;
                Object.DestroyImmediate(noFilter.root.GetComponent<MeshFilter>());
                noFilter.root.transform.SetParent(first.root.transform, false);
                noMesh.root.GetComponent<MeshFilter>().sharedMesh = null;
                inactive.root.SetActive(false);
                Selection.activeGameObject = first.root; fixture.tool.OnActivate(null, null);
                fixture.tool.settings.filterRenderers = true; fixture.tool.settings.renderers = new[] { first.renderer };
                foreach (var selection in new Object[] { light, camera, empty, first.mesh, null,
                    collisionRenderer.root, collisionMesh.root, disabled.root, noFilter.root, noMesh.root, inactive.root })
                {
                    Selection.activeObject = selection; fixture.tool.FollowSelection();
                    Assert.AreSame(first.root, fixture.tool.root, "An unsupported selection cannot discard an idle tree.");
                    Selection.activeGameObject = first.root; fixture.tool.FollowSelection();
                    fixture.tool.pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
                    var task = fixture.tool.pipeline.Run(first.root, fixture.tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                    Assert.IsTrue(fixture.tool.pipeline.IsRunning);
                    Selection.activeGameObject = second.root; fixture.tool.FollowSelection();
                    Selection.activeObject = selection; fixture.tool.FollowSelection();
                    yield return FoliageNormalsToolTests.Await(task); Assert.IsFalse(task.Result, selection ? selection.name : "empty selection");
                    for (int frame = 0; frame < 5; frame++) yield return null;
                    Assert.AreSame(first.root, fixture.tool.root, "An ineligible latest selection keeps the current tree.");
                }
                Selection.activeGameObject = first.root; fixture.tool.FollowSelection();
                var finalTask = fixture.tool.pipeline.Run(first.root, fixture.tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(finalTask);
                Assert.IsTrue(finalTask.Result, fixture.tool.pipeline.Status);
                Assert.IsTrue(fixture.tool.pipeline.CanApply, "A mesh-less child must not invalidate a valid capture.");
                fixture.tool.OnRefresh(); yield return AwaitPreview(fixture.tool.sourcePreview);
                Assert.IsNull(fixture.tool.sourcePreview.Error);
                Assert.AreEqual(1, fixture.tool.sourcePreview.Sources(0).Count(), "Renderers without meshes are omitted from source context.");
            }
            finally { Object.DestroyImmediate(light); Object.DestroyImmediate(camera); Object.DestroyImmediate(empty); }
        }

        [UnityTest]
        public IEnumerator NonTriangleAndEmptyMeshSelectionsKeepTheWorkingTreeAndDoNotCancelRuns()
        {
            using var first = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture(); var tool = fixture.tool;
            Selection.activeGameObject = first.root; tool.OnActivate(null, null);
            foreach (var topology in new[] { MeshTopology.Lines, MeshTopology.Points, MeshTopology.Triangles })
                foreach (bool unreadable in new[] { false, true })
                {
                    using var unsupported = new FoliageNormalTransferTests.Fixture();
                    unsupported.mesh.Clear(); unsupported.mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                    unsupported.mesh.SetIndices(topology == MeshTopology.Lines ? new[] { 0, 1 } :
                        topology == MeshTopology.Points ? new[] { 0 } : Array.Empty<int>(), topology, 0);
                    if (unreadable) unsupported.mesh.UploadMeshData(true);
                    Assert.IsFalse(FoliageLodSelection.HasEligibleRenderer(unsupported.root));
                    Selection.activeGameObject = unsupported.root; tool.FollowSelection(); Assert.AreSame(first.root, tool.root);
                    Selection.activeGameObject = first.root; tool.FollowSelection();
                    tool.pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
                    var task = tool.pipeline.Run(first.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                    Assert.IsTrue(tool.pipeline.IsRunning);
                    Selection.activeGameObject = unsupported.root; tool.FollowSelection();
                    yield return FoliageNormalsToolTests.Await(task);
                    Assert.IsTrue(task.Result, tool.pipeline.Status); Assert.AreSame(first.root, tool.root);
                    Assert.IsTrue(tool.pipeline.CanApply);
                    Assert.AreSame(first.mesh, first.root.GetComponent<MeshFilter>().sharedMesh);
                }
        }

        [UnityTest]
        public IEnumerator HierarchyRefreshesReplayWhenSuccessfulOrFailedCalculationsBecomeIdle()
        {
            foreach (bool fail in new[] { false, true })
            {
                using var leaf = new FoliageNormalTransferTests.Fixture(); using var context = new FoliageNormalTransferTests.Fixture();
                using var fixture = new ToolFixture(); var tool = fixture.tool;
                Selection.activeGameObject = leaf.root; tool.OnActivate(null, null);
                var hierarchy = (Action)Delegate.CreateDelegate(typeof(Action), tool,
                    typeof(FoliageNormalsTool).GetMethod("HierarchyChanged", BindingFlags.Instance | BindingFlags.NonPublic));
                // Deliver the hierarchy event explicitly while running, so a later
                // automatic idle event cannot conceal a dropped notification.
                EditorApplication.hierarchyChanged -= hierarchy;
                tool.settings.filterRenderers = true; tool.settings.renderers = new[] { leaf.renderer };
                var capture = tool.pipeline.Run(leaf.root, tool.settings, FoliageNormalsPipeline.Stage.Capture);
                yield return FoliageNormalsToolTests.Await(capture); Assert.IsTrue(capture.Result, tool.pipeline.Status);
                yield return AwaitPreview(tool.sourcePreview);
                var previous = tool.sourcePreview.Sources(0).Single().mask;
                var task = tool.pipeline.Run(leaf.root, tool.settings,
                    fail ? FoliageNormalsPipeline.Stage.Transfer : FoliageNormalsPipeline.Stage.Scatter);
                Assert.IsTrue(tool.pipeline.IsRunning);
                context.root.transform.SetParent(leaf.root.transform, false);
                if (fail)
                {
                    leaf.mesh.vertices = leaf.mesh.vertices.Select(p => p + Vector3.right).ToArray(); leaf.mesh.RecalculateBounds();
                    LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Source.*changed.*Recapture"));
                }
                hierarchy(); Assert.IsTrue(previous, "Active calculations defer the preview replacement.");
                yield return FoliageNormalsToolTests.Await(task); Assert.AreEqual(!fail, task.Result, tool.pipeline.Status);
                yield return AwaitPreview(tool.sourcePreview);
                Assert.IsFalse(previous); Assert.AreEqual(2, tool.sourcePreview.Sources(0).Count());
                Assert.IsTrue(tool.sourcePreview.Sources(0).All(source => source.mask));
                Assert.AreEqual(!fail, tool.pipeline.Has(FoliageNormalsPipeline.Stage.Capture));
                Assert.AreSame(leaf.mesh, leaf.root.GetComponent<MeshFilter>().sharedMesh);
            }
        }

        [UnityTest]
        public IEnumerator PreCaptureNormalArrowsSampleSelectedVerticesBeforeApplyingTheLimit()
        {
            var positions = Enumerable.Range(0, 512).Select(i => new Vector3(i % 32, i / 32, 0)).ToArray();
            using var leaf = new FoliageNormalTransferTests.Fixture(positions);
            leaf.mesh.SetTriangles(new[] { 0, 2, 4 }, 0);
            leaf.mesh.SetTriangles(Enumerable.Range(0, 255).Select(i => i * 2 + 1).ToArray(), 1);
            var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 1 } };
            using var preview = new FoliageNormalsPreview(); using var pipeline = new FoliageNormalsPipeline();
            preview.Refresh(leaf.root, settings, pipeline);
            yield return AwaitPreview(preview);
            var lines = new List<Vector3>();
            FoliageNormalsPreview.AppendSourceNormalLines(preview.Sources(0).Single(), .1f, lines);
            Assert.AreEqual(510, lines.Count, "Every selected odd-indexed vertex fits within the 256-arrow limit.");
            for (int i = 0; i < lines.Count; i += 2)
            {
                int vertex = Array.FindIndex(positions, p => p == lines[i]);
                Assert.AreEqual(1, vertex % 2); Assert.AreEqual(Vector3.forward * .1f, lines[i + 1] - lines[i]);
            }
        }

        [UnityTest]
        public IEnumerator InvalidatedOrClearedTransferNeverShowsAnAfterCopyOfTheOriginal()
        {
            using var leaf = new FoliageNormalTransferTests.Fixture(); using var fixture = new ToolFixture();
            var tool = fixture.tool; tool.SetRoot(leaf.root); yield return AwaitPreview(tool.sourcePreview);
            var task = tool.pipeline.Run(leaf.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
            tool.preview = FoliageNormalsTool.Preview.BeforeAfter;
            tool.settings.proxyFit = FoliageProxyFit.ObjectBoundingBox; tool.pipeline.InvalidateChanged(tool.settings, leaf.root);
            var items = new List<MeshViewport3D.Item>(); tool.Get3DContent(items);
            Assert.AreEqual(1, items.Count); Assert.AreEqual(tool.pipeline.Targets[0].localToCapture, items[0].matrix);
            Assert.AreSame(tool.pipeline.Targets[0].original, items[0].mesh); Assert.IsFalse(tool.afterNormals);
            tool.pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture);
            tool.preview = FoliageNormalsTool.Preview.BeforeAfter; items.Clear(); tool.Get3DContent(items);
            Assert.AreEqual(1, items.Count); Assert.AreEqual(Matrix4x4.identity, items[0].matrix);
            Assert.AreEqual(FoliageNormalsTool.Preview.TransferredNormals, tool.preview);
        }

        [UnityTest]
        public IEnumerator MaskSeparatesSelectedCornersAndPreservesUnreadableSource()
        {
            for (int unreadable = 0; unreadable < 2; unreadable++)
            {
                using var leaf = new FoliageNormalTransferTests.Fixture();
                using var fixture = new ToolFixture();
                var tool = fixture.tool;
                tool.settings.selection = FoliageFaceSelection.SelectedSubmeshIndices;
                tool.settings.submeshes = new[] { 1 };
                if (unreadable == 1) leaf.mesh.UploadMeshData(true);
                var fingerprint = FoliageTargetSnapshot.ContentFingerprint(leaf.mesh);
                tool.SetRoot(leaf.root);
                yield return AwaitPreview(tool.sourcePreview);
                var source = tool.sourcePreview.Sources(0).Single();
                var colors = source.mask.colors32;
                Assert.AreEqual(6, source.mask.vertexCount);
                CollectionAssert.AreEqual(Enumerable.Repeat(FoliageNormalsPreview.ExcludedColor, 3), colors.Take(3));
                CollectionAssert.AreEqual(Enumerable.Repeat(FoliageNormalsPreview.ProxySourceColor, 3), colors.Skip(3));
                Assert.AreEqual(fingerprint, FoliageTargetSnapshot.ContentFingerprint(leaf.mesh));
                var directions = new List<Vector3>(); FoliageNormalsPreview.AppendSourceNormalLines(source, .1f, directions);
                Assert.AreEqual(6, directions.Count, "Selected source normals are visible even before Capture.");
                for (int i = 0; i < directions.Count; i += 2)
                    Assert.That(Vector3.Distance(directions[i + 1] - directions[i], Vector3.forward * .1f), Is.LessThan(.00001f));
                Assert.AreEqual(unreadable == 0, leaf.mesh.isReadable);
                var items = new List<MeshViewport3D.Item>(); tool.Get3DContent(items);
                Assert.AreEqual(MeshViewport3D.Shading.VertexColors, items.Single().ShadingOverride);
                var mask = source.mask; var copy = source.mesh;
                for (int draw = 0; draw < 10; draw++) { items.Clear(); tool.Get3DContent(items); Assert.AreSame(mask, items.Single().mesh); }
                tool.sourcePreview.Dispose(); Assert.IsFalse(mask); Assert.IsFalse(copy);
                Assert.IsTrue(leaf.mesh);
            }
        }

        [UnityTest]
        public IEnumerator LodMaskDistinguishesProxySourcesTransferRecipientsAndExcludedRenderers()
        {
            using var leaf0 = new FoliageNormalTransferTests.Fixture(); using var leaf1 = new FoliageNormalTransferTests.Fixture();
            using var trunk0 = new FoliageNormalTransferTests.Fixture(); using var trunk1 = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture(); var root = new GameObject("Tree");
            try
            {
                var meshes = new[] { leaf0, leaf1, trunk0, trunk1 };
                for (int i = 0; i < meshes.Length; i++)
                {
                    meshes[i].root.name = (i < 2 ? "Leaf_LOD" : "Trunk_LOD") + i % 2;
                    meshes[i].root.transform.SetParent(root.transform, false);
                }
                root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(.6f, new Renderer[] { leaf0.renderer, trunk0.renderer }),
                    new LOD(.2f, new Renderer[] { leaf1.renderer, trunk1.renderer }) });
                var tool = fixture.tool; tool.SetRoot(root);
                tool.settings.filterRenderers = true; tool.settings.renderers = new[] { leaf0.renderer };
                tool.OnRefresh(); yield return AwaitPreview(tool.sourcePreview);
                CollectionAssert.AreEqual(new[] { 0, 1 }, tool.sourcePreview.Levels);
                for (int lod = 0; lod < 2; lod++)
                {
                    var sources = tool.sourcePreview.Sources(lod).ToArray(); Assert.AreEqual(2, sources.Length);
                    var leaf = sources.Single(s => s.renderer.name.StartsWith("Leaf"));
                    var trunk = sources.Single(s => s.renderer.name.StartsWith("Trunk"));
                    Assert.IsTrue(leaf.mask.colors32.All(c => c.Equals(lod == 0 ? FoliageNormalsPreview.ProxySourceColor : FoliageNormalsPreview.TransferOnlyColor)));
                    Assert.IsTrue(trunk.mask.colors32.All(c => c.Equals(FoliageNormalsPreview.ExcludedColor)));
                }
            }
            finally { fixture.tool.OnDeactivate(); Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator MeshNamedLodChainsKeepNamesAndDisplayOnlyTheSelectedLevel()
        {
            foreach (bool unreadable in new[] { false, true })
            {
                using var first = new FoliageNormalTransferTests.Fixture();
                using var second = new FoliageNormalTransferTests.Fixture();
                using var fixture = new ToolFixture(); var root = new GameObject("Mesh-named tree");
                try {
                    var levels = new[] { first, second };
                    for (int lod = 0; lod < levels.Length; lod++) {
                        levels[lod].root.name = "Generic renderer";
                        levels[lod].mesh.name = "Leaves_LOD" + lod;
                        levels[lod].root.transform.SetParent(root.transform, false);
                        if (unreadable) levels[lod].mesh.UploadMeshData(true);
                    }
                    var tool = fixture.tool; tool.SetRoot(root); tool.OnRefresh();
                    yield return AwaitPreview(tool.sourcePreview);
                    CollectionAssert.AreEqual(new[] { 0, 1 }, tool.sourcePreview.Levels);
                    for (int lod = 0; lod < levels.Length; lod++) {
                        var source = tool.sourcePreview.Sources(lod).Single();
                        Assert.AreSame(levels[lod].renderer, source.renderer);
                        Assert.AreEqual(levels[lod].mesh.name, source.mesh.name);
                        Assert.AreNotSame(levels[lod].mesh, source.mesh);
                    }
                    var task = tool.pipeline.Run(root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                    yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
                    tool.OnRefresh(); yield return AwaitPreview(tool.sourcePreview);
                    tool.SetNormalComparison(true);
                    for (int lod = 0; lod < levels.Length; lod++) {
                        tool.previewLod = lod;
                        var items = new List<MeshViewport3D.Item>(); tool.Get3DContent(items);
                        Assert.AreEqual(1, items.Count);
                        Assert.AreSame(tool.pipeline.Targets.Single(t => t.renderer == levels[lod].renderer).working, items[0].mesh);
                        Assert.AreSame(levels[lod].mesh, levels[lod].root.GetComponent<MeshFilter>().sharedMesh);
                        Assert.AreEqual(!unreadable, levels[lod].mesh.isReadable);
                    }
                }
                finally { fixture.tool.OnDeactivate(); Object.DestroyImmediate(root); }
            }
        }

        [UnityTest]
        public IEnumerator NormalDirectionsOnlyDescribeRenderersInTheVisibleLod()
        {
            using var captured = new FoliageNormalTransferTests.Fixture();
            using var excluded = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture(); var root = new GameObject("Tree");
            try
            {
                captured.root.name = "Leaves_LOD0"; excluded.root.name = "Trunk_LOD1";
                captured.root.transform.SetParent(root.transform, false); excluded.root.transform.SetParent(root.transform, false);
                excluded.root.transform.localPosition = Vector3.right * 10;
                var tool = fixture.tool; tool.SetRoot(root);
                tool.settings.filterRenderers = true; tool.settings.renderers = new[] { captured.renderer };
                tool.OnRefresh(); yield return AwaitPreview(tool.sourcePreview);
                var task = tool.pipeline.Run(root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
                tool.OnRefresh(); yield return AwaitPreview(tool.sourcePreview);
                CollectionAssert.AreEqual(new[] { 0, 1 }, tool.sourcePreview.Levels);
                CollectionAssert.AreEqual(new[] { 0 }, tool.pipeline.LodLevels);
                var before = new List<Vector3>(); var after = new List<Vector3>(); var items = new List<MeshViewport3D.Item>();
                foreach (var mode in new[] { FoliageNormalsTool.Preview.TransferredNormals, FoliageNormalsTool.Preview.BeforeAfter })
                    foreach (bool result in new[] { false, true })
                    {
                        tool.preview = mode; tool.afterNormals = result; tool.previewLod = 1;
                        items.Clear(); tool.Get3DContent(items);
                        Assert.AreEqual(mode == FoliageNormalsTool.Preview.BeforeAfter ? 2 : 1, items.Count);
                        Assert.AreSame(tool.sourcePreview.Sources(1).Single().mesh, items[0].mesh);
                        before.Clear(); after.Clear(); tool.AppendPreviewNormalLines(before, after);
                        Assert.IsEmpty(before, "A visible uncaptured LOD cannot borrow another LOD's before arrows.");
                        Assert.IsEmpty(after, "A visible uncaptured LOD cannot borrow another LOD's after arrows.");
                        tool.previewLod = 0; tool.AppendPreviewNormalLines(before, after);
                        Assert.Greater(before.Count + after.Count, 0, "Captured arrows remain visible on their own LOD.");
                        if (mode == FoliageNormalsTool.Preview.BeforeAfter) { Assert.IsNotEmpty(before); Assert.IsNotEmpty(after); }
                        else { Assert.AreEqual(result, after.Count > 0); Assert.AreEqual(!result, before.Count > 0); }
                    }
                Assert.AreSame(captured.mesh, captured.root.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreSame(excluded.mesh, excluded.root.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { fixture.tool.OnDeactivate(); Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator BeforeAfterKeepsPlacementAndProxyWireDoesNotReplaceTheMesh()
        {
            using var leaf = new FoliageNormalTransferTests.Fixture(); using var trunk = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture(); var tool = fixture.tool;
            trunk.root.transform.SetParent(leaf.root.transform, false);
            leaf.root.transform.localScale = new Vector3(2, 3, 4);
            tool.SetRoot(leaf.root); tool.settings.filterRenderers = true; tool.settings.renderers = new[] { leaf.renderer };
            tool.OnRefresh(); yield return AwaitPreview(tool.sourcePreview);
            var task = tool.pipeline.Run(leaf.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
            var target = tool.pipeline.Targets.Single();
            var items = new List<MeshViewport3D.Item>(); tool.SetNormalComparison(false); tool.Get3DContent(items);
            Assert.AreSame(target.original, items[0].mesh); var beforeMatrix = items[0].matrix;
            items.Clear(); tool.SetNormalComparison(true); tool.Get3DContent(items);
            Assert.AreSame(target.working, items[0].mesh); Assert.AreEqual(beforeMatrix, items[0].matrix);
            Assert.AreEqual(2, items.Count, "The excluded trunk stays visible in both views.");
            tool.preview = FoliageNormalsTool.Preview.ProxyAndMesh; items.Clear(); tool.Get3DContent(items);
            Assert.AreEqual(3, items.Count);
            Assert.IsTrue(items.Last().WireOnly); Assert.AreSame(tool.pipeline.RemeshedMesh, items.Last().mesh);
            Assert.IsTrue(tool.showProxySurface); Assert.AreEqual(.25f, tool.proxyOpacity); Assert.IsFalse(items.Last().SuppressWire);
            tool.showProxyEdges = false; items.Clear(); tool.Get3DContent(items);
            Assert.IsTrue(items.Last().SuppressWire); Assert.IsTrue(items.Last().WireOnly);
            Assert.IsFalse(items[0].SuppressWire); Assert.AreEqual(beforeMatrix, items[0].matrix);
            tool.SetNormalComparison(false); Assert.AreEqual(FoliageNormalsTool.Preview.ProxyAndMesh, tool.preview);
            items.Clear(); tool.Get3DContent(items); Assert.AreSame(target.original, items[0].mesh); Assert.IsTrue(items.Last().WireOnly);
            tool.preview = FoliageNormalsTool.Preview.BeforeAfter; items.Clear(); tool.Get3DContent(items);
            Assert.AreEqual(4, items.Count); Assert.AreSame(target.original, items[0].mesh); Assert.AreSame(target.working, items[1].mesh);
            Assert.Less(items[0].matrix.m03, items[1].matrix.m03);
            Assert.AreSame(leaf.mesh, leaf.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator FlatYzMeshesHaveSeparatedBeforeAfterViewsAndAlignedArrows()
        {
            using var leaf = new FoliageNormalTransferTests.Fixture(new[] { Vector3.zero, Vector3.up, Vector3.forward, Vector3.up + Vector3.forward });
            using var fixture = new ToolFixture(); var tool = fixture.tool;
            tool.SetRoot(leaf.root); tool.OnRefresh(); yield return AwaitPreview(tool.sourcePreview);
            var task = tool.pipeline.Run(leaf.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
            Assert.AreEqual(0, tool.pipeline.CaptureBounds.size.x);
            tool.preview = FoliageNormalsTool.Preview.BeforeAfter;
            var items = new List<MeshViewport3D.Item>(); tool.Get3DContent(items); Assert.AreEqual(2, items.Count);
            Assert.Less(items[0].matrix.m03, 0); Assert.Greater(items[1].matrix.m03, 0);
            var before = new List<Vector3>(); var after = new List<Vector3>(); tool.AppendPreviewNormalLines(before, after);
            Assert.IsNotEmpty(before); Assert.IsNotEmpty(after);
            var vertex = leaf.mesh.vertices[0];
            Assert.Less(Vector3.Distance(items[0].matrix.MultiplyPoint3x4(vertex), before[0]), 1e-6f);
            Assert.Less(Vector3.Distance(items[1].matrix.MultiplyPoint3x4(vertex), after[0]), 1e-6f);
            Assert.AreSame(leaf.mesh, leaf.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator BeforeAfterSeparatesWideExcludedContextAndKeepsArrowsAligned()
        {
            using var leaf = new FoliageNormalTransferTests.Fixture();
            using var trunk = new FoliageNormalTransferTests.Fixture(new[] { Vector3.left * 100, Vector3.right * 100, Vector3.up, Vector3.forward });
            using var fixture = new ToolFixture(); var tool = fixture.tool;
            trunk.root.transform.SetParent(leaf.root.transform, false);
            tool.SetRoot(leaf.root); tool.settings.filterRenderers = true; tool.settings.renderers = new[] { leaf.renderer };
            tool.OnRefresh(); yield return AwaitPreview(tool.sourcePreview);
            var task = tool.pipeline.Run(leaf.root, tool.settings, FoliageNormalsPipeline.Stage.Transfer);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
            tool.preview = FoliageNormalsTool.Preview.BeforeAfter;
            var items = new List<MeshViewport3D.Item>(); tool.Get3DContent(items); Assert.AreEqual(4, items.Count);
            var beforeBounds = MeshGeometry.TransformBounds(items[0].mesh.bounds, items[0].matrix);
            beforeBounds.Encapsulate(MeshGeometry.TransformBounds(items[2].mesh.bounds, items[2].matrix));
            var afterBounds = MeshGeometry.TransformBounds(items[1].mesh.bounds, items[1].matrix);
            afterBounds.Encapsulate(MeshGeometry.TransformBounds(items[3].mesh.bounds, items[3].matrix));
            Assert.Less(beforeBounds.max.x, afterBounds.min.x);
            var before = new List<Vector3>(); var after = new List<Vector3>(); tool.AppendPreviewNormalLines(before, after);
            Assert.Less(Vector3.Distance(items[0].matrix.MultiplyPoint3x4(leaf.mesh.vertices[0]), before[0]), 1e-6f);
            Assert.Less(Vector3.Distance(items[1].matrix.MultiplyPoint3x4(leaf.mesh.vertices[0]), after[0]), 1e-6f);
            Assert.AreSame(trunk.mesh, trunk.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [UnityTest]
        public IEnumerator NamedLodSiblingChannelChangesRefreshBeforeCapture()
        {
            using var first = new FoliageNormalTransferTests.Fixture();
            using var second = new FoliageNormalTransferTests.Fixture();
            using var fixture = new ToolFixture(); var parent = new GameObject("Named LOD family");
            try
            {
                first.mesh.name = "Leaves_LOD0"; second.mesh.name = "Leaves_LOD1";
                first.root.transform.SetParent(parent.transform, false); second.root.transform.SetParent(parent.transform, false);
                Selection.activeGameObject = first.root; fixture.tool.OnActivate(null, null);
                var tool = fixture.tool; yield return AwaitPreview(tool.sourcePreview);
                Assert.AreSame(first.root, tool.root); Assert.IsEmpty(tool.pipeline.Targets);
                var previous = tool.sourcePreview.Sources(1).Single(); var oldCopy = previous.mesh; var oldMask = previous.mask;
                second.mesh.normals = Enumerable.Repeat(Vector3.up, second.mesh.vertexCount).ToArray();
                second.mesh.vertices = second.mesh.vertices.Select(p => p + Vector3.right * 5).ToArray(); second.mesh.RecalculateBounds();
                VertexChannels.RaiseChanged(second.mesh);
                yield return AwaitPreview(tool.sourcePreview);
                Assert.IsFalse(oldCopy); Assert.IsFalse(oldMask);
                var refreshed = tool.sourcePreview.Sources(1).Single();
                CollectionAssert.AreEqual(second.mesh.vertices, refreshed.mesh.vertices);
                CollectionAssert.AreEqual(second.mesh.normals, refreshed.mesh.normals);
                Assert.IsNotEmpty(refreshed.normalLines);
                Assert.Less(Vector3.Distance(refreshed.normalLines[1] - refreshed.normalLines[0], Vector3.up), 1e-6f);
                Assert.AreSame(second.mesh, second.root.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { fixture.tool.OnDeactivate(); Object.DestroyImmediate(parent); }
        }

        static Func<Func<CancellationToken, T>> BlockingPreview<T>(ManualResetEventSlim started, ManualResetEventSlim release)
            => () => token => { started.Set(); release.Wait(); token.ThrowIfCancellationRequested(); return default; };

        [UnityTest]
        public IEnumerator RefreshWaitsForCancelledPaintBeforePreparingTheLatestSource()
        {
            using var first = new FoliageNormalTransferTests.Fixture(); using var latest = new FoliageNormalTransferTests.Fixture();
            using var preview = new FoliageNormalsPreview(); using var pipeline = new FoliageNormalsPipeline();
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var worker = typeof(FoliageNormalsPreview).GetField("work", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(preview);
            var resultType = worker.GetType().GetGenericArguments()[0];
            var prepare = typeof(FoliageNormalsPreviewTests).GetMethod(nameof(BlockingPreview), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(resultType).Invoke(null, new object[] { started, release });
            worker.GetType().GetMethod("Enqueue").Invoke(worker, new[] { prepare, null });
            try
            {
                double end = EditorApplication.timeSinceStartup + 5;
                while (!started.IsSet && EditorApplication.timeSinceStartup < end) yield return null;
                Assert.IsTrue(started.IsSet);
                var settings = new FoliageNormalsSettings();
                preview.Refresh(first.root, settings, pipeline); preview.Refresh(latest.root, settings, pipeline);
                for (int i = 0; i < 3; i++) yield return null;
                Assert.IsTrue(preview.IsPending); Assert.IsEmpty(preview.Sources(0), "A replacement must wait for the cancelled worker to finish.");
                release.Set(); yield return AwaitPreview(preview);
                Assert.AreSame(latest.renderer, preview.Sources(0).Single().renderer);
                Assert.IsNull(preview.Error); Assert.IsTrue(preview.Sources(0).Single().mask);
            }
            finally { release.Set(); }
        }

        [TestCase(255)]
        [TestCase(600)]
        public void CapturedArrowsSampleSelectedVerticesAndKeepSkippedBoundaryCopiesAligned(int selectedCount)
        {
            var positions = Enumerable.Range(0, selectedCount * 2 + 2).Select(i => new Vector3(i % 2, i / 2 % 2, i / 4)).ToArray();
            using var fixture = new FoliageNormalTransferTests.Fixture(positions);
            var selected = Enumerable.Range(0, selectedCount).Select(i => i * 2 + 1).ToArray();
            fixture.mesh.SetTriangles(new[] { 0, 2, 4 }.Concat(selected).ToArray(), 0);
            fixture.mesh.SetTriangles(selected, 1);
            var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices,
                submeshes = new[] { 1 }, recalculateTangents = false };
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var before = new List<Vector3>(); var after = new List<Vector3>();
            var offset = new Vector3(.3f, -1, 7); const float length = .1f;
            FoliageNormalsPreview.AppendNormalLines(target, false, offset, length, before);
            target.working = target.BuildResult(Enumerable.Repeat(Vector3.up, positions.Length).ToArray(), false,
                CancellationToken.None, out int duplicated);
            Assert.AreEqual(selectedCount, duplicated);
            FoliageNormalsPreview.AppendNormalLines(target, true, offset, length, after);
            int stride = (selectedCount + 255) / 256;
            Assert.AreEqual(2 * ((selectedCount + stride - 1) / stride), before.Count);
            Assert.AreEqual(before.Count, after.Count); Assert.LessOrEqual(after.Count, 512);
            for (int line = 0; line < after.Count; line += 2) {
                Assert.AreEqual(positions[selected[line / 2 * stride]] + offset, before[line]);
                Assert.AreEqual(before[line], after[line]);
                Assert.That(Vector3.Distance(before[line + 1] - before[line], Vector3.forward * length), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(after[line + 1] - after[line], Vector3.up * length), Is.LessThan(1e-5f));
            }
            Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void DirectionArrowsUseSelectedBoundaryCopiesAndNormalTransform()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture();
            fixture.root.transform.localScale = new Vector3(2, 3, 4);
            fixture.root.transform.localRotation = Quaternion.Euler(15, 40, 25);
            var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices,
                submeshes = new[] { 1 }, recalculateTangents = false };
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            target.working = target.BuildResult(Enumerable.Repeat(Vector3.up, 4).ToArray(), false, CancellationToken.None, out int duplicated);
            Assert.Greater(duplicated, 0);
            var before = new List<Vector3>(); var after = new List<Vector3>();
            FoliageNormalsPreview.AppendNormalLines(target, false, Vector3.zero, .1f, before);
            FoliageNormalsPreview.AppendNormalLines(target, true, Vector3.zero, .1f, after);
            Assert.AreEqual(6, before.Count); Assert.AreEqual(before.Count, after.Count);
            var direction = MeshGeometry.UnitDirection(target.normalLocalToCapture.MultiplyVector(Vector3.up));
            for (int i = 0; i < after.Count; i += 2)
            {
                Assert.AreEqual(before[i], after[i]);
                Assert.That(Vector3.Distance(after[i + 1] - after[i], direction * .1f), Is.LessThan(.00001f));
                Assert.That(Vector3.Distance(before[i + 1] - before[i], target.captureNormals[Array.FindIndex(target.positions, p => p == before[i])] * .1f), Is.LessThan(.00001f));
            }
        }

        public sealed class PreviewWindow : EditorWindow
        {
            internal readonly MeshViewport3D viewport = new MeshViewport3D();
            internal List<MeshViewport3D.Item> items = new List<MeshViewport3D.Item>();
            internal Action<MeshViewport3D> overlay;
            void OnGUI() => viewport.Draw(new Rect(0, 0, position.width, position.height), items, overlay);
            void OnDisable() => viewport.Dispose();
            internal Color[] Pixels(string path = null)
            {
                SendEvent(new Event { type = EventType.Layout }); SendEvent(new Event { type = EventType.Repaint });
                var target = (RenderTexture)typeof(MeshViewport3D).GetField("offscreen", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(viewport);
                Assert.IsNotNull(target);
                var resolved = RenderTexture.GetTemporary(target.width, target.height, 0, target.format);
                var previous = RenderTexture.active;
                var pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                try
                {
                    MeshViewport3D.ResolveFrame(target, resolved); RenderTexture.active = resolved;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    if (path != null) File.WriteAllBytes(path, pixels.EncodeToPNG());
                    return pixels.GetPixels();
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(resolved); Object.DestroyImmediate(pixels); }
            }
        }

        internal static IEnumerator CapturePinePreview(GameObject root, FoliageNormalsSettings settings, FoliageNormalsPipeline pipeline, string output)
        {
            using var preview = new FoliageNormalsPreview();
            preview.Refresh(root, settings, pipeline);
            yield return AwaitPreview(preview);
            var source = preview.Sources(0).Single(); var target = pipeline.Targets.Single();
            var colors = source.mask.colors32;
            Assert.AreEqual(target.indices[0].Length, colors.Count(c => c.Equals(FoliageNormalsPreview.ExcludedColor)));
            Assert.AreEqual(target.selectedIndices.Length, colors.Count(c => c.Equals(FoliageNormalsPreview.ProxySourceColor)));
            var window = ScriptableObject.CreateInstance<PreviewWindow>();
            try
            {
                window.position = new Rect(0, 0, 768, 768); window.ShowUtility();
                window.viewport.ShowGrid = window.viewport.ShowAxes = false;
                window.viewport.Mode = MeshViewport3D.Shading.Normals;
                window.items.Add(new MeshViewport3D.Item(source.mask, source.matrix) { ShadingOverride = MeshViewport3D.Shading.VertexColors });
                yield return null;
                window.Pixels(Path.Combine(output, "pine-selection-mask.png"));
                window.items.Clear();
                window.items.Add(new MeshViewport3D.Item(target.working, target.localToCapture, target.materials));
                window.items.Add(new MeshViewport3D.Item(pipeline.RemeshedMesh, Matrix4x4.identity) { WireOnly = true });
                Action<MeshViewport3D> proxySurface = view => view.DrawHighlightMesh(pipeline.RemeshedMesh, Matrix4x4.identity, new Color(.05f, .8f, 1f, .25f));
                window.overlay = proxySurface;
                for (int frame = 0; frame < 10; frame++) { window.Pixels(); yield return null; }
                window.Pixels(Path.Combine(output, "pine-proxy-plus-mesh.png"));
                var lines = new List<Vector3>();
                float length = pipeline.CaptureBounds.size.magnitude * .025f;
                FoliageNormalsPreview.AppendNormalLines(target, true, Vector3.zero, length, lines);
                window.overlay = view => { proxySurface(view); view.DrawLines(lines, Matrix4x4.identity, Color.yellow); };
                for (int frame = 0; frame < 5; frame++) { window.Pixels(); yield return null; }
                window.Pixels(Path.Combine(output, "pine-normal-directions.png"));
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator TranslucentProxyKeepsTheMainMeshVisibleAndUsesIndependentOpacity()
        {
            Assert.AreNotEqual(GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType);
            using var leaf = new FoliageNormalTransferTests.Fixture(new[] { Vector3.zero, Vector3.right, Vector3.up });
            leaf.mesh.colors32 = Enumerable.Repeat(new Color32(0, 255, 0, 255), 3).ToArray();
            var fingerprint = FoliageTargetSnapshot.ContentFingerprint(leaf.mesh);
            using var fixture = new ToolFixture(); var tool = fixture.tool;
            tool.SetRoot(leaf.root); yield return AwaitPreview(tool.sourcePreview);
            var task = tool.pipeline.Run(leaf.root, tool.settings, FoliageNormalsPipeline.Stage.Envelope);
            yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, tool.pipeline.Status);
            tool.preview = FoliageNormalsTool.Preview.ProxyAndMesh; tool.showProxyEdges = false;
            var output = Environment.GetEnvironmentVariable("MESHLAB_PROXY_PREVIEW_OUTPUT");
            if (!string.IsNullOrEmpty(output)) Directory.CreateDirectory(output);
            var window = ScriptableObject.CreateInstance<PreviewWindow>();
            try
            {
                window.position = new Rect(0, 0, 512, 512); window.ShowUtility();
                window.viewport.ShowGrid = window.viewport.ShowAxes = false;
                window.viewport.Mode = MeshViewport3D.Shading.VertexColors;
                window.viewport.ViewProjection = MeshViewport3D.Projection.XY;
                tool.Get3DContent(window.items); window.overlay = tool.OnDraw3D;
                tool.showProxySurface = false;
                for (int frame = 0; frame < 10; frame++) { window.Pixels(); yield return null; }
                var source = window.Pixels(string.IsNullOrEmpty(output) ? null : Path.Combine(output, "mesh.png"));
                var green = Enumerable.Range(0, source.Length).Where(i => source[i].g > .7f && source[i].r < .1f && source[i].b < .1f).ToArray();
                Assert.Greater(green.Length, 1000, "The main leaf card must be visible before adding the proxy surface.");
                tool.showProxySurface = true; tool.proxyOpacity = .25f;
                var translucent = window.Pixels(string.IsNullOrEmpty(output) ? null : Path.Combine(output, "proxy-translucent.png"));
                tool.proxyOpacity = 1;
                var opaque = window.Pixels(string.IsNullOrEmpty(output) ? null : Path.Combine(output, "proxy-opaque.png"));
                Assert.Greater(green.Average(i => translucent[i].b - source[i].b), .05, "The proxy must draw a filled translucent surface.");
                Assert.Greater(green.Count(i => translucent[i].g > translucent[i].b * 1.2f), green.Length * .8f, "The green mesh remains readable through the proxy.");
                Assert.Less(green.Count(i => opaque[i].g > opaque[i].b * 1.2f), green.Length * .2f, "Full opacity must differ from translucent display.");
                Assert.AreEqual(1, window.viewport.SurfaceOpacity, "Proxy opacity must not change the source opacity.");
                Assert.AreSame(leaf.mesh, leaf.root.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreEqual(fingerprint, FoliageTargetSnapshot.ContentFingerprint(leaf.mesh));
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator SelectionPaintOverridesNormalShadingAndWireProxyLeavesMeshVisible()
        {
            Assert.AreNotEqual(GraphicsDeviceType.Null, SystemInfo.graphicsDeviceType);
            using var leaf = new FoliageNormalTransferTests.Fixture(new[] { Vector3.zero, Vector3.right, Vector3.up });
            leaf.mesh.colors32 = Enumerable.Repeat(FoliageNormalsPreview.ProxySourceColor, 3).ToArray();
            var window = ScriptableObject.CreateInstance<PreviewWindow>();
            try
            {
                window.position = new Rect(0, 0, 256, 256); window.ShowUtility();
                window.viewport.ShowGrid = window.viewport.ShowAxes = false;
                window.viewport.Mode = MeshViewport3D.Shading.Normals; window.viewport.ViewProjection = MeshViewport3D.Projection.XY;
                window.items.Add(new MeshViewport3D.Item(leaf.mesh, Matrix4x4.identity) { ShadingOverride = MeshViewport3D.Shading.VertexColors });
                yield return null;
                var painted = window.Pixels(); Assert.Greater(painted.Count(c => c.g > c.r * 1.8f && c.g > c.b * 1.4f), 1000);
                window.items.Add(new MeshViewport3D.Item(leaf.mesh, Matrix4x4.identity) { WireOnly = true });
                for (int frame = 0; frame < 5; frame++) { window.Pixels(); yield return null; }
                var combined = window.Pixels(); Assert.Greater(combined.Count(c => c.g > c.r * 1.8f && c.g > c.b * 1.4f), 1000,
                    "The wire-only proxy cannot cover the green source with its normal-colored surface.");
            }
            finally { window.Close(); }
        }
    }
}
