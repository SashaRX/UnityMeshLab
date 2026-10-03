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
            public Vector3[] normals;
            public Vector2[] uv;
            public bool draftUv; // temporary planar UV0, not a completed unwrap
            public int[] indices;
            public int TriangleCount => indices.Length / 3;

            internal IndexedMesh PrepareChannels(CancellationToken token = default)
            {
                normals = MeshGeometry.AveragedNormals(positions, indices, token);
                if (uv == null || uv.Length != positions.Length) {
                    uv = MeshGeometry.NormalizedXYUv(positions, token);
                    draftUv = true;
                }
                return this;
            }
        }

        /// <summary>Unwrapped result: split vertices with normals, UV0, meshoptimizer tangents and xatlas chart ids.</summary>
        internal sealed class Geometry
        {
            public Vector3[] positions, normals;
            public Vector2[] uv;
            public Vector4[] tangents;
            public int[] indices, charts;
            public int chartCount;
            public bool draftUv;
            public int originalChartCount, originalSmallChartCount, smallChartCount;
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
                int code = meshLabVoxelRemesh(MeshSimplifier.PackPositions(positions), (uint)positions.Length, indices, (uint)indices.Length,
                    settings.voxelResolution, (settings.solve ? 1u : 0u) | (settings.shell ? 2u : 0u),
                    out handle, out uint vertexCount, out uint indexCount);
                token.ThrowIfCancellationRequested();
                if (code != 0) throw new InvalidOperationException("Voxel remesh failed: " + Error(code));
                return CopyMesh(handle, vertexCount, indexCount).PrepareChannels(token);
            }
            finally { if (handle != IntPtr.Zero) MeshSimplifier.meshLabMeshDestroy(handle); }
        }

        public static IndexedMesh Simplify(IndexedMesh input, RemeshSettings settings, CancellationToken token, out float error)
        {
            settings.Validate();
            var options = new MeshSimplifier.GeometrySettings {
                targetTriangles = settings.targetTriangles,
                maximumError = settings.maximumError,
                flags = (settings.regularize == RemeshRegularize.Light ? 1u : 0u) |
                    (settings.regularize == RemeshRegularize.Strong ? 2u : 0u) |
                    (settings.preserveFolds ? 4u : 0u) | (settings.pruneSmallParts ? 16u : 0u)
            };
            var result = MeshSimplifier.SimplifyGeometry(input.positions, input.indices, options, token, out error);
            return new IndexedMesh { positions = result.positions, indices = result.indices }.PrepareChannels(token);
        }

        public static Geometry Unwrap(IndexedMesh input, RemeshSettings settings, CancellationToken token)
        {
            settings.Validate();
            token.ThrowIfCancellationRequested();
            // Matches ParseUnwrapOptions in Native~/src/remesh.cpp.
            float[] options = {
                settings.maxChartArea, settings.maxChartBoundary, settings.chartNormalDeviation, settings.chartRoundness,
                settings.chartStraightness, settings.chartNormalSeam, 0.5f, settings.chartMaxCost, settings.chartIterations,
                settings.textureResolution, settings.padding, 0, 1,
                settings.packBlockAlign ? 1 : 0, settings.packBruteForce ? 1 : 0, settings.packRotate ? 1 : 0, settings.packRotate ? 1 : 0,
            };
            var result = UnwrapWithOptions(input, options, token);
            var original = UvChartQuality.Measure(result, token);
            var best = original;
            if (settings.reduceUvFragmentation && result.chartCount > 1) {
                // More iterations or a larger maxCost alone can create MORE slivers
                // after parameterization. Compare actual output instead. Keep the
                // user's chart-size limits and packing in every trial. Final normal
                // settings never change the charting input.
                foreach (float straightness in new[] { 6f, 10f }) {
                    var trial = (float[])options.Clone();
                    trial[2] = 2; trial[3] = .01f; trial[4] = straightness;
                    // 1000 is xatlas's explicit seam constraint, not a preference.
                    trial[5] = options[5] >= 1000 ? options[5] : 4;
                    trial[7] = 5; trial[8] = 1;
                    bool same = true;
                    for (int i = 0; i < options.Length; ++i) if (!trial[i].Equals(options[i])) { same = false; break; }
                    if (same) continue;
                    token.ThrowIfCancellationRequested();
                    Geometry candidate;
                    try { candidate = UnwrapWithOptions(input, trial, token); }
                    catch (InvalidOperationException error) {
                        UvtLog.Warn("[Remesh] UV fragmentation alternative failed; keeping the current unwrap. " + error.Message);
                        continue;
                    }
                    var quality = UvChartQuality.Measure(candidate, token);
                    if (!quality.Improves(best, original)) continue;
                    result = candidate; best = quality;
                }
            }
            result.originalChartCount = original.charts;
            result.originalSmallChartCount = original.smallCharts;
            result.smallChartCount = best.smallCharts;
            RemeshNormals.ApplyFinal(result, settings, token);
            return result;
        }

        static Geometry UnwrapWithOptions(IndexedMesh input, float[] options, CancellationToken token)
        {
            IntPtr handle = IntPtr.Zero;
            try {
                int code = meshLabUnwrap(MeshSimplifier.PackPositions(input.positions), (uint)input.positions.Length, input.indices, (uint)input.indices.Length,
                    Mathf.PI, 0f,
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
                // Native charting always uses averaged normals (crease PI).
                // Final normal weighting, crease splits and smoothing run once,
                // after the best UV result has been selected.
                return result;
            }
            finally { if (handle != IntPtr.Zero) meshLabRemeshDestroy(handle); }
        }

        // Regenerates vertex normals from the split geometry after the UV cut.
        // xatlas splits vertices along chart borders. RemeshNormals adds crease
        // splits only AFTER charting, then supplies the corner-fan normal groups.
        // Weighting follows the Blender
        // Weighted Normal analog: face area (meshopt's own accumulation),
        // corner angle, or both multiplied together.
        // The degeneracy gate is RELATIVE to the strongest accumulation on the
        // mesh: real captures are ~5 mm models whose raw crosses sit near 1e-7,
        // and an absolute floor there classified valid smooth vertices as
        // degenerate (the all-zero-normal regression on face-area weighting).
        // Every output vertex is its own group: hard across every split (chart borders
        // and creases alike) — the UV-island hard-edge modes.
        internal static void GenerateSplitNormals(Geometry geometry, RemeshNormalWeighting weighting)
            => GenerateSplitNormals(geometry, weighting, null);

        // With smoothAcross given (the native averaged normals), vertices that xatlas
        // duplicated along chart borders are accumulated together again: copies at one
        // position whose native normals agree share one normal, so Smooth stays
        // smooth across islands. Angle uses explicit corner-fan groups instead.
        // Returns the per-vertex normal group (null when every vertex is its own).
        internal static int[] GenerateSplitNormals(Geometry geometry, RemeshNormalWeighting weighting, Vector3[] smoothAcross)
        {
            var p = geometry.positions;
            int[] group = null;
            int groups = p.Length;
            if (smoothAcross != null) {
                group = new int[p.Length];
                var map = new System.Collections.Generic.Dictionary<(int, int, int, int, int, int), int>(p.Length);
                groups = 0;
                for (int i = 0; i < p.Length; ++i) {
                    var n = smoothAcross[i];
                    var key = (BitConverter.SingleToInt32Bits(p[i].x), BitConverter.SingleToInt32Bits(p[i].y), BitConverter.SingleToInt32Bits(p[i].z),
                        Mathf.RoundToInt(n.x * 1024f), Mathf.RoundToInt(n.y * 1024f), Mathf.RoundToInt(n.z * 1024f));
                    if (!map.TryGetValue(key, out int g)) { g = groups++; map[key] = g; }
                    group[i] = g;
                }
            }
            GenerateGroupedNormals(geometry, weighting, group);
            return group;
        }

        internal static void GenerateGroupedNormals(Geometry geometry, RemeshNormalWeighting weighting, int[] group)
        {
            var p = geometry.positions;
            int groups = p.Length;
            if (group != null) {
                groups = 0;
                foreach (int g in group) groups = Math.Max(groups, g + 1);
            }
            int G(int v) => group == null ? v : group[v];
            var sum = new Vector3[groups];
            var strongestFace = new Vector3[groups];
            void KeepFace(int g, Vector3 face)
            {
                if (face.sqrMagnitude > strongestFace[g].sqrMagnitude) strongestFace[g] = face;
            }
            bool byArea = weighting != RemeshNormalWeighting.CornerAngle;
            bool byAngle = weighting != RemeshNormalWeighting.FaceArea;
            for (int i = 0; i < geometry.indices.Length; i += 3) {
                int a = geometry.indices[i], b = geometry.indices[i + 1], c = geometry.indices[i + 2];
                int ga = G(a), gb = G(b), gc = G(c);
                Vector3 ab = p[b] - p[a], ac = p[c] - p[a], bc = p[c] - p[b];
                Vector3 n = Vector3.Cross(ab, ac); // |n| = 2 * face area
                KeepFace(ga, n); KeepFace(gb, n); KeepFace(gc, n);
                if (!byAngle) { sum[ga] += n; sum[gb] += n; sum[gc] += n; continue; }
                Vector3 face = n.sqrMagnitude > 1e-30f ? MeshGeometry.UnitDirection(n) : Vector3.zero;
                float angleA = CornerAngle(ab, ac), angleB = CornerAngle(-ab, bc), angleC = CornerAngle(-ac, -bc);
                if (byArea) { sum[ga] += n * angleA; sum[gb] += n * angleB; sum[gc] += n * angleC; }
                else { sum[ga] += face * angleA; sum[gb] += face * angleB; sum[gc] += face * angleC; }
            }
            float maxSq = 0f;
            for (int i = 0; i < sum.Length; ++i) {
                float sq = sum[i].sqrMagnitude;
                if (sq > maxSq) maxSq = sq;
            }
            float floor = maxSq * 1e-12f;
            for (int i = 0; i < p.Length; ++i) {
                int g = G(i);
                if (sum[g].sqrMagnitude > floor) geometry.normals[i] = MeshGeometry.UnitDirection(sum[g]);
                // Preserve a usable native normal when the weighted sum cancels.
                // If that is also zero, use the largest incident face of this split
                // group. A valid sliver must not inherit zero merely because another
                // face on the mesh is much larger. Truly degenerate faces stay zero.
                else if (geometry.normals[i].sqrMagnitude < 1e-12f)
                    geometry.normals[i] = MeshGeometry.UnitDirection(strongestFace[g]);
            }
        }

        // Gram-Schmidt every tangent against the final vertex normal, keeping its
        // handedness. Missing/parallel tangents need the same perpendicular fallback
        // as the bake's Basis(), so the saved mesh and baked map use the same frame.
        internal static void OrthogonalizeTangents(Geometry geometry)
        {
            if (geometry.tangents == null) return;
            for (int i = 0; i < geometry.tangents.Length; ++i) {
                var t4 = geometry.tangents[i];
                var n = geometry.normals[i];
                var t = new Vector3(t4.x, t4.y, t4.z);
                t -= n * Vector3.Dot(t, n);
                if (t.sqrMagnitude < 1e-12f) {
                    if (n.sqrMagnitude < 1e-12f) continue;
                    t = Vector3.Cross(n, Mathf.Abs(n.y) < .9f ? Vector3.up : Vector3.right);
                }
                t = MeshGeometry.UnitDirection(t);
                geometry.tangents[i] = new Vector4(t.x, t.y, t.z, t4.w < 0 ? -1 : 1);
            }
        }

        // Angle between two edge directions meeting at a corner, in radians.
        static float CornerAngle(Vector3 u, Vector3 v)
        {
            return MeshGeometry.CornerAngle(u, v);
        }

        // Port of meshopt's generateNormals smoothing pass, run on the split unwrap
        // output where every vertex is its own normal group. Each pass averages the
        // alignment-weighted normal deltas across mesh edges (aligned neighbours pull
        // more, opposing ones not at all), so smoothing flows along the surface and
        // stops at every hard edge — crease splits and UV chart borders alike.
        internal static void SmoothNormals(Geometry geometry, float smoothing)
            => SmoothNormals(geometry, smoothing, null);

        // With a normal group per vertex (see GenerateSplitNormals) the pass runs over
        // the group-welded connectivity, so it flows through chart borders that are not
        // hard edges instead of letting the copies drift apart, and every member of a
        // group ends with the same normal.
        internal static void SmoothNormals(Geometry geometry, float smoothing, int[] group)
        {
            if (smoothing <= 0) return;
            if (group != null) {
                int groups = 0;
                foreach (int g in group) groups = Math.Max(groups, g + 1);
                var welded = new Geometry { normals = new Vector3[groups], indices = new int[geometry.indices.Length] };
                for (int i = 0; i < group.Length; ++i) welded.normals[group[i]] = geometry.normals[i];
                for (int i = 0; i < geometry.indices.Length; ++i) welded.indices[i] = group[geometry.indices[i]];
                SmoothNormals(welded, smoothing, null);
                for (int i = 0; i < group.Length; ++i) geometry.normals[i] = welded.normals[group[i]];
                return;
            }
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

        static IndexedMesh CopyMesh(IntPtr handle, uint vertexCount, uint indexCount)
        {
            var result = MeshSimplifier.CopyGeometry(handle, vertexCount, indexCount);
            return new IndexedMesh { positions = result.positions, indices = result.indices };
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
