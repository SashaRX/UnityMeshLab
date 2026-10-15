// Copy into a scratch project's Assets/Editor. Run -batchmode -force-d3d11
// -executeMethod ViewportPerformanceReplay.Run -viewportCsv <absolute output.csv>.
// Reference either the baseline or candidate package in that project's manifest.
// This measures warmed CPU repaint submission, not isolated GPU execution time.
using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using SashaRX.UnityMeshLab;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;
using Object = UnityEngine.Object;

public sealed class ViewportPerformanceReplay : EditorWindow
{
    Mesh mesh;
    Vector2[] uv;
    int[] triangles;
    TriangleStatus[] status;
    MeshViewport3D view;
    UvCanvasView canvas;
    RenderTexture target;
    Mesh pointCloud, edgeSource;
    Vector3[] diagnosticPoints, edgePairs;
    Action<Mesh, Matrix4x4, Color, float> drawPointCloud;
    UvToolContext context;
    MeshEntry entry;
    readonly List<MeshViewport3D.Item> items = new List<MeshViewport3D.Item>();
    readonly List<double> samples = new List<double>();
    readonly List<long> allocations = new List<long>();
    readonly List<string> rows = new List<string>();
    readonly FieldInfo orbit = typeof(MeshViewport3D).GetField("orbit", BindingFlags.Instance | BindingFlags.NonPublic);
    int phase, frame;
    double warmUntil;
    double deadline;
    string output;

