using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Main-thread-only experimental broad phase. GPU emits a superset
    /// of AABB candidates; exact intersection predicates remain on the CPU.</summary>
    internal sealed class GpuUvAtlasPairs
    {
        const long MaxPairVisits=128000000;
        const int MaxRows=64*65535;
        readonly ComputeShader shader;
        readonly int kernel, capacity;
        [StructLayout(LayoutKind.Sequential)]
        struct Four {public uint x,y,z,w;}
        [StructLayout(LayoutKind.Sequential)]
        struct Cell {public int x,y,z,w;}

        internal GpuUvAtlasPairs(int capacity=2000000)
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("Compute shaders and asynchronous GPU readback are required.");
            if(capacity<1 || capacity>2000000) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity=capacity;
            shader=ComputeShaders.Find("UvAtlasPairs");
            if(!shader) throw new InvalidOperationException("UvAtlasPairs.compute is unavailable.");
            kernel=shader.FindKernel("FindPairs");
            if(!shader.IsSupported(kernel)) throw new InvalidOperationException("UvAtlasPairs kernel is unsupported.");
        }

        internal async Task<UvAtlasDiagnostics.CandidateSelection> FindAsync(UvAtlasDiagnostics.GpuInput input,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var rows=new List<Four>();
            long visits=0;
            for(int cell=0;cell<input.offsets.Length-1;++cell)
            {
                int end=input.offsets[cell+1];
                for(int m=input.offsets[cell];m+1<end;++m)
                {
                    visits+=end-m-1;
                    if(visits>MaxPairVisits || rows.Count>=MaxRows)
                        throw new InvalidOperationException("GPU broad phase work limit; use the complete CPU scan.");
                    rows.Add(new Four {x=(uint)m,y=(uint)end,z=(uint)(cell%input.resolutionX),w=(uint)(cell/input.resolutionX)});
                }
                if((cell&255)==0) token.ThrowIfCancellationRequested();
            }
            if(rows.Count==0) return new UvAtlasDiagnostics.CandidateSelection {pairs=Array.Empty<UvAtlasDiagnostics.CandidatePair>()};
            var cells=new Cell[input.bounds.Length];
            for(int i=0;i<cells.Length;++i) cells[i]=new Cell {x=input.cells[i*4],y=input.cells[i*4+1],
                z=input.cells[i*4+2],w=input.cells[i*4+3]};
            var buffers=new List<ComputeBuffer>();
            ComputeBuffer Upload<T>(T[] values,int stride) where T:struct
            {
                var buffer=new ComputeBuffer(values.Length,stride);buffers.Add(buffer);buffer.SetData(values);return buffer;
            }
            ComputeBuffer Allocate(int count,int stride)
            {var buffer=new ComputeBuffer(count,stride);buffers.Add(buffer);return buffer;}
            try
            {
                var boundsBuffer=Upload(input.bounds,16);var cellsBuffer=Upload(cells,16);
                var membersBuffer=Upload(input.members,4);var rowsBuffer=Upload(rows.ToArray(),16);
                var countsBuffer=Allocate(rows.Count,4);var counter=Upload(new uint[2],4);var pairs=Allocate(capacity,16);
                shader.SetBuffer(kernel,"_Bounds",boundsBuffer);shader.SetBuffer(kernel,"_Cells",cellsBuffer);
                shader.SetBuffer(kernel,"_Members",membersBuffer);shader.SetBuffer(kernel,"_Rows",rowsBuffer);
                shader.SetBuffer(kernel,"_OwnedCounts",countsBuffer);shader.SetBuffer(kernel,"_Counter",counter);
                shader.SetBuffer(kernel,"_Pairs",pairs);shader.SetInt("_RowCount",rows.Count);shader.SetInt("_Capacity",capacity);
                shader.Dispatch(kernel,(rows.Count+63)/64,1,1);
                // Await every submitted request before releasing buffers, including
                // cancellation/errors. No synchronous GetData or GPU wait is used.
                var countsTask=Read<uint>(countsBuffer,rows.Count,4);
                var counterTask=Read<uint>(counter,2,4);
                await Task.WhenAll(countsTask,counterTask);
                token.ThrowIfCancellationRequested();
                var counterData=counterTask.Result;
                if(counterData[1]!=0 || counterData[0]>(uint)capacity)
                    throw new InvalidOperationException("GPU candidate buffer overflow; use the complete CPU scan.");
                int count=(int)counterData[0];
                var raw=count==0?Array.Empty<Four>():await Read<Four>(pairs,count,16);
                token.ThrowIfCancellationRequested();
                var counts=countsTask.Result;var prefix=new long[counts.Length+1];
                for(int i=0;i<counts.Length;++i)
                {
                    if(counts[i]>rows[i].y-rows[i].x-1) throw new InvalidOperationException("Invalid GPU pair count.");
                    prefix[i+1]=prefix[i]+counts[i];
                }
                var selected=new UvAtlasDiagnostics.CandidatePair[raw.Length];
                for(int i=0;i<raw.Length;++i)
                {
                    var p=raw[i];
                    if(p.x>=(uint)cells.Length || p.y>=(uint)cells.Length || p.x==p.y
                        || p.z>=(uint)counts.Length || p.w==0 || p.w>counts[p.z])
                        throw new InvalidOperationException("Invalid GPU candidate record.");
                    selected[i]=new UvAtlasDiagnostics.CandidatePair {a=(int)p.x,b=(int)p.y,ordinal=prefix[p.z]+p.w};
                }
                Array.Sort(selected,(a,b)=>a.ordinal.CompareTo(b.ordinal));
                for(int i=1;i<selected.Length;++i)
                    if(selected[i-1].ordinal==selected[i].ordinal) throw new InvalidOperationException("Duplicate GPU candidate ordinal.");
                return new UvAtlasDiagnostics.CandidateSelection {pairs=selected,comparisons=prefix[counts.Length]};
            }
            finally {foreach(var buffer in buffers) buffer.Release();}
        }

        static Task<T[]> Read<T>(ComputeBuffer buffer,int count,int stride) where T:struct
        {
            var completion=new TaskCompletionSource<T[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                AsyncGPUReadback.Request(buffer,checked(count*stride),0,request=>{
                    try
                    {
                        if(request.hasError) completion.TrySetException(new InvalidOperationException("GPU candidate readback failed."));
                        else completion.TrySetResult(request.GetData<T>().ToArray());
                    }
                    catch(Exception error) {completion.TrySetException(error);}
                });
            }
            catch(Exception error) {completion.TrySetException(error);}
            return completion.Task;
        }
    }
}
