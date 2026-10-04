using System;
using System.IO;
using System.Collections;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using SashaRX.UnityMeshLab;

public static class SimplifyTopologyReplay
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object Get(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    static void Save(object geometry, string path)
    {
        if (geometry == null) return;
        var positions = (Vector3[])Get(geometry, "positions");
        var indices = (int[])Get(geometry, "indices");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(positions.Length); writer.Write(indices.Length);
        foreach (var p in positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
        foreach (int index in indices) writer.Write(index);
        Debug.Log("TOPOLOGY_CAPTURE " + Path.GetFileName(path) + " " + positions.Length + " vertices " + indices.Length / 3 + " faces");
    }
    public static async void Run()
    {
        GameObject root = null;
        IDisposable cleanup = null;
        Material material = null;
        try {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../simplify-topology"));
            Directory.CreateDirectory(dir);
            root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Bust.fbx"));
            material = new Material(Shader.Find("Standard"));
            foreach (var renderer in root.GetComponentsInChildren<Renderer>()) {
                var materials = new Material[Math.Max(1, renderer.sharedMaterials.Length)];
                for (int i = 0; i < materials.Length; i++) materials[i] = material;
                renderer.sharedMaterials = materials;
            }
            var settings = new RemeshSettings { minPartSize = .043f, minRodVoxels = .8f,
                targetTriangles = 1600, maximumError = .0041f, normalSmoothing = 4.8f,
                normalCrease = 133, textureResolution = 512, padding = 3, mergeCharts = false };
            var type = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshPipeline");
            var pipe = Activator.CreateInstance(type, true); cleanup = (IDisposable)pipe;
            var stage = type.GetNestedType("Stage", Flags);
            var run = type.GetMethod("Run", Flags);
            bool ok = await (Task<bool>)run.Invoke(pipe, new object[] { root, settings, Enum.ToObject(stage, 0), Enum.ToObject(stage, 1) });
            if (!ok) throw new Exception("Pipeline failed: " + type.GetProperty("Status").GetValue(pipe));
            int id = 0;
            foreach (object node in (IEnumerable)Get(pipe, "nodes")) {
                Save(Get(node, "source"), Path.Combine(dir, "source-" + id + ".bin"));
                Save(Get(node, "voxelRaw"), Path.Combine(dir, "raw-" + id + ".bin"));
                Save(Get(node, "voxel"), Path.Combine(dir, "trim-" + id + ".bin"));
                Save(Get(node, "simplified"), Path.Combine(dir, "simplify-" + id + ".bin"));
                id++;
            }
            var native = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshNative");
            var topology = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.RemeshTopology");
            var measure = topology.GetMethod("Inspect", Flags);
            foreach (object node in (IEnumerable)Get(pipe, "nodes")) {
                var input = Get(node, "voxel");
                foreach (var profile in new[] { RemeshRegularize.None, RemeshRegularize.Light, RemeshRegularize.Strong }) {
                    settings.regularize = profile;
                    byte[] golden = null;
                    for (int repeat = 0; repeat < 3; repeat++) {
                        var args = new object[] { input, settings, CancellationToken.None, 0f };
                        var simplified = await Task.Run(() => native.GetMethod("Simplify", Flags).Invoke(null, args));
                        string path = Path.Combine(dir, "profile-" + profile + ".bin"); Save(simplified, path);
                        var current = File.ReadAllBytes(path);
                        if (golden == null) golden = current;
                        if (!golden.SequenceEqual(current)) throw new Exception("Nondeterministic simplification " + profile);
                        var snap = measure.Invoke(null, new object[] { Get(simplified, "positions"), Get(simplified, "indices"), CancellationToken.None });
                        if (!(bool)snap.GetType().GetProperty("Valid", Flags).GetValue(snap)) throw new Exception("Invalid topology " + profile);
                        Debug.Log("TOPOLOGY_REPEAT " + profile + " repeat=" + repeat + " achieved=" + args[3] + " " + snap.GetType().GetProperty("Description", Flags).GetValue(snap));
                        if (profile == RemeshRegularize.None && repeat == 0) {
                            foreach (bool merge in new[] { false, true }) {
                                settings.mergeCharts = merge;
                                var uv = await Task.Run(() => native.GetMethod("Unwrap", Flags).Invoke(null, new object[] { simplified, settings, CancellationToken.None }));
                                var sourceP = (Vector3[])Get(simplified, "positions"); var sourceIx = (int[])Get(simplified, "indices");
                                var p = (Vector3[])Get(uv, "positions"); var ix = (int[])Get(uv, "indices");
                                if (sourceIx.Length != ix.Length) throw new Exception("Unwrap changed triangle count");
                                for (int i = 0; i < ix.Length; i++) if (!sourceP[sourceIx[i]].Equals(p[ix[i]])) throw new Exception("Unwrap changed source corner");
                                var diag = typeof(RemeshSettings).Assembly.GetType("SashaRX.UnityMeshLab.UvAtlasDiagnostics").GetMethod("Measure", Flags).Invoke(null, new object[] { uv, CancellationToken.None, false, 2000000L, false });
                                if (!(bool)Get(diag, "complete") || (int)Get(diag, "pairs") != 0 || (int)Get(diag, "invalidFaces") != 0 || (int)Get(diag, "degenerateFaces") != 0) throw new Exception("Invalid unwrap");
                                Debug.Log("TOPOLOGY_UNWRAP merge=" + merge + " charts=" + Get(uv, "chartCount") + " corners preserved; complete overlap scan clean");
                            }
                        }
                    }
                }
            }
            cleanup.Dispose(); cleanup = null;
            UnityEngine.Object.DestroyImmediate(root); root = null;
            UnityEngine.Object.DestroyImmediate(material); material = null;
            EditorApplication.Exit(0);
        } catch (Exception error) {
            Debug.LogException(error); cleanup?.Dispose();
            if (root) UnityEngine.Object.DestroyImmediate(root);
            if (material) UnityEngine.Object.DestroyImmediate(material);
            EditorApplication.Exit(1);
        }
    }
}
