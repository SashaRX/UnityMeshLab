using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PreviewSpaceTests
    {
        [TestCase(1f)] [TestCase(-2f)] [TestCase(0f)]
        public void ModelCapturesPreserveThinFacesRegardlessOfRootScenePose(float scale)
        {
            var model = new GameObject("Model");
            var scene = new GameObject("Scene");
            using var fixture = new FoliageNormalTransferTests.Fixture(new[] {
                new Vector3(-.754247129f,2.82715511f,-1.81862569f),
                new Vector3(-.754247129f,2.85412812f,-1.81862545f),
                new Vector3(-.754247129f,2.82715535f,-1.81862545f) });
            try {
                model.transform.SetParent(scene.transform, false);
                fixture.root.transform.SetParent(model.transform, false);
                scene.transform.SetPositionAndRotation(new Vector3(100000, -90000, 85000), Quaternion.Euler(30, 45, 70));
                scene.transform.localScale = new Vector3(2, 3, .1f);
                model.transform.localPosition = new Vector3(52.7f, -.07f, 85.75f);
                model.transform.localRotation = Quaternion.Euler(0, 148.031784f, 0);
                model.transform.localScale = new Vector3(scale, 3, .5f);
                var before = TransferMeshSnapshot.Capture(fixture.mesh);
                var source = RemeshSource.Capture(Matrix4x4.identity, new[] { fixture.renderer }, geometryOnly: true, captureRoot: model.transform);
                CollectionAssert.AreEqual(fixture.mesh.vertices, source.positions);
                Assert.IsTrue(MeshGeometry.HasArea(source.positions[0], source.positions[1], source.positions[2]));
                using var foliage = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity,
                    new FoliageNormalsSettings { recalculateTangents = false }, CancellationToken.None, captureRoot: model.transform);
                Assert.AreEqual(Matrix4x4.identity, foliage.localToCapture);
                CollectionAssert.AreEqual(fixture.mesh.vertices, foliage.positions);
                Assert.IsTrue(foliage.IsCurrent(Matrix4x4.identity));
                var context = new UvToolContext { LodGroup = model.AddComponent<LODGroup>() };
                Assert.AreEqual(Matrix4x4.identity, context.PreviewMatrix(fixture.renderer.transform));
                CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(fixture.mesh));
                Assert.AreEqual(new Vector3(scale, 3, .5f), model.transform.localScale);
            }
            finally { fixture.root.transform.SetParent(null, false); Object.DestroyImmediate(scene); }
        }

        [Test]
        public void RemeshFilterPreviewKeepsModelCoordinatesAfterSceneRootMoves()
        {
            using var fixture = new FoliageNormalTransferTests.Fixture();
            using var highlight = new RemeshCaptureHighlight();
            var settings = new RemeshSettings { minPartSize = 0, minRodVoxels = 0 };
            highlight.Build(fixture.root, settings);
            Assert.IsNull(highlight.Error);
            var before = highlight.PreviewMesh.vertices;
            fixture.root.transform.SetPositionAndRotation(new Vector3(100000, 0, 85000), Quaternion.Euler(10, 148, 20));
            fixture.root.transform.localScale = new Vector3(-2, 3, .1f);
            highlight.Build(fixture.root, settings);
            CollectionAssert.AreEqual(before, highlight.PreviewMesh.vertices);
            var items = new List<MeshViewport3D.Item>();
            Assert.IsTrue(highlight.FillPreview(items));
            Assert.AreEqual(Matrix4x4.identity, items[0].matrix);
        }

        [TestCase(1f)]
        [TestCase(-2f)]
        [TestCase(0f)]
        public void RootAndSceneAncestorsDoNotAffectNestedModelPlacement(float sceneScale)
        {
            var ancestor = new GameObject("Scene");
            var root = new GameObject("Model");
            var branch = new GameObject("Branch");
            var child = new GameObject("Part");
            try {
                root.transform.SetParent(ancestor.transform, false);
                branch.transform.SetParent(root.transform, false);
                child.transform.SetParent(branch.transform, false);
                ancestor.transform.SetPositionAndRotation(new Vector3(80, -90, 25), Quaternion.Euler(30, 45, 70));
                ancestor.transform.localScale = new Vector3(sceneScale, 3, .1f);
                root.transform.localPosition = new Vector3(4, 8, 2);
                root.transform.localRotation = Quaternion.Euler(40, 25, 75);
                root.transform.localScale = new Vector3(3, sceneScale, 4);
                branch.transform.localPosition = new Vector3(1, 2, 3);
                branch.transform.localRotation = Quaternion.Euler(10, 20, 30);
                branch.transform.localScale = new Vector3(2, 3, .5f);
                child.transform.localPosition = new Vector3(-4, 0, 2);
                child.transform.localRotation = Quaternion.Euler(40, 25, 15);
                child.transform.localScale = new Vector3(-1, 2, 1);
                var expected = Matrix4x4.TRS(branch.transform.localPosition, branch.transform.localRotation, branch.transform.localScale) *
                    Matrix4x4.TRS(child.transform.localPosition, child.transform.localRotation, child.transform.localScale);
                Assert.AreEqual(expected, PreviewSpace.LocalToRoot(child.transform, root.transform), "retain the model's nested placement, including shear and reflection");
                Assert.AreEqual(Matrix4x4.identity, PreviewSpace.LocalToRoot(root.transform, root.transform));
                Assert.AreEqual(Matrix4x4.identity, PreviewSpace.LocalToRoot(null, root.transform));
                Assert.AreEqual(Matrix4x4.identity, PreviewSpace.LocalToRoot(ancestor.transform, root.transform));
                Assert.AreEqual(new Vector3(3, sceneScale, 4), root.transform.localScale);
            }
            finally { Object.DestroyImmediate(ancestor); }
        }
    }
}
