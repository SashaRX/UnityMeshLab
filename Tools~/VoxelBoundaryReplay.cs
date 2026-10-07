using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;

// Run in a dedicated test project. Geometry captures contain two Int32 counts,
// XYZ float positions, then triangle Int32 indices; no scene or asset is modified.
public static class VoxelBoundaryReplay
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Type Native = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshNative");
    static object Get(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    static object Read(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        return Read(reader);
    }
    static object Read(BinaryReader reader)
    {
        var p = new Vector3[reader.ReadInt32()]; var ix = new int[reader.ReadInt32()];
        for (int i = 0; i < p.Length; i++) p[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        for (int i = 0; i < ix.Length; i++) ix[i] = reader.ReadInt32();
        var type = Native.GetNestedType("IndexedMesh", Flags); var mesh = Activator.CreateInstance(type, true);
        type.GetField("positions", Flags).SetValue(mesh, p); type.GetField("indices", Flags).SetValue(mesh, ix);
        return mesh;
    }
    static void Save(object mesh, string name, string dir)
    {
        var p = (Vector3[])Get(mesh, "positions"); var ix = (int[])Get(mesh, "indices");
        using var writer = new BinaryWriter(File.Create(Path.Combine(dir, name + ".bin")));
        writer.Write(p.Length); writer.Write(ix.Length);
        foreach (var point in p) { writer.Write(point.x); writer.Write(point.y); writer.Write(point.z); }
        foreach (int index in ix) writer.Write(index);
        var topology = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshTopology");
        var info = topology.GetMethod("Inspect", Flags).Invoke(null, new object[] { p, ix, CancellationToken.None });
        Debug.Log("BOUNDARY_REPLAY " + name + " faces=" + ix.Length / 3 + " " + info.GetType().GetProperty("Description", Flags).GetValue(info));
    }
    static void SaveUv(object input, object geometry, string file)
    {
        var p = (Vector3[])Get(geometry, "positions"); var ix = (int[])Get(geometry, "indices");
        var uv = (Vector2[])Get(geometry, "uv"); var charts = (int[])Get(geometry, "charts");
        var originalP = (Vector3[])Get(input, "positions"); var originalIx = (int[])Get(input, "indices");
        if (ix.Length != originalIx.Length) throw new Exception("Unwrap changed face count");
        for (int i = 0; i < ix.Length; i++) if (!p[ix[i]].Equals(originalP[originalIx[i]])) throw new Exception("Unwrap changed a source corner");
        var diagType = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics");
        var diag = diagType.GetMethod("Measure", Flags).Invoke(null, new object[] { geometry, CancellationToken.None, false, 2000000L, false });
        if (!(bool)Get(diag, "complete") || (int)Get(diag, "pairs") != 0 || (int)Get(diag, "invalidFaces") != 0 ||
            (int)Get(diag, "degenerateFaces") != 0 || (int)Get(diag, "outOfBoundsVertices") != 0) throw new Exception("Invalid final UV atlas");
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write(p.Length); writer.Write(ix.Length); writer.Write((int)Get(geometry, "chartCount"));
        foreach (var point in p) { writer.Write(point.x); writer.Write(point.y); writer.Write(point.z); }
        foreach (var point in uv) { writer.Write(point.x); writer.Write(point.y); }
        foreach (int index in ix) writer.Write(index);
        foreach (int chart in charts) writer.Write(chart);
        Debug.Log("BOUNDARY_UV " + Path.GetFileName(file) + " charts=" + Get(geometry, "chartCount") + "; source corners preserved; full overlap scan clean");
    }
    public static async void UnwrapSaved()
    {
        try {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = Environment.GetEnvironmentVariable("MESHLAB_BOUNDARY_OUTPUT") ?? Path.Combine(root, "voxel-boundary");
            string settingsFile = Environment.GetEnvironmentVariable("MESHLAB_BOUNDARY_SETTINGS") ?? Path.Combine(root, "latest-user-log/latest-settings.json");
            var settings = JsonUtility.FromJson<RemeshSettings>(File.ReadAllText(settingsFile));
            await Task.Run(() => {
                foreach (string name in new[] { "ordinary", "refined" }) {
                    var input = Read(Path.Combine(output, name + ".bin"));
                    foreach (bool merge in new[] { false, true }) {
                        settings.mergeCharts = merge;
                        var geometry = Native.GetMethod("Unwrap", Flags).Invoke(null, new object[] { input, settings, CancellationToken.None });
                        SaveUv(input, geometry, Path.Combine(output, name + (merge ? "-merge-uv.bin" : "-plain-uv.bin")));
                    }
                }
            });
            EditorApplication.Exit(0);
        } catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    public static async void Run()
    {
        try {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string output = Environment.GetEnvironmentVariable("MESHLAB_BOUNDARY_OUTPUT") ?? Path.Combine(root, "voxel-boundary");
            string sourceFile = Environment.GetEnvironmentVariable("MESHLAB_BOUNDARY_SOURCE") ?? Path.Combine(root, "simplify-topology/source-0.bin");
            string settingsFile = Environment.GetEnvironmentVariable("MESHLAB_BOUNDARY_SETTINGS") ?? Path.Combine(root, "latest-user-log/latest-settings.json");
            Directory.CreateDirectory(output);
            object capturedSource = null, capturedRaw = null, capturedTrim = null;
            RemeshSettings settings;
            string capture = Environment.GetEnvironmentVariable("MESHLAB_REMESH_CAPTURE");
            if (!string.IsNullOrEmpty(capture)) {
                using var reader = new BinaryReader(File.OpenRead(capture));
                if (reader.ReadInt32() != 0x524D4C42 || reader.ReadInt32() != 1) throw new Exception("Unsupported remesh capture");
                settings = JsonUtility.FromJson<RemeshSettings>(reader.ReadString());
                capturedSource = Read(reader); capturedRaw = Read(reader); capturedTrim = Read(reader);
            } else settings = JsonUtility.FromJson<RemeshSettings>(File.ReadAllText(settingsFile));
            await Task.Run(() => {
                var source = capturedSource ?? Read(sourceFile); Save(source, "source", output);
                if (capturedRaw != null) { Save(capturedRaw, "captured-raw", output); Save(capturedTrim, "captured-trim", output); }
                var p = (Vector3[])Get(source, "positions"); var ix = (int[])Get(source, "indices");
                var low = p[0]; var high = low;
                foreach (var point in p) { low = Vector3.Min(low, point); high = Vector3.Max(high, point); }
                var e = high - low; float cell = Mathf.Max(e.x, Mathf.Max(e.y, e.z)) / settings.voxelResolution;
                uint flags = (settings.solve ? 1u : 0u) | (settings.shell ? 2u : 0u);
                var nativeRaw = Native.GetMethod("VoxelizeRaw", Flags).Invoke(null,
                    new object[] { p, ix, settings.voxelResolution, flags, CancellationToken.None });
                Save(nativeRaw, "native-before-guard", output);
                var raw = Native.GetMethod("Voxelize", Flags).Invoke(null, new object[] { p, ix, settings, CancellationToken.None });
                Save(raw, "raw", output);
                var trimType = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshTrim");
                var trimMethod = trimType.GetMethod("Trim", Flags, null, new[] { raw.GetType(), typeof(Vector3[]), typeof(int[]), typeof(float), typeof(CancellationToken) }, null);
                var trim = trimMethod.Invoke(null, new object[] { raw, p, ix, cell * 2, CancellationToken.None });
                File.WriteAllBytes(Path.Combine(output, "trim-classes.bin"), (byte[])Get(trim, "classes"));
                Debug.Log("BOUNDARY_REPLAY trim removed=" + Get(trim, "removed") + " flipped=" + Get(trim, "flipped"));
                var input = Get(trim, "mesh"); Save(input, "trim", output);
                var ordinaryArgs = new object[] { input, settings, CancellationToken.None, 0f };
                var ordinary = Native.GetMethod("Simplify", Flags).Invoke(null, ordinaryArgs); Save(ordinary, "ordinary", output);
                var refiner = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshSurfaceRefine");
                var refinedArgs = new object[] { input, p, ix, settings, CancellationToken.None, 0f };
                var refined = refiner.GetMethod("Simplify", Flags).Invoke(null, refinedArgs); Save(refined, "refined", output);
            });
            EditorApplication.Exit(0);
        } catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
