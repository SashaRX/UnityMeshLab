using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvFootprintPackingTests
    {
        static Vector2[] Rectangle(float x,float y,float width,float height)
            => new[] {new Vector2(x,y),new Vector2(x+width,y),new Vector2(x+width,y+height),
                new Vector2(x,y),new Vector2(x+width,y+height),new Vector2(x,y+height)};

        [TestCase(false)] [TestCase(true)]
        public void RingInteriorAcceptsAnotherFamilyWithoutMovingItsOverlayIndependently(bool overlay)
        {
            var frame=Rectangle(0,0,24,2).Concat(Rectangle(0,22,24,2)).Concat(Rectangle(0,2,2,20))
                .Concat(Rectangle(22,2,2,20)).ToArray();
            if(overlay) frame=frame.Concat(Rectangle(0,0,24,2)).ToArray();
            var source=new[] {frame,Rectangle(0,0,8,8)};var snapshot=source.Select(p=>p.ToArray()).ToArray();
            var options=new ReverseUvTransfer.Options {padding=2};
            var baseline=ReverseUvFamilyPacking.Pack(source,options,default,out int side,out float scale);
            var output=ReverseUvFootprintPacking.Improve(source,baseline,side,scale,options,default,null,out float finalScale,out bool improved);
            Assert.IsTrue(improved,"Rectangle reservations must not consume a ring's empty interior.");
            Assert.AreEqual(scale,finalScale);
            var outerMin=output[0].Aggregate(Vector2.Min);var outerMax=output[0].Aggregate(Vector2.Max);
            Assert.IsTrue(output[1].All(p=>p.x>outerMin.x+2 && p.y>outerMin.y+2 && p.x<outerMax.x-2 && p.y<outerMax.y-2));
            if(overlay) CollectionAssert.AreEqual(output[0].Take(6),output[0].Skip(24));
            for(int i=0;i<source.Length;++i) CollectionAssert.AreEqual(snapshot[i],source[i]);
            var corners=output.SelectMany(p=>p).ToArray();
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=corners,indices=Enumerable.Range(0,corners.Length).ToArray(),
                charts=new int[corners.Length]},default,collectConflicts:true);
            Assert.IsTrue(scan.complete);Assert.AreEqual(0,scan.degenerateFaces);
            foreach(var pair in scan.conflicts) Assert.IsTrue(overlay && pair.a<frame.Length/3 && pair.b<frame.Length/3);
        }

        [Test]
        public void FillingRefusalKeepsTheValidatedFootprintImprovement()
        {
            var frame=Rectangle(0,0,24,2).Concat(Rectangle(0,22,24,2)).Concat(Rectangle(0,2,2,20))
                .Concat(Rectangle(22,2,2,20)).ToArray();
            var source=new[] {frame,Rectangle(0,0,8,8)};
            var options=new ReverseUvTransfer.Options {padding=2};
            var baseline=ReverseUvFamilyPacking.Pack(source,options,default,out int side,out float scale);
            options.fillFinalAtlas=true;
            bool Valid(int group,Vector2[] points,int atlas)
            {
                var min=points.Aggregate(Vector2.Min);var max=points.Aggregate(Vector2.Max);
                return Math.Max(max.x-min.x,max.y-min.y)<=(group==0?24:8)*scale+.001;
            }
            var result=ReverseUvFootprintPacking.Improve(source,baseline,side,scale,options,default,Valid,
                out float finalScale,out bool improved);
            Assert.IsTrue(improved,"A failed optional density increase must not discard a valid tighter packing.");
            Assert.AreEqual(scale,finalScale);
            var outerMin=result[0].Aggregate(Vector2.Min);var outerMax=result[0].Aggregate(Vector2.Max);
            Assert.IsTrue(result[1].All(p=>p.x>outerMin.x+2 && p.y>outerMin.y+2 && p.x<outerMax.x-2 && p.y<outerMax.y-2));
        }

        [Test]
        public void UnrepresentablePlacementsRetainBaselineWithBoundedValidationWork()
        {
            var source=new[] {Rectangle(0,0,8,8)};
            var baseline=new[] {Rectangle(2,2,8,8)};var snapshot=baseline[0].ToArray();int audits=0;
            var result=ReverseUvFootprintPacking.Improve(source,baseline,1024,1,
                new ReverseUvTransfer.Options {padding=2},default,(group,points,side)=>{++audits;return false;},
                out float scale,out bool improved);
            Assert.IsFalse(improved);Assert.AreEqual(1,scale);
            Assert.LessOrEqual(audits,512,"One unrepresentable family must not audit every free cell in the atlas.");
            CollectionAssert.AreEqual(snapshot,result[0]);CollectionAssert.AreEqual(snapshot,baseline[0]);
        }

        [Test]
        public void OneImmovableFamilyDoesNotCancelAnotherFamilysCompaction()
        {
            var frame=Rectangle(0,0,24,2).Concat(Rectangle(0,22,24,2)).Concat(Rectangle(0,2,2,20))
                .Concat(Rectangle(22,2,2,20)).ToArray();
            var thin=new[] {Vector2.zero,Vector2.right*3,new Vector2(0,.00001f)};
            var source=new[] {frame,Rectangle(0,0,8,8),thin};
            var baseline=new[] {frame.Select(p=>p+Vector2.one*2).ToArray(),Rectangle(2,32,8,8),
                thin.Select(p=>p+new Vector2(50,2)).ToArray()};
            bool Valid(int family,Vector2[] points,int atlas)=>family!=2 || points.SequenceEqual(baseline[2]);
            var result=ReverseUvFootprintPacking.Improve(source,baseline,64,1,new ReverseUvTransfer.Options {padding=2},
                default,Valid,out float scale,out bool improved);
            Assert.IsTrue(improved,"The complete atlas must not be rolled back when one family must retain its coordinates.");
            Assert.AreEqual(1,scale);CollectionAssert.AreEqual(baseline[2],result[2]);
            Assert.Less(result[1].Max(p=>p.y),32);
            var corners=result.SelectMany(p=>p).ToArray();
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=corners,
                indices=Enumerable.Range(0,corners.Length).ToArray(),charts=new int[corners.Length]},default);
            Assert.IsTrue(scan.complete);Assert.AreEqual(0,scan.pairs);Assert.AreEqual(0,scan.degenerateFaces);
        }

        [Test]
        public void RectangleRefusalStillRetainsTheValidatedFootprintLayout()
        {
            var frame=Rectangle(2,2,24,2).Concat(Rectangle(2,24,24,2)).Concat(Rectangle(2,4,2,20))
                .Concat(Rectangle(24,4,2,20)).ToArray();
            var inner=Rectangle(8,8,8,8);
            ReverseUvTransfer.Surface Surface(Vector2[] pixels,int chart,int node)=>new ReverseUvTransfer.Surface {
                lod=1,node=node,pixels=pixels,positions=pixels.Select(p=>new Vector3(p.x,p.y,0)).ToArray(),
                indices=Enumerable.Range(0,pixels.Length).ToArray(),
                faces=Enumerable.Range(0,pixels.Length/3).Select(f=>new ReverseUvTransfer.Face {chart=chart}).ToArray()};
            var surfaces=new[] {Surface(frame,0,0),Surface(inner,1,1)};
            var report=new ReverseUvTransfer.Report {texelsPerUnit=1};
            int side=ReverseUvFinalPacking.Pack(new System.Collections.Generic.List<ReverseUvTransfer.Surface[]> {surfaces},
                new ReverseUvTransfer.Options {padding=2,seedResolution=32,maxAtlasSize=32},report,32,default);
            Assert.AreEqual(32,side);Assert.IsNotEmpty(report.finalPackingRectangleRefusal);
            Assert.IsNotEmpty(report.finalPacking);Assert.IsNull(report.finalPackingRefusal);
            var corners=surfaces.SelectMany(s=>s.pixels).ToArray();
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=corners,
                indices=Enumerable.Range(0,corners.Length).ToArray(),charts=new int[corners.Length]},default);
            Assert.IsTrue(scan.complete);Assert.AreEqual(0,scan.pairs);Assert.AreEqual(0,scan.degenerateFaces);
        }

        [Test]
        public void NativeFamilyEnvelopeKeepsOverlaidTrianglesInOneRigidTransform()
        {
            var under=Rectangle(0,0,12,4);
            var source=new[] {under.Concat(Rectangle(2,1,3,2)).ToArray(),Rectangle(30,10,6,9),Rectangle(50,20,8,3)};
            var snapshots=source.Select(p=>p.ToArray()).ToArray();
            var result=ReverseUvNativeFamilyPacking.TryPack(source,128,new ReverseUvTransfer.Options {padding=2},default,
                (g,p,s)=>p.All(v=>v.x>=0 && v.y>=0 && v.x<=s && v.y<=s),out float scale,out int side,allowLargerCanvas:true);
            Assert.IsNotNull(result,"The installed native packer must preserve one connected family envelope.");
            for(int g=0;g<source.Length;++g)
                for(int p=0;p<source[g].Length;++p)
                    Assert.AreEqual((source[g][p]-source[g][0]).magnitude*scale,(result[g][p]-result[g][0]).magnitude,.001);
            Assert.Greater(scale,1);
            for(int g=0;g<source.Length;++g) CollectionAssert.AreEqual(snapshots[g],source[g]);
            // Independent families remain disjoint; overlaps inside a family
            // retain their relative coordinates rather than becoming new items.
            var boxes=result.Select(p=>p.Take(6)).SelectMany(p=>p).ToArray();
            var audit=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=boxes.Select(p=>p/side).ToArray(),
                indices=Enumerable.Range(0,boxes.Length).ToArray(),charts=new int[boxes.Length]},default);
            Assert.IsTrue(audit.complete);Assert.AreEqual(0,audit.pairs);
        }

        [Test]
        public void CancelledFootprintSearchDoesNotMutateTheRetainedCoordinates()
        {
            var source=new[] {Rectangle(0,0,8,2)};var before=source[0].ToArray();
            Assert.Throws<OperationCanceledException>(()=>ReverseUvFootprintPacking.Improve(source,source,32,1,
                new ReverseUvTransfer.Options {},new CancellationToken(true),null,out _,out _));
            CollectionAssert.AreEqual(before,source[0]);
        }
    }
}
