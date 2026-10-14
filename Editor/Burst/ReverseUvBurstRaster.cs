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
        static ReverseUvBurstRaster()
        {ReverseUvRasterAcceleration.evaluate=Evaluate;ReverseUvRasterAcceleration.beginTestScope=()=>new CompilerTestScope();}

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
                bool native=true;Managed(ref native);compiled[index]=(byte)(native?1:0);
                var t=triangles[index];
                for(int y=t.firstRow;y<t.lastRow;++y)
                {
                    double bottom=y-radius,top=y+1+radius,left=double.MaxValue,right=double.MinValue;
                    Include(t.a,bottom,top,ref left,ref right);Include(t.b,bottom,top,ref left,ref right);
                    Include(t.c,bottom,top,ref left,ref right);
                    Edge(t.a,t.b,bottom,top,ref left,ref right);Edge(t.b,t.c,bottom,top,ref left,ref right);
                    Edge(t.c,t.a,bottom,top,ref left,ref right);
                    var span=new ReverseUvRasterAcceleration.Span {row=y};
                    if(left<=right)
                    {span.start=Math.Max(0,(int)Math.Floor(left-radius));span.end=Math.Min(width,(int)Math.Ceiling(right+radius));}
                    spans[t.outputStart+y-t.firstRow]=span;
                }
            }
            [BurstDiscard] static void Managed(ref bool native) {native=false;}
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
