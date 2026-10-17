using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ReverseUvAlignedPackingTests
    {
        static Vector2[] Rectangle(float x,float y,float width,float height)=>new[] {
            new Vector2(x,y),new Vector2(x+width,y),new Vector2(x,y+height),
            new Vector2(x+width,y),new Vector2(x+width,y+height),new Vector2(x,y+height)};
        static Vector2[] Turn(Vector2[] points,double angle)
            =>points.Select(p=>new Vector2((float)(p.x*Math.Cos(angle)-p.y*Math.Sin(angle)),
                (float)(p.x*Math.Sin(angle)+p.y*Math.Cos(angle)))).ToArray();

        [Test] public void FinestAxisMovesEveryLodWithOneRigidRotation()
        {
            var finest=Turn(Rectangle(0,0,8,3),.31);var coarse=Turn(Rectangle(0,0,10,4),.31);
            var family=finest.Concat(coarse).ToArray();var snapshot=(Vector2[])family.Clone();
            var result=ReverseUvFamilyAxis.Rotate(family,finest,default);
            CollectionAssert.AreEqual(snapshot,family);
            Assert.Less(Math.Abs(result[1].y-result[0].y),.00001);
            Assert.Less(Math.Abs(result[2].x-result[0].x),.00001);
            AssertSameEdges(family,result);
        }

        [Test] public void FootprintsUseRingHoleWithoutScalingOrGrowingCanvas()
        {
            var ring=Rectangle(0,0,48,12).Concat(Rectangle(0,36,48,12)).Concat(Rectangle(0,12,12,24)).Concat(Rectangle(36,12,12,24)).ToArray();
            var small=Rectangle(0,0,12,12);var source=new[] {ring,small};
            var saved=source.Select(p=>(Vector2[])p.Clone()).ToArray();
            Assert.IsTrue(ReverseUvFootprintPacking.TryPackWithin(source,60,1,new ReverseUvTransfer.Options {padding=2},default,
                (g,p,s)=>true,out var result,out int side));
            Assert.LessOrEqual(side,60);
            for(int g=0;g<source.Length;++g) {CollectionAssert.AreEqual(saved[g],source[g]);AssertSameEdges(source[g],result[g]);}
            var all=result.SelectMany(p=>p).ToArray();
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=all,
                indices=Enumerable.Range(0,all.Length).ToArray(),charts=new int[all.Length]},default,comparisonBudget:2000000);
            Assert.IsTrue(scan.complete);Assert.AreEqual(0,scan.invalidFaces);Assert.AreEqual(0,scan.degenerateFaces);Assert.AreEqual(0,scan.pairs);
        }

        [Test] public void ExistingMetricRefusalCannotBeBypassedByCompaction()
        {
            Assert.IsFalse(ReverseUvFootprintPacking.TryPackWithin(new[] {Rectangle(0,0,8,3)},32,1,new ReverseUvTransfer.Options(),default,
                (g,p,s)=>false,out _,out _));
        }

        [Test] public void InsufficientCanvasDoesNotShrinkOrPublishPartialLayout()
        {
            var source=new[] {Rectangle(0,0,48,12)};var saved=(Vector2[])source[0].Clone();
            Assert.IsFalse(ReverseUvFootprintPacking.TryPackWithin(source,32,1,new ReverseUvTransfer.Options(),default,
                (g,p,s)=>true,out var result,out int side));
            Assert.IsNull(result);Assert.AreEqual(32,side);CollectionAssert.AreEqual(saved,source[0]);
        }

        [Test] public void AxisAndPackingHonorCancellation()
        {
            var token=new CancellationToken(true);var points=Rectangle(0,0,8,3);
            Assert.Throws<OperationCanceledException>(()=>ReverseUvFamilyAxis.Rotate(points,points,token));
            Assert.Throws<OperationCanceledException>(()=>ReverseUvFootprintPacking.TryPackWithin(new[] {points},32,1,
                new ReverseUvTransfer.Options(),token,null,out _,out _));
        }

        [TestCase(false)] [TestCase(true)]
        public void AlternativeMainQuarterTurnAvoidsGreedyDeadEnd(bool native)
        {
            // Procedural L plus two rectangles. The initial equal-extent
            // orientation blocks both complete remaining shells in a 48px bin.
            var main=Rectangle(0,0,28,7).Concat(Rectangle(0,7,7,12)).ToArray();
            var source=new[] {main,Rectangle(0,0,18,17),Rectangle(0,0,18,14)};
            var saved=source.Select(p=>(Vector2[])p.Clone()).ToArray();
            var options=new ReverseUvTransfer.Options {padding=2,rotateCharts=true,nativeFootprintSearch=native};
            Assert.IsTrue(ReverseUvFootprintPacking.TryPackWithin(source,48,1,options,default,
                (g,p,s)=>true,out var result,out int side));
            Assert.LessOrEqual(side,48);
            for(int g=0;g<source.Length;++g) {CollectionAssert.AreEqual(saved[g],source[g]);AssertSameEdges(saved[g],result[g]);}
            var points=result.SelectMany(p=>p).ToArray();
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=points,
                indices=Enumerable.Range(0,points.Length).ToArray(),charts=new int[points.Length]},default,comparisonBudget:2000000);
            Assert.IsTrue(scan.complete);Assert.AreEqual(0,scan.invalidFaces);Assert.AreEqual(0,scan.degenerateFaces);Assert.AreEqual(0,scan.pairs);
        }

        static void AssertSameEdges(Vector2[] before,Vector2[] after)
        {
            for(int t=0;t<before.Length;t+=3) for(int k=0;k<3;++k)
                Assert.AreEqual((before[t+k]-before[t+(k+1)%3]).magnitude,(after[t+k]-after[t+(k+1)%3]).magnitude,.00002);
        }
    }
}
