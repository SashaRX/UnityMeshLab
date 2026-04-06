// VertexAOBaker.cs — GPU vertex AO baking via depth-map hemisphere sampling.
// Renders mesh from N hemisphere directions, accumulates occlusion per vertex via compute shader.

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
    }

    public static class VertexAOBaker
    {
        // ── Public API ──

        public static Dictionary<Mesh, float[]> BakeMultiMesh(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
        {
            if (meshes == null || meshes.Count == 0)
                return new Dictionary<Mesh, float[]>();

            if (!SystemInfo.supportsComputeShaders)
            {
                UvtLog.Warn("[Vertex AO] Compute shaders not supported (GPU: " + SystemInfo.graphicsDeviceType + "). Switch to DX11/DX12/Vulkan/Metal for GPU bake.");
                return BakeMultiMeshCPU(meshes, settings);
            }

            return BakeMultiMeshGPU(meshes, settings);
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

        public static float[] BlurAO(float[] ao, int[] triangles, int vertexCount, int iterations, float strength)
        {
            if (ao == null || iterations <= 0) return ao;

            // Build adjacency: vertex → set of neighbor vertices
            var neighbors = new List<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                neighbors[i] = new List<int>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                AddNeighbor(neighbors, a, b);
                AddNeighbor(neighbors, a, c);
                AddNeighbor(neighbors, b, c);
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
                    int occluded = 0, sampled = 0;

                    for (int d = 0; d < directions.Length; d++)
                    {
                        if (Vector3.Dot(directions[d], faceNorm) <= 0) continue;
                        sampled++;
                        var hit = bvh.Raycast(origin, directions[d], maxDist);
                        if (hit.triangleIndex >= 0) { occluded++; continue; }
                        if (settings.groundPlane && directions[d].y < -0.001f)
                        {
                            float tt = (groundY - origin.y) / directions[d].y;
                            if (tt > 0 && tt < maxDist) occluded++;
                        }
                    }

                    if (sampled > 0)
                    {
                        surfaceAO += 1f - (float)occluded / sampled;
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

        // ── GPU Path ──

        static Dictionary<Mesh, float[]> BakeMultiMeshGPU(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
        {
            var result = new Dictionary<Mesh, float[]>();

            // Load shaders
            var depthShader = Shader.Find("Hidden/LightmapUvTool/VertexAODepth");
            var computeShader = (ComputeShader)EditorGUIUtility.Load("VertexAOAccum.compute");
            if (computeShader == null)
                computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    FindComputeShaderPath("VertexAOAccum"));

            if (depthShader == null || computeShader == null)
            {
                UvtLog.Error("[Vertex AO] Cannot find shaders. Falling back to CPU.");
                return BakeMultiMeshCPU(meshes, settings);
            }

            var depthMat = new Material(depthShader) { hideFlags = HideFlags.HideAndDontSave };

            // Compute combined bounds
            Bounds combinedBounds = ComputeCombinedBounds(meshes);
            if (settings.groundPlane)
            {
                float extend = Mathf.Max(combinedBounds.size.x, combinedBounds.size.z) * 2.5f;
                var groundMin = new Vector3(
                    combinedBounds.center.x - extend,
                    combinedBounds.min.y - settings.groundOffset,
                    combinedBounds.center.z - extend);
                var groundMax = new Vector3(
                    combinedBounds.center.x + extend,
                    combinedBounds.min.y - settings.groundOffset,
                    combinedBounds.center.z + extend);
                combinedBounds.Encapsulate(groundMin);
                combinedBounds.Encapsulate(groundMax);
            }

            float extent = combinedBounds.extents.magnitude;
            if (settings.maxRadius > 0 && settings.maxRadius < extent)
                extent = settings.maxRadius;
            Vector3 center = combinedBounds.center;
            float bias = 0.001f * extent;
            float normalOffset = 0.0005f * extent;

            // Create render texture
            int res = settings.depthResolution;
            var rt = new RenderTexture(res, res, 24, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();

            // Create ground plane mesh
            Mesh groundQuad = null;
            Matrix4x4 groundMatrix = Matrix4x4.identity;
            if (settings.groundPlane)
            {
                groundQuad = CreateGroundQuad(combinedBounds, settings.groundOffset);
                groundMatrix = Matrix4x4.identity;
            }

            // Per-mesh GPU buffers
            var meshBuffers = new List<(Mesh mesh, ComputeBuffer pos, ComputeBuffer norm, ComputeBuffer counters, int vertCount)>();
            foreach (var (mesh, xform) in meshes)
            {
                var verts = mesh.vertices;
                var norms = mesh.normals;
                if (norms == null || norms.Length != verts.Length)
                {
                    var tmp = UnityEngine.Object.Instantiate(mesh);
                    tmp.RecalculateNormals();
                    norms = tmp.normals;
                    UnityEngine.Object.DestroyImmediate(tmp);
                }

                // Transform to world space
                var worldVerts = new Vector3[verts.Length];
                var worldNorms = new Vector3[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    worldVerts[i] = xform.MultiplyPoint3x4(verts[i]);
                    worldNorms[i] = xform.MultiplyVector(norms[i]).normalized;
                }

                var posBuf = new ComputeBuffer(verts.Length, 12);
                posBuf.SetData(worldVerts);
                var normBuf = new ComputeBuffer(verts.Length, 12);
                normBuf.SetData(worldNorms);
                var counterBuf = new ComputeBuffer(verts.Length, 8);
                counterBuf.SetData(new uint[verts.Length * 2]); // zero init

                meshBuffers.Add((mesh, posBuf, normBuf, counterBuf, verts.Length));
            }

            // Generate hemisphere directions
            var directions = GenerateHemisphereDirections(settings.sampleCount);

            // Kernel indices
            int accumKernel = computeShader.FindKernel("AccumulateAO");
            int finalKernel = computeShader.FindKernel("FinalizeAO");

            var cmd = new CommandBuffer { name = "VertexAO_Depth" };

            try
            {
                // Main loop: render + accumulate per direction
                for (int d = 0; d < directions.Length; d++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Baking Vertex AO",
                        $"Sample {d + 1}/{directions.Length}", (float)d / directions.Length))
                        break;

                    Vector3 dir = directions[d];

                    // Build ortho view/proj matrices
                    Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.99f
                        ? Vector3.forward : Vector3.up;
                    Matrix4x4 view = Matrix4x4.LookAt(center - dir * extent, center, up);
                    // LookAt returns camera-to-world, we need world-to-camera
                    view = view.inverse;
                    Matrix4x4 proj = Matrix4x4.Ortho(-extent, extent, -extent, extent, 0, 2 * extent);
                    Matrix4x4 vp = proj * view;

                    // Render depth pass
                    cmd.Clear();
                    cmd.SetRenderTarget(rt);
                    cmd.ClearRenderTarget(true, true, Color.white, 1f);
                    cmd.SetViewProjectionMatrices(view, proj);
                    foreach (var (mesh, xform) in meshes)
                    {
                        for (int sub = 0; sub < mesh.subMeshCount; sub++)
                            cmd.DrawMesh(mesh, xform, depthMat, sub);
                    }
                    if (groundQuad != null)
                        cmd.DrawMesh(groundQuad, groundMatrix, depthMat);
                    Graphics.ExecuteCommandBuffer(cmd);

                    // Dispatch compute per mesh
                    computeShader.SetTexture(accumKernel, "_DepthTex", rt);
                    computeShader.SetMatrix("_VP", vp);
                    computeShader.SetVector("_SampleDir", dir);
                    computeShader.SetFloat("_DepthBias", bias);
                    computeShader.SetFloat("_NormalOffset", normalOffset);
                    computeShader.SetInt("_DepthTexSize", res);

                    foreach (var (mesh, posBuf, normBuf, counterBuf, vertCount) in meshBuffers)
                    {
                        computeShader.SetInt("_VertexCount", vertCount);
                        computeShader.SetBuffer(accumKernel, "_Positions", posBuf);
                        computeShader.SetBuffer(accumKernel, "_Normals", normBuf);
                        computeShader.SetBuffer(accumKernel, "_AOCounters", counterBuf);
                        computeShader.Dispatch(accumKernel, Mathf.CeilToInt(vertCount / 64f), 1, 1);
                    }
                }

                // Finalize: compute final AO per mesh
                foreach (var (mesh, posBuf, normBuf, counterBuf, vertCount) in meshBuffers)
                {
                    var aoResultBuf = new ComputeBuffer(vertCount, 4);
                    computeShader.SetInt("_VertexCount", vertCount);
                    computeShader.SetFloat("_Intensity", settings.intensity);
                    computeShader.SetBuffer(finalKernel, "_AOCounters", counterBuf);
                    computeShader.SetBuffer(finalKernel, "_AOResult", aoResultBuf);
                    computeShader.Dispatch(finalKernel, Mathf.CeilToInt(vertCount / 64f), 1, 1);

                    var aoData = new float[vertCount];
                    aoResultBuf.GetData(aoData);
                    aoResultBuf.Dispose();

                    result[mesh] = aoData;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                cmd.Dispose();
                foreach (var (_, posBuf, normBuf, counterBuf, _) in meshBuffers)
                {
                    posBuf.Dispose();
                    normBuf.Dispose();
                    counterBuf.Dispose();
                }
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(depthMat);
                if (groundQuad != null) UnityEngine.Object.DestroyImmediate(groundQuad);
            }

            return result;
        }

        // ── CPU Fallback ──

        static Dictionary<Mesh, float[]> BakeMultiMeshCPU(
            List<(Mesh mesh, Matrix4x4 transform)> meshes,
            VertexAOSettings settings)
        {
            UvtLog.Info("[Vertex AO] Using CPU fallback (no compute shader support).");
            var result = new Dictionary<Mesh, float[]>();

            // Build combined BVH from all meshes
            var allVerts = new List<Vector3>();
            var allTris = new List<int>();
            foreach (var (mesh, xform) in meshes)
            {
                int baseVert = allVerts.Count;
                var verts = mesh.vertices;
                for (int i = 0; i < verts.Length; i++)
                    allVerts.Add(xform.MultiplyPoint3x4(verts[i]));
                var tris = mesh.triangles;
                for (int i = 0; i < tris.Length; i++)
                    allTris.Add(tris[i] + baseVert);
            }

            var bvh = new TriangleBvh(allVerts.ToArray(), allTris.ToArray());
            var directions = GenerateHemisphereDirections(settings.sampleCount);

            Bounds combinedBounds = ComputeCombinedBounds(meshes);
            float extent = combinedBounds.extents.magnitude;
            float normalOffset = 0.0005f * extent;
            float groundY = settings.groundPlane
                ? combinedBounds.min.y - settings.groundOffset
                : float.NegativeInfinity;

            int totalVerts = 0;
            foreach (var (mesh, _) in meshes) totalVerts += mesh.vertexCount;
            int processed = 0;

            foreach (var (mesh, xform) in meshes)
            {
                var verts = mesh.vertices;
                var norms = mesh.normals;
                if (norms == null || norms.Length != verts.Length)
                {
                    var tmp = UnityEngine.Object.Instantiate(mesh);
                    tmp.RecalculateNormals();
                    norms = tmp.normals;
                    UnityEngine.Object.DestroyImmediate(tmp);
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

                Parallel.For(0, verts.Length, (v, loopState) =>
                {
                    if (cancelled) { loopState.Stop(); return; }

                    Vector3 origin = worldPos[v] + worldNorm[v] * normalOffset;

                    int occluded = 0, sampled = 0;
                    for (int d = 0; d < directions.Length; d++)
                    {
                        if (Vector3.Dot(directions[d], worldNorm[v]) <= 0) continue;
                        sampled++;

                        var hit = bvh.Raycast(origin, directions[d], maxDist);
                        if (hit.triangleIndex >= 0) { occluded++; continue; }

                        if (settings.groundPlane && directions[d].y < -0.001f)
                        {
                            float t = (groundY - origin.y) / directions[d].y;
                            if (t > 0 && t < maxDist) { occluded++; }
                        }
                    }

                    float aoVal = sampled > 0 ? 1f - (float)occluded / sampled : 1f;
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

                // Face-area correction: fix large polygons where all vertices
                // are in occlusion but the surface itself is mostly open.
                if (settings.faceAreaCorrection)
                    ao = FaceAreaCorrection(ao, mesh, xform, bvh, directions, maxDist,
                        normalOffset, settings, groundY);

                result[mesh] = ao;
            }

            EditorUtility.ClearProgressBar();
            return result;
        }

        // ── Helpers ──

        static Vector3[] GenerateHemisphereDirections(int count)
        {
            var dirs = new Vector3[count];
            float goldenRatio = (1f + Mathf.Sqrt(5f)) / 2f;
            for (int i = 0; i < count; i++)
            {
                float theta = Mathf.Acos(1f - (i + 0.5f) / count);
                float phi = 2f * Mathf.PI * i / goldenRatio;
                dirs[i] = new Vector3(
                    Mathf.Sin(theta) * Mathf.Cos(phi),
                    Mathf.Cos(theta),
                    Mathf.Sin(theta) * Mathf.Sin(phi));
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

        static string FindComputeShaderPath(string name)
        {
            var guids = AssetDatabase.FindAssets($"t:ComputeShader {name}");
            return guids.Length > 0 ? AssetDatabase.GUIDToAssetPath(guids[0]) : null;
        }
    }
}
