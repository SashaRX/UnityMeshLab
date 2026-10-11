using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        /// <summary>Only newly unwrapped faces can acquire these additional seams.
        /// Inherited faces keep their atlas coordinates and ancestry unchanged.</summary>
        static int[] CutNewStrips(ref List<(Surface s,int f)> refs,ref List<Vector3> positions,
            ref Vector2[] uv,Options options,float density,CancellationToken token)
        {
            var vertices=new List<Vector2>();var world=new List<Vector3>();var indices=new int[uv.Length];
            var unique=new Dictionary<(Surface,Vector3,Vector2),int>();
            for(int i=0;i<uv.Length;++i)
            {
                var key=(refs[i/3].s,positions[i],uv[i]);
                if(!unique.TryGetValue(key,out int v)) {v=vertices.Count;vertices.Add(uv[i]);world.Add(positions[i]);unique.Add(key,v);}
                indices[i]=v;
            }
            var plan=UvStripSubdivision.Build(vertices.ToArray(),indices,token,world.ToArray(),
                options.maxAtlasSize*1.1920928955078125e-7*8/density);
            if(plan.cuts==0) return null;
            var selected=Enumerable.Range(0,plan.sourceFaces.Length).GroupBy(f=>plan.sourceFaces[f]).ToDictionary(g=>g.Key,g=>g.ToArray());
            var newRefs=new List<(Surface,int)>();var newPositions=new List<Vector3>();
            var newUv=new List<Vector2>();var regions=new List<int>();
            foreach(var surfaceGroup in refs.Select((p,i)=>(p,i)).GroupBy(p=>p.p.s))
            {
                var s=surfaceGroup.Key;var local=surfaceGroup.ToDictionary(p=>p.p.f,p=>p.i);
                var corners=new List<Vector3>();var pixels=new List<Vector2>();var faces=new List<Face>();
                var sourceFaces=new List<int>();var bary=new List<Vector3>();
                for(int f=0;f<s.faces.Length;++f)
                {
                    token.ThrowIfCancellationRequested();
                    if(!local.TryGetValue(f,out int original)) Add(f,new[] {Vector3.right,Vector3.up,Vector3.forward},-1);
                    else foreach(int output in selected[original])
                        Add(f,Enumerable.Range(0,3).Select(k=>plan.barycentrics[output*3+k]).ToArray(),output);
                }
                void Add(int f,Vector3[] weights,int output)
                {
                    int start=f*3,destination=faces.Count;
                    faces.Add(s.faces[f].Copy());sourceFaces.Add(s.sourceFaces[f]);
                    for(int k=0;k<3;++k)
                    {
                        var b=weights[k];
                        var p=s.positions[s.indices[start]]*b.x+s.positions[s.indices[start+1]]*b.y+s.positions[s.indices[start+2]]*b.z;
                        corners.Add(p);pixels.Add(s.pixels[start]*b.x+s.pixels[start+1]*b.y+s.pixels[start+2]*b.z);
                        bary.Add(s.sourceBarycentrics==null?b:
                            s.sourceBarycentrics[start]*b.x+s.sourceBarycentrics[start+1]*b.y+s.sourceBarycentrics[start+2]*b.z);
                        if(output>=0) {newPositions.Add(p);newUv.Add(plan.uv[output*3+k]);}
                    }
                    if(output>=0) {newRefs.Add((s,destination));regions.Add(plan.regions[output]);}
                }
                s.positions=corners.ToArray();s.indices=Enumerable.Range(0,corners.Count).ToArray();s.pixels=pixels.ToArray();
                s.faces=faces.ToArray();s.sourceFaces=sourceFaces.ToArray();s.sourceBarycentrics=bary.ToArray();
                s.splitSourceFaces=sourceFaces.GroupBy(f=>f).Count(g=>g.Count()>1);
                RefreshMetricFrames(s);
            }
            refs=newRefs;positions=newPositions;uv=newUv.ToArray();
            return regions.ToArray();
        }
    }
}
