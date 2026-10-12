using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvFrozenCascadeTests
    {
        readonly List<Mesh> owned=new List<Mesh>();
        [TearDown] public void Cleanup() {foreach(var mesh in owned) if(mesh) UnityEngine.Object.DestroyImmediate(mesh);owned.Clear();}

        Mesh Quad(float x=0,float y=0,float size=1)
        {
            var positions=new[] {new Vector3(x,y),new Vector3(x+size,y),new Vector3(x+size,y+size),new Vector3(x,y+size)};
            var mesh=new Mesh {name="Frozen",vertices=positions,triangles=new[] {0,1,2,0,2,3},
                uv2=new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up}.Select(p=>p*.25f).ToArray()};
            owned.Add(mesh);return mesh;
        }
        static ReverseUvTransfer.Level Level(int lod,params Mesh[] meshes)=>new ReverseUvTransfer.Level {lod=lod,
            inputs=meshes.Select((m,i)=>new ReverseUvTransfer.Input {mesh=m,key="Frozen"+i,usePreparedUv2=true}).ToArray()};
        static ReverseUvTransfer.Result Build(params ReverseUvTransfer.Level[] levels)=>ReverseUvTransfer.Build(levels,
            new ReverseUvTransfer.Options {seedResolution=128,seedDensity=32,projectionReach=.1f,
                regroupPreparedAtlas=true,frozenShellCascade=true,repackFinalAtlas=true}).GetAwaiter().GetResult();

        [Test]
        public void OneCoarseIslandContainsSeveralWholeFineIslands()
        {
            var coarse=Quad();var a=Quad(.1f,.1f,.3f);var b=Quad(.6f,.6f,.3f);
            var snapshots=new[] {coarse,a,b}.Select(TransferMeshSnapshot.Capture).ToArray();
            using var result=Build(Level(1,coarse),Level(0,a,b));
            Assert.AreEqual(1,result.report.finalPackingFamilies);Assert.AreEqual(4,result.report.inheritedFaces);
            Assert.AreEqual(32,result.report.texelsPerUnit,.001);Assert.IsTrue(result.report.finalPackingNative);
            foreach(var node in result.report.nodes) Assert.AreEqual(0,node.splitSourceFaces);
            var meshes=new[] {coarse,a,b};for(int i=0;i<meshes.Length;++i) CollectionAssert.AreEqual(snapshots[i],TransferMeshSnapshot.Capture(meshes[i]));
            var seed=result.meshes[0][0];var seedUv=seed.uv2;var seedPositions=seed.vertices;
            foreach(var child in result.meshes[1])
                for(int v=0;v<child.vertexCount;++v)
                {
                    var p=child.vertices[v];var expected=seedUv[0]+(seedUv[1]-seedUv[0])*(p.x-seedPositions[0].x)
                        +(seedUv[3]-seedUv[0])*(p.y-seedPositions[0].y);
                    Assert.LessOrEqual(Vector2.Distance(expected,child.uv2[v])*result.report.atlasSize,.01);
                }
        }

        [Test]
        public void ApproximateLodPanelUsesProjectionReachInsteadOfSeamPrecision()
        {
            var coarse=Quad();var fine=Quad();
            // The panel remains one healthy island, but simplification changes
            // its corner layout enough to miss the 0.25px seam tolerance.
            fine.uv2=new[] {Vector2.zero,new Vector2(.25f,0),new Vector2(.25f,.2575f),new Vector2(0,.25f)};
            using var result=Build(Level(1,coarse),Level(0,fine));
            Assert.AreEqual(1,result.report.finalPackingFamilies);
            Assert.AreEqual(2,result.report.nodes.Single(n=>n.lod==0).inheritedFaces);
            Assert.AreEqual(0,result.report.splitSourceFaces);
            Assert.AreEqual(1,UvShellExtractor.Extract(result.meshes[1][0].uv2,result.meshes[1][0].triangles).Count);
            var a=fine.uv2;var b=result.meshes[1][0].uv2;
            Assert.AreEqual(Vector2.Distance(a[0],a[1])/Vector2.Distance(a[0],a[3]),
                Vector2.Distance(b[0],b[1])/Vector2.Distance(b[0],b[3]),1e-5);
        }

        [Test]
        public void IncompatibleWholeIslandIsNotForcedIntoAParent()
        {
            var coarse=Quad();var fine=Quad();
            fine.uv2=new[] {Vector2.zero,new Vector2(.25f,0),new Vector2(.25f,.5f),new Vector2(0,.25f)};
            using var result=Build(Level(1,coarse),Level(0,fine));
            Assert.AreEqual(2,result.report.finalPackingFamilies);
            Assert.IsTrue(result.report.nodes.Single(n=>n.lod==0).faces.All(f=>!f.inherited));
            Assert.AreEqual(0,result.report.splitSourceFaces);
        }

        [Test]
        public void RelaxedWholeIslandFitStillRejectsSiblingOverlap()
        {
            var coarse=Quad();var a=Quad(.1f,.1f,.6f);var b=Quad(.2f,.2f,.6f);
            using var result=Build(Level(1,coarse),Level(0,a,b));
            Assert.AreEqual(2,result.report.finalPackingFamilies);
            Assert.AreEqual(0,result.report.overlapFaces);
            Assert.AreEqual(0,result.report.splitSourceFaces);
            var fine=result.report.nodes.Where(n=>n.lod==0).ToArray();
            Assert.AreNotEqual(fine[0].faces[0].chart,fine[1].faces[0].chart);
            Assert.IsTrue(fine[1].faces.All(f=>f.wholeMatchRefusal.Contains("sibling contact")));
        }

        [Test]
        public void ThinMirroredIslandUsesTheBestOrientationWithinTheFitBudget()
        {
            var coarse=Quad();coarse.vertices=coarse.vertices.Select(p=>new Vector3(p.x,p.y*.01f)).ToArray();
            coarse.uv2=coarse.uv2.Select(p=>new Vector2(p.x,p.y*.01f)).ToArray();
            var fine=UnityEngine.Object.Instantiate(coarse);owned.Add(fine);
            fine.uv2=fine.uv2.Select(p=>new Vector2(p.x,-p.y)).ToArray();
            using var result=Build(Level(1,coarse),Level(0,fine));
            Assert.AreEqual(1,result.report.finalPackingFamilies);
            var a=result.meshes[0][0].uv2;var b=result.meshes[1][0].uv2;
            for(int i=0;i<a.Length;++i) Assert.LessOrEqual(Vector2.Distance(a[i],b[i])*result.report.atlasSize,.01);
        }

        [Test]
        public void PartialMatchLocksTheCompleteIslandToTheDonorGroup()
        {
            var coarse=Quad();coarse.triangles=new[] {0,1,2};var fine=Quad();
            using var result=Build(Level(1,coarse),Level(0,fine));
            var node=result.report.nodes.Single(n=>n.lod==0);
            Assert.AreEqual(1,node.inheritedFaces);Assert.AreEqual(1,result.report.finalPackingFamilies);
            Assert.AreEqual(node.faces[0].chart,node.faces[1].chart);
            Assert.AreEqual(2,result.meshes[1][0].triangles.Length/3);
            Assert.AreEqual(1,UvShellExtractor.Extract(result.meshes[1][0].uv2,result.meshes[1][0].triangles).Count);
        }

        [Test]
        public void NewIntermediateIslandIsADonorForTheFollowingLod()
        {
            var seed=Quad();var middle=Quad();var newIsland=Quad(3);var fine=Quad(3);
            using var result=Build(Level(2,seed),Level(1,middle,newIsland),Level(0,fine));
            var node=result.report.nodes.Single(n=>n.lod==0);
            Assert.AreEqual(2,node.inheritedFaces);Assert.IsTrue(node.faces.All(f=>f.parentLod==1 && f.parentMesh==1));
            Assert.AreEqual(2,result.report.finalPackingFamilies);
            var a=result.meshes[1][1].uv2;var b=result.meshes[2][0].uv2;
            for(int i=0;i<a.Length;++i) Assert.LessOrEqual(Vector2.Distance(a[i],b[i])*result.report.atlasSize,.01);
        }

        [Test]
        public void RepairedWholeIslandRetriesItsDonorBeforeBecomingNew()
        {
            var coarse=Quad();var fine=Quad();fine.uv2=fine.uv2.Select(p=>new Vector2(p.x,p.y*.0001f)).ToArray();
            using var result=Build(Level(1,coarse),Level(0,fine));
            var node=result.report.nodes.Single(n=>n.lod==0);
            Assert.AreEqual(2,node.inheritedFaces);Assert.AreEqual(2,node.localFallbackFaces);
            Assert.AreEqual(1,result.report.finalPackingFamilies);Assert.AreEqual(0,node.splitSourceFaces);
            Assert.AreEqual(1,UvShellExtractor.Extract(result.meshes[1][0].uv2,result.meshes[1][0].triangles).Count);
        }

        [Test]
        public void MicroscopicSharedFaceRetainsItsIslandDuringHighDensityRepack()
        {
            var positions=new[] {Vector3.zero,Vector3.right,new Vector3(1,1),Vector3.up,new Vector3(.5f,-.000000001f)};
            var coarse=new Mesh {name="TinyFace",vertices=positions,triangles=new[] {0,1,2,0,2,3,1,0,4},
                uv2=positions.Select(p=>new Vector2(p.x,p.y)*.25f).ToArray()};owned.Add(coarse);
            var fine=UnityEngine.Object.Instantiate(coarse);owned.Add(fine);
            var coarseBefore=TransferMeshSnapshot.Capture(coarse);var fineBefore=TransferMeshSnapshot.Capture(fine);
            using var result=ReverseUvTransfer.Build(new[] {Level(1,coarse),Level(0,fine)},
                new ReverseUvTransfer.Options {seedResolution=128,seedDensity=32,padding=2,projectionReach=.1f,
                    regroupPreparedAtlas=true,frozenShellCascade=true,rotateCharts=true,rotateChartsToAxis=true,
                    repackFinalAtlas=true,fillFinalAtlas=true}).GetAwaiter().GetResult();
            Assert.Greater(result.report.texelsPerUnit,32);
            foreach(var level in result.meshes) foreach(var mesh in level)
            {
                Assert.AreEqual(9,mesh.triangles.Length);Assert.AreEqual(1,UvShellExtractor.Extract(mesh.uv2,mesh.triangles).Count);
                var quality=TransferUvQuality.Measure(mesh,mesh.uv2,Vector2.one,Matrix4x4.identity);
                Assert.AreEqual(0,quality.degenerateFaces+quality.overlapPairs);
            }
            Assert.AreEqual(0,result.report.splitSourceFaces);
            CollectionAssert.AreEqual(coarseBefore,TransferMeshSnapshot.Capture(coarse));
            CollectionAssert.AreEqual(fineBefore,TransferMeshSnapshot.Capture(fine));
        }

        [Test]
        public void FillAtlasChoosesANewDensityAndReappliesPixelPadding()
        {
            var coarse=Quad();var inherited=Quad();var fresh=Quad(3);
            using var result=ReverseUvTransfer.Build(new[] {Level(1,coarse),Level(0,inherited,fresh)},
                new ReverseUvTransfer.Options {seedResolution=128,seedDensity=8,padding=4,projectionReach=.1f,
                    regroupPreparedAtlas=true,frozenShellCascade=true,repackFinalAtlas=true,fillFinalAtlas=true}).GetAwaiter().GetResult();
            Assert.Greater(result.report.texelsPerUnit,8);Assert.AreEqual(2,result.report.finalPackingFamilies);
            var a=result.meshes[1][0].uv2.Select(p=>p*result.report.atlasSize).ToArray();
            var b=result.meshes[1][1].uv2.Select(p=>p*result.report.atlasSize).ToArray();
            var lowA=a.Aggregate(Vector2.Min);var highA=a.Aggregate(Vector2.Max);
            var lowB=b.Aggregate(Vector2.Min);var highB=b.Aggregate(Vector2.Max);
            float gap=Math.Max(Math.Max(lowB.x-highA.x,lowA.x-highB.x),Math.Max(lowB.y-highA.y,lowA.y-highB.y));
            Assert.GreaterOrEqual(gap,3.99f);
            Assert.Less(gap,12,"New packing must not scale the old 4px gutter with texel density.");
            Assert.AreEqual(result.report.texelsPerUnit,highA.x-lowA.x,.001);
            Assert.AreEqual(0,result.report.splitSourceFaces);
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(8)]
        public void CompleteGroupsAreRepackedWithTheFinalPixelPadding(int padding)
        {
            var coarse=Quad();var inherited=Quad();var fresh=Quad(3);
            using var result=ReverseUvTransfer.Build(new[] {Level(1,coarse),Level(0,inherited,fresh)},
                new ReverseUvTransfer.Options {seedResolution=128,seedDensity=48,padding=padding,projectionReach=.1f,
                    regroupPreparedAtlas=true,frozenShellCascade=true,repackFinalAtlas=true}).GetAwaiter().GetResult();
            Assert.AreEqual(48,result.report.texelsPerUnit,.001);Assert.AreEqual(2,result.report.finalPackingFamilies);
            var first=result.meshes[1][0].uv2.Select(p=>p*result.report.atlasSize).ToArray();
            var second=result.meshes[1][1].uv2.Select(p=>p*result.report.atlasSize).ToArray();
            var a=first.Aggregate(Vector2.Min);var b=first.Aggregate(Vector2.Max);
            var c=second.Aggregate(Vector2.Min);var d=second.Aggregate(Vector2.Max);
            float gap=Math.Max(Math.Max(c.x-b.x,a.x-d.x),Math.Max(c.y-b.y,a.y-d.y));
            Assert.GreaterOrEqual(gap,padding-.01f,"Gutter is measured at the final density, after all group matches.");
            Assert.AreEqual(48,b.x-a.x,.001);Assert.AreEqual(48,d.x-c.x,.001);
            Assert.AreEqual(0,result.report.splitSourceFaces);
        }

        [TestCase("#####/#...#/#####")]
        [TestCase("#...#/#...#/#####")]
        [TestCase("#####/#..../#####")]
        [TestCase("#...#/#####/#...#")]
        [TestCase("#####/..#../..#..")]
        public void FoundationCutsFrameAndBranchPatternsWithoutGeneratingFinerTriangles(string pattern)
        {
            var vertices=new List<Vector3>();var indices=new List<int>();var unique=new Dictionary<Vector3,int>();var rows=pattern.Split('/');
            for(int y=0;y<rows.Length;++y)
                for(int x=0;x<rows[y].Length;++x) if(rows[y][x]=='#')
                {
                    var ids=new int[4];var cell=new[] {new Vector3(x,y),new Vector3(x+1,y),new Vector3(x+1,y+1),new Vector3(x,y+1)};
                    for(int k=0;k<4;++k) {if(!unique.TryGetValue(cell[k],out int v)) {v=vertices.Count;unique.Add(cell[k],v);vertices.Add(cell[k]);}ids[k]=v;}
                    indices.AddRange(new[] {ids[0],ids[1],ids[2],ids[0],ids[2],ids[3]});
                }
            var coarse=new Mesh {name="Pattern",vertices=vertices.ToArray(),triangles=indices.ToArray(),
                uv2=vertices.Select(p=>new Vector2(p.x,p.y)*.25f).ToArray()};owned.Add(coarse);
            var fine=Quad(20);using var result=Build(Level(1,coarse),Level(0,fine));
            Assert.Greater(result.report.nodes.Single(n=>n.lod==1).faces.Select(f=>f.chart).Distinct().Count(),1);
            Assert.AreEqual(indices.Count,result.meshes[0][0].triangles.Length);
            Assert.AreEqual(6,result.meshes[1][0].triangles.Length);
            Assert.AreEqual(0,result.report.splitSourceFaces);
        }
    }
}
