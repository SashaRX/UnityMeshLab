using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Optional discovery library. Final answers always use TriangleBvh's managed
    // primitives/order; absent or older native plugins retain the managed path.
    internal static class ReverseUvQueryBackend
    {
        internal enum Mode { Legacy, Managed, Auto, Embree }
        [StructLayout(LayoutKind.Sequential)] internal struct Query
        { internal Vector3 origin,direction; internal float reach,target,dotMin; internal int mode; }
        [StructLayout(LayoutKind.Sequential)] internal struct Answer
        { internal int tri; internal float distance; internal Vector3 bary,point; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Create(
            [In] Vector3[] vertices,int vertexCount,[In] int[] indices,int indexCount,[In] Vector3[] normals,[In] byte[] either);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Destroy(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Proposals(IntPtr handle,
            [In] Query[] queries,[Out] Answer[] answers,int count,int workers);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Candidates(IntPtr handle,
            [In] Query[] queries,[Out] int[] candidates,[Out] int[] counts,int count,int capacity,int workers);
        internal sealed class Api
        { internal Create create; internal Destroy destroy; internal Proposals proposals; internal Candidates candidates; }
        const string Library="meshlab-queries";
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int meshLabEmbreeVersion();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr meshLabEmbreeCreate(
            Vector3[] vertices,int vertexCount,int[] indices,int indexCount,Vector3[] normals,byte[] either);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void meshLabEmbreeDestroy(IntPtr handle);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int meshLabEmbreeQueries(
            IntPtr handle,Query[] queries,Answer[] answers,int count,int workers);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int meshLabEmbreeCandidates(
            IntPtr handle,Query[] queries,int[] candidates,int[] counts,int count,int capacity,int workers);
        static readonly Lazy<Api> plugin=new Lazy<Api>(()=>{
            try {return meshLabEmbreeVersion()==1?new Api {create=meshLabEmbreeCreate,destroy=meshLabEmbreeDestroy,
                proposals=meshLabEmbreeQueries,candidates=meshLabEmbreeCandidates}:null;}
            catch(Exception e) when(e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {return null;}
        });
        internal static Api Available => plugin.Value;
        internal const int BatchSize=4096, CandidateCapacity=256;
        internal static bool SelectEmbree(Mode mode,int faces,int queries,bool available)
            => available && (mode==Mode.Embree || mode==Mode.Auto && faces>=1024 && queries>=512);

        internal sealed class Scene : IDisposable
        {
            readonly Api api;
            internal readonly ReverseUvCandidateBvh resolver;
            IntPtr handle;
            internal static Scene TryCreate(TriangleBvh tree,Vector3[] normals,Api api,CancellationToken token)
            {
                if(api==null) return null;
                tree.GetGPUData(out _,out _,out var vertices,out var indices);
                if(indices.Length==0 || normals.Length!=indices.Length/3) return null;
                foreach(var p in vertices) if(!Finite(p)) return null;
                foreach(var p in normals) if(!Finite(p)) return null;
                token.ThrowIfCancellationRequested();
                IntPtr native=api.create(vertices,vertices.Length,indices,indices.Length,normals,new byte[normals.Length]);
                if(native==IntPtr.Zero) return null;
                try {return new Scene(api,native,new ReverseUvCandidateBvh(tree,normals,null));}
                catch {api.destroy(native);throw;}
            }
            Scene(Api api,IntPtr handle,ReverseUvCandidateBvh resolver) {this.api=api;this.handle=handle;this.resolver=resolver;}
            internal bool Discover(Buffers buffers,int count,int workers,CancellationToken token)
            {
                if(handle==IntPtr.Zero) throw new ObjectDisposedException(nameof(Scene));
                token.ThrowIfCancellationRequested();
                if(api.proposals(handle,buffers.queries,buffers.proposals,count,workers)!=0) return false;
                buffers.For(count,workers,token,i=>buffers.regions[i]=resolver.Region(buffers.queries[i],buffers.proposals[i]));
                token.ThrowIfCancellationRequested();
                if(api.candidates(handle,buffers.regions,buffers.candidates,buffers.counts,count,CandidateCapacity,workers)!=0) return false;
                buffers.Workers(workers,token,worker=>{
                    for(int i=worker;i<count;i+=workers) {
                        if((i&63)==worker) token.ThrowIfCancellationRequested();
                        buffers.answers[i]=resolver.Resolve(buffers.queries[i],buffers.candidates,i*CandidateCapacity,buffers.counts[i],buffers.scratch[worker]);
                    }
                });
                token.ThrowIfCancellationRequested();return true;
            }
            public void Dispose() {if(handle==IntPtr.Zero) return;var previous=handle;handle=IntPtr.Zero;api.destroy(previous);}
        }

        // One bounded owner per projection call. Arrays/scratch survive every
        // chart and face block, but never survive a LOD/context change.
        internal sealed class Buffers
        {
            internal readonly Query[] queries=new Query[BatchSize];
            internal readonly Answer[] answers=new Answer[BatchSize];
            internal Query[] regions;
            internal Answer[] proposals;
            internal int[] candidates,counts;
            internal int[][] scratch;
            internal int nativeAllocations;
            internal void EnsureNative()
            {
                if(candidates!=null) return;
                regions=new Query[BatchSize];proposals=new Answer[BatchSize];counts=new int[BatchSize];
                candidates=new int[BatchSize*CandidateCapacity];scratch=new int[8][];
                for(int i=0;i<scratch.Length;++i) scratch[i]=new int[CandidateCapacity*2];
                ++nativeAllocations;
            }
            internal void Resolve(TriangleBvh tree,Vector3[] normals,Scene scene,int count,CancellationToken token,
                TransferPerformanceProfile profile)
            {
                if(count<0 || count>BatchSize) throw new ArgumentOutOfRangeException(nameof(count));
                int workers=count>=512?Math.Min(8,Environment.ProcessorCount):1;
                if(scene!=null) {
                    EnsureNative();using var timing=profile?.Measure("Embree nearest batches");
                    if(scene.Discover(this,count,workers,token)) {
                        int fallback=0;for(int i=0;i<count;++i) if(counts[i]<0) ++fallback;
                        profile?.Record("Embree nearest queries",0,count);profile?.Record("Embree capacity fallback queries",0,fallback);return;
                    }
                    profile?.Record("Embree failed batches",0,1);
                }
                using var managedTiming=profile?.Measure("Managed nearest batches");
                For(count,workers,token,i=>{
                    var q=queries[i];var h=tree.FindNearestNormalFiltered(q.origin,q.direction,normals,q.dotMin,q.reach);
                    answers[i]=new Answer {tri=h.triangleIndex,distance=h.distSq,bary=h.barycentric,point=h.point};
                });
                profile?.Record("Managed nearest batch queries",0,count);
            }
            internal void For(int count,int workers,CancellationToken token,Action<int> action)
                => Workers(workers,token,w=>{for(int i=w;i<count;i+=workers) {if((i&63)==w) token.ThrowIfCancellationRequested();action(i);}});
            internal void Workers(int workers,CancellationToken token,Action<int> action)
            {
                if(workers==1) {action(0);return;}
                Parallel.For(0,workers,new ParallelOptions {MaxDegreeOfParallelism=workers,CancellationToken=token},action);
            }
        }
        internal static bool Finite(Vector3 p)=>float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
}
