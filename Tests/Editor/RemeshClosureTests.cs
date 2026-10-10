using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshClosureTests
    {
        static readonly Vector3[] Box = { new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,1,-1),new Vector3(-1,1,-1),
            new Vector3(-1,-1,1),new Vector3(1,-1,1),new Vector3(1,1,1),new Vector3(-1,1,1) };
        static readonly int[] Faces = {0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5};
        static int[] Missing(params int[] missing)=>Faces.Where((v,i)=>!missing.Contains(i/6)).ToArray();

        [TestCase(0,2,4)] [TestCase(0,2,5)] [TestCase(0,3,4)] [TestCase(0,3,5)]
        [TestCase(1,2,4)] [TestCase(1,2,5)] [TestCase(1,3,4)] [TestCase(1,3,5)]
        public void ThreePlaneCornersCloseEachBoxCornerWithoutMovingDonors(int a,int b,int c)
        {
            var source=Missing(a,b,c); var saved=(int[])source.Clone();
            var cap=RemeshPlanarCap.Prepare(Box,source,"0",default,true);
            Assert.AreEqual(6,cap.addedFaces); Assert.AreEqual(3,cap.localPatches);
            var topology=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
            CollectionAssert.AreEqual(new[] {2},topology.euler);
            CollectionAssert.AreEqual(saved,source); CollectionAssert.AreEqual(source,cap.indices.Take(source.Length));
            for(int f=cap.originalFaces;f<cap.indices.Length/3;++f) Assert.Greater(cap.facePatches[f],0);
        }

        [TestCase(false,.125f)] [TestCase(true,8f)]
        public void ThreePlaneCornerSurvivesRigidTransformAndWinding(bool reverse,float scale)
        {
            var p=Box.Select(v=>Quaternion.Euler(23,39,17)*v*scale+new Vector3(3,-2,1)).ToArray();
            var ix=Missing(0,2,4); if(reverse) for(int i=0;i<ix.Length;i+=3) (ix[i],ix[i+2])=(ix[i+2],ix[i]);
            var cap=RemeshPlanarCap.Prepare(p,ix,"0",default,true);
            Assert.AreEqual(6,cap.addedFaces); Assert.AreEqual(0,RemeshTopology.Inspect(cap.positions,cap.indices).boundary.Count);
            CollectionAssert.AreEqual(p,cap.positions.Take(p.Length));
        }

        static (Vector3[],int[]) TorusGap(int count=12,int ring=6)
        {
            var p=new Vector3[count*ring]; var ix=new List<int>();
            for(int i=0;i<count;++i) for(int j=0;j<ring;++j) {
                float theta=i*2*Mathf.PI/count,phi=j*2*Mathf.PI/ring;
                p[i*ring+j]=new Vector3((2+.5f*Mathf.Cos(phi))*Mathf.Cos(theta),(2+.5f*Mathf.Cos(phi))*Mathf.Sin(theta),.5f*Mathf.Sin(phi));
                if(i==0) continue;
                int a=i*ring+j,b=((i+1)%count)*ring+j,c=((i+1)%count)*ring+(j+1)%ring,d=i*ring+(j+1)%ring;
                ix.AddRange(new[] {a,b,c,a,c,d});
            }
            return(p,ix.ToArray());
        }

        [TestCase(RemeshClosureMode.Bridge)] [TestCase(RemeshClosureMode.Automatic)]
        public void TorusGapUsesAnnulusAndRetainsHandle(RemeshClosureMode mode)
        {
            var (p,ix)=TorusGap(); var cap=RemeshPlanarCap.Prepare(p,ix,"0,1",default,true,mode);
            Assert.AreEqual(12,cap.addedFaces); Assert.AreEqual(1,cap.patchEnds.Count);
            var topology=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
            CollectionAssert.AreEqual(new[] {0},topology.euler);
            CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [Test]
        public void AutomaticOppositeBoxHolesChooseDisksAndNotABridge()
        {
            var cap=RemeshPlanarCap.Prepare(Box,Missing(0,1),"0,1",default,true,RemeshClosureMode.Automatic);
            Assert.AreEqual(4,cap.addedFaces); Assert.AreEqual(2,cap.patchEnds.Count);
            CollectionAssert.AreEqual(new[] {2},RemeshTopology.Inspect(cap.positions,cap.indices).euler);
            Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(Box,Missing(0,1),"0",default,false,RemeshClosureMode.Bridge));
        }

        [Test]
        public void BridgeIncludesBothConnectedElementsAndUnitesTheirPreparedFaceProvenance()
        {
            var p = Box.Select(v => v + Vector3.back * 2).Concat(Box.Select(v => v + Vector3.forward * 2)).ToArray();
            var ix = Missing(1).Concat(Missing(0).Select(v => v + 8)).ToArray();
            var cap = RemeshPlanarCap.Prepare(p, ix, "0,1", default, mode: RemeshClosureMode.Bridge, elementScopedContacts: true);
            Assert.AreEqual(8, cap.addedFaces); Assert.AreEqual(0, cap.remainingBoundaryEdges);
            Assert.AreNotEqual(cap.externalContacts.faceElements[0], cap.externalContacts.faceElements[10]);
            Assert.IsTrue(cap.faceElements.All(id => id == cap.faceElements[0]));
            Assert.AreEqual(0, cap.externalContacts.excludedElementPairs);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions, cap.indices, default).All(v => v));
        }

        [Test]
        public void BridgeWithUnequalRimsPreservesEveryBorderVertexAndEdge()
        {
            var (points,source)=TorusGap(); var p=points.ToList(); var ix=source.ToList();
            var topology=RemeshTopology.Inspect(points,source); var edge=topology.edges.First(pair=>pair.Value.count==1);
            int f=edge.Value.firstFace;
            int k=Enumerable.Range(0,3).First(c=> {
                int a=topology.slots[ix[f*3+c]],b=topology.slots[ix[f*3+(c+1)%3]];
                return (a<b?(a,b):(b,a))==edge.Key;
            });
            int x=ix[f*3+k],y=ix[f*3+(k+1)%3],z=ix[f*3+(k+2)%3],m=p.Count;
            p.Add((p[x]*3+p[y])/4); ix.RemoveRange(f*3,3); ix.AddRange(new[] {x,m,z,m,y,z});
            var cap=RemeshPlanarCap.Prepare(p.ToArray(),ix.ToArray(),"0,1",default,false,RemeshClosureMode.Bridge);
            Assert.AreEqual(13,cap.addedFaces); Assert.AreEqual(p.Count,cap.positions.Length);
            var closed=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(closed.Valid,closed.Description); Assert.AreEqual(0,closed.boundary.Count);
            CollectionAssert.AreEqual(new[] {0},closed.euler);
        }

        [Test]
        public void BridgeRefusesObstacleAndNeverPublishesAPartialWinner()
        {
            var (points,source)=TorusGap(); var p=points.ToList(); var ix=source.ToList();
            float angle=Mathf.PI/12; var radial=new Vector3(Mathf.Cos(angle),Mathf.Sin(angle),0);
            p.Add(radial*1+Vector3.back); p.Add(radial*3+Vector3.back); p.Add(radial*3+Vector3.forward); p.Add(radial*1+Vector3.forward);
            int n=points.Length; ix.AddRange(new[] {n,n+1,n+2,n,n+2,n+3});
            var saved=ix.ToArray();
            Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(p.ToArray(),saved,"0,1",default,false,RemeshClosureMode.Bridge));
            var refused = RemeshPlanarCap.Prepare(p.ToArray(), saved, "0,1", default, false, RemeshClosureMode.Bridge, continueOnRefusal: true);
            Assert.AreEqual(2, refused.loopFailures.Count); Assert.AreEqual(refused.loopFailures[0], refused.loopFailures[1]);
            Assert.AreEqual(0, refused.addedFaces); Assert.IsEmpty(refused.patchEnds);
            Assert.IsTrue(refused.facePatches.All(id => id == 0));
            CollectionAssert.AreEqual(saved, refused.indices); CollectionAssert.AreEqual(p, refused.positions);
            var owners = Enumerable.Repeat(0, source.Length / 3).Concat(new[] { 1, 1 }).ToArray();
            var accepted = RemeshPlanarCap.Prepare(p.ToArray(), saved, "0,1", default, false, RemeshClosureMode.Bridge, sourceFaceOwners: owners);
            Assert.Greater(accepted.externalContacts.count, 0);
            var contacts = new RemeshPlanarCap.ExternalContacts { faceOwners = owners, closingOwners = new HashSet<int> { 0 } };
            int trials = 0;
            RemeshPlanarCap.AuditContacts(accepted.positions, accepted.positions.Select(RemeshCapIntersection.Point).ToArray(),
                accepted.indices, saved.Length / 3, default, ref trials, out _, contacts);
            Assert.AreEqual(contacts.count, accepted.externalContacts.count, "Diagnostics must describe only the winning Bridge candidate.");
            CollectionAssert.AreEqual(ix,saved);
            Assert.Throws<OperationCanceledException>(()=>RemeshPlanarCap.Prepare(points,source,"0,1",new System.Threading.CancellationToken(true),false,RemeshClosureMode.Bridge));
            Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(points,source,"0,1",default,false,(RemeshClosureMode)999));
        }

        [Test]
        public void ProjectedMaskIdentifiesClosureOriginalAndMixedFaces()
        {
            var p=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.one};
            var source=new[] {0,1,2,1,3,2}; var labels=new[] {0,3};
            CollectionAssert.AreEqual(labels,RemeshSyntheticFaces.Project(p,source,p,source,labels,default));
            var spanning=new[] {0,1,3};
            CollectionAssert.AreEqual(new[] {-1},RemeshSyntheticFaces.Project(p,spanning,p,source,labels,default));
        }

        [Test]
        public void RegionAreaIsInvariantUnderTriangleSubdivisionAndCountsWorldScale()
        {
            var p=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.right+Vector3.up};
            var mesh=new RemeshNative.IndexedMesh {positions=p,indices=new[] {0,1,2,1,3,2}}.PrepareChannels();
            var groups=RemeshSyntheticFaces.Group(mesh,new[] {2,2},Matrix4x4.Scale(new Vector3(2,3,1)));
            Assert.AreEqual(1,groups.faces.Count); Assert.AreEqual(6,groups.areas[0],1e-6);
            var split=new RemeshNative.IndexedMesh {positions=mesh.indices.Select(v=>p[v]).ToArray(),indices=Enumerable.Range(0,6).ToArray()}.PrepareChannels();
            var seams=RemeshSyntheticFaces.Group(split,new[] {2,2},Matrix4x4.Scale(new Vector3(2,3,1)));
            Assert.AreEqual(1,seams.faces.Count); Assert.AreEqual(groups.areas[0],seams.areas[0]);
            var different=RemeshSyntheticFaces.Group(mesh,new[] {2,-1},Matrix4x4.identity);
            Assert.AreEqual(2,different.faces.Count);
        }

        [Test]
        public void RemovingRegionInvalidatesUvAndBakeAndRestoresExactOptimizedMesh()
        {
            var mesh=new RemeshNative.IndexedMesh {positions=Box,indices=Faces}.PrepareChannels();
            var labels=new int[12]; labels[0]=labels[1]=1;
            var node=new RemeshPipeline.Node {simplified=mesh,completeSimplified=mesh,syntheticFaces=labels,completeSyntheticFaces=labels,
                selectedSyntheticFaces=new bool[12],geometry=new RemeshNative.Geometry(),maps=new RemeshBaker.Maps()};
            node.selectedSyntheticFaces[0]=node.selectedSyntheticFaces[1]=true;
            var pipeline=new RemeshPipeline();
            var nodes=(List<RemeshPipeline.Node>)typeof(RemeshPipeline).GetField("nodes",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(pipeline);
            nodes.Add(node);
            try {
                pipeline.RemoveSelectedSyntheticFaces();
                Assert.AreEqual(10,node.simplified.TriangleCount); Assert.IsNull(node.geometry); Assert.IsNull(node.maps);
                CollectionAssert.AreEqual(Faces.Skip(6),node.simplified.indices); Assert.AreSame(Box,node.simplified.positions);
                pipeline.RestoreSyntheticFaces(); Assert.AreSame(mesh,node.simplified); CollectionAssert.AreEqual(labels,node.syntheticFaces);
                node.selectedSyntheticFaces[2]=true;
                Assert.Throws<InvalidOperationException>(()=>pipeline.RemoveSelectedSyntheticFaces()); Assert.AreSame(mesh,node.simplified);
            }
            finally {pipeline.Dispose();}
        }
    }
}
