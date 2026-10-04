// Copy into a scratch Unity project's Assets/Editor and reference this package.
// Set MESHLAB_UV_REFERENCE_FBX to one FBX with one MeshFilter, and optionally
// MESHLAB_UV_REFERENCE_TAG to a distinct tag. Run -executeMethod UvReferenceBenchmark.Run.
// Captures original UVs/normals, then repeats the real Unwrap entry point three
// times with Merge off/on. Does not modify the source FBX or EditorPrefs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;

public static class UvReferenceBenchmark
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly Assembly Assembly = typeof(RemeshSettings).Assembly;
    static readonly Type Native = Assembly.GetType("SashaRX.UnityMeshLab.RemeshNative");
    static object Get(object g, string n) => g.GetType().GetField(n, Flags).GetValue(g);
    static void Set(object g, string n, object value) => g.GetType().GetField(n, Flags).SetValue(g, value);

    static int Root(int[] parent, int a)
    {
        while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
        return a;
    }

    static int[] Charts(Vector3[] p, Vector2[] uv, int[] ix, out int count)
    {
        var slots = (int[])Assembly.GetType("SashaRX.UnityMeshLab.MeshGeometry").GetMethod("WeldPositions", Flags)
            .Invoke(null, new object[] { p, 0 });
        int[] parent = Enumerable.Range(0, ix.Length / 3).ToArray();
        var edges = new Dictionary<(int, int), List<(int face, int a, int b)>>();
        for (int face = 0; face < parent.Length; ++face)
            for (int k = 0; k < 3; ++k)
            {
                int a = ix[face * 3 + k], b = ix[face * 3 + (k + 1) % 3];
                var key = (Math.Min(slots[a], slots[b]), Math.Max(slots[a], slots[b]));
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<(int, int, int)>();
                list.Add((face, a, b));
            }
        foreach (var list in edges.Values)
        {
            if (list.Count != 2) continue;
            var a = list[0]; var b = list[1];
            int b0 = b.a, b1 = b.b;
            if (slots[a.a] != slots[b0]) { b0 = b.b; b1 = b.a; }
            if ((uv[a.a] - uv[b0]).sqrMagnitude < 1e-14f && (uv[a.b] - uv[b1]).sqrMagnitude < 1e-14f)
                parent[Root(parent, b.face)] = Root(parent, a.face);
        }
        var compact = new Dictionary<int, int>(); var charts = new int[p.Length];
        for (int face = 0; face < parent.Length; ++face)
        {
            int root = Root(parent, face);
            if (!compact.TryGetValue(root, out int chart)) compact[root] = chart = compact.Count;
            for (int k = 0; k < 3; ++k) charts[ix[face * 3 + k]] = chart;
        }
        count = compact.Count;
        return charts;
    }

    static void Capture(object g, string path)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            var p = (Vector3[])Get(g, "positions"); var uv = (Vector2[])Get(g, "uv");
            var ix = (int[])Get(g, "indices"); var charts = (int[])Get(g, "charts");
            w.Write(p.Length); w.Write(ix.Length); w.Write((int)Get(g, "chartCount"));
            foreach (var v in p) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
            foreach (var v in uv) { w.Write(v.x); w.Write(v.y); }
            foreach (int v in ix) w.Write(v);
            foreach (int v in charts) w.Write(v);
        }
    }

    static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    public static async void Run()
    {
        try
        {
            string source = Environment.GetEnvironmentVariable("MESHLAB_UV_REFERENCE_FBX");
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) throw new FileNotFoundException("Set MESHLAB_UV_REFERENCE_FBX to a reference FBX.");
            string tag = Environment.GetEnvironmentVariable("MESHLAB_UV_REFERENCE_TAG") ?? "reference";
            if (tag.Length == 0 || tag.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException("Reference tag must contain letters, digits, hyphens or underscores.");
            string name = "uv-reference-" + tag;
            string asset = "Assets/" + name + ".fbx", destination = Path.Combine(Application.dataPath, name + ".fbx");
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../" + name)); Directory.CreateDirectory(directory);
            string hash = Hash(source);
            if (File.Exists(destination) && Hash(destination) != hash) throw new InvalidOperationException("Cached reference differs; use a new tag.");
            if (!File.Exists(destination))
                using (new AssetDatabase.AssetEditingScope())
                { FileUtil.CopyFileOrDirectory(source, destination); AssetDatabase.ImportAsset(asset); }
            var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
            importer.importNormals = ModelImporterNormals.Import; importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.isReadable = true; importer.SaveAndReimport();
            var filters = AssetDatabase.LoadAssetAtPath<GameObject>(asset).GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length != 1) throw new InvalidOperationException("Expected exactly one MeshFilter.");
            var mesh = filters[0].sharedMesh; var p = mesh.vertices; var ix = mesh.triangles;
            if (mesh.uv.Length != p.Length || mesh.bounds.size.magnitude <= 0) throw new InvalidOperationException("Reference must have UVs and nonzero bounds.");
            for (int i = 0; i < p.Length; ++i) p[i] = (p[i] - mesh.bounds.center) / mesh.bounds.size.magnitude;
            var reference = Activator.CreateInstance(Native.GetNestedType("Geometry", Flags), true);
            Set(reference, "positions", p); Set(reference, "indices", ix); Set(reference, "uv", mesh.uv); Set(reference, "normals", mesh.normals);
            Set(reference, "charts", Charts(p, mesh.uv, ix, out int count)); Set(reference, "chartCount", count);
            Capture(reference, Path.Combine(directory, "reference.bin"));
            File.WriteAllText(Path.Combine(directory, "source.sha256"), hash);
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "reference-normals.bin"))))
                foreach (var n in mesh.normals) { writer.Write(n.x); writer.Write(n.y); writer.Write(n.z); }
            Assembly.GetType("SashaRX.UnityMeshLab.UvtLog").GetField("_cachedLevel", Flags).SetValue(null, UvtLog.Level.Info);
            _ = UvtLog.EnabledCategories;
            var diagnostics = Assembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics");
            diagnostics.GetMethod("Log", Flags).Invoke(null, new object[] { reference, name + " artist", CancellationToken.None, false });
            var input = Activator.CreateInstance(Native.GetNestedType("IndexedMesh", Flags), true);
            Set(input, "positions", p); Set(input, "indices", ix);
            var settings = new RemeshSettings { textureResolution = 512, padding = 3, normalSmoothing = 0, hardEdges = RemeshHardEdges.Angle, normalCrease = 60 };
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonUtility.ToJson(settings, true));
            await Task.Run(() => {
                foreach (bool merge in new[] { false, true })
                {
                    settings.mergeCharts = merge; byte[] golden = null;
                    for (int repeat = 0; repeat < 3; ++repeat)
                    {
                        var g = Native.GetMethod("Unwrap", Flags).Invoke(null, new object[] { input, settings, CancellationToken.None });
                        var outputP = (Vector3[])Get(g, "positions"); var outputIx = (int[])Get(g, "indices");
                        if (outputIx.Length != ix.Length || ix.Where((v, i) => !p[v].Equals(outputP[outputIx[i]])).Any())
                            throw new InvalidOperationException("Source corners changed.");
                        var scan = diagnostics.GetMethod("Measure", Flags).Invoke(null, new object[] { g, CancellationToken.None, false, 2_000_000L, false });
                        if (!(bool)Get(scan, "complete") || (int)Get(scan, "pairs") != 0 || (int)Get(scan, "invalidFaces") != 0 ||
                            (int)Get(scan, "degenerateFaces") != 0 || (int)Get(scan, "outOfBoundsVertices") != 0)
                            throw new InvalidOperationException("Automatic atlas failed its complete scan.");
                        string path = Path.Combine(directory, merge ? "auto-merge.bin" : "auto-plain.bin"); Capture(g, path);
                        byte[] bytes = File.ReadAllBytes(path); if (golden == null) golden = bytes;
                        if (!golden.SequenceEqual(bytes)) throw new InvalidOperationException("Nondeterministic output buffers.");
                        Debug.Log($"REFERENCE_REPEAT {tag} merge={merge} repeat={repeat} sourceCorners=True byteIdentical=True");
                        if (repeat == 0) diagnostics.GetMethod("Log", Flags).Invoke(null, new object[] { g, name + " automatic merge=" + merge, CancellationToken.None, false });
                    }
                }
            });
            Debug.Log("REFERENCE_COMPLETE " + tag); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }
}
