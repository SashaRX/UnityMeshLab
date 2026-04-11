// VertexAOBaker.cs — GPU vertex AO baking via BVH ray tracing compute shader.
// Supports async non-blocking GPU path (GpuAOBakeJob) and synchronous CPU fallback.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace LightmapUvTool
{
    public enum AOTargetChannel
    {
        VertexColorR, VertexColorG, VertexColorB, VertexColorA,
        UV0_X, UV0_Y, UV1_X, UV1_Y, UV2_X, UV2_Y,
        UV3_X, UV3_Y, UV4_X, UV4_Y
    }

    public enum AOBakeType
    {
        AmbientOcclusion,
        Thickness
    }

    [Serializable]
    public class VertexAOSettings
    {
        public int sampleCount    = 256;
        public int depthResolution = 512;
        public float maxRadius    = 10f;
        public float intensity    = 1.0f;
        public bool groundPlane   = true;
        public float groundOffset = 0.01f;
        public bool faceAreaCorrection = false;
        public bool backfaceCulling = true;
        public bool useGPU        = true;
        public bool cosineWeighted = true;
        public AOBakeType bakeType = AOBakeType.AmbientOcclusion;
    }

    public static class VertexAOBaker
    {
        // ── Public API ──

        /// <summary>
        /// Synchronous CPU bake. For GPU path, use StartGPUBake() instead.
        /// </summary>
        public static Dictionary<Mesh, float[]> BakeMultiMesh(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
        {
            if (meshes == null || meshes.Count == 0)
                return new Dictionary<Mesh, float[]>();

            return BakeMultiMeshCPU(meshes, settings);
        }

        /// <summary>
        /// Apply face-area correction as a post-pass on already-baked AO values.
        /// Builds a BVH once and corrects dark vertices on large triangles.
        /// </summary>
        public static Dictionary<Mesh, float[]> ApplyFaceAreaCorrection(
            Dictionary<Mesh, float[]> rawAO,
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
        {
            if (rawAO == null || rawAO.Count == 0)
                return rawAO;

            // Build combined BVH
            var allVerts = new List<Vector3>();
            var allTris = new List<int>();
            var copies = new List<Mesh>();
            foreach (var (mesh, xform) in meshes)
            {
                var readable = EnsureReadable(mesh);
                if (readable != mesh) copies.Add(readable);
                int baseVert = allVerts.Count;
                var verts = readable.vertices;
                for (int i = 0; i < verts.Length; i++)
                    allVerts.Add(xform.MultiplyPoint3x4(verts[i]));
                var tris = readable.triangles;
                for (int i = 0; i < tris.Length; i++)
                    allTris.Add(tris[i] + baseVert);
            }
            var bvh = new TriangleBvh(allVerts.ToArray(), allTris.ToArray());
            var directions = GenerateSphereDirections(settings.sampleCount);

            Bounds combinedBounds = ComputeCombinedBounds(meshes);
            float extent = combinedBounds.extents.magnitude;
            float normalOffset = 0.001f * extent;
            float maxDist = settings.maxRadius > 0 ? settings.maxRadius : float.MaxValue;
            float groundY = settings.groundPlane
                ? combinedBounds.min.y - settings.groundOffset
                : float.NegativeInfinity;

            var result = new Dictionary<Mesh, float[]>();
            foreach (var (mesh, xform) in meshes)
            {
                if (!rawAO.ContainsKey(mesh))
                    continue;
                result[mesh] = FaceAreaCorrection(rawAO[mesh], mesh, xform, bvh,
                    directions, maxDist, normalOffset, settings, groundY);
            }
            foreach (var c in copies)
                UnityEngine.Object.DestroyImmediate(c);
            return result;
        }

        public static void WriteToChannel(Mesh mesh, float[] aoValues, AOTargetChannel channel)
        {
            if (mesh == null || aoValues == null || aoValues.Length != mesh.vertexCount) return;

            Undo.RecordObject(mesh, "Write AO Channel");

            int ch = (int)channel;
            if (ch <= (int)AOTargetChannel.VertexColorA)
            {
                // Vertex Color R/G/B/A
                var colors = mesh.colors32;
                if (colors == null || colors.Length != mesh.vertexCount)
                {
                    colors = new Color32[mesh.vertexCount];
                    for (int i = 0; i < colors.Length; i++)
                        colors[i] = new Color32(255, 255, 255, 255);
                }
                int comp = ch - (int)AOTargetChannel.VertexColorR; // 0=R,1=G,2=B,3=A
                for (int i = 0; i < aoValues.Length; i++)
                {
                    byte v = (byte)(Mathf.Clamp01(aoValues[i]) * 255f);
                    var c = colors[i];
                    if (comp == 0) c.r = v;
                    else if (comp == 1) c.g = v;
                    else if (comp == 2) c.b = v;
                    else c.a = v;
                    colors[i] = c;
                }
                mesh.colors32 = colors;
            }
            else
            {
                // UV channel X or Y
                int uvIdx = (ch - (int)AOTargetChannel.UV0_X) / 2;  // 0-4
                int comp  = (ch - (int)AOTargetChannel.UV0_X) % 2;  // 0=X, 1=Y
                var uvs = new List<Vector2>();
                mesh.GetUVs(uvIdx, uvs);
                if (uvs.Count != mesh.vertexCount)
                {
                    uvs.Clear();
                    for (int i = 0; i < mesh.vertexCount; i++)
                        uvs.Add(Vector2.zero);
                }
                for (int i = 0; i < aoValues.Length; i++)
                {
                    var uv = uvs[i];
                    if (comp == 0) uv.x = aoValues[i];
                    else uv.y = aoValues[i];
                    uvs[i] = uv;
                }
                mesh.SetUVs(uvIdx, uvs);
            }
        }

        public static float[] BlurAO(float[] ao, int[] triangles, int vertexCount, int iterations, float strength,
            Vector3[] positions = null, Vector3[] normals = null, Vector2[] uv0 = null,
            bool crossHardEdges = true, bool crossUvSeams = true)
        {
            if (ao == null || iterations <= 0) return ao;

            // Build adjacency: vertex → set of neighbor vertices
            var neighbors = new List<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                neighbors[i] = new List<int>();

            // Triangle-based adjacency
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                AddNeighbor(neighbors, a, b);
                AddNeighbor(neighbors, a, c);
                AddNeighbor(neighbors, b, c);
            }

            // Connect position-duplicate vertices across seams
            if (positions != null && positions.Length == vertexCount && (crossHardEdges || crossUvSeams))
            {
                const float posEps = 1e-4f;    // position match tolerance (FBX floats)
                const float posEpsSq = posEps * posEps;
                const float normThresh = 0.9f;  // cos ~26°
                const float uvEps = 1e-4f;
                float cellSize = posEps * 10f;  // grid cell larger than epsilon

                var posMap = new Dictionary<long, List<int>>();
                for (int i = 0; i < vertexCount; i++)
                {
                    // Use RoundToInt for stable bucketing at cell boundaries
                    int cx = Mathf.RoundToInt(positions[i].x / cellSize);
                    int cy = Mathf.RoundToInt(positions[i].y / cellSize);
                    int cz = Mathf.RoundToInt(positions[i].z / cellSize);
                    long key = ((long)cx * 73856093L) ^ ((long)cy * 19349663L) ^ ((long)cz * 83492791L);
                    if (!posMap.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        posMap[key] = list;
                    }
                    list.Add(i);
                }

                // Check each vertex against same cell + 26 neighbors
                var processed = new HashSet<long>();
                foreach (var kvp in posMap)
                {
                    var group = kvp.Value;
                    // Match within same cell
                    for (int i = 0; i < group.Count; i++)
                        for (int j = i + 1; j < group.Count; j++)
                            TryConnectSeamVerts(neighbors, positions, normals, uv0,
                                group[i], group[j], posEpsSq, normThresh, uvEps,
                                crossHardEdges, crossUvSeams);
                }

                // Also check across adjacent cells
                var keys = new List<long>(posMap.Keys);
                foreach (var key in keys)
                {
                    var group = posMap[key];
                    // Reconstruct cell coords from first vertex
                    var p0 = positions[group[0]];
                    int bx = Mathf.RoundToInt(p0.x / cellSize);
                    int by = Mathf.RoundToInt(p0.y / cellSize);
                    int bz = Mathf.RoundToInt(p0.z / cellSize);

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        long nkey = ((long)(bx+dx) * 73856093L) ^ ((long)(by+dy) * 19349663L) ^ ((long)(bz+dz) * 83492791L);
                        if (!posMap.TryGetValue(nkey, out var ngroup)) continue;

                        foreach (int vi in group)
                            foreach (int vj in ngroup)
                                TryConnectSeamVerts(neighbors, positions, normals, uv0,
                                    vi, vj, posEpsSq, normThresh, uvEps,
                                    crossHardEdges, crossUvSeams);
                    }
                }
            }

            float[] src = (float[])ao.Clone();
            float[] dst = new float[vertexCount];

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    var nb = neighbors[v];
                    if (nb.Count == 0) { dst[v] = src[v]; continue; }
                    float avg = 0;
                    for (int n = 0; n < nb.Count; n++)
                        avg += src[nb[n]];
                    avg /= nb.Count;
                    dst[v] = Mathf.Lerp(src[v], avg, strength);
                }
                var tmp = src; src = dst; dst = tmp;
            }
            return src;
        }

        /// <summary>
        /// 3D spatial blur — ignores mesh topology entirely.
        /// Each vertex averages AO of all vertices within radius, across any mesh/seam/edge.
        /// </summary>
        public static float[] BlurAO3D(float[] ao, Vector3[] positions, int iterations, float strength, float radius)
        {
            if (ao == null || positions == null || iterations <= 0 || radius <= 0) return ao;
            int count = ao.Length;

            // Build spatial grid for fast neighbor lookup
            float cellSize = radius;
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < count; i++)
            {
                long key = SpatialKey(positions[i], cellSize);
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    grid[key] = list;
                }
                list.Add(i);
            }

            float radiusSq = radius * radius;
            float[] src = (float[])ao.Clone();
            float[] dst = new float[count];

            for (int iter = 0; iter < iterations; iter++)
            {
                Parallel.For(0, count, v =>
                {
                    Vector3 p = positions[v];
                    int cx = Mathf.FloorToInt(p.x / cellSize);
                    int cy = Mathf.FloorToInt(p.y / cellSize);
                    int cz = Mathf.FloorToInt(p.z / cellSize);

                    float weightSum = 1f; // self weight
                    float aoSum = src[v];

                    // 27-cell neighborhood
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        long nkey = PackKey(cx + dx, cy + dy, cz + dz);
                        if (!grid.TryGetValue(nkey, out var cell)) continue;
                        for (int ci = 0; ci < cell.Count; ci++)
                        {
                            int ni = cell[ci];
                            if (ni == v) continue;
                            float distSq = (positions[ni] - p).sqrMagnitude;
                            if (distSq >= radiusSq) continue;

                            // Linear falloff: closer = more weight
                            float w = 1f - Mathf.Sqrt(distSq) / radius;
                            weightSum += w;
                            aoSum += src[ni] * w;
                        }
                    }

                    float avg = aoSum / weightSum;
                    dst[v] = Mathf.Lerp(src[v], avg, strength);
                });

                var tmp = src; src = dst; dst = tmp;
            }
            return src;
        }

        static long SpatialKey(Vector3 p, float cellSize)
        {
            return PackKey(
                Mathf.FloorToInt(p.x / cellSize),
                Mathf.FloorToInt(p.y / cellSize),
                Mathf.FloorToInt(p.z / cellSize));
        }

        static long PackKey(int x, int y, int z)
        {
            return ((long)x * 73856093L) ^ ((long)y * 19349663L) ^ ((long)z * 83492791L);
        }

        static void TryConnectSeamVerts(List<int>[] neighbors,
            Vector3[] positions, Vector3[] normals, Vector2[] uv0,
            int vi, int vj, float posEpsSq, float normThresh, float uvEps,
            bool crossHardEdges, bool crossUvSeams)
        {
            if ((positions[vi] - positions[vj]).sqrMagnitude > posEpsSq) return;

            bool normalsMatch = normals == null ||
                Vector3.Dot(normals[vi], normals[vj]) >= normThresh;
            bool uvsMatch = uv0 == null ||
                (uv0[vi] - uv0[vj]).sqrMagnitude < uvEps * uvEps;

            if ((normalsMatch || crossHardEdges) && (uvsMatch || crossUvSeams))
                AddNeighbor(neighbors, vi, vj);
        }

        static void AddNeighbor(List<int>[] neighbors, int a, int b)
        {
            if (!neighbors[a].Contains(b)) neighbors[a].Add(b);
            if (!neighbors[b].Contains(a)) neighbors[b].Add(a);
        }

        // ── Face-area AO correction ──
        // Large polygons where all vertices sit in occluded corners get fully
        // black even though most of the surface is unoccluded.  This post-pass
        // shoots rays from triangle centroids and interior sample points.
        // If the centroid AO is significantly brighter than vertex AO, vertices
        // are pushed towards the centroid value, weighted by triangle area.

        public static float[] FaceAreaCorrection(
            float[] ao, Mesh mesh, Matrix4x4 xform,
            TriangleBvh bvh, Vector3[] directions, float maxDist,
            float normalOffset, VertexAOSettings settings, float groundY)
        {
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tris  = mesh.triangles;
            int vertCount = verts.Length;

            // Per-vertex correction accumulator (weighted sum) — use double for atomic add
            var correction  = new double[vertCount];
            var totalWeight = new double[vertCount];

            // Median area to define "large" triangle threshold
            float medianArea = ComputeMedianTriArea(verts, tris, xform);
            float largeThreshold = medianArea * 4f;

            int triCount = tris.Length / 3;
            Parallel.For(0, triCount, ti =>
            {
                int t = ti * 3;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                Vector3 p0 = xform.MultiplyPoint3x4(verts[i0]);
                Vector3 p1 = xform.MultiplyPoint3x4(verts[i1]);
                Vector3 p2 = xform.MultiplyPoint3x4(verts[i2]);

                float area = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
                if (area < largeThreshold) return;

                Vector3 faceNorm = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                if (faceNorm.sqrMagnitude < 0.001f) return;

                // Sample points inside triangle: centroid + 3 edge midpoints
                var samplePoints = new Vector3[]
                {
                    (p0 + p1 + p2) / 3f,
                    (p0 + p1) * 0.5f,
                    (p1 + p2) * 0.5f,
                    (p2 + p0) * 0.5f
                };

                float surfaceAO = 0;
                int surfaceSamples = 0;

                foreach (var pt in samplePoints)
                {
                    Vector3 origin = pt + faceNorm * normalOffset;
                    float occW = 0f, totW = 0f;

                    for (int d = 0; d < directions.Length; d++)
                    {
                        float ndot = Vector3.Dot(directions[d], faceNorm);
                        if (ndot <= 0) continue;
                        totW += ndot;
                        var hit = bvh.Raycast(origin, directions[d], maxDist);
                        if (hit.triangleIndex >= 0) { occW += ndot; continue; }
                        if (settings.groundPlane && directions[d].y < -0.001f)
                        {
                            float tt = (groundY - origin.y) / directions[d].y;
                            if (tt > 0 && tt < maxDist) occW += ndot;
                        }
                    }

                    if (totW > 0)
                    {
                        surfaceAO += 1f - occW / totW;
                        surfaceSamples++;
                    }
                }

                if (surfaceSamples == 0) return;
                surfaceAO /= surfaceSamples;
                surfaceAO = Mathf.Pow(Mathf.Clamp01(surfaceAO), settings.intensity);

                float vertexAvgAO = (ao[i0] + ao[i1] + ao[i2]) / 3f;

                // Only fix anomalies: skip if average vertex AO is reasonable (normal gradient)
                if (vertexAvgAO > 0.15f) return;

                // Surface must be significantly brighter than the dark vertices
                if (surfaceAO <= vertexAvgAO + 0.1f) return;

                double weight = area / medianArea;
                double sao = surfaceAO;

                // Only correct vertices that are very dark (< 0.2)
                int[] faceVerts = { i0, i1, i2 };
                foreach (int vi in faceVerts)
                {
                    if (ao[vi] >= 0.2f) continue; // normal AO, don't touch
                    InterlockedAddDouble(ref correction[vi], sao * weight);
                    InterlockedAddDouble(ref totalWeight[vi], weight);
                }
            });

            // Apply corrections
            var correctedAO = (float[])ao.Clone();
            int correctedCount = 0;
            for (int v = 0; v < vertCount; v++)
            {
                if (totalWeight[v] <= 0) continue;
                float targetAO = (float)(correction[v] / totalWeight[v]);
                // Only brighten, never darken
                if (targetAO > correctedAO[v])
                {
                    float blend = Mathf.Clamp01((float)(totalWeight[v] / (totalWeight[v] + 1.0)));
                    correctedAO[v] = Mathf.Lerp(correctedAO[v], targetAO, blend);
                    correctedCount++;
                }
            }

            if (correctedCount > 0)
                UvtLog.Info($"[Vertex AO] Face-area correction: {correctedCount} vertices adjusted.");

            return correctedAO;
        }

        static void InterlockedAddDouble(ref double location, double value)
        {
            double initial, computed;
            do
            {
                initial = location;
                computed = initial + value;
            }
            while (initial != Interlocked.CompareExchange(ref location, computed, initial));
        }

        static float ComputeMedianTriArea(Vector3[] verts, int[] tris, Matrix4x4 xform)
        {
            int triCount = tris.Length / 3;
            if (triCount == 0) return 1f;
            var areas = new float[triCount];
            for (int t = 0; t < triCount; t++)
            {
                Vector3 p0 = xform.MultiplyPoint3x4(verts[tris[t * 3]]);
                Vector3 p1 = xform.MultiplyPoint3x4(verts[tris[t * 3 + 1]]);
                Vector3 p2 = xform.MultiplyPoint3x4(verts[tris[t * 3 + 2]]);
                areas[t] = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
            }
            System.Array.Sort(areas);
            return areas[triCount / 2];
        }

        // ── GPU Path (Async Compute Ray Tracing) ──

        /// <summary>
        /// Start a non-blocking GPU AO bake. Returns a GpuAOBakeJob that drives itself
        /// via EditorApplication.update. Returns null if the compute shader is missing.
        /// </summary>
        internal static GpuAOBakeJob StartGPUBake(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings,
            Action<Dictionary<Mesh, float[]>> onComplete,
            Action<string> onError)
        {
            var computeShader = FindComputeShader("VertexAORayTrace");
            if (computeShader == null)
            {
                onError?.Invoke("Cannot find VertexAORayTrace compute shader.");
                return null;
            }

            var job = new GpuAOBakeJob(computeShader, meshes, settings, onComplete, onError);
            job.Start();
            return job;
        }

        /// <summary>
        /// Non-blocking GPU AO bake driven by EditorApplication.update.
        /// Splits direction sampling into batches to avoid TDR, uses AsyncGPUReadback.
        /// </summary>
        internal class GpuAOBakeJob
        {
            enum Phase { Dispatching, ReadingBack, Done, Cancelled }

            Phase phase = Phase.Done;
            readonly ComputeShader cs;
            readonly VertexAOSettings settings;
            readonly Action<Dictionary<Mesh, float[]>> onComplete;
            readonly Action<string> onError;

            // Kernels
            int bakeKernel, finalKernel;

            // Shared GPU buffers
            ComputeBuffer bvhNodeBuf, triVertBuf, triIdxBuf, trisBuf, faceNormBuf, dirBuf;

            // Per-mesh data
            struct MeshSlot
            {
                public Mesh mesh;
                public ComputeBuffer posBuf, normBuf, counterBuf, resultBuf;
                public int vertCount;
            }
            MeshSlot[] slots;

            // Readback requests (one per mesh)
            AsyncGPUReadbackRequest[] readbackRequests;

            // Direction batching
            int dirCount;
            int dirBatchSize;
            int totalBatches;  // per mesh

            // Progress tracking
            int curMesh;
            int curBatch;
            int totalDispatches;
            int completedDispatches;

            // Cleanup
            List<Mesh> readableCopies = new List<Mesh>();

            /// <summary>Bake progress 0..1 for UI display.</summary>
            public float Progress =>
                totalDispatches > 0 ? (float)completedDispatches / totalDispatches : 0f;

            /// <summary>True while the job is actively running.</summary>
            public bool IsRunning => phase == Phase.Dispatching || phase == Phase.ReadingBack;

            public string StatusText
            {
                get
                {
                    switch (phase)
                    {
                        case Phase.Dispatching:
                            return $"Baking mesh {curMesh + 1}/{slots.Length}, " +
                                   $"batch {curBatch + 1}/{totalBatches}";
                        case Phase.ReadingBack:
                            return "Reading back results...";
                        default:
                            return "";
                    }
                }
            }

            public GpuAOBakeJob(
                ComputeShader computeShader,
                List<(Mesh mesh, Matrix4x4 transform)> meshes,
                VertexAOSettings settings,
                Action<Dictionary<Mesh, float[]>> onComplete,
                Action<string> onError)
            {
                this.cs = computeShader;
                this.settings = settings;
                this.onComplete = onComplete;
                this.onError = onError;

                Prepare(meshes);
            }

            void Prepare(List<(Mesh mesh, Matrix4x4 transform)> meshes)
            {
                bool isThickness = settings.bakeType == AOBakeType.Thickness;

                // Build combined BVH from all meshes
                var allVerts = new List<Vector3>();
                var allTris = new List<int>();
                foreach (var (mesh, xform) in meshes)
                {
                    var readable = EnsureReadable(mesh);
                    if (readable != mesh) readableCopies.Add(readable);
                    int baseVert = allVerts.Count;
                    var verts = readable.vertices;
                    for (int i = 0; i < verts.Length; i++)
                        allVerts.Add(xform.MultiplyPoint3x4(verts[i]));
                    var tris = readable.triangles;
                    for (int i = 0; i < tris.Length; i++)
                        allTris.Add(tris[i] + baseVert);
                }

                var bvh = new TriangleBvh(allVerts.ToArray(), allTris.ToArray());
                bvh.GetGPUData(out var gpuNodes, out var gpuTriIndices, out var gpuVerts, out var gpuTris);

                var directions = GenerateSphereDirections(settings.sampleCount);
                dirCount = directions.Length;

                // Auto batch size: larger BVH → smaller batches to avoid TDR
                int totalTris = allTris.Count / 3;
                dirBatchSize = Mathf.Clamp(500000 / Mathf.Max(totalTris, 1), 8, 64);
                totalBatches = Mathf.CeilToInt((float)dirCount / dirBatchSize);

                // Precompute face normals
                int faceCount = totalTris;
                var faceNormals = new Vector3[faceCount];
                var allVertsArr = allVerts.ToArray();
                var allTrisArr = allTris.ToArray();
                for (int f = 0; f < faceCount; f++)
                {
                    var a = allVertsArr[allTrisArr[f * 3]];
                    var b = allVertsArr[allTrisArr[f * 3 + 1]];
                    var c = allVertsArr[allTrisArr[f * 3 + 2]];
                    faceNormals[f] = Vector3.Cross(b - a, c - a).normalized;
                }

                Bounds combinedBounds = ComputeCombinedBounds(meshes);
                float extent = combinedBounds.extents.magnitude;
                float normalOffset = 0.001f * extent;
                float minHitDist = 0.003f * extent;
                float maxDist = settings.maxRadius > 0 ? settings.maxRadius : float.MaxValue;
                float groundY = settings.groundPlane
                    ? combinedBounds.min.y - settings.groundOffset
                    : float.NegativeInfinity;

                // Upload shared BVH data to GPU
                int nodeStride = System.Runtime.InteropServices.Marshal.SizeOf<TriangleBvh.GPUNode>();
                bvhNodeBuf = new ComputeBuffer(gpuNodes.Length, nodeStride);
                bvhNodeBuf.SetData(gpuNodes);
                triVertBuf = new ComputeBuffer(gpuVerts.Length, 12);
                triVertBuf.SetData(gpuVerts);
                triIdxBuf = new ComputeBuffer(gpuTriIndices.Length, 4);
                triIdxBuf.SetData(gpuTriIndices);
                trisBuf = new ComputeBuffer(gpuTris.Length, 4);
                trisBuf.SetData(gpuTris);
                faceNormBuf = new ComputeBuffer(faceNormals.Length, 12);
                faceNormBuf.SetData(faceNormals);
                dirBuf = new ComputeBuffer(directions.Length, 12);
                dirBuf.SetData(directions);

                // Per-mesh vertex buffers
                slots = new MeshSlot[meshes.Count];
                for (int i = 0; i < meshes.Count; i++)
                {
                    var (mesh, xform) = meshes[i];
                    var readable = EnsureReadable(mesh);
                    if (readable != mesh && !readableCopies.Contains(readable))
                        readableCopies.Add(readable);
                    var verts = readable.vertices;
                    var norms = readable.normals;
                    if (norms == null || norms.Length != verts.Length)
                    {
                        readable.RecalculateNormals();
                        norms = readable.normals;
                    }

                    var worldVerts = new Vector3[verts.Length];
                    var worldNorms = new Vector3[verts.Length];
                    for (int v = 0; v < verts.Length; v++)
                    {
                        worldVerts[v] = xform.MultiplyPoint3x4(verts[v]);
                        worldNorms[v] = xform.MultiplyVector(norms[v]).normalized;
                    }

                    slots[i].mesh = mesh;
                    slots[i].vertCount = verts.Length;
                    slots[i].posBuf = new ComputeBuffer(verts.Length, 12);
                    slots[i].posBuf.SetData(worldVerts);
                    slots[i].normBuf = new ComputeBuffer(verts.Length, 12);
                    slots[i].normBuf.SetData(worldNorms);
                    slots[i].counterBuf = new ComputeBuffer(verts.Length, 8);
                    slots[i].counterBuf.SetData(new uint[verts.Length * 2]);
                }

                // Find kernels and bind shared state
                bakeKernel = cs.FindKernel("BakeAO");
                finalKernel = cs.FindKernel("FinalizeAO");

                cs.SetBuffer(bakeKernel, "_BVHNodes", bvhNodeBuf);
                cs.SetBuffer(bakeKernel, "_TriVerts", triVertBuf);
                cs.SetBuffer(bakeKernel, "_TriIndices", triIdxBuf);
                cs.SetBuffer(bakeKernel, "_Tris", trisBuf);
                cs.SetBuffer(bakeKernel, "_FaceNormals", faceNormBuf);
                cs.SetBuffer(bakeKernel, "_Directions", dirBuf);

                cs.SetInt("_DirectionCount", dirCount);
                cs.SetFloat("_MaxDist", maxDist);
                cs.SetFloat("_NormalOffset", normalOffset);
                cs.SetFloat("_MinHitDist", minHitDist);
                cs.SetFloat("_CosineWeighted", settings.cosineWeighted ? 1f : 0f);
                cs.SetFloat("_FlipNormals", isThickness ? 1f : 0f);
                cs.SetFloat("_BackfaceCulling", settings.backfaceCulling ? 1f : 0f);
                cs.SetFloat("_GroundPlane", (settings.groundPlane && !isThickness) ? 1f : 0f);
                cs.SetFloat("_GroundY", groundY);

                totalDispatches = slots.Length * totalBatches;
            }

            public void Start()
            {
                phase = Phase.Dispatching;
                curMesh = 0;
                curBatch = 0;
                completedDispatches = 0;
                EditorApplication.update += Tick;
            }

            public void Cancel()
            {
                if (!IsRunning) return;
                phase = Phase.Cancelled;
                Cleanup();
            }

            void Tick()
            {
                try
                {
                    switch (phase)
                    {
                        case Phase.Dispatching:
                            TickDispatching();
                            break;
                        case Phase.ReadingBack:
                            TickReadback();
                            break;
                        default:
                            EditorApplication.update -= Tick;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    UvtLog.Error($"[Vertex AO] GPU bake error: {ex.Message}");
                    phase = Phase.Done;
                    Cleanup();
                    onError?.Invoke(ex.Message);
                }
            }

            void TickDispatching()
            {
                ref var slot = ref slots[curMesh];
                int dirStart = curBatch * dirBatchSize;
                int dirEnd = Mathf.Min(dirStart + dirBatchSize, dirCount);

                cs.SetInt("_VertexCount", slot.vertCount);
                cs.SetInt("_DirStart", dirStart);
                cs.SetInt("_DirEnd", dirEnd);
                cs.SetBuffer(bakeKernel, "_Positions", slot.posBuf);
                cs.SetBuffer(bakeKernel, "_Normals", slot.normBuf);
                cs.SetBuffer(bakeKernel, "_AOCounters", slot.counterBuf);
                cs.Dispatch(bakeKernel, Mathf.CeilToInt(slot.vertCount / 64f), 1, 1);

                completedDispatches++;
                curBatch++;

                if (curBatch >= totalBatches)
                {
                    curBatch = 0;
                    curMesh++;
                    if (curMesh >= slots.Length)
                        BeginFinalize();
                }
            }

            void BeginFinalize()
            {
                bool isThickness = settings.bakeType == AOBakeType.Thickness;

                // Dispatch FinalizeAO for all meshes and issue async readback
                readbackRequests = new AsyncGPUReadbackRequest[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    ref var slot = ref slots[i];
                    slot.resultBuf = new ComputeBuffer(slot.vertCount, 4);

                    cs.SetInt("_VertexCount", slot.vertCount);
                    cs.SetFloat("_Intensity", settings.intensity);
                    cs.SetFloat("_FlipNormals", isThickness ? 1f : 0f);
                    cs.SetBuffer(finalKernel, "_AOCounters", slot.counterBuf);
                    cs.SetBuffer(finalKernel, "_AOResult", slot.resultBuf);
                    cs.Dispatch(finalKernel, Mathf.CeilToInt(slot.vertCount / 64f), 1, 1);

                    readbackRequests[i] = AsyncGPUReadback.Request(slot.resultBuf);
                }

                phase = Phase.ReadingBack;
            }

            void TickReadback()
            {
                bool allDone = true;
                for (int i = 0; i < readbackRequests.Length; i++)
                {
                    if (!readbackRequests[i].done)
                    {
                        allDone = false;
                        break;
                    }
                }
                if (!allDone) return;

                // All readbacks complete — extract results
                var result = new Dictionary<Mesh, float[]>();
                bool hasError = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (readbackRequests[i].hasError)
                    {
                        hasError = true;
                        break;
                    }
                    var data = readbackRequests[i].GetData<float>();
                    var aoData = new float[slots[i].vertCount];
                    data.CopyTo(aoData);
                    result[slots[i].mesh] = aoData;
                }

                phase = Phase.Done;
                Cleanup();

                if (hasError)
                    onError?.Invoke("AsyncGPUReadback failed.");
                else
                    onComplete?.Invoke(result);
            }

            void Cleanup()
            {
                EditorApplication.update -= Tick;

                bvhNodeBuf?.Dispose();
                triVertBuf?.Dispose();
                triIdxBuf?.Dispose();
                trisBuf?.Dispose();
                faceNormBuf?.Dispose();
                dirBuf?.Dispose();
                bvhNodeBuf = triVertBuf = triIdxBuf = trisBuf = faceNormBuf = dirBuf = null;

                if (slots != null)
                {
                    for (int i = 0; i < slots.Length; i++)
                    {
                        slots[i].posBuf?.Dispose();
                        slots[i].normBuf?.Dispose();
                        slots[i].counterBuf?.Dispose();
                        slots[i].resultBuf?.Dispose();
                    }
                    slots = null;
                }

                foreach (var copy in readableCopies)
                    if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                readableCopies.Clear();
            }
        }

        // ── CPU Fallback ──

        static Dictionary<Mesh, float[]> BakeMultiMeshCPU(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
        {
            UvtLog.Info("[Vertex AO] Using CPU mode (BVH ray tracing).");
            var result = new Dictionary<Mesh, float[]>();

            // Build combined BVH from all meshes
            var allVerts = new List<Vector3>();
            var allTris = new List<int>();
            var cpuCopies = new List<Mesh>();
            foreach (var (mesh, xform) in meshes)
            {
                var readable = EnsureReadable(mesh);
                if (readable != mesh) cpuCopies.Add(readable);
                int baseVert = allVerts.Count;
                var verts = readable.vertices;
                for (int i = 0; i < verts.Length; i++)
                    allVerts.Add(xform.MultiplyPoint3x4(verts[i]));
                var tris = readable.triangles;
                for (int i = 0; i < tris.Length; i++)
                    allTris.Add(tris[i] + baseVert);
            }

            var allVertsArr = allVerts.ToArray();
            var allTrisArr = allTris.ToArray();
            var bvh = new TriangleBvh(allVertsArr, allTrisArr);
            var directions = GenerateSphereDirections(settings.sampleCount);

            // Precompute face normals for backface culling
            Vector3[] faceNormals = null;
            if (settings.backfaceCulling)
            {
                int faceCount = allTrisArr.Length / 3;
                faceNormals = new Vector3[faceCount];
                for (int f = 0; f < faceCount; f++)
                {
                    var a = allVertsArr[allTrisArr[f * 3]];
                    var b = allVertsArr[allTrisArr[f * 3 + 1]];
                    var c = allVertsArr[allTrisArr[f * 3 + 2]];
                    faceNormals[f] = Vector3.Cross(b - a, c - a).normalized;
                }
            }

            Bounds combinedBounds = ComputeCombinedBounds(meshes);
            float extent = combinedBounds.extents.magnitude;
            float normalOffset = 0.001f * extent;
            float minHitDist   = 0.003f * extent; // skip self-intersection on sharp edges
            float groundY = settings.groundPlane
                ? combinedBounds.min.y - settings.groundOffset
                : float.NegativeInfinity;

            int totalVerts = 0;
            foreach (var (mesh, _) in meshes) totalVerts += mesh.vertexCount;
            int processed = 0;

            foreach (var (mesh, xform) in meshes)
            {
                var readable = EnsureReadable(mesh);
                var verts = readable.vertices;
                var norms = readable.normals;
                if (norms == null || norms.Length != verts.Length)
                {
                    readable.RecalculateNormals();
                    norms = readable.normals;
                }

                var ao = new float[verts.Length];
                float maxDist = settings.maxRadius > 0 ? settings.maxRadius : float.MaxValue;

                // Transform verts/normals to world space once (main thread)
                var worldPos  = new Vector3[verts.Length];
                var worldNorm = new Vector3[verts.Length];
                for (int v = 0; v < verts.Length; v++)
                {
                    worldPos[v]  = xform.MultiplyPoint3x4(verts[v]);
                    worldNorm[v] = xform.MultiplyVector(norms[v]).normalized;
                }

                // Parallel bake — BVH is read-only, each vertex is independent
                int progressCounter = 0;
                bool cancelled = false;

                bool isThickness = settings.bakeType == AOBakeType.Thickness;

                Parallel.For(0, verts.Length, (v, loopState) =>
                {
                    if (cancelled) { loopState.Stop(); return; }

                    // Thickness: flip hemisphere — rays go into the mesh
                    Vector3 hemisphereNorm = isThickness ? -worldNorm[v] : worldNorm[v];
                    // Thickness: offset inward; AO: offset outward
                    Vector3 origin = worldPos[v] + hemisphereNorm * normalOffset;

                    // Per-vertex jitter: rotate all directions by a small random angle
                    // to break banding artifacts with low sample counts
                    float jitterAngle = (v * 2654435761u % 360) * Mathf.Deg2Rad;
                    float jCos = Mathf.Cos(jitterAngle), jSin = Mathf.Sin(jitterAngle);

                    bool cosW = settings.cosineWeighted;
                    float occludedWeight = 0f, totalWeight = 0f;
                    for (int d = 0; d < directions.Length; d++)
                    {
                        // Apply Y-axis rotation jitter
                        var dir = directions[d];
                        float rx = dir.x * jCos - dir.z * jSin;
                        float rz = dir.x * jSin + dir.z * jCos;
                        var jitteredDir = new Vector3(rx, dir.y, rz);

                        float ndot = Vector3.Dot(jitteredDir, hemisphereNorm);
                        if (ndot <= 0) continue;

                        // Cosine-weighted: rays near normal contribute more;
                        // uniform: all hemisphere directions contribute equally.
                        float weight = cosW ? ndot : 1f;
                        totalWeight += weight;

                        var hit = bvh.Raycast(origin, jitteredDir, maxDist);
                        if (hit.triangleIndex >= 0 && hit.t > minHitDist)
                        {
                            // Backface culling: skip if ray hit the back side of a triangle
                            // (skip for thickness — we want all inward hits)
                            if (!isThickness && faceNormals != null &&
                                Vector3.Dot(jitteredDir, faceNormals[hit.triangleIndex]) > 0)
                            {
                                // Hit backface — treat as no geometry (fall through to ground check)
                            }
                            else
                            {
                                // Distance falloff: closer hits occlude more
                                float falloff = 1f - hit.t / maxDist;
                                occludedWeight += weight * falloff;
                                continue;
                            }
                        }

                        // Ground plane only for AO, not thickness
                        if (!isThickness && settings.groundPlane && jitteredDir.y < -0.001f)
                        {
                            float t = (groundY - origin.y) / jitteredDir.y;
                            if (t > 0 && t < maxDist)
                            {
                                float falloff = 1f - t / maxDist;
                                occludedWeight += weight * falloff;
                            }
                        }
                    }

                    float aoVal = totalWeight > 0 ? 1f - occludedWeight / totalWeight : 1f;
                    // Thickness: invert so closer hits = brighter (1=thin, 0=thick)
                    if (isThickness) aoVal = 1f - aoVal;
                    ao[v] = Mathf.Pow(Mathf.Clamp01(aoVal), settings.intensity);

                    Interlocked.Increment(ref progressCounter);
                });

                // Progress bar on main thread (poll after parallel completes per mesh)
                if (EditorUtility.DisplayCancelableProgressBar("Baking Vertex AO (CPU, parallel)",
                    $"Mesh {processed + verts.Length}/{totalVerts} vertices", (float)(processed + verts.Length) / totalVerts))
                {
                    EditorUtility.ClearProgressBar();
                    result[mesh] = ao;
                    return result;
                }

                processed += verts.Length;

                if (readable != mesh) UnityEngine.Object.DestroyImmediate(readable);
                result[mesh] = ao;
            }

            foreach (var c in cpuCopies)
                UnityEngine.Object.DestroyImmediate(c);
            EditorUtility.ClearProgressBar();
            return result;
        }

        // ── Helpers ──

        /// <summary>
        /// Returns a readable copy of the mesh if needed.
        /// Caller must DestroyImmediate the copy when done (if copy != original).
        /// </summary>
        static Mesh EnsureReadable(Mesh mesh)
        {
            if (mesh.isReadable) return mesh;
            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.hideFlags = HideFlags.HideAndDontSave;
            return copy;
        }

        static Vector3[] GenerateSphereDirections(int count)
        {
            // Full sphere via golden spiral — per-vertex dot(dir, normal) filter
            // selects the correct hemisphere. This ensures all normal orientations
            // get equal sampling coverage (fixes black artifacts on sideways/downward faces).
            var dirs = new Vector3[count];
            float goldenRatio = (1f + Mathf.Sqrt(5f)) / 2f;
            for (int i = 0; i < count; i++)
            {
                float cosTheta = 1f - 2f * (i + 0.5f) / count; // -1 to +1 (full sphere)
                float sinTheta = Mathf.Sqrt(1f - cosTheta * cosTheta);
                float phi = 2f * Mathf.PI * i / goldenRatio;
                dirs[i] = new Vector3(
                    sinTheta * Mathf.Cos(phi),
                    cosTheta,
                    sinTheta * Mathf.Sin(phi));
            }
            return dirs;
        }

        static Bounds ComputeCombinedBounds(List<(Mesh mesh, Matrix4x4 transform)> meshes)
        {
            var bounds = new Bounds();
            bool first = true;
            foreach (var (mesh, xform) in meshes)
            {
                var mb = mesh.bounds;
                var corners = new Vector3[8];
                corners[0] = xform.MultiplyPoint3x4(new Vector3(mb.min.x, mb.min.y, mb.min.z));
                corners[1] = xform.MultiplyPoint3x4(new Vector3(mb.max.x, mb.min.y, mb.min.z));
                corners[2] = xform.MultiplyPoint3x4(new Vector3(mb.min.x, mb.max.y, mb.min.z));
                corners[3] = xform.MultiplyPoint3x4(new Vector3(mb.max.x, mb.max.y, mb.min.z));
                corners[4] = xform.MultiplyPoint3x4(new Vector3(mb.min.x, mb.min.y, mb.max.z));
                corners[5] = xform.MultiplyPoint3x4(new Vector3(mb.max.x, mb.min.y, mb.max.z));
                corners[6] = xform.MultiplyPoint3x4(new Vector3(mb.min.x, mb.max.y, mb.max.z));
                corners[7] = xform.MultiplyPoint3x4(new Vector3(mb.max.x, mb.max.y, mb.max.z));
                foreach (var c in corners)
                {
                    if (first) { bounds = new Bounds(c, Vector3.zero); first = false; }
                    else bounds.Encapsulate(c);
                }
            }
            return bounds;
        }

        static Mesh CreateGroundQuad(Bounds bounds, float offset)
        {
            float y = bounds.min.y - offset;
            float extend = Mathf.Max(bounds.size.x, bounds.size.z) * 2.5f;
            float cx = bounds.center.x, cz = bounds.center.z;

            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(new[]
            {
                new Vector3(cx - extend, y, cz - extend),
                new Vector3(cx + extend, y, cz - extend),
                new Vector3(cx + extend, y, cz + extend),
                new Vector3(cx - extend, y, cz + extend),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static ComputeShader FindComputeShader(string name)
        {
            // 1. EditorGUIUtility.Load (Editor Default Resources)
            var cs = (ComputeShader)EditorGUIUtility.Load(name + ".compute");
            if (cs != null) return cs;

            // 2. AssetDatabase search by type + name
            var guids = AssetDatabase.FindAssets($"t:ComputeShader {name}");
            foreach (var guid in guids)
            {
                cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guid));
                if (cs != null) return cs;
            }

            // 3. Resolve relative to this script (works for UPM packages)
            var scriptGuids = AssetDatabase.FindAssets("t:Script VertexAOBaker");
            foreach (var guid in scriptGuids)
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                string editorDir = System.IO.Path.GetDirectoryName(scriptPath);
                string packageRoot = System.IO.Path.GetDirectoryName(editorDir);
                string shaderPath = packageRoot + "/Shaders/" + name + ".compute";
                cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(shaderPath);
                if (cs != null) return cs;
            }

            return null;
        }
    }
}
