// CollisionRootPlacementTests.cs — collision put at the model root's placement through the
// sidecar: the import step on a built hierarchy (a single node and a hull container keep
// their surface, a node sharing its mesh is left as it is), the choice of nodes, and the
// consumers both versions keep in place (CollisionConsumers: refusals, shear, the moves).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class CollisionRootPlacementTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        Mesh Box(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * .5f).ToArray();
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        static Transform Node(Transform parent, string name, Mesh mesh, Vector3 position, Quaternion rotation)
        {
            var node = new GameObject(name).transform;
            node.SetParent(parent, false);
            node.localPosition = position;
            node.localRotation = rotation;
            if (mesh != null) node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            return node;
        }

        static Vector3[] World(Transform node)
            => node.GetComponent<MeshFilter>().sharedMesh.vertices.Select(v => node.localToWorldMatrix.MultiplyPoint3x4(v)).ToArray();

        [Test]
        public void IsTrs_RejectsAShearedMatrix()
        {
            var offset = Matrix4x4.TRS(new Vector3(.4f, 0, 0), Quaternion.Euler(0, 0, 45), new Vector3(2, 1, 1));
            Assert.IsTrue(CollisionConsumers.IsTrs(offset));
            Assert.IsTrue(CollisionConsumers.IsTrs(offset * offset.inverse), "an object that follows the node is set to identity");
            Assert.IsFalse(CollisionConsumers.IsTrs(offset.inverse), "an override at identity against a rotated, unevenly scaled node would need shear");
        }

        [Test]
        public void ApplyOnImport_PutsListedCollisionAtTheRootAndKeepsItsSurface()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            root.transform.position = new Vector3(5, 0, 0);
            var seat = Node(root.transform, "Seat", Box("Seat"), Vector3.zero, Quaternion.identity);
            var col = Node(root.transform, "Seat_COL", Box("Seat_COL"), new Vector3(.392f, 0, .002f), Quaternion.Euler(0, 30, 0));
            var hulls = Node(root.transform, "Base_COL", null, new Vector3(0, .25f, 0), Quaternion.identity);
            var hull0 = Node(hulls, "Base_COL_Hull0", Box("Base_COL_Hull0"), Vector3.zero, Quaternion.identity);
            var hull1 = Node(hulls, "Base_COL_Hull1", Box("Base_COL_Hull1"), new Vector3(.5f, 0, 0), Quaternion.Euler(0, 0, 90));
            var shared = Node(root.transform, "Back_COL", seat.GetComponent<MeshFilter>().sharedMesh, new Vector3(0, 1, 0), Quaternion.identity);
            var before = new[] { col, hull0, hull1 }.ToDictionary(t => t, World);

            LogAssert.Expect(LogType.Warning, new Regex("'Back_COL' shares its mesh 'Seat' with another node"));
            LogAssert.Expect(LogType.Warning, new Regex("'Missing_COL' is not in the model"));
            CollisionRootPlacement.ApplyOnImport(root, new List<string> { "Seat_COL[0]", "Base_COL[0]", "Back_COL[0]", "Missing_COL[0]" }, "Bench.fbx");

            foreach (var node in new[] { col, hulls, hull0, hull1 })
                Assert.IsNull(LodHierarchy.OffsetFromRoot(root.transform, node), $"'{node.name}' sits at the root's placement");
            foreach (var pair in before)
            {
                var after = World(pair.Key);
                for (int i = 0; i < after.Length; i++)
                    Assert.Less(Vector3.Distance(pair.Value[i], after[i]), 1e-4f, $"'{pair.Key.name}' keeps its surface");
            }
            Assert.AreEqual(new Vector3(0, 1, 0), shared.localPosition, "a node sharing its mesh is left as it is");
        }

        [Test]
        public void ApplyOnImport_LeavesANodeTheClipsAnimateOrWhoseMeshASkinnedRendererShows()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var animated = Node(root.transform, "Seat_COL", Box("Seat_COL"), new Vector3(.4f, 0, 0), Quaternion.identity);
            var skinnedMesh = Box("Back_COL");
            var shown = Node(root.transform, "Back_COL", skinnedMesh, new Vector3(0, 1, 0), Quaternion.identity);
            Node(root.transform, "Back", null, Vector3.zero, Quaternion.identity).gameObject.AddComponent<SkinnedMeshRenderer>().sharedMesh = skinnedMesh;

            LogAssert.Expect(LogType.Warning, new Regex("'Seat_COL' has transform curves"));
            LogAssert.Expect(LogType.Warning, new Regex("'Back_COL' shares its mesh 'Back_COL' with another node"));
            CollisionRootPlacement.ApplyOnImport(root, new List<string> { "Seat_COL[0]", "Back_COL[0]" }, "Bench.fbx", new HashSet<string> { "Seat_COL" });

            Assert.AreEqual(new Vector3(.4f, 0, 0), animated.localPosition, "a node the clips animate is left as it is");
            Assert.AreEqual(new Vector3(0, 1, 0), shown.localPosition, "a mesh a skinned renderer shows too is left as it is");
        }

        [Test]
        public void Candidates_TakeOffsetCollisionChildrenOfTheRootThatCanMove()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var offset = new Vector3(.4f, 0, 0);
            Node(root.transform, "Seat_COL", Box("Seat_COL"), offset, Quaternion.identity);
            Node(root.transform, "Back_COL", Box("Back_COL"), Vector3.zero, Quaternion.identity);
            Node(root.transform, "Chair_COLOR", Box("Chair_COLOR"), offset, Quaternion.identity);
            var arm = Node(root.transform, "Arm_COL", Box("Arm_COL"), offset, Quaternion.identity);
            Node(arm, "Arm", Box("Arm"), Vector3.zero, Quaternion.identity);
            Node(root.transform, "Leg_COL", Box("Leg_COL"), offset, Quaternion.identity);

            var result = CollisionRootPlacement.Candidates(root, new HashSet<string> { "Leg_COL" }, out var skipped);

            CollectionAssert.AreEqual(new[] { "Seat_COL" }, result.Select(t => t.name).ToArray(), "at the root already, or no collision name: nothing to place");
            Assert.AreEqual(2, skipped.Count);
            StringAssert.Contains("'Arm_COL' holds 'Arm', which is no collision node", skipped[0]);
            StringAssert.Contains("'Leg_COL' has transform curves", skipped[1]);
        }

        [Test]
        public void Consumers_RefuseAnObjectWithChildrenAndAColliderAwayFromItsNode()
        {
            var mesh = Box("Seat_COL");
            var copy = Node(null, "Seat_COL", mesh, Vector3.zero, Quaternion.identity); owned.Add(copy.gameObject);
            Node(copy, "Added", null, Vector3.zero, Quaternion.identity);
            var holder = new GameObject("Holder"); owned.Add(holder);
            holder.AddComponent<MeshCollider>().sharedMesh = mesh;

            var consumers = CollisionConsumers.Scan("Assets/None.fbx", null, new Dictionary<Mesh, string> { { mesh, "Seat_COL[0]" } });

            Assert.AreEqual(2, consumers.Refusals.Count);
            StringAssert.Contains("'Seat_COL' has child objects of its own, which would move with 'Seat_COL'", consumers.Refusals[0]);
            StringAssert.Contains("'Holder' has a MeshCollider using 'Seat_COL' away from its node", consumers.Refusals[1]);
        }

        [Test]
        public void Consumers_KeepAnUnpackedCopyWhereItWas()
        {
            var mesh = Box("Seat_COL");
            var copy = Node(null, "Bench copy", null, new Vector3(3, 0, -2), Quaternion.Euler(0, 90, 0)); owned.Add(copy.gameObject);
            var node = Node(copy, "Seat_COL", mesh, new Vector3(.4f, 0, 0), Quaternion.Euler(0, 30, 0));
            var offset = CollisionConsumers.Local(node);
            var before = new Dictionary<string, Matrix4x4> { { "Seat_COL[0]", offset } };
            var after = new Dictionary<string, Matrix4x4> { { "Seat_COL[0]", Matrix4x4.identity } };
            var world = World(node);

            var consumers = CollisionConsumers.Scan("Assets/None.fbx", null, new Dictionary<Mesh, string> { { mesh, "Seat_COL[0]" } });
            CollectionAssert.IsEmpty(consumers.Refusals);
            CollectionAssert.IsEmpty(consumers.Problems(before, after));
            MeshTransform.BakeMatrix(mesh, offset);   // the change: the mesh takes its node's offset
            Assert.AreEqual((1, 0), consumers.Apply(before, after));

            Assert.IsNull(LodHierarchy.OffsetFromRoot(copy, node), "the copy's node takes the root's placement");
            var moved = World(node);
            for (int i = 0; i < world.Length; i++) Assert.Less(Vector3.Distance(world[i], moved[i]), 1e-4f, "its collision stays where it was");
        }

        [Test]
        public void DeleteKeepingCollisionPlacement_KeepsOnlyTheNodesPlacedAtTheRoot()
        {
            if (!AssetDatabase.IsValidFolder("Assets/UnityMeshLab")) AssetDatabase.CreateFolder("Assets", "UnityMeshLab");
            string fbx = "Assets/UnityMeshLab/Placement-" + Guid.NewGuid().ToString("N") + ".fbx";
            string sidecar = SidecarStore.PathFor(fbx);
            try
            {
                SidecarStore.SetCollisionAtRoot(fbx, new[] { "Seat_COL[0]" });
                Assert.IsNull(SidecarStore.LoadSettings(sidecar), "a sidecar no settings were saved in has none");
                SidecarStore.SaveSettings(sidecar, settings => { });
                Assert.IsNotNull(SidecarStore.LoadSettings(sidecar), "saved settings, the defaults included");

                Assert.AreEqual(1, SidecarStore.DeleteKeepingCollisionPlacement(new[] { fbx }));

                CollectionAssert.AreEqual(new[] { "Seat_COL[0]" }, SidecarStore.CollisionAtRoot(fbx), "a UV2 reset keeps the nodes placed at the root");
                Assert.IsNull(SidecarStore.LoadSettings(sidecar), "and drops the rest of the sidecar");
                var kept = SidecarStore.Load(fbx);
                kept.toolSettings = new ToolSettings { atlasResolution = 2048 };   // as saved before ToolSettings.saved
                SidecarStore.Save(kept);
                Assert.AreEqual(2048, SidecarStore.LoadSettings(sidecar)?.atlasResolution, "older settings count when they differ from the defaults");
            }
            finally { AssetDatabase.DeleteAsset(sidecar); }
        }

        [Test]
        public void NodeName_ReadsTheNameBackFromAPath()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var node = Node(root.transform, "Seat COL/1", null, Vector3.zero, Quaternion.identity);
            Assert.AreEqual("Seat COL/1", CollisionHierarchyAnalyzer.NodeName(CollisionHierarchyAnalyzer.RelativePath(root.transform, node)));
        }
    }
}
