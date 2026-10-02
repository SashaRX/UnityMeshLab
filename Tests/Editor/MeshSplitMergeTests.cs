using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    // Editor-only (Unity objects): runs in the Unity test runner, not the console harness.
    public class MeshSplitMergeTests
    {
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created) if (o) Object.DestroyImmediate(o);
            created.Clear();
        }

        static Mesh Quad(float x0, int submeshes)
        {
            var m = new Mesh { name = "Quad" };
            m.vertices = new[] { new Vector3(x0, 0, 0), new Vector3(x0 + 1, 0, 0), new Vector3(x0 + 1, 1, 0), new Vector3(x0, 1, 0) };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            m.colors = new[] { Color.red, Color.green, Color.blue, Color.white };
            m.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            m.uv2 = new[] { Vector2.one * 0.5f, Vector2.one * 0.5f, Vector2.one * 0.5f, Vector2.one * 0.5f };
            m.subMeshCount = submeshes;
            if (submeshes == 1) m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            else { m.SetTriangles(new[] { 0, 1, 2 }, 0); m.SetTriangles(new[] { 0, 2, 3 }, 1); }
            return m;
        }

        [Test]
        public void ExtractSubmesh_CompactsVerticesAndKeepsEveryAttribute()
        {
            var source = Quad(0, 2); created.Add(source);
            var part = MeshSplitMerge.ExtractSubmesh(source, source.GetTriangles(1)); created.Add(part);
            Assert.AreEqual(3, part.vertexCount);
            Assert.AreEqual(3, part.normals.Length); Assert.AreEqual(3, part.tangents.Length); Assert.AreEqual(3, part.colors.Length);
            Assert.AreEqual(3, part.uv.Length); Assert.AreEqual(3, part.uv2.Length);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, part.GetTriangles(0));
            Assert.AreEqual(Color.red, part.colors[0]); Assert.AreEqual(Color.blue, part.colors[1]); Assert.AreEqual(Color.white, part.colors[2]);
        }

        [Test]
        public void SplitAndMerge_RoundTripThroughALodGroup()
        {
            var root = new GameObject("Asset"); created.Add(root);
            var lodGroup = root.AddComponent<LODGroup>();
            var mat = new Material(Shader.Find("Standard")); created.Add(mat);
            var go = new GameObject("Asset_LOD0"); created.Add(go);
            go.transform.SetParent(root.transform, false);
            var mesh = Quad(0, 2); created.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = new[] { mat, mat };
            lodGroup.SetLODs(new[] { new LOD(0.1f, new Renderer[] { mr }) });

            var ctx = new UvToolContext();
            ctx.Refresh(lodGroup);
            var report = MeshSplitMerge.Scan(ctx);
            Assert.AreEqual(1, report.split.Count); Assert.AreEqual(0, report.merge.Count);

            Assert.AreEqual(1, MeshSplitMerge.SplitByMaterial(ctx, report.split, "test split"));
            Assert.AreEqual(2, lodGroup.GetLODs()[0].renderers.Length, "both children took the source's LOD slot");
            foreach (var r in lodGroup.GetLODs()[0].renderers) { created.Add(r.gameObject); created.Add(r.GetComponent<MeshFilter>().sharedMesh); Assert.That(r.name, Does.EndWith("_LOD0")); }

            ctx.Refresh(lodGroup);
            report = MeshSplitMerge.Scan(ctx);
            Assert.AreEqual(0, report.split.Count); Assert.AreEqual(1, report.merge.Count); Assert.AreEqual(2, report.merge[0].entries.Count);
            Assert.AreEqual(1, MeshSplitMerge.MergeSameMaterial(ctx, report.merge, "test merge"));
            var renderers = lodGroup.GetLODs()[0].renderers;
            Assert.AreEqual(1, renderers.Length);
            var merged = renderers[0].GetComponent<MeshFilter>().sharedMesh; created.Add(merged);
            Assert.AreEqual("Asset_LOD0", renderers[0].name);
            Assert.AreEqual(6, merged.vertexCount); Assert.AreEqual(6, merged.triangles.Length);
            Assert.AreEqual(6, merged.tangents.Length); Assert.AreEqual(6, merged.colors.Length); Assert.AreEqual(6, merged.uv2.Length, "UV2 survives the merge");
        }
    }
}
