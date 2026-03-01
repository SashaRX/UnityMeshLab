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
            if (go == null) { UvtLog.Error("[xatlas] Select a GameObject with MeshFilter"); return; }

            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) { UvtLog.Error("[xatlas] No MeshFilter/Mesh"); return; }

            Mesh mesh = Object.Instantiate(mf.sharedMesh);
            mesh.name = mf.sharedMesh.name + "_UV2";

            var opts = RepackOptions.Default;
            opts.padding = 4;

            var result = XatlasRepack.RepackSingle(mesh, opts);

            if (!result.ok)
            {
                UvtLog.Error($"[xatlas] FAILED: {result.error}");
                Object.DestroyImmediate(mesh);
                return;
            }

            mf.sharedMesh = mesh;

            UvtLog.Verbose("[xatlas] ═══════════════════════════════════════");
            UvtLog.Verbose($"[xatlas] Result: {mesh.name}");
            UvtLog.Verbose($"[xatlas]   Atlas: {result.atlasWidth} x {result.atlasHeight}");
            UvtLog.Verbose($"[xatlas]   Charts: {result.chartCount}");
            UvtLog.Verbose($"[xatlas]   Shells: {result.shellCount}");
            UvtLog.Verbose($"[xatlas]   Overlap groups: {result.overlapGroupCount}");
            UvtLog.Verbose($"[xatlas]   Conflict vertices: {result.conflictVertices}");
            UvtLog.Verbose($"[xatlas]   Orphan vertices: {result.orphanVertices}");
            UvtLog.Verbose($"[xatlas]   Orphan triangles: {result.orphanTriangles}");
            UvtLog.Verbose($"[xatlas]   Snapped vertices: {result.snappedVertices}");
            UvtLog.Verbose("[xatlas] ═══════════════════════════════════════");
        }
    }
}
