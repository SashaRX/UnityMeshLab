using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class RemeshNative
    {
        const string Library = "xatlas-unity";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabRemeshVersion();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabRemeshBuild(float[] positions, uint vertexCount, int[] indices, uint indexCount,
            int resolution, uint targetTriangles, float error, float crease, float smoothing,
            uint textureSize, uint padding, uint flags, out IntPtr handle, out uint vertices, out uint outputIndices);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabRemeshCopy(IntPtr handle, [Out] float[] vertices, uint vertexCapacity,
            [Out] int[] indices, uint indexCapacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern void meshLabRemeshDestroy(IntPtr handle);

        internal sealed class Geometry
        {
            public Vector3[] positions, normals;
            public Vector2[] uv;
            public int[] indices;
        }

        public static void CheckAvailable()
        {
            try {
                if (meshLabRemeshVersion() != 1) throw new InvalidOperationException("Unsupported remesh native ABI.");
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException) {
                throw new InvalidOperationException("Remesh requires rebuilt native plugins. Install the binaries from Build Native Libraries for this commit, then restart Unity.", e);
            }
        }

        public static Geometry Build(Vector3[] positions, int[] indices, RemeshSettings settings, CancellationToken token)
        {
            settings.Validate();
            token.ThrowIfCancellationRequested();
            var packed = new float[checked(positions.Length * 3)];
            for (int i = 0; i < positions.Length; ++i) {
                packed[i * 3] = positions[i].x; packed[i * 3 + 1] = positions[i].y; packed[i * 3 + 2] = positions[i].z;
            }
            IntPtr handle = IntPtr.Zero;
            try {
                int code = meshLabRemeshBuild(packed, (uint)positions.Length, indices, (uint)indices.Length,
                    settings.voxelResolution, (uint)settings.targetTriangles, settings.maximumError,
                    settings.normalCrease * Mathf.Deg2Rad, settings.normalSmoothing, (uint)settings.textureResolution,
                    (uint)settings.padding, (settings.solve ? 1u : 0u) | (settings.shell ? 2u : 0u),
                    out handle, out uint vertexCount, out uint indexCount);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new InvalidOperationException("Remesh failed: " + Error(code));
                var data = new float[checked((int)vertexCount * 8)];
                var result = new Geometry { positions = new Vector3[vertexCount], normals = new Vector3[vertexCount],
                    uv = new Vector2[vertexCount], indices = new int[indexCount] };
                if (meshLabRemeshCopy(handle, data, vertexCount, result.indices, indexCount) != 0)
                    throw new InvalidOperationException("Remesh output copy failed.");
                for (int i = 0; i < vertexCount; ++i) {
                    result.positions[i] = new Vector3(data[i * 8], data[i * 8 + 1], data[i * 8 + 2]);
                    result.normals[i] = new Vector3(data[i * 8 + 3], data[i * 8 + 4], data[i * 8 + 5]);
                    result.uv[i] = new Vector2(data[i * 8 + 6], data[i * 8 + 7]);
                }
                return result;
            }
            finally { if (handle != IntPtr.Zero) meshLabRemeshDestroy(handle); }
        }

        static string Error(int code)
        {
            switch (code) {
                case 1: return "invalid geometry or settings";
                case 2: return "intermediate mesh exceeds five million triangles; reduce voxel resolution";
                case 3: return "empty output; increase voxel resolution or reduce simplification";
                case 4: return "UV unwrap rejected the remeshed geometry";
                case 5: return "UV unwrap needs multiple atlases; reduce padding or increase texture resolution";
                case 7: return "UV unwrap produced no usable atlas";
                case 8: return "UV unwrap returned invalid or unmapped vertices";
                default: return "native allocation or processing error";
            }
        }
    }
}
