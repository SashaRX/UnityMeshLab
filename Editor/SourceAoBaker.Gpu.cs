using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    internal sealed partial class SourceAoBaker
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SurfacePoint
        {
            public Vector3 origin;
            public uint seed;
            public Vector3 normal;
            public float padding;
        }

        internal static bool GpuSupported => GpuBvh.Supported && SystemInfo.supportsAsyncGPUReadback;

        internal Gpu TryCreateGpu(GpuBvh tree)
        {
            if (tree == null || !GpuSupported) return null;
            var shader = ComputeShaders.Find("SourceAORayTrace");
            if (!shader) { UvtLog.Warn("[Texture AO] SourceAORayTrace.compute missing; using CPU AO."); return null; }
            try { return new Gpu(this, tree, shader); }
            catch (Exception exception) { UvtLog.Warn("[Texture AO] GPU AO unavailable: " + exception.Message + "; using CPU AO."); return null; }
        }

        // Owns only AO buffers; the caller owns the shared projection/occlusion BVH.
        // Small point/direction batches bound dispatch time. Readbacks drain before
        // cancellation propagates, so Dispose never releases an in-flight AO buffer.
        internal sealed class Gpu : IDisposable
        {
            internal const int PointBatch = 4096;
            const int DirectionBatch = 64;
            readonly SourceAoBaker owner;
            readonly GpuBvh tree;
            readonly ComputeShader shader;
            readonly int bakeKernel, finalKernel;
            readonly float[] zeroCounters = new float[PointBatch * 2];
            ComputeBuffer points, directions, counters, result;

            internal Gpu(SourceAoBaker owner, GpuBvh tree, ComputeShader shader)
            {
                this.owner = owner; this.tree = tree; this.shader = shader;
                bakeKernel = shader.FindKernel("BakeAO"); finalKernel = shader.FindKernel("FinalizeAO");
                if (!shader.IsSupported(bakeKernel) || !shader.IsSupported(finalKernel))
                    throw new InvalidOperationException("Source AO kernels unsupported or failed to compile.");
                try {
                    points = new ComputeBuffer(PointBatch, Marshal.SizeOf<SurfacePoint>());
                    directions = new ComputeBuffer(owner.directions.Length, 12); directions.SetData(owner.directions);
                    counters = new ComputeBuffer(PointBatch, 8); result = new ComputeBuffer(PointBatch, 4);
                }
                catch { Dispose(); throw; }
            }

            void Bind(int count, int start, int end)
            {
                // Rebind every dispatch: another bake can share the shader asset.
                tree.Bind(shader, bakeKernel);
                shader.SetBuffer(bakeKernel, "_SourcePoints", points);
                shader.SetBuffer(bakeKernel, "_Directions", directions);
                shader.SetBuffer(bakeKernel, "_AOCounters", counters);
                shader.SetInt("_PointCount", count); shader.SetInt("_DirStart", start); shader.SetInt("_DirEnd", end);
                shader.SetFloat("_MaxDist", owner.distance); shader.SetFloat("_MinHitDist", owner.offset * .1f);
                shader.SetFloat("_CosineWeighted", owner.settings.cosineWeighted ? 1 : 0);
                shader.SetFloat("_BinaryHit", owner.settings.binaryHit ? 1 : 0);
                shader.SetFloat("_BackfaceCulling", owner.settings.backfaceCulling ? 1 : 0);
                shader.SetFloat("_GroundPlane", owner.settings.groundPlane ? 1 : 0);
                shader.SetVector("_GroundNormal", owner.groundNormal);
                shader.SetFloat("_GroundHeight", owner.groundHeight);
            }

            Task<float[]> Readback(int count)
            {
                var completion = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                AsyncGPUReadback.Request(result, count * sizeof(float), 0, request => {
                    try {
                        if (request.hasError) completion.TrySetException(new InvalidOperationException("GPU AO readback failed."));
                        else completion.TrySetResult(request.GetData<float>().ToArray());
                    }
                    catch (Exception exception) { completion.TrySetException(exception); }
                });
                return completion.Task;
            }

            internal async Task SampleAsync(SurfacePoint[] samples, int count, float[] values, CancellationToken token)
            {
                for (int start = 0; start < count; start += PointBatch) {
                    token.ThrowIfCancellationRequested();
                    int n = Math.Min(PointBatch, count - start);
                    points.SetData(samples, start, 0, n); counters.SetData(zeroCounters);
                    Task<float[]> readback = null; bool submitted = false;
                    try {
                        for (int d = 0; d < owner.directions.Length; d += DirectionBatch) {
                            if (token.IsCancellationRequested) break;
                            Bind(n, d, Math.Min(owner.directions.Length, d + DirectionBatch));
                            shader.Dispatch(bakeKernel, (n + 63) / 64, 1, 1); submitted = true;
                            await Task.Yield();
                        }
                        shader.SetInt("_PointCount", n); shader.SetFloat("_Intensity", owner.settings.intensity);
                        shader.SetBuffer(finalKernel, "_AOCounters", counters); shader.SetBuffer(finalKernel, "_AOResult", result);
                        shader.Dispatch(finalKernel, (n + 63) / 64, 1, 1); submitted = true;
                        readback = Readback(n);
                        var pixels = await readback;
                        token.ThrowIfCancellationRequested();
                        Array.Copy(pixels, 0, values, start, n);
                    }
                    finally { if (submitted && readback == null) await Readback(n); }
                }
            }

            public void Dispose()
            {
                points?.Dispose(); directions?.Dispose(); counters?.Dispose(); result?.Dispose();
                points = directions = counters = result = null;
            }
        }
    }
}
