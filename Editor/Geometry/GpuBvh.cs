using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// A <see cref="TriangleBvh"/> on the GPU with the same queries: closest ray hit
    /// (optionally only faces that face the ray, either-side faces excepted) and
    /// nearest face within a radius (optionally only faces whose normal agrees with a
    /// query normal). Queries run in batches, one thread each, through
    /// <c>Shaders/BvhQueries.compute</c>; the hit records mean exactly what the CPU
    /// ones do (face index, t / distance², barycentric weights of vertices 0, 1, 2), so a
    /// caller can resolve a batch on either side without a code change. Main thread
    /// only. <see cref="Bind"/> lets another kernel (the AO bake) use the same uploaded
    /// tree through <c>BvhTraversal.hlsl</c> instead of uploading its own.
    /// </summary>
    internal sealed class GpuBvh : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RayHit { public int tri; public float t, u, v; }                        // 16 bytes
        [StructLayout(LayoutKind.Sequential)]
        public struct NearestHit { public int tri; public float distSq; public Vector3 point, bary; } // 32 bytes

        public const int MaxBatch = 1 << 20;
        // Keep readbacks bounded while amortizing Editor-frame completion latency.
        // A full async chunk occupies 7 MiB of reusable query buffers.
        internal const int AsyncBatch = 65536;
        // 28 MiB of query buffers at 256k; keep the 7 MiB path on low-memory GPUs.
        internal int ProjectionBatchSize { get; set; } = SystemInfo.graphicsMemorySize > 1024 ? 262144 : AsyncBatch;
        internal double ProjectionSubmitMs { get; private set; }
        internal double ProjectionReadbackMs { get; private set; }
        internal double ProjectionResumeMs { get; private set; }
        internal double ReadbackCopyMs { get; private set; }

        public static bool Supported => SystemInfo.supportsComputeShaders;
        /// <summary>BVH_MAX_STACK in BvhTraversal.hlsl: a node at depth d holds up to d
        /// pending siblings and pushes two children, so the tree must satisfy depth + 2 ≤ this.</summary>
        internal const int TraversalStack = 48;
        // A readback that never completes (a lost device, a stalled driver) would hold
        // the bake and its buffers forever; the vertex-AO bake gives up after the same time.
        const double ReadbackTimeoutSec = 120.0;

        readonly ComputeShader shader;
        readonly int rayKernel, nearestKernel, projectionKernel;
        ComputeBuffer nodes, triIndices, verts, tris, faceNormals, eitherSide;
        ComputeBuffer rayOrigins, rayDirs, rayHits, points, queryNormals, nearestHits;
        int capacity;
        readonly int eitherSideCount;
        public int FaceCount { get; }
        internal int RayBatchCount { get; private set; }
        internal int NearestBatchCount { get; private set; }
        internal int ProjectionBatchCount { get; private set; }

        /// <summary>Null when compute shaders are unavailable or the kernel asset is missing (logged once).</summary>
        public static GpuBvh TryCreate(TriangleBvh bvh, Vector3[] faceNormals = null, bool[] eitherSide = null)
        {
            if (!Supported) return null;
            var shader = ComputeShaders.Find("BvhQueries");
            if (!shader) { UvtLog.Warn("[GPU] BvhQueries.compute not found; using the CPU BVH."); return null; }
            try { return new GpuBvh(bvh, shader, faceNormals, eitherSide); }
            catch (Exception e) { UvtLog.Warn("[GPU] BVH upload failed (" + e.Message + "); using the CPU BVH."); return null; }
        }

        GpuBvh(TriangleBvh bvh, ComputeShader shader, Vector3[] normals, bool[] either)
        {
            this.shader = shader;
            rayKernel = shader.FindKernel("Raycast");
            nearestKernel = shader.FindKernel("Nearest");
            projectionKernel = shader.FindKernel("ProjectSurface");
            // FindKernel can return an index even when that kernel failed to compile.
            // Reject the backend before allocating buffers or reading invalid results.
            if (!shader.IsSupported(rayKernel) || !shader.IsSupported(nearestKernel) || !shader.IsSupported(projectionKernel))
                throw new InvalidOperationException("BvhQueries kernels are not supported or failed to compile");
            // The traversal silently skips the children it cannot push; a deeper tree
            // would miss hits on the GPU that the CPU finds, so it stays on the CPU.
            int depth = bvh.Depth;
            if (depth + 2 > TraversalStack)
                throw new InvalidOperationException($"BVH depth {depth} exceeds the GPU traversal stack of {TraversalStack} entries");
            bvh.GetGPUData(out var gpuNodes, out var gpuTriIndices, out var gpuVerts, out var gpuTris);
            FaceCount = gpuTris.Length / 3;
            // A ComputeBuffer allocation can throw mid-way (out of GPU memory);
            // the caller's catch in TryCreate never sees a constructed instance,
            // so release whatever was already uploaded here instead of leaking it.
            try
            {
                nodes = new ComputeBuffer(Math.Max(1, gpuNodes.Length), Marshal.SizeOf<TriangleBvh.GPUNode>()); nodes.SetData(gpuNodes);
                triIndices = new ComputeBuffer(Math.Max(1, gpuTriIndices.Length), 4); triIndices.SetData(gpuTriIndices);
                verts = new ComputeBuffer(Math.Max(1, gpuVerts.Length), 12); verts.SetData(gpuVerts);
                tris = new ComputeBuffer(Math.Max(1, gpuTris.Length), 4); tris.SetData(gpuTris);
                // The filters read these per face; unused, they stay 1-element dummies.
                var n = normals != null && normals.Length == FaceCount ? normals : new Vector3[1];
                faceNormals = new ComputeBuffer(Math.Max(1, n.Length), 12); faceNormals.SetData(n);
                uint[] mask = new uint[1];
                if (either != null && either.Length == FaceCount) {
                    mask = new uint[FaceCount];
                    for (int i = 0; i < FaceCount; ++i) { mask[i] = either[i] ? 1u : 0u; }
                    eitherSideCount = FaceCount;
                }
                eitherSide = new ComputeBuffer(Math.Max(1, mask.Length), 4); eitherSide.SetData(mask);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Binds the tree (nodes, faces, normals, either-side mask) to a kernel that includes BvhTraversal.hlsl.</summary>
        public void Bind(ComputeShader target, int kernel)
        {
            target.SetBuffer(kernel, "_BVHNodes", nodes);
            target.SetBuffer(kernel, "_TriVerts", verts);
            target.SetBuffer(kernel, "_TriIndices", triIndices);
            target.SetBuffer(kernel, "_Tris", tris);
            target.SetBuffer(kernel, "_FaceNormals", faceNormals);
            target.SetBuffer(kernel, "_EitherSide", eitherSide);
            target.SetInt("_EitherSideCount", eitherSideCount);
        }

        /// <summary>
        /// Closest hit for count rays: origins.xyz / dirs.xyz with the reach in origins.w
        /// (≤ 0 skips the ray). facingFilter mirrors TriangleBvh.RaycastFacingFiltered
        /// with the uploaded (oriented) face normals and either-side mask.
        /// </summary>
        public void Raycast(Vector4[] origins, Vector4[] dirs, int count, bool facingFilter, RayHit[] results)
        {
            for (int start = 0; start < count; start += MaxBatch) {
                int n = Math.Min(MaxBatch, count - start);
                DispatchRays(origins, dirs, start, n, facingFilter);
                rayHits.GetData(results, start, 0, n);
            }
        }

        /// <summary>Sequential main-thread batches; cancellation drains the current
        /// readback before returning so the caller can safely dispose or reuse buffers.</summary>
        public async Task RaycastAsync(Vector4[] origins, Vector4[] dirs, int count, bool facingFilter,
            RayHit[] results, CancellationToken token)
        {
            for (int start = 0; start < count; start += AsyncBatch) {
                token.ThrowIfCancellationRequested();
                int n = Math.Min(AsyncBatch, count - start);
                DispatchRays(origins, dirs, start, n, facingFilter);
                await ReadAsync(rayHits, n, results, start);
                token.ThrowIfCancellationRequested();
            }
        }

        void DispatchRays(Vector4[] origins, Vector4[] dirs, int start, int n, bool facingFilter)
        {
            ++RayBatchCount;
            Ensure(n);
            rayOrigins.SetData(origins, start, 0, n);
            rayDirs.SetData(dirs, start, 0, n);
            Bind(shader, rayKernel);
            shader.SetBuffer(rayKernel, "_RayOrigins", rayOrigins);
            shader.SetBuffer(rayKernel, "_RayDirs", rayDirs);
            shader.SetBuffer(rayKernel, "_RayHits", rayHits);
            shader.SetInt("_QueryCount", n);
            shader.SetInt("_FacingFilter", facingFilter ? 1 : 0);
            shader.Dispatch(rayKernel, (n + 63) / 64, 1, 1);
        }

        /// <summary>
        /// Nearest face for count points: points.xyz with the radius in points.w (≤ 0
        /// skips the point). normalFilter mirrors TriangleBvh.FindNearestNormalFiltered
        /// with normals.xyz as the query normal and normals.w as the minimum dot.
        /// </summary>
        public void Nearest(Vector4[] pts, Vector4[] normals, int count, bool normalFilter, NearestHit[] results)
        {
            for (int start = 0; start < count; start += MaxBatch) {
                int n = Math.Min(MaxBatch, count - start);
                DispatchNearest(pts, normals, start, n, normalFilter);
                nearestHits.GetData(results, start, 0, n);
            }
        }

        public async Task NearestAsync(Vector4[] pts, Vector4[] normals, int count, bool normalFilter,
            NearestHit[] results, CancellationToken token)
        {
            for (int start = 0; start < count; start += AsyncBatch) {
                token.ThrowIfCancellationRequested();
                int n = Math.Min(AsyncBatch, count - start);
                DispatchNearest(pts, normals, start, n, normalFilter);
                await ReadAsync(nearestHits, n, results, start);
                token.ThrowIfCancellationRequested();
            }
        }

        void DispatchNearest(Vector4[] pts, Vector4[] normals, int start, int n, bool normalFilter)
        {
            ++NearestBatchCount;
            Ensure(n);
            points.SetData(pts, start, 0, n);
            queryNormals.SetData(normals, start, 0, n);
            Bind(shader, nearestKernel);
            shader.SetBuffer(nearestKernel, "_Points", points);
            shader.SetBuffer(nearestKernel, "_QueryNormals", queryNormals);
            shader.SetBuffer(nearestKernel, "_NearestHits", nearestHits);
            shader.SetInt("_QueryCount", n);
            shader.SetInt("_NormalFilter", normalFilter ? 1 : 0);
            shader.Dispatch(nearestKernel, (n + 63) / 64, 1, 1);
        }

        /// <summary>Ray projection plus the identical nearest fallback in one dispatch.
        /// Both readbacks drain before cancellation or buffer reuse.</summary>
        public async Task ProjectSurfaceAsync(Vector4[] origins, Vector4[] dirs, Vector4[] pts, Vector4[] normals,
            int count, bool facingFilter, RayHit[] rays, NearestHit[] nearest, CancellationToken token)
        {
            int batchSize = Math.Clamp(ProjectionBatchSize, 1, MaxBatch);
            for (int start = 0; start < count; start += batchSize) {
                token.ThrowIfCancellationRequested();
                int n = Math.Min(batchSize, count - start);
                var submit = Stopwatch.StartNew();
                Ensure(n);
                rayOrigins.SetData(origins, start, 0, n); rayDirs.SetData(dirs, start, 0, n);
                points.SetData(pts, start, 0, n); queryNormals.SetData(normals, start, 0, n);
                Bind(shader, projectionKernel);
                shader.SetBuffer(projectionKernel, "_RayOrigins", rayOrigins);
                shader.SetBuffer(projectionKernel, "_RayDirs", rayDirs);
                shader.SetBuffer(projectionKernel, "_Points", points);
                shader.SetBuffer(projectionKernel, "_QueryNormals", queryNormals);
                shader.SetBuffer(projectionKernel, "_RayHits", rayHits);
                shader.SetBuffer(projectionKernel, "_NearestHits", nearestHits);
                shader.SetInt("_QueryCount", n);
                shader.SetInt("_FacingFilter", facingFilter ? 1 : 0);
                shader.Dispatch(projectionKernel, (n + 63) / 64, 1, 1);
                ProjectionSubmitMs += submit.Elapsed.TotalMilliseconds;
                long submitted = Stopwatch.GetTimestamp(), rayReady = 0, nearestReady = 0;
                ++ProjectionBatchCount;
                var rayRead = ReadAsync(rayHits, n, rays, start, () => rayReady = Stopwatch.GetTimestamp());
                try { await Task.WhenAll(rayRead, ReadAsync(nearestHits, n, nearest, start, () => nearestReady = Stopwatch.GetTimestamp())); }
                // Even a synchronous failure submitting the second readback must
                // not let the caller dispose buffers still used by the first.
                finally { await rayRead; }
                long ready = Math.Max(rayReady, nearestReady);
                ProjectionReadbackMs += (ready - submitted) * (1000.0 / Stopwatch.Frequency);
                ProjectionResumeMs += (Stopwatch.GetTimestamp() - ready) * (1000.0 / Stopwatch.Frequency);
                token.ThrowIfCancellationRequested();
            }
        }

        Task ReadAsync<T>(ComputeBuffer buffer, int count, T[] results, int start, Action ready = null) where T : struct
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AsyncGPUReadback.Request(buffer, count * buffer.stride, 0, request => {
                // After the timeout below the task is already faulted and the caller may
                // have released its buffers and dropped its arrays: a late callback must
                // not write into results. (A request whose buffer was released reports an
                // error; a hung one never calls back at all, which is what the timeout is for.)
                if (completion.Task.IsCompleted) return;
                try {
                    if (request.hasError) throw new InvalidOperationException("GPU BVH readback failed.");
                    var copy = Stopwatch.StartNew();
                    var data = request.GetData<T>();
                    for (int i = 0; i < count; ++i) results[start + i] = data[i];
                    ReadbackCopyMs += copy.Elapsed.TotalMilliseconds;
                    ready?.Invoke();
                    completion.TrySetResult(true);
                }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
            // The timer is cancelled by the readback's own completion, so a healthy bake
            // keeps no two-minute timers alive; a late callback then finds the task set.
            var timeout = new CancellationTokenSource();
            Task.Delay(TimeSpan.FromSeconds(ReadbackTimeoutSec), timeout.Token).ContinueWith(delay => {
                if (!delay.IsCanceled)
                    completion.TrySetException(new TimeoutException($"GPU BVH readback did not complete within {ReadbackTimeoutSec:F0}s."));
            }, TaskScheduler.Default);
            completion.Task.ContinueWith(_ => {
                timeout.Cancel();
                timeout.Dispose();
            }, TaskScheduler.Default);
            return completion.Task;
        }

        void Ensure(int n)
        {
            if (n <= capacity) return;
            ReleaseQueryBuffers();
            int size = Math.Max(n, 4096);
            // capacity is set last: a failed allocation leaves it at zero, so the next
            // batch allocates again instead of dispatching into null buffers.
            try {
                rayOrigins = new ComputeBuffer(size, 16); rayDirs = new ComputeBuffer(size, 16);
                rayHits = new ComputeBuffer(size, Marshal.SizeOf<RayHit>());
                points = new ComputeBuffer(size, 16); queryNormals = new ComputeBuffer(size, 16);
                nearestHits = new ComputeBuffer(size, Marshal.SizeOf<NearestHit>());
            }
            catch { ReleaseQueryBuffers(); throw; }
            capacity = size;
        }

        void ReleaseQueryBuffers()
        {
            rayOrigins?.Dispose(); rayDirs?.Dispose(); rayHits?.Dispose(); points?.Dispose(); queryNormals?.Dispose(); nearestHits?.Dispose();
            rayOrigins = rayDirs = rayHits = points = queryNormals = nearestHits = null;
            capacity = 0;
        }

        public void Dispose()
        {
            ReleaseQueryBuffers();
            nodes?.Dispose(); triIndices?.Dispose(); verts?.Dispose(); tris?.Dispose(); faceNormals?.Dispose(); eitherSide?.Dispose();
            nodes = triIndices = verts = tris = faceNormals = eitherSide = null;
        }
    }
}
