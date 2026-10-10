using System;
using System.Collections.Generic;
using System.IO;
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

        static (Vector3[],int[]) CurvedPrism(int count = 20)
        {
            var p = new Vector3[count * 2 + 1]; var ix = new List<int>();
            for (int i = 0; i < count; ++i) {
                float angle = i * 2 * Mathf.PI / count;
                p[i] = new Vector3(Mathf.Cos(angle),Mathf.Sin(angle),0);
                p[i+count] = p[i] + Vector3.forward * (1 + .12f * Mathf.Sin(angle * 3));
                int j = (i+1) % count;
                ix.AddRange(new[] {i,j,j+count,i,j+count,i+count,count*2,j,i});
            }
            return (p,ix.ToArray());
        }

        [TestCase(false,.125f)] [TestCase(false,1f)] [TestCase(true,8f)]
        public void SurfaceCapClosesCurvedRimAndPreservesSourceAndSyntheticMask(bool reverse, float scale)
        {
            var (p,ix) = CurvedPrism();
            p = p.Select(v => Quaternion.Euler(23,39,17) * v * scale + new Vector3(3,-2,1)).ToArray();
            if (reverse) for (int i=0;i<ix.Length;i+=3) (ix[i],ix[i+2]) = (ix[i+2],ix[i]);
            var points = (Vector3[])p.Clone(); var indices = (int[])ix.Clone();
            var cap = RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.SurfaceCaps);
            var repeat = RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.SurfaceCaps);
            Assert.AreEqual(18,cap.addedFaces); Assert.AreEqual(1,cap.patchEnds.Count);
            Assert.AreEqual(0,cap.remainingBoundaryEdges);
            var topology = RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(topology.Valid,topology.Description); CollectionAssert.AreEqual(new[] {2},topology.euler);
            CollectionAssert.AreEqual(points,p); CollectionAssert.AreEqual(indices,ix);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
            CollectionAssert.AreEqual(cap.indices,repeat.indices);
            Assert.IsTrue(cap.facePatches.Take(ix.Length/3).All(id => id == 0));
            Assert.IsTrue(cap.facePatches.Skip(ix.Length/3).All(id => id == 1));
        }

        [Test]
        public void SurfaceCapBudgetAndCancellationNeverPublishPartialEars()
        {
            var (p,ix) = CurvedPrism(); var points = (Vector3[])p.Clone(); var indices = (int[])ix.Clone();
            var loop = Enumerable.Range(20,20).Reverse().ToList(); int trials = 0;
            StringAssert.Contains("budget",Assert.Throws<InvalidOperationException>(() =>
                RemeshSurfaceCap.Generate(p,ix,loop,default,ref trials,out _,maxCandidates:1)).Message);
            Assert.Throws<OperationCanceledException>(() => RemeshSurfaceCap.Generate(p,ix,loop,
                new System.Threading.CancellationToken(true),ref trials,out _));
            CollectionAssert.AreEqual(points,p); CollectionAssert.AreEqual(indices,ix);
        }

        [TestCase(false)] [TestCase(true)]
        public void SurfaceCapAuditsObstacleAndCanExcludeAnotherElement(bool elementScoped)
        {
            var (p,ix) = CurvedPrism();
            p = p.Concat(new[] {new Vector3(0,0,.5f),new Vector3(0,0,1.5f),new Vector3(.5f,0,1)}).ToArray();
            ix = ix.Concat(new[] {41,42,43}).ToArray();
            var cap = RemeshPlanarCap.Prepare(p,ix,"0",default,mode:RemeshClosureMode.SurfaceCaps,
                continueOnRefusal:true,elementScopedContacts:elementScoped);
            Assert.AreEqual(elementScoped ? 18 : 0,cap.addedFaces);
            Assert.AreEqual(elementScoped ? 0 : 1,cap.loopFailures.Count);
            Assert.AreEqual(elementScoped ? 3 : 23,cap.remainingBoundaryEdges);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [Test]
        public void SurfaceCapKeepsAnIndependentClosureWhenAnotherRimExceedsItsBudget()
        {
            var (large,largeIndices) = CurvedPrism(65); var (small,smallIndices) = CurvedPrism();
            var p = large.Concat(small.Select(v => v + Vector3.right*4)).ToArray();
            var ix = largeIndices.Concat(smallIndices.Select(v => v+large.Length)).ToArray();
            var cap = RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.SurfaceCaps,
                continueOnRefusal:true,elementScopedContacts:true);
            Assert.AreEqual(18,cap.addedFaces); Assert.AreEqual(1,cap.patchEnds.Count);
            Assert.AreEqual(1,cap.loopFailures.Count); Assert.AreEqual(65,cap.remainingBoundaryEdges);
            StringAssert.Contains("64 edges",cap.loopFailures.Single().Value);
            Assert.IsTrue(RemeshTopology.Inspect(cap.positions,cap.indices).Valid);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

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

        static (Vector3[],int[]) LongitudinalTorusGap(int count,int ring,int strip)
        {
            var (p,_) = TorusGap(count,ring); var ix = new List<int>();
            for (int i=0;i<count;++i) for (int j=0;j<ring;++j) {
                if (j==strip) continue;
                int a=i*ring+j,b=((i+1)%count)*ring+j,c=((i+1)%count)*ring+(j+1)%ring,d=i*ring+(j+1)%ring;
                ix.AddRange(new[] {a,b,c,a,c,d});
            }
            return (p,ix.ToArray());
        }

        static (Vector3[],int[]) SubdividedTorusGap(int ring,int divisions,bool uneven=false,bool offset=false)
        {
            var (points,source)=TorusGap(16,ring);
            if (offset) for (int i=0;i<ring;++i) points[i]+=new Vector3(.08f,-.03f,.02f);
            var p=points.ToList(); var faces=new List<int>();
            for (int f=0;f<source.Length;f+=3) {
                int k=Enumerable.Range(0,3).Where(corner=>source[f+corner]<ring && source[f+(corner+1)%3]<ring).DefaultIfEmpty(-1).First();
                if (k<0) { faces.AddRange(source.Skip(f).Take(3)); continue; }
                int a=source[f+k],b=source[f+(k+1)%3],c=source[f+(k+2)%3],previous=a;
                for (int j=1;j<divisions;++j) {
                    float fraction=(float)j/divisions; if (uneven) fraction*=fraction;
                    int m=p.Count; p.Add(p[a]+(p[b]-p[a])*fraction);
                    faces.AddRange(new[] {previous,m,c}); previous=m;
                }
                faces.AddRange(new[] {previous,b,c});
            }
            return (p.ToArray(),faces.ToArray());
        }

        [TestCase(RemeshClosureMode.Bridge,16,0,false,1f)]
        [TestCase(RemeshClosureMode.Automatic,16,0,false,1f)]
        [TestCase(RemeshClosureMode.Automatic,32,4,false,1f)]
        [TestCase(RemeshClosureMode.Automatic,32,8,false,1f)]
        [TestCase(RemeshClosureMode.Automatic,32,12,false,1f)]
        [TestCase(RemeshClosureMode.Automatic,64,15,false,1f)]
        [TestCase(RemeshClosureMode.Automatic,24,8,true,.125f)]
        [TestCase(RemeshClosureMode.Bridge,24,4,true,8f)]
        public void LongitudinalCutKeepsOneComponentAndRestoresTheTorusWithAnAnnulus(
            RemeshClosureMode mode,int count,int strip,bool reverse,float scale)
        {
            const int ring=16;
            var (p,ix) = LongitudinalTorusGap(count,ring,strip);
            if (reverse) {
                p=p.Select(v=>Quaternion.Euler(23,39,17)*v*scale+new Vector3(3,-2,1)).ToArray();
                for (int i=0;i<ix.Length;i+=3) (ix[i],ix[i+2])=(ix[i+2],ix[i]);
            }
            var points=(Vector3[])p.Clone(); var source=(int[])ix.Clone();
            var before=RemeshTopology.Inspect(p,ix);
            Assert.IsTrue(before.Valid,before.Description); CollectionAssert.AreEqual(new[] {0},before.euler);
            Assert.AreEqual(count*2,before.boundary.Count);
            var cap=RemeshPlanarCap.Prepare(p,ix,"all",default,mode:mode,
                continueOnRefusal:true,elementScopedContacts:true,bridgeCapFallback:true);
            Assert.IsEmpty(cap.loopFailures); Assert.IsEmpty(cap.bridgeCapFallbacks);
            Assert.AreEqual(2,cap.loops); Assert.AreEqual(2,cap.bridgePartners.Count); Assert.AreEqual(1,cap.patchEnds.Count);
            Assert.AreEqual(count*2,cap.addedFaces); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            var after=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); CollectionAssert.AreEqual(new[] {0},after.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions,cap.indices,default).All(v=>v));
            var patch=cap.indices.Skip(ix.Length).ToArray();
            var annulus=RemeshTopology.Inspect(cap.positions,patch);
            Assert.IsTrue(annulus.Valid,annulus.Description); CollectionAssert.AreEqual(new[] {0},annulus.euler);
            Assert.IsTrue(annulus.boundary.SetEquals(before.boundary));
            for (int f=0;f<patch.Length;f+=3) for (int k=0;k<3;++k) {
                int a=patch[f+k],b=patch[f+(k+1)%3];
                Assert.IsTrue(a%ring==strip || a%ring==(strip+1)%ring,"Bridge must stay on the two selected circumferential rims.");
                int delta=Math.Abs(a/ring-b/ring);
                Assert.LessOrEqual(Math.Min(delta,count-delta),1,"Bridge must not shortcut across the central hole.");
            }
            Assert.IsTrue(cap.facePatches.Take(ix.Length/3).All(id=>id==0));
            Assert.IsTrue(cap.facePatches.Skip(ix.Length/3).All(id=>id==1));
            CollectionAssert.AreEqual(points,p); CollectionAssert.AreEqual(source,ix);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [Test]
        public void SeparateCapsOnTheInnerLongitudinalRimsLoseTheHandleDespiteValidTopology()
        {
            var (p,ix)=LongitudinalTorusGap(24,16,8);
            var cap=RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.Caps,
                continueOnRefusal:true,elementScopedContacts:true);
            Assert.IsEmpty(cap.loopFailures); Assert.AreEqual(2,cap.patchEnds.Count);
            Assert.AreEqual(44,cap.addedFaces); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            var after=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); CollectionAssert.AreEqual(new[] {2},after.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions,cap.indices,default).All(v=>v));
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [Test]
        public void RefusedLongitudinalBridgeFallsBackToOneClosedComponentWithoutTheHandle()
        {
            var (p,ix)=LongitudinalTorusGap(138,16,8);
            var before=RemeshTopology.Inspect(p,ix);
            CollectionAssert.AreEqual(new[] {0},before.euler); Assert.AreEqual(276,before.boundary.Count);
            var cap=RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.Automatic,
                continueOnRefusal:true,elementScopedContacts:true,bridgeCapFallback:true);
            Assert.IsEmpty(cap.loopFailures); Assert.IsEmpty(cap.bridgePartners); Assert.AreEqual(2,cap.bridgeCapFallbacks.Count);
            Assert.IsTrue(cap.bridgeCapFallbacks.Values.All(reason=>reason.Contains("search budget")));
            Assert.AreEqual(2,cap.patchEnds.Count); Assert.AreEqual(272,cap.addedFaces); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            var after=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); CollectionAssert.AreEqual(new[] {2},after.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions,cap.indices,default).All(v=>v));
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [TestCase(0,false)] [TestCase(0,true)] [TestCase(4,false)] [TestCase(8,true)]
        public void LongitudinalBridgeRetainsTheHandleThroughNativeRemeshSimplifyAndUv(int strip,bool solve)
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException ex) when (ex.InnerException is DllNotFoundException ||
                ex.InnerException is EntryPointNotFoundException || ex.InnerException is BadImageFormatException)
                { Assert.Ignore("Native remesh unavailable: "+ex.Message); }
            var (p,ix)=LongitudinalTorusGap(24,16,strip);
            var cap=RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.Automatic,
                elementScopedContacts:true,bridgeCapFallback:true);
            Assert.IsEmpty(cap.bridgeCapFallbacks); Assert.AreEqual(2,cap.bridgePartners.Count);
            var settings=new RemeshSettings {voxelResolution=64,solve=solve,maximumError=.02f,
                textureResolution=512,padding=3,mergeCharts=true};
            var voxel=RemeshNative.Voxelize(cap.positions,cap.indices,settings,default);
            var simplified=solve ? RemeshSurfaceRefine.Simplify(voxel,cap.positions,cap.indices,settings,default,out _) :
                RemeshNative.Simplify(voxel,settings,default,out _);
            var uv=RemeshNative.Unwrap(simplified,settings,default);
            foreach (var topology in new[] {RemeshTopology.Inspect(voxel.positions,voxel.indices),
                RemeshTopology.Inspect(simplified.positions,simplified.indices),RemeshTopology.Inspect(uv.positions,uv.indices)}) {
                Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
                CollectionAssert.AreEqual(new[] {0},topology.euler);
            }
            var quality=UvChartQuality.Measure(uv,default); var atlas=UvAtlasDiagnostics.Measure(uv,default);
            Assert.IsTrue(quality.valid); Assert.IsTrue(atlas.complete); Assert.AreEqual(0,atlas.invalidFaces);
            Assert.AreEqual(0,atlas.degenerateFaces); Assert.AreEqual(0,atlas.pairs); Assert.AreEqual(0,atlas.outOfBoundsVertices);
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

        [TestCase(.001f, false)] [TestCase(1f, false)] [TestCase(1000f, true)]
        public void AmbiguousAutomaticPartnersUseIndependentCapsOnlyWhenFallbackIsEnabled(float scale, bool reverse)
        {
            var (p,ix) = TorusGap();
            var points = p.Concat(p.Select(v => v + Vector3.forward * .1f)).Select(v => v * scale).ToArray();
            var indices = ix.Concat(ix.Select(v => v + p.Length)).ToArray();
            if (reverse) for (int i = 0; i < indices.Length; i += 3) (indices[i],indices[i+2]) = (indices[i+2],indices[i]);
            var original = (int[])indices.Clone(); var originalPoints = (Vector3[])points.Clone();
            var refused = RemeshPlanarCap.Prepare(points, indices, "all", default, mode:RemeshClosureMode.Automatic,
                continueOnRefusal:true, elementScopedContacts:true);
            Assert.AreEqual(4, refused.loopFailures.Count); Assert.IsEmpty(refused.bridgeCapFallbacks);
            Assert.AreEqual(0, refused.addedFaces);
            foreach (bool partial in new[] { false, true }) {
                var cap = RemeshPlanarCap.Prepare(points, indices, "all", default, mode:RemeshClosureMode.Automatic,
                    continueOnRefusal:partial, elementScopedContacts:true, bridgeCapFallback:true);
                Assert.AreEqual(4, cap.bridgeCapFallbacks.Count); Assert.IsEmpty(cap.bridgePartners); Assert.IsEmpty(cap.loopFailures);
                Assert.IsTrue(cap.bridgeCapFallbacks.Values.All(reason => reason.Contains("more than one collar partner")));
                Assert.AreEqual(16, cap.addedFaces); Assert.AreEqual(0, cap.remainingBoundaryEdges);
                var topology = RemeshTopology.Inspect(cap.positions, cap.indices);
                Assert.IsTrue(topology.Valid, topology.Description); CollectionAssert.AreEqual(new[] {2,2}, topology.euler);
                Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions, cap.indices, default).All(v => v));
                CollectionAssert.AreEqual(original, cap.indices.Take(indices.Length)); CollectionAssert.AreEqual(originalPoints, cap.positions);
            }
            CollectionAssert.AreEqual(original, indices); CollectionAssert.AreEqual(originalPoints, points);
        }

        [TestCase(8)] [TestCase(3)]
        public void TwoCutsInOneTorusRestoreOneHandle(int secondCut)
        {
            const int ring=8;
            var (p,source)=TorusGap(16,ring);
            var ix=source.Where((v,i)=>i/(ring*6)!=secondCut-1).ToArray();
            var before=RemeshTopology.Inspect(p,ix);
            Assert.AreEqual(2,before.euler.Count); Assert.AreEqual(ring*4,before.boundary.Count);
            var cap=RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.Automatic,
                continueOnRefusal:true,elementScopedContacts:true);
            Assert.IsEmpty(cap.loopFailures); Assert.AreEqual(4,cap.loops);
            Assert.AreEqual(4,cap.bridgePartners.Count); Assert.AreEqual(2,cap.patchEnds.Count);
            Assert.AreEqual(ring*4,cap.addedFaces); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            foreach (var pair in cap.bridgePartners) {
                int a=cap.boundaryLoops[pair.Key][0]/ring,b=cap.boundaryLoops[pair.Value][0]/ring;
                Assert.IsTrue(Math.Min(a,b)==0 && Math.Max(a,b)==1 ||
                    Math.Min(a,b)==secondCut && Math.Max(a,b)==secondCut+1,"Each Bridge must connect the two sides of its own cut.");
            }
            var first=RemeshTopology.Inspect(cap.positions,cap.indices.Take(cap.patchEnds[0]*3).ToArray());
            Assert.IsTrue(first.Valid,first.Description); Assert.AreEqual(1,first.euler.Count);
            Assert.AreEqual(ring*2,first.boundary.Count);
            var after=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); CollectionAssert.AreEqual(new[] {0},after.euler);
            Assert.IsTrue(cap.faceElements.All(id=>id==cap.faceElements[0]));
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [Test]
        public void BridgeCapFallbackIsOptInAndKeepsSuccessfulBridges()
        {
            var (p,ix) = TorusGap();
            var cap = RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:RemeshClosureMode.Bridge,bridgeCapFallback:true);
            Assert.AreEqual(2,cap.bridgePartners.Count); Assert.IsEmpty(cap.bridgeCapFallbacks);
            CollectionAssert.AreEqual(new[] {0},RemeshTopology.Inspect(cap.positions,cap.indices).euler);
            var settings = new RemeshSettings {planarCap=true,closureMode=RemeshClosureMode.Automatic};
            string prepareKey = RemeshPipeline.Key(RemeshPipeline.Stage.Prepare,settings,null);
            string remeshKey = RemeshPipeline.Key(RemeshPipeline.Stage.Remesh,settings,null);
            Assert.IsFalse(RemeshSettings.FromSavedJson("{\"planarCap\":true,\"closureMode\":2}").bridgeCapFallback);
            settings.bridgeCapFallback = true;
            Assert.IsTrue(RemeshSettings.FromSavedJson(JsonUtility.ToJson(settings)).bridgeCapFallback);
            Assert.AreNotEqual(prepareKey,RemeshPipeline.Key(RemeshPipeline.Stage.Prepare,settings,null));
            Assert.AreNotEqual(remeshKey,RemeshPipeline.Key(RemeshPipeline.Stage.Remesh,settings,null));
        }

        [TestCase(RemeshClosureMode.Bridge)] [TestCase(RemeshClosureMode.Automatic)]
        public void RefusedOverBudgetBridgeCanCloseBothRimsAsSeparatePlanarCaps(RemeshClosureMode mode)
        {
            var (p,ix) = TorusGap(16,138);
            var points = (Vector3[])p.Clone(); var indices = (int[])ix.Clone();
            var refused = RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:mode,continueOnRefusal:true);
            Assert.AreEqual(2,refused.loopFailures.Count); Assert.AreEqual(0,refused.addedFaces);
            Assert.IsEmpty(refused.bridgeCapFallbacks); CollectionAssert.AreEqual(ix,refused.indices);
            var progress = new List<int>();
            var cap = RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:mode,elementScopedContacts:true,
                loopCompleted:(loop,done,total)=>progress.Add(done),bridgeCapFallback:true);
            Assert.IsEmpty(cap.loopFailures); Assert.IsEmpty(cap.bridgePartners); Assert.AreEqual(2,cap.bridgeCapFallbacks.Count);
            Assert.IsTrue(cap.bridgeCapFallbacks.Values.All(reason=>reason.Contains("search budget")));
            Assert.AreEqual(272,cap.addedFaces); Assert.AreEqual(2,cap.patchEnds.Count); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            CollectionAssert.AreEqual(new[] {1,2},progress);
            var after = RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); CollectionAssert.AreEqual(new[] {2},after.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions,cap.indices,default).All(v=>v));
            Assert.IsTrue(cap.facePatches.Take(ix.Length/3).All(id=>id==0));
            Assert.AreEqual(136,cap.facePatches.Count(id=>id==1)); Assert.AreEqual(136,cap.facePatches.Count(id=>id==2));
            CollectionAssert.AreEqual(points,p); CollectionAssert.AreEqual(indices,ix);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [TestCase(8)] [TestCase(3)]
        public void TwoRefusedBridgesWithCapFallbackLeaveTwoClosedTorusSegments(int secondCut)
        {
            const int ring = 138;
            var (p,source) = TorusGap(16,ring);
            var ix = source.Where((v,i)=>i/(ring*6)!=secondCut-1).ToArray();
            var cap = RemeshPlanarCap.Prepare(p,ix,"all",default,mode:RemeshClosureMode.Automatic,
                continueOnRefusal:true,elementScopedContacts:true,bridgeCapFallback:true);
            Assert.IsEmpty(cap.loopFailures); Assert.IsEmpty(cap.bridgePartners);
            Assert.AreEqual(4,cap.bridgeCapFallbacks.Count); Assert.AreEqual(4,cap.patchEnds.Count);
            Assert.AreEqual(544,cap.addedFaces); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            var after = RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); CollectionAssert.AreEqual(new[] {2,2},after.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions,cap.indices,default).All(v=>v));
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [TestCase(false)] [TestCase(true)]
        public void CapFallbackAuditsEachRimIndependentlyAndRetainsTheOtherCap(bool intersectCap)
        {
            var (points,source) = TorusGap(); var p = points.ToList(); var ix = source.ToList();
            float angle = intersectCap ? 0 : Mathf.PI/12;
            var radial = new Vector3(Mathf.Cos(angle),Mathf.Sin(angle),0);
            p.Add(radial+Vector3.back); p.Add(radial*3+Vector3.back);
            p.Add(radial*3+Vector3.forward); p.Add(radial+Vector3.forward);
            int n=points.Length; ix.AddRange(new[] {n,n+1,n+2,n,n+2,n+3});
            var savedPoints=p.ToArray(); var savedIndices=ix.ToArray();
            var cap=RemeshPlanarCap.Prepare(savedPoints,savedIndices,"0,1",default,mode:RemeshClosureMode.Bridge,
                continueOnRefusal:true,bridgeCapFallback:true);
            Assert.AreEqual(2,cap.bridgeCapFallbacks.Count); Assert.IsEmpty(cap.bridgePartners);
            Assert.IsTrue(cap.bridgeCapFallbacks.Values.All(reason=>reason.Contains("contacts face")));
            Assert.AreEqual(intersectCap ? 1 : 0,cap.loopFailures.Count);
            Assert.AreEqual(intersectCap ? 1 : 2,cap.patchEnds.Count);
            Assert.AreEqual(intersectCap ? 4 : 8,cap.addedFaces);
            Assert.AreEqual(intersectCap ? 10 : 4,cap.remainingBoundaryEdges);
            if (intersectCap) StringAssert.Contains("contacts face",cap.loopFailures.Values.Single());
            var after=RemeshTopology.Inspect(cap.positions,cap.indices); Assert.IsTrue(after.Valid,after.Description);
            CollectionAssert.AreEqual(savedPoints,p); CollectionAssert.AreEqual(savedIndices,ix);
            CollectionAssert.AreEqual(savedIndices,cap.indices.Take(savedIndices.Length));
            string directory=Path.Combine(Path.GetTempPath(),"meshlab-bridge-fallback-test-"+Guid.NewGuid().ToString("N"));
            try {
                var metadata=new RemeshGeometryDiagnostics.FailureMetadata {
                    settingsJson=JsonUtility.ToJson(new RemeshSettings {bridgeCapFallback=true}) };
                string path=RemeshGeometryDiagnostics.WriteFailure(directory,savedPoints,savedIndices,null,null,metadata,cap);
                using var reader=new BinaryReader(File.OpenRead(path)); reader.ReadInt32(); Assert.AreEqual(3,reader.ReadInt32());
                var restored=JsonUtility.FromJson<RemeshGeometryDiagnostics.FailureMetadata>(reader.ReadString());
                CollectionAssert.AreEqual(cap.bridgeCapFallbacks.Keys,restored.bridgeCapFallbackLoops);
                CollectionAssert.AreEqual(cap.bridgeCapFallbacks.Values,restored.bridgeCapFallbackReasons);
                Assert.AreEqual(intersectCap ? 1 : 0,restored.refusedLoops.Length);
                Assert.IsTrue(RemeshSettings.FromSavedJson(restored.settingsJson).bridgeCapFallback);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
        }

        [Test]
        public void CapFallbackDoesNotReinterpretAnInvalidBridgeSelectionOrCancellation()
        {
            var (p,ix) = TorusGap();
            var cap = RemeshPlanarCap.Prepare(p,ix,"0",default,mode:RemeshClosureMode.Bridge,
                continueOnRefusal:true,bridgeCapFallback:true);
            Assert.AreEqual(1,cap.loopFailures.Count); Assert.IsEmpty(cap.bridgeCapFallbacks); Assert.AreEqual(0,cap.addedFaces);
            CollectionAssert.AreEqual(ix,cap.indices);
            Assert.Throws<OperationCanceledException>(()=>RemeshPlanarCap.Prepare(p,ix,"0,1",
                new System.Threading.CancellationToken(true),mode:RemeshClosureMode.Bridge,bridgeCapFallback:true));
        }

        [Test]
        public void CapFallbackRetainsTheSharedContactBudget()
        {
            var (points,source) = TorusGap(12,138);
            var p = points.Concat(points.Select(v=>v+Vector3.right*8)).Concat(points.Select(v=>v+Vector3.right*16)).ToArray();
            var ix = source.Concat(source.Select(v=>v+points.Length)).Concat(source.Select(v=>v+points.Length*2)).ToArray();
            var cap = RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:RemeshClosureMode.Bridge,
                continueOnRefusal:true,bridgeCapFallback:true);
            Assert.AreEqual(2,cap.bridgeCapFallbacks.Count); Assert.AreEqual(1,cap.patchEnds.Count);
            Assert.AreEqual(136,cap.addedFaces); Assert.AreEqual(1,cap.loopFailures.Count);
            StringAssert.Contains("pair budget",cap.loopFailures.Values.Single());
            CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [Test]
        public void CapFallbackClosesTheTubeThroughNativeRemeshSimplifyAndUv()
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException ex) when (ex.InnerException is DllNotFoundException ||
                ex.InnerException is EntryPointNotFoundException || ex.InnerException is BadImageFormatException)
                { Assert.Ignore("Native remesh unavailable: "+ex.Message); }
            var (p,ix) = TorusGap(12,138);
            var cap = RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:RemeshClosureMode.Bridge,
                elementScopedContacts:true,bridgeCapFallback:true);
            var settings = new RemeshSettings {voxelResolution=64,solve=false,maximumError=.02f,
                textureResolution=512,padding=3,mergeCharts=true};
            var voxel = RemeshNative.Voxelize(cap.positions,cap.indices,settings,default);
            var simplified = RemeshNative.Simplify(voxel,settings,default,out _);
            var uv = RemeshNative.Unwrap(simplified,settings,default);
            foreach (var topology in new[] {RemeshTopology.Inspect(voxel.positions,voxel.indices),
                RemeshTopology.Inspect(simplified.positions,simplified.indices),RemeshTopology.Inspect(uv.positions,uv.indices)}) {
                Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
                CollectionAssert.AreEqual(new[] {2},topology.euler,"Separate Caps must leave a closed tube, without restoring the handle.");
            }
            var atlas = UvAtlasDiagnostics.Measure(uv,default);
            Assert.IsTrue(atlas.complete); Assert.AreEqual(0,atlas.invalidFaces); Assert.AreEqual(0,atlas.degenerateFaces);
            Assert.AreEqual(0,atlas.pairs); Assert.AreEqual(0,atlas.outOfBoundsVertices);
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
        public void AutomaticClosesIndependentBridgePairsAndAnOrdinaryCap()
        {
            var (p,ix)=TorusGap();
            var points=p.Concat(p.Select(v=>v+Vector3.right*8)).Concat(Box.Select(v=>v+Vector3.right*16)).ToArray();
            var indices=ix.Concat(ix.Select(v=>v+p.Length)).Concat(Missing(0).Select(v=>v+p.Length*2)).ToArray();
            var cap=RemeshPlanarCap.Prepare(points,indices,"all",default,mode:RemeshClosureMode.Automatic,
                continueOnRefusal:true,elementScopedContacts:true);
            Assert.AreEqual(5,cap.loops); Assert.AreEqual(26,cap.addedFaces); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            Assert.AreEqual(4,cap.bridgePartners.Count); Assert.AreEqual(3,cap.patchEnds.Count); Assert.IsEmpty(cap.loopFailures);
            var after=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); CollectionAssert.AreEqual(new[] {0,0,2},after.euler);
            CollectionAssert.AreEqual(points,cap.positions); CollectionAssert.AreEqual(indices,cap.indices.Take(indices.Length));
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

        [TestCase(6)] [TestCase(64)] [TestCase(96)]
        public void BridgeWithUnequalRimsPreservesEveryBorderVertexAndEdge(int edges)
        {
            var (points,source)=TorusGap(8,edges); var p=points.ToList(); var ix=source.ToList();
            var topology=RemeshTopology.Inspect(points,source); var edge=topology.edges.First(pair=>pair.Value.count==1);
            int f=edge.Value.firstFace;
            int k=Enumerable.Range(0,3).First(c=> {
                int a=topology.slots[ix[f*3+c]],b=topology.slots[ix[f*3+(c+1)%3]];
                return (a<b?(a,b):(b,a))==edge.Key;
            });
            int x=ix[f*3+k],y=ix[f*3+(k+1)%3],z=ix[f*3+(k+2)%3],m=p.Count;
            p.Add((p[x]*3+p[y])/4); ix.RemoveRange(f*3,3); ix.AddRange(new[] {x,m,z,m,y,z});
            var cap=RemeshPlanarCap.Prepare(p.ToArray(),ix.ToArray(),"0,1",default,false,RemeshClosureMode.Bridge);
            Assert.AreEqual(edges*2+1,cap.addedFaces); Assert.AreEqual(p.Count,cap.positions.Length);
            var closed=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(closed.Valid,closed.Description); Assert.AreEqual(0,closed.boundary.Count);
            CollectionAssert.AreEqual(new[] {0},closed.euler);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Count));
            Assert.AreEqual(1,cap.bridgePartners[0]); Assert.AreEqual(0,cap.bridgePartners[1]);
            Assert.IsTrue(cap.facePatches.Skip(ix.Count/3).All(id=>id==1));
        }

        [TestCase(RemeshClosureMode.Bridge,false,1f)]
        [TestCase(RemeshClosureMode.Automatic,false,1f)]
        [TestCase(RemeshClosureMode.Bridge,true,.125f)]
        [TestCase(RemeshClosureMode.Automatic,true,8f)]
        public void BridgeWithEightAndSixteenEdgesPreservesBothRims(RemeshClosureMode mode,bool reverse,float scale)
        {
            var (vertices,indices)=SubdividedTorusGap(8,2);
            if (reverse) {
                vertices=vertices.Select(v=>Quaternion.Euler(23,39,17)*v*scale+new Vector3(3,-2,1)).ToArray();
                for (int f=0;f<indices.Length;f+=3) (indices[f],indices[f+2])=(indices[f+2],indices[f]);
            }
            var originalPoints=(Vector3[])vertices.Clone(); var originalFaces=(int[])indices.Clone();
            var before=RemeshTopology.Inspect(vertices,indices);
            Assert.IsTrue(before.Valid,before.Description);
            var cap=RemeshPlanarCap.Prepare(vertices,indices,"0,1",default,mode:mode,
                continueOnRefusal:true,elementScopedContacts:true,bridgeCapFallback:true);
            CollectionAssert.AreEquivalent(new[] {8,16},cap.boundaryLoops.Select(loop=>loop.Length));
            Assert.IsEmpty(cap.loopFailures); Assert.IsEmpty(cap.bridgeCapFallbacks);
            Assert.AreEqual(24,cap.addedFaces); Assert.AreEqual(1,cap.patchEnds.Count);
            Assert.AreEqual(1,cap.bridgePartners[0]); Assert.AreEqual(0,cap.bridgePartners[1]);
            var closed=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(closed.Valid,closed.Description); Assert.AreEqual(0,closed.boundary.Count);
            CollectionAssert.AreEqual(new[] {0},closed.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions,cap.indices,default).All(v=>v));
            var annulus=RemeshTopology.Inspect(cap.positions,cap.indices.Skip(indices.Length).ToArray());
            Assert.IsTrue(annulus.Valid,annulus.Description); CollectionAssert.AreEqual(new[] {0},annulus.euler);
            Assert.IsTrue(annulus.boundary.SetEquals(before.boundary));
            Assert.IsTrue(cap.facePatches.Take(indices.Length/3).All(id=>id==0));
            Assert.IsTrue(cap.facePatches.Skip(indices.Length/3).All(id=>id==1));
            CollectionAssert.AreEqual(originalPoints,vertices); CollectionAssert.AreEqual(originalFaces,indices);
            CollectionAssert.AreEqual(vertices,cap.positions); CollectionAssert.AreEqual(indices,cap.indices.Take(indices.Length));
        }

        [TestCase(64)] [TestCase(128)] [TestCase(129)]
        public void BridgeCompletesItsAdvertisedRimBudget(int edges)
        {
            var (p,ix) = TorusGap(8,edges);
            var cap = RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:RemeshClosureMode.Bridge);
            Assert.AreEqual(edges*2,cap.addedFaces); Assert.AreEqual(0,cap.remainingBoundaryEdges);
            var closed = RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(closed.Valid,closed.Description); CollectionAssert.AreEqual(new[] {0},closed.euler);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [TestCase(8,4,false,false,RemeshClosureMode.Bridge)]
        [TestCase(8,32,false,false,RemeshClosureMode.Bridge)]
        [TestCase(8,32,true,true,RemeshClosureMode.Bridge)]
        [TestCase(8,4,true,true,RemeshClosureMode.Automatic)]
        [TestCase(64,4,true,true,RemeshClosureMode.Bridge)]
        public void BridgeSearchBudgetSupportsLargeUnequalAndOffsetRims(int ring,int divisions,bool uneven,bool offset,RemeshClosureMode mode)
        {
            var (p,ix)=SubdividedTorusGap(ring,divisions,uneven,offset);
            var before=RemeshTopology.Inspect(p,ix); Assert.IsTrue(before.Valid,before.Description);
            var cap=RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:mode,elementScopedContacts:true,bridgeCapFallback:true);
            CollectionAssert.AreEquivalent(new[] {ring,ring*divisions},cap.boundaryLoops.Select(loop=>loop.Length));
            Assert.AreEqual(ring*(divisions+1),cap.addedFaces); Assert.IsEmpty(cap.loopFailures); Assert.IsEmpty(cap.bridgeCapFallbacks);
            Assert.AreEqual(2,cap.bridgePartners.Count); Assert.AreEqual(1,cap.patchEnds.Count);
            var closed=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(closed.Valid,closed.Description); Assert.AreEqual(0,closed.boundary.Count);
            CollectionAssert.AreEqual(new[] {0},closed.euler);
            var annulus=RemeshTopology.Inspect(cap.positions,cap.indices.Skip(ix.Length).ToArray());
            Assert.IsTrue(annulus.Valid,annulus.Description); CollectionAssert.AreEqual(new[] {0},annulus.euler);
            Assert.IsTrue(annulus.boundary.SetEquals(before.boundary));
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
        }

        [TestCase(256,256,"search budget")]
        [TestCase(20000,3,"memory budget")]
        [TestCase(int.MaxValue,int.MaxValue,"search budget")]
        public void BridgeBudgetPreflightRefusesBeforeAnySearchStateOrCandidate(int first,int second,string reason)
        {
            var report=new RemeshBridge.SearchReport();
            StringAssert.Contains(reason,Assert.Throws<InvalidOperationException>(()=>RemeshBridge.CheckBudget(first,second,report)).Message);
            Assert.AreEqual(0,report.states); Assert.AreEqual(0,report.phases);
            Assert.AreEqual(0,report.candidates); Assert.AreEqual(0,report.audited);
            Assert.Greater(report.plannedStates,0); Assert.Greater(report.plannedBytes,0);
        }

        [Test]
        public void BridgeMemoryRefusalPreservesDonorsAndContactDiagnostics()
        {
            var (p,ix)=TorusGap(); var points=(Vector3[])p.Clone(); var source=(int[])ix.Clone();
            var loops=RemeshPlanarCap.Boundaries(RemeshTopology.Inspect(p,ix),default);
            var report=new RemeshBridge.SearchReport(); int trials=0;
            StringAssert.Contains("memory budget",Assert.Throws<InvalidOperationException>(()=>
                RemeshBridge.Generate(p,ix,loops[0],loops[1],default,ref trials,out _,report:report,maxSearchBytes:1)).Message);
            Assert.AreEqual(0,trials); Assert.AreEqual(0,report.states); Assert.AreEqual(0,report.audited);
            CollectionAssert.AreEqual(points,p); CollectionAssert.AreEqual(source,ix);
        }

        [TestCase(false)] [TestCase(true)]
        public void LargeUnequalBridgePreservesHandleAndMaskThroughNativeStagesAndBake(bool solve)
        {
            RemeshNative.CheckAvailable();
            var (p,ix)=SubdividedTorusGap(8,32,true,true);
            var points=(Vector3[])p.Clone(); var indices=(int[])ix.Clone();
            var cap=RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:RemeshClosureMode.Bridge,elementScopedContacts:true);
            var settings=new RemeshSettings {voxelResolution=64,solve=solve,maximumError=.02f,
                textureResolution=256,padding=3,mergeCharts=true,bakeSamples=1,projectionDistance=.03f,
                gpuProjection=false,dilationRadius=0};
            var voxel=RemeshNative.Voxelize(cap.positions,cap.indices,settings,default);
            var simplified=solve ? RemeshSurfaceRefine.Simplify(voxel,cap.positions,cap.indices,settings,default,out _) :
                RemeshNative.Simplify(voxel,settings,default,out _);
            var uv=RemeshNative.Unwrap(simplified,settings,default);
            foreach (var topology in new[] {RemeshTopology.Inspect(voxel.positions,voxel.indices),
                RemeshTopology.Inspect(simplified.positions,simplified.indices),RemeshTopology.Inspect(uv.positions,uv.indices)}) {
                Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
                CollectionAssert.AreEqual(new[] {0},topology.euler);
            }
            var atlas=UvAtlasDiagnostics.Measure(uv,default);
            Assert.IsTrue(atlas.complete); Assert.AreEqual(0,atlas.invalidFaces); Assert.AreEqual(0,atlas.degenerateFaces);
            Assert.AreEqual(0,atlas.pairs); Assert.AreEqual(0,atlas.outOfBoundsVertices);
            var labels=RemeshSyntheticFaces.Project(uv.positions,uv.indices,cap.positions,cap.indices,cap.facePatches,default);
            Assert.AreEqual(uv.indices.Length/3,labels.Length); Assert.IsTrue(labels.Any(id=>id>0)); Assert.IsTrue(labels.Any(id=>id==0));
            var donor=new RemeshNative.IndexedMesh {positions=p,indices=ix}.PrepareChannels();
            var source=new RemeshSource {positions=p,indices=ix,normals=donor.normals,uv=donor.uv,
                tangents=p.Select(v=>new Vector4(1,0,0,1)).ToArray(),faceMaterials=new int[ix.Length/3],
                colors=p.Select(v=>Color.white).ToArray(),diagonal=6,
                materials=new[] {new RemeshSource.Surface {color=new RemeshSource.Map(),normal=new RemeshSource.Map(),
                    metal=new RemeshSource.Map(),ao=new RemeshSource.Map(),emission=new RemeshSource.Map(),tint=Color.white}}};
            var maps=RemeshBaker.Bake(source,uv,uv.tangents,settings,default,support:cap);
            Assert.Greater(maps.covered,0); Assert.Greater(maps.capCovered,0); Assert.Less(maps.capCovered,maps.covered);
            Assert.LessOrEqual(maps.capMisses,maps.misses); Assert.AreEqual(256*256,maps.color.Length);
            TestContext.WriteLine($"8x256 Solve={solve}: bake covered={maps.covered}, misses={maps.misses}, Bridge covered={maps.capCovered}, Bridge misses={maps.capMisses}");
            CollectionAssert.AreEqual(points,p); CollectionAssert.AreEqual(indices,ix);
        }

        [TestCase(false,.125f)] [TestCase(true,8f)]
        public void BridgePreservesAnUnselectedHoleUnderTransformAndReversedWinding(bool reverse,float scale)
        {
            var (p,ix)=TorusGap(12,16);
            p=p.Select(v=>Quaternion.Euler(23,39,17)*v*scale+new Vector3(3,-2,1)).ToArray();
            ix=ix.Where((v,i)=>i/3!=166).ToArray();
            if (reverse) for (int i=0;i<ix.Length;i+=3) (ix[i],ix[i+2])=(ix[i+2],ix[i]);
            var before=RemeshTopology.Inspect(p,ix);
            // The extra opening is far from the missing band and has three edges.
            var loops=RemeshPlanarCap.Boundaries(before,default);
            string selection=string.Join(",",loops.Select((loop,id)=>(loop,id)).Where(x=>x.loop.Count==16).Select(x=>x.id));
            var untouched=loops.Single(loop=>loop.Count==3);
            var expected=new HashSet<(Vector3,Vector3)>();
            for (int j=0;j<3;++j) expected.Add(RemeshTopology.Snapshot.BoundaryKey(p[untouched[j]],p[untouched[(j+1)%3]]));
            var cap=RemeshPlanarCap.Prepare(p,ix,selection,default,mode:RemeshClosureMode.Bridge);
            var repeat=RemeshPlanarCap.Prepare(p,ix,selection,default,mode:RemeshClosureMode.Bridge);
            Assert.AreEqual(32,cap.addedFaces); Assert.AreEqual(3,cap.remainingBoundaryEdges);
            var after=RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(after.Valid,after.Description); Assert.IsTrue(after.boundary.SetEquals(expected));
            CollectionAssert.AreEqual(before.euler,after.euler);
            CollectionAssert.AreEqual(p,cap.positions); CollectionAssert.AreEqual(ix,cap.indices.Take(ix.Length));
            CollectionAssert.AreEqual(cap.indices,repeat.indices);
        }

        [Test]
        public void BridgeSearchAndContactBudgetsKeepDonorsAndDiagnosticsUnchanged()
        {
            var (p,ix)=TorusGap(); var before=(int[])ix.Clone(); var points=(Vector3[])p.Clone();
            var loops=RemeshPlanarCap.Boundaries(RemeshTopology.Inspect(p,ix),default);
            var contacts=new RemeshPlanarCap.ExternalContacts {faceOwners=new int[ix.Length/3],closingOwners=new HashSet<int> {0}};
            int trials=0;
            StringAssert.Contains("search budget",Assert.Throws<InvalidOperationException>(()=>
                RemeshBridge.Generate(p,ix,loops[0],loops[1],default,ref trials,out _,contacts,maxStates:1)).Message);
            Assert.AreEqual(0,trials); Assert.AreEqual(0,contacts.count);
            trials=2000000;
            StringAssert.Contains("pair budget",Assert.Throws<InvalidOperationException>(()=>
                RemeshBridge.Generate(p,ix,loops[0],loops[1],default,ref trials,out _,contacts)).Message);
            Assert.AreEqual(0,contacts.count);
            CollectionAssert.AreEqual(before,ix); CollectionAssert.AreEqual(points,p);
        }

        [TestCase(32,false)] [TestCase(32,true)] [TestCase(64,false)] [TestCase(64,true)]
        [TestCase(128,false)] [TestCase(128,true)]
        public void BridgeRetainsTheHandleThroughNativeRemeshSimplifyAndUv(int resolution,bool solve)
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException ex) when (ex.InnerException is DllNotFoundException ||
                ex.InnerException is EntryPointNotFoundException || ex.InnerException is BadImageFormatException)
                { Assert.Ignore("Native remesh unavailable: "+ex.Message); }
            var (p,ix)=TorusGap(16,16);
            var cap=RemeshPlanarCap.Prepare(p,ix,"0,1",default,mode:RemeshClosureMode.Bridge);
            var settings=new RemeshSettings {voxelResolution=resolution,solve=solve,maximumError=.02f,
                textureResolution=512,padding=3,mergeCharts=true};
            var voxel=RemeshNative.Voxelize(cap.positions,cap.indices,settings,default);
            var simplified=solve ? RemeshSurfaceRefine.Simplify(voxel,cap.positions,cap.indices,settings,default,out _) :
                RemeshNative.Simplify(voxel,settings,default,out _);
            var uv=RemeshNative.Unwrap(simplified,settings,default);
            foreach (var topology in new[] {RemeshTopology.Inspect(voxel.positions,voxel.indices),
                RemeshTopology.Inspect(simplified.positions,simplified.indices),RemeshTopology.Inspect(uv.positions,uv.indices)}) {
                Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
                CollectionAssert.AreEqual(new[] {0},topology.euler,"The torus handle must survive every downstream stage.");
            }
            var quality=UvChartQuality.Measure(uv,default); var atlas=UvAtlasDiagnostics.Measure(uv,default);
            Assert.IsTrue(quality.valid); Assert.IsTrue(atlas.complete);
            Assert.AreEqual(0,atlas.invalidFaces); Assert.AreEqual(0,atlas.degenerateFaces);
            Assert.AreEqual(0,atlas.pairs); Assert.AreEqual(0,atlas.outOfBoundsVertices);
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
            var diagnostic=new RemeshBridge.SearchReport(captureRejected:true); int diagnosticTrials=0;
            var rims=RemeshPlanarCap.Boundaries(RemeshTopology.Inspect(p.ToArray(),saved),default);
            Assert.Throws<InvalidOperationException>(()=>RemeshBridge.Generate(p.ToArray(),saved,rims[0],rims[1],default,
                ref diagnosticTrials,out _,report:diagnostic));
            StringAssert.Contains("contacts face",diagnostic.firstContact);
            Assert.IsNotNull(diagnostic.firstRejectedIndices);
            CollectionAssert.AreEqual(saved,diagnostic.firstRejectedIndices.Take(saved.Length));
            CollectionAssert.AreEqual(saved,refused.indices,"Rejected diagnostic geometry must never be published as support.");
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
