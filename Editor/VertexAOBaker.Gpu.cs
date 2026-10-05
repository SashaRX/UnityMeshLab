using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace SashaRX.UnityMeshLab
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
            => StartGPUBake(meshes, null, settings, onComplete, onError);

        internal static GpuAOBakeJob StartGPUBake(
            List<(Mesh mesh, Matrix4x4 transform)> targets,
            List<(Mesh mesh, Matrix4x4 transform)> occluders,
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
                job = new GpuAOBakeJob(computeShader, targets, occluders, settings, onComplete, onError);
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
            enum Phase { Dispatching, ReadingBack, Cancelling, Done, Cancelled }

            Phase phase = Phase.Done;
            readonly ComputeShader cs;
            readonly VertexAOSettings settings;
            readonly Action<Dictionary<Mesh, float[]>> onComplete;
            readonly Action<string> onError;

            // Kernels
            int bakeKernel, finalKernel;

            // Shared GPU buffers: the tree through GpuBvh, the directions here
            GpuBvh gpuBvh;
            ComputeBuffer dirBuf;

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
            AsyncGPUReadbackRequest cancellationBarrier;
            bool hasCancellationBarrier;

            // AsyncGPUReadback has no completion guarantee (device loss can
            // strand a request); polling phases time out instead of pinning
            // the job — and its buffers — forever.
            const double kReadbackTimeoutSec = 120.0;
            double stallStart;

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
            public bool IsRunning => phase == Phase.Dispatching ||
                                     phase == Phase.ReadingBack ||
                                     phase == Phase.Cancelling;

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
                        case Phase.Cancelling:
                            return "Cancelling...";
                        default:
                            return "";
                    }
                }
            }

            public GpuAOBakeJob(
                ComputeShader computeShader,
                List<(Mesh mesh, Matrix4x4 transform)> targets,
                List<(Mesh mesh, Matrix4x4 transform)> occluders,
                VertexAOSettings settings,
                Action<Dictionary<Mesh, float[]>> onComplete,
                Action<string> onError)
            {
                this.cs = computeShader;
                this.settings = settings;
                this.onComplete = onComplete;
                this.onError = onError;

                // Prepare can throw after buffers and readable copies were
                // already allocated (missing kernels, a rejected ComputeBuffer).
                // The job never reaches Start()/Tick() then, so nothing else
                // would release them — clean up here instead of leaking.
                try { Prepare(targets, occluders); }
                catch { Cleanup(); throw; }
            }

            void Prepare(
                List<(Mesh mesh, Matrix4x4 transform)> targets,
                List<(Mesh mesh, Matrix4x4 transform)> occluders)
            {
                // Filter out zero-vertex meshes — ComputeBuffer construction
                // rejects count==0, so one empty target would poison the batch.
                if (targets != null)
                    targets = targets.FindAll(t => t.mesh != null && t.mesh.vertexCount > 0);
                if (occluders != null)
                    occluders = occluders.FindAll(t => t.mesh != null && t.mesh.vertexCount > 0);

                if (targets == null || targets.Count == 0)
                    throw new Exception("GPU bake requires at least one target mesh.");

                // Kernel indices alone do not prove support on the active API.
                // Reject this backend before uploading geometry; the tool can
                // then fall back to CPU on devices with tighter GPU limits.
                if (!cs.HasKernel("BakeAO") || !cs.HasKernel("FinalizeAO"))
                    throw new InvalidOperationException("Vertex AO kernels are missing or unsupported.");
                bakeKernel = cs.FindKernel("BakeAO");
                finalKernel = cs.FindKernel("FinalizeAO");
                if (!cs.IsSupported(bakeKernel) || !cs.IsSupported(finalKernel))
                    throw new InvalidOperationException("Vertex AO kernels are unsupported or failed to compile.");

                bool isThickness = settings.bakeType == AOBakeType.Thickness;

                // Build combined BVH from all meshes
                var allVerts = new List<Vector3>();
                var allTris = new List<int>();
                AppendGeometryBuffers(targets, allVerts, allTris, readableCopies);
                AppendGeometryBuffers(occluders, allVerts, allTris, readableCopies);
                if (allVerts.Count == 0 || allTris.Count == 0)
                    throw new Exception("GPU bake did not receive any readable geometry.");

                var bvh = new TriangleBvh(allVerts.ToArray(), allTris.ToArray());

                var directions = MeshGeometry.SphereDirections(settings.sampleCount);
                dirCount = directions.Length;

                // Auto batch size: larger BVH → smaller batches to avoid TDR
                int totalTris = allTris.Count / 3;
                dirBatchSize = Mathf.Clamp(500000 / Mathf.Max(totalTris, 1), 8, 64);
                totalBatches = Mathf.CeilToInt((float)dirCount / dirBatchSize);

                // Precompute face normals
                int faceCount = totalTris;
                var allVertsArr = allVerts.ToArray();
                var allTrisArr = allTris.ToArray();
                var faceNormals = MeshGeometry.FaceNormals(allVertsArr, allTrisArr);

                Bounds combinedBounds = ComputeCombinedBounds(targets);
                float extent = Mathf.Max(combinedBounds.extents.magnitude, 0.0001f);
                float normalOffset = 0.001f * extent;
                float minHitDist = 0.003f * extent;
                float maxDist = settings.maxRadius > 0 ? settings.maxRadius : float.MaxValue;
                float groundY = settings.groundPlane
                    ? combinedBounds.min.y - settings.groundOffset
                    : float.NegativeInfinity;

                // Upload the tree once (GpuBvh owns the buffers) and the directions
                gpuBvh = GpuBvh.TryCreate(bvh, faceNormals);
                if (gpuBvh == null) throw new InvalidOperationException("GPU BVH unavailable (compute shaders unsupported or BvhQueries.compute missing).");
                dirBuf = new ComputeBuffer(directions.Length, 12);
                dirBuf.SetData(directions);

                // Per-mesh vertex buffers
                slots = new MeshSlot[targets.Count];
                for (int i = 0; i < targets.Count; i++)
                {
                    var (mesh, xform) = targets[i];
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

                // Bind shared state to the validated kernels.
                gpuBvh.Bind(cs, bakeKernel);
                cs.SetBuffer(bakeKernel, "_Directions", dirBuf);

                cs.SetInt("_DirectionCount", dirCount);
                cs.SetFloat("_MaxDist", maxDist);
                cs.SetFloat("_NormalOffset", normalOffset);
                cs.SetFloat("_MinHitDist", minHitDist);
                cs.SetFloat("_CosineWeighted", settings.cosineWeighted ? 1f : 0f);
                cs.SetFloat("_BinaryHit", settings.binaryHit ? 1f : 0f);
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

                if (phase == Phase.Cancelling)
                    return;

                // Do not release buffers while previously queued dispatches or readbacks
                // may still reference them. A readback queued after the dispatches acts as
                // a completion barrier; existing final readbacks provide the same guarantee.
                if (phase == Phase.Dispatching && completedDispatches > 0)
                {
                    int barrierMesh = Mathf.Clamp(curMesh, 0, slots.Length - 1);
                    cancellationBarrier = AsyncGPUReadback.Request(slots[barrierMesh].counterBuf);
                    hasCancellationBarrier = true;
                }

                phase = Phase.Cancelling;
                stallStart = EditorApplication.timeSinceStartup;
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
                        case Phase.Cancelling:
                            TickCancelling();
                            break;
                        default:
                            EditorApplication.update -= Tick;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    UvtLog.Error($"[Vertex AO] GPU bake error: {ex.Message}");
                    bool cancelled = phase == Phase.Cancelling;
                    phase = cancelled ? Phase.Cancelled : Phase.Done;
                    Cleanup();
                    if (!cancelled) onError?.Invoke(ex.Message);
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

                stallStart = EditorApplication.timeSinceStartup;
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
                if (!allDone)
                {
                    if (EditorApplication.timeSinceStartup - stallStart > kReadbackTimeoutSec)
                        FailStalled("result readback");
                    return;
                }

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

            void TickCancelling()
            {
                if (hasCancellationBarrier && !cancellationBarrier.done)
                {
                    if (EditorApplication.timeSinceStartup - stallStart > kReadbackTimeoutSec)
                        FailStalled("cancellation barrier readback");
                    return;
                }

                if (readbackRequests != null)
                {
                    for (int i = 0; i < readbackRequests.Length; i++)
                    {
                        if (!readbackRequests[i].done)
                        {
                            if (EditorApplication.timeSinceStartup - stallStart > kReadbackTimeoutSec)
                                FailStalled("final readback during cancel");
                            return;
                        }
                    }
                }

                phase = Phase.Cancelled;
                Cleanup();
            }

            void FailStalled(string what)
            {
                UvtLog.Error($"[Vertex AO] GPU bake stalled: {what} did not complete " +
                             $"within {kReadbackTimeoutSec:F0}s — releasing buffers.");
                bool cancelled = phase == Phase.Cancelling;
                phase = cancelled ? Phase.Cancelled : Phase.Done;
                Cleanup();
                if (!cancelled) onError?.Invoke($"GPU {what} timed out.");
            }

            void Cleanup()
            {
                EditorApplication.update -= Tick;

                gpuBvh?.Dispose(); gpuBvh = null;
                dirBuf?.Dispose(); dirBuf = null;

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

        static ComputeShader FindComputeShader(string name) => ComputeShaders.Find(name);
    }
}
