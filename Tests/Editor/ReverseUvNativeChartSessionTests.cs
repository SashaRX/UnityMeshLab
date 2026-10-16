using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvNativeChartSessionTests
    {
        [Test]
        public void ResidentProxyStreamsKeepFamilyOrderAndReuseOwnedBuffers()
        {
            int computes=0;
            var api=new XatlasNative.Api {create=()=>{},destroy=()=>{},
                addUvMesh=(uv,v,indices,i,materials,f)=>0,computeCharts=()=>++computes};
            using var binding=XatlasNative.UseForTests(api);
            using var session=new ReverseUvNativeChartSession();
            Assert.IsTrue(session.TryAcquire(default));
            var a=new[] {Vector2.zero,Vector2.right,Vector2.up};
            var b=new[] {Vector2.one,Vector2.right*2,Vector2.up*2};
            var first=session.Input(new[] {a,b},default);
            Assert.AreSame(first,session.Input(new[] {a,b},default));
            Assert.AreEqual(1,computes);
            var reversed=session.Input(new[] {b,a},default);
            Assert.AreNotSame(first,reversed);Assert.AreEqual(2,computes);
            Assert.AreEqual(b[0],reversed.points[0]);Assert.AreEqual(a[0],reversed.points[3]);
            CollectionAssert.AreEqual(new uint[] {0,1},reversed.materials);
        }

        [Test]
        public void PreparedInputOwnsItsKeyAndDistinguishesEveryNativeStream()
        {
            int creates=0, destroys=0, computes=0;
            var api=new XatlasNative.Api {create=()=>++creates,destroy=()=>++destroys,
                addUvMesh=(uv,v,indices,i,materials,f)=>0,computeCharts=()=>++computes};
            using var binding=XatlasNative.UseForTests(api);
            var session=new ReverseUvNativeChartSession();
            var uv=new float[] {0,0,1,0,0,1};var triangles=new uint[] {0,1,2};var materials=new uint[] {0};
            try
            {
                Assert.IsTrue(session.TryAcquire(default));
                Assert.IsFalse(XatlasRepack.TryAcquireNativeSession(),"Resident charts must own the global atlas lease.");
                Assert.AreEqual(0,session.Prepare(uv,triangles,materials,default));
                Assert.AreEqual(0,session.Prepare((float[])uv.Clone(),(uint[])triangles.Clone(),(uint[])materials.Clone(),default));
                Assert.AreEqual(1,computes);
                uv[0]=.25f;session.Prepare(uv,triangles,materials,default);
                triangles[0]=2;session.Prepare(uv,triangles,materials,default);
                materials[0]=1;session.Prepare(uv,triangles,materials,default);
                uv[0]=0;session.Prepare(uv,triangles,materials,default);
                uv[0]=BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
                session.Prepare(uv,triangles,materials,default);
                Assert.AreEqual(6,computes,"Signed-zero changes must not alias native input bytes.");
                using var cancelled=new CancellationTokenSource();cancelled.Cancel();
                Assert.Throws<OperationCanceledException>(()=>session.Prepare(uv,triangles,materials,cancelled.Token));
            }
            finally {session.Dispose();session.Dispose();}
            Assert.AreEqual(creates,destroys);
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());XatlasRepack.ReleaseNativeSession();
            Assert.Throws<ObjectDisposedException>(()=>session.TryAcquire(default));
        }

        [TestCase(false)] [TestCase(true)]
        public void FailedPreparationNeverLeavesAReusableKeyOrLeaksItsLease(bool throwOnCompute)
        {
            int destroys=0, computes=0, adds=0;
            var api=new XatlasNative.Api {create=()=>{},destroy=()=>++destroys,
                addUvMesh=(uv,v,indices,i,materials,f)=>{++adds;return throwOnCompute?0:7;},
                computeCharts=()=>{++computes;throw new InvalidOperationException("fixture failure");}};
            using var binding=XatlasNative.UseForTests(api);
            using(var session=new ReverseUvNativeChartSession())
            {
                Assert.IsTrue(session.TryAcquire(default));
                for(int i=0;i<2;++i)
                {
                    int Prepare()=>session.Prepare(new float[] {0,0,1,0,0,1},new uint[] {0,1,2},new uint[] {0},default);
                    if(throwOnCompute) Assert.Throws<InvalidOperationException>(()=>Prepare());
                    else Assert.AreEqual(7,Prepare());
                }
            }
            Assert.AreEqual(2,adds);Assert.AreEqual(2,destroys);Assert.AreEqual(throwOnCompute?2:0,computes);
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());XatlasRepack.ReleaseNativeSession();
        }

        [Test]
        public void BusySessionDoesNotReleaseAnotherOwnersLease()
        {
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            try
            {
                using(var session=new ReverseUvNativeChartSession()) Assert.IsFalse(session.TryAcquire(default));
                Assert.IsFalse(XatlasRepack.TryAcquireNativeSession());
            }
            finally {XatlasRepack.ReleaseNativeSession();}
        }
    }
}
