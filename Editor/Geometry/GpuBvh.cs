using System;
using System.Runtime.InteropServices;
using UnityEngine;

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

        public static bool Supported => SystemInfo.supportsComputeShaders;

        readonly ComputeShader shader;
        readonly int rayKernel, nearestKernel;
        ComputeBuffer nodes, triIndices, verts, tris, faceNormals, eitherSide;
        ComputeBuffer rayOrigins, rayDirs, rayHits, points, queryNormals, nearestHits;
        int capacity;
        readonly int eitherSideCount;
        public int FaceCount { get; }

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
            bvh.GetGPUData(out var gpuNodes, out var gpuTriIndices, out var gpuVerts, out var gpuTris);
            FaceCount = gpuTris.Length / 3;
            nodes = new ComputeBuffer(Math.Max(1, gpuNodes.Length), Marshal.SizeOf<TriangleBvh.GPUNode>()); nodes.SetData(gpuNodes);
            triIndices = new ComputeBuffer(Math.Max(1, gpuTriIndices.Length), 4); triIndices.SetData(gpuTriIndices);
            verts = new ComputeBuffer(Math.Max(1, gpuVerts.Length), 12); verts.SetData(gpuVerts);
            tris = new ComputeBuffer(Math.Max(1, gpuTris.Length), 4); tris.SetData(gpuTris);
            // The filters read these per face; unused, they stay 1-element dummies.
            var n = normals != null && normals.Length == FaceCount ? normals : new Vector3[1];
            faceNormals = new ComputeBuffer(Math.Max(1, n.Length), 12); faceNormals.SetData(n);
            uint[] mask = new uint[1];
            if (either != null && either.Length == FaceCount) { mask = new uint[FaceCount]; for (int i = 0; i < FaceCount; ++i) mask[i] = either[i] ? 1u : 0u; eitherSideCount = FaceCount; }
            eitherSide = new ComputeBuffer(Math.Max(1, mask.Length), 4); eitherSide.SetData(mask);
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
                rayHits.GetData(results, start, 0, n);
            }
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
                nearestHits.GetData(results, start, 0, n);
            }
        }

        void Ensure(int n)
        {
            if (n <= capacity) return;
            ReleaseQueryBuffers();
            capacity = Math.Max(n, 4096);
            rayOrigins = new ComputeBuffer(capacity, 16); rayDirs = new ComputeBuffer(capacity, 16);
            rayHits = new ComputeBuffer(capacity, Marshal.SizeOf<RayHit>());
            points = new ComputeBuffer(capacity, 16); queryNormals = new ComputeBuffer(capacity, 16);
            nearestHits = new ComputeBuffer(capacity, Marshal.SizeOf<NearestHit>());
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
