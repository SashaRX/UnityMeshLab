using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // The same simplification library serves individual meshes, LOD batches and
    // indexed geometry before normals/UVs exist. Scene grouping belongs to LodPipelineOps.
    public static partial class MeshSimplifier
    {
        internal sealed class IndexedGeometry
        {
            internal Vector3[] positions;
            internal int[] indices;
        }

        internal struct GeometrySettings
        {
            internal int targetTriangles;
            internal float maximumError;
            internal uint flags;
        }

        const string GeometryLibrary = "xatlas-unity";
        [DllImport(GeometryLibrary, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabSimplify(float[] positions, uint vertexCount, int[] indices, uint indexCount,
            uint targetTriangles, float error, uint flags, out IntPtr handle, out uint vertices, out uint outputIndices,
            out float resultError);
        [DllImport(GeometryLibrary, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabMeshCopy(IntPtr handle, [Out] float[] positions, uint vertexCapacity,
            [Out] int[] indices, uint indexCapacity);
        [DllImport(GeometryLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshLabMeshDestroy(IntPtr handle);

        internal static IndexedGeometry SimplifyGeometry(Vector3[] positions, int[] indices,
            GeometrySettings settings, CancellationToken token, out float error)
        {
            token.ThrowIfCancellationRequested();
            if (positions == null || indices == null || positions.Length == 0 || indices.Length < 3 || indices.Length % 3 != 0 ||
                settings.targetTriangles < 0 || settings.targetTriangles > 5000000 || settings.maximumError < 0 || settings.maximumError > 1 ||
                (settings.flags & ~31u) != 0 ||
                float.IsNaN(settings.maximumError) || float.IsInfinity(settings.maximumError))
                throw new ArgumentException("Invalid geometry or simplification settings.");
            IntPtr handle = IntPtr.Zero;
            try
            {
                int code = meshLabSimplify(PackPositions(positions), (uint)positions.Length, indices,
                    (uint)indices.Length, (uint)settings.targetTriangles, settings.maximumError, settings.flags,
                    out handle, out uint vertexCount, out uint indexCount, out error);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new InvalidOperationException("Simplification failed: " + SimplificationError(code));
                return CopyGeometry(handle, vertexCount, indexCount);
            }
            finally { if (handle != IntPtr.Zero) meshLabMeshDestroy(handle); }
        }

        internal static float[] PackPositions(Vector3[] positions)
        {
            var packed = new float[checked(positions.Length * 3)];
            for (int i = 0; i < positions.Length; ++i)
            {
                packed[i * 3] = positions[i].x; packed[i * 3 + 1] = positions[i].y; packed[i * 3 + 2] = positions[i].z;
            }
            return packed;
        }

        static string SimplificationError(int code)
        {
            if (code == 1) return "invalid geometry or settings";
            if (code == 3) return "empty output; reduce simplification";
            return "native processing error";
        }

        internal static IndexedGeometry CopyGeometry(IntPtr handle, uint vertexCount, uint indexCount)
        {
            var data = new float[checked((int)vertexCount * 3)];
            var result = new IndexedGeometry { positions = new Vector3[vertexCount], indices = new int[indexCount] };
            if (meshLabMeshCopy(handle, data, vertexCount, result.indices, indexCount) != 0)
                throw new InvalidOperationException("Geometry output copy failed.");
            for (int i = 0; i < vertexCount; ++i) result.positions[i] = new Vector3(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);
            return result;
        }
    }
}
