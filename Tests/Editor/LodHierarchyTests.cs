// LodHierarchyTests.cs — a LODGroup read from and written to a scratch hierarchy.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class LodHierarchyTests
    {
        readonly List<Object> scratch = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in scratch) if (o != null) Object.DestroyImmediate(o);
            scratch.Clear();
        }

        Mesh Tris(int count)
        {
            var m = new Mesh();
            var v = new Vector3[count * 3];
            var t = new int[count * 3];
            for (int i = 0; i < t.Length; i++) { v[i] = new Vector3(i, 0, 0); t[i] = i; }
            m.vertices = v; m.triangles = t;
            scratch.Add(m);
            return m;
        }

        GameObject Node(string name, Transform parent, Mesh mesh = null)
        {
            var go = new GameObject(name);
            scratch.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            if (mesh != null) { go.AddComponent<MeshFilter>().sharedMesh = mesh; go.AddComponent<MeshRenderer>(); }
            return go;
        }

        [Test]
        public void CollectByNameReadsDirectChildrenOrTheWholeSubtreeAndSkipsCollision()
        {
            var root = Node("Chair", null);
            Node("Chair_LOD0", root.transform, Tris(4));
            Node("Chair_LOD2", root.transform, Tris(1));
            Node("Chair_COL", root.transform, Tris(1));
            var container = Node("Parts", root.transform);
            Node("Leg_LOD1", container.transform, Tris(2));
            Node("Decor", root.transform, Tris(1));

            var direct = LodHierarchy.CollectByName(root.transform, recursive: false);
            CollectionAssert.AreEqual(new[] { 0, 2 }, direct.Keys);

            var deep = LodHierarchy.CollectByName(root.transform, recursive: true);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, deep.Keys);
            Assert.AreEqual("Leg_LOD1", deep[1][0].name);
        }

        [Test]
        public void LodsFromNamesKeepGapsOrCompactAndLayOutTransitions()
        {
            var root = Node("Chair", null);
            var a = Node("Chair_LOD0", root.transform, Tris(4)).GetComponent<Renderer>();
            var c = Node("Chair_LOD2", root.transform, Tris(1)).GetComponent<Renderer>();
            var byIndex = LodHierarchy.CollectByName(root.transform, false);

            var gapped = LodHierarchy.LodsFromNames(byIndex, keepEmptySlots: true, LodHierarchy.Transitions.Halving);
            Assert.AreEqual(3, gapped.Length);
            Assert.AreEqual(0, gapped[1].renderers.Length, "the missing LOD1 is an empty slot");
            Assert.AreSame(c, gapped[2].renderers[0]);
            Assert.AreEqual(new[] { .5f, .25f, .125f }, new[] { gapped[0].screenRelativeTransitionHeight, gapped[1].screenRelativeTransitionHeight, gapped[2].screenRelativeTransitionHeight });

            var compact = LodHierarchy.LodsFromNames(byIndex, keepEmptySlots: false, LodHierarchy.Transitions.Linear);
            Assert.AreEqual(2, compact.Length);
            Assert.AreSame(a, compact[0].renderers[0]);
            Assert.AreSame(c, compact[1].renderers[0]);
            Assert.AreEqual(1f, compact[0].screenRelativeTransitionHeight, 1e-6f);
            Assert.AreEqual(.01f, compact[1].screenRelativeTransitionHeight, 1e-6f);
            Assert.AreEqual(.01f, LodHierarchy.Transition(LodHierarchy.Transitions.Linear, 0, 1), 1e-6f, "a single level is nearly culled");
            Assert.AreEqual(0, LodHierarchy.LodsFromNames(new SortedDictionary<int, List<Renderer>>(), true, LodHierarchy.Transitions.Halving).Length);
        }

        [Test]
        public void RebuildFromNamesWritesTheGroupAndLeavesItWhenNothingIsNamed()
        {
            var root = Node("Chair", null);
            var lg = root.AddComponent<LODGroup>();
            Node("Chair_LOD0", root.transform, Tris(4));
            Node("Chair_LOD1", root.transform, Tris(2));

            Assert.AreEqual(2, LodHierarchy.RebuildFromNames(lg, false, true, LodHierarchy.Transitions.Halving));
            Assert.AreEqual(2, lg.GetLODs().Length);
            Assert.AreEqual("Chair_LOD1", lg.GetLODs()[1].renderers[0].name);

            var bare = Node("Table", null);
            var bareGroup = bare.AddComponent<LODGroup>();
            Node("Top", bare.transform, Tris(1));
            Assert.AreEqual(0, LodHierarchy.RebuildFromNames(bareGroup, false, true, LodHierarchy.Transitions.Halving));
            Assert.AreEqual(1, bareGroup.GetLODs().Length, "a fresh LODGroup's default slot is untouched");
        }

        [Test]
        public void CreateFromRenderersAndSiblings()
        {
            var root = Node("Chair", null);
            Node("A", root.transform, Tris(1));
            Node("B", root.transform, Tris(1));
            var lg = LodHierarchy.CreateFromRenderers(root, 0.5f, requireRenderers: true, out int count);
            Assert.AreEqual(2, count);
            Assert.AreEqual(2, lg.GetLODs()[0].renderers.Length);
            Assert.AreEqual(.5f, lg.GetLODs()[0].screenRelativeTransitionHeight);

            var empty = Node("Empty", null);
            Assert.IsNull(LodHierarchy.CreateFromRenderers(empty, 0.5f, requireRenderers: true, out _));
            Assert.IsNotNull(LodHierarchy.CreateFromRenderers(empty, 0.5f, requireRenderers: false, out _), "the Cleanup button adds an empty group");

            var parent = Node("Desk", null);
            var s0 = Node("Desk_LOD0", parent.transform, Tris(3));
            var s2 = Node("Desk_LOD2", parent.transform, Tris(1));
            var fromSiblings = LodHierarchy.CreateFromSiblings(new List<(GameObject, int)> { (s0, 0), (s2, 2) });
            Assert.AreSame(parent, fromSiblings.gameObject);
            Assert.AreEqual(2, fromSiblings.GetLODs().Length, "gaps close up");
            Assert.AreEqual(.25f, fromSiblings.GetLODs()[1].screenRelativeTransitionHeight);
        }

        [Test]
        public void CompactDropsNullRenderersAndOptionallyEmptySlots()
        {
            var root = Node("Chair", null);
            var lg = root.AddComponent<LODGroup>();
            var r = Node("Chair_LOD0", root.transform, Tris(1)).GetComponent<Renderer>();
            lg.SetLODs(new[] { new LOD(.5f, new[] { r, null }), new LOD(.25f, new Renderer[0]) });

            Assert.IsTrue(LodHierarchy.Compact(lg, removeEmptySlots: false));
            Assert.AreEqual(2, lg.GetLODs().Length);
            Assert.AreEqual(1, lg.GetLODs()[0].renderers.Length);
            Assert.IsFalse(LodHierarchy.Compact(lg, removeEmptySlots: false), "nothing left to do");
            Assert.IsTrue(LodHierarchy.Compact(lg, removeEmptySlots: true));
            Assert.AreEqual(1, lg.GetLODs().Length);
        }

        [Test]
        public void MoveRootMeshToChildSortsTheChildrenByPolycount()
        {
            var root = Node("Chair.01", null, Tris(5));
            root.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var lg = root.AddComponent<LODGroup>();
            Node("Low", root.transform, Tris(1));
            Node("Mid", root.transform, Tris(3));
            Node("Chair_COL", root.transform, Tris(1));
            Assert.IsFalse(LodHierarchy.RootRendererIsLod0(root, lg.GetLODs()));

            Assert.AreEqual(3, LodHierarchy.MoveRootMeshToChild(root));

            Assert.IsNull(root.GetComponent<MeshFilter>(), "the root is a pivot now");
            var lod0 = root.transform.Find("Chair_01_LOD0");
            Assert.IsNotNull(lod0, "the root mesh (5 triangles) is LOD0 under the sanitized base name");
            Assert.AreEqual(5 * 3, lod0.GetComponent<MeshFilter>().sharedMesh.triangles.Length);
            Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, lod0.GetComponent<MeshRenderer>().shadowCastingMode);
            Assert.AreEqual("Chair_01_LOD1", root.transform.GetChild(1).name);
            Assert.AreEqual("Chair_01_LOD2", root.transform.GetChild(0).name);
            Assert.IsNotNull(root.transform.Find("Chair_COL"), "collision children keep their name");
        }

        [Test]
        public void RootRendererIsLod0WhenTheGroupSaysSo()
        {
            var root = Node("Chair", null, Tris(1));
            var lg = root.AddComponent<LODGroup>();
            lg.SetLODs(new[] { new LOD(.5f, new[] { root.GetComponent<Renderer>() }) });
            Assert.IsTrue(LodHierarchy.RootRendererIsLod0(root, lg.GetLODs()));
            Assert.IsFalse(LodHierarchy.RootRendererIsLod0(root, new LOD[0]));
        }

        [Test]
        public void ColliderComesFromTheFirstCollisionChildAndCanBeAssigned()
        {
            var root = Node("Chair", null);
            var col = Node("Chair_COL", root.transform, Tris(2));
            Assert.IsNotNull(LodHierarchy.AddColliderFromCollisionMesh(root));
            Assert.AreSame(col.GetComponent<MeshFilter>().sharedMesh, root.GetComponent<MeshCollider>().sharedMesh);
            Assert.IsFalse(col.GetComponent<MeshRenderer>().enabled, "the collision node no longer renders");
            Assert.IsNull(LodHierarchy.AddColliderFromCollisionMesh(root), "a root with a collider is left alone");

            var other = Tris(1);
            Assert.AreSame(other, LodHierarchy.AssignCollider(root, other).sharedMesh);
            Assert.IsNull(LodHierarchy.AddColliderFromCollisionMesh(Node("Bare", null)));
        }
    }
}
