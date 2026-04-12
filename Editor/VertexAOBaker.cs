// VertexAOBaker.cs — Public API, types, and shared helpers.
// Partial classes: Blur (.Blur.cs), GPU (.Gpu.cs), CPU (.Cpu.cs).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
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

    public static partial class VertexAOBaker
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

        // ── Face-area AO correction ──

        static float[] FaceAreaCorrection(
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
