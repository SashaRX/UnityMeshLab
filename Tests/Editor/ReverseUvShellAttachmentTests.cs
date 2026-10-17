using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ReverseUvShellAttachmentTests
    {
        static readonly Vector3[] Points={new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(0,1,0),
            new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(0,1,0),
            new Vector3(1,0,0),new Vector3(2,0,0),new Vector3(1,1,0)};
        static Vector2[] Reference()=>Points.Select(p=>new Vector2(p.x,p.y)).ToArray();
        static Vector2[] Current()
        {
            var uv=Reference();for(int c=6;c<9;++c) uv[c]=new Vector2(10-uv[c].y,10+uv[c].x);return uv;
        }
        static Vector2[] Run(Vector3[] points,Vector2[] reference,Vector2[] before,out int count,long budget=2000000,CancellationToken token=default,float normalDot=.5f)
        {
            var frames=Enumerable.Range(0,points.Length/3).Select(f=>TransferUvQuality.WorldFrame(points[f*3],points[f*3+1],points[f*3+2],Matrix4x4.identity)).ToArray();
            return ReverseUvTransfer.AttachPreparedShells(points,reference,before,frames,
                new ReverseUvTransfer.Options {comparisonBudget=budget,normalDot=normalDot},token,out count,DetachedScope(points,reference,token));
        }
        static ReverseUvAttachmentSource.Scope DetachedScope(Vector3[] points,Vector2[] reference,CancellationToken token)
        {
            var corners=Enumerable.Range(0,points.Length).ToArray();
            return ReverseUvAttachmentSource.Capture(points,corners,reference,null,token).ForPatch(corners,reference,token);
        }
        static Vector2[] RunWithScope(ReverseUvAttachmentSource.Scope scope,Vector2[] reference,Vector2[] before,out int count)
        {
            var frames=Enumerable.Range(0,Points.Length/3).Select(f=>TransferUvQuality.WorldFrame(Points[f*3],Points[f*3+1],Points[f*3+2],Matrix4x4.identity)).ToArray();
            return ReverseUvTransfer.AttachPreparedShells(Points,reference,before,frames,new ReverseUvTransfer.Options(),default,out count,scope);
        }
        static ReverseUvAttachmentSource.Scope OutsidePointScope(bool authoredSeam)
        {
            var points=Points.Concat(new[] {Points[7],new Vector3(4,2,0),new Vector3(4,3,0)}).ToArray();
            var uv=Reference().Concat(new[] {Reference()[7]+(authoredSeam?Vector2.one:Vector2.zero),new Vector2(4,2),new Vector2(4,3)}).ToArray();
            var ids=Enumerable.Range(0,points.Length).ToArray();
            return ReverseUvAttachmentSource.Capture(points,ids,uv,null,default).ForPatch(ids.Take(9).ToArray(),Reference(),default);
        }
        [Test] public void FullSurfaceOutsideUvAliasKeepsDonorFixed()
        {
            var before=Current();Assert.AreSame(before,RunWithScope(OutsidePointScope(false),Reference(),before,out int count));
            Assert.AreEqual(0,count);
        }
        [Test] public void FullSurfaceOutsideAuthoredSeamRemainsIndependent()
        {
            var before=Current();var after=RunWithScope(OutsidePointScope(true),Reference(),before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(Reference(),after);CollectionAssert.AreEqual(Current(),before);
        }
        [Test] public void CompleteSurfaceNonManifoldEdgeCannotJoin()
        {
            var points=Points.Concat(new[] {Points[3],Points[4],new Vector3(1,0,1)}).ToArray();
            var uv=Reference().Concat(new[] {new Vector2(10,0),new Vector2(10,1),new Vector2(11,0)}).ToArray();
            var ids=Enumerable.Range(0,points.Length).ToArray();
            var scope=ReverseUvAttachmentSource.Capture(points,ids,uv,null,default).ForPatch(ids.Take(9).ToArray(),Reference(),default);
            var before=Current();Assert.AreSame(before,RunWithScope(scope,Reference(),before,out int count));Assert.AreEqual(0,count);
        }
        [Test] public void SelectedDonorBoundaryWithIdenticalUvRemainsSeparate()
        {
            var ids=Enumerable.Range(0,Points.Length).ToArray();var donors=new int[Points.Length];
            for(int c=6;c<donors.Length;++c) donors[c]=7;
            var scope=ReverseUvAttachmentSource.Capture(Points,ids,Reference(),donors,default).ForPatch(ids,Reference(),default);
            var before=Current();Assert.AreSame(before,RunWithScope(scope,Reference(),before,out int count));Assert.AreEqual(0,count);
        }
        [Test] public void StaleOrMissingSourcePublishesNoAttachment()
        {
            var reference=Reference();var scope=DetachedScope(Points,reference,default);reference[0]+=Vector2.one;
            var before=Current();Assert.AreSame(before,RunWithScope(scope,reference,before,out int count));Assert.AreEqual(0,count);
            Assert.AreSame(before,RunWithScope(null,Reference(),before,out count));Assert.AreEqual(0,count);
        }
        static ReverseUvAttachmentSource CaptureSelectedSource(int[] donors,bool stale)
        {
            var uv=Reference();var recorded=(Vector2[])uv.Clone();if(stale) recorded[0]+=Vector2.one;
            var constructor=typeof(ForwardUvReference).GetConstructors(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Single();
            var selected=(ForwardUvReference)constructor.Invoke(new object[] {Points,uv,recorded,donors,new bool[3]});
            var indices=Enumerable.Range(0,Points.Length).ToArray();
            var surface=new ReverseUvTransfer.Surface {positions=Points,indices=indices,sourcePositions=Points,sourceIndices=indices,
                input=new ReverseUvTransfer.Input {forwardReference=selected}};
            var capture=typeof(ReverseUvTransfer).GetMethod("CaptureShellAttachmentSource",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            return (ReverseUvAttachmentSource)capture.Invoke(null,new object[] {surface,uv,CancellationToken.None});
        }
        [Test] public void ActualPreparedSourceRejectsStaleForwardSelection()
        {
            Assert.IsNull(CaptureSelectedSource(new int[Points.Length],true));
        }
        [Test] public void ActualPreparedSourceRetainsSelectedDonorBoundary()
        {
            var donors=new int[Points.Length];for(int c=6;c<donors.Length;++c) donors[c]=7;
            var source=CaptureSelectedSource(donors,false);Assert.IsNotNull(source);
            var scope=source.ForPatch(Enumerable.Range(0,Points.Length).ToArray(),Reference(),default);
            var before=Current();Assert.AreSame(before,RunWithScope(scope,Reference(),before,out int count));Assert.AreEqual(0,count);
        }
        static Vector3[] DensityChainPoints()=>new[] {
            new Vector3(0,0,0),new Vector3(0,2,0),new Vector3(1,0,0),
            new Vector3(-1,0,0),new Vector3(-1,2,0),new Vector3(0,0,0),
            new Vector3(-1,2,0),new Vector3(0,2,0),new Vector3(0,0,0)}
            .Concat(Enumerable.Range(0,2).SelectMany(y=>new[] {
                new Vector3(0,-y-1,0),new Vector3(0,-y,0),new Vector3(1,-y-1,0),
                new Vector3(0,-y,0),new Vector3(1,-y,0),new Vector3(1,-y-1,0)})).ToArray();

        [TestCase(1.5f,1)]
        [TestCase(0.6666667f,1)]
        [TestCase(1.25f,2)]
        public void SequentialAttachmentsRespectOriginalDensity(float step,int expectedJoins)
        {
            var points=DensityChainPoints();var reference=points.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=reference.Select((p,c)=>p*(c<3?1:c<9?step:step*step)
                +new Vector2(c<3?30:c<9?10:20,0)).ToArray();
            var saved=(Vector2[])before.Clone();var after=Run(points,reference,before,out int count);
            Assert.AreEqual(expectedJoins,count,"Check the cumulative scale of the original chart, not only the latest join.");
            CollectionAssert.AreEqual(saved,before);
            if(expectedJoins==1) CollectionAssert.AreEqual(before.Skip(9),after.Skip(9));
        }

        [Test] public void MovesWholeDonorAndWeldsOntoImmutableMain()
        {
            var before=Current();var snapshot=(Vector2[])before.Clone();
            var after=Run(Points,Reference(),before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(snapshot,before);
            CollectionAssert.AreEqual(before.Take(6),after.Take(6));
            CollectionAssert.AreEqual(Reference(),after);
        }
        [Test] public void CompatibleFoldMovesCompleteTriangleWithoutChangingMain()
        {
            var points=(Vector3[])Points.Clone();
            float radians=55*Mathf.Deg2Rad;points[7]=new Vector3(1+Mathf.Cos(radians),0,Mathf.Sin(radians));
            var before=Current();var snapshot=(Vector2[])before.Clone();
            var after=Run(points,Reference(),before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(snapshot,before);
            CollectionAssert.AreEqual(before.Take(6),after.Take(6));CollectionAssert.AreEqual(Reference(),after);
        }
        [Test] public void ConfiguredNormalCompatibilityStillRefusesTheSameFold()
        {
            var points=(Vector3[])Points.Clone();
            float radians=55*Mathf.Deg2Rad;points[7]=new Vector3(1+Mathf.Cos(radians),0,Mathf.Sin(radians));
            var before=Current();Assert.AreSame(before,Run(points,Reference(),before,out int count,normalDot:.8f));
            Assert.AreEqual(0,count);
        }
        [Test] public void CompleteFourFaceDonorAttachesToLargerImmutableMain()
        {
            var points=Enumerable.Range(0,5).SelectMany(x=>new[] {
                new Vector3(x,0,0),new Vector3(x+1,0,0),new Vector3(x,1,0),
                new Vector3(x+1,0,0),new Vector3(x+1,1,0),new Vector3(x,1,0)}).ToArray();
            var reference=points.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=(Vector2[])reference.Clone();
            for(int c=18;c<before.Length;++c) before[c]=new Vector2(10-before[c].y,10+before[c].x);
            var snapshot=(Vector2[])before.Clone();
            var after=Run(points,reference,before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(snapshot,before);
            CollectionAssert.AreEqual(before.Take(18),after.Take(18));
            CollectionAssert.AreEqual(reference,after);
        }
        static Vector3[] TwoRectangles(float split,float end)=>new[] {
            new Vector3(0,0,0),new Vector3(split,0,0),new Vector3(0,1,0),
            new Vector3(split,0,0),new Vector3(split,1,0),new Vector3(0,1,0),
            new Vector3(split,0,0),new Vector3(end,0,0),new Vector3(split,1,0),
            new Vector3(end,0,0),new Vector3(end,1,0),new Vector3(split,1,0)};
        [Test] public void EqualFaceCountUsesLargerAreaImmutableMain()
        {
            var points=TwoRectangles(2,3);var reference=points.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=(Vector2[])reference.Clone();
            for(int c=6;c<12;++c) before[c]=new Vector2(10-before[c].y,10+before[c].x);
            var snapshot=(Vector2[])before.Clone();var after=Run(points,reference,before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(snapshot,before);
            CollectionAssert.AreEqual(before.Take(6),after.Take(6));CollectionAssert.AreEqual(reference,after);
        }
        [Test] public void EqualAreaTieKeepsDeterministicMainUnchanged()
        {
            var points=TwoRectangles(1,2);var reference=points.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=(Vector2[])reference.Clone();
            for(int c=0;c<6;++c) before[c]=new Vector2(10-before[c].y,10+before[c].x);
            var snapshot=(Vector2[])before.Clone();var after=Run(points,reference,before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(snapshot,before);
            CollectionAssert.AreEqual(before.Skip(6),after.Skip(6));CollectionAssert.AreEqual(reference,after);
        }
        [Test] public void EqualFaceCountStillPreservesAuthoredSeam()
        {
            var points=TwoRectangles(2,3);var reference=points.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=(Vector2[])reference.Clone();
            for(int c=6;c<12;++c) {before[c]=new Vector2(10-before[c].y,10+before[c].x);reference[c]+=Vector2.one;}
            Assert.AreSame(before,Run(points,reference,before,out int count));Assert.AreEqual(0,count);
        }
        static Vector3[] CompetingContactPoints()=>new[] {
            new Vector3(0,0,0),new Vector3(0,1,0),new Vector3(2,0,0),
            new Vector3(-1,0,0),new Vector3(0,1,0),new Vector3(0,0,0),
            new Vector3(-1,0,0),new Vector3(-1,1,0),new Vector3(0,1,0),
            new Vector3(0,-1,0),new Vector3(0,0,0),new Vector3(2,0,0),
            new Vector3(0,-1,0),new Vector3(2,0,0),new Vector3(2,-1,0)};
        static Vector2[] SeparateContactCharts(Vector2[] reference)
        {
            var uv=(Vector2[])reference.Clone();
            for(int c=0;c<uv.Length;++c) uv[c]+=new Vector2(c<3?30:c<9?10:20,0);
            return uv;
        }
        [Test] public void LongerCompatibleContactChoosesImmutableMain()
        {
            var points=CompetingContactPoints();var reference=points.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=SeparateContactCharts(reference);var saved=(Vector2[])before.Clone();
            var after=Run(points,reference,before,out int count);
            Assert.AreEqual(2,count);CollectionAssert.AreEqual(saved,before);
            CollectionAssert.AreEqual(before.Skip(9),after.Skip(9));
            CollectionAssert.AreEqual(reference.Select(p=>p+new Vector2(20,0)),after);
        }
        [Test] public void LongerAuthoredSeamFallsBackToCompatibleShortContact()
        {
            var points=CompetingContactPoints();var reference=points.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=SeparateContactCharts(reference);var saved=(Vector2[])before.Clone();
            for(int c=9;c<reference.Length;++c) reference[c]+=Vector2.one;
            var after=Run(points,reference,before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(saved,before);
            CollectionAssert.AreEqual(before.Skip(3),after.Skip(3));
            CollectionAssert.AreEqual(reference.Take(3).Select(p=>p+new Vector2(10,0)),after.Take(3));
        }
        [Test] public void AuthoredSeamStaysSeparate()
        {
            var reference=Reference();reference[6]+=Vector2.one;
            var before=Current();Assert.AreSame(before,Run(Points,reference,before,out int count));Assert.AreEqual(0,count);
        }
        [Test] public void SharpEdgeStaysSeparate()
        {
            var points=(Vector3[])Points.Clone();points[7]=new Vector3(1,0,1);
            var before=Current();Assert.AreSame(before,Run(points,Reference(),before,out int count));Assert.AreEqual(0,count);
        }
        [Test] public void DetachedBlockingChartMovesAsWholeComponent()
        {
            var points=Points.Concat(new[] {new Vector3(10,0,0),new Vector3(11,0,0),new Vector3(10,1,0)}).ToArray();
            var before=Current().Concat(new[] {new Vector2(1,0),new Vector2(2,0),new Vector2(1,1)}).ToArray();
            var reference=Reference().Concat(new[] {Vector2.zero,Vector2.right,Vector2.up}).ToArray();
            var snapshot=(Vector2[])before.Clone();var after=Run(points,reference,before,out int count);
            Assert.AreEqual(1,count);CollectionAssert.AreEqual(snapshot,before);
            CollectionAssert.AreEqual(before.Take(6),after.Take(6));CollectionAssert.AreEqual(Reference().Skip(6),after.Skip(6).Take(3));
            var shift=after[9]-before[9];Assert.Greater(shift.x,0);Assert.AreEqual(0,shift.y);
            for(int c=9;c<12;++c) Assert.AreEqual(shift,after[c]-before[c]);
        }
        [Test] public void CollisionWithImmutableMainStillRefuses()
        {
            var before=Current();before[7]=new Vector2(10,10);
            Assert.AreSame(before,Run(Points,Reference(),before,out int count));Assert.AreEqual(0,count);
        }
        [Test] public void PointAnchoredBlockingChartCannotMove()
        {
            var points=Points.Concat(new[] {Points[4],new Vector3(11,0,0),new Vector3(10,1,0)}).ToArray();
            var before=Current().Concat(new[] {new Vector2(1,1),new Vector2(2,0),new Vector2(1.5f,0)}).ToArray();
            var reference=Reference().Concat(new[] {Vector2.zero,Vector2.right,Vector2.up}).ToArray();
            Assert.AreSame(before,Run(points,reference,before,out int count));Assert.AreEqual(0,count);
        }
        [Test] public void BudgetExhaustionPublishesNoPartialMerge()
        {
            var before=Current();Assert.AreSame(before,Run(Points,Reference(),before,out int count,0));Assert.AreEqual(0,count);
        }
        [Test] public void CancellationPreservesInput()
        {
            var before=Current();var snapshot=(Vector2[])before.Clone();
            Assert.Throws<OperationCanceledException>(()=>Run(Points,Reference(),before,out _,token:new CancellationToken(true)));
            CollectionAssert.AreEqual(snapshot,before);
        }
    }
}
