// ConvexDecompNative.cs — P/Invoke declarations for V-HACD convex decomposition in xatlas-unity bridge

using System;
using System.Runtime.InteropServices;

namespace SashaRX.UnityMeshLab
{
    public static class ConvexDecompNative
    {
        const string DLL = "xatlas-unity";

        /// <summary>
        /// Compute convex decomposition of a triangle mesh using V-HACD.
        /// Returns a native handle (IntPtr) to query results, or IntPtr.Zero on failure.
        /// Must call ConvexDecomp_Destroy when done.
        /// </summary>
        [DllImport(DLL)]
        public static extern IntPtr ConvexDecomp_Compute(
            float[] vertices,
            int     vertexCount,
            int[]   indices,
            int     indexCount,
            int     maxHulls,
            int     resolution,
            int     maxVertsPerHull,
            float   minVolumePerHull,
            int     maxRecursionDepth,
            int     shrinkWrap,
            int     fillMode,
            int     minEdgeLength,
            int     findBestPlane);

        [DllImport(DLL)]
        public static extern int ConvexDecomp_GetHullCount(IntPtr ctx);

        [DllImport(DLL)]
        public static extern int ConvexDecomp_GetHullVertexCount(IntPtr ctx, int hullIndex);

        [DllImport(DLL)]
        public static extern int ConvexDecomp_GetHullIndexCount(IntPtr ctx, int hullIndex);

        /// <summary>
        /// Copy hull vertices into outVertices (x,y,z interleaved).
        /// Returns number of floats written.
        /// </summary>
        [DllImport(DLL)]
        public static extern int ConvexDecomp_GetHullVertices(
            IntPtr  ctx,
            int     hullIndex,
            float[] outVertices,
            int     maxFloats);

        /// <summary>
        /// Copy hull triangle indices into outIndices.
        /// Returns number of ints written.
        /// </summary>
        [DllImport(DLL)]
        public static extern int ConvexDecomp_GetHullIndices(
            IntPtr ctx,
            int    hullIndex,
            int[]  outIndices,
            int    maxIndices);

        /// <summary>
        /// Free memory allocated by ConvexDecomp_Compute.
        /// </summary>
        [DllImport(DLL)]
        public static extern void ConvexDecomp_Destroy(IntPtr ctx);
    }
}
