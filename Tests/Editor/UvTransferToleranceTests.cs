using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvTransferToleranceTests
    {
        static Vector2[] Triangle(float offset=0) => new[] {new Vector2(offset,0),new Vector2(10,0),new Vector2(offset,10-offset)};

        // Transferred LODs carry duplicated vertices (normal/UV0 splits) whose UV2
        // differ by ULPs to fractions of a texel. They are one point of the surface;
        // reverse grouping and packing must not see a seam there.
        [Test]
        public void CoincidentCornersWeldWithinTheBoundaryTolerance()
        {
            const float atlas=1000f, texel=1f/atlas, tolerance=UvTransferTolerance.BoundaryTexels*texel;
            var positions=new[] {Vector3.zero,Vector3.right,new Vector3(1,1,0),Vector3.up,Vector3.zero,new Vector3(1,1,0)};
            var triangles=new[] {0,1,2, 4,5,3};
            Vector2[] Layout(float diagonalGap) => new[] {new Vector2(.1f,.1f),new Vector2(.2f,.1f),new Vector2(.2f,.2f),new Vector2(.1f,.2f),
                new Vector2(.1f+1e-7f,.1f),new Vector2(.2f+diagonalGap*texel,.2f)};

            var uv=Layout(.2f);
            Assert.AreEqual(2,UvTransferTolerance.WeldCoincidentCorners(uv,positions,triangles,tolerance));
            Assert.AreEqual(uv[0],uv[4],"An ULP split takes the lower-index corner's exact value.");
            Assert.AreEqual(uv[2],uv[5],"A subtexel split welds as well.");

            var seam=Layout(5f);var before=(Vector2[])seam.Clone();
            Assert.AreEqual(1,UvTransferTolerance.WeldCoincidentCorners(seam,positions,triangles,tolerance));
            Assert.AreEqual(before[5],seam[5],"A five-texel gap is a real seam and stays.");

            // Three copies 0.2 texel apart: the far one is 0.4 texel from the kept value.
            var chain=new[] {new Vector2(.5f,.5f),new Vector2(.5f+.2f*texel,.5f),new Vector2(.5f+.4f*texel,.5f),
                new Vector2(.6f,.5f),new Vector2(.6f,.6f)};
            var chainPositions=new[] {Vector3.zero,Vector3.zero,Vector3.zero,Vector3.right,Vector3.up};
            Assert.AreEqual(1,UvTransferTolerance.WeldCoincidentCorners(chain,chainPositions,new[] {0,3,4, 1,3,4, 2,3,4},tolerance));
            Assert.AreEqual(chain[0],chain[1]);Assert.AreNotEqual(chain[0],chain[2],"Welds never accumulate beyond the tolerance.");

            // Moving the apex of a sliver across its base would flip the face: keep it.
            var sliver=new[] {new Vector2(.3f,.3f-.1f*texel),new Vector2(.3f-5*texel,.3f),new Vector2(.3f+5*texel,.3f),
                new Vector2(.3f,.3f+.1f*texel),new Vector2(.3f,.4f)};
            var sliverPositions=new[] {Vector3.up,Vector3.zero,Vector3.right,Vector3.up,new Vector3(0,2,0)};
            var kept=(Vector2[])sliver.Clone();
            Assert.AreEqual(0,UvTransferTolerance.WeldCoincidentCorners(sliver,sliverPositions,new[] {1,2,3, 0,2,4},tolerance));
            CollectionAssert.AreEqual(kept,sliver,"A weld that flips or collapses an incident face is rolled back.");
        }

        [TestCase(.2f,.25f,true)]
        [TestCase(.4f,.25f,false)]
        [TestCase(.4f,.5f,true)]
        public void BoundaryDiscrepancyIsBoundedInTexels(float shift,float tolerance,bool expected)
        {
            long work=0;
            var shifted=Triangle().Select(p=>p+Vector2.right*shift).ToArray();
            Assert.AreEqual(expected,UvTransferTolerance.Covers(Triangle(),new[] {shifted},tolerance,ref work,10000,default));
        }

        [TestCase(1e-8f)] [TestCase(1f)] [TestCase(128f)]
        public void BoundaryDiscrepancyDoesNotDependOnCoordinateUnits(float scale)
        {
            var triangle=Triangle().Select(p=>p*scale).ToArray();
            var shifted=Triangle().Select(p=>(p+Vector2.right*.4f)*scale).ToArray();
            long work=0;
            Assert.IsFalse(UvTransferTolerance.Covers(triangle,new[] {shifted},.25*scale,ref work,10000,default));
        }

        [TestCase(.25f)] [TestCase(.5f)]
        public void LargeInteriorHoleAndDuplicatedDonorsRemainUncovered(float tolerance)
        {
            Vector2[] Face(float ax,float ay,float bx,float by,float cx,float cy)
                =>new[] {new Vector2(ax,ay),new Vector2(bx,by),new Vector2(cx,cy)};
            var ring=new[] {Face(0,0,10,0,10,4),Face(0,0,10,4,0,4),Face(0,4,4,4,4,10),Face(0,4,4,10,0,10),
                Face(6,4,10,4,10,10),Face(6,4,10,10,6,10),Face(4,6,6,6,6,10),Face(4,6,6,10,4,10)};
            long work=0;
            Assert.IsFalse(UvTransferTolerance.Covers(Face(2,2,8,2,5,8),ring,tolerance,ref work,10000,default));
            work=0;
            Assert.IsFalse(UvTransferTolerance.Covers(Triangle(),Enumerable.Repeat(Face(0,0,4,0,0,4),20),tolerance,ref work,10000,default));
        }

        [Test]
        public void AcuteCornerCannotExtendTheBoundaryBandIndefinitely()
        {
            var donor=new[] {Vector2.zero,new Vector2(10,0),new Vector2(.001f,.001f)};
            var outside=new[] {new Vector2(-3,-.0001f),new Vector2(-2,-.0001f),new Vector2(-2,.0001f)};
            long work=0;
            Assert.IsFalse(UvTransferTolerance.Covers(outside,new[] {donor},.5,ref work,10000,default));
        }

        [Test]
        public void BandHonoursWorkBudgetAndCancellation()
        {
            long work=0;
            Assert.Throws<InvalidOperationException>(()=>UvTransferTolerance.Covers(Triangle(),new[] {Triangle(.4f)},.25,ref work,0,default));
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            Assert.Throws<OperationCanceledException>(()=>UvTransferTolerance.Covers(Triangle(),new[] {Triangle(.4f)},.25,ref work,10000,canceled.Token));
        }

        [TestCase(.001f,.25f,true)] [TestCase(.003f,.25f,false)] [TestCase(.003f,.5f,true)]
        public void InheritanceComparisonUsesOriginalFacePixels(float gap,float tolerance,bool expected)
        {
            var mesh=new Mesh {vertices=new[] {Vector3.zero,Vector3.right,Vector3.up},triangles=new[] {0,1,2},
                uv2=new[] {Vector2.zero,Vector2.right,Vector2.up}};
            try
            {
                var before=new ReverseUvTransfer.NodeReport();
                var after=new ReverseUvTransfer.NodeReport {sourceBarycentrics=new[] {
                    new Vector3(1-gap,gap,0),Vector3.up,new Vector3(0,gap,1-gap)}};
                var coverage=new ReverseUvJunctionTrial.CoverageContext(before,mesh,100,tolerance);
                Assert.AreEqual(expected,coverage.Covers(0,after,new[] {0},default));
                Assert.IsFalse(coverage.Covers(0,after,Array.Empty<int>(),default),"Losing an entire inherited face is never a rounding allowance.");
            }
            finally {UnityEngine.Object.DestroyImmediate(mesh);}
        }

        [TestCase(1.005f,true)] [TestCase(1.02f,false)]
        public void CompleteChainAllowsOnePercentLocalMetricChange(float scale,bool expected)
        {
            Mesh Mesh(float s)=>new Mesh {vertices=new[] {Vector3.zero,Vector3.right,Vector3.up},triangles=new[] {0,1,2},
                uv2=new[] {Vector2.zero,Vector2.right*s*.25f,Vector2.up*.25f}};
            var a=Mesh(1);var b=Mesh(scale);
            using var baseline=new ReverseUvTransfer.Result {report=new ReverseUvTransfer.Report {atlasSize=128,texelsPerUnit=8}};
            using var candidate=new ReverseUvTransfer.Result {report=new ReverseUvTransfer.Report {atlasSize=128,texelsPerUnit=8,
                relativeStretchTolerance=.01f,boundaryToleranceTexels=.25f}};
            baseline.meshes.Add(new[] {a});candidate.meshes.Add(new[] {b});
            foreach(var result in new[] {baseline,candidate}) result.report.nodes.Add(new ReverseUvTransfer.NodeReport {
                seed=true,faces=new[] {new ReverseUvTransfer.Face()},sourceFaces=new[] {0}});
            var levels=new[] {new ReverseUvTransfer.Level {lod=0,inputs=new[] {new ReverseUvTransfer.Input {mesh=a}}}};
            string refusal=ReverseUvJunctionTrial.Compare(baseline,candidate,levels);
            if(expected) Assert.IsNull(refusal);else StringAssert.Contains("increases distortion",refusal);
        }

        [TestCase(.995f,true)] [TestCase(.98f,false)]
        public void RefinementCanSpendAtMostOnePercentOfFilledDensity(float ratio,bool expected)
        {
            using var baseline=new ReverseUvTransfer.Result {report=new ReverseUvTransfer.Report {texelsPerUnit=100}};
            using var candidate=new ReverseUvTransfer.Result {report=new ReverseUvTransfer.Report {
                texelsPerUnit=100*ratio,relativeStretchTolerance=.01f}};
            string refusal=ReverseUvJunctionTrial.Compare(baseline,candidate,Array.Empty<ReverseUvTransfer.Level>());
            if(expected) Assert.IsNull(refusal);else StringAssert.Contains("cuts reduce texel density",refusal);
        }

        [TestCase(.001f,true,1f)] [TestCase(.01f,false,1f)]
        [TestCase(.001f,true,.001f)] [TestCase(.01f,false,.001f)]
        public void PlanarRecoveryUsesTexelSizedPlaneTolerance(float height,bool expected,float scale)
        {
            var uv=new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up};
            var indices=new[] {0,1,2,0,2,3};var positions=new[] {Vector3.zero,Vector3.right,new Vector3(1,1,0),Vector3.up};
            var shell=UvShellExtractor.Extract(uv,indices)[0];
            positions=positions.Select(p=>p*scale).ToArray();
            var target=positions.Select(p=>p+Vector3.forward*height*scale).ToArray();
            var recovered=PlanarTransferRecovery.Project(shell,indices,target,shell,indices,positions,uv,128,.25f);
            Assert.AreEqual(expected,recovered!=null);
        }

        [TestCase(false)] [TestCase(true)]
        public void UniformFinalFitUsesAtlasExtentAndPreservesPadding(bool rotate)
        {
            var charts=new[] {new[] {Vector2.zero,new Vector2(2,0),new Vector2(0,1)},
                new[] {Vector2.zero,new Vector2(3,0),new Vector2(0,4)}};
            var options=new ReverseUvTransfer.Options {padding=2,fillFinalAtlas=true,rotateCharts=rotate};
            var packed=ReverseUvSeedPacking.PackRigid(charts,options,default,out int side,out float scale);
            var lower=packed.SelectMany(p=>p).Aggregate(Vector2.Min);
            var span=packed.SelectMany(p=>p).Aggregate(Vector2.Max)-lower;
            Assert.AreEqual(side-options.padding*2,Math.Max(span.x,span.y),.001);
            Assert.AreEqual(options.padding,Math.Min(lower.x,lower.y),.001);
            for(int c=0;c<charts.Length;++c)
                for(int i=0;i<3;++i)
                    for(int j=i+1;j<3;++j)
                        Assert.AreEqual((charts[c][i]-charts[c][j]).magnitude*scale,
                            (packed[c][i]-packed[c][j]).magnitude,.001);
            var a=packed[0].Aggregate(Vector2.Min);var b=packed[0].Aggregate(Vector2.Max);
            var d=packed[1].Aggregate(Vector2.Min);var e=packed[1].Aggregate(Vector2.Max);
            Assert.IsTrue(d.x-b.x>=options.padding || a.x-e.x>=options.padding
                || d.y-b.y>=options.padding || a.y-e.y>=options.padding);
        }
    }
}
