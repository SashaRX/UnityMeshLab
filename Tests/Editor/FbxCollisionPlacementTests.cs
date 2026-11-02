// FbxCollisionPlacementTests.cs — a collision node's offset from the model root baked into
// its mesh in the FBX: the node sits at the root's placement afterwards and its surface stays
// where it was; the path lookup and the surface comparison the check after the bake uses.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
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
        public void IsTrs_RejectsAShearedMatrix()
        {
            var offset = Matrix4x4.TRS(new Vector3(.4f, 0, 0), Quaternion.Euler(0, 0, 45), new Vector3(2, 1, 1));
            Assert.IsTrue(FbxCollisionPlacement.IsTrs(offset));
            Assert.IsTrue(FbxCollisionPlacement.IsTrs(offset * offset.inverse), "an object that follows the node is set to identity");
            Assert.IsFalse(FbxCollisionPlacement.IsTrs(offset.inverse), "an override at identity against a rotated, unevenly scaled node would need shear");
        }

        // The fixture model, exported to a new FBX: Seat at the root, Seat_COL moved and turned
        // off the root's placement, and a render node Chair_COLOR off it too.
        string ExportOffsetBench()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var seat = new GameObject("Seat"); seat.transform.SetParent(root.transform, false);
            seat.AddComponent<MeshFilter>().sharedMesh = Box("Seat"); seat.AddComponent<MeshRenderer>();
            var col = new GameObject("Seat_COL"); col.transform.SetParent(root.transform, false);
            col.transform.localPosition = new Vector3(.392f, 0, .002f);
            col.transform.localRotation = Quaternion.Euler(0, 30, 0);
            col.AddComponent<MeshFilter>().sharedMesh = Box("Seat_COL"); col.AddComponent<MeshRenderer>();
            // "_COLOR" is no collision name: an offset render node named so is left as it is.
            var colour = new GameObject("Chair_COLOR"); colour.transform.SetParent(root.transform, false);
            colour.transform.localPosition = new Vector3(0, .5f, 0);
            colour.AddComponent<MeshFilter>().sharedMesh = Box("Chair_COLOR"); colour.AddComponent<MeshRenderer>();

            if (!AssetDatabase.IsValidFolder("Assets/UnityMeshLab")) AssetDatabase.CreateFolder("Assets", "UnityMeshLab");
            string fbxPath = "Assets/UnityMeshLab/CollisionOffset-" + Guid.NewGuid().ToString("N") + ".fbx";
            assets.Add(fbxPath); assets.Add(SidecarStore.PathFor(fbxPath));
            FbxExport.Write(fbxPath, root);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);
            return fbxPath;
        }

        [Test]
        public void BakeOffsets_PutsAnOffsetCollisionNodeAtTheRootsPlacement()
        {
            string fbxPath = ExportOffsetBench();
            var before = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var importedCol = before.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == "Seat_COL");
            Assert.IsNotNull(LodHierarchy.OffsetFromRoot(before.transform, importedCol.transform), "the fixture's collision node is offset");
            var toRoot = before.transform.worldToLocalMatrix * importedCol.transform.localToWorldMatrix;
            var expected = MeshAccess.ReadableCopy(importedCol.sharedMesh); owned.Add(expected);
            expected.vertices = expected.vertices.Select(v => toRoot.MultiplyPoint3x4(v)).ToArray();
            // An unpacked copy in the scene keeps its own transforms: it is moved to keep its collision in place.
            var copy = (GameObject)Object.Instantiate(before); owned.Add(copy);
            copy.transform.position = new Vector3(3, 0, -2);
            var copyCol = copy.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == "Seat_COL");
            var copyWorld = MeshAccess.ReadableCopy(copyCol.sharedMesh); owned.Add(copyWorld);
            copyWorld.vertices = copyWorld.vertices.Select(v => copyCol.transform.localToWorldMatrix.MultiplyPoint3x4(v)).ToArray();

            Assert.IsTrue(FbxCollisionPlacement.BakeOffsets(fbxPath));

            var after = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var baked = after.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == "Seat_COL");
            Assert.IsNull(LodHierarchy.OffsetFromRoot(after.transform, baked.transform), "the node sits at the root's placement");
            var bakedMesh = MeshAccess.ReadableCopy(baked.sharedMesh); owned.Add(bakedMesh);
            Assert.IsNull(FbxCollisionPlacement.SurfaceDifference(bakedMesh, expected), "its surface stays where it was");
            Assert.IsNotNull(LodHierarchy.OffsetFromRoot(after.transform, after.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == "Chair_COLOR").transform),
                "a render node named _COLOR is not baked");

            Assert.IsNull(LodHierarchy.OffsetFromRoot(copy.transform, copyCol.transform), "the scene copy's node takes the root's placement");
            var copyAfter = MeshAccess.ReadableCopy(copyCol.sharedMesh); owned.Add(copyAfter);
            copyAfter.vertices = copyAfter.vertices.Select(v => copyCol.transform.localToWorldMatrix.MultiplyPoint3x4(v)).ToArray();
            Assert.IsNull(FbxCollisionPlacement.SurfaceDifference(copyAfter, copyWorld), "and its collision stays where it was in the world");
            Assert.IsFalse(FbxCollisionPlacement.BakeOffsets(fbxPath), "nothing is left to bake");
        }

        [Test]
        public void BakeOffsets_RefusesAColliderAwayFromItsNodeAndAlignsOneOnTheRoot()
        {
            string fbxPath = ExportOffsetBench();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var colMesh = model.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name == "Seat_COL").sharedMesh;

            // A collider elsewhere would move with the mesh: the file is left as it was.
            var holder = new GameObject("Seat collision"); owned.Add(holder);
            holder.AddComponent<MeshCollider>().sharedMesh = colMesh;
            var bytes = File.ReadAllBytes(fbxPath);
            LogAssert.Expect(LogType.Error, new Regex("'Seat collision' has a MeshCollider using 'Seat_COL' away from its node"));
            Assert.IsFalse(FbxCollisionPlacement.BakeOffsets(fbxPath));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fbxPath), "the file is unchanged");
            Object.DestroyImmediate(holder);

            // On the root of an instance of the model, the collision is meant to land where the model shows the node.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model); owned.Add(instance);
            var rootCollider = instance.AddComponent<MeshCollider>();
            rootCollider.sharedMesh = colMesh;
            Assert.IsTrue(FbxCollisionPlacement.BakeOffsets(fbxPath));
            Assert.AreEqual("Seat_COL", rootCollider.sharedMesh.name);
            Assert.AreEqual(fbxPath, AssetDatabase.GetAssetPath(rootCollider.sharedMesh), "the root's collider keeps the file's mesh");
        }
#endif
    }
}
