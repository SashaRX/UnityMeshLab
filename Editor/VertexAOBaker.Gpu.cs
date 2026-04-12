using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace LightmapUvTool
{
    public static partial class VertexAOBaker
    {
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

            GpuAOBakeJob job;
            try
            {
                job = new GpuAOBakeJob(computeShader, meshes, settings, onComplete, onError);
            }
            catch (Exception ex)
            {
                UvtLog.Error($"[Vertex AO] GPU bake setup failed: {ex.Message}");
                onError?.Invoke(ex.Message);
                return null;
            }
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

                if (bakeKernel < 0 || finalKernel < 0)
                {
                    throw new Exception(
                        "VertexAORayTrace.compute kernels missing " +
                        $"(BakeAO={bakeKernel}, FinalizeAO={finalKernel}). " +
                        "The compute shader likely failed to compile — check the " +
                        "Console for shader compilation errors.");
                }

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
