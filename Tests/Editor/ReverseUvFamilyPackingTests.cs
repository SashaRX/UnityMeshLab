using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvFamilyPackingTests
    {
        static Vector2[] Rectangle(float width,float height,Vector2 origin=default)
            => new[] {origin,origin+Vector2.right*width,origin+new Vector2(width,height),
                origin,origin+new Vector2(width,height),origin+Vector2.up*height};

        [Test,Timeout(30000)]
        public void FiveThousandFamiliesRemainDeterministicPaddedAndRigid()
        {
            var input=Enumerable.Range(0,5000).Select(i=>Rectangle(1+i%19,1+i%11,new Vector2(300+i,900))).ToArray();
            var copies=input.Select(p=>p.ToArray()).ToArray();
            var options=new ReverseUvTransfer.Options {padding=2,fillFinalAtlas=true};
            var output=ReverseUvFamilyPacking.Pack(input,options,default,out int side,out float scale);
            var repeated=ReverseUvFamilyPacking.Pack(input,options,default,out int again,out float againScale);
            Assert.AreEqual(side,again);Assert.AreEqual(scale,againScale);Assert.GreaterOrEqual(scale,1);
            for(int f=0;f<input.Length;++f)
            {
                CollectionAssert.AreEqual(copies[f],input[f]);CollectionAssert.AreEqual(output[f],repeated[f]);
                Assert.AreEqual((input[f][1]-input[f][0]).magnitude*scale,(output[f][1]-output[f][0]).magnitude,.001);
                Assert.AreEqual((input[f][2]-input[f][1]).magnitude*scale,(output[f][2]-output[f][1]).magnitude,.001);
                var min=output[f].Aggregate(Vector2.Min);var max=output[f].Aggregate(Vector2.Max);
                Assert.IsTrue(min.x>=2 && min.y>=2 && max.x<=side-2 && max.y<=side-2);
            }
            AssertPadding(output,options.padding);
        }

        static void AssertPadding(Vector2[][] output,float padding)
        {
            var boxes=output.Select(p=>(min:p.Aggregate(Vector2.Min),max:p.Aggregate(Vector2.Max))).ToArray();
            float nearest=float.MaxValue;
            for(int a=0;a<boxes.Length;++a)
                for(int b=a+1;b<boxes.Length;++b)
                {
                    float x=Math.Max(boxes[b].min.x-boxes[a].max.x,boxes[a].min.x-boxes[b].max.x);
                    float y=Math.Max(boxes[b].min.y-boxes[a].max.y,boxes[a].min.y-boxes[b].max.y);
                    nearest=Math.Min(nearest,Math.Max(x,y));
                }
            Assert.GreaterOrEqual(nearest,padding-.001f);
        }

        [Test]
        public void RoundingPhasesRetainTheRequestedGutter()
        {
            var input=Enumerable.Range(0,40).Select(i=>Rectangle(2,3)).ToArray();
            bool Valid(int family,Vector2[] points,int side)
            {
                var min=points.Aggregate(Vector2.Min);
                float fraction=min.x-Mathf.Floor(min.x), desired=family%2==0?.25f:.75f;
                return Math.Abs(fraction-desired)<.001 && points.All(p=>p.x<=side && p.y<=side);
            }
            var result=ReverseUvFamilyPacking.Pack(input,new ReverseUvTransfer.Options {},default,out int side,out _,Valid);
            AssertPadding(result,2);
            for(int f=0;f<result.Length;++f) Assert.IsTrue(Valid(f,result[f],side));
        }

        [Test]
        public void FillingKeepsTheValidPlacementWhenAdditionalFitIsRefused()
        {
            var input=new[] {Rectangle(2,2)};
            bool Valid(int family,Vector2[] points,int atlas)
                => (points.Aggregate(Vector2.Max)-points.Aggregate(Vector2.Min)).x<=5 && points.All(p=>p.x<=atlas && p.y<=atlas);
            var output=ReverseUvFamilyPacking.Pack(input,new ReverseUvTransfer.Options {fillFinalAtlas=true},
                default,out int side,out float scale,Valid);
            Assert.IsTrue(Valid(0,output[0],side));Assert.Greater(scale,2);Assert.LessOrEqual(scale,2.5f);
        }

        [Test]
        public void ThinTriangleFamilyKeepsItsMetricAmongLargeCharts()
        {
            var needle=Rectangle(1,.0000024f,new Vector2(2,2));
            var input=Enumerable.Range(0,240).Select(i=>Rectangle(8+i%9,5+i%7)).Concat(new[] {needle}).ToArray();
            bool Valid(int family,Vector2[] points,int atlas)
            {
                if(points.Any(p=>p.x>atlas || p.y>atlas)) return false;
                if(family!=input.Length-1) return true;
                for(int t=0;t<6;t+=3)
                {
                    double metric=ReverseUvTransfer.TriangleAnisotropy(needle[t],needle[t+1],needle[t+2],points[t],points[t+1],points[t+2]);
                    if(!UvTransferTolerance.MetricFits(metric,1)) return false;
                }
                return true;
            }
            var output=ReverseUvFamilyPacking.Pack(input,new ReverseUvTransfer.Options {fillFinalAtlas=true},default,out int side,out _,Valid);
            Assert.IsTrue(Valid(input.Length-1,output.Last(),side));
        }

        [TestCase(2)] [TestCase(3)]
        public void OppositeQuarterTurnsRemainAvailableForPrecisionPlacement(int rotation)
        {
            var points=new[] {Vector2.zero,Vector2.right*2,Vector2.up};
            bool Valid(int family,Vector2[] uv,int side)
            {
                var min=uv.Aggregate(Vector2.Min);var max=uv.Aggregate(Vector2.Max);
                return uv[0].y==max.y && uv[0].x==(rotation==2?max.x:min.x) && max.x<=side && max.y<=side;
            }
            var result=ReverseUvFamilyPacking.Pack(new[] {points},new ReverseUvTransfer.Options {maxAtlasSize=64},
                default,out int side,out float scale,Valid);
            Assert.IsTrue(Valid(0,result[0],side));
            Assert.AreEqual(2*scale,(result[0][1]-result[0][0]).magnitude,.0001);
            Assert.AreEqual(scale,(result[0][2]-result[0][0]).magnitude,.0001);
            var a=result[0][1]-result[0][0];var b=result[0][2]-result[0][0];
            Assert.Greater(a.x*b.y-a.y*b.x,0,"Rotation must preserve winding.");
        }

        [Test]
        public void NearlyCollinearSiblingRimsDoNotGainContactsAfterPacking()
        {
            var points=new[] {new Vector2(245.744384765625f,74.00200653076172f),new Vector2(245.83363342285156f,74.20191192626953f),
                new Vector2(245.2118377685547f,74.20004272460938f),new Vector2(246.6077880859375f,75.98282623291016f),
                new Vector2(240.00001525878906f,74.201904296875f),new Vector2(246.6077880859375f,74.20191955566406f)};
            var surface=new ReverseUvTransfer.Surface {lod=0,node=0,pixels=points,
                input=new ReverseUvTransfer.Input {key="Nearly collinear rims"},
                positions=points.Select(p=>new Vector3(p.x,p.y)).ToArray(),indices=Enumerable.Range(0,6).ToArray(),
                faces=new[] {new ReverseUvTransfer.Face {chart=0},new ReverseUvTransfer.Face {chart=0}}};
            int Pairs(int side)=>UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=surface.pixels.Select(p=>p/side).ToArray(),
                indices=surface.indices,charts=new int[6]},default).pairs;
            Assert.AreEqual(0,Pairs(512));
            var report=new ReverseUvTransfer.Report {texelsPerUnit=1};
            int atlas=ReverseUvFinalPacking.Pack(new List<ReverseUvTransfer.Surface[]> {new[] {surface}},
                new ReverseUvTransfer.Options {fillFinalAtlas=true},report,512,default);
            Assert.AreEqual("shared-chart-families",report.finalPacking,report.finalPackingRefusal);
            Assert.AreEqual(0,Pairs(atlas));
        }

        [Test]
        public void ExplicitAncestryAndOverlayUniteTheWholeThreeLodFamily()
        {
            ReverseUvTransfer.Surface Surface(int lod,int node,int chart)
            {
                var points=Rectangle(4,2,new Vector2(40,60));
                return new ReverseUvTransfer.Surface {lod=lod,node=node,pixels=points,
                    input=new ReverseUvTransfer.Input {key="family"},
                    positions=points.Select(p=>new Vector3(p.x,p.y)).ToArray(),indices=Enumerable.Range(0,6).ToArray(),
                    faces=Enumerable.Range(0,2).Select(f=>new ReverseUvTransfer.Face {chart=chart,
                        inherited=lod<2,parentLod=lod<2?lod+1:-1,parentMesh=0,parentFace=f}).ToArray()};
            }
            var seed=Surface(2,0,0);var middle=Surface(1,0,3);var fine=Surface(0,0,7);var overlay=Surface(0,1,9);
            foreach(var face in overlay.faces) {face.inherited=false;face.intentionalOverlap=true;face.parentLod=-1;}
            var report=new ReverseUvTransfer.Report {texelsPerUnit=1};
            report.overlaps.Add(new ReverseUvTransfer.OverlapRelation {lod=0,underMesh=0,underFace=0,overMesh=1,overFace=0});
            report.overlaps.Add(new ReverseUvTransfer.OverlapRelation {lod=0,underMesh=0,underFace=1,overMesh=1,overFace=1});
            ReverseUvFinalPacking.Pack(new List<ReverseUvTransfer.Surface[]> {new[] {seed},new[] {middle},new[] {fine,overlay}},
                new ReverseUvTransfer.Options {fillFinalAtlas=true},report,128,default);
            Assert.AreEqual("shared-chart-families",report.finalPacking,report.finalPackingRefusal);
            Assert.AreEqual(1,report.finalPackingFamilies);
            CollectionAssert.AreEqual(seed.pixels,middle.pixels);CollectionAssert.AreEqual(seed.pixels,fine.pixels);
            CollectionAssert.AreEqual(seed.pixels,overlay.pixels);
        }

        [TestCase(false,false)] [TestCase(false,true)]
        [TestCase(true,false)] [TestCase(true,true)]
        public void PreparedFootprintsRetainForwardCorrespondenceWithoutReverseFaceAcceptance(bool overlap,bool otherMeshGroup)
        {
            ReverseUvTransfer.Surface Surface(int lod,string name)
            {
                var uv=Rectangle(20,8,new Vector2(40,60));
                return new ReverseUvTransfer.Surface {lod=lod,node=0,pixels=uv,
                    input=new ReverseUvTransfer.Input {key=name+"_LOD"+lod},
                    positions=uv.Select(p=>new Vector3(p.x,p.y,lod*.01f)).ToArray(),
                    indices=Enumerable.Range(0,uv.Length).ToArray(),preparedRegions=new[] {0,0},
                    faces=new[] {new ReverseUvTransfer.Face {chart=lod,ambiguous=true},new ReverseUvTransfer.Face {chart=lod,ambiguous=true}}};
            }
            var coarse=Surface(1,"Panel");var fine=Surface(0,otherMeshGroup?"Handle":"Panel");
            var report=new ReverseUvTransfer.Report {texelsPerUnit=1};
            ReverseUvFinalPacking.Pack(new List<ReverseUvTransfer.Surface[]> {new[] {coarse},new[] {fine}},
                new ReverseUvTransfer.Options {regroupPreparedAtlas=true,preserveProjectedOverlap=overlap,fillFinalAtlas=true},
                report,128,default);
            Assert.IsNull(report.finalPackingRefusal);
            Assert.AreEqual(otherMeshGroup?2:1,report.finalPackingFamilies);
            if(!otherMeshGroup) CollectionAssert.AreEqual(coarse.pixels,fine.pixels,
                "An ambiguous reverse proposal cannot disconnect a valid shared Forward footprint.");
            Assert.IsFalse(fine.faces.Any(f=>f.inherited || f.intentionalOverlap),
                "Packing correspondence does not invent projection or same-LOD overlap provenance.");
        }

        [Test]
        public void CancelledPackingNeverModifiesItsInput()
        {
            var input=new[] {Rectangle(2,2)};var saved=input[0].ToArray();
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>ReverseUvFamilyPacking.Pack(input,new ReverseUvTransfer.Options {},
                cancellation.Token,out _,out _));CollectionAssert.AreEqual(saved,input[0]);
        }

        [TestCase(false)] [TestCase(true)]
        public void DisconnectedRegionPiecesPackSeparatelyWithTheirDescendantsAndOverlays(bool overlay)
        {
            ReverseUvTransfer.Surface Surface(int lod,int chart)
            {
                var pixels=Rectangle(8,4,new Vector2(40,60)).Concat(Rectangle(8,4,new Vector2(240,160))).ToArray();
                return new ReverseUvTransfer.Surface {lod=lod,node=0,pixels=pixels,
                    input=new ReverseUvTransfer.Input {key="disconnected region"},
                    positions=pixels.Select(p=>new Vector3(p.x,p.y)).ToArray(),indices=Enumerable.Range(0,pixels.Length).ToArray(),
                    faces=Enumerable.Range(0,4).Select(f=>new ReverseUvTransfer.Face {chart=chart,
                        inherited=lod<2,parentLod=lod<2?lod+1:-1,parentMesh=0,parentFace=f}).ToArray()};
            }
            var seed=Surface(2,0);var middle=Surface(1,5);var fine=Surface(0,9);var detail=Surface(0,15);
            foreach(var face in detail.faces) {face.inherited=false;face.intentionalOverlap=true;face.parentLod=-1;}
            var report=new ReverseUvTransfer.Report {texelsPerUnit=1};
            if(overlay) for(int f=0;f<4;++f) report.overlaps.Add(new ReverseUvTransfer.OverlapRelation {
                lod=0,underMesh=0,underFace=f,overMesh=1,overFace=f});
            var fineLevel=overlay?new[] {fine,detail}:new[] {fine};
            int side=ReverseUvFinalPacking.Pack(new List<ReverseUvTransfer.Surface[]> {new[] {seed},new[] {middle},fineLevel},
                new ReverseUvTransfer.Options {fillFinalAtlas=true},report,512,default);
            Assert.AreEqual(2,report.finalPackingFamilies,"A reused chart label cannot pin disconnected islands to their original gaps.");
            Assert.AreEqual("shared-chart-families",report.finalPacking,report.finalPackingRefusal);
            Assert.Less(side,64,"The empty 200-texel gap must not be reserved as part of either island.");
            CollectionAssert.AreEqual(seed.pixels,middle.pixels);CollectionAssert.AreEqual(seed.pixels,fine.pixels);
            if(overlay) CollectionAssert.AreEqual(seed.pixels,detail.pixels);
        }

        [TestCase(false)] [TestCase(true)]
        public void InvalidTrianglesCannotEnterTheCapacitySearch(bool nonfinite)
        {
            var points=new[] {Vector2.zero,Vector2.right,nonfinite?new Vector2(float.NaN,1):Vector2.right*2};
            Assert.Throws<InvalidOperationException>(()=>ReverseUvFamilyPacking.Pack(new[] {points},
                new ReverseUvTransfer.Options {fillFinalAtlas=true},default,out _,out _));
        }

        [Test]
        public void PackingMetricMatchesTheExportedMeshAudit()
        {
            var mesh=new Mesh();
            try
            {
                mesh.vertices=new[] {new Vector3(40.123456f,23.23456f,1),new Vector3(40.234567f,23.45678f,1),new Vector3(40.34567f,23.67891f,1)};
                mesh.triangles=new[] {0,1,2};
                mesh.uv2=new[] {new Vector2(.23f,.47f),new Vector2(.2303f,.4706f),new Vector2(.2306f,.47123f)};
                var transform=Matrix4x4.TRS(new Vector3(300,30,-400),Quaternion.Euler(18,27,9),new Vector3(2,.3f,-1));
                var p=mesh.vertices;var uv=mesh.uv2;
                double packed=ReverseUvTransfer.TriangleAnisotropy(p[0],p[1],p[2],uv[0],uv[1],uv[2],transform);
                double rounded=TransferUvQuality.TriangleAnisotropy(transform.MultiplyVector(p[1]-p[0]),transform.MultiplyVector(p[2]-p[0]),uv[0],uv[1],uv[2]);
                var measured=TransferUvQuality.Measure(mesh,uv,Vector2.one,transform);
                Assert.AreEqual(0,measured.invalidFaces);Assert.AreEqual(0,measured.degenerateFaces);
                Assert.AreEqual(measured.worstAnisotropy,packed,1e-10);
                Assert.That(Math.Abs(rounded-packed),Is.GreaterThan(.1),"Rounding transformed edges before the metric must remain detectable.");
            }
            finally {UnityEngine.Object.DestroyImmediate(mesh);}
        }

        [Test]
        public void SubpixelFamilyMovesKeepAbsoluteStretchAndCollapseGuards()
        {
            var options=new ReverseUvTransfer.Options {boundaryToleranceTexels=.25f,relativeStretchTolerance=.01f,maxAnisotropy=4};
            Assert.IsTrue(ReverseUvFinalPacking.MetricFits(1.05,1,.00001,.000011,options));
            Assert.IsFalse(ReverseUvFinalPacking.MetricFits(1.05,1,1,1,options));
            Assert.IsFalse(ReverseUvFinalPacking.MetricFits(4.1,1,.00001,.000011,options));
            Assert.IsFalse(ReverseUvFinalPacking.MetricFits(double.PositiveInfinity,1,.00001,0,options));
            Assert.IsFalse(ReverseUvFinalPacking.MetricFits(1.05,1,.00001,0,options));
            options.boundaryToleranceTexels=0;
            Assert.IsFalse(ReverseUvFinalPacking.MetricFits(1.05,1,.00001,.000011,options));
        }
    }
}
