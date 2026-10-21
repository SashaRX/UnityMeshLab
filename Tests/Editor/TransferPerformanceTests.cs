using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class TransferPerformanceTests
    {
        static Vector2[] Shape() => new[] { Vector2.zero, Vector2.right, Vector2.one,
            Vector2.zero, Vector2.one, Vector2.up };

        static Vector2[] Rectangle(float x,float y,float width,float height)
            =>Shape().Select(p=>new Vector2(x+p.x*width,y+p.y*height)).ToArray();

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator AsyncReversePackingMatchesSynchronousWithBothOverlapPolicies()
        {
            if(!ReverseUvRasterAcceleration.Available &&
                Environment.GetEnvironmentVariable("MESHLAB_REQUIRE_BURST_RASTER")=="1")
                Assert.Fail("Required Burst assembly did not register.");
            return CompareAsyncReversePackingWithBothOverlapPolicies();
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator AsyncReversePackingMatchesSynchronousWithoutOptionalProvider()
        {
            var previous=ReverseUvRasterAcceleration.evaluate;
            try
            {
                ReverseUvRasterAcceleration.evaluate=null;
                yield return CompareAsyncReversePackingWithBothOverlapPolicies();
            }
            finally {ReverseUvRasterAcceleration.evaluate=previous;}
        }

        static System.Collections.IEnumerator CompareAsyncReversePackingWithBothOverlapPolicies()
        {
            bool burstAvailable=ReverseUvRasterAcceleration.Available;
            var vertices=Enumerable.Range(0,81).Select(i=>new Vector3(i%9/8f,i/9/8f,0)).ToArray();
            var indices=Enumerable.Range(0,64).SelectMany(i=>{
                int a=i%8+i/8*9;return new[] {a,a+1,a+10,a,a+10,a+9};
            }).ToArray();
            var source=new Mesh {name="Grid",vertices=vertices,triangles=indices,
                uv2=vertices.Select(p=>new Vector2(p.x,p.y)*.25f).ToArray()};
            var target=UnityEngine.Object.Instantiate(source);
            try
            {
                var levels=new[] {
                    new ReverseUvTransfer.Level {lod=1,inputs=new[] {new ReverseUvTransfer.Input {mesh=source,key="Grid",usePreparedUv2=true}}},
                    new ReverseUvTransfer.Level {lod=0,inputs=new[] {new ReverseUvTransfer.Input {mesh=target,key="Grid",usePreparedUv2=true}}}};
                var before=new[] {source,target}.Select(TransferMeshSnapshot.Capture).ToArray();
                using var compiler=burstAvailable?ReverseUvRasterAcceleration.beginTestScope?.Invoke():null;
                foreach(bool overlap in new[] {false,true})
                {
                    ReverseUvTransfer.Options Options() => new ReverseUvTransfer.Options {seedResolution=128,seedDensity=32,
                        regroupPreparedAtlas=true,frozenShellCascade=!overlap,preserveProjectedOverlap=overlap,
                        repackFinalAtlas=true,fillFinalAtlas=true,burstFootprintRaster=true};
                    using var baseline=ReverseUvTransfer.Build(levels,Options()).GetAwaiter().GetResult();
                    var options=Options();options.performance=new TransferPerformanceProfile();
                    var task=ReverseUvTransfer.Build(levels,options,true);
                    while(!task.IsCompleted) yield return null;
                    using var actual=task.GetAwaiter().GetResult();
                    Assert.AreEqual(JsonUtility.ToJson(baseline.report),JsonUtility.ToJson(actual.report));
                    for(int l=0;l<levels.Length;++l)
                        CollectionAssert.AreEqual(TransferMeshSnapshot.Capture(baseline.meshes[l][0]),TransferMeshSnapshot.Capture(actual.meshes[l][0]));
                    // Worker threads cannot schedule jobs; an installed Burst
                    // provider must execute every qualifying mask by direct call.
                    long Count(string stage)=>options.performance.samples.Where(s=>s.stage==stage).Sum(s=>s.count);
                    long qualifying=Count("Burst raster qualifying triangles");
                    if(overlap) Assert.Greater(qualifying,0,"The shared-overlap fixture must exercise qualifying raster masks.");
                    Assert.AreEqual(burstAvailable?qualifying:0,Count("Burst footprint raster spans"),
                        burstAvailable?"Async final packing must execute qualifying masks through the Burst direct call: "+ReverseUvRasterAcceleration.LastRefusal:
                            "Without the optional provider, async packing must use the scalar fallback.");
                }
                CollectionAssert.AreEqual(before[0],TransferMeshSnapshot.Capture(source));
                CollectionAssert.AreEqual(before[1],TransferMeshSnapshot.Capture(target));
            }
            finally {UnityEngine.Object.DestroyImmediate(source);UnityEngine.Object.DestroyImmediate(target);}
        }

        [Test]
        public void BackgroundPackingUsesBurstDirectCallAndPreservesMainThreadExecution()
        {
            if(!ReverseUvRasterAcceleration.Available)
            {
                if(Environment.GetEnvironmentVariable("MESHLAB_REQUIRE_BURST_RASTER")=="1") Assert.Fail("Required Burst assembly did not register.");
                Assert.Ignore("Optional Burst/Collections packages are not installed.");
            }
            using var compiler=ReverseUvRasterAcceleration.beginTestScope?.Invoke();
            var points=Enumerable.Range(0,32).SelectMany(_=>Rectangle(1,1,8,8)).ToArray();
            // A worker never reaches the job API; it runs the direct call instead.
            var result=System.Threading.Tasks.Task.Run(()=>ReverseUvRasterAcceleration.TryRasterize(points,16,16,2,default))
                .GetAwaiter().GetResult();
            Assert.NotNull(result,"Background packing must execute the Burst direct call: "+ReverseUvRasterAcceleration.LastRefusal);
            Assert.IsTrue(result.burstExecuted);
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>System.Threading.Tasks.Task.Run(
                ()=>ReverseUvRasterAcceleration.TryRasterize(points,16,16,2,cancellation.Token)).GetAwaiter().GetResult());
            var main=ReverseUvRasterAcceleration.TryRasterize(points,16,16,2,default);
            Assert.NotNull(main,"A background call must keep main-thread Burst available.");
            Assert.IsTrue(main.burstExecuted);
            CollectionAssert.AreEqual(main.spans,result.spans,"Direct-call spans must equal the job's.");
        }

        [Test]
        public void ChartBoundsIndexKeepsEveryChartWithinReachInOrder()
        {
            var random=new System.Random(0x5eed);
            float Next() => (float)random.NextDouble();
            foreach(float scale in new[] {1f,100f,5000f})
            foreach(float reach in new[] {.05f,.5f})
            {
                // Degenerate (point) bounds and boxes of mixed size; queries cluster
                // around box faces, edges and corners near the reach boundary.
                var charts=Enumerable.Range(0,400).Select(i=>new ReverseUvTransfer.Chart {bounds=new Bounds(
                    new Vector3(Next(),Next(),Next())*scale,i%7==0?Vector3.zero:new Vector3(Next(),Next(),Next())*scale*.02f)}).ToList();
                var index=ReverseUvTransfer.ChartBoundsIndex.Create(charts,reach);
                Assert.NotNull(index);
                for(int q=0;q<4000;++q)
                {
                    var box=charts[random.Next(charts.Count)].bounds;
                    var lo=box.min-Vector3.one*reach*1.2f;var hi=box.max+Vector3.one*reach*1.2f;
                    var point=new Vector3(Mathf.Lerp(lo.x,hi.x,Next()),Mathf.Lerp(lo.y,hi.y,Next()),Mathf.Lerp(lo.z,hi.z,Next()));
                    var expected=Enumerable.Range(0,charts.Count).Where(i=>!(charts[i].bounds.SqrDistance(point)>reach*reach)).ToList();
                    var near=index.Near(point,new List<int>());
                    CollectionAssert.IsOrdered(near);
                    CollectionAssert.AreEqual(expected,near.Where(i=>!(charts[i].bounds.SqrDistance(point)>reach*reach)).ToList(),
                        $"scale {scale}, reach {reach}, point {point:R}");
                }
            }
            var finite=new List<ReverseUvTransfer.Chart> {new ReverseUvTransfer.Chart {bounds=new Bounds(Vector3.zero,Vector3.one)}};
            Assert.IsNull(ReverseUvTransfer.ChartBoundsIndex.Create(finite,float.PositiveInfinity),"Unbounded reach scans every chart.");
            Assert.IsNull(ReverseUvTransfer.ChartBoundsIndex.Create(new List<ReverseUvTransfer.Chart> {new ReverseUvTransfer.Chart {
                bounds=new Bounds(new Vector3(float.NaN,0,0),Vector3.one)}},.05f),"Non-finite bounds scan every chart.");
        }

        // Rows [first, last) of a curved grid over [inset, 2 - inset]². The donor is
        // cut into four charts around a hole; target faces start empty, every
        // seventh already inherited, and the outer ring lies beyond reach.
        static ReverseUvTransfer.Surface ProjectionGrid(bool donor,int cells,float inset,int first,int last)
        {
            var positions=new List<Vector3>();var pixels=new List<Vector2>();var faces=new List<ReverseUvTransfer.Face>();
            float size=(2-2*inset)/cells;
            for(int y=first;y<last;++y)
                for(int x=0;x<cells;++x)
                {
                    var o=new Vector2(inset+x*size,inset+y*size);var center=o+Vector2.one*size/2;
                    if(donor && center.x>.8f && center.x<1.1f && center.y>1.3f && center.y<1.5f) continue;
                    var quad=new[] {o,o+new Vector2(size,0),o+Vector2.one*size,o+new Vector2(0,size)};
                    foreach(int corner in new[] {0,1,2,0,2,3})
                    {
                        var p=quad[corner];
                        positions.Add(new Vector3(p.x,p.y,.05f*Mathf.Sin(p.x*2)*Mathf.Cos(p.y*2)+(donor?0:.002f)));
                        pixels.Add(donor?p*100:Vector2.zero);
                    }
                    int chart=(center.x<1?0:1)+(center.y<1?0:2);
                    for(int k=0;k<2;++k)
                        faces.Add(donor?new ReverseUvTransfer.Face {chart=chart,coordinateDomain=chart}
                            :new ReverseUvTransfer.Face {inherited=faces.Count%7==3,chart=9});
                }
            return new ReverseUvTransfer.Surface {lod=donor?0:1,node=first,orientation=1,positions=positions.ToArray(),
                indices=Enumerable.Range(0,positions.Count).ToArray(),pixels=pixels.ToArray(),faces=faces.ToArray()};
        }

        [Test]
        public void ParallelFaceProjectionMatchesTheOrderedPassIncludingBudgetFailures()
        {
            var donor=new[] {ProjectionGrid(true,24,0,0,24)};
            // An empty surface between two halves crosses every surface boundary.
            ReverseUvTransfer.Surface[] Targets()=>new[] {ProjectionGrid(false,17,-.15f,0,9),
                ProjectionGrid(false,17,-.15f,9,9),ProjectionGrid(false,17,-.15f,9,17)};
            ReverseUvTransfer.Options Options(long budget)=>new ReverseUvTransfer.Options {comparisonBudget=budget,
                queryBackend=ReverseUvQueryBackend.Mode.Managed};
            var fields=typeof(ReverseUvTransfer.Face).GetFields(System.Reflection.BindingFlags.Instance
                |System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            void AssertSameState(ReverseUvTransfer.Surface[] expected,ReverseUvTransfer.Surface[] actual,string label)
            {
                for(int s=0;s<expected.Length;++s)
                {
                    CollectionAssert.AreEqual(expected[s].pixels,actual[s].pixels,$"{label}: surface {s} pixels");
                    for(int f=0;f<expected[s].faces.Length;++f)
                        foreach(var field in fields)
                            Assert.AreEqual(field.GetValue(expected[s].faces[f]),field.GetValue(actual[s].faces[f]),
                                $"{label}: surface {s} face {f} {field.Name}");
                }
            }
            (ReverseUvTransfer.Surface[] targets,long queries,string error) Run(int minimum,long budget)
            {
                var targets=Targets();long queries=5;
                ReverseUvTransfer.ParallelProjectionMinimumFaces=minimum;
                try {ReverseUvTransfer.ProjectFaces(donor,targets,ReverseUvTransfer.Charts(donor),Options(budget),ref queries,CancellationToken.None);}
                catch(InvalidOperationException error) {return (targets,-1,error.Message);}
                return (targets,queries,null);
            }
            int saved=ReverseUvTransfer.ParallelProjectionMinimumFaces;
            try
            {
                var baseline=Run(int.MaxValue,long.MaxValue/2);
                var records=baseline.targets.SelectMany(s=>s.faces).ToArray();
                Assert.Greater(records.Count(f=>f.inherited && f.chart!=9),0,"The fixture must inherit faces.");
                Assert.Greater(records.Count(f=>f.rejection!=null),0,"The fixture must reject faces.");
                long total=baseline.queries;
                foreach(long budget in new[] {long.MaxValue/2,total,total-1,total/2,40})
                {
                    var ordered=Run(int.MaxValue,budget);var parallel=Run(0,budget);
                    Assert.AreEqual(budget<total,ordered.error!=null,$"budget {budget} of {total}");
                    Assert.AreEqual(ordered.error,parallel.error,$"budget {budget}");
                    Assert.AreEqual(ordered.queries,parallel.queries,$"budget {budget}");
                    AssertSameState(ordered.targets,parallel.targets,$"budget {budget}");
                }
                ReverseUvTransfer.ParallelProjectionMinimumFaces=0;
                foreach(long budget in new[] {total,total-1,40})
                {
                    var targets=Targets();long queries=5;
                    var projector=new ReverseUvTransfer.FaceProjector(donor,ReverseUvTransfer.Charts(donor),Options(budget),CancellationToken.None);
                    var counters=projector.ProjectInParallel(targets,ref queries);
                    if(budget==total)
                    {
                        Assert.NotNull(counters,"A pass within the budget must not fall back.");
                        Assert.AreEqual(total,queries);
                        AssertSameState(baseline.targets,targets,"parallel");
                        continue;
                    }
                    Assert.IsNull(counters,$"budget {budget}: an exhausted budget repeats the pass in order.");
                    Assert.AreEqual(5,queries);
                    AssertSameState(Targets(),targets,$"budget {budget} restored");
                }
                using var cancellation=new CancellationTokenSource();
                var cancelled=new ReverseUvTransfer.FaceProjector(donor,ReverseUvTransfer.Charts(donor),Options(total),cancellation.Token);
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(()=>{long queries=5;cancelled.ProjectInParallel(Targets(),ref queries);});
            }
            finally {ReverseUvTransfer.ParallelProjectionMinimumFaces=saved;}
        }

        [Test]
        public void IndexedParentCoverageMatchesTheFullScan()
        {
            var random=new System.Random(0xc0fe);
            float Next() => (float)random.NextDouble();
            int saved=ReverseUvTransfer.CoverageIndexMinimumFaces;
            try
            {
                for(int trial=0;trial<40;++trial)
                {
                    // A grid of parent triangles over two layers, with shared edges,
                    // plus scattered slivers; queries straddle cells, edges and gaps.
                    int n=4+trial%9;float cell=8;var pixels=new List<Vector2>();var layers=new List<int>();
                    for(int y=0;y<n;++y) for(int x=0;x<n;++x)
                    {
                        var o=new Vector2(x,y)*cell;int layer=(x+y+trial)%2;
                        pixels.AddRange(new[] {o,o+new Vector2(cell,0),o+new Vector2(cell,cell)});layers.Add(layer);
                        pixels.AddRange(new[] {o,o+new Vector2(cell,cell),o+new Vector2(0,cell)});layers.Add(layer);
                    }
                    for(int s=0;s<20;++s)
                    {
                        var o=new Vector2(Next(),Next())*n*cell;
                        pixels.AddRange(new[] {o,o+new Vector2(Next()*3,0),o+new Vector2(0,Next()*.01f)});layers.Add(s%2);
                    }
                    ReverseUvTransfer.Chart Parent() => new ReverseUvTransfer.Chart {pixels=pixels.ToArray(),layers=layers.ToArray()};
                    for(int q=0;q<60;++q)
                    {
                        var a=new Vector2(Next(),Next())*n*cell;
                        var query=new[] {a,a+new Vector2(Next()*cell*2-cell,Next()*cell*2-cell),a+new Vector2(Next()*cell*2-cell,Next()*cell*2-cell)};
                        int layer=q%2;long budget=q%10==0?pixels.Count/6:long.MaxValue/2;
                        var options=new ReverseUvTransfer.Options {comparisonBudget=budget,boundaryToleranceTexels=.01f};
                        (bool? covered,long comparisons,string error) Run(int minimum)
                        {
                            ReverseUvTransfer.CoverageIndexMinimumFaces=minimum;long comparisons=3;
                            try {return (ReverseUvTransfer.CoveredByParentLayer(Parent(),layer,query,0,options,ref comparisons,CancellationToken.None),comparisons,null);}
                            catch(InvalidOperationException error) {return (null,comparisons,error.Message);}
                        }
                        var scan=Run(int.MaxValue);var indexed=Run(1);
                        Assert.AreEqual(scan.covered,indexed.covered,$"trial {trial} query {q}");
                        Assert.AreEqual(scan.error,indexed.error,$"trial {trial} query {q}");
                        if(scan.error==null) Assert.AreEqual(scan.comparisons,indexed.comparisons,$"trial {trial} query {q}");
                    }
                }
            }
            finally {ReverseUvTransfer.CoverageIndexMinimumFaces=saved;}
        }

        [Test]
        public void BurstRasterSpansMatchScalarEdgesIncludingClippingAndRoundoff()
        {
            if(!ReverseUvRasterAcceleration.Available)
            {
                if(Environment.GetEnvironmentVariable("MESHLAB_REQUIRE_BURST_RASTER")=="1") Assert.Fail("Required Burst assembly did not register.");
                Assert.Ignore("Optional Burst/Collections packages are not installed.");
            }
            var random=new System.Random(0x6bc2);
            var preferenceKeys=new[] {"BurstCompilation","BurstCompileSynchronously","BurstShowTimings","BurstDebug","BurstForceSafetyChecks"};
            var preferences=preferenceKeys.Select(key=>(UnityEditor.EditorPrefs.HasKey(key),UnityEditor.EditorPrefs.GetBool(key))).ToArray();
            using var compiler=ReverseUvRasterAcceleration.beginTestScope?.Invoke();
            for(int trial=0;trial<48;++trial)
            {
                int w=16+trial%49,h=16+trial%31;double radius=1+(trial%9)*.25;
                var points=Enumerable.Range(0,192).Select(i=>new Vector2(
                    (float)(random.NextDouble()*w*1.4-w*.2),(float)(random.NextDouble()*h*1.4-h*.2))).ToArray();
                // Horizontal, vertical and almost horizontal edges exercise
                // exact comparisons near scan-line boundaries.
                points[0]=new Vector2(0,4);points[1]=new Vector2(8,4);points[2]=new Vector2(8,4.0000005f);
                points[3]=new Vector2(3,0);points[4]=new Vector2(3,8);points[5]=new Vector2(3.0000002f,8);
                var expected=new System.Collections.Generic.List<ReverseUvRasterAcceleration.Span>();
                for(int t=0;t<points.Length;t+=3)
                {
                    var triangle=points.Skip(t).Take(3).ToArray();
                    int lo=Math.Max(0,(int)Math.Floor(triangle.Min(p=>p.y)-radius));
                    int hi=Math.Min(h,(int)Math.Ceiling(triangle.Max(p=>p.y)+radius));
                    for(int y=lo;y<hi;++y)
                    {
                        double bottom=y-radius,top=y+1+radius,left=double.MaxValue,right=double.MinValue;
                        foreach(var p in triangle) if(p.y>=bottom && p.y<=top) {left=Math.Min(left,p.x);right=Math.Max(right,p.x);}
                        for(int e=0;e<3;++e)
                        {
                            var a=triangle[e];var b=triangle[(e+1)%3];double rise=(double)b.y-a.y;if(rise==0) continue;
                            foreach(double v in new[] {bottom,top})
                            {
                                double fraction=(v-a.y)/rise;if(fraction<0 || fraction>1) continue;
                                double x=a.x+((double)b.x-a.x)*fraction;left=Math.Min(left,x);right=Math.Max(right,x);
                            }
                        }
                        var span=new ReverseUvRasterAcceleration.Span {row=y};
                        if(left<=right) {span.start=Math.Max(0,(int)Math.Floor(left-radius));span.end=Math.Min(w,(int)Math.Ceiling(right+radius));}
                        expected.Add(span);
                    }
                }
                var actual=ReverseUvRasterAcceleration.TryRasterize(points,w,h,radius,default);
                Assert.NotNull(actual,"Configured provider must execute Burst: "+ReverseUvRasterAcceleration.LastRefusal);
                Assert.IsTrue(actual.burstExecuted);CollectionAssert.AreEqual(expected,actual.spans,$"trial {trial}");
                // Asynchronous Reverse packs on a worker, where jobs cannot be scheduled.
                var worker=System.Threading.Tasks.Task.Run(()=>ReverseUvRasterAcceleration.TryRasterize(points,w,h,radius,default));
                Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(60)),"Worker raster did not finish.");
                Assert.NotNull(worker.Result,"Worker threads must execute the Burst direct call: "+ReverseUvRasterAcceleration.LastRefusal);
                Assert.IsTrue(worker.Result.burstExecuted);CollectionAssert.AreEqual(expected,worker.Result.spans,$"worker trial {trial}");
            }
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>ReverseUvRasterAcceleration.TryRasterize(
                Enumerable.Repeat(Vector2.one,192).ToArray(),16,16,2,cancellation.Token));
            compiler?.Dispose();
            CollectionAssert.AreEqual(preferences,preferenceKeys.Select(key=>(UnityEditor.EditorPrefs.HasKey(key),UnityEditor.EditorPrefs.GetBool(key))).ToArray(),
                "Scoped Burst testing must preserve user EditorPrefs, including missing keys.");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(20)] [TestCase(32768)]
        public void CollisionRowIndexPreservesFirstHitAndLogicalWorkWithPartialPrefixes(int effort)
        {
            var random=new System.Random(0x6bc2);
            var rows=Enumerable.Range(0,96).Select(_=>new System.Collections.Generic.List<(int start,int end)>()).ToArray();
            var index=new ReverseUvFootprintPacking.CollisionRowIndex(63);
            for(int trial=0;trial<60;++trial)
            {
                foreach(var row in rows)
                {
                    row.Clear();int x=0;
                    while(x<90) {x+=random.Next(1,8);int end=Math.Min(96,x+random.Next(1,12));row.Add((x,end));x=end;}
                }
                var spans=Enumerable.Range(0,24).Select(i=>{
                    int start=random.Next(0,12);return (row:i/3,start,end:start+random.Next(1,10));
                }).ToArray();
                int y=trial%12,min=trial%3==0?0:trial%3==1?7:63;
                index.Build(spans,rows,y,min,63,default,effort,trial%2==0?8:128);
                for(int x=min;x<=63;++x)
                {
                    (int next,int visits) Reference(int first)
                    {
                        for(int s=first;s<spans.Length;++s)
                            foreach(var occupied in rows[y+spans[s].row])
                                if(occupied.end>x+spans[s].start && occupied.start<x+spans[s].end)
                                    return (Math.Max(x+1,occupied.end-spans[s].start),s+1);
                        return (0,spans.Length);
                    }
                    var expected=Reference(0);
                    var actual=index.TryCollision(x,out int next,out int visits)?(next,visits):Reference(index.completeSpans);
                    Assert.AreEqual(expected,actual,$"trial {trial}, x {x}, prefix {index.completeSpans}");
                }
            }
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>index.Build(new[] {(row:0,start:0,end:1)},rows,0,0,63,cancellation.Token));
        }

        [Test]
        public void IndexedLogicalWorkStopsAtTheSameOverBudgetSpan()
        {
            foreach(long initial in new[] {0L,127999998L,128000000L,128000099L})
                foreach(int visits in new[] {0,1,4,200})
                {
                    long expected=initial,actual=initial;bool complete=true;
                    for(int s=0;s<visits;++s) if(++expected>128000000) {complete=false;break;}
                    Assert.AreEqual(complete,ReverseUvFootprintPacking.ConsumeSearchWork(visits,ref actual));
                    Assert.AreEqual(expected,actual);
                }
        }

        [Test]
        public void CollisionRowIndexSkipsEmptyPrefixesAndKeepsLateHitPriority()
        {
            var rows=Enumerable.Range(0,384).Select(_=>new System.Collections.Generic.List<(int start,int end)>()).ToArray();
            rows[299].Add((20,25));rows[299].Add((30,31));rows[315].Add((5,15));rows[315].Add((23,29));
            var spans=Enumerable.Range(0,320).Select(row=>(row,start:1,end:3)).ToArray();
            var index=new ReverseUvFootprintPacking.CollisionRowIndex(63);
            index.Build(spans,rows,0,0,63,default);
            Assert.AreEqual(320,index.completeSpans);
            Assert.IsTrue(index.TryCollision(20,out int next,out int visits));
            Assert.AreEqual(24,next);Assert.AreEqual(300,visits);
            Assert.IsTrue(index.TryCollision(5,out next,out visits));
            Assert.AreEqual(14,next);Assert.AreEqual(316,visits);
            Assert.IsFalse(index.TryCollision(63,out _,out _));
            long work=127999710;
            Assert.IsFalse(ReverseUvFootprintPacking.ConsumeSearchWork(300,ref work));
            Assert.AreEqual(128000001,work);
        }

        [TestCase(16, false, false, false)] [TestCase(16, true, false, false)]
        [TestCase(260, false, false, false)] [TestCase(260, true, false, false)]
        [TestCase(16, false, true, false)] [TestCase(16, true, true, false)]
        [TestCase(260, false, true, false)] [TestCase(260, true, true, false)]
        [TestCase(16, false, false, true)] [TestCase(16, true, false, true)]
        [TestCase(260, false, false, true)] [TestCase(260, true, false, true)]
        [TestCase(16, false, true, true)] [TestCase(16, true, true, true)]
        [TestCase(260, false, true, true)] [TestCase(260, true, true, true)]
        public void IndexedSearchPreservesAllCandidatesRefusalsAndWorkBudgets(int count,bool rotate,bool collisionRows,bool native)
        {
            using var library=native?NativeFootprintTestLibrary.OpenFromEnvironment():null;
            if(native && library==null) Assert.Ignore("Optional private native search build is not configured.");
            // Multi-span ring rows exercise backwards queries when moving
            // from a later span to the first span at the next x position.
            var ring=Rectangle(0,0,20,2).Concat(Rectangle(0,18,20,2))
                .Concat(Rectangle(0,2,2,16)).Concat(Rectangle(18,2,2,16)).ToArray();
            var source=Enumerable.Range(0,count).Select(id=>id%17==0?ring.ToArray()
                :Rectangle(0,0,3+id%5,2+id%7)).ToArray();
            var original=source.Select(p=>p.ToArray()).ToArray();
            var options=new ReverseUvTransfer.Options {padding=2,rotateCharts=rotate};
            var baseline=ReverseUvFamilyPacking.Pack(source,options,default,out int side,out float scale);
            var before=baseline.Select(p=>p.ToArray()).ToArray();
            Vector2[][] Run(bool indexed,out ReverseUvFootprintPacking.Diagnostics diagnostics,
                out System.Collections.Generic.List<(int group,int canvas,Vector2[] points)> trace,out float density,out bool improved)
            {
                var calls=new System.Collections.Generic.List<(int,int,Vector2[])>();
                diagnostics=new ReverseUvFootprintPacking.Diagnostics();
                options.indexFootprintSearch=indexed;options.fillFinalAtlas=true;options.cacheCollisionRows=collisionRows;
                options.nativeFootprintSearch=indexed && native;options.footprintSearchApi=library?.api;
                options.reuseOccupancySnapshots=collisionRows;
                bool Valid(int group,Vector2[] points,int canvas)
                {
                    calls.Add((group,canvas,points.ToArray()));
                    // Keep one family immovable and force some phase retries.
                    return group==0?points.SequenceEqual(before[0])
                        :points.Min(p=>p.x)%1!=0 || (int)points.Min(p=>p.y)%3!=0;
                }
                var result=ReverseUvFootprintPacking.Improve(source,baseline,side,scale,options,default,Valid,
                    out density,out improved,diagnostics,testNative:false);
                trace=calls;return result;
            }
            var reference=Run(false,out var slow,out var slowTrace,out float slowDensity,out bool slowImproved);
            var indexed=Run(true,out var fast,out var fastTrace,out float fastDensity,out bool fastImproved);
            Assert.Greater(slowTrace.Count,20);
            Assert.AreEqual(slowDensity,fastDensity);Assert.AreEqual(slowImproved,fastImproved);
            Assert.AreEqual(slow.tighteningWork,fast.tighteningWork);Assert.AreEqual(slow.repackingWork,fast.repackingWork);
            Assert.AreEqual(slow.validations,fast.validations);Assert.AreEqual(slow.completedOrders,fast.completedOrders);
            Assert.AreEqual(slow.retainedFamilies,fast.retainedFamilies);CollectionAssert.AreEqual(slow.refusedOrders,fast.refusedOrders);
            Assert.AreEqual(slowTrace.Count,fastTrace.Count);
            for(int i=0;i<slowTrace.Count;++i)
            {
                Assert.AreEqual(slowTrace[i].group,fastTrace[i].group);Assert.AreEqual(slowTrace[i].canvas,fastTrace[i].canvas);
                CollectionAssert.AreEqual(slowTrace[i].points,fastTrace[i].points);
            }
            for(int group=0;group<count;++group)
            {
                CollectionAssert.AreEqual(reference[group],indexed[group]);
                CollectionAssert.AreEqual(original[group],source[group]);CollectionAssert.AreEqual(before[group],baseline[group]);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedOccupancyKeepsItsOwnRowsAndSearchesOutliveTheSnapshot(bool releaseFirst)
        {
            using var library=NativeFootprintTestLibrary.OpenFromEnvironment();
            if(library==null) Assert.Ignore("Optional private native search build is not configured.");
            var rows=Enumerable.Range(0,8).Select(_=>new System.Collections.Generic.List<(int start,int end)>()).ToArray();
            rows[0].Add((0,3));
            using var snapshot=ReverseUvOccupancySearch.Snapshot.TryCapture(rows,default,null,library.api);
            Assert.NotNull(snapshot);
            using var first=snapshot.CreateSearch(2,1,new[] {(row:0,start:0,end:1)},rows,default);
            using var second=snapshot.CreateSearch(2,1,new[] {(row:0,start:1,end:2)},rows,default);
            Assert.NotNull(first);Assert.NotNull(second);rows[0].Clear();
            if(releaseFirst) snapshot.Dispose();
            long a=0,b=0;
            Assert.IsTrue(first.Next(int.MaxValue,ref a,default,out _));Assert.AreEqual(3,first.x);Assert.AreEqual(2,a);
            Assert.IsTrue(second.Next(int.MaxValue,ref b,default,out _));Assert.AreEqual(2,second.x);Assert.AreEqual(2,b);
            using var fresh=ReverseUvOccupancySearch.Snapshot.TryCapture(rows,default,null,library.api);
            using var next=fresh.CreateSearch(2,1,new[] {(row:0,start:0,end:1)},rows,default);long c=0;
            Assert.IsTrue(next.Next(int.MaxValue,ref c,default,out _));Assert.AreEqual(0,next.x);Assert.AreEqual(1,c);
            snapshot.Dispose();snapshot.Dispose();
            Assert.Throws<ObjectDisposedException>(()=>snapshot.CreateSearch(2,1,new[] {(row:0,start:0,end:1)},rows,default));
        }

        [Test]
        public void NativePackingBindingsRestoreNestedScopesAndRefuseActiveAtlases()
        {
            var first=new XatlasNative.Api {getAtlasWidth=()=>123};
            var second=new XatlasNative.Api {getAtlasWidth=()=>456};
            using var outer=XatlasNative.UseForTests(first);Assert.AreEqual(123,XatlasNative.xatlasGetAtlasWidth());
            using(var inner=XatlasNative.UseForTests(second)) {Assert.AreEqual(456,XatlasNative.xatlasGetAtlasWidth());}
            Assert.AreEqual(123,XatlasNative.xatlasGetAtlasWidth());
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            try {Assert.Throws<InvalidOperationException>(()=>XatlasNative.UseForTests(second));}
            finally {XatlasRepack.ReleaseNativeSession();}
            Assert.AreEqual(123,XatlasNative.xatlasGetAtlasWidth());
        }

        [Test]
        public void NativeSearchYieldsForCancellationAndDestroysExactlyOnce()
        {
            var rows=Enumerable.Range(0,8).Select(_=>new System.Collections.Generic.List<(int start,int end)>()).ToArray();
            using var cancellation=new CancellationTokenSource();int advances=0,destroys=0;
            var api=new ReverseUvOccupancySearch.Api {
                create=(side,width,height,spans,spanInts,offsets,offsetInts,occupied,occupiedInts)=>new IntPtr(123),
                advance=(IntPtr handle,ref int x,ref int y,int best,ref long work,int chunk)=>{
                    ++advances;work+=chunk;cancellation.Cancel();return 2;},
                destroy=handle=>{Assert.AreEqual(new IntPtr(123),handle);++destroys;}
            };
            var search=ReverseUvOccupancySearch.TryCreate(2,2,new[] {(row:0,start:0,end:1)},rows,default,api);
            Assert.NotNull(search);long work=0;
            Assert.Throws<OperationCanceledException>(()=>search.Next(int.MaxValue,ref work,cancellation.Token,out _));
            search.Dispose();search.Dispose();Assert.AreEqual(1,advances);Assert.AreEqual(1,destroys);
            Assert.Throws<ObjectDisposedException>(()=>search.Next(int.MaxValue,ref work,default,out _));
        }

        [TestCase(false)] [TestCase(true)]
        public void PackingCancellationClosesSearchProfiling(bool indexed)
        {
            var source=new[] {Rectangle(0,0,20,8),Rectangle(0,0,8,8)};
            var options=new ReverseUvTransfer.Options {padding=2,indexFootprintSearch=indexed,performance=new TransferPerformanceProfile()};
            var baseline=ReverseUvFamilyPacking.Pack(source,options,default,out int side,out float scale);
            using var cancellation=new CancellationTokenSource();
            int audits=0;
            Assert.Throws<OperationCanceledException>(()=>ReverseUvFootprintPacking.Improve(source,baseline,side,scale,options,
                cancellation.Token,(group,points,canvas)=>{if(++audits==5) cancellation.Cancel();return false;},out _,out _));
            Assert.GreaterOrEqual(audits,5);Assert.LessOrEqual(audits,64);
            Assert.AreEqual(1,options.performance.samples.Count(s=>s.stage=="Footprint candidate search"));
            Assert.AreEqual(1,options.performance.samples.Count(s=>s.stage=="Footprint placement audits"));
        }

        [Test]
        public void ProfilingPreservesInvalidInputErrorsAndClosesFailedScopes()
        {
            Assert.Throws<ArgumentException>(() => ReverseUvTransfer.Build(null, null).GetAwaiter().GetResult());
            var profile = new TransferPerformanceProfile();
            Assert.Throws<ArgumentException>(() => ReverseUvTransfer.Build(null,
                new ReverseUvTransfer.Options {performance=profile}).GetAwaiter().GetResult());
            Assert.AreEqual(1,profile.samples.Count);
            Assert.AreEqual("Reverse total",profile.samples[0].stage);
        }

        [Test]
        public void FootprintCacheSeparatesPoliciesAndHonoursCancellationOnHits()
        {
            var shape = Shape();
            using var cache = new ReverseUvNativeFamilyPacking.FootprintCache(true);
            var first = cache.Get(shape, default, true, true);
            Assert.AreSame(first, cache.Get(shape, default, true, true));
            Assert.AreNotSame(first, cache.Get(shape, default, false, true));
            Assert.AreNotSame(first, cache.Get(shape, default, true, false));
            using var nextPack = new ReverseUvNativeFamilyPacking.FootprintCache(true);
            CollectionAssert.AreEqual(first, nextPack.Get(shape, default, true, true));
            Assert.AreNotSame(first, nextPack.Get(shape, default, true, true));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => cache.Get(shape, cancellation.Token, true, true));
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void CachedReverseIsByteIdenticalWithBothOverlapAndFillPolicies(bool overlap, bool fill)
        {
            var a = new Mesh { name = "Panel", vertices = new[] {Vector3.zero, Vector3.right, new Vector3(1,1), Vector3.up},
                triangles = new[] {0,1,2,0,2,3}, uv2 = Shape().Take(3).Concat(new[] {Vector2.up}).Select(p => p * .25f).ToArray() };
            var b = UnityEngine.Object.Instantiate(a);
            try
            {
                var levels = new[] {new ReverseUvTransfer.Level {lod=1,inputs=new[] {new ReverseUvTransfer.Input {mesh=a,key="Panel",usePreparedUv2=true}}},
                    new ReverseUvTransfer.Level {lod=0,inputs=new[] {new ReverseUvTransfer.Input {mesh=b,key="Panel",usePreparedUv2=true}}}};
                var before = new[] {a,b}.Select(TransferMeshSnapshot.Capture).ToArray();
                var profile = new TransferPerformanceProfile();
                ReverseUvTransfer.Options Options(bool cache) => new ReverseUvTransfer.Options {seedResolution=128,seedDensity=32,
                    regroupPreparedAtlas=true,frozenShellCascade=!overlap,preserveProjectedOverlap=overlap,repackFinalAtlas=true,
                    fillFinalAtlas=fill,cachePackingFootprints=cache,cacheNativeCharts=cache,performance=cache?profile:null};
                using var baseline = ReverseUvTransfer.Build(levels,Options(false)).GetAwaiter().GetResult();
                using var cached = ReverseUvTransfer.Build(levels,Options(true)).GetAwaiter().GetResult();
                Assert.AreEqual(JsonUtility.ToJson(baseline.report),JsonUtility.ToJson(cached.report));
                for(int l=0;l<levels.Length;++l)
                    CollectionAssert.AreEqual(TransferMeshSnapshot.Capture(baseline.meshes[l][0]),TransferMeshSnapshot.Capture(cached.meshes[l][0]));
                CollectionAssert.AreEqual(before[0],TransferMeshSnapshot.Capture(a));
                CollectionAssert.AreEqual(before[1],TransferMeshSnapshot.Capture(b));
                Assert.IsTrue(profile.samples.Any(s=>s.stage=="Reverse total" && s.milliseconds>0));
                Assert.IsTrue(profile.samples.Any(s=>s.stage=="Packing footprint construction" && s.count>0));
                if(fill) Assert.IsTrue(profile.samples.Any(s=>s.stage=="Packing footprint cache hits" && s.count>0));
                if(!overlap && fill) Assert.IsTrue(profile.samples.Any(s=>s.stage=="Native chart cache hits" && s.count>0));
            }
            finally {UnityEngine.Object.DestroyImmediate(a);UnityEngine.Object.DestroyImmediate(b);}
        }

    }
}
