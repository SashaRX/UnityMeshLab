using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>Optional isolated replay of a completed private chain: separates
    /// final packing from projection and retains exported Float32 geometry.</summary>
    public sealed class ReverseUvPackingReplayTests
    {
        [Serializable] sealed class Layout
        {
            public int lod;
            public string key;
            public Vector3[] positions, metricPositions;
            public Matrix4x4 metricToWorld;
            public Vector2[] uv;
            public int[] indices;
        }

        [Serializable] sealed class MotionAudit
        {
            public int relations,corners;
            public double maximumErrorPixels;
        }

        [TestCase("kamaz_Off", "reverse-exclusive")]
        [TestCase("kamaz_Off", "reverse-overlap")]
        [TestCase("Modern_Kitchen_A", "reverse-exclusive")]
        [TestCase("Modern_Kitchen_A", "reverse-overlap")]
        [TestCase("Cafe_Table_A_01", "reverse-exclusive")]
        [TestCase("Cafe_Table_A_01", "reverse-overlap")]
        [Timeout(600000)]
        public void RepackCompletedPrivateChain(string model,string mode)
        {
            string root=Environment.GetEnvironmentVariable("MESHLAB_FINALPACK_REPLAY");
            string output=Environment.GetEnvironmentVariable("MESHLAB_FINALPACK_OUTPUT");
            if(string.IsNullOrEmpty(root) || string.IsNullOrEmpty(output)) Assert.Ignore("Optional private final-packing replay is not configured.");
            string directory=Path.Combine(root,model,"32",mode);
            var report=JsonUtility.FromJson<ReverseUvTransfer.Report>(File.ReadAllText(Path.Combine(directory,"report.json")));
            int previousSide=report.atlasSize,previousFamilies=report.finalPackingFamilies;
            var layouts=report.nodes.Select(n=>JsonUtility.FromJson<Layout>(File.ReadAllText(Path.Combine(directory,n.key+".layout.json")))).ToArray();
            var surfaces=report.nodes.Select((node,n)=>{
                var l=layouts[n];
                return new ReverseUvTransfer.Surface {lod=l.lod,pixels=l.indices.Select(i=>l.uv[i]*previousSide).ToArray(),
                    input=new ReverseUvTransfer.Input {key=l.key},positions=l.positions,indices=l.indices,faces=node.faces,
                    packingFrames=Enumerable.Range(0,l.indices.Length/3).Select(f=>TransferUvQuality.WorldFrame(
                        l.metricPositions[l.indices[f*3]],l.metricPositions[l.indices[f*3+1]],l.metricPositions[l.indices[f*3+2]],l.metricToWorld)).ToArray()};
            }).GroupBy(s=>s.lod).OrderByDescending(g=>g.Key).Select(g=>g.ToArray()).ToList();
            for(int l=0;l<surfaces.Count;++l) for(int n=0;n<surfaces[l].Length;++n) surfaces[l][n].node=n;
            // Frozen prepared chains already contain intact islands. Reconstruct
            // their edge connectivity without projection chart labels, which may
            // change on only part of an island after correspondence.
            foreach(var s in surfaces.SelectMany(level=>level))
            {
                var union=new DisjointSet(s.faces.Length);
                var edges=new Dictionary<((Vector3,Vector2),(Vector3,Vector2)),int>();
                for(int f=0;f<s.faces.Length;++f) for(int k=0;k<3;++k)
                {
                    int a=f*3+k,b=f*3+(k+1)%3;
                    var x=(s.positions[s.indices[a]],s.pixels[a]);var y=(s.positions[s.indices[b]],s.pixels[b]);
                    if(edges.TryGetValue((x,y),out int other) || edges.TryGetValue((y,x),out other)) union.Union(f,other);
                    else edges.Add((x,y),f);
                }
                s.preparedRegions=Enumerable.Range(0,s.faces.Length).Select(union.Find).ToArray();
            }
            var before=surfaces.SelectMany(l=>l).ToDictionary(s=>s,s=>s.pixels.ToArray());
            int side=ReverseUvFinalPacking.Pack(surfaces,new ReverseUvTransfer.Options {padding=2,fillFinalAtlas=true,regroupPreparedAtlas=true},report,previousSide,default);
            Assert.IsTrue(string.IsNullOrEmpty(report.finalPackingRefusal),report.finalPackingRefusal);
            Assert.LessOrEqual(report.finalPackingFamilies,previousFamilies,"Packing cannot improve fill by splitting existing chart families.");
            report.atlasSize=side;
            string target=Path.Combine(output,model,"32",mode);Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target,"report.json"),JsonUtility.ToJson(report,true));
            var audit=AuditMotion(surfaces,before,report);
            File.WriteAllText(Path.Combine(target,"motion.json"),JsonUtility.ToJson(audit,true));
            Assert.Less(audit.maximumErrorPixels,.002,"Packing must retain one rigid motion for every ancestry/overlay relation.");
            var provenance=ReverseUvAudit.Provenance(report).Select(JsonUtility.FromJson<ReverseUvAudit.ProvenanceEntry>).ToArray();
            foreach(var level in surfaces)
            {
                var parts=new List<LightmapUvQuality.Part>();
                foreach(var s in level)
                {
                    var original=layouts.Single(l=>l.key==s.input.key);
                    var p=new LightmapUvQuality.Part {positions=s.indices.Select(i=>original.positions[i]).ToArray(),
                        metricPositions=s.indices.Select(i=>original.metricPositions[i]).ToArray(),metricToWorld=original.metricToWorld,
                        uv=s.pixels.Select(p=>p/side).ToArray(),triangles=Enumerable.Range(0,s.pixels.Length).ToArray(),
                        sourceKey=s.input.key,key=UvToolContext.ExtractGroupKey(s.input.key),
                        provenance=provenance.Single(p=>p.surface.lod==s.lod && p.surface.key==s.input.key)};
                    parts.Add(p);
                    File.WriteAllText(Path.Combine(target,s.input.key+".layout.json"),JsonUtility.ToJson(new Layout {lod=s.lod,key=s.input.key,
                        positions=p.positions,metricPositions=p.metricPositions,metricToWorld=p.metricToWorld,uv=p.uv,indices=p.triangles}));
                }
                var q=LightmapUvQuality.Measure(parts.ToArray());
                File.WriteAllText(Path.Combine(target,"quality_LOD"+level[0].lod+".json"),JsonUtility.ToJson(q,true));
                Assert.IsTrue(q.complete);Assert.AreEqual(0,q.invalidFaces+q.collapsedFaces+q.unexpectedOverlapPairs);
            }
        }

        static MotionAudit AuditMotion(List<ReverseUvTransfer.Surface[]> levels,
            Dictionary<ReverseUvTransfer.Surface,Vector2[]> before,ReverseUvTransfer.Report report)
        {
            var result=new MotionAudit();var byLod=levels.ToDictionary(l=>l[0].lod);
            foreach(var level in levels) foreach(var child in level)
                for(int f=0;f<child.faces.Length;++f)
                {
                    var face=child.faces[f];
                    if(face.inherited && face.parentLod>child.lod)
                        Check(byLod[face.parentLod][face.parentMesh],face.parentFace,child,f);
                }
            foreach(var s in levels.SelectMany(level=>level))
                foreach(var island in Enumerable.Range(0,s.faces.Length).GroupBy(f=>s.preparedRegions[f]))
                    foreach(int f in island) Check(s,island.First(),s,f);
            foreach(var r in report.overlaps)
                Check(byLod[r.lod][r.underMesh],r.underFace,byLod[r.lod][r.overMesh],r.overFace);
            return result;

            void Check(ReverseUvTransfer.Surface parent,int parentFace,ReverseUvTransfer.Surface child,int childFace)
            {
                int a=parentFace*3,b=childFace*3;double best=double.MaxValue;
                for(int turn=0;turn<4;++turn)
                {
                    double error=0;
                    for(int k=0;k<3;++k)
                    {
                        error=Math.Max(error,Error(before[parent][a+k],parent.pixels[a+k],turn));
                        error=Math.Max(error,Error(before[child][b+k],child.pixels[b+k],turn));
                    }
                    best=Math.Min(best,error);
                }
                ++result.relations;result.corners+=6;result.maximumErrorPixels=Math.Max(result.maximumErrorPixels,best);

                double Error(Vector2 old,Vector2 moved,int turn)
                {
                    double x=(double)old.x-before[parent][a].x,y=(double)old.y-before[parent][a].y;
                    if(turn==1) (x,y)=(-y,x);
                    else if(turn==2) (x,y)=(-x,-y);
                    else if(turn==3) (x,y)=(y,-x);
                    double dx=parent.pixels[a].x+x*report.finalPackingScale-moved.x;
                    double dy=parent.pixels[a].y+y*report.finalPackingScale-moved.y;
                    return Math.Sqrt(dx*dx+dy*dy);
                }
            }
        }
    }
}
