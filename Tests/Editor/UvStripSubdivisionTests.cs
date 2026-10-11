using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class UvStripSubdivisionTests
    {
        [Serializable] sealed class Visual
        {
            public string name;
            public Vector2[] original,cut,packed;
            public int[] triangles,regions;
        }
        static (Vector2[] uv,int[] triangles) Grid(string pattern)
        {
            var rows=pattern.Split('/');var points=new List<Vector2>();var triangles=new List<int>();
            var unique=new Dictionary<Vector2,int>();
            for(int y=0;y<rows.Length;++y) for(int x=0;x<rows[y].Length;++x)
            {
                if(rows[y][x]!='#') continue;
                var ids=new int[4];var square=new[] {new Vector2(x,y),new Vector2(x+1,y),new Vector2(x+1,y+1),new Vector2(x,y+1)};
                for(int k=0;k<4;++k)
                {
                    if(!unique.TryGetValue(square[k],out int v)) {v=points.Count;points.Add(square[k]);unique.Add(square[k],v);}
                    ids[k]=v;
                }
                foreach(int k in new[] {0,1,2,0,2,3}) triangles.Add(ids[k]);
            }
            return (points.ToArray(),triangles.ToArray());
        }

        static double Area(Vector2 a,Vector2 b,Vector2 c)
            =>Math.Abs(((double)b.x-a.x)*(c.y-a.y)-((double)b.y-a.y)*(c.x-a.x))*.5;

        [Test]
        public void CurvedSurfaceDoesNotAcquireFlatFrameCuts()
        {
            var data=Grid("#####/#..../#..../####./#..../#..../#####");
            var flat=data.uv.Select(p=>new Vector3(p.x,p.y,0)).ToArray();
            var curved=data.uv.Select(p=>new Vector3(p.x,p.y,p.x*p.x*.5f)).ToArray();
            Validate(data.uv,data.triangles,UvStripSubdivision.Build(data.uv,data.triangles,positions:flat));
            var result=UvStripSubdivision.Build(data.uv,data.triangles,positions:curved);
            Assert.AreEqual(0,result.cuts,"A curved donor needs surface-aware charts, not flat-frame subdivision.");
            Assert.AreEqual(data.triangles.Length/3,result.sourceFaces.Length);
            CollectionAssert.AreEqual(Enumerable.Range(0,result.sourceFaces.Length),result.sourceFaces);
        }

        static void Validate(Vector2[] uv,int[] triangles,UvStripSubdivision.Result result,double minimumFill=.9)
        {
            Assert.Greater(result.charts,result.originalCharts);
            Assert.Greater(result.cuts,0);
            var area=new double[triangles.Length/3];
            for(int f=0;f<result.sourceFaces.Length;++f)
            {
                int t=result.sourceFaces[f]*3;
                for(int k=0;k<3;++k)
                {
                    var b=result.barycentrics[f*3+k];
                    Assert.That(b.x+b.y+b.z,Is.EqualTo(1).Within(1e-6));
                    Assert.GreaterOrEqual(Math.Min(b.x,Math.Min(b.y,b.z)),-1e-6);
                    var expected=uv[triangles[t]]*b.x+uv[triangles[t+1]]*b.y+uv[triangles[t+2]]*b.z;
                    Assert.Less((expected-result.uv[f*3+k]).magnitude,1e-5*Math.Max(1,expected.magnitude));
                }
                area[result.sourceFaces[f]]+=Area(result.uv[f*3],result.uv[f*3+1],result.uv[f*3+2]);
            }
            for(int f=0;f<area.Length;++f)
                Assert.That(area[f],Is.EqualTo(Area(uv[triangles[f*3]],uv[triangles[f*3+1]],uv[triangles[f*3+2]])).Within(Math.Max(1e-10,area[f]*1e-5)),"Original face "+f);
            var indices=Enumerable.Range(0,result.uv.Length).ToArray();
            foreach(var group in Enumerable.Range(0,result.regions.Length).GroupBy(f=>result.regions[f]))
            {
                var faces=group.ToArray();double sum=faces.Sum(f=>Area(result.uv[f*3],result.uv[f*3+1],result.uv[f*3+2]));
                var box=UvJunctionCuts.BestBox(result.uv,indices,faces,default);
                Assert.GreaterOrEqual(sum/box.Area,minimumFill,"Region "+group.Key+" retains a branch or large void");
            }
        }

        [TestCase("#####/#..../#..../####./#..../#..../#####",17f,false)] // E
        [TestCase("#####/..#../..#../..#../..#..",43f,false)] // T
        [TestCase("#...#/#...#/#...#/#...#/#####",0f,false)] // U
        [TestCase("#######/#.....#/#.....#/#.....#/#.....#/#.....#/#######",137f,false)] // O
        [TestCase("#####/#...#/#...#/#####/#..../#..../#....",29f,true)] // P
        [TestCase("#####/#..../#..../####./#..../#..../#....",73f,false)] // F
        [TestCase("#...#/#...#/#...#/#####/#...#/#...#/#...#",91f,false)] // H
        [TestCase("#..../#..../#..../#..../#####",0f,true)] // L
        [TestCase("...#.../...#.../...#.../#######/...#.../...#.../...#...",45f,false)] // X
        [TestCase("#####/#..../#..../#..../#####",211f,false)] // C
        public void EveryPieceIsCompactAndEveryOriginalTriangleIsCovered(string shape,float degrees,bool mirror)
        {
            var data=Grid(shape);float angle=degrees*Mathf.Deg2Rad;
            data.uv=Array.ConvertAll(data.uv,p=>new Vector2(Mathf.Cos(angle)*p.x-Mathf.Sin(angle)*p.y,
                (mirror?-1:1)*(Mathf.Sin(angle)*p.x+Mathf.Cos(angle)*p.y)));
            var result=UvStripSubdivision.Build(data.uv,data.triangles);
            Validate(data.uv,data.triangles,result);
            var world=result.uv.Select(p=>new Vector3(p.x,p.y,0)).ToArray();
            var options=new ReverseUvTransfer.Options {seedResolution=256,cutNarrowJunctions=true};
            var packed=ReverseUvSeedPacking.Prepare(world,result.uv,8,true,options,default,out _,regions:result.regions);
            for(int f=0;f<result.sourceFaces.Length;++f)
                Assert.Less(ReverseUvTransfer.TriangleAnisotropy(world[f*3],world[f*3+1],world[f*3+2],
                    packed[f*3],packed[f*3+1],packed[f*3+2]),1.001);
            string output=Environment.GetEnvironmentVariable("MESHLAB_STRIP_OUTPUT");
            if(!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output,$"shape_{degrees}_{mirror}.json"),JsonUtility.ToJson(new Visual {
                    name=shape,original=data.uv,triangles=data.triangles,cut=result.uv,packed=packed,regions=result.regions},true));
            }
        }

        // No interior grid vertices: the cut must create barycentric corners in
        // the large triangles instead of following an already available seam.
        [TestCase(.001f,17f,false)] [TestCase(1f,0f,false)] [TestCase(1000f,43f,true)]
        public void SparseTRequiresInteriorCutsAndPreservesMeshAttributes(float scale,float degrees,bool mirror)
        {
            var uv=new[] {new Vector2(0,0),new Vector2(5,0),new Vector2(5,1),new Vector2(3,1),
                new Vector2(3,5),new Vector2(2,5),new Vector2(2,1),new Vector2(0,1)};
            var triangles=new[] {0,1,3,1,2,3,0,3,6,0,6,7,3,4,5,3,5,6};
            float angle=degrees*Mathf.Deg2Rad;
            uv=Array.ConvertAll(uv,p=>scale*new Vector2(Mathf.Cos(angle)*p.x-Mathf.Sin(angle)*p.y,
                (mirror?-1:1)*(Mathf.Sin(angle)*p.x+Mathf.Cos(angle)*p.y)));
            var original=new Mesh {vertices=uv.Select(p=>new Vector3(p.x,p.y,0)).ToArray(),triangles=triangles,uv=uv,uv2=uv};
            original.colors=uv.Select(p=>new Color(p.x,p.y,.4f,1)).ToArray();
            original.tangents=uv.Select(p=>new Vector4(1,0,0,-1)).ToArray();
            original.RecalculateNormals();var before=TransferMeshSnapshot.Capture(original);Mesh copy=null;
            try
            {
                var result=UvStripSubdivision.Build(uv,triangles);Validate(uv,triangles,result,.88);
                Assert.Greater(result.sourceFaces.Length,triangles.Length/3);
                copy=ReverseUvRefinedMesh.Copy(original,result.sourceFaces,result.barycentrics,result.uv);
                var positions=copy.vertices;var source=original.vertices;var indices=copy.triangles;
                for(int f=0;f<result.sourceFaces.Length;++f) for(int k=0;k<3;++k)
                {
                    int t=result.sourceFaces[f]*3;var b=result.barycentrics[f*3+k];int i=indices[f*3+k];
                    Assert.That((positions[i]-source[triangles[t]]*b.x-source[triangles[t+1]]*b.y-source[triangles[t+2]]*b.z).magnitude,Is.LessThan(scale*1e-5));
                    Assert.AreEqual(-1,copy.tangents[i].w);
                    Assert.That(copy.colors[i].r,Is.EqualTo(copy.uv[i].x).Within(scale*1e-5));
                }
                CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(original));
            }
            finally {if(copy) Object.DestroyImmediate(copy);Object.DestroyImmediate(original);}
        }

        [Test]
        public void CancellationPreservesInputCoordinates()
        {
            var data=Grid("#####/..#../..#../..#..");var before=(Vector2[])data.uv.Clone();
            Assert.Throws<OperationCanceledException>(()=>UvStripSubdivision.Build(data.uv,data.triangles,new CancellationToken(true)));
            CollectionAssert.AreEqual(before,data.uv);
        }

        [Test]
        public void VArmsBecomeIndependentCompactPieces()
        {
            var uv=new[] {new Vector2(0,5),new Vector2(1,5),new Vector2(3,1),new Vector2(5,5),new Vector2(6,5),new Vector2(3,0)};
            var triangles=new[] {0,1,2,2,3,4,0,2,5,2,4,5};
            // The intrinsic taper may form a triangle at the joint. A convex
            // triangular piece fills half its best rectangle without a UV defect.
            Validate(uv,triangles,UvStripSubdivision.Build(uv,triangles),.49);
        }

        [Test]
        public void SubdivisionsKeepMaterialOrderWhenChartsSpanMultipleSlots()
        {
            var data=Grid("#####.#####/..#.....#../..#.....#../..#.....#../..#.....#..");
            var parts=new[] {0,1}.Select(part=>Enumerable.Range(0,data.triangles.Length/3).Where(f=>f%2==part)
                .SelectMany(f=>Enumerable.Range(0,3).Select(k=>data.triangles[f*3+k])).ToArray()).ToArray();
            var source=new Mesh {vertices=data.uv.Select(p=>new Vector3(p.x,p.y,0)).ToArray(),uv=data.uv,uv2=data.uv,subMeshCount=2};
            source.SetTriangles(parts[0],0);source.SetTriangles(parts[1],1);source.RecalculateNormals();Mesh copy=null;
            try
            {
                var triangles=source.triangles;var plan=UvStripSubdivision.Build(data.uv,triangles);
                Assert.Greater(plan.charts,2);Assert.That(plan.sourceFaces,Is.Ordered);
                var tags=plan.regions.SelectMany(r=>Enumerable.Repeat(new Vector2(r,0),3)).ToArray();
                copy=ReverseUvRefinedMesh.Copy(source,plan.sourceFaces,plan.barycentrics,tags);
                Assert.AreEqual(2,copy.subMeshCount);
                var positions=copy.vertices;var actual=copy.triangles;
                for(int f=0;f<plan.sourceFaces.Length;++f) for(int k=0;k<3;++k)
                {
                    var b=plan.barycentrics[f*3+k];int t=plan.sourceFaces[f]*3;
                    var p=data.uv[triangles[t]]*b.x+data.uv[triangles[t+1]]*b.y+data.uv[triangles[t+2]]*b.z;
                    Assert.Less((positions[actual[f*3+k]]-new Vector3(p.x,p.y,0)).magnitude,1e-5);
                }
            }
            finally {if(copy) Object.DestroyImmediate(copy);Object.DestroyImmediate(source);}
        }

        [TestCase(false)] [TestCase(true)]
        public void PreparedSeedAndNewStripsKeepTheirOriginalSourcePartitions(bool overlap)
        {
            var data=Grid("#####/..#../..#../..#../..#..");
            var mesh=new Mesh {name="T-strip input",vertices=data.uv.Select(p=>new Vector3(p.x,p.y,0)).ToArray(),
                triangles=data.triangles,uv=data.uv,uv2=data.uv};mesh.RecalculateNormals();
            var before=TransferMeshSnapshot.Capture(mesh);
            var levels=new[] {new ReverseUvTransfer.Level {lod=1,inputs=new[] {new ReverseUvTransfer.Input {mesh=mesh,key="seed"}}},
                new ReverseUvTransfer.Level {lod=0,inputs=new[] {new ReverseUvTransfer.Input {mesh=mesh,key="fine",toWorld=Matrix4x4.Translate(new Vector3(8,0,0))}}}};
            try
            {
                using var prepared=ReverseUvJunctionTrial.Build(levels,new ReverseUvTransfer.Options {cutNarrowJunctions=true,
                    repackFinalAtlas=true,fillFinalAtlas=true,preserveProjectedOverlap=overlap},true,16).GetAwaiter().GetResult();
                Assert.IsNull(prepared.result.report.seedPackingRefusal);
                foreach(var node in prepared.result.report.nodes)
                {
                    Assert.IsNotNull(node.sourceBarycentrics,"Both the seed and unsupported finer geometry must retain explicit strip subdivisions");
                    var sum=new double[data.triangles.Length/3];
                    for(int f=0;f<node.sourceFaces.Length;++f)
                    {
                        var a=node.sourceBarycentrics[f*3];var b=node.sourceBarycentrics[f*3+1];var c=node.sourceBarycentrics[f*3+2];
                        sum[node.sourceFaces[f]]+=((double)b.y-a.y)*(c.z-a.z)-((double)b.z-a.z)*(c.y-a.y);
                    }
                    foreach(double area in sum) Assert.That(area,Is.EqualTo(1).Within(1e-5));
                }
                ReverseUvAudit.Write(prepared.result,levels);
                CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(mesh));
            }
            finally {Object.DestroyImmediate(mesh);}
        }
    }
}
