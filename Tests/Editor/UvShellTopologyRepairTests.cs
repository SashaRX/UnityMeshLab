using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    internal sealed class UvShellTopologyRepairTests
    {

        [TestCase(false,.001f)] [TestCase(true,.001f)]
        [TestCase(false,1f)] [TestCase(true,1f)]
        [TestCase(false,1000f)] [TestCase(true,1000f)]
        public void PlanarInteriorSlitCanBeWeldedWithoutChangingGeometry(bool mirrored,float scale)
        {
            var ring=new[] {new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
            var corners=Enumerable.Range(0,4).SelectMany(f=>new[] {Vector3.zero,ring[f],ring[(f+1)%4]}).ToArray();
            var uv=corners.Select(p=>new Vector2(p.x,p.y*(mirrored?-1:1))).ToArray();
            uv[0]+=new Vector2(.1f,.1f);
            uv=uv.Select(p=>p*scale).ToArray();
            var snapshot=(Vector3[])corners.Clone();var original=(Vector2[])uv.Clone();
            var result=UvShellTopologyRepair.TryRepair(corners,uv,RepackOptions.Default.arapIterations,default,out var reason);
            Assert.NotNull(result,reason);CollectionAssert.AreEqual(snapshot,corners);CollectionAssert.AreEqual(original,uv);
            for(int c=0;c<corners.Length;++c)
                for(int d=c+1;d<corners.Length;++d) if(corners[c]==corners[d]) Assert.AreEqual(result[c],result[d]);
        }
        [Test]
        public void GeometricHoleIsPreserved()
        {
            var p=new[] {new Vector3(-2,-2,0),new Vector3(2,-2,0),new Vector3(2,2,0),new Vector3(-2,2,0),
                new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
            var ix=Enumerable.Range(0,4).SelectMany(i=>new[] {i,(i+1)%4,4+i,(i+1)%4,4+(i+1)%4,4+i}).ToArray();
            var corners=ix.Select(i=>p[i]).ToArray();var uv=corners.Select(v=>new Vector2(v.x,v.y)).ToArray();uv[0].x+=.1f;
            Assert.IsNull(UvShellTopologyRepair.TryRepair(corners,uv,20,default,out _));
        }
        [Test]
        public void ValidUvDiskIsNotReparameterized()
        {
            var corners=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.right,Vector3.one,Vector3.up};
            var uv=corners.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var before=(Vector2[])uv.Clone();
            Assert.IsNull(UvShellTopologyRepair.TryRepair(corners,uv,20,default,out _));
            CollectionAssert.AreEqual(before,uv);
        }
        [Test]
        public void RequiredClosedSurfaceCutIsPreserved()
        {
            var p=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.forward};
            var ix=new[] {0,2,1,0,1,3,0,3,2,1,2,3};
            var corners=ix.Select(i=>p[i]).ToArray();
            var uv=Enumerable.Range(0,corners.Length).Select(i=>new Vector2(i%3,i/3)).ToArray();
            Assert.IsNull(UvShellTopologyRepair.TryRepair(corners,uv,20,default,out _));
        }
        [Test]
        public void CancellationCannotPublishOrMutateTheInput()
        {
            var corners=new[] {Vector3.zero,Vector3.right,Vector3.up};
            var uv=new[] {Vector2.zero,Vector2.right,Vector2.up};var before=(Vector2[])uv.Clone();
            using var cancel=new System.Threading.CancellationTokenSource();cancel.Cancel();
            Assert.Throws<OperationCanceledException>(()=>UvShellTopologyRepair.TryRepair(corners,uv,20,cancel.Token,out _));
            CollectionAssert.AreEqual(before,uv);
        }

        static (Vector3[] corners,Vector2[] uv) SlitDisk()
        {
            var inner=new[] {new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
            var corners=new List<Vector3>();
            for(int i=0;i<4;++i)
            {
                int next=(i+1)%4;
                corners.AddRange(new[] {Vector3.zero,inner[i],inner[next],inner[i],inner[i]*2,inner[next]*2,
                    inner[i],inner[next]*2,inner[next]});
            }
            var uv=corners.Select(p=>new Vector2(p.x,p.y)*.2f+Vector2.one*.5f).ToArray();
            uv[0]+=Vector2.down*.02f;
            return (corners.ToArray(),uv);
        }

        [Test]
        public void RepairRespectsAStricterCallerMetricTolerance()
        {
            var data=SlitDisk();
            var curved=data.corners.Select(p=>p.x==0 && p.y==0?new Vector3(0,0,.05f):p).ToArray();
            Assert.NotNull(UvShellTopologyRepair.TryRepair(curved,data.uv,50,default,out var ordinaryReason),ordinaryReason);
            Assert.IsNull(UvShellTopologyRepair.TryRepair(curved,data.uv,50,default,out _,relativeStretchTolerance:0),
                "Closing this mildly curved patch needs a small tradeoff on previously isometric faces, forbidden by a zero relative tolerance.");
        }

        sealed class CancelOnCount : ICollection<int>
        {
            readonly int[] values;readonly System.Threading.CancellationTokenSource cancellation;
            internal CancelOnCount(int[] values,System.Threading.CancellationTokenSource cancellation)
            {this.values=values;this.cancellation=cancellation;}
            public int Count {get {cancellation.Cancel();return values.Length;}}
            public bool IsReadOnly=>true;
            public IEnumerator<int> GetEnumerator()=>((IEnumerable<int>)values).GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
            public bool Contains(int value)=>values.Contains(value);
            public void CopyTo(int[] array,int index)=>values.CopyTo(array,index);
            public void Add(int value)=>throw new NotSupportedException();
            public bool Remove(int value)=>throw new NotSupportedException();
            public void Clear()=>throw new NotSupportedException();
        }

        [Test]
        public void ArapCancellationAfterEntryLeavesTheCoordinateBufferUnchanged()
        {
            var points=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.one};
            var ix=new[] {0,1,2,1,3,2};var flat=new[] {0f,0f,1f,0f,0f,1f,1f,1f};var old=(float[])flat.Clone();
            using var cancellation=new System.Threading.CancellationTokenSource();
            var vertices=new CancelOnCount(Enumerable.Range(0,points.Length).ToArray(),cancellation);
            Assert.Throws<OperationCanceledException>(()=>ArapParameterization.Reparameterize(points,ix,new[] {0,1},vertices,
                flat,50,out _,cancellation.Token));
            CollectionAssert.AreEqual(old,flat);
        }

        [Test]
        public void ConjugateGradientIterationsObserveCancellationBeforeUpdatingTheSolution()
        {
            using var cancellation=new System.Threading.CancellationTokenSource();cancellation.Cancel();
            var solution=new double[2];
            var method=typeof(ArapParameterization).GetMethod("ConjugateGradientSolve",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            var error=Assert.Throws<System.Reflection.TargetInvocationException>(()=>method.Invoke(null,new object[] {
                new[] {0,1,2},new[] {1,0},new[] {-1d,-1d},new[] {2d,2d},new[] {1d,0d},solution,500,1e-6,cancellation.Token}));
            Assert.IsInstanceOf<OperationCanceledException>(error.InnerException);
            CollectionAssert.AreEqual(new double[2],solution);
        }

        [UnityTest]
        public IEnumerator AsyncFrozenTopologyPreparationMatchesTheSynchronousResult()
        {
            var data=SlitDisk();
            var coarse=new Mesh {name="Slit_LOD1",vertices=data.corners,triangles=Enumerable.Range(0,data.corners.Length).ToArray(),
                uv=data.uv,uv2=data.uv,normals=data.corners.Select(_=>Vector3.forward).ToArray()};
            var fine=UnityEngine.Object.Instantiate(coarse);fine.name="Slit_LOD0";
            var before=new[] {coarse,fine}.Select(TransferMeshSnapshot.Capture).ToArray();
            try
            {
                var levels=new[] {coarse,fine}.Select((m,i)=>new ReverseUvTransfer.Level {lod=1-i,
                    inputs=new[] {new ReverseUvTransfer.Input {mesh=m,key="Slit",toWorld=Matrix4x4.identity,usePreparedUv2=true}}}).ToArray();
                ReverseUvTransfer.Options Options()=>new ReverseUvTransfer.Options {seedResolution=128,seedDensity=32,projectionReach=.1f,
                    padding=2,regroupPreparedAtlas=true,frozenShellCascade=true,repackFinalAtlas=true};
                using var synchronous=ReverseUvTransfer.Build(levels,Options()).GetAwaiter().GetResult();
                var pending=ReverseUvTransfer.Build(levels,Options(),true);
                while(!pending.IsCompleted) yield return null;
                using var asynchronous=pending.GetAwaiter().GetResult();
                Assert.AreEqual(JsonUtility.ToJson(synchronous.report),JsonUtility.ToJson(asynchronous.report));
                for(int i=0;i<2;++i)
                {
                    CollectionAssert.AreEqual(synchronous.meshes[i][0].uv2,asynchronous.meshes[i][0].uv2);
                    CollectionAssert.AreEqual(before[i],TransferMeshSnapshot.Capture(i==0?coarse:fine));
                }
            }
            finally {UnityEngine.Object.DestroyImmediate(coarse);UnityEngine.Object.DestroyImmediate(fine);}
        }

        [Test]
        public void AConnectedChartWithAnInternalUvHoleBecomesOneDisk()
        {
            var data=SlitDisk();
            var result=UvShellTopologyRepair.TryRepair(data.corners,data.uv,RepackOptions.Default.arapIterations,default,out var reason);
            Assert.NotNull(result,reason);
            for(int i=0;i<result.Length;++i)
                for(int j=i+1;j<result.Length;++j)
                    if(data.corners[i].Equals(data.corners[j])) Assert.AreEqual(result[i],result[j]);
        }

        [Test]
        public void TopologyRepairHonorsTheExistingComparisonBudget()
        {
            var data=SlitDisk();var old=(Vector2[])data.uv.Clone();
            Assert.IsNull(UvShellTopologyRepair.TryRepair(data.corners,data.uv,RepackOptions.Default.arapIterations,
                default,out _,comparisonBudget:0));
            CollectionAssert.AreEqual(old,data.uv);
        }

        [Test]
        public void FrozenReverseRepairsTopologyBeforeCuttingItsFoundation()
        {
            var data=SlitDisk();
            var coarse=new Mesh {name="Slit_LOD1",vertices=data.corners,triangles=Enumerable.Range(0,data.corners.Length).ToArray(),
                uv=data.uv,uv2=data.uv,normals=data.corners.Select(_=>Vector3.forward).ToArray()};
            var fine=UnityEngine.Object.Instantiate(coarse);fine.name="Slit_LOD0";
            var before=new[] {coarse,fine}.Select(TransferMeshSnapshot.Capture).ToArray();
            try
            {
                var levels=new[] {coarse,fine}.Select((m,i)=>new ReverseUvTransfer.Level {lod=1-i,
                    inputs=new[] {new ReverseUvTransfer.Input {mesh=m,key="Slit",toWorld=Matrix4x4.identity,usePreparedUv2=true}}}).ToArray();
                using var result=ReverseUvTransfer.Build(levels,new ReverseUvTransfer.Options {
                    seedResolution=128,seedDensity=32,projectionReach=.1f,padding=2,
                    regroupPreparedAtlas=true,frozenShellCascade=true,repackFinalAtlas=true}).GetAwaiter().GetResult();
                var mesh=result.meshes[0][0];var uv=mesh.uv2;var positions=mesh.vertices;
                Assert.AreEqual(coarse.triangles.Length,mesh.triangles.Length);
                for(int i=0;i<uv.Length;++i)
                    for(int j=i+1;j<uv.Length;++j)
                        if(positions[i].Equals(positions[j])) Assert.AreEqual(uv[i],uv[j],"The spurious internal cut must be welded before foundation cuts.");
                CollectionAssert.AreEqual(before[0],TransferMeshSnapshot.Capture(coarse));
                CollectionAssert.AreEqual(before[1],TransferMeshSnapshot.Capture(fine));
            }
            finally {UnityEngine.Object.DestroyImmediate(coarse);UnityEngine.Object.DestroyImmediate(fine);}
        }
    }
}
