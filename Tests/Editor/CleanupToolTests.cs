using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class CleanupToolTests
    {
        // The helper moved out of CleanupTool into the shared MeshHygieneUtility
        // (CleanupTool and PrefabBuilderTool both parse _LOD{N} suffixes). That
        // class is internal, so it is reached through the assembly rather than
        // a typeof().
        static bool TryParseLodIndex(string name, out int lodIndex)
        {
            var type = typeof(CleanupTool).Assembly.GetType(
                "SashaRX.UnityMeshLab.MeshHygieneUtility");
            Assert.IsNotNull(type, "MeshHygieneUtility not found in the editor assembly");
            var method = type.GetMethod(
                "TryParseLodIndex", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "MeshHygieneUtility.TryParseLodIndex not found");
            var arguments = new object[] { name, 0 };
            bool result = (bool)method.Invoke(null, arguments);
            lodIndex = (int)arguments[1];
            return result;
        }

        [TestCase("Part_LOD0", 0)]
        [TestCase("Part_lod7", 7)]
        public void TryParseLodIndex_ValidIndex_ReturnsTrue(string name, int expected)
        {
            Assert.IsTrue(TryParseLodIndex(name, out int actual));
            Assert.AreEqual(expected, actual);
        }

        [TestCase("Part_LOD8")]
        [TestCase("Part_LOD100000000")]
        [TestCase("Part_LOD999999999999999999999999999999")]
        public void TryParseLodIndex_UnsupportedOrOverflowingIndex_ReturnsFalse(string name)
        {
            Assert.IsFalse(TryParseLodIndex(name, out _));
        }

        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);

        /// <summary>
        /// A Read/Write-disabled mesh is scanned and fixed through a readable copy: the scan
        /// skipped it and reported "No mesh issues found", so no fix button appeared.
        /// </summary>
        [Test]
        public void MeshScanFindsAndRemovesAnUnusedVertexOfAnUnreadableMesh()
        {
            string folder = "Assets/__MeshLabCleanupTests_" + System.Guid.NewGuid().ToString("N");
            var go = new GameObject("Part");
            Mesh edited = null;
            try
            {
                var mesh = new Mesh { name = "Part" };
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up, new Vector3(2, 2, 2) };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up, Vector2.one };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
                AssetDatabase.CreateAsset(mesh, folder + "/Part.asset");
                mesh.UploadMeshData(true);
                Assert.IsFalse(mesh.isReadable);

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var ctx = new UvToolContext { StandaloneMesh = true };
                ctx.MeshEntries.Add(new MeshEntry { renderer = go.AddComponent<MeshRenderer>(), meshFilter = filter, originalMesh = mesh, fbxMesh = mesh });
                var tool = new CleanupTool();
                tool.OnActivate(ctx, null);
                typeof(CleanupTool).GetMethod("ScanMesh", Private).Invoke(tool, null);
                var report = Get(tool, "meshReport");
                var unused = (IList)Get(report, "unusedVertEntries");
                Assert.AreEqual(1, unused.Count, "the unreadable mesh is checked");
                Assert.AreEqual(1, Get(unused[0], "count"));
                Assert.IsEmpty((IList)Get(report, "unreadMeshes"));

                typeof(CleanupTool).GetMethod("FixMeshRemoveUnusedVertices", Private).Invoke(tool, null);
                edited = filter.sharedMesh;
                Assert.AreNotSame(mesh, edited, "the asset mesh is edited in a scene copy");
                Assert.IsTrue(edited.isReadable);
                Assert.AreEqual(4, edited.vertexCount);
                Assert.AreEqual(5, mesh.vertexCount, "the asset keeps its vertices");
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (edited) Object.DestroyImmediate(edited);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
