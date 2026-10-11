// MeshoptNative.cs — P/Invoke declarations for meshoptimizer functions in xatlas-unity bridge
// Place in Assets/Editor/

using System.Runtime.InteropServices;

namespace SashaRX.UnityMeshLab
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
            uint     dedupStride,
            uint[]   indices,
            uint     indexCount,
            float    overdrawThreshold,
            byte[]   outVertexData,
            uint[]   outIndices,
            out uint outVertexCount);

        /// <summary>
        /// Mesh simplification with attribute preservation via meshopt_simplifyWithAttributes.
        /// Reduces triangle count while respecting weighted vertex attributes (normals, UVs).
        /// Returns: 0=OK, 1=null ptr, 2=stride&lt;12, 3=indexCount%3!=0, 4=attributeCount&gt;16.
        /// </summary>
        [DllImport(DLL)]
        public static extern int meshoptSimplify(
            byte[]    vertexData,
            uint      vertexCount,
            uint      vertexStride,
            uint[]    indices,
            uint      indexCount,
            float[]   attributes,
            uint      attributeStride,
            float[]   attributeWeights,
            uint      attributeCount,
            float     targetRatio,
            float     targetError,
            uint      options,
            uint[]    outIndices,
            out uint  outIndexCount,
            out float outResultError);

        [DllImport(DLL)]
        internal static extern int meshoptConstraintVersion();

        // Additive ABI: byte count must equal vertexCount, including unused wedges.
        [DllImport(DLL)]
        internal static extern int meshoptSimplifyConstrained(
            byte[] vertexData, uint vertexCount, uint vertexStride,
            uint[] indices, uint indexCount,
            float[] attributes, uint attributeStride,
            float[] attributeWeights, uint attributeCount,
            byte[] vertexLocks, uint lockCount,
            float targetRatio, float targetError, uint options,
            uint[] outIndices, out uint outIndexCount, out float outResultError);

        internal const byte VertexLock = 1;
        internal const byte VertexProtect = 2;

        // meshopt simplify option flags (match meshoptimizer.h enum)
        public const uint SimplifyLockBorder    = 1;
        public const uint SimplifySparse        = 2;
        public const uint SimplifyErrorAbsolute = 4;
        public const uint SimplifyPrune         = 8;
        // meshoptimizer v1.3, pinned in Native~/CMakeLists.txt; existing options ABI.
        public const uint SimplifyPermissive    = 1 << 5;
    }
}
