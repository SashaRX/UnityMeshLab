using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class GpuUvAtlasPairsTests
    {
        static GpuUvAtlasPairs Backend(int capacity=2000000)
        {
            bool supported=SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;
            if(!supported && Environment.GetEnvironmentVariable("MESHLAB_REQUIRE_GPU_PAIR_SCAN")!="1")
                Assert.Ignore("GPU pair tests require a graphics device; omit -nographics.");
            Assert.IsTrue(supported,"The explicitly required GPU broad phase must execute.");
            return new GpuUvAtlasPairs(capacity);
        }

        static RemeshNative.Geometry Triangles(int count,bool duplicate=false)
        {
            var uv=new Vector2[count*3];
            for(int i=0;i<count;++i)
            {
                var p=duplicate?Vector2.zero:new Vector2((i%16)*.013f,(i/16)*.021f);
                uv[i*3]=p;uv[i*3+1]=p+new Vector2(.04f,.00003f);
                uv[i*3+2]=p+new Vector2(.011f,.07f);
            }
            return new RemeshNative.Geometry {uv=uv,indices=Enumerable.Range(0,uv.Length).ToArray(),
                charts=Enumerable.Range(0,uv.Length).Select(i=>i/3%3).ToArray()};
        }

        static void Equal(UvAtlasDiagnostics.Report a,UvAtlasDiagnostics.Report b)
        {
            Assert.AreEqual(a.complete,b.complete);Assert.AreEqual(a.comparisons,b.comparisons);
            Assert.AreEqual(a.pairs,b.pairs);Assert.AreEqual(a.sameChartPairs,b.sameChartPairs);
            Assert.AreEqual(a.crossChartPairs,b.crossChartPairs);Assert.AreEqual(a.invalidFaces,b.invalidFaces);
            Assert.AreEqual(a.degenerateFaces,b.degenerateFaces);Assert.AreEqual(a.outOfBoundsVertices,b.outOfBoundsVertices);
            Assert.AreEqual(a.pairAreaSum,b.pairAreaSum,"Exact predicates and summation order must be unchanged.");
            CollectionAssert.AreEqual(a.conflicts,b.conflicts);CollectionAssert.AreEqual(a.samples,b.samples);
        }

        [UnityTest]
        public IEnumerator ConservativeGpuPairsPreserveContactsBudgetsAndReportOrder()
        {
            var backend=Backend();
            var ordinary=Triangles(192);
            var duplicate=Triangles(32,true);
            var edges=new RemeshNative.Geometry {uv=new[] {Vector2.zero,Vector2.right,Vector2.up,Vector2.one,
                new Vector2(.3f,.7f),new Vector2(.7f,.3f),new Vector2(.30000004f,.69999999f)},
                indices=new[] {0,1,2,1,3,2,4,5,3,4,6,3,0,0,1},charts=new int[7]};
            var invalid=Triangles(12);
            invalid.uv[0]=new Vector2(float.NaN,0);invalid.uv[3]=new Vector2(0,float.PositiveInfinity);
            invalid.uv[6]=new Vector2(float.Epsilon,0);
            var wide=Triangles(24);
            for(int i=0;i<wide.uv.Length;++i) wide.uv[i]=(wide.uv[i]-Vector2.one*.5f)*1e20f;
            foreach(var geometry in new[] {ordinary,duplicate,edges,invalid,wide})
            foreach(long budget in new long[] {-1,0,5,2000})
            foreach(bool sameChart in new[] {false,true})
            {
                var uv=(Vector2[])geometry.uv.Clone();var indices=(int[])geometry.indices.Clone();
                var task=UvAtlasDiagnostics.MeasureGpuAsync(geometry,backend,default,budget,true,sameChart);
                while(!task.IsCompleted) yield return null;
                var actual=task.GetAwaiter().GetResult();Assert.IsTrue(actual.executed,actual.refusal);
                Equal(UvAtlasDiagnostics.Measure(geometry,default,sameChart,budget,true),actual.report);
                for(int i=0;i<uv.Length;++i)
                {
                    Assert.AreEqual(BitConverter.SingleToInt32Bits(uv[i].x),BitConverter.SingleToInt32Bits(geometry.uv[i].x));
                    Assert.AreEqual(BitConverter.SingleToInt32Bits(uv[i].y),BitConverter.SingleToInt32Bits(geometry.uv[i].y));
                }
                CollectionAssert.AreEqual(indices,geometry.indices);
            }
        }

        [UnityTest]
        public IEnumerator OverflowUsesTheCompleteCpuScan()
        {
            var geometry=Triangles(24,true);
            var task=UvAtlasDiagnostics.MeasureGpuAsync(geometry,Backend(1),default);
            while(!task.IsCompleted) yield return null;
            var actual=task.GetAwaiter().GetResult();
            Assert.IsFalse(actual.executed);Assert.That(actual.refusal,Does.Contain("overflow"));
            Equal(UvAtlasDiagnostics.Measure(geometry,default,collectConflicts:true),actual.report);
            Assert.AreEqual(276,actual.report.pairs);
        }

        [UnityTest]
        public IEnumerator SubmittedCancellationRetiresBuffersBeforeAnotherScan()
        {
            var backend=Backend();var geometry=Triangles(192);
            using var cancellation=new CancellationTokenSource();
            var cancelled=UvAtlasDiagnostics.MeasureGpuAsync(geometry,backend,cancellation.Token);
            cancellation.Cancel();
            while(!cancelled.IsCompleted) yield return null;
            Assert.Throws<OperationCanceledException>(()=>cancelled.GetAwaiter().GetResult());
            var next=UvAtlasDiagnostics.MeasureGpuAsync(geometry,backend,default);
            while(!next.IsCompleted) yield return null;
            var actual=next.GetAwaiter().GetResult();Assert.IsTrue(actual.executed,actual.refusal);
            Equal(UvAtlasDiagnostics.Measure(geometry,default,collectConflicts:true),actual.report);
        }

        [Serializable] sealed class Layout {public Vector2[] uv=Array.Empty<Vector2>();public int[] indices=Array.Empty<int>();}
        [Serializable] sealed class Row
        {
            public string source,refusal;
            public int round,triangles,candidates,pairs;
            public long comparisons;
            public bool executed;
            public double cpuMs,prepareMs,selectionMs,exactMs;
        }
        [Serializable] sealed class Measurements
        {
            public string gpu,api;
            public List<Row> cases=new List<Row>();
        }

        [UnityTest]
        public IEnumerator CapturedKamazEnvelopesAndCementWallLayoutsMatchTheFullCpuAudit()
        {
            string native=Environment.GetEnvironmentVariable("MESHLAB_GPU_PAIR_CAPTURE_ROOT");
            string layouts=Environment.GetEnvironmentVariable("MESHLAB_GPU_PAIR_LAYOUT_ROOT");
            string output=Environment.GetEnvironmentVariable("MESHLAB_GPU_PAIR_OUTPUT");
            if(string.IsNullOrEmpty(native)||string.IsNullOrEmpty(layouts)||string.IsNullOrEmpty(output))
                Assert.Ignore("Explicit private native-capture/layout/output directories are required.");
            var backend=Backend();
            var inputs=new List<(string name,RemeshNative.Geometry geometry)>();
            foreach(string file in Directory.GetFiles(native,"*.xatlaspack").OrderBy(p=>p,StringComparer.Ordinal))
                inputs.Add(("Kamaz/"+Path.GetFileName(file),ReadNative(file)));
            foreach(string file in Directory.GetFiles(layouts,"*.layout.json").OrderBy(p=>p,StringComparer.Ordinal))
            {
                var layout=JsonUtility.FromJson<Layout>(File.ReadAllText(file));
                inputs.Add(("CementWall/"+Path.GetFileName(file),new RemeshNative.Geometry {uv=layout.uv,
                    indices=layout.indices,charts=new int[layout.uv.Length]}));
            }
            Assert.AreEqual(25,inputs.Count,"All 22 native Kamaz requests and three CementWall LOD layouts must execute.");
            var measurements=new Measurements {gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString()};
            var warmup=UvAtlasDiagnostics.MeasureGpuAsync(Triangles(32),backend,default);
            while(!warmup.IsCompleted) yield return null;
            Assert.IsTrue(warmup.GetAwaiter().GetResult().executed);
            for(int round=1;round<=3;++round)
            foreach(var input in inputs)
            {
                var watch=Stopwatch.StartNew();
                UvAtlasDiagnostics.Report expected=null;double cpu=0;
                if(round%2==1)
                {
                    expected=UvAtlasDiagnostics.Measure(input.geometry,default,collectConflicts:true);
                    cpu=watch.Elapsed.TotalMilliseconds;
                }
                var task=UvAtlasDiagnostics.MeasureGpuAsync(input.geometry,backend,default);
                while(!task.IsCompleted) yield return null;
                var actual=task.GetAwaiter().GetResult();
                if(expected==null)
                {
                    watch.Restart();expected=UvAtlasDiagnostics.Measure(input.geometry,default,collectConflicts:true);
                    cpu=watch.Elapsed.TotalMilliseconds;
                }
                Equal(expected,actual.report);
                measurements.cases.Add(new Row {round=round,source=input.name,triangles=input.geometry.indices.Length/3,
                    cpuMs=cpu,prepareMs=actual.prepareMs,selectionMs=actual.selectionMs,exactMs=actual.exactMs,
                    executed=actual.executed,refusal=actual.refusal,candidates=actual.candidateCount,
                    comparisons=actual.report.comparisons,pairs=actual.report.pairs});
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output,"measurements.json"),JsonUtility.ToJson(measurements,true));
                Assert.IsTrue(actual.executed,input.name+": "+actual.refusal);
            }
        }

        static RemeshNative.Geometry ReadNative(string path)
        {
            using var reader=new BinaryReader(File.OpenRead(path));
            Assert.AreEqual(0x58525031u,reader.ReadUInt32());
            reader.ReadUInt32();reader.ReadSingle();reader.ReadUInt32();
            reader.ReadBoolean();reader.ReadBoolean();reader.ReadBoolean();
            float[] Floats()=>Enumerable.Range(0,reader.ReadInt32()).Select(_=>reader.ReadSingle()).ToArray();
            uint[] Integers()=>Enumerable.Range(0,reader.ReadInt32()).Select(_=>reader.ReadUInt32()).ToArray();
            var input=Floats();var indices=Integers();Integers();
            uint width=reader.ReadUInt32(),height=reader.ReadUInt32();reader.ReadUInt32();
            var references=Integers();var packed=Floats();Integers();
            int canvas=(int)Math.Max(16,Math.Max(width,height));
            var uv=new Vector2[input.Length/2];
            for(int i=0;i<references.Length;++i)
                uv[references[i]]=new Vector2(packed[i*2]*width,packed[i*2+1]*height)/canvas;
            return new RemeshNative.Geometry {uv=uv,indices=indices.Select(i=>(int)i).ToArray(),charts=new int[uv.Length]};
        }
    }
}
