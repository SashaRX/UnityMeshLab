// Copy to an isolated Unity project's Assets/Editor. Set MESHLAB_UNWRAP_CAPTURE
// to the Info/RemeshDiag unwrap_*.bin and MESHLAB_REPLAY_OUTPUT to a local folder.
// No scene, source asset, or EditorPrefs writes. Captures contain private geometry.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;

public static class UvCapturedInputReplay
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly Assembly Assembly = typeof(RemeshSettings).Assembly;
    static readonly Type Native = Assembly.GetType("SashaRX.UnityMeshLab.RemeshNative");
    static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);

    static object ReadInput(string path, out RemeshSettings settings)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            if (reader.ReadInt32() != 0x554D4C42 || reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported unwrap capture.");
            settings = JsonUtility.FromJson<RemeshSettings>(reader.ReadString());
            var positions = new Vector3[reader.ReadInt32()]; var indices = new int[reader.ReadInt32()];
            for (int i = 0; i < positions.Length; ++i) positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < indices.Length; ++i) indices[i] = reader.ReadInt32();
            var input = Activator.CreateInstance(Native.GetNestedType("IndexedMesh", Flags), true);
            Set(input, "positions", positions); Set(input, "indices", indices);
            return input;
        }
    }

    static void Save(object geometry, string path)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            var positions = (Vector3[])Get(geometry, "positions"); var uv = (Vector2[])Get(geometry, "uv");
            var indices = (int[])Get(geometry, "indices"); var charts = (int[])Get(geometry, "charts");
            writer.Write(positions.Length); writer.Write(indices.Length); writer.Write((int)Get(geometry, "chartCount"));
            foreach (var p in positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
            foreach (var p in uv) { writer.Write(p.x); writer.Write(p.y); }
            foreach (int index in indices) writer.Write(index);
            foreach (int chart in charts) writer.Write(chart);
        }
    }

    static void Validate(object input, object geometry)
    {
        var sourcePositions = (Vector3[])Get(input, "positions"); var sourceIndices = (int[])Get(input, "indices");
        var positions = (Vector3[])Get(geometry, "positions"); var indices = (int[])Get(geometry, "indices");
        if (indices.Length != sourceIndices.Length || indices.Where((vertex, corner) =>
            !positions[vertex].Equals(sourcePositions[sourceIndices[corner]])).Any())
            throw new InvalidDataException("Source triangle corners changed.");
        var diagnostics = Assembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics");
        var scan = diagnostics.GetMethod("Measure", Flags).Invoke(null, new object[] { geometry, CancellationToken.None, false, 2_000_000L, false });
        if (!(bool)Get(scan, "complete") || (int)Get(scan, "pairs") != 0 || (int)Get(scan, "degenerateFaces") != 0 ||
            (int)Get(scan, "invalidFaces") != 0 || (int)Get(scan, "outOfBoundsVertices") != 0)
            throw new InvalidDataException("Atlas failed its complete intersection/bounds scan.");
    }

    public static async void Run()
    {
        try
        {
            string source = Environment.GetEnvironmentVariable("MESHLAB_UNWRAP_CAPTURE");
            string output = Environment.GetEnvironmentVariable("MESHLAB_REPLAY_OUTPUT");
            string tag = Environment.GetEnvironmentVariable("MESHLAB_REPLAY_TAG") ?? "replay";
            if (tag.Length == 0 || tag.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("Invalid output tag.");
            Directory.CreateDirectory(output);
            var input = ReadInput(source, out var settings);
            File.WriteAllText(Path.Combine(output, tag + "-settings.json"), JsonUtility.ToJson(settings, true));
            Assembly.GetType("SashaRX.UnityMeshLab.UvtLog").GetField("_cachedLevel", Flags).SetValue(null, UvtLog.Level.Info);
            _ = UvtLog.EnabledCategories;
            await Task.Run(() =>
            {
                foreach (bool merge in new[] { false, true })
                {
                    settings.mergeCharts = merge; byte[] golden = null;
                    for (int repeat = 0; repeat < 3; ++repeat)
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var geometry = Native.GetMethod("Unwrap", Flags).Invoke(null, new object[] { input, settings, CancellationToken.None });
                        double elapsed = watch.Elapsed.TotalMilliseconds;
                        Validate(input, geometry);
                        string path = Path.Combine(output, tag + (merge ? "-merge.bin" : "-plain.bin")); Save(geometry, path);
                        byte[] bytes = File.ReadAllBytes(path);
                        if (golden == null) golden = bytes;
                        if (!golden.SequenceEqual(bytes)) throw new InvalidDataException("Unwrap output is nondeterministic.");
                        Assembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics").GetMethod("Log", Flags).Invoke(null,
                            new object[] { geometry, tag + " merge=" + merge, CancellationToken.None, false });
                        Debug.Log($"CAPTURE_REPLAY {tag} merge={merge} repeat={repeat} elapsedMs={elapsed:F1} sourceCorners=True byteIdentical=True");
                    }
                }
            });
            Debug.Log("CAPTURE_REPLAY_COMPLETE " + tag); EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
