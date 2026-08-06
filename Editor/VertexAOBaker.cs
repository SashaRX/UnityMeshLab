// VertexAOBaker.cs — Public API, types, and shared helpers.
// Partial classes: Blur (.Blur.cs), GPU (.Gpu.cs), CPU (.Cpu.cs).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
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

    public enum VertexAOOccluderMode
    {
        SelfOnly,
        SameRootNearby
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
        public bool binaryHit     = false;
        public AOBakeType bakeType = AOBakeType.AmbientOcclusion;
        public VertexAOOccluderMode occluderMode = VertexAOOccluderMode.SameRootNearby;
        public float occluderRadiusMultiplier = 2.0f;
        public bool includeCollisionOccluders = false;
    }

    public static partial class VertexAOBaker
    {
        // Hard ceilings keep imported, attacker-controlled meshes from exhausting
        // editor or GPU resources before the bake's cancellation UI can run.
        const long MaxBakeVertices = 2_000_000;
        const long MaxBakeTriangles = 4_000_000;
        const long MaxBakeSubMeshes = 1_024;
        const long MaxBakeEstimatedBytes = 512L * 1024L * 1024L;
        const long MaxBakeWorkItems = 250_000_000;

        // ── Public API ──

        /// <summary>
        /// Synchronous CPU bake. For GPU path, use StartGPUBake() instead.
        /// </summary>
        public static Dictionary<Mesh, float[]> BakeMultiMesh(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
            => BakeMultiMesh(meshes, null, settings);

        /// <summary>
        /// Synchronous CPU bake for target meshes with additional occluder-only geometry.
        /// For GPU path, use StartGPUBake() instead.
        /// </summary>
        public static Dictionary<Mesh, float[]> BakeMultiMesh(
            List<(Mesh mesh, Matrix4x4 transform)> targets,
            List<(Mesh mesh, Matrix4x4 transform)> occluders,
            VertexAOSettings settings)
        {
            if (targets == null || targets.Count == 0)
                return new Dictionary<Mesh, float[]>();

            if (!TryValidateBakeBudget(targets, occluders, settings, false, out var error))
            {
                UvtLog.Error($"[Vertex AO] Bake refused: {error}");
                return new Dictionary<Mesh, float[]>();
            }

            return BakeMultiMeshCPU(targets, occluders, settings);
        }

        /// <summary>
        /// Apply face-area correction as a post-pass on already-baked AO values.
        /// Builds a BVH once and corrects dark vertices on large triangles.
        /// </summary>
        public static Dictionary<Mesh, float[]> ApplyFaceAreaCorrection(
            Dictionary<Mesh, float[]> rawAO,
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
            => ApplyFaceAreaCorrection(rawAO, meshes, null, settings);

        /// <summary>
        /// Apply face-area correction using the same occlusion context as the bake:
        /// target meshes receive the correction, while the BVH includes both targets
        /// and occluder-only geometry.
        /// </summary>
        public static Dictionary<Mesh, float[]> ApplyFaceAreaCorrection(
            Dictionary<Mesh, float[]> rawAO,
            List<(Mesh mesh, Matrix4x4 transform)> targets,
            List<(Mesh mesh, Matrix4x4 transform)> occluders,
            VertexAOSettings settings)
        {
            if (rawAO == null || rawAO.Count == 0)
                return rawAO;

            if (!TryValidateBakeBudget(targets, occluders, settings, true, out var error))
            {
                UvtLog.Error($"[Vertex AO] Face-area correction refused: {error}");
                return rawAO;
            }

            // Build combined BVH
            var allVerts = new List<Vector3>();
            var allTris = new List<int>();
            var copies = new List<Mesh>();
            AppendGeometryBuffers(targets, allVerts, allTris, copies);
            AppendGeometryBuffers(occluders, allVerts, allTris, copies);
            if (allVerts.Count == 0 || allTris.Count == 0)
                return rawAO;

            var bvh = new TriangleBvh(allVerts.ToArray(), allTris.ToArray());
            var directions = GenerateSphereDirections(settings.sampleCount);

            Bounds combinedBounds = ComputeCombinedBounds(targets);
            float extent = Mathf.Max(combinedBounds.extents.magnitude, 0.0001f);
            float normalOffset = 0.001f * extent;
            float maxDist = settings.maxRadius > 0 ? settings.maxRadius : float.MaxValue;
            float groundY = settings.groundPlane
                ? combinedBounds.min.y - settings.groundOffset
                : float.NegativeInfinity;

            var result = new Dictionary<Mesh, float[]>();
            foreach (var (mesh, xform) in targets)
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

        internal static bool TryValidateBakeBudget(
            List<(Mesh mesh, Matrix4x4 transform)> targets,
            List<(Mesh mesh, Matrix4x4 transform)> occluders,
            VertexAOSettings settings,
            bool includeFaceAreaCorrection,
            out string error)
        {
            long targetVertices = 0;
            long totalVertices = 0;
            long totalTriangles = 0;
            long totalSubMeshes = 0;

            if (!AccumulateMeshBudget(targets, true, ref targetVertices, ref totalVertices,
                    ref totalTriangles, ref totalSubMeshes, out error) ||
                !AccumulateMeshBudget(occluders, false, ref targetVertices, ref totalVertices,
                    ref totalTriangles, ref totalSubMeshes, out error))
                return false;

            int samples = settings != null ? settings.sampleCount : 0;
            if (samples <= 0)
            {
                error = "sample count must be greater than zero.";
                return false;
            }

            // Conservative lower bound covering combined CPU arrays/BVH storage and
            // per-target transformed arrays/results or equivalent GPU buffers.
            long estimatedBytes = SaturatingAdd(
                SaturatingMultiply(totalVertices, 64),
                SaturatingAdd(SaturatingMultiply(totalTriangles, 80),
                    SaturatingMultiply(targetVertices, 44)));
            long workItems = SaturatingMultiply(targetVertices, samples);
            if (includeFaceAreaCorrection)
                workItems = SaturatingAdd(workItems,
                    SaturatingMultiply(SaturatingMultiply(totalTriangles, samples), 4));

            if (totalVertices > MaxBakeVertices || totalTriangles > MaxBakeTriangles ||
                totalSubMeshes > MaxBakeSubMeshes || estimatedBytes > MaxBakeEstimatedBytes ||
                workItems > MaxBakeWorkItems)
            {
                error = $"resource budget exceeded (vertices {totalVertices:N0}/{MaxBakeVertices:N0}, " +
                        $"triangles {totalTriangles:N0}/{MaxBakeTriangles:N0}, " +
                        $"submeshes {totalSubMeshes:N0}/{MaxBakeSubMeshes:N0}, " +
                        $"estimated memory {estimatedBytes / (1024 * 1024):N0}/{MaxBakeEstimatedBytes / (1024 * 1024):N0} MiB, " +
                        $"work {workItems:N0}/{MaxBakeWorkItems:N0}). Reduce mesh complexity or sample count.";
                return false;
            }

            error = null;
            return true;
        }

        static bool AccumulateMeshBudget(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            bool targets,
            ref long targetVertices,
            ref long totalVertices,
            ref long totalTriangles,
            ref long totalSubMeshes,
            out string error)
        {
            error = null;
            if (meshes == null) return true;

            foreach (var (mesh, _) in meshes)
            {
                if (mesh == null) continue;
                long vertices = mesh.vertexCount;
                totalVertices = SaturatingAdd(totalVertices, vertices);
                if (targets) targetVertices = SaturatingAdd(targetVertices, vertices);
                totalSubMeshes = SaturatingAdd(totalSubMeshes, mesh.subMeshCount);
                if (totalSubMeshes > MaxBakeSubMeshes)
                {
                    error = $"mesh totals exceed the structural limits ({MaxBakeVertices:N0} vertices, " +
                            $"{MaxBakeTriangles:N0} triangles, {MaxBakeSubMeshes:N0} submeshes).";
                    return false;
                }

                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) == MeshTopology.Triangles)
                        totalTriangles = SaturatingAdd(totalTriangles, (long)mesh.GetIndexCount(sub) / 3);
                }

                // Stop inspecting submeshes as soon as a cheap structural ceiling is hit.
                if (totalVertices > MaxBakeVertices || totalTriangles > MaxBakeTriangles ||
                    totalSubMeshes > MaxBakeSubMeshes)
                {
                    error = $"mesh totals exceed the structural limits ({MaxBakeVertices:N0} vertices, " +
                            $"{MaxBakeTriangles:N0} triangles, {MaxBakeSubMeshes:N0} submeshes).";
                    return false;
                }
            }
            return true;
        }

        static long SaturatingMultiply(long a, long b)
        {
            if (a <= 0 || b <= 0) return 0;
            return a > long.MaxValue / b ? long.MaxValue : a * b;
        }

        static long SaturatingAdd(long a, long b)
            => a > long.MaxValue - b ? long.MaxValue : a + b;

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

        // ── Face-area AO correction ──

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
            Array.Sort(areas);
            return areas[triCount / 2];
        }

        // ── Shared Helpers ──

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

        static void AppendGeometryBuffers(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            List<Vector3> allVerts,
            List<int> allTris,
            List<Mesh> readableCopies)
        {
            if (meshes == null) return;

            foreach (var (mesh, xform) in meshes)
            {
                if (mesh == null) continue;
                var readable = EnsureReadable(mesh);
                if (readable != mesh && !readableCopies.Contains(readable))
                    readableCopies.Add(readable);

                int baseVert = allVerts.Count;
                var verts = readable.vertices;
                for (int i = 0; i < verts.Length; i++)
                    allVerts.Add(xform.MultiplyPoint3x4(verts[i]));

                var tris = readable.triangles;
                for (int i = 0; i < tris.Length; i++)
                    allTris.Add(tris[i] + baseVert);
            }
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
    }
}
