using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ViewportTopologyTests
    {
        static readonly Vector3[] Cube = { new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
            new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
        static readonly int[] CubeFaces = { 0,2,1,0,3,2, 4,5,6,4,6,7, 0,1,5,0,5,4, 3,7,6,3,6,2, 0,4,7,0,7,3, 1,2,6,1,6,5 };

        [TestCase(true, 0)]
        [TestCase(false, 1)]
        public void PositionWeldExcludesUvAndHardNormalSeams(bool closed, int holes)
        {
            var indices = closed ? CubeFaces : CubeFaces.Skip(6).ToArray();
            var positions = indices.Select(i => Cube[i]).ToArray();
            var before = (Vector3[])positions.Clone();
            var data = ViewportTopology.Build(positions, Enumerable.Range(0, positions.Length).ToArray(), CancellationToken.None);
            Assert.AreEqual(holes, data.rims.Length);
            if (!closed) { Assert.IsTrue(data.rims[0].closed); Assert.AreEqual(4, data.rims[0].edges.Length); Assert.AreEqual(8, data.rims[0].length); }
            CollectionAssert.AreEqual(before, positions, "Inspection must not weld the input in place.");
        }

        [Test]
        public void MultipleHolesRemainSeparateAndDegenerateFacesDoNotInventEdges()
        {
            var positions = Cube.Concat(Cube.Select(p => p + Vector3.right * 4)).ToArray();
            var open = CubeFaces.Skip(6).ToArray();
            var indices = open.Concat(open.Select(i => i + 8)).Concat(new[] { 0,0,1 }).ToArray();
            var data = ViewportTopology.Build(positions, indices, CancellationToken.None);
            Assert.AreEqual(2, data.rims.Length); Assert.AreEqual(1, data.degenerateFaces);
            Assert.IsTrue(data.rims.All(r => r.closed && r.edges.Length == 4));
        }

        [Test]
        public void BranchedBoundaryAndNonmanifoldEdgeAreReportedWithoutCancellingOtherHoles()
        {
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.down };
            var data = ViewportTopology.Build(positions, new[] { 0,1,2, 1,0,3, 0,1,4 }, CancellationToken.None);
            Assert.AreEqual(1, data.nonmanifoldEdges);
            Assert.AreEqual(1, data.rims.Length); Assert.IsFalse(data.rims[0].closed);
            StringAssert.Contains("branched", data.rims[0].Reason);
        }

        [UnityTest]
        public IEnumerator HoleSearchUploadsOnUpdateAndDisposesStaleMeshResults()
        {
            var mesh = new Mesh { vertices = Cube, triangles = CubeFaces.Skip(6).ToArray() };
            using var preview = new MeshTopologyPreview { ShowHoles = true };
            try {
                var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.identity) };
                preview.Prepare(items);
                Assert.IsNull(preview.Data(mesh), "The GUI only schedules the snapshot.");
                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 10;
                while (preview.Data(mesh) == null && UnityEditor.EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.AreEqual(1, preview.Data(mesh).rims.Length);
                preview.Clear(); Assert.IsNull(preview.Data(mesh));
                mesh.triangles = CubeFaces;
                preview.Prepare(items);
                while (preview.Data(mesh) == null && UnityEditor.EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.AreEqual(0, preview.Data(mesh).rims.Length);
                preview.Prepare(Array.Empty<MeshViewport3D.Item>());
                Assert.IsNull(preview.Data(mesh), "Switching models must release the old hole overlay.");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ClosureSurfacePartsKeepOriginalColoursAndDoNotChangeSourceIndices()
        {
            var source = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                triangles = new[] { 0,1,2, 0,2,3 }, colors = new[] { Color.gray, Color.gray, ViewportHighlight.Cap, ViewportHighlight.Cap } };
            Mesh patches = null;
            try {
                patches = RemeshPreview.ClosurePart(source, new List<int> { 0,2,3 }, "Patches");
                Assert.AreEqual(3, patches.vertexCount, "Cap-only view must not expose unused original vertices.");
                CollectionAssert.AreEqual(new[] { Vector3.zero,Vector3.up,Vector3.forward }, patches.vertices);
                CollectionAssert.AreEqual(new[] { Color.gray,ViewportHighlight.Cap,ViewportHighlight.Cap }, patches.colors);
                CollectionAssert.AreEqual(new[] { 0,1,2,0,2,3 }, source.triangles);
            }
            finally { if (patches) UnityEngine.Object.DestroyImmediate(patches); UnityEngine.Object.DestroyImmediate(source); }
        }
    }
}
