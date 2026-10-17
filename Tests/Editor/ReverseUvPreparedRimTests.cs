using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvPreparedRimTests
    {
        static (Vector3[] points,Vector2[] uv) Patch(float rimHeight=.001f)
        {
            var p=new[] {Vector3.zero,new Vector3(2,0,0),new Vector3(2,2,0),new Vector3(0,2,0),new Vector3(0,2,.2f),new Vector3(2,2,.2f)};
            var u=new[] {new Vector2(.1f,.1f),new Vector2(2.1f,.1f),new Vector2(2.1f,2.1f),new Vector2(.1f,2.1f),new Vector2(.1f,2.1f+rimHeight),new Vector2(2.1f,2.1f+rimHeight)};
            var indices=new[] {0,1,2,0,2,3,3,2,5,3,5,4};
            return (indices.Select(i=>p[i]).ToArray(),indices.Select(i=>u[i]).ToArray());
        }

        static Vector2[] Repair(Vector3[] points,Vector2[] uv,long budget=2000000,CancellationToken token=default)
        {
            var frames=Enumerable.Range(0,points.Length/3).Select(f=>TransferUvQuality.WorldFrame(points[f*3],points[f*3+1],points[f*3+2],Matrix4x4.identity)).ToArray();
            object[] args={points,uv,frames,new ReverseUvTransfer.Options {comparisonBudget=budget},token,null,-1L};
            bool success=(bool)typeof(ReverseUvTransfer).GetMethod("TryRepairPreparedRim",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
            return success?(Vector2[])args[5]:null;
        }

        [Test]
        public void CompleteFoldedRimRecoversWithoutMovingHealthyMainOrBreakingItsJoinedEdge()
        {
            var (p,u)=Patch();var original=(Vector2[])u.Clone();var positions=(Vector3[])p.Clone();
            var result=Repair(p,u);Assert.IsNotNull(result);
            CollectionAssert.AreEqual(original.Take(6),result.Take(6));
            CollectionAssert.AreEqual(original,u);CollectionAssert.AreEqual(positions,p);
            Assert.AreEqual(result[5],result[6]);Assert.AreEqual(result[2],result[7]);
            Assert.AreEqual(result[6],result[9]);Assert.AreEqual(result[8],result[10]);
            var mesh=new Mesh {vertices=p,triangles=Enumerable.Range(0,p.Length).ToArray(),uv2=result};
            try
            {
                var quality=TransferUvQuality.Measure(mesh,result,Vector2.one,Matrix4x4.identity);
                Assert.Less(quality.worstAnisotropy,1.0001);
                Assert.AreEqual(0,quality.degenerateFaces+quality.invalidFaces+quality.overlapPairs);
                Assert.IsTrue(quality.overlapScanComplete);
                Assert.IsTrue(UvShellTopologyRepair.IsUvDisk(p,result,default), "All original continuous edges must still form one manifold UV disk.");
            }
            finally {UnityEngine.Object.DestroyImmediate(mesh);}
        }

        [Test]
        public void HealthyChartRetainsItsExistingParameterization()
        {var (p,u)=Patch(.2f);Assert.IsNull(Repair(p,u));}

        [Test]
        public void AuthoredSeamIsNotWeldedByGeometricCoincidence()
        {
            var (p,u)=Patch();for(int c=6;c<u.Length;++c) u[c]+=Vector2.right*5;
            Assert.IsNull(Repair(p,u));
        }

        [Test]
        public void MirroredRimCannotBeForcedOverTheImmutableMain()
        {var (p,u)=Patch(-.001f);Assert.IsNull(Repair(p,u));}

        [Test]
        public void NonmanifoldPatchIsRefused()
        {
            var (p,u)=Patch();p=p.Concat(p.Take(3)).ToArray();u=u.Concat(u.Take(3)).ToArray();
            Assert.IsNull(Repair(p,u));
        }

        [Test]
        public void AlternativeMustAlsoPreserveEveryEstablishedRepairMetric()
        {
            var frame=TransferUvQuality.WorldFrame(Vector3.zero,Vector3.right,Vector3.up,Matrix4x4.identity);
            var baseline=new[] {Vector2.zero,Vector2.right,Vector2.up};
            var candidate=new[] {Vector2.zero,new Vector2(1.2f,0),Vector2.up};
            object[] args={new[] {Vector3.zero,Vector3.right,Vector3.up},candidate,baseline,new[] {frame},new ReverseUvTransfer.Options()};
            var fits=typeof(ReverseUvTransfer).GetMethod("PreparedRimFitsBaseline",BindingFlags.Static|BindingFlags.NonPublic);
            Assert.IsFalse((bool)fits.Invoke(null,args),"Improving damaged input cannot displace a better established repair.");
            args[1]=baseline;args[2]=candidate;
            Assert.IsTrue((bool)fits.Invoke(null,args));
        }

        [Test]
        public void FinerIslandRepairKeepsTheEstablishedImplementationByDefault()
        {
            var (p,u)=Patch();
            var frames=Enumerable.Range(0,p.Length/3).Select(f=>TransferUvQuality.WorldFrame(p[f*3],p[f*3+1],p[f*3+2],Matrix4x4.identity)).ToArray();
            var options=new ReverseUvTransfer.Options();
            var flags=BindingFlags.Static|BindingFlags.NonPublic;
            var type=typeof(ReverseUvTransfer);
            object[] legacyArgs={p,u,frames,options,CancellationToken.None,(Action<long>)(_=>{}),null};
            var expected=(Vector2[])type.GetMethod("RepairPreparedShellExisting",flags).Invoke(null,legacyArgs);
            object[] actualArgs={p,u,frames,options,CancellationToken.None,null,false};
            var actual=(Vector2[])type.GetMethod("RepairPreparedShell",flags).Invoke(null,actualArgs);
            CollectionAssert.AreEqual(expected,actual,"Only the foundation may opt into the alternative before freezing the LOD chain.");
        }

        [Test]
        public void ShapeRecoveryCannotOpenOrCloseAnEstablishedRepairSeam()
        {
            var (p,u)=Patch(.2f);var separated=(Vector2[])u.Clone();
            for(int i=6;i<separated.Length;++i) separated[i]+=Vector2.right*5;
            var frames=Enumerable.Range(0,p.Length/3).Select(f=>TransferUvQuality.WorldFrame(p[f*3],p[f*3+1],p[f*3+2],Matrix4x4.identity)).ToArray();
            var fits=typeof(ReverseUvTransfer).GetMethod("PreparedRimFitsBaseline",BindingFlags.Static|BindingFlags.NonPublic);
            var options=new ReverseUvTransfer.Options();
            Assert.IsFalse((bool)fits.Invoke(null,new object[] {p,u,separated,frames,options}),"Do not silently close an existing repair seam.");
            Assert.IsFalse((bool)fits.Invoke(null,new object[] {p,separated,u,frames,options}),"Do not open an existing continuous edge.");
            Assert.IsTrue((bool)fits.Invoke(null,new object[] {p,u.Select(v=>v*2+Vector2.one).ToArray(),u,frames,options}));
        }

        [Test]
        public void WorkBudgetAndCancellationRemainEffective()
        {
            var (p,u)=Patch();Assert.IsNull(Repair(p,u,0));
            var error=Assert.Throws<TargetInvocationException>(()=>Repair(p,u,2000000,new CancellationToken(true)));
            Assert.IsInstanceOf<OperationCanceledException>(error.InnerException);
        }
    }
}
