using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;
using Debug = UnityEngine.Debug;

// Copy into a dedicated Unity project's Assets/Editor; it uses the installed
// package through reflection and never touches an imported model or EditorPrefs.
public static class SurfaceRefineReplay
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Type Native = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshNative");
    static readonly Type Indexed = Native.GetNestedType("IndexedMesh", Flags);
    static readonly Type Refiner = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshSurfaceRefine");
    static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    static object Mesh(Vector3[] p, int[] ix)
    {
        var mesh = Activator.CreateInstance(Indexed, true);
        Indexed.GetField("positions", Flags).SetValue(mesh, p); Indexed.GetField("indices", Flags).SetValue(mesh, ix);
        return mesh;
    }
    static object Read(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        var p = new Vector3[reader.ReadInt32()]; var ix = new int[reader.ReadInt32()];
        for (int i = 0; i < p.Length; i++) p[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        for (int i = 0; i < ix.Length; i++) ix[i] = reader.ReadInt32();
        return Mesh(p, ix);
    }
    static byte[] Bytes(object mesh)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var p = (Vector3[])Get(mesh, "positions"); var ix = (int[])Get(mesh, "indices");
        writer.Write(p.Length); writer.Write(ix.Length);
        foreach (var v in p) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
        foreach (int i in ix) writer.Write(i);
        return stream.ToArray();
    }
    static object Refine(object mesh, object source, float cell, string name)
    {
        var args = new object[] { mesh, Get(source, "positions"), Get(source, "indices"), cell, CancellationToken.None, null };
        var watch = Stopwatch.StartNew();
        var result = Refiner.GetMethod("Apply", Flags).Invoke(null, args);
        Debug.Log("SURFACE_REFINE " + name + " moves=" + Get(args[5], "moves") + " flips=" + Get(args[5], "flips") +
            " anchors=" + Get(args[5], "features") + " reverted=" + Get(args[5], "reverted") + " seconds=" + watch.Elapsed.TotalSeconds);
        return result;
    }
    static object Simplify(object mesh, RemeshSettings settings)
    {
        var args = new object[] { mesh, settings, CancellationToken.None, 0f };
        var result = Native.GetMethod("Simplify", Flags).Invoke(null, args);
        Debug.Log("SURFACE_COLLAPSE error=" + args[3]);
        return result;
    }
    static double[] SampleDistance(Vector3[] p, int[] ix, TriangleBvh target)
    {
        double area = 0, sum = 0, sum2 = 0, max = 0;
        for (int f = 0; f < ix.Length; f += 3) {
            var a = p[ix[f]]; var b = p[ix[f + 1]]; var c = p[ix[f + 2]];
            double weight = Vector3.Cross(b - a, c - a).magnitude * .5;
            foreach (var point in new[] { (a + b) / 2, (b + c) / 2, (c + a) / 2, (a + b + c) / 3 }) {
                double distance = Math.Sqrt(target.FindNearest(point).distSq);
                area += weight; sum += weight * distance; sum2 += weight * distance * distance; max = Math.Max(max, distance);
            }
        }
        return new[] { sum / area, Math.Sqrt(sum2 / area), max };
    }
    static string Measure(object mesh, object source, string name, float cell)
    {
        var p = (Vector3[])Get(mesh, "positions"); var ix = (int[])Get(mesh, "indices");
        var sp = (Vector3[])Get(source, "positions"); var si = (int[])Get(source, "indices");
        var forward = SampleDistance(p, ix, new TriangleBvh(sp, si));
        var reverse = SampleDistance(sp, si, new TriangleBvh(p, ix));
        double sum = 0; int slivers = 0;
        for (int f = 0; f < ix.Length; f += 3) {
            var a = p[ix[f]]; var b = p[ix[f + 1]]; var c = p[ix[f + 2]];
            double twiceArea = Vector3.Cross(b - a, c - a).magnitude;
            double sides = (b - a).sqrMagnitude + (c - b).sqrMagnitude + (a - c).sqrMagnitude;
            sum += 2 * Math.Sqrt(3) * twiceArea / sides;
            double aspect = Math.Sqrt(3) * Math.Max((b - a).sqrMagnitude, Math.Max((c - b).sqrMagnitude, (a - c).sqrMagnitude)) / (2 * twiceArea);
            if (aspect > 10) slivers++;
        }
        var topo = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshTopology").GetMethod("Inspect", Flags)
            .Invoke(null, new object[] { p, ix, CancellationToken.None });
        if (!(bool)topo.GetType().GetProperty("Valid", Flags).GetValue(topo)) throw new Exception("Invalid topology: " + name);
        int borders = ((System.Collections.ICollection)Get(topo, "euler")).Count; // components, reported separately below
        int boundary = (int)Get(topo, "boundary").GetType().GetProperty("Count").GetValue(Get(topo, "boundary"));
        var values = new object[] { name, ix.Length / 3, sum / (ix.Length / 3), slivers, boundary, borders,
            forward[0] / cell, forward[1] / cell, forward[2] / cell, reverse[0] / cell, reverse[1] / cell, reverse[2] / cell };
        string row = string.Join(",", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
        Debug.Log("SURFACE_METRICS " + row); return row;
    }
    static void Unwrap(object mesh, RemeshSettings settings, string file)
    {
        settings.mergeCharts = false;
        var uv = Native.GetMethod("Unwrap", Flags).Invoke(null, new object[] { mesh, settings, CancellationToken.None });
        var p = (Vector3[])Get(uv, "positions"); var ix = (int[])Get(uv, "indices"); var tex = (Vector2[])Get(uv, "uv"); var charts = (int[])Get(uv, "charts");
        var op = (Vector3[])Get(mesh, "positions"); var oi = (int[])Get(mesh, "indices");
        if (oi.Length != ix.Length) throw new Exception("Unwrap changed face count");
        for (int i = 0; i < ix.Length; i++) if (!op[oi[i]].Equals(p[ix[i]])) throw new Exception("Unwrap changed source corner");
        var diag = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics").GetMethod("Measure", Flags)
            .Invoke(null, new object[] { uv, CancellationToken.None, false, 2000000L, false });
        if (!(bool)Get(diag, "complete") || (int)Get(diag, "pairs") != 0 || (int)Get(diag, "invalidFaces") != 0 || (int)Get(diag, "degenerateFaces") != 0)
            throw new Exception("Invalid final UV: " + file);
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write(p.Length); writer.Write(ix.Length); writer.Write((int)Get(uv, "chartCount"));
        for (int i = 0; i < p.Length; i++) { writer.Write(p[i].x); writer.Write(p[i].y); writer.Write(p[i].z); writer.Write(tex[i].x); writer.Write(tex[i].y); writer.Write(charts[i]); }
        foreach (int i in ix) writer.Write(i);
        Debug.Log("SURFACE_UV " + file + " charts=" + Get(uv, "chartCount") + " overlap scan clean; corners preserved");
    }
    static void Case(object input, object source, RemeshSettings settings, string name, string dir, List<string> rows)
    {
        var sp = (Vector3[])Get(source, "positions"); var low = sp[0]; var high = low;
        foreach (var p in sp) { low = Vector3.Min(low, p); high = Vector3.Max(high, p); }
        var e = high - low; float cell = Mathf.Max(e.x, Mathf.Max(e.y, e.z)) / settings.voxelResolution;
        byte[] initial = Bytes(input);
        var before = Refine(input, source, cell, name + "-before");
        var repeat = Refine(input, source, cell, name + "-repeat");
        if (!Bytes(before).SequenceEqual(Bytes(repeat)) || !initial.SequenceEqual(Bytes(input))) throw new Exception("Refinement changed input or is nondeterministic");
        var baseline = Simplify(input, settings);
        var args = new object[] { input, Get(source, "positions"), Get(source, "indices"), settings, CancellationToken.None, 0f };
        var after = Refiner.GetMethod("Simplify", Flags).Invoke(null, args);
        foreach (var value in new[] { ("voxel", input), ("voxel-refined", before), ("simplify-baseline", baseline), ("simplify-refined", after) }) {
            string label = name + "-" + value.Item1;
            File.WriteAllBytes(Path.Combine(dir, label + ".bin"), Bytes(value.Item2));
            rows.Add(Measure(value.Item2, source, label, cell));
        }
        File.WriteAllLines(Path.Combine(dir, "metrics.csv"), rows);
        File.WriteAllBytes(Path.Combine(dir, name + "-source.bin"), Bytes(source));
        Unwrap(baseline, settings, Path.Combine(dir, name + "-baseline-uv.bin"));
        Unwrap(after, settings, Path.Combine(dir, name + "-refined-uv.bin"));
    }
    public static async void Run()
    {
        try {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dir = Path.Combine(root, "surface-refine"); Directory.CreateDirectory(dir);
            await Task.Run(() => {
                var rows = new List<string> { "method,faces,meanQuality,sliverFaces,boundaryEdges,components,targetToSourceMeanCells,targetToSourceRmsCells,targetToSourceMaxCells,sourceToTargetMeanCells,sourceToTargetRmsCells,sourceToTargetMaxCells" };
                var settings = new RemeshSettings { voxelResolution = 128, targetTriangles = 1600, maximumError = .0041f, preserveFolds = true, textureResolution = 512, padding = 3 };
                Case(Read(Path.Combine(root, "simplify-topology/trim-0.bin")), Read(Path.Combine(root, "simplify-topology/source-0.bin")), settings, "bust", dir, rows);
                var p = new[] { new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
                    new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
                var rotation = Quaternion.Euler(23, 31, 17);
                for (int i = 0; i < p.Length; i++) p[i] = rotation * Vector3.Scale(p[i], new Vector3(1, .6f, .4f));
                var ix = new[] { 4,5,6,4,6,7, 0,3,2,0,2,1, 0,4,7,0,7,3, 1,2,6,1,6,5, 0,1,5,0,5,4, 3,7,6,3,6,2 };
                settings.voxelResolution = 32; settings.targetTriangles = 100; settings.maximumError = .003f;
                var voxel = Native.GetMethod("Voxelize", Flags).Invoke(null, new object[] { p, ix, settings, CancellationToken.None });
                Case(voxel, Mesh(p, ix), settings, "diagonal-box", dir, rows);
                File.WriteAllLines(Path.Combine(dir, "metrics.csv"), rows);
            });
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }
}
