using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class RemeshNative
    {
        const string Library = "xatlas-unity";
        const int AbiVersion = 3;
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

        /// <summary>Unwrapped result: split vertices with normals, UV0, meshoptimizer tangents and xatlas chart ids.</summary>
        internal sealed class Geometry
        {
            public Vector3[] positions, normals;
            public Vector2[] uv;
            public Vector4[] tangents;
            public int[] indices, charts;
            public int chartCount;
        }

        public static void CheckAvailable()
        {
            try {
                int native = meshLabRemeshVersion();
                if (native != AbiVersion)
                    throw new InvalidOperationException("Unsupported remesh native ABI: expected " + AbiVersion + ", the loaded plugin reports " + native +
                        ". Update this package to the commit whose Build Native Libraries run finished, then restart Unity.");
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
                    angle ? settings.normalCrease * Mathf.Deg2Rad : Mathf.PI, 0f,
                    options, (uint)options.Length, out handle, out uint vertexCount, out uint indexCount, out uint charts);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new InvalidOperationException("UV unwrap failed: " + Error(code));
                // Vertex layout is sixteen float32: position, normal, UV0, tangent.
                var data = new float[checked((int)vertexCount * 16)];
                var result = new Geometry { positions = new Vector3[vertexCount], normals = new Vector3[vertexCount],
                    uv = new Vector2[vertexCount], tangents = new Vector4[vertexCount],
                    indices = new int[indexCount], charts = new int[vertexCount], chartCount = (int)charts };
                if (meshLabUnwrapCopy(handle, data, vertexCount, result.indices, indexCount, result.charts) != 0)
                    throw new InvalidOperationException("UV unwrap output copy failed.");
                for (int i = 0; i < vertexCount; ++i) {
                    result.positions[i] = new Vector3(data[i * 16], data[i * 16 + 1], data[i * 16 + 2]);
                    result.normals[i] = new Vector3(data[i * 16 + 3], data[i * 16 + 4], data[i * 16 + 5]);
                    result.uv[i] = new Vector2(data[i * 16 + 6], data[i * 16 + 7]);
                    result.tangents[i] = new Vector4(data[i * 16 + 8], data[i * 16 + 9], data[i * 16 + 10], data[i * 16 + 11]);
                }
                // Normals are regenerated from the split geometry after the UV cut, so
                // every hard-edge source behaves the same: crease splits and chart
                // borders are already vertex splits, no face crosses one, and the
                // weighting follows the Blender Weighted Normal analog modes. The
                // native meshopt normals are kept as a fallback: a vertex whose
                // accumulation degenerates restores them instead of casting zero
                // rays through the bake (they respect the same crease splits and
                // are smooth across chart borders — a soft edge beats a dead one).
                var nativeNormals = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; ++i) nativeNormals[i] = result.normals[i];
                GenerateSplitNormals(result, settings.normalWeighting);
                int healedNormals = 0;
                for (int i = 0; i < vertexCount; ++i)
                    if (result.normals[i].sqrMagnitude < 1e-12f) {
                        result.normals[i] = nativeNormals[i];
                        if (nativeNormals[i].sqrMagnitude > 1e-12f) ++healedNormals;
                    }
                int zeroNormals = 0;
                for (int i = 0; i < vertexCount; ++i)
                    if (result.normals[i].sqrMagnitude < 1e-12f) ++zeroNormals;
                if (healedNormals > 0)
                    UvtLog.Warn("[Remesh] " + healedNormals + " of " + vertexCount +
                        " split normals degenerated to zero (cancelling or degenerate faces); restored the native smooth normal on them — hard edges may soften there.");
                if (zeroNormals > 0)
                    UvtLog.Warn("[Remesh] " + zeroNormals + " of " + vertexCount +
                        " split normals are still zero (the native output was zero as well); their rays fall back to the welded cage.");
                // Normal smoothing runs after UV generation so it works the same for
                // every hard-edge source: crease splits and chart borders already
                // materialized as vertex splits, and mesh edges never cross a split,
                // so the pass stops at hard edges by construction.
                SmoothNormals(result, settings.normalSmoothing);
                return result;
            }
            finally { if (handle != IntPtr.Zero) meshLabRemeshDestroy(handle); }
        }

        // Regenerates vertex normals from the split geometry after the UV cut.
        // xatlas splits vertices along every chart border and the native normals
        // split them along crease edges, so accumulating face normals per output
        // vertex smooths inside every split group and leaves its border hard —
        // creases and island borders alike. Weighting follows the Blender
        // Weighted Normal analog: face area (meshopt's own accumulation),
        // corner angle, or both multiplied together.
        // The degeneracy gate is RELATIVE to the strongest accumulation on the
        // mesh: real captures are ~5 mm models whose raw crosses sit near 1e-7,
        // and an absolute floor there classified valid smooth vertices as
        // degenerate (the all-zero-normal regression on face-area weighting).
        internal static void GenerateSplitNormals(Geometry geometry, RemeshNormalWeighting weighting)
        {
            var sum = new Vector3[geometry.positions.Length];
            var p = geometry.positions;
            bool byArea = weighting != RemeshNormalWeighting.CornerAngle;
            bool byAngle = weighting != RemeshNormalWeighting.FaceArea;
            for (int i = 0; i < geometry.indices.Length; i += 3) {
                int a = geometry.indices[i], b = geometry.indices[i + 1], c = geometry.indices[i + 2];
                Vector3 ab = p[b] - p[a], ac = p[c] - p[a], bc = p[c] - p[b];
                Vector3 n = Vector3.Cross(ab, ac); // |n| = 2 * face area
                if (!byAngle) { sum[a] += n; sum[b] += n; sum[c] += n; continue; }
                Vector3 face = n.sqrMagnitude > 1e-30f ? n.normalized : Vector3.zero;
                float angleA = CornerAngle(ab, ac), angleB = CornerAngle(-ab, bc), angleC = CornerAngle(-ac, -bc);
                if (byArea) { sum[a] += n * angleA; sum[b] += n * angleB; sum[c] += n * angleC; }
                else { sum[a] += face * angleA; sum[b] += face * angleB; sum[c] += face * angleC; }
            }
            float maxSq = 0f;
            for (int i = 0; i < sum.Length; ++i) {
                float sq = sum[i].sqrMagnitude;
                if (sq > maxSq) maxSq = sq;
            }
            float floor = maxSq * 1e-12f;
            for (int i = 0; i < sum.Length; ++i)
                if (sum[i].sqrMagnitude > floor) geometry.normals[i] = sum[i].normalized;
        }

        // Angle between two edge directions meeting at a corner, in radians.
        static float CornerAngle(Vector3 u, Vector3 v)
        {
            float lengths = u.magnitude * v.magnitude;
            if (lengths < 1e-20f) return 0;
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(u, v) / lengths, -1f, 1f));
        }

        // Port of meshopt's generateNormals smoothing pass, run on the split unwrap
        // output where every vertex is its own normal group. Each pass averages the
        // alignment-weighted normal deltas across mesh edges (aligned neighbours pull
        // more, opposing ones not at all), so smoothing flows along the surface and
        // stops at every hard edge — crease splits and UV chart borders alike.
        internal static void SmoothNormals(Geometry geometry, float smoothing)
        {
            if (smoothing <= 0) return;
            int passes = Math.Min(10, (int)Math.Ceiling(smoothing));
            var normals = geometry.normals;
            var indices = geometry.indices;
            var delta = new Vector3[normals.Length];
            var edges = new float[normals.Length];
            for (int pass = 0; pass < passes; ++pass) {
                float alpha = 0.5f * Mathf.Min(1f, smoothing - pass);
                Array.Clear(delta, 0, delta.Length);
                Array.Clear(edges, 0, edges.Length);
                for (int i = 0; i < indices.Length; i += 3) {
                    SmoothEdge(normals, delta, edges, indices[i], indices[i + 1]);
                    SmoothEdge(normals, delta, edges, indices[i + 1], indices[i + 2]);
                    SmoothEdge(normals, delta, edges, indices[i + 2], indices[i]);
                }
                for (int i = 0; i < normals.Length; ++i) {
                    if (edges[i] <= 0) continue;
                    Vector3 n = normals[i] + delta[i] * (alpha / edges[i]);
                    float length = n.magnitude;
                    if (length > 1e-12f) normals[i] = n / length;
                }
            }
        }

        static void SmoothEdge(Vector3[] normals, Vector3[] delta, float[] edges, int a, int b)
        {
            float dp = Vector3.Dot(normals[a], normals[b]);
            float w = dp > 0f ? dp * dp : 0f;
            Vector3 d = (normals[b] - normals[a]) * w;
            delta[a] += d; edges[a] += 1f;
            delta[b] -= d; edges[b] += 1f;
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
