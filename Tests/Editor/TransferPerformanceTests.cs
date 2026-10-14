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
                using var compiler=ReverseUvRasterAcceleration.beginTestScope?.Invoke();
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
                    // Worker threads cannot schedule jobs; every qualifying mask
                    // must still run through the Burst direct call there.
                    long Count(string stage)=>options.performance.samples.Where(s=>s.stage==stage).Sum(s=>s.count);
                    Assert.AreEqual(Count("Burst raster qualifying triangles"),Count("Burst footprint raster spans"),
                        "Async final packing must execute qualifying masks through the Burst direct call: "+ReverseUvRasterAcceleration.LastRefusal);
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
                    fillFinalAtlas=fill,cachePackingFootprints=cache,performance=cache?profile:null};
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
            }
            finally {UnityEngine.Object.DestroyImmediate(a);UnityEngine.Object.DestroyImmediate(b);}
        }

        [TestCase(false)] [TestCase(true)]
        public void ParallelFamilyValidationIsByteIdenticalWithBothOverlapPolicies(bool overlap)
        {
            // Separate quads with their own UV2 islands: several packing families.
            var vertices = new List<Vector3>(); var uv2 = new List<Vector2>(); var triangles = new List<int>();
            for(int i=0;i<6;++i)
            {
                int start=vertices.Count;float size=.06f+.02f*i;var corner=new Vector2(.05f+.15f*i,.1f+.12f*(i%3));
                vertices.AddRange(new[] {new Vector3(2*i,0,0),new Vector3(2*i+1,0,0),new Vector3(2*i+1,1+i*.25f,0),new Vector3(2*i,1+i*.25f,0)});
                uv2.AddRange(new[] {corner,corner+new Vector2(size,0),corner+new Vector2(size,size*(1+i*.25f)),corner+new Vector2(0,size*(1+i*.25f))});
                triangles.AddRange(new[] {start,start+1,start+2,start,start+2,start+3});
            }
            var a = new Mesh { name = "Panels", vertices = vertices.ToArray(), triangles = triangles.ToArray(), uv2 = uv2.ToArray() };
            var b = UnityEngine.Object.Instantiate(a);
            try
            {
                var levels = new[] {new ReverseUvTransfer.Level {lod=1,inputs=new[] {new ReverseUvTransfer.Input {mesh=a,key="Panels",usePreparedUv2=true}}},
                    new ReverseUvTransfer.Level {lod=0,inputs=new[] {new ReverseUvTransfer.Input {mesh=b,key="Panels",usePreparedUv2=true}}}};
                var before = new[] {a,b}.Select(TransferMeshSnapshot.Capture).ToArray();
                var profile = new TransferPerformanceProfile();
                ReverseUvTransfer.Options Options(bool parallel) => new ReverseUvTransfer.Options {seedResolution=128,seedDensity=32,
                    regroupPreparedAtlas=true,frozenShellCascade=!overlap,preserveProjectedOverlap=overlap,repackFinalAtlas=true,
                    fillFinalAtlas=true,parallelFamilyValidation=parallel,performance=parallel?profile:null};
                using var sequential = ReverseUvTransfer.Build(levels,Options(false)).GetAwaiter().GetResult();
                using var parallel = ReverseUvTransfer.Build(levels,Options(true)).GetAwaiter().GetResult();
                Assert.AreEqual(JsonUtility.ToJson(sequential.report),JsonUtility.ToJson(parallel.report));
                for(int l=0;l<levels.Length;++l)
                    CollectionAssert.AreEqual(TransferMeshSnapshot.Capture(sequential.meshes[l][0]),TransferMeshSnapshot.Capture(parallel.meshes[l][0]));
                CollectionAssert.AreEqual(before[0],TransferMeshSnapshot.Capture(a));
                CollectionAssert.AreEqual(before[1],TransferMeshSnapshot.Capture(b));
                if(profile.samples.Any(s=>s.stage=="Native chart placement" && s.count>0))
                    Assert.IsTrue(profile.samples.Any(s=>s.stage=="Native family parallel validation" && s.count>0),
                        "Multi-family native trials must validate their families concurrently.");
            }
            finally {UnityEngine.Object.DestroyImmediate(a);UnityEngine.Object.DestroyImmediate(b);}
        }
    }
}
