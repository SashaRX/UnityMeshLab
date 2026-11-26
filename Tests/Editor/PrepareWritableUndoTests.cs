using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    // Cleanup's mesh fixes take each mesh through PrepareWritable and then rewrite it whole, many
    // meshes in one Undo group. A property record of a rewritten mesh made Undo's next operation
    // cost seconds, then minutes, from the second mesh on (Optimize Mesh on a six-mesh model).
    public sealed class PrepareWritableUndoTests
    {
        // A folder of its own per test, so the teardown deletes only what the test made.
        string Folder;
        readonly List<Object> owned = new List<Object>();

        [SetUp]
        public void CreateFolder()
        {
            string name = "__MeshLabPrepareWritableUndoTests_" + System.Guid.NewGuid().ToString("N");
            Folder = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder("Assets", name));
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var item in owned) if (item) Object.DestroyImmediate(item);
            owned.Clear();
            if (!string.IsNullOrEmpty(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        // A grid of quads with duplicated corners, so the optimizer welds and reorders it.
        static Mesh Grid(string name, int side)
        {
            var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int b = vertices.Count;
                    vertices.AddRange(new[] { new Vector3(x, 0, y), new Vector3(x + 1, 0, y), new Vector3(x + 1, 0, y + 1), new Vector3(x, 0, y + 1) });
                    uv.AddRange(new[] { new Vector2(x, y), new Vector2(x + 1, y), new Vector2(x + 1, y + 1), new Vector2(x, y + 1) });
                    triangles.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                }
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        MeshEntry Entry(Mesh mesh, int lod)
        {
            var go = new GameObject(mesh.name); owned.Add(go);
            var filter = go.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            return new MeshEntry { lodIndex = lod, renderer = go.AddComponent<MeshRenderer>(), meshFilter = filter, originalMesh = mesh, fbxMesh = mesh };
        }

        MeshEntry AssetEntry(string name, int side, int lod)
        {
            var mesh = Grid(name, side);
            AssetDatabase.CreateAsset(mesh, $"{Folder}/{name}.asset");
            return Entry(mesh, lod);
        }

        [Test, Timeout(300000)]
        public void ManyRewrittenMeshesInOneUndoGroupStayFast()
        {
            // Six meshes of 1 600-10 000 vertices (side² quads, four corners each), around the sizes of
            // the model whose Optimize Mesh took over an hour.
            var entries = new[] { 50, 40, 32, 30, 25, 20 }.Select((side, i) => AssetEntry("Mesh" + i, side, i / 2)).ToList();
            var watch = Stopwatch.StartNew();
            using (MeshHygieneUtility.BeginUndoGroup("Cleanup: Optimize Mesh"))
                foreach (var e in entries)
                {
                    Assert.IsTrue(MeshHygieneUtility.PrepareWritable(e, "Optimize Mesh", out var mesh));
                    Assert.IsTrue(MeshOptimizer.Optimize(mesh).ok);
                }
            Assert.Less(watch.ElapsedMilliseconds, 10000, "Undo must not make each rewritten mesh cost more than the last");
        }

        [Test]
        public void UndoRestoresBothTheSwappedAssetAndAMeshEditedInPlace()
        {
            var asset = AssetEntry("Imported", 4, 0);
            var imported = asset.fbxMesh;
            var inPlace = Entry(Grid("SceneEdit", 4), 1); owned.Add(inPlace.fbxMesh);
            inPlace.sceneEditMesh = inPlace.fbxMesh;
            var before = inPlace.fbxMesh.vertices;

            Undo.IncrementCurrentGroup();
            using (MeshHygieneUtility.BeginUndoGroup("Cleanup: Optimize Mesh"))
            {
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(asset, "Optimize Mesh", out var clone));
                Assert.AreNotSame(imported, clone, "an asset mesh is edited in a clone");
                Assert.IsTrue(MeshOptimizer.Optimize(clone).ok);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(inPlace, "Optimize Mesh", out var edited));
                Assert.AreSame(inPlace.fbxMesh, edited, "a scene edit is edited in place");
                edited.vertices = edited.vertices.Select(v => v * 2).ToArray();
            }
            Undo.PerformUndo();

            Assert.AreSame(imported, asset.meshFilter.sharedMesh, "Undo puts the imported mesh back on the renderer");
            CollectionAssert.AreEqual(before, inPlace.fbxMesh.vertices, "Undo restores the mesh edited in place");
        }
    }
}
