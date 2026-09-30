using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests.Editor
{
    /// <summary>Keep-hierarchy remesh: the renderer collection shared by the weld capture and the
    /// per-node scope, and the settings plumbing. Native remeshing itself is covered by the
    /// Native~ ctest battery and the DllNotFoundException-skipping stage tests.</summary>
    public sealed class RemeshHierarchyTests
    {
        static Mesh TriangleMesh()
        {
            var mesh = new Mesh { name = "TestTriangle" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            return mesh;
        }

        [Test]
        public void CollectRenderers_AppliesTheCaptureFilters()
        {
            var mesh = TriangleMesh();
            var collisionMesh = TriangleMesh(); collisionMesh.name = "Body_COL";
            var root = new GameObject("Root");
            try {
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform);
                body.AddComponent<MeshFilter>().sharedMesh = mesh;
                body.AddComponent<MeshRenderer>();

                var lod1 = new GameObject("Body_LOD1");
                lod1.transform.SetParent(root.transform);
                lod1.AddComponent<MeshFilter>().sharedMesh = mesh;
                lod1.AddComponent<MeshRenderer>();

                var collision = new GameObject("Body_COL");
                collision.transform.SetParent(root.transform);
                collision.AddComponent<MeshFilter>().sharedMesh = mesh;
                collision.AddComponent<MeshRenderer>();

                var inactive = new GameObject("Dark");
                inactive.transform.SetParent(root.transform);
                inactive.AddComponent<MeshFilter>().sharedMesh = mesh;
                var hidden = inactive.AddComponent<MeshRenderer>();
                hidden.enabled = false;

                // A normally named object carrying a collision mesh asset is filtered
                // by the mesh name, exactly like the weld capture does.
                var hull = new GameObject("Hull");
                hull.transform.SetParent(root.transform);
                hull.AddComponent<MeshFilter>().sharedMesh = collisionMesh;
                hull.AddComponent<MeshRenderer>();

                var collected = RemeshSource.CollectRenderers(root, lod0Only: true);
                Assert.AreEqual(1, collected.Count, "only the enabled LOD0 non-collision renderer is a node");
                Assert.AreEqual(body.transform, collected[0].transform);

                var withLods = RemeshSource.CollectRenderers(root, lod0Only: false);
                Assert.AreEqual(2, withLods.Count, "lod0Only=false still excludes the collision nodes and the disabled renderer");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(collisionMesh); }
        }

        [Test]
        public void BakeScaleIntoMesh_MirrorsWindingAndHandednessAndRefreshesBounds()
        {
            var mesh = TriangleMesh();
            mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            try {
                RemeshExporter.BakeScaleIntoMesh(mesh, new Vector3(-2, 3, 1));
                Assert.AreEqual(new Vector3(-2, 0, 0), mesh.vertices[1]);
                Assert.AreEqual(new Vector3(0, 3, 0), mesh.vertices[2]);
                Assert.AreEqual(new Vector3(-2, 3, 0) * 0.5f, mesh.bounds.center, "bounds follow the scaled vertices");
                Assert.AreEqual(new[] { 0, 2, 1 }, mesh.triangles, "a mirroring scale flips the winding");
                var t = mesh.tangents[0];
                Assert.AreEqual(-1f, t.x, 1e-5f, "tangents take the forward scale");
                Assert.AreEqual(-1f, t.w, "and the handedness flips with the mirror");
                Assert.AreEqual(1f, Mathf.Abs(Vector3.Dot(mesh.normals[0], Vector3.forward)), 1e-5f);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void KeepHierarchy_SurvivesTheSettingsRoundTrip()
        {
            var settings = new RemeshSettings { keepHierarchy = true, voxelResolution = 64 };
            var restored = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(settings));
            Assert.IsTrue(restored.keepHierarchy, "keepHierarchy persists through the EditorPrefs JSON");
            Assert.AreEqual(64, restored.voxelResolution);
            Assert.IsFalse(new RemeshSettings().keepHierarchy, "the weld remains the default");
        }
    }
}
