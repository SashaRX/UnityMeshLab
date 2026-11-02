// FbxCollisionPlacementTests.cs — a collision node's offset from the model root baked into
// its mesh in the FBX: the node sits at the root's placement afterwards and its surface stays
// where it was; the path lookup and the surface comparison the check after the bake uses.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FbxCollisionPlacementTests
    {
        readonly List<Object> owned = new List<Object>();
        readonly List<string> assets = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var path in assets) AssetDatabase.DeleteAsset(path);
            assets.Clear();
        }

        Mesh Box(string name)
        {
            var mesh = new Mesh { name = name };
            var corners = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * .5f).ToArray();
            mesh.vertices = corners;
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        [Test]
        public void Find_ReadsTheRelativePathBack()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var a = new GameObject("Seat"); a.transform.SetParent(root.transform);
            var b = new GameObject("Seat"); b.transform.SetParent(root.transform);
            var c = new GameObject("Seat COL"); c.transform.SetParent(b.transform);
            foreach (var node in new[] { root.transform, a.transform, b.transform, c.transform })
                Assert.AreSame(node, CollisionHierarchyAnalyzer.Find(root.transform, CollisionHierarchyAnalyzer.RelativePath(root.transform, node)));
            Assert.IsNull(CollisionHierarchyAnalyzer.Find(root.transform, "Seat[2]"));
            Assert.IsNull(CollisionHierarchyAnalyzer.Find(root.transform, "Back[0]"));
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [Test]
        public void SurfaceDifference_TellsAMovedSurfaceFromAResplitOne()
        {
            var box = Box("Box");
            var resplit = Object.Instantiate(box); owned.Add(resplit);
            resplit.triangles = resplit.triangles.Reverse().ToArray();
            Assert.IsNull(FbxCollisionPlacement.SurfaceDifference(resplit, box), "the same surface, faces in another order");
            var moved = Object.Instantiate(box); owned.Add(moved);
            moved.vertices = moved.vertices.Select(v => v + new Vector3(.01f, 0, 0)).ToArray();
            StringAssert.Contains("its surface moved by up to", FbxCollisionPlacement.SurfaceDifference(moved, box));
        }

        [Test]
        public void BakeOffsets_PutsAnOffsetCollisionNodeAtTheRootsPlacement()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var seat = new GameObject("Seat"); seat.transform.SetParent(root.transform, false);
            seat.AddComponent<MeshFilter>().sharedMesh = Box("Seat"); seat.AddComponent<MeshRenderer>();
            var col = new GameObject("Seat_COL"); col.transform.SetParent(root.transform, false);
            col.transform.localPosition = new Vector3(.392f, 0, .002f);
            col.transform.localRotation = Quaternion.Euler(0, 30, 0);
            col.AddComponent<MeshFilter>().sharedMesh = Box("Seat_COL"); col.AddComponent<MeshRenderer>();

            if (!AssetDatabase.IsValidFolder("Assets/UnityMeshLab")) AssetDatabase.CreateFolder("Assets", "UnityMeshLab");
            string fbxPath = "Assets/UnityMeshLab/CollisionOffset-" + Guid.NewGuid().ToString("N") + ".fbx";
            assets.Add(fbxPath); assets.Add(SidecarStore.PathFor(fbxPath));
            FbxExport.Write(fbxPath, root);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);

            var before = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var importedCol = before.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == "Seat_COL");
            Assert.IsNotNull(LodHierarchy.OffsetFromRoot(before.transform, importedCol.transform), "the fixture's collision node is offset");
            var toRoot = before.transform.worldToLocalMatrix * importedCol.transform.localToWorldMatrix;
            var expected = MeshAccess.ReadableCopy(importedCol.sharedMesh); owned.Add(expected);
            expected.vertices = expected.vertices.Select(v => toRoot.MultiplyPoint3x4(v)).ToArray();

            Assert.IsTrue(FbxCollisionPlacement.BakeOffsets(fbxPath));

            var after = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var baked = after.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == "Seat_COL");
            Assert.IsNull(LodHierarchy.OffsetFromRoot(after.transform, baked.transform), "the node sits at the root's placement");
            var bakedMesh = MeshAccess.ReadableCopy(baked.sharedMesh); owned.Add(bakedMesh);
            Assert.IsNull(FbxCollisionPlacement.SurfaceDifference(bakedMesh, expected), "its surface stays where it was");
            Assert.IsFalse(FbxCollisionPlacement.BakeOffsets(fbxPath), "nothing is left to bake");
        }
#endif
    }
}
