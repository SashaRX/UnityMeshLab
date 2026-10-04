// Developer harness: copy into a scratch Unity project's Assets/Editor, reference
// this package, then -batchmode -executeMethod XatlasDefaultsBenchmark.Run.
// Optional MESHLAB_XATLAS_PROFILES: JSON { "profiles": [{ "name":..., "settings":... }] }.
// MESHLAB_XATLAS_PHASE: raw (native only), repaired, or pipeline. Reuses one fixed
// simplified Bust input, plus four generated fixtures. Never writes EditorPrefs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;

public static class XatlasDefaultsBenchmark
{
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Assembly EditorAssembly = typeof(RemeshSettings).Assembly;
    static readonly Type Native = EditorAssembly.GetType("SashaRX.UnityMeshLab.RemeshNative");
    static readonly Type InputType = Native.GetNestedType("IndexedMesh", Members);
    static readonly Type Quality = EditorAssembly.GetType("SashaRX.UnityMeshLab.UvChartQuality");
    static readonly Type Diagnostics = EditorAssembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics");
    static readonly Type Repair = EditorAssembly.GetType("SashaRX.UnityMeshLab.UvChartRepair");
    static readonly MethodInfo Raw = Native.GetMethod("UnwrapWithOptions", Members);
    static readonly MethodInfo Unwrap = Native.GetMethod("Unwrap", Members);
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    [Serializable] public sealed class Profile { public string name; public RemeshSettings settings; }
    [Serializable] public sealed class Profiles { public Profile[] profiles; }
    sealed class Fixture { public string name; public Vector3[] positions; public int[] indices; }
    static object Get(object value, string field) => value.GetType().GetField(field, Members).GetValue(value);
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Members).SetValue(value, data);
    static object Measure(object geometry) => Quality.GetMethod("Measure", Members).Invoke(null, new[] { geometry, (object)CancellationToken.None });
    static object Scan(object geometry) => Diagnostics.GetMethod("Measure", Members).Invoke(null, new[] { geometry, (object)CancellationToken.None, false, 2_000_000L, false });
    static string Csv(object value) => "\"" + Convert.ToString(value, Invariant).Replace("\"", "\"\"") + "\"";
    static object Input(Fixture f) {
        var input = Activator.CreateInstance(InputType, true);
        Set(input, "positions", f.positions); Set(input, "indices", f.indices);
        return input;
    }
    static RemeshSettings Settings(float cost = 2, float straight = 6, float normal = 2, float round = .01f, float seam = 4, int iterations = 1)
        => new RemeshSettings { chartMaxCost = cost, chartStraightness = straight, chartNormalDeviation = normal,
            chartRoundness = round, chartNormalSeam = seam, chartIterations = iterations,
            textureResolution = 512, padding = 2, reduceUvFragmentation = false, mergeCharts = false };
    static Profile[] ChartProfiles() {
        var profiles = new List<Profile>(); var keys = new HashSet<string>();
        void Add(RemeshSettings s, string name = null) {
            string key = JsonUtility.ToJson(s); if (!keys.Add(key)) return;
            profiles.Add(new Profile { name = name ?? string.Format(Invariant, "c{0}_s{1}_n{2}_r{3}_se{4}_i{5}",
                s.chartMaxCost, s.chartStraightness, s.chartNormalDeviation, s.chartRoundness, s.chartNormalSeam, s.chartIterations), settings = s });
        }
        Add(Settings(), "upstream-default");
        foreach (float cost in new[] { 1f, 2, 3, 5, 8, 12 })
            foreach (int iterations in new[] { 1, 2, 4, 8, 16 }) Add(Settings(cost, iterations: iterations));
        foreach (float cost in new[] { 2f, 5, 8 }) {
            foreach (float straight in new[] { 0f, 2, 4, 10, 20 }) Add(Settings(cost, straight: straight));
            foreach (float normal in new[] { .5f, 1, 4, 8 }) Add(Settings(cost, normal: normal));
            foreach (float round in new[] { 0f, .1f, .5f, 1 }) Add(Settings(cost, round: round));
            foreach (float seam in new[] { 0f, 1, 20, 1000 }) Add(Settings(cost, seam: seam));
        }
        Add(Settings(10, 20, 7.1f, .73f, 251, 16), "user-chart-settings");
        return profiles.ToArray();
    }
    static float[] Options(RemeshSettings s) => new[] { s.maxChartArea, s.maxChartBoundary,
        s.chartNormalDeviation, s.chartRoundness, s.chartStraightness, s.chartNormalSeam, .5f,
        s.chartMaxCost, s.chartIterations, s.textureResolution, s.padding, 0, 1,
        s.packBlockAlign ? 1f : 0, s.packBruteForce ? 1f : 0, s.packRotate ? 1f : 0, s.packRotate ? 1f : 0 };
    static Fixture Load(string path) {
        using (var reader = new BinaryReader(File.OpenRead(path))) {
            var f = new Fixture { name = "bust", positions = new Vector3[reader.ReadInt32()], indices = new int[reader.ReadInt32()] };
            for (int v = 0; v < f.positions.Length; ++v) f.positions[v] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < f.indices.Length; ++i) f.indices[i] = reader.ReadInt32();
            return f;
        }
    }
    static void Save(Fixture f, string path) {
        using (var writer = new BinaryWriter(File.Create(path))) {
            writer.Write(f.positions.Length); writer.Write(f.indices.Length);
            foreach (var p in f.positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
            foreach (int i in f.indices) writer.Write(i);
        }
    }
    static async Task<Fixture> Bust(string path) {
        if (File.Exists(path)) return Load(path);
        var root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Bust.fbx"));
        var material = new Material(Shader.Find("Standard"));
        var type = EditorAssembly.GetType("SashaRX.UnityMeshLab.RemeshPipeline");
        var pipe = Activator.CreateInstance(type, true);
        try {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>()) {
                var materials = new Material[Math.Max(1, renderer.sharedMaterials.Length)];
                for (int i = 0; i < materials.Length; ++i) materials[i] = material;
                renderer.sharedMaterials = materials;
            }
            var s = JsonUtility.FromJson<RemeshSettings>(File.ReadAllText(Path.Combine(Application.dataPath, "../settings.json")));
            var stage = type.GetNestedType("Stage", Members);
            bool ok = await (Task<bool>)type.GetMethod("Run").Invoke(pipe, new[] { root, (object)s, Enum.ToObject(stage, 0), Enum.ToObject(stage, 1) });
            if (!ok) throw new InvalidOperationException("Bust preparation failed.");
            var mesh = (Mesh)type.GetProperty("SimplifiedMesh").GetValue(pipe);
            var f = new Fixture { name = "bust", positions = mesh.vertices, indices = mesh.triangles };
            Save(f, path); return f;
        }
        finally { ((IDisposable)pipe).Dispose(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(material); }
    }
    static Fixture Wave() {
        const int cells = 20, stride = cells + 1;
        var p = new Vector3[stride * stride]; var ix = new List<int>();
        for (int y = 0; y <= cells; ++y) for (int x = 0; x <= cells; ++x) {
            float u = x / (float)cells, v = y / (float)cells;
            p[y * stride + x] = new Vector3(u, .2f * Mathf.Sin(u * 2 * Mathf.PI) * Mathf.Sin(v * 2 * Mathf.PI), v);
        }
        for (int y = 0; y < cells; ++y) for (int x = 0; x < cells; ++x) {
            int a = y * stride + x; ix.AddRange(new[] { a, a + stride, a + 1, a + 1, a + stride, a + stride + 1 });
        }
        return new Fixture { name = "wave", positions = p, indices = ix.ToArray() };
    }
    static Fixture Torus() {
        const int rings = 32, segments = 12;
        var p = new Vector3[rings * segments]; var ix = new List<int>();
        for (int r = 0; r < rings; ++r) for (int s = 0; s < segments; ++s) {
            float a = r * 2 * Mathf.PI / rings, b = s * 2 * Mathf.PI / segments, radius = .7f + .2f * Mathf.Cos(b);
            p[r * segments + s] = new Vector3(radius * Mathf.Cos(a), .2f * Mathf.Sin(b), radius * Mathf.Sin(a));
            int v = r * segments + s, right = (r + 1) % rings * segments + s;
            int up = r * segments + (s + 1) % segments, diagonal = (r + 1) % rings * segments + (s + 1) % segments;
            ix.AddRange(new[] { v, up, right, right, up, diagonal });
        }
        return new Fixture { name = "torus", positions = p, indices = ix.ToArray() };
    }
    static Fixture Cylinder() {
        const int segments = 32, levels = 5;
        var p = new Vector3[levels * segments + 2]; var ix = new List<int>();
        for (int l = 0; l < levels; ++l) for (int s = 0; s < segments; ++s) {
            float a = s * 2 * Mathf.PI / segments;
            p[l * segments + s] = new Vector3(Mathf.Cos(a), l / (float)(levels - 1), Mathf.Sin(a));
            int next = l * segments + (s + 1) % segments;
            if (l + 1 < levels) ix.AddRange(new[] { l * segments + s, l * segments + s + segments, next, next, l * segments + s + segments, next + segments });
        }
        int bottom = p.Length - 2, top = p.Length - 1; p[top] = Vector3.up;
        for (int s = 0; s < segments; ++s) {
            int next = (s + 1) % segments; ix.AddRange(new[] { bottom, s, next, top, (levels - 1) * segments + next, (levels - 1) * segments + s });
        }
        return new Fixture { name = "cylinder", positions = p, indices = ix.ToArray() };
    }
    static Fixture Cube() {
        var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try { var mesh = root.GetComponent<MeshFilter>().sharedMesh; return new Fixture { name = "cube", positions = mesh.vertices, indices = mesh.triangles }; }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
    static double UvArea(object g) {
        var uv = (Vector2[])Get(g, "uv"); var ix = (int[])Get(g, "indices"); double area = 0;
        for (int i = 0; i < ix.Length; i += 3) { var a = uv[ix[i + 1]] - uv[ix[i]]; var b = uv[ix[i + 2]] - uv[ix[i]]; area += Math.Abs((double)a.x * b.y - (double)a.y * b.x) * .5; }
        return area;
    }
    static void Sweep(Fixture[] fixtures, Profile[] profiles, string phase, string path, int repeats) {
        using (var writer = new StreamWriter(path)) {
            writer.AutoFlush = true;
            writer.WriteLine("case,profile,phase,repeat,faces,charts,small,mean,worst,overlap,complete,valid,uvArea,unwrapMs,checkMs,error,settings");
            foreach (var f in fixtures) foreach (var profile in profiles) {
                profile.settings.Validate(); var input = Input(f);
                for (int repeat = 0; repeat < repeats; ++repeat) {
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(path), "stop-after-native"))) return;
                    var watch = Stopwatch.StartNew(); object g = null; string error = "";
                    try {
                        g = phase == "pipeline" ? Unwrap.Invoke(null, new[] { input, (object)profile.settings, CancellationToken.None })
                            : Raw.Invoke(null, new[] { input, (object)Options(profile.settings), CancellationToken.None });
                        if (phase == "repaired") g = Repair.GetMethod("Apply", Members).Invoke(null, new[] { g, (object)profile.settings, CancellationToken.None });
                    }
                    catch (Exception e) { error = (e.InnerException ?? e).Message; }
                    double elapsed = watch.Elapsed.TotalMilliseconds; watch.Restart();
                    object q = g == null ? null : Measure(g), scan = g == null ? null : Scan(g);
                    writer.WriteLine(string.Join(",", new[] { Csv(f.name), Csv(profile.name), Csv(phase), Csv(repeat), Csv(f.indices.Length / 3),
                        Csv(q == null ? -1 : Get(q, "charts")), Csv(q == null ? -1 : Get(q, "smallCharts")),
                        Csv(q == null ? 0 : Get(q, "meanStretch")), Csv(q == null ? 0 : Get(q, "maxStretch")),
                        Csv(scan == null ? -1 : Get(scan, "pairs")), Csv(scan != null && (bool)Get(scan, "complete")),
                        Csv(q != null && (bool)Get(q, "valid")), Csv(g == null ? 0 : UvArea(g)), Csv(elapsed), Csv(watch.Elapsed.TotalMilliseconds), Csv(error), Csv(JsonUtility.ToJson(profile.settings)) }));
                    // Keep one measured sample of slow variants rather than spending
                    // minutes on redundant repeats. The measured cost stays in CSV.
                    if (elapsed > 2000) break;
                }
                UnityEngine.Debug.Log("SWEEP " + phase + " " + f.name + " " + profile.name);
            }
        }
    }
    public static async void Run() {
        try {
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../xatlas-benchmark")); Directory.CreateDirectory(directory);
            string phase = Environment.GetEnvironmentVariable("MESHLAB_XATLAS_PHASE") ?? "raw";
            string config = Environment.GetEnvironmentVariable("MESHLAB_XATLAS_PROFILES");
            var profiles = string.IsNullOrEmpty(config) ? ChartProfiles() : JsonUtility.FromJson<Profiles>(File.ReadAllText(config)).profiles;
            var bust = await Bust(Path.Combine(directory, "bust-input.bin"));
            // Suppress verbose diagnostic formatting during timing without changing
            // the user's saved logger preferences. Explicit quality scans still run.
            var levelCache = EditorAssembly.GetType("SashaRX.UnityMeshLab.UvtLog").GetField("_cachedLevel", Members);
            levelCache.SetValue(null, Enum.ToObject(levelCache.FieldType.GetGenericArguments()[0], 0));
            var fixtures = new[] { bust, Wave(), Cylinder(), Torus(), Cube() };
            string filter = Environment.GetEnvironmentVariable("MESHLAB_XATLAS_CASES");
            if (!string.IsNullOrEmpty(filter)) fixtures = Array.FindAll(fixtures, f => Array.IndexOf(filter.Split(','), f.name) >= 0);
            await Task.Run(() => Sweep(fixtures, profiles, phase, Path.Combine(directory, "results-" + phase + ".csv"), phase == "raw" ? 1 : 3));
            UnityEngine.Debug.Log("SWEEP_COMPLETE " + phase + " profiles=" + profiles.Length);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { UnityEngine.Debug.LogException(e); EditorApplication.Exit(1); }
    }
}
