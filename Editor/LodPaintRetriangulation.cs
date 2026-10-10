using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    // Flip only an isolated pair's diagonal; retain its four geometric corners,
    // boundary, winding, render-buffer size and continuous UV charts.
    internal static class LodPaintRetriangulation
    {
        const float UvViewRmsTolerance=2e-5f;
        [Serializable] internal sealed class Report
        {
            public bool accepted;
            public int pairs,trials,flips,verticesBefore,verticesAfter,intersectionRejected,screenRejected,uvRejected;
            public float boundaryBefore,boundaryAfter,uvBefore,uvAfter,distanceBefore,distanceAfter;
            public float uvViewRmsTolerance=UvViewRmsTolerance;
            public float[] uvViewsBefore,uvViewsAfter;
            public List<Probe> probes=new List<Probe>();
        }
        [Serializable] internal sealed class Probe { public int a,b,method; public string refusal; public float boundary,uv,distance,maximumDistance,detail,rgba,outlineMean,outlineMax; public float[] boundaries,uvViews; }
        readonly struct Side
        {
            internal readonly int face,corner;
            internal Side(int face,int corner) { this.face=face; this.corner=corner; }
        }

        internal static Mesh Correct(Mesh source,Mesh reduced,LodScreenValidation screen,
            MeshSimplifier.SimplifySettings settings,out Report report,Func<bool> cancelled = null)
        {
            report = new Report { verticesBefore=reduced.vertexCount,verticesAfter=reduced.vertexCount };
            if (screen == null || source.vertexCount == 0 || reduced.vertexCount == 0 ||
                source.colors.Length != source.vertexCount || reduced.colors.Length != reduced.vertexCount ||
                source.subMeshCount != 1 || reduced.subMeshCount != 1 || reduced.GetTopology(0) != MeshTopology.Triangles ||
                source.blendShapeCount > 0 || reduced.blendShapeCount > 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                reduced.HasVertexAttribute(VertexAttribute.BlendWeight) || LodMeshData.TriangleCount(reduced) > 128) return null;
            bool sourceUv2=source.HasVertexAttribute(VertexAttribute.TexCoord1),targetUv2=reduced.HasVertexAttribute(VertexAttribute.TexCoord1);
            if (sourceUv2!=targetUv2 || sourceUv2 && (source.GetVertexAttributeDimension(VertexAttribute.TexCoord1)!=reduced.GetVertexAttributeDimension(VertexAttribute.TexCoord1) ||
                source.normals.Length!=source.vertexCount || reduced.normals.Length!=reduced.vertexCount)) return null;
            var before=screen.Measure(reduced,cancelled); var best=before;
            report.boundaryBefore=report.boundaryAfter=before.colorBoundaryMean;
            if (!before.rgbaEvaluated || before.objectPixels > 128 || before.colorBoundaryMean <= 0) return null;
            var verification=screen.WithAllSides(source,cancelled); var allBefore=verification.Measure(reduced,cancelled);
            var sourceData=new LodMeshData(source);
            var uvBefore=UvViews(sourceData,reduced,verification,cancelled);
            report.uvViewsBefore=report.uvViewsAfter=uvBefore;
            var originalMetrics=LodSurfaceValidation.MeasureMeshes(source,reduced,settings,cancelled,ignoreDegenerateFaces:true);
            report.uvBefore=report.uvAfter=originalMetrics.UvRms;
            report.distanceBefore=report.distanceAfter=originalMetrics.DistanceRms;
            Mesh accepted=null,trial=null;
            try
            {
                // Rebuild connectivity after every accepted flip. A small cap bounds
                // the extra work on the far-LOD path.
                for (int pass=0; pass<4; pass++)
                {
                    Check(cancelled); var current=accepted ? accepted : reduced;
                    var data=new LodMeshData(current); var indices=current.triangles;
                    var colorFormat=current.GetVertexAttributeFormat(VertexAttribute.Color);
                    var active=new HashSet<int>(screen.LostPaintFaces(current,cancelled));
                    var edges=Edges(data.positions,indices); var uses=new int[current.vertexCount];
                    foreach (int v in indices) uses[v]++;
                    bool changed=false;
                    foreach (var pair in edges.OrderBy(p => p.Key))
                    {
                        Check(cancelled); if (pair.Value.Count != 2) continue;
                        var a=pair.Value[0]; var b=pair.Value[1];
                        if (!active.Contains(a.face) && !active.Contains(b.face)) continue;
                        var left=Enumerable.Range(0,3).Select(k => indices[a.face*3+(a.corner+k)%3]).ToArray();
                        var right=Enumerable.Range(0,3).Select(k => indices[b.face*3+(b.corner+k)%3]).ToArray();
                        if (!CanFlip(data,left,right,uses,indices)) continue;
                        report.pairs++;
                        trial=UnityEngine.Object.Instantiate(current);
                        var replacements=new Dictionary<int,int> { [left[0]]=left[2],[left[1]]=right[2],[left[2]]=left[1],
                            [right[0]]=right[2],[right[1]]=left[2],[right[2]]=right[1] };
                        CopyCorners(trial,data,replacements);
                        if (IntroducesIntersection(data.positions,trial.vertices,indices,a.face,b.face))
                        { report.intersectionRejected++; UnityEngine.Object.DestroyImmediate(trial); trial=null; continue; }
                        var colors=trial.colors;
                        var sums=new Vector4[2]; var masses=new double[2];
                        var clusters=new[] { new List<(Vector4 sum,double mass)>(),new List<(Vector4 sum,double mass)>() };
                        screen.AddVisibleBoundaryColorSamples(trial,(t,offset,bary,value,mass) => {
                            int f=offset/3,id=f==a.face ? 0 : f==b.face ? 1 : -1; if (id < 0) return;
                            sums[id]+=value*(float)mass; masses[id]+=mass;
                            var list=clusters[id]; int found=list.FindIndex(c => MaxAbs(value-c.sum/(float)c.mass)<.15f);
                            if (found<0) list.Add((value*(float)mass,mass));
                            else list[found]=(list[found].sum+value*(float)mass,list[found].mass+mass);
                        },8,cancelled);
                        LodAttributeCorrection.FitVisibleUv2(source,trial,verification,cancelled);
                        for (int method=0; method<3; method++)
                        {
                            Check(cancelled); var values=(Color[])colors.Clone();
                            if (method>0) for (int id=0; id<2; id++)
                            {
                                if (masses[id]<=0) continue;
                                var value=sums[id]/(float)masses[id];
                                if (method==2) { var cluster=clusters[id].OrderByDescending(c => c.mass).First(); value=cluster.sum/(float)cluster.mass; }
                                foreach (int v in id==0 ? left : right) values[v]=value;
                            }
                            if (colorFormat==VertexAttributeFormat.UNorm8) trial.SetColors(values.Select(c => (Color32)c).ToArray());
                            else trial.colors=values;
                            UvProgress.Report(UvProgress.Current.fraction,$"{source.name}: verify far paint diagonal");
                            var local=screen.Measure(trial,cancelled); report.trials++;
                            var probe=new Probe { a=a.face,b=b.face,method=method,boundary=local.colorBoundaryMean,detail=local.detailLoss,rgba=local.rgbaRms,outlineMean=local.silhouetteMean,outlineMax=local.silhouetteMax,boundaries=local.colorBoundaryViewLoss }; report.probes.Add(probe);
                            if (!local.DoesNotWorsen(best) || !local.PaintViewsDoNotWorsen(best) ||
                                local.colorBoundaryMean >= best.colorBoundaryMean-1e-6f || local.rgbaRms>before.rgbaRms+1e-6f ||
                                local.silhouetteMean>before.silhouetteMean+1e-6f || local.silhouetteMax>before.silhouetteMax+1e-6f) { probe.refusal="screen"; report.screenRejected++; continue; }
                            var all=verification.Measure(trial,cancelled);
                            if (!all.ColorViewsWithin(allBefore,1f/255) || all.detailLoss>allBefore.detailLoss+1e-6f ||
                                all.silhouetteMean>allBefore.silhouetteMean+1e-6f || all.silhouetteMax>allBefore.silhouetteMax+1e-6f) { probe.refusal="reverse views"; continue; }
                            var metrics=LodSurfaceValidation.MeasureMeshes(source,trial,settings,cancelled,ignoreDegenerateFaces:true);
                            probe.uv=metrics.UvRms; probe.distance=metrics.DistanceRms; probe.maximumDistance=metrics.distance;
                            var uvViews=UvViews(sourceData,trial,verification,cancelled);
                            probe.uvViews=uvViews;
                            if (float.IsNaN(metrics.weightedError) || float.IsInfinity(metrics.weightedError) ||
                                metrics.UvRms>originalMetrics.UvRms+1e-6f ||
                                uvViews.Where((error,v) => float.IsNaN(error) || float.IsInfinity(error) || error>uvBefore[v]+UvViewRmsTolerance).Any() || metrics.DistanceRms>originalMetrics.DistanceRms+1e-6f ||
                                metrics.distance>originalMetrics.distance+1e-6f) { probe.refusal="surface/UV"; report.uvRejected++; continue; }
                            var replacement=UnityEngine.Object.Instantiate(trial);
                            if (accepted) UnityEngine.Object.DestroyImmediate(accepted); accepted=replacement; best=local;
                            report.accepted=true; changed=true;
                            report.uvAfter=metrics.UvRms; report.distanceAfter=metrics.DistanceRms;
                            report.uvViewsAfter=uvViews;
                        }
                        UnityEngine.Object.DestroyImmediate(trial); trial=null;
                        if (changed) { report.flips++; break; }
                    }
                    if (!changed) break;
                }
                report.boundaryAfter=best.colorBoundaryMean;
                var result=accepted; accepted=null; return result;
            }
            finally { if (trial) UnityEngine.Object.DestroyImmediate(trial); if (accepted) UnityEngine.Object.DestroyImmediate(accepted); }
        }

        static Dictionary<long,List<Side>> Edges(Vector3[] positions,int[] indices)
        {
            var ids=new Dictionary<Vector3,int>(); var edges=new Dictionary<long,List<Side>>();
            int Point(int v) { if (!ids.TryGetValue(positions[v],out int id)) ids[positions[v]]=id=ids.Count; return id; }
            for (int f=0; f<indices.Length/3; f++) for (int k=0; k<3; k++)
            {
                long key=LodTopologyGraph.EdgeKey(Point(indices[f*3+k]),Point(indices[f*3+(k+1)%3]));
                if (!edges.TryGetValue(key,out var sides)) edges[key]=sides=new List<Side>(); sides.Add(new Side(f,k));
            }
            return edges;
        }
        static bool CanFlip(LodMeshData data,int[] a,int[] b,int[] uses,int[] indices)
        {
            var p=data.positions;
            if (!p[a[0]].Equals(p[b[1]]) || !p[a[1]].Equals(p[b[0]]) ||
                a.Concat(b).Distinct().Count()!=6 || a.Concat(b).Any(v => uses[v]!=1) || p[a[2]].Equals(p[b[2]])) return false;
            var n=Vector3.Cross(p[a[1]]-p[a[0]],p[a[2]]-p[a[0]]);
            var m=Vector3.Cross(p[b[1]]-p[b[0]],p[b[2]]-p[b[0]]);
            if (n.sqrMagnitude<1e-20f || m.sqrMagnitude<1e-20f || Vector3.Dot(n.normalized,m.normalized)<.99f) return false;
            var normal=(n+m).normalized;
            if (Vector3.Dot(Vector3.Cross(p[b[2]]-p[a[2]],p[a[1]]-p[a[2]]),normal)<=1e-10f ||
                Vector3.Dot(Vector3.Cross(p[a[2]]-p[b[2]],p[a[0]]-p[b[2]]),normal)<=1e-10f) return false;
            // Pin UV and shading interfaces; new corners carry the exact field
            // values at their retained geometric endpoints.
            for (int k=0; k<2; k++)
            {
                int x=a[k],y=b[1-k];
                for (int ch=0; ch<8; ch++) if (data.uvs[ch]!=null && (data.uvs[ch][x]-data.uvs[ch][y]).sqrMagnitude>1e-12f) return false;
                if (data.normals.Length==p.Length && Vector3.Dot(data.normals[x].normalized,data.normals[y].normalized)<.99f) return false;
                if (data.tangents.Length==p.Length && data.tangents[x].w!=data.tangents[y].w) return false;
            }
            for (int i=0; i<indices.Length; i++)
            {
                // The opposite diagonal must not already belong to the mesh.
                var x=p[indices[i]]; var y=p[indices[i/3*3+(i+1)%3]];
                if (x.Equals(p[a[2]]) && y.Equals(p[b[2]]) || x.Equals(p[b[2]]) && y.Equals(p[a[2]])) return false;
            }
            return true;
        }
        static void CopyCorners(Mesh mesh,LodMeshData data,Dictionary<int,int> replacements)
        {
            var positions=(Vector3[])data.positions.Clone(); var colors=(Color[])data.colors.Clone();
            var normals=(Vector3[])data.normals.Clone(); var tangents=(Vector4[])data.tangents.Clone();
            foreach (var pair in replacements)
            {
                positions[pair.Key]=data.positions[pair.Value]; colors[pair.Key]=data.colors[pair.Value];
                if (normals.Length==positions.Length) normals[pair.Key]=data.normals[pair.Value];
                if (tangents.Length==positions.Length) tangents[pair.Key]=data.tangents[pair.Value];
            }
            mesh.vertices=positions; if (normals.Length==positions.Length) mesh.normals=normals;
            if (tangents.Length==positions.Length) mesh.tangents=tangents;
            if (data.source.GetVertexAttributeFormat(VertexAttribute.Color)==VertexAttributeFormat.UNorm8) mesh.SetColors(colors.Select(c => (Color32)c).ToArray());
            else mesh.colors=colors;
            for (int ch=0; ch<8; ch++) if (data.uvs[ch]!=null)
            {
                var values=data.uvs[ch].ToArray(); foreach (var pair in replacements) values[pair.Key]=data.uvs[ch][pair.Value];
                if (data.uvDimensions[ch]==2) mesh.SetUVs(ch,values.Select(v => (Vector2)v).ToList());
                else if (data.uvDimensions[ch]==3) mesh.SetUVs(ch,values.Select(v => (Vector3)v).ToList()); else mesh.SetUVs(ch,values.ToList());
            }
            mesh.RecalculateBounds();
        }
        static bool IntroducesIntersection(Vector3[] before,Vector3[] after,int[] indices,int a,int b)
        {
            foreach (int changed in new[] { a,b }) for (int f=0; f<indices.Length/3; f++)
            {
                if (f==a || f==b || !LodTriangleIntersections.Overlap(after,indices,changed*3,indices,f*3)) continue;
                // A reduction can already contain touching/overlapping sheets;
                // changing one diagonal must not introduce a new sheet contact.
                if (!LodTriangleIntersections.Overlap(before,indices,a*3,indices,f*3) &&
                    !LodTriangleIntersections.Overlap(before,indices,b*3,indices,f*3)) return true;
            }
            return LodTriangleIntersections.Overlap(after,indices,a*3,indices,b*3);
        }
        static float[] UvViews(LodMeshData source,Mesh mesh,LodScreenValidation screen,Func<bool> cancelled)
        {
            var target=new LodMeshData(mesh); if (source.uvs[1]==null && target.uvs[1]==null) return Array.Empty<float>();
            if (source.uvs[1]==null || target.uvs[1]==null || source.uvDimensions[1]!=target.uvDimensions[1]) return new[] { float.PositiveInfinity };
            var sourceIndices=source.source.triangles; var targetIndices=mesh.triangles;
            var sums=new double[12]; var counts=new int[12];
            screen.AddVisibleSurfaceSamples(mesh,(sf,sb,tf,tb,sn,tn,view,pixel) => {
                var a=source.uvs[1][sourceIndices[sf*3]]*sb.x+source.uvs[1][sourceIndices[sf*3+1]]*sb.y+source.uvs[1][sourceIndices[sf*3+2]]*sb.z;
                var b=target.uvs[1][targetIndices[tf*3]]*tb.x+target.uvs[1][targetIndices[tf*3+1]]*tb.y+target.uvs[1][targetIndices[tf*3+2]]*tb.z;
                sums[view]+=(a-b).sqrMagnitude; counts[view]++;
            },cancelled);
            return sums.Select((sum,v) => counts[v]>0 ? (float)Math.Sqrt(sum/counts[v]) : 0).ToArray();
        }
        static float MaxAbs(Vector4 v) => Mathf.Max(Mathf.Abs(v.x),Mathf.Max(Mathf.Abs(v.y),Mathf.Max(Mathf.Abs(v.z),Mathf.Abs(v.w))));
        static void Check(Func<bool> cancelled) { if (cancelled?.Invoke()==true) throw new OperationCanceledException(); }
    }
}
