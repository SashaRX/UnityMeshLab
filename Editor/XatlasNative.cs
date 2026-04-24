// XatlasNative.cs — P/Invoke declarations for xatlas-unity bridge
// Place in Assets/Editor/

using System.Runtime.InteropServices;

namespace SashaRX.UnityMeshLab
{
    public static class XatlasNative
    {
        const string DLL = "xatlas-unity";

        // ── Lifecycle ──
        [DllImport(DLL)] public static extern void xatlasCreate();
        [DllImport(DLL)] public static extern void xatlasDestroy();

        // ── Input ──
        [DllImport(DLL)] public static extern int xatlasAddUvMesh(
            float[]  uvData,
            uint     vertexCount,
            uint[]   indexData,
            uint     indexCount,
            uint[]   faceMaterialData,
            uint     faceCount);

        // ── Processing ──
        [DllImport(DLL)] public static extern void xatlasComputeCharts();

        [DllImport(DLL)] public static extern void xatlasPackCharts(
            int  maxChartSize,
            uint padding,
            float texelsPerUnit,
            uint resolution,
            int  bilinear,
            int  blockAlign,
            int  bruteForce);

        // ── Queries ──
        [DllImport(DLL)] public static extern int  xatlasGetMeshCount();
        [DllImport(DLL)] public static extern uint xatlasGetAtlasWidth();
        [DllImport(DLL)] public static extern uint xatlasGetAtlasHeight();
        [DllImport(DLL)] public static extern uint xatlasGetChartCount();

        // ── Raw output data ──
        [DllImport(DLL)] public static extern int xatlasGetOutputVertexCount(int meshIndex);
        [DllImport(DLL)] public static extern int xatlasGetOutputIndexCount(int meshIndex);

        [DllImport(DLL)] public static extern int xatlasGetOutputVertexData(
            int    meshIndex,
            uint[] outXref,
            float[] outUV,
            uint[] outChartIndex,
            int    maxVerts);

        [DllImport(DLL)] public static extern int xatlasGetOutputIndices(
            int    meshIndex,
            uint[] outIndices,
            int    maxIndices);
    }
}
