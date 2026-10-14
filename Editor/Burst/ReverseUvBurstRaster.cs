#if MESHLAB_BURST && MESHLAB_COLLECTIONS
using System;
using System.Threading;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [InitializeOnLoad]
    internal static class ReverseUvBurstRaster
    {
        // InitializeOnLoad runs on the main thread. Jobs can be scheduled only
        // there; asynchronous Reverse packs on a worker use the direct call.
        static readonly int mainThread=Thread.CurrentThread.ManagedThreadId;

        static ReverseUvBurstRaster()
        {
            ReverseUvRasterAcceleration.evaluate=Evaluate;ReverseUvRasterAcceleration.beginTestScope=()=>new CompilerTestScope();
            // Compile the direct-call kernel on the main thread, off the load path.
            EditorApplication.delayCall+=() => {if(BurstCompiler.IsEnabled) WarmUp();};
        }

        static unsafe void WarmUp()
        {
            var triangle=new ReverseUvRasterAcceleration.Triangle();var span=new ReverseUvRasterAcceleration.Span();byte compiled=0;
            RasterKernel.Rasterize(&triangle,1,&span,&compiled,0,0);
        }

        // Only the explicit test hook uses this scope. Mute the editor option
        // persister while toggling runtime flags; no user EditorPrefs are written.
        sealed class CompilerTestScope : IDisposable
        {
            readonly bool enabled= BurstCompiler.Options.EnableBurstCompilation;
            readonly bool synchronous= BurstCompiler.Options.EnableBurstCompileSynchronously;
            bool disposed;
            internal CompilerTestScope() {Set(true,true);}
            static void Set(bool enabled,bool synchronous)
            {
                var options=BurstCompiler.Options;
                var callback=typeof(BurstCompilerOptions).GetProperty("OptionsChanged",BindingFlags.Instance|BindingFlags.NonPublic);
                if(callback==null) throw new MissingMemberException("Burst test option callback was not found.");
                var previous=callback.GetValue(options);callback.SetValue(options,null);
                try {options.EnableBurstCompilation=enabled;options.EnableBurstCompileSynchronously=synchronous;}
                finally {callback.SetValue(options,previous);}
            }
            public void Dispose() {if(disposed) return;Set(enabled,synchronous);disposed=true;}
        }

        static ReverseUvRasterAcceleration.Output Evaluate(ReverseUvRasterAcceleration.Triangle[] triangles,
            int capacity,int width,double radius,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!BurstCompiler.IsEnabled) {ReverseUvRasterAcceleration.LastRefusal="Burst compilation is disabled";return null;}
            if(Thread.CurrentThread.ManagedThreadId!=mainThread) return EvaluateDirect(triangles,capacity,width,radius,token);
            using var input=new NativeArray<ReverseUvRasterAcceleration.Triangle>(triangles,Allocator.TempJob);
            using var spans=new NativeArray<ReverseUvRasterAcceleration.Span>(capacity,Allocator.TempJob);
            using var compiled=new NativeArray<byte>(triangles.Length,Allocator.TempJob);
            new RasterJob {triangles=input,spans=spans,compiled=compiled,width=width,radius=radius}
                .Schedule(triangles.Length,32).Complete();
            token.ThrowIfCancellationRequested();
            for(int i=0;i<compiled.Length;++i) if(compiled[i]!=1)
            {ReverseUvRasterAcceleration.LastRefusal="Job ran managed; enabled="+BurstCompiler.IsEnabled+", job compiler="+Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobCompilerEnabled;return null;}
            return new ReverseUvRasterAcceleration.Output {spans=spans.ToArray(),burstExecuted=true};
        }

        // Worker threads cannot schedule jobs. Chunks of the same per-triangle
        // kernel run on the thread pool and write their own output ranges, so
        // the spans equal the job's; the caller merges them in triangle/row order.
        static unsafe ReverseUvRasterAcceleration.Output EvaluateDirect(ReverseUvRasterAcceleration.Triangle[] triangles,
            int capacity,int width,double radius,CancellationToken token)
        {
            const int Chunk=32;
            var spans=new ReverseUvRasterAcceleration.Span[capacity];var compiled=new byte[triangles.Length];
            System.Threading.Tasks.Parallel.For(0,(triangles.Length+Chunk-1)/Chunk,chunk=>{
                if(token.IsCancellationRequested) return;
                int start=chunk*Chunk,count=Math.Min(Chunk,triangles.Length-start);
                fixed(ReverseUvRasterAcceleration.Triangle* input=triangles)
                fixed(ReverseUvRasterAcceleration.Span* output=spans)
                fixed(byte* witness=compiled)
                    RasterKernel.Rasterize(input+start,count,output,witness+start,width,radius);
            });
            token.ThrowIfCancellationRequested();
            for(int i=0;i<compiled.Length;++i) if(compiled[i]!=1)
            {ReverseUvRasterAcceleration.LastRefusal="Direct call ran managed; enabled="+BurstCompiler.IsEnabled;return null;}
            return new ReverseUvRasterAcceleration.Output {spans=spans,burstExecuted=true};
        }

        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.Standard,CompileSynchronously=true)]
        static unsafe class RasterKernel
        {
            [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.Standard,CompileSynchronously=true)]
            internal static void Rasterize(ReverseUvRasterAcceleration.Triangle* triangles,int count,
                ReverseUvRasterAcceleration.Span* spans,byte* compiled,int width,double radius)
            {
                for(int i=0;i<count;++i)
                {
                    bool native=true;RasterMath.Managed(ref native);compiled[i]=(byte)(native?1:0);
                    var t=triangles[i];
                    for(int y=t.firstRow;y<t.lastRow;++y) spans[t.outputStart+y-t.firstRow]=RasterMath.Row(t,y,width,radius);
                }
            }
        }

        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.Standard,CompileSynchronously=true)]
        struct RasterJob : IJobParallelFor
        {
            [ReadOnly] internal NativeArray<ReverseUvRasterAcceleration.Triangle> triangles;
            [NativeDisableParallelForRestriction] internal NativeArray<ReverseUvRasterAcceleration.Span> spans;
            internal NativeArray<byte> compiled;
            internal int width;
            internal double radius;
            public void Execute(int index)
            {
                bool native=true;RasterMath.Managed(ref native);compiled[index]=(byte)(native?1:0);
                var t=triangles[index];
                for(int y=t.firstRow;y<t.lastRow;++y) spans[t.outputStart+y-t.firstRow]=RasterMath.Row(t,y,width,radius);
            }
        }

        // One scan-line span of one triangle, shared by the job and the direct call.
        static class RasterMath
        {
            internal static ReverseUvRasterAcceleration.Span Row(ReverseUvRasterAcceleration.Triangle t,int y,int width,double radius)
            {
                double bottom=y-radius,top=y+1+radius,left=double.MaxValue,right=double.MinValue;
                Include(t.a,bottom,top,ref left,ref right);Include(t.b,bottom,top,ref left,ref right);
                Include(t.c,bottom,top,ref left,ref right);
                Edge(t.a,t.b,bottom,top,ref left,ref right);Edge(t.b,t.c,bottom,top,ref left,ref right);
                Edge(t.c,t.a,bottom,top,ref left,ref right);
                var span=new ReverseUvRasterAcceleration.Span {row=y};
                if(left<=right)
                {span.start=Math.Max(0,(int)Math.Floor(left-radius));span.end=Math.Min(width,(int)Math.Ceiling(right+radius));}
                return span;
            }
            [BurstDiscard] internal static void Managed(ref bool native) {native=false;}
            static void Include(Vector2 p,double bottom,double top,ref double left,ref double right)
            {if(p.y>=bottom && p.y<=top) {left=Math.Min(left,p.x);right=Math.Max(right,p.x);}}
            static void Edge(Vector2 a,Vector2 b,double bottom,double top,ref double left,ref double right)
            {
                double rise=(double)b.y-a.y;if(rise==0) return;
                Intersection(a,b,rise,bottom,ref left,ref right);Intersection(a,b,rise,top,ref left,ref right);
            }
            static void Intersection(Vector2 a,Vector2 b,double rise,double v,ref double left,ref double right)
            {
                double fraction=(v-a.y)/rise;if(fraction<0 || fraction>1) return;
                double x=a.x+((double)b.x-a.x)*fraction;
                left=Math.Min(left,x);right=Math.Max(right,x);
            }
        }
    }
}
#endif
