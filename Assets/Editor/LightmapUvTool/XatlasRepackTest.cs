// XatlasRepackTest.cs — Menu test for xatlas repack pipeline
// Place in Assets/Editor/

using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public static class XatlasRepackTest
    {
        [MenuItem("Tools/Xatlas/Test Repack Selected")]
        static void TestRepackSelected()
        {
            var go = Selection.activeGameObject;
            if (go == null) { Debug.LogError("[xatlas] Select a GameObject with MeshFilter"); return; }

            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) { Debug.LogError("[xatlas] No MeshFilter/Mesh"); return; }

            Mesh mesh = Object.Instantiate(mf.sharedMesh);
            mesh.name = mf.sharedMesh.name + "_UV2";

            var opts = RepackOptions.Default;
            opts.padding = 4;

            var result = XatlasRepack.RepackSingle(mesh, opts);

            if (!result.ok)
            {
                Debug.LogError($"[xatlas] FAILED: {result.error}");
                Object.DestroyImmediate(mesh);
                return;
            }

            mf.sharedMesh = mesh;

            Debug.Log("[xatlas] ═══════════════════════════════════════");
            Debug.Log($"[xatlas] Result: {mesh.name}");
            Debug.Log($"[xatlas]   Atlas: {result.atlasWidth} x {result.atlasHeight}");
            Debug.Log($"[xatlas]   Charts: {result.chartCount}");
            Debug.Log($"[xatlas]   Shells: {result.shellCount}");
            Debug.Log($"[xatlas]   Overlap groups: {result.overlapGroupCount}");
            Debug.Log($"[xatlas]   Conflict vertices: {result.conflictVertices}");
            Debug.Log($"[xatlas]   Orphan vertices: {result.orphanVertices}");
            Debug.Log($"[xatlas]   Orphan triangles: {result.orphanTriangles}");
            Debug.Log($"[xatlas]   Snapped vertices: {result.snappedVertices}");
            Debug.Log("[xatlas] ═══════════════════════════════════════");
        }
    }
}
