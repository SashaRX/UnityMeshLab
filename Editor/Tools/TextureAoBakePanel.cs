using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    // Standalone texture mode in the existing AO tab. Scene meshes/materials are
    // never replaced: its previews are owned transient meshes in the shared canvas.
    internal sealed class TextureAoBakePanel
    {
        sealed class Result
        {
            internal string name;
            internal RemeshNative.Geometry geometry;
            internal Matrix4x4 placement;
            internal RemeshBaker.Maps maps;
            internal byte[] png;
            internal Mesh mesh;
            internal Texture2D texture;
            internal Material material;
            internal MeshEntry entry;
        }
        readonly RemeshSettings settings = new RemeshSettings { bakeSourceAO = true, textureResolution = 512,
            bakeSamples = 1, padding = 4, gpuProjection = false, vertexColorTint = false };
        readonly List<Result> results = new List<Result>();
        GameObject sourceRoot;
        int uvChannel, resultIndex, generation;
        bool lod0Only = true, running;
        string status = "Bake source AO into the target's existing UVs.";
        CancellationTokenSource cancellation;
        Action repaint;

        internal bool IsRunning => running;
        internal void Activate(Action requestRepaint) => repaint = requestRepaint;
        internal void Clear()
        {
            ++generation; cancellation?.Cancel();
            foreach (var r in results) Destroy(r);
            results.Clear(); resultIndex = 0;
            status = "Bake source AO into the target's existing UVs.";
        }
        internal void Deactivate() { Clear(); repaint = null; }
        static void Destroy(Result r)
        {
            if (r.mesh) Object.DestroyImmediate(r.mesh);
            if (r.texture) Object.DestroyImmediate(r.texture);
            if (r.material) Object.DestroyImmediate(r.material);
        }

        internal static void DrawSourceSettings(SourceAoSettings s)
        {
            s.samples = EditorGUILayout.IntPopup("AO rays", s.samples, new[] { "16", "32", "64", "128", "256", "512", "1024" }, new[] { 16, 32, 64, 128, 256, 512, 1024 });
            s.radius = EditorGUILayout.Slider(new GUIContent("AO radius / bounds", "Maximum ray distance as a fraction of the source bounds diagonal."), s.radius, .001f, 2f);
            s.intensity = EditorGUILayout.Slider("AO intensity", s.intensity, .25f, 4f);
            s.bias = EditorGUILayout.Slider(new GUIContent("AO bias / bounds", "Offset from the source surface to avoid self intersections."), s.bias, .000001f, .005f);
            s.normalMap = EditorGUILayout.Toggle(new GUIContent("Use source normal map", "Orient the AO hemisphere using the material's tangent-space normal map, texture transform and normal strength."), s.normalMap);
            s.cosineWeighted = EditorGUILayout.Toggle("Cosine weighted", s.cosineWeighted);
            s.binaryHit = EditorGUILayout.Toggle(new GUIContent("Binary hit", "Any hit fully occludes. Off: occlusion decreases with distance."), s.binaryHit);
            s.backfaceCulling = EditorGUILayout.Toggle("AO backface culling", s.backfaceCulling);
            s.groundPlane = EditorGUILayout.Toggle("AO ground plane", s.groundPlane);
            if (s.groundPlane) s.groundOffset = EditorGUILayout.Slider("Ground offset / bounds", s.groundOffset, 0, .2f);
        }

        internal void Draw(List<MeshEntry> targets, GameObject defaultSource)
        {
            using (new EditorGUI.DisabledScope(running)) {
                sourceRoot = (GameObject)EditorGUILayout.ObjectField(new GUIContent("AO source root", "High-detail source with its original materials. Empty: the selected model root."), sourceRoot, typeof(GameObject), true);
                lod0Only = EditorGUILayout.Toggle("Source LOD0 only", lod0Only);
                uvChannel = EditorGUILayout.IntSlider("Target UV channel", uvChannel, 0, 7);
                settings.textureResolution = EditorGUILayout.IntPopup("Texture size", settings.textureResolution,
                    new[] { "256", "512", "1024", "2048" }, new[] { 256, 512, 1024, 2048 });
                settings.padding = EditorGUILayout.IntSlider("Padding", settings.padding, 1, 32);
                settings.bakeSamples = EditorGUILayout.IntPopup("Samples per texel", settings.bakeSamples, new[] { "1", "4", "9", "16" }, new[] { 1, 4, 9, 16 });
                settings.projectionDistance = EditorGUILayout.Slider("Projection / bounds", settings.projectionDistance, .001f, .2f);
                DrawSourceSettings(settings.sourceAO);
                settings.multiplySourceAO = EditorGUILayout.Toggle("Multiply source AO map", settings.multiplySourceAO);
                EditorGUILayout.LabelField($"Targets: {targets.Count} included mesh(es)", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(targets.Count == 0 || !(sourceRoot ? sourceRoot : defaultSource)))
                    if (GUILayout.Button("Bake Texture AO", GUILayout.Height(28))) {
                        var inputs = targets.ToArray(); var root = sourceRoot ? sourceRoot : defaultSource;
                        _ = Bake(inputs, root);
                    }
            }
            if (running && GUILayout.Button("Cancel Texture AO")) cancellation?.Cancel();
            EditorGUILayout.HelpBox(status, MessageType.None);
            if (results.Count == 0) return;
            resultIndex = EditorGUILayout.Popup("Result", Mathf.Clamp(resultIndex, 0, results.Count - 1), results.Select(r => r.name).ToArray());
            var selected = results[resultIndex];
            if (selected.texture) GUILayout.Label(selected.texture, GUILayout.Width(180), GUILayout.Height(180));
            using (new EditorGUI.DisabledScope(running))
                if (GUILayout.Button("Save selected AO PNG")) _ = Save(selected);
        }

        async Task Bake(MeshEntry[] entries, GameObject root)
        {
            if (running) return;
            Clear(); running = true;
            var cts = new CancellationTokenSource(); cancellation = cts;
            var token = cts.Token; int version = generation;
            var completed = new List<Result>();
            try {
                // Leave OnGUI before reading meshes/materials; all rasterization,
                // BVH traversal, normal-map sampling and PNG encoding run on workers.
                status = "Capturing source…"; repaint?.Invoke();
                await Task.Yield(); token.ThrowIfCancellationRequested();
                var options = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(settings));
                options.Validate();
                Matrix4x4 worldToSource = root.transform.worldToLocalMatrix, placement = root.transform.localToWorldMatrix;
                var source = RemeshSource.Capture(worldToSource, RemeshSource.CollectRenderers(root, lod0Only), aoOnly: true, aoReadbackSettings: options);
                await source.TextureReadbacks; token.ThrowIfCancellationRequested();
                for (int i = 0; i < entries.Length; ++i) {
                    token.ThrowIfCancellationRequested();
                    var entry = entries[i]; var mesh = entry.originalMesh;
                    if (!mesh || !entry.renderer) continue;
                    status = $"Texture AO: {i + 1}/{entries.Length} · {entry.renderer.name}"; repaint?.Invoke();
                    await Task.Yield(); token.ThrowIfCancellationRequested();
                    var geometry = CaptureTarget(mesh, uvChannel);
                    Matrix4x4 toSource = worldToSource * entry.renderer.localToWorldMatrix;
                    await Task.Run(() => TransformTarget(geometry, toSource), token);
                    var item = new Result { name = entry.renderer.name, geometry = geometry, placement = placement };
                    item.maps = await Task.Run(() => RemeshBaker.Bake(source, geometry, geometry.tangents, options, token), token);
                    item.png = await Task.Run(() => ImageConversion.EncodeArrayToPNG(item.maps.ao, GraphicsFormat.R8G8B8A8_UNorm,
                        (uint)item.maps.size, (uint)item.maps.size), token);
                    completed.Add(item);
                }
                token.ThrowIfCancellationRequested();
                if (completed.Count == 0) throw new InvalidOperationException("No target mesh with usable UVs.");
                if (version != generation) return;
                foreach (var item in completed) {
                    token.ThrowIfCancellationRequested();
                    item.mesh = new Mesh { name = item.name + "_AO_Preview", hideFlags = HideFlags.HideAndDontSave,
                        indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    item.mesh.vertices = item.geometry.positions; item.mesh.normals = item.geometry.normals;
                    item.mesh.uv = item.geometry.uv; item.mesh.triangles = item.geometry.indices; item.mesh.RecalculateBounds();
                    item.texture = TextureAssets.FromPixels(item.maps.ao, item.maps.size, item.maps.size, true);
                    var shader = Shader.Find("Unlit/Texture");
                    if (shader) item.material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, mainTexture = item.texture };
                    item.entry = new MeshEntry { originalMesh = item.mesh, previewTexture = item.texture };
                }
                results.AddRange(completed);
                completed.Clear();
                int misses = results.Sum(r => r.maps.misses);
                status = $"Texture AO: {results.Count} map(s), {misses} projection misses. Preview uses target UV{uvChannel}.";
                UvtLog.Info("[Texture AO] " + status);
            }
            catch (OperationCanceledException) { if (version == generation) status = "Texture AO cancelled."; }
            catch (Exception ex) { if (version == generation) { status = ex.Message; UvtLog.Error("[Texture AO] " + ex.Message); } }
            finally {
                foreach (var item in completed) Destroy(item);
                if (ReferenceEquals(cancellation, cts)) cancellation = null;
                cts.Dispose(); running = false; repaint?.Invoke();
            }
        }

        internal static RemeshNative.Geometry CaptureTarget(Mesh mesh, int channel)
        {
            var readable = MeshAccess.Readable(mesh, out bool copy);
            try {
                var uv = UvTopology.ReadUv(readable, channel);
                if (uv == null || uv.Length != readable.vertexCount) throw new InvalidOperationException($"'{mesh.name}' has no complete UV{channel} stream.");
                return new RemeshNative.Geometry { positions = readable.vertices, indices = readable.triangles,
                    normals = readable.normals, tangents = readable.tangents, uv = uv };
            }
            finally { if (copy) Object.DestroyImmediate(readable); }
        }

        internal static void TransformTarget(RemeshNative.Geometry geometry, Matrix4x4 toSource)
        {
            var positions = geometry.positions; var indices = geometry.indices;
            bool missingNormals = geometry.normals.Length != positions.Length;
            if (missingNormals) geometry.normals = new Vector3[positions.Length];
            if (geometry.tangents.Length != positions.Length) geometry.tangents = new Vector4[positions.Length];
            var normals = geometry.normals; var tangents = geometry.tangents;
            Matrix4x4 normalMatrix = toSource.inverse.transpose;
            float determinant = toSource.determinant;
            if (float.IsNaN(determinant) || float.IsInfinity(determinant) || Mathf.Abs(determinant) < 1e-12f)
                throw new InvalidOperationException("Target transform has zero or invalid scale.");
            bool mirrored = determinant < 0;
            for (int i = 0; i < positions.Length; ++i) {
                positions[i] = toSource.MultiplyPoint3x4(positions[i]);
                normals[i] = MeshGeometry.UnitDirection(normalMatrix.MultiplyVector(normals[i]));
                Vector3 t = toSource.MultiplyVector(tangents[i]);
                tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w * (mirrored ? -1 : 1));
            }
            if (mirrored)
                for (int i = 0; i < indices.Length; i += 3) { int b = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = b; }
            if (missingNormals) RemeshNative.GenerateSplitNormals(geometry, RemeshNormalWeighting.FaceArea);
            RemeshNative.OrthogonalizeTangents(geometry);
        }

        async Task Save(Result result)
        {
            string path = EditorUtility.SaveFilePanelInProject("Save texture AO", result.name + "_AO", "png", "Save linear AO texture.");
            if (string.IsNullOrEmpty(path)) return;
            running = true;
            try {
                string absolute = Path.GetFullPath(path); byte[] png = result.png;
                await Task.Run(() => File.WriteAllBytes(absolute, png), CancellationToken.None);
                AssetDatabase.ImportAsset(path);
                TextureAssets.Configure(path, TextureAssets.Kind.Linear, result.maps.size);
                status = "Saved linear AO: " + path;
            }
            catch (Exception ex) { status = ex.Message; UvtLog.Error("[Texture AO] Save failed: " + ex.Message); }
            finally { running = false; repaint?.Invoke(); }
        }

        internal bool GetUvContent(List<MeshEntry> entries)
        {
            if (results.Count == 0) return false;
            entries.Add(results[Mathf.Clamp(resultIndex, 0, results.Count - 1)].entry);
            return true;
        }
        internal bool Get3DContent(List<MeshViewport3D.Item> items)
        {
            if (results.Count == 0) return false;
            var r = results[Mathf.Clamp(resultIndex, 0, results.Count - 1)];
            items.Add(new MeshViewport3D.Item(r.mesh, r.placement, r.material ? Enumerable.Repeat(r.material, r.mesh.subMeshCount).ToArray() : null));
            return true;
        }
    }
}
