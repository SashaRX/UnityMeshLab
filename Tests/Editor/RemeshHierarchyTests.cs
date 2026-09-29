using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests.Editor
{
    /// <summary>Keep-hierarchy remesh: the per-node capture scope (RemeshBakeTool's renderer
    /// collection) and the settings plumbing. Native remeshing itself is covered by the
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
        public void CollectHierarchyRenderers_FollowsTheWeldCaptureFilters()
        {
            var mesh = TriangleMesh();
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

                var collected = RemeshBakeTool.CollectHierarchyRenderers(root, lod0Only: true);
                Assert.AreEqual(1, collected.Count, "only the enabled LOD0 non-collision renderer is a node");
                Assert.AreEqual(body.transform, collected[0].transform);

                var withLods = RemeshBakeTool.CollectHierarchyRenderers(root, lod0Only: false);
                Assert.AreEqual(2, withLods.Count, "lod0Only=false still excludes the collision node and the disabled renderer");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
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