    public static void Run()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Use a disposable batch-mode project.");
        var window = CreateInstance<ViewportPerformanceReplay>();
        window.position = new Rect(20, 20, 1024, 768); window.Show(); window.Initialize();
    }

    void Initialize()
    {
        try {
            output = Argument("-viewportCsv");
            context = new UvToolContext { PreviewUvChannel = 0 };
            const int side = 316;
            var vertices = new Vector3[(side + 1) * (side + 1)]; uv = new Vector2[vertices.Length];
            var normals = new Vector3[vertices.Length];
            triangles = new int[side * side * 6];
            for (int y = 0; y <= side; ++y)
                for (int x = 0; x <= side; ++x) {
                    int v = y * (side + 1) + x;
                    uv[v] = new Vector2((float)x / side, (float)y / side);
                    vertices[v] = new Vector3(uv[v].x * 2 - 1, uv[v].y * 2 - 1, 0);
                    normals[v] = Vector3.back;
                }
            for (int y = 0, t = 0; y < side; ++y)
                for (int x = 0; x < side; ++x) {
                    int v = y * (side + 1) + x;
                    triangles[t++] = v; triangles[t++] = v + side + 1; triangles[t++] = v + 1;
                    triangles[t++] = v + 1; triangles[t++] = v + side + 1; triangles[t++] = v + side + 2;
                }
            mesh = new Mesh { name = "Viewport benchmark 199712 triangles", indexFormat = IndexFormat.UInt32,
                vertices = vertices, normals = normals, uv = uv, triangles = triangles, hideFlags = HideFlags.HideAndDontSave };
            status = Enumerable.Repeat(TriangleStatus.Accepted, triangles.Length / 3).ToArray();
            view = new MeshViewport3D { Wireframe = true };
            view.RequestRepaint = Repaint;
            canvas = new UvCanvasView(); canvas.Init();
            canvas.RequestRepaint = Repaint;
            entry = new MeshEntry { originalMesh = mesh }; canvas.EntriesOverride = new List<MeshEntry> { entry };
            diagnosticPoints = new Vector3[10000]; edgePairs = new Vector3[20000];
            var pointIndices = new int[diagnosticPoints.Length]; var edgeIndices = new int[edgePairs.Length];
            for (int i = 0; i < diagnosticPoints.Length; ++i) {
                diagnosticPoints[i] = new Vector3((i % 100) / 50f - 1, (i / 100) / 50f - 1, -.02f); pointIndices[i] = i;
                edgePairs[i * 2] = diagnosticPoints[i]; edgePairs[i * 2 + 1] = diagnosticPoints[i] + Vector3.right * .01f;
                edgeIndices[i * 2] = i * 2; edgeIndices[i * 2 + 1] = i * 2 + 1;
            }
            pointCloud = new Mesh { vertices = diagnosticPoints }; pointCloud.SetIndices(pointIndices, MeshTopology.Points, 0);
            edgeSource = new Mesh { vertices = edgePairs }; edgeSource.SetIndices(edgeIndices, MeshTopology.Lines, 0);
            var pointMethod = typeof(MeshViewport3D).GetMethod("DrawPointCloud", BindingFlags.NonPublic | BindingFlags.Instance);
            if (pointMethod != null) drawPointCloud = (Action<Mesh, Matrix4x4, Color, float>)pointMethod.CreateDelegate(typeof(Action<Mesh, Matrix4x4, Color, float>), view);
            target = new RenderTexture(1024, 768, 0) { hideFlags = HideFlags.HideAndDontSave }; target.Create();
            rows.Add("mode,triangles,samples,medianCpuMs,p95CpuMs,medianAllocatedBytes,previewMeshBytes");
            warmUntil = EditorApplication.timeSinceStartup + 5;
            deadline = warmUntil + 90;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }

    void Tick() { Repaint(); EditorApplication.QueuePlayerLoopUpdate(); }

    void OnGUI()
    {
        if (!mesh || Event.current.type != EventType.Repaint) return;
        try {
            float angle = EditorApplication.timeSinceStartup >= warmUntil ? 15 + samples.Count * .35f : frame++ * .35f;
            items.Clear(); items.Add(new MeshViewport3D.Item(mesh, Matrix4x4.Translate(new Vector3(Mathf.Sin(angle * .02f) * .2f, 0, 0))));
            orbit.SetValue(view, new Vector2(-90 + angle, 25));
            long before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
            if (phase == 0 || phase == 2) DrawUv(angle);
            else view.Draw(new Rect(0, 0, 1024, 768), items, phase == 3 ? (Action<MeshViewport3D>)DrawDiagnostics : null);
            timer.Stop(); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (EditorApplication.timeSinceStartup < warmUntil) return;
            bool ready = !((phase == 1 || phase == 3) && !WireReady()) &&
                !((phase == 0 || phase == 2) && !BuffersReady(canvas, "geometry")) && !(phase == 3 && !BuffersReady(view, "pointSprites"));
            if (!ready) {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Viewport draw buffers did not become ready.");
                return;
            }
            samples.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(allocated);
            if (samples.Count < 96) return;
            samples.Sort(); allocations.Sort();
            long bytes = Resources.FindObjectsOfTypeAll<Mesh>().Where(m => phase == 0 || phase == 2 ? m.name == "MeshLab UV batch" : m.name == "Preview line ribbons" || m.name.EndsWith("_Wire") || m.name == "MeshLab point sprites")
                .Sum(m => Profiler.GetRuntimeMemorySizeLong(m));
            var modes = new[] { "UV pan+zoom", "3D orbit+translate", "Shell fill+opacity", "Diagnostics 10k points+10k lines" };
            rows.Add(string.Join(",", modes[phase], triangles.Length / 3, samples.Count,
                samples[samples.Count / 2].ToString("F4", CultureInfo.InvariantCulture),
                samples[(int)(samples.Count * .95)].ToString("F4", CultureInfo.InvariantCulture),
                allocations.All(n => n == 0) ? "" : allocations[allocations.Count / 2].ToString(CultureInfo.InvariantCulture), bytes));
            samples.Clear(); allocations.Clear();
            if (++phase < 4) { warmUntil = EditorApplication.timeSinceStartup + 5; deadline = warmUntil + 90; return; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllLines(output, rows);
            Debug.Log("Viewport replay saved to " + output);
            EditorApplication.update -= Tick; Close(); EditorApplication.Exit(0);
        }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.update -= Tick; EditorApplication.Exit(1); }
    }

    static bool BuffersReady(object owner, string field)
    {
        var cache = owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner);
        if (cache == null) return true; // baseline's immediate drawing path
        var entries = (IDictionary)cache.GetType().GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cache);
        if (entries.Count == 0) return false;
        foreach (DictionaryEntry entry in entries)
            if (!(Mesh)entry.Value.GetType().GetField("mesh", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(entry.Value)) return false;
        return true;
    }

    void DrawDiagnostics(MeshViewport3D viewport)
    {
        if (drawPointCloud != null) {
            drawPointCloud(pointCloud, Matrix4x4.identity, Color.yellow, 6);
            viewport.DrawLineMesh(edgeSource, Matrix4x4.identity, Color.green);
        }
        else { viewport.DrawPoints(diagnosticPoints, Matrix4x4.identity, Color.yellow, 6); viewport.DrawLines(edgePairs, Matrix4x4.identity, Color.green); }
    }

    bool WireReady()
    {
        var lines = typeof(MeshViewport3D).GetField("lineRibbons", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
        var entries = (IDictionary)lines.GetType().GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lines);
        if (entries.Count == 0) return false;
        foreach (DictionaryEntry entry in entries) {
            var ribbons = (Mesh)entry.Value.GetType().GetField("ribbons", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(entry.Value);
            if (!ribbons) return false;
        }
        return true;
    }

    void DrawUv(float angle)
    {
        var previous = RenderTexture.active; RenderTexture.active = target;
        GL.Clear(false, true, Color.black); GL.PushMatrix();
        try {
            GL.LoadPixelMatrix(0, 1024, 768, 0); canvas.GlMat.SetPass(0);
            float x = 90 + Mathf.Sin(angle * .05f) * 30, y = 50, size = 600 + Mathf.Cos(angle * .03f) * 80;
            if (phase == 2) {
                canvas.FillAlpha = .3f + .2f * Mathf.Sin(angle * .1f);
                canvas.GlFillSh(context, x, y, size, mesh, status.Length, uv.Length, entry, -1, -1);
            }
            else canvas.GlFillSt(x, y, size, uv, triangles, status.Length, uv.Length, status);
            canvas.GlWr(x, y, size, uv, triangles, status.Length, uv.Length);
        }
        finally { GL.PopMatrix(); RenderTexture.active = previous; }
        GUI.DrawTexture(new Rect(0, 0, 1024, 768), target);
    }

    static string Argument(string key)
    {
        var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + key);
        return args[index + 1];
    }

    void OnDisable()
    {
        EditorApplication.update -= Tick;
        view?.Dispose(); canvas?.Cleanup();
        if (mesh) Object.DestroyImmediate(mesh);
        if (pointCloud) Object.DestroyImmediate(pointCloud);
        if (edgeSource) Object.DestroyImmediate(edgeSource);
        if (target) { target.Release(); Object.DestroyImmediate(target); }
    }
}
