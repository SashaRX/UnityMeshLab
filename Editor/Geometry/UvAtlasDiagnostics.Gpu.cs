using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class UvAtlasDiagnostics
    {
        internal sealed class GpuInput
        {
            internal Vector4[] bounds;
            internal int[] cells, members, offsets;
            internal int resolutionX;
        }

        internal struct CandidatePair
        {
            internal int a, b;
            internal long ordinal;
        }

        internal sealed class CandidateSelection
        {
            internal CandidatePair[] pairs;
            internal long comparisons;
        }

        sealed class GpuScan
        {
            internal bool capture;
            internal GpuInput input;
            internal CandidateSelection selection;
        }

        internal sealed class GpuMeasurement
        {
            internal Report report;
            internal bool executed;
            internal string refusal;
            internal double prepareMs, selectionMs, exactMs;
            internal int candidateCount;
        }

        // Explicit experimental entry point. Normal Measure and publication
        // quality gates remain on their existing path until this backend qualifies.
        internal static async Task<GpuMeasurement> MeasureGpuAsync(RemeshNative.Geometry geometry,
            GpuUvAtlasPairs backend, CancellationToken token, long comparisonBudget = -1,
            bool collectConflicts = true, bool sameChartOnly = false)
        {
            var watch = Stopwatch.StartNew();
            var snapshot = new RemeshNative.Geometry {uv=(Vector2[])geometry.uv.Clone(),
                indices=(int[])geometry.indices.Clone(),charts=(int[])geometry.charts.Clone()};
            var result = new GpuMeasurement();
            var request = new GpuScan {capture=true};
            MeasureCore(snapshot,token,sameChartOnly,comparisonBudget,collectConflicts,default,1,request);
            result.prepareMs=watch.Elapsed.TotalMilliseconds;
            CandidateSelection selection=null;
            if(request.input!=null && backend!=null)
            {
                watch.Restart();
                try {selection=await backend.FindAsync(request.input,token);}
                catch(OperationCanceledException) {throw;}
                catch(Exception error) {result.refusal=error.Message;}
                result.selectionMs=watch.Elapsed.TotalMilliseconds;
            }
            else result.refusal="GPU backend unavailable or fewer than two valid triangles.";
            token.ThrowIfCancellationRequested();
            watch.Restart();
            result.executed=selection!=null;
            result.candidateCount=selection?.pairs.Length??0;
            result.report=MeasureCore(snapshot,token,sameChartOnly,comparisonBudget,collectConflicts,
                default,1,selection==null?null:new GpuScan {selection=selection});
            result.exactMs=watch.Elapsed.TotalMilliseconds;
            return result;
        }

        static GpuInput CaptureGpuInput(List<Triangle> triangles,int[] cells,int[] members,int[] offsets,int resolutionX)
        {
            var bounds=new Vector4[triangles.Count];
            for(int i=0;i<bounds.Length;++i)
            {
                var t=triangles[i];
                if(!FiniteBound(t.minX)||!FiniteBound(t.minY)||!FiniteBound(t.maxX)||!FiniteBound(t.maxY)) return null;
                // Only reject separated boxes on GPU. Widen before Float32
                // conversion, including a normal-sized margin for denormals.
                double magnitude=Math.Max(1,Math.Max(Math.Max(Math.Abs(t.minX),Math.Abs(t.maxX)),
                    Math.Max(Math.Abs(t.minY),Math.Abs(t.maxY))));
                double pad=magnitude/1048576;
                bounds[i]=new Vector4((float)(t.minX-pad),(float)(t.minY-pad),
                    (float)(t.maxX+pad),(float)(t.maxY+pad));
            }
            return new GpuInput {bounds=bounds,cells=cells,members=members,offsets=offsets,resolutionX=resolutionX};
        }

        static bool FiniteBound(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
    }
}
