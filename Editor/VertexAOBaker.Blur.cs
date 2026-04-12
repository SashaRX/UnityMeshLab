using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace LightmapUvTool
{
    public static partial class VertexAOBaker
    {
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
    }
}
