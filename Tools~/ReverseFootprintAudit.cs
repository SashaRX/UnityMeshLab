using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

// Private benchmark driver, copied into the isolated Unity project only.
public static class ReverseFootprintAudit
{
    [Serializable] public class Layout { public int lod; public Vector3[] positions; public Vector2[] uv; public int[] indices; }
    [Serializable] public class Face { public int chart; }
    [Serializable] public class Node { public string key; public int lod; public Face[] faces; }
    [Serializable] public class Report { public int atlasSize; public Node[] nodes; }
    [Serializable] public class Item { public int chart; public Vector2[] triangles, proxy; public double area, proxyArea; public string[] nodes; }
    [Serializable] public class Audit { public Item[] groups; }
    public static void Run()
    {
        string root=Environment.GetEnvironmentVariable("MESHLAB_FOOTPRINT_INPUT");
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("SashaRX.UnityMeshLab.ReverseUvNativeFamilyPacking")).First(t=>t!=null);
        var method=type.GetMethod("Footprint",BindingFlags.Static|BindingFlags.NonPublic);
        foreach(var model in new[]{"Modern_Kitchen_A","kamaz_Off"})
        {
            string dir=Path.Combine(root,model);var report=JsonUtility.FromJson<Report>(File.ReadAllText(Path.Combine(dir,"report.json")));
            var groups=new Dictionary<int,List<Vector2>>();var names=new Dictionary<int,HashSet<string>>();
            foreach(var node in report.nodes)
            {
                var mesh=JsonUtility.FromJson<Layout>(File.ReadAllText(Path.Combine(dir,node.key+".layout.json")));
                for(int f=0;f<node.faces.Length;++f)
                {
                    int chart=node.faces[f].chart;
                    if(!groups.ContainsKey(chart)) {groups.Add(chart,new List<Vector2>());names.Add(chart,new HashSet<string>());}
                    names[chart].Add(node.key);
                    for(int k=0;k<3;++k) groups[chart].Add(mesh.uv[mesh.indices[f*3+k]]*report.atlasSize);
                }
            }
            var audit=new Audit {groups=groups.Select(g=>{
                var triangles=g.Value.ToArray();var proxy=(Vector2[])method.Invoke(null,new object[]{triangles,CancellationToken.None,true,true});
                return new Item {chart=g.Key,triangles=triangles,proxy=proxy,area=Area(triangles),proxyArea=Area(proxy),nodes=names[g.Key].ToArray()};
            }).ToArray()};
            File.WriteAllText(Path.Combine(dir,"footprint-audit.json"),JsonUtility.ToJson(audit));
            Debug.Log(model+": "+audit.groups.Length+" footprints captured");
        }
    }
    static double Area(Vector2[] p) {double sum=0;for(int i=0;i<p.Length;i+=3)
        sum+=Math.Abs(((double)p[i+1].x-p[i].x)*((double)p[i+2].y-p[i].y)-((double)p[i+1].y-p[i].y)*((double)p[i+2].x-p[i].x))*.5;return sum;}
}
