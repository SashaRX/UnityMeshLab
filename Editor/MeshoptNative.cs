// MeshoptNative.cs — P/Invoke declarations for meshoptimizer functions in xatlas-unity bridge
// Place in Assets/Editor/

using System.Runtime.InteropServices;

namespace LightmapUvTool
{
    public static class MeshoptNative
    {
        const string DLL = "xatlas-unity";

        /// <summary>
        /// Full meshoptimizer pipeline: vertex dedup → cache → overdraw → fetch.
        /// vertexData must be interleaved with position (float3) as the first 12 bytes.
        /// Returns: 0=OK, 1=null ptr, 2=stride&lt;12, 3=indexCount not multiple of 3.
        /// </summary>
        [DllImport(DLL)]
        public static extern int meshoptOptimize(
            byte[]   vertexData,
            uint     vertexCount,
            uint     vertexStride,
            uint[]   indices,
            uint     indexCount,
            float    overdrawThreshold,
            byte[]   outVertexData,
            uint[]   outIndices,
            out uint outVertexCount);
    }
}
