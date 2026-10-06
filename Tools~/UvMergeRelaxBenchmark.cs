// Copy with XatlasDefaultsBenchmark.cs into a scratch project's Assets/Editor.
// Requires its cached xatlas-benchmark/bust-input.bin. No EditorPrefs writes.
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

public static class UvMergeRelaxBenchmark
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly Assembly Assembly = typeof(RemeshSettings).Assembly;
    static readonly Type Native = Assembly.GetType("SashaRX.UnityMeshLab.RemeshNative");
    static readonly Type Merge = Assembly.GetType("SashaRX.UnityMeshLab.UvChartMerge");
    static readonly Type Relax = Assembly.GetType("SashaRX.UnityMeshLab.UvChartRelax");
    static readonly Type Quality = Assembly.GetType("SashaRX.UnityMeshLab.UvChartQuality");
    static readonly Type Diagnostics = Assembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics");
    static object Get(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    static void Set(object value, string name, object data) => value.GetType().GetField(name, Flags).SetValue(value, data);
    static object Measure(object g) => Quality.GetMethod("Measure", Flags).Invoke(null, new[] { g, (object)CancellationToken.None });
    static object Scan(object g) => Diagnostics.GetMethod("Measure", Flags).Invoke(null, new[] { g, (object)CancellationToken.None, false, 2_000_000L, false });
    static object Copy(object source)
    {
        var result = Activator.CreateInstance(source.GetType(), true);
        foreach (var field in source.GetType().GetFields(Flags))
        {
            if (field.IsStatic) continue;
            var value = field.GetValue(source);
            field.SetValue(result, value is Array array ? array.Clone() : value);
        }
        return result;
    }
    static string Csv(object value) => "\"" + Convert.ToString(value, CultureInfo.InvariantCulture).Replace("\"", "\"\"") + "\"";
    static void Capture(object g, string path)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            var p = (Vector3[])Get(g, "positions"); var uv = (Vector2[])Get(g, "uv");
            var ix = (int[])Get(g, "indices"); var charts = (int[])Get(g, "charts");
            writer.Write(p.Length); writer.Write(ix.Length); writer.Write((int)Get(g, "chartCount"));
            foreach (var v in p) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
            foreach (var v in uv) { writer.Write(v.x); writer.Write(v.y); }
            foreach (int v in ix) writer.Write(v);
            foreach (int v in charts) writer.Write(v);
        }
    }
    static void Sweep(object[] fixtures, string directory)
    {
        string stage = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_STAGE") ?? "merged";
        string onlyProfile = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_PROFILE");
        bool arap = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_SOLVER") == "arap";
        int resolution = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_RESOLUTION") == "2048" ? 2048 : 512;
        int repeats = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_REPEAT") == "3" ? 3 : 1;
        int[] iterationCounts = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_FINAL") == "1" ? new[] { 0, 50 } : new[] { 0, 5, 15, 30, 50, 100 };
        var settings = new[] {
            new RemeshSettings { textureResolution = 512, padding = 2, mergeCharts = false },
            new RemeshSettings { textureResolution = 512, padding = 2, mergeCharts = false,
                chartMaxCost = 10, chartNormalDeviation = 7.1f, chartRoundness = .73f,
                chartStraightness = 20, chartNormalSeam = 251, chartIterations = 16,
                packBruteForce = true, packBlockAlign = true }
        };
        using (var writer = new StreamWriter(Path.Combine(directory, "relax.csv")))
        {
            writer.AutoFlush = true;
            writer.WriteLine("case,profile,stage,resolution,repeat,iterations,charts,small,accepted,preMean,preWorst,relaxedMean,relaxedWorst,mean,worst,valid,overlap,complete,mergeMs,relaxMs,packMs,error");
            for (int profile = 0; profile < settings.Length; ++profile)
                foreach (var fixture in fixtures)
                {
                    string name = (string)Get(fixture, "name"), preset = profile == 0 ? "recommended" : "user";
                    if (!string.IsNullOrEmpty(onlyProfile) && onlyProfile != preset) continue;
                    settings[profile].textureResolution = resolution; settings[profile].padding = resolution == 512 ? 2 : 8;
                    var input = Activator.CreateInstance(Native.GetNestedType("IndexedMesh", Flags), true);
                    Set(input, "positions", Get(fixture, "positions")); Set(input, "indices", Get(fixture, "indices"));
                    var baseline = Native.GetMethod("Unwrap", Flags).Invoke(null, new[] { input, (object)settings[profile], CancellationToken.None });
                    var core = Copy(baseline); var mergedIds = new HashSet<int>(); var watch = Stopwatch.StartNew();
                    if (stage == "before")
                    {
                        var all = new HashSet<int>(); for (int i = 0; i < (int)Get(core, "chartCount"); ++i) all.Add(i);
                        Relax.GetMethod("Apply", Flags).Invoke(null, new[] { core, (object)all, 50, CancellationToken.None, (object)arap });
                    }
                    Merge.GetMethod("MergeCharts", Flags).Invoke(null, new[] { core, (object)settings[profile], CancellationToken.None, mergedIds });
                    double mergeMs = watch.Elapsed.TotalMilliseconds;
                    Capture(core, Path.Combine(directory, name + "-" + preset + "-core.bin"));
                    if (stage == "all" || stage == "before") for (int i = 0; i < (int)Get(core, "chartCount"); ++i) mergedIds.Add(i);
                    foreach (int iterations in iterationCounts)
                    for (int repeat = 0; repeat < repeats; ++repeat)
                    {
                        var g = Copy(core); var pre = Measure(g); watch.Restart();
                        int accepted = iterations == 0 ? 0 : (int)Relax.GetMethod("Apply", Flags).Invoke(null, new[] { g, (object)mergedIds, iterations, CancellationToken.None, (object)arap });
                        double relaxMs = watch.Elapsed.TotalMilliseconds;
                        var relaxed = Measure(g);
                        Capture(g, Path.Combine(directory, name + "-" + preset + "-relax" + iterations + "-unpacked.bin"));
                        watch.Restart(); string error = "";
                        try { if (!(bool)Merge.GetMethod("Repack", Flags).Invoke(null, new[] { g, (object)settings[profile], CancellationToken.None })) error = "pack failed"; }
                        catch (Exception e) { error = (e.InnerException ?? e).Message; }
                        double packMs = watch.Elapsed.TotalMilliseconds; var q = Measure(g); var scan = Scan(g);
                        Capture(g, Path.Combine(directory, name + "-" + preset + "-relax" + iterations + ".bin"));
                        writer.WriteLine(string.Join(",", new[] { Csv(name), Csv(preset), Csv(stage), Csv(resolution), Csv(repeat), Csv(iterations), Csv(Get(q, "charts")), Csv(Get(q, "smallCharts")), Csv(accepted),
                            Csv(Get(pre, "meanStretch")), Csv(Get(pre, "maxStretch")), Csv(Get(relaxed, "meanStretch")), Csv(Get(relaxed, "maxStretch")),
                            Csv(Get(q, "meanStretch")), Csv(Get(q, "maxStretch")), Csv(Get(q, "valid")), Csv(Get(scan, "pairs")), Csv(Get(scan, "complete")),
                            Csv(mergeMs), Csv(relaxMs), Csv(packMs), Csv(error) }));
                        UnityEngine.Debug.Log("RELAX " + name + " " + preset + " iterations=" + iterations + " accepted=" + accepted);
                    }
                }
        }
    }
    public static async void Run()
    {
        try
        {
            var benchmark = typeof(UvMergeRelaxBenchmark).Assembly.GetType("XatlasDefaultsBenchmark");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../"));
            var bust = benchmark.GetMethod("Load", Flags).Invoke(null, new object[] { Path.Combine(root, "xatlas-benchmark/bust-input.bin") });
            var fixtures = new[] { bust, benchmark.GetMethod("Wave", Flags).Invoke(null, null),
                benchmark.GetMethod("Cylinder", Flags).Invoke(null, null), benchmark.GetMethod("Torus", Flags).Invoke(null, null), benchmark.GetMethod("Cube", Flags).Invoke(null, null) };
            string filter = Environment.GetEnvironmentVariable("MESHLAB_XATLAS_CASES");
            if (!string.IsNullOrEmpty(filter)) fixtures = Array.FindAll(fixtures, f => Array.IndexOf(filter.Split(','), (string)Get(f, "name")) >= 0);
            var level = Assembly.GetType("SashaRX.UnityMeshLab.UvtLog").GetField("_cachedLevel", Flags);
            level.SetValue(null, Enum.ToObject(level.FieldType.GetGenericArguments()[0], 0));
            string solver = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_SOLVER") == "arap" ? "arap" : "conformal";
            string stage = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_STAGE") ?? "merged";
            if (stage != "merged" && stage != "all" && stage != "before") throw new ArgumentException("Unknown relax stage");
            string resolution = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_RESOLUTION") == "2048" ? "2048" : "512";
            string directory = Path.Combine(root, "merge-relax-" + solver + "-" + stage + "-" + resolution); Directory.CreateDirectory(directory);
            await Task.Run(() => Sweep(fixtures, directory));
            UnityEngine.Debug.Log("RELAX_COMPLETE"); EditorApplication.Exit(0);
        }
        catch (Exception e) { UnityEngine.Debug.LogException(e); EditorApplication.Exit(1); }
    }

    // Verify the actual production entry point, all final atlas gates and every
    // 3D source corner, with byte-exact repeats of UV/index/chart output buffers.
    public static async void Production()
    {
        try
        {
            var benchmark = typeof(UvMergeRelaxBenchmark).Assembly.GetType("XatlasDefaultsBenchmark");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../"));
            var bust = benchmark.GetMethod("Load", Flags).Invoke(null, new object[] { Path.Combine(root, "xatlas-benchmark/bust-input.bin") });
            var fixtures = new[] { bust, benchmark.GetMethod("Wave", Flags).Invoke(null, null),
                benchmark.GetMethod("Cylinder", Flags).Invoke(null, null), benchmark.GetMethod("Torus", Flags).Invoke(null, null), benchmark.GetMethod("Cube", Flags).Invoke(null, null) };
            int resolution = Environment.GetEnvironmentVariable("MESHLAB_UV_RELAX_RESOLUTION") == "2048" ? 2048 : 512;
            var settings = new[] {
                new RemeshSettings { textureResolution = resolution, padding = resolution == 512 ? 2 : 8, mergeCharts = true },
                new RemeshSettings { textureResolution = resolution, padding = resolution == 512 ? 2 : 8, mergeCharts = true,
                    chartMaxCost = 10, chartNormalDeviation = 7.1f, chartRoundness = .73f, chartStraightness = 20,
                    chartNormalSeam = 251, chartIterations = 16, packBruteForce = true, packBlockAlign = true }
            };
            // Cache logger settings on the main thread; never write EditorPrefs.
            Assembly.GetType("SashaRX.UnityMeshLab.UvtLog").GetField("_cachedLevel", Flags).SetValue(null, UvtLog.Level.Info);
            _ = UvtLog.EnabledCategories;
            string directory = Path.Combine(root, "merge-relax-production-" + resolution); Directory.CreateDirectory(directory);
            await Task.Run(() => {
                using (var writer = new StreamWriter(Path.Combine(directory, "production.csv")))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine("case,profile,resolution,repeat,charts,small,mean,worst,valid,overlap,complete,invalid,degenerate,outOfBounds,preserveCorners,unwrapMs");
                    for (int profile = 0; profile < settings.Length; ++profile)
                    {
                        if (resolution == 2048 && profile == 1) continue;
                        foreach (var fixture in fixtures)
                        {
                            string name = (string)Get(fixture, "name"), preset = profile == 0 ? "recommended" : "user";
                            int repeats = resolution == 512 && name == "bust" ? 10 : 3;
                            byte[] golden = null;
                            for (int repeat = 0; repeat < repeats; ++repeat)
                            {
                                var input = Activator.CreateInstance(Native.GetNestedType("IndexedMesh", Flags), true);
                                var p = (Vector3[])Get(fixture, "positions"); var ix = (int[])Get(fixture, "indices");
                                Set(input, "positions", p); Set(input, "indices", ix);
                                var watch = Stopwatch.StartNew();
                                var g = Native.GetMethod("Unwrap", Flags).Invoke(null, new[] { input, (object)settings[profile], CancellationToken.None });
                                double ms = watch.Elapsed.TotalMilliseconds;
                                var q = Measure(g); var scan = Scan(g);
                                var outP = (Vector3[])Get(g, "positions"); var outIx = (int[])Get(g, "indices");
                                bool preserve = ix.Length == outIx.Length;
                                for (int i = 0; i < ix.Length && preserve; ++i) preserve = p[ix[i]].Equals(outP[outIx[i]]);
                                string path = Path.Combine(directory, name + "-" + preset + ".bin"); Capture(g, path);
                                var bytes = File.ReadAllBytes(path);
                                if (golden == null) golden = bytes;
                                if (bytes.Length != golden.Length) throw new InvalidOperationException("Non-deterministic output length");
                                for (int i = 0; i < bytes.Length; ++i) if (bytes[i] != golden[i]) throw new InvalidOperationException("Non-deterministic output buffers");
                                if (!preserve || !(bool)Get(q, "valid") || !(bool)Get(scan, "complete") || (int)Get(scan, "pairs") != 0 ||
                                    (int)Get(scan, "invalidFaces") != 0 || (int)Get(scan, "degenerateFaces") != 0 || (int)Get(scan, "outOfBoundsVertices") != 0)
                                    throw new InvalidOperationException("Production atlas validation failed");
                                writer.WriteLine(string.Join(",", new[] { Csv(name), Csv(preset), Csv(resolution), Csv(repeat), Csv(Get(q, "charts")), Csv(Get(q, "smallCharts")),
                                    Csv(Get(q, "meanStretch")), Csv(Get(q, "maxStretch")), Csv(Get(q, "valid")), Csv(Get(scan, "pairs")), Csv(Get(scan, "complete")),
                                    Csv(Get(scan, "invalidFaces")), Csv(Get(scan, "degenerateFaces")), Csv(Get(scan, "outOfBoundsVertices")), Csv(preserve), Csv(ms) }));
                                UnityEngine.Debug.Log("PRODUCTION " + name + " " + preset + " repeat=" + repeat + " byte-identical and source corners preserved");
                            }
                        }
                    }
                }
            });
            UnityEngine.Debug.Log("PRODUCTION_COMPLETE"); EditorApplication.Exit(0);
        }
        catch (Exception e) { UnityEngine.Debug.LogException(e); EditorApplication.Exit(1); }
    }
}
