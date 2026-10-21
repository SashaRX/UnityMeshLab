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
            session.Acquire(default);
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
                session.Acquire(default);
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
            Assert.Throws<ObjectDisposedException>(()=>session.Acquire(default));
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
                session.Acquire(default);
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

        /// <summary>The editor thread reports a busy session instead of blocking, and never releases another owner's lease.</summary>
        [Test]
        public void BusySessionDoesNotReleaseAnotherOwnersLease()
        {
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            try
            {
                using(var session=new ReverseUvNativeChartSession())
                    Assert.Throws<InvalidOperationException>(()=>session.Acquire(default));
                Assert.IsFalse(XatlasRepack.TryAcquireNativeSession());
            }
            finally {XatlasRepack.ReleaseNativeSession();}
        }

        /// <summary>A worker waits for the current owner instead of refusing its final pack.</summary>
        [Test]
        public void WorkerWaitsForABusySessionUntilItsOwnerReleases()
        {
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            System.Threading.Tasks.Task worker;
            try
            {
                worker=System.Threading.Tasks.Task.Run(()=>{using var session=new ReverseUvNativeChartSession();session.Acquire(default);});
                Assert.IsFalse(worker.Wait(200),"The worker must not acquire a lease that is still owned.");
            }
            finally {XatlasRepack.ReleaseNativeSession();}
            Assert.IsTrue(worker.Wait(10000),"The worker acquires the session after its owner releases it.");
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession(),"Disposing the worker's session releases its lease.");
            XatlasRepack.ReleaseNativeSession();
        }

        /// <summary>Cancelling a waiting worker leaves the owner's lease intact.</summary>
        [Test]
        public void WaitingWorkerCanBeCancelled()
        {
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            try
            {
                using var cancel=new CancellationTokenSource();
                var worker=System.Threading.Tasks.Task.Run(()=>XatlasRepack.AcquireNativeSessionForRequiredWork("test",cancel.Token));
                Assert.IsFalse(worker.Wait(200));
                cancel.Cancel();
                var error=Assert.Throws<AggregateException>(()=>worker.Wait(10000));
                Assert.IsInstanceOf<OperationCanceledException>(error.InnerException);
                Assert.IsFalse(XatlasRepack.TryAcquireNativeSession(),"The owner still holds its lease.");
            }
            finally {XatlasRepack.ReleaseNativeSession();}
        }

        /// <summary>A thread that already owns the lease is refused rather than waiting on itself forever.</summary>
        [Test]
        public void OwnerThreadIsRefusedInsteadOfWaitingForItself()
        {
            var worker=System.Threading.Tasks.Task.Run(()=>
            {
                Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
                try {Assert.Throws<InvalidOperationException>(()=>XatlasRepack.AcquireNativeSessionForRequiredWork("test",default));}
                finally {XatlasRepack.ReleaseNativeSession();}
            });
            Assert.IsTrue(worker.Wait(10000));
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());XatlasRepack.ReleaseNativeSession();
        }
    }
}
