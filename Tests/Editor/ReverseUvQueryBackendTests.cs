using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ReverseUvQueryBackendTests
    {
        [Test] public void SelectionKeepsSmallTreesAndSmallBatchesManaged()
        {
            Assert.False(ReverseUvQueryBackend.SelectEmbree(ReverseUvQueryBackend.Mode.Auto,1023,1024,true));
            Assert.False(ReverseUvQueryBackend.SelectEmbree(ReverseUvQueryBackend.Mode.Auto,2048,511,true));
            Assert.True(ReverseUvQueryBackend.SelectEmbree(ReverseUvQueryBackend.Mode.Auto,1024,512,true));
            Assert.False(ReverseUvQueryBackend.SelectEmbree(ReverseUvQueryBackend.Mode.Managed,8192,8192,true));
            Assert.False(ReverseUvQueryBackend.SelectEmbree(ReverseUvQueryBackend.Mode.Auto,8192,8192,false));
        }
        [TestCase(false)] [TestCase(true)]
        public void BatchesRetainEveryResultBitAndReuseNativeBuffers(bool native)
        {
            using var library=native?NativeQueryTestLibrary.OpenFromEnvironment():null;
            if(native && library==null) Assert.Ignore("Private query library not configured.");
            var vertices=new Vector3[1024*3];var indices=new int[vertices.Length];var normals=new Vector3[1024];
            for(int f=0;f<normals.Length;++f) {
                // Coincident UV-seam identities plus separated thin layers.
                float x=(f%32)*.04f,z=(f/32%4)*.01f;
                vertices[f*3]=new Vector3(x,0,z);vertices[f*3+1]=new Vector3(x+.03f,0,z);vertices[f*3+2]=new Vector3(x,.03f,z);
                normals[f]=f%2==0?Vector3.forward:Vector3.back;
                for(int k=0;k<3;++k) indices[f*3+k]=f*3+k;
            }
            var tree=new TriangleBvh(vertices,indices);
            using var scene=ReverseUvQueryBackend.Scene.TryCreate(tree,normals,library?.api,default);
            if(native) Assert.NotNull(scene);
            var buffers=new ReverseUvQueryBackend.Buffers();
            for(int repeat=0;repeat<3;++repeat) {
                const int count=1200;
                for(int i=0;i<count;++i) buffers.queries[i]=new ReverseUvQueryBackend.Query {mode=4,
                    origin=new Vector3((i%32)*.04f+(i%3==0?0:.003f),.004f,(i%4)*.01f+.002f),
                    direction=i%2==0?Vector3.forward:Vector3.back,dotMin=.5f,reach=i%7==0?float.PositiveInfinity:i%7==1?0:.05f};
                buffers.Resolve(tree,normals,scene,count,default,null);
                for(int i=0;i<count;++i) {
                    var q=buffers.queries[i];var h=tree.FindNearestNormalFiltered(q.origin,q.direction,normals,q.dotMin,q.reach);
                    Exact(h,buffers.answers[i]);
                }
            }
            Assert.AreEqual(native?1:0,buffers.nativeAllocations);
            var canceled=new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(()=>buffers.Resolve(tree,normals,scene,512,canceled,null));
            if(scene!=null) {scene.Dispose();Assert.Throws<ObjectDisposedException>(()=>buffers.Resolve(tree,normals,scene,512,default,null));}
        }
        [Test] public void CapacityOverflowUsesCompleteOriginalQuery()
        {
            using var library=NativeQueryTestLibrary.OpenFromEnvironment();
            if(library==null) Assert.Ignore("Private query library not configured.");
            var positions=new Vector3[1024*3];var indices=new int[positions.Length];var normals=new Vector3[1024];
            for(int f=0;f<normals.Length;++f) {
                positions[f*3]=Vector3.zero;positions[f*3+1]=Vector3.right;positions[f*3+2]=Vector3.up;normals[f]=Vector3.forward;
                for(int k=0;k<3;++k) indices[f*3+k]=f*3+k;
            }
            var tree=new TriangleBvh(positions,indices);
            using var scene=ReverseUvQueryBackend.Scene.TryCreate(tree,normals,library.api,default);
            var buffers=new ReverseUvQueryBackend.Buffers();buffers.queries[0]=new ReverseUvQueryBackend.Query {
                mode=4,origin=new Vector3(.2f,.2f,.01f),direction=Vector3.forward,dotMin=.5f,reach=.05f};
            buffers.Resolve(tree,normals,scene,1,default,null);Assert.AreEqual(-1,buffers.counts[0]);
            Exact(tree.FindNearestNormalFiltered(buffers.queries[0].origin,Vector3.forward,normals,.5f,.05f),buffers.answers[0]);
        }
        static void Exact(TriangleBvh.HitResult h,ReverseUvQueryBackend.Answer a)
        {
            Assert.AreEqual(h.triangleIndex,a.tri);Bits(h.distSq,a.distance);
            Bits(h.barycentric.x,a.bary.x);Bits(h.barycentric.y,a.bary.y);Bits(h.barycentric.z,a.bary.z);
            Bits(h.point.x,a.point.x);Bits(h.point.y,a.point.y);Bits(h.point.z,a.point.z);
        }
        static void Bits(float a,float b)=>Assert.AreEqual(BitConverter.SingleToInt32Bits(a),BitConverter.SingleToInt32Bits(b));

        [TestCase(false)] [TestCase(true)]
        public void GlobalDiscoveryRetainsSeparateChartHitsAcrossBlocksAndFailure(bool fail)
        {
            using var library=NativeQueryTestLibrary.OpenFromEnvironment();
            if(library==null) Assert.Ignore("Private query library not configured.");
            var charts=new List<ReverseUvTransfer.Chart>();
            foreach(float z in new[] {0f,.02f}) {
                var p=new[] {new Vector3(-1,-1,z),new Vector3(1,-1,z),new Vector3(1,1,z),new Vector3(-1,1,z)};
                var t=new[] {0,1,2,0,2,3};var n=new[] {Vector3.forward,Vector3.forward};
                charts.Add(new ReverseUvTransfer.Chart {positions=p,indices=t,normals=n,faces=new int[2],bvh=new TriangleBvh(p,t)});
            }
            const int faces=4352;
            var surface=new ReverseUvTransfer.Surface {positions=new Vector3[faces*3],indices=new int[faces*3],
                faces=new ReverseUvTransfer.Face[faces],orientation=1};
            for(int f=0;f<faces;++f) {
                surface.faces[f]=new ReverseUvTransfer.Face();int t=f*3;
                surface.positions[t]=new Vector3(-.5f,-.5f,.001f);surface.positions[t+1]=new Vector3(.5f,-.5f,.001f);
                surface.positions[t+2]=new Vector3(-.5f,.5f,.001f);
                for(int k=0;k<3;++k) surface.indices[t+k]=t+k;
            }
            var api=fail?new ReverseUvQueryBackend.Api {create=library.api.create,destroy=library.api.destroy,
                proposals=library.api.proposals,candidates=(handle,q,candidates,counts,count,capacity,workers)=>2}:library.api;
            var profile=new TransferPerformanceProfile();
            var options=new ReverseUvTransfer.Options {queryBackend=ReverseUvQueryBackend.Mode.Embree,queryApi=api,performance=profile};
            using var cancellation=new CancellationTokenSource();
            using(var batch=new ReverseUvTransfer.ProjectionBatch(charts,options,cancellation.Token)) {
                foreach(int first in new[] {0,4096}) {
                    batch.Prepare(surface,first);
                    for(int f=first;f<Math.Min(faces,first+4096);++f) foreach(var chart in charts) {
                        bool cached=batch.Get(surface,f,chart,out var actual);Assert.AreEqual(!fail,cached);
                        if(cached) {
                            int t=f*3;var center=(surface.positions[t]+surface.positions[t+1]+surface.positions[t+2])/3;
                            var h=chart.bvh.FindNearestNormalFiltered(center,Vector3.forward,chart.normals,.5f,.05f);
                            Exact(h,new ReverseUvQueryBackend.Answer {tri=actual.triangleIndex,distance=actual.distSq,bary=actual.barycentric,point=actual.point});
                        }
                    }
                }
                cancellation.Cancel();Assert.Throws<OperationCanceledException>(()=>batch.Prepare(surface,0));
            }
            Assert.AreEqual(1,profile.samples.Find(s=>s.stage=="Native query buffer allocations").count);
        }
    }
}
