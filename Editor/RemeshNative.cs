using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class RemeshNative
    {
        const string Library = "xatlas-unity";
        const int AbiVersion = 2;
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabRemeshVersion();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern void meshLabRemeshDestroy(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabVoxelRemesh(float[] positions, uint vertexCount, int[] indices, uint indexCount,
            int resolution, uint flags, out IntPtr handle, out uint vertices, out uint outputIndices);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabSimplify(float[] positions, uint vertexCount, int[] indices, uint indexCount,
            uint targetTriangles, float error, uint flags, out IntPtr handle, out uint vertices, out uint outputIndices,
            out float resultError);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabMeshCopy(IntPtr handle, [Out] float[] positions, uint vertexCapacity,
            [Out] int[] indices, uint indexCapacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern void meshLabMeshDestroy(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabUnwrap(float[] positions, uint vertexCount, int[] indices, uint indexCount,
            float crease, float smoothing, float[] options, uint optionCount,
            out IntPtr handle, out uint vertices, out uint outputIndices, out uint charts);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern int meshLabUnwrapCopy(IntPtr handle, [Out] float[] vertices, uint vertexCapacity,
            [Out] int[] indices, uint indexCapacity, [Out] int[] charts);

        /// <summary>Indexed positions produced by the voxel and simplify stages.</summary>
        internal sealed class IndexedMesh
        {
            public Vector3[] positions;
            public int[] indices;
            public int TriangleCount => indices.Length / 3;
        }

        /// <summary>Unwrapped result: split vertices with normals, UV0 and xatlas chart ids.</summary>
        internal sealed class Geometry
        {
            public Vector3[] positions, normals;
            public Vector2[] uv;
            public int[] indices, charts;
            public int chartCount;
        }

        public static void CheckAvailable()
        {
            try {
                if (meshLabRemeshVersion() != AbiVersion) throw new InvalidOperationException("Unsupported remesh native ABI.");
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException) {
                throw new InvalidOperationException("Remesh requires rebuilt native plugins. Install the binaries from Build Native Libraries for this commit, then restart Unity.", e);
            }
        }

        public static IndexedMesh Voxelize(Vector3[] positions, int[] indices, RemeshSettings settings, CancellationToken token)
        {
            settings.Validate();
            token.ThrowIfCancellationRequested();
            IntPtr handle = IntPtr.Zero;
            try {
                int code = meshLabVoxelRemesh(Pack(positions), (uint)positions.Length, indices, (uint)indices.Length,
                    settings.voxelResolution, (settings.solve ? 1u : 0u) | (settings.shell ? 2u : 0u),
                    out handle, out uint vertexCount, out uint indexCount);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new InvalidOperationException("Voxel remesh failed: " + Error(code));
                return CopyMesh(handle, vertexCount, indexCount);
            }
            finally { if (handle != IntPtr.Zero) meshLabMeshDestroy(handle); }
        }

        public static IndexedMesh Simplify(IndexedMesh input, RemeshSettings settings, CancellationToken token, out float error)
        {
            settings.Validate();
            token.ThrowIfCancellationRequested();
            uint flags = (settings.regularize == RemeshRegularize.Light ? 1u : 0u) |
                (settings.regularize == RemeshRegularize.Strong ? 2u : 0u) |
                (settings.preserveFolds ? 4u : 0u) | (settings.pruneSmallParts ? 16u : 0u);
            IntPtr handle = IntPtr.Zero;
            try {
                int code = meshLabSimplify(Pack(input.positions), (uint)input.positions.Length, input.indices,
                    (uint)input.indices.Length, (uint)settings.targetTriangles, settings.maximumError, flags,
                    out handle, out uint vertexCount, out uint indexCount, out error);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new InvalidOperationException("Simplification failed: " + Error(code));
                return CopyMesh(handle, vertexCount, indexCount);
            }
            finally { if (handle != IntPtr.Zero) meshLabMeshDestroy(handle); }
        }

        public static Geometry Unwrap(IndexedMesh input, RemeshSettings settings, CancellationToken token)
        {
            settings.Validate();
            token.ThrowIfCancellationRequested();
            bool angle = settings.hardEdges == RemeshHardEdges.Angle || settings.hardEdges == RemeshHardEdges.UvIslandsAndAngle;
            // Matches ParseUnwrapOptions in Native~/src/remesh.cpp.
            float[] options = {
                settings.maxChartArea, settings.maxChartBoundary, settings.chartNormalDeviation, settings.chartRoundness,
                settings.chartStraightness, settings.chartNormalSeam, 0.5f, settings.chartMaxCost, settings.chartIterations,
                settings.textureResolution, settings.padding, 0, 1,
                settings.packBlockAlign ? 1 : 0, settings.packBruteForce ? 1 : 0, settings.packRotate ? 1 : 0, settings.packRotate ? 1 : 0,
            };
            IntPtr handle = IntPtr.Zero;
            try {
                int code = meshLabUnwrap(Pack(input.positions), (uint)input.positions.Length, input.indices, (uint)input.indices.Length,
                    angle ? settings.normalCrease * Mathf.Deg2Rad : Mathf.PI, settings.normalSmoothing,
                    options, (uint)options.Length, out handle, out uint vertexCount, out uint indexCount, out uint charts);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new InvalidOperationException("UV unwrap failed: " + Error(code));
                var data = new float[checked((int)vertexCount * 8)];
                var result = new Geometry { positions = new Vector3[vertexCount], normals = new Vector3[vertexCount],
                    uv = new Vector2[vertexCount], indices = new int[indexCount], charts = new int[vertexCount], chartCount = (int)charts };
                if (meshLabUnwrapCopy(handle, data, vertexCount, result.indices, indexCount, result.charts) != 0)
                    throw new InvalidOperationException("UV unwrap output copy failed.");
                for (int i = 0; i < vertexCount; ++i) {
                    result.positions[i] = new Vector3(data[i * 8], data[i * 8 + 1], data[i * 8 + 2]);
                    result.normals[i] = new Vector3(data[i * 8 + 3], data[i * 8 + 4], data[i * 8 + 5]);
                    result.uv[i] = new Vector2(data[i * 8 + 6], data[i * 8 + 7]);
                }
                if (settings.hardEdges == RemeshHardEdges.UvIslands || settings.hardEdges == RemeshHardEdges.UvIslandsAndAngle)
                    SmoothWithinSplitVertices(result);
                return result;
            }
            finally { if (handle != IntPtr.Zero) meshLabRemeshDestroy(handle); }
        }

        // xatlas splits vertices along every chart border (and the native normals split
        // them along crease edges). Averaging face normals per output vertex therefore
        // smooths inside each island and leaves its border hard.
        internal static void SmoothWithinSplitVertices(Geometry geometry)
        {
            var sum = new Vector3[geometry.positions.Length];
            var p = geometry.positions;
            for (int i = 0; i < geometry.indices.Length; i += 3) {
                int a = geometry.indices[i], b = geometry.indices[i + 1], c = geometry.indices[i + 2];
                Vector3 n = Vector3.Cross(p[b] - p[a], p[c] - p[a]); // area weighted
                sum[a] += n; sum[b] += n; sum[c] += n;
            }
            for (int i = 0; i < sum.Length; ++i)
                if (sum[i].sqrMagnitude > 1e-30f) geometry.normals[i] = sum[i].normalized;
        }

        static float[] Pack(Vector3[] positions)
        {
            var packed = new float[checked(positions.Length * 3)];
            for (int i = 0; i < positions.Length; ++i) {
                packed[i * 3] = positions[i].x; packed[i * 3 + 1] = positions[i].y; packed[i * 3 + 2] = positions[i].z;
            }
            return packed;
        }

        static IndexedMesh CopyMesh(IntPtr handle, uint vertexCount, uint indexCount)
        {
            var data = new float[checked((int)vertexCount * 3)];
            var result = new IndexedMesh { positions = new Vector3[vertexCount], indices = new int[indexCount] };
            if (meshLabMeshCopy(handle, data, vertexCount, result.indices, indexCount) != 0)
                throw new InvalidOperationException("Remesh output copy failed.");
            for (int i = 0; i < vertexCount; ++i) result.positions[i] = new Vector3(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);
            return result;
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
