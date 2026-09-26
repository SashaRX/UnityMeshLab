using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Right-sidebar previews for Remesh &amp; Bake: an orbitable 3D view of any pipeline
    /// stage, the result UV layout and the baked maps. Owns every Unity object it creates.
    /// </summary>
    internal sealed class RemeshPreview : IDisposable
    {
        internal enum View { Mesh, Uv, Maps }
        internal enum Stage { Source, Remesh, Simplified, Result }
        internal enum Channel { BaseColor, Normal, MetallicSmoothness, Occlusion, Emission }

        internal sealed class Data
        {
            public readonly Mesh[] meshes = new Mesh[4]; // indexed by Stage
            public RemeshNative.Geometry geometry;
            public RemeshBaker.Maps maps;
            public Texture2D baseColor;
        }

        static readonly string[] ViewNames = { "3D", "UV", "Maps" };
        public Action RequestRepaint;
        View view;
        Stage stage = Stage.Result;
        Channel channel;
        bool wireframe = true, shaded = true, textured = true, vertexColors, uvTexture = true, uvTint = true;
        Vector2 orbit = new Vector2(-135, 20);
        float zoom = 1;
        PreviewRenderUtility utility;
        Material surface, wire, lines;
        readonly Dictionary<int, Mesh> wireCache = new Dictionary<int, Mesh>();
        RemeshBaker.Maps mapSource;
        readonly Texture2D[] mapTextures = new Texture2D[5];

        public void Show(Stage value) { stage = value; }

        public void Draw(Data data)
        {
            view = (View)GUILayout.Toolbar((int)view, ViewNames, EditorStyles.toolbarButton);
            EditorGUILayout.Space(2);
            switch (view) {
                case View.Mesh: DrawMesh(data); break;
                case View.Uv: DrawUv(data); break;
                default: DrawMaps(data); break;
            }
        }

        /// <summary>Drop cached per-mesh wireframes; call before destroying preview meshes.</summary>
        public void Invalidate()
        {
            foreach (var mesh in wireCache.Values) if (mesh) Object.DestroyImmediate(mesh);
            wireCache.Clear();
            foreach (var texture in mapTextures) if (texture) Object.DestroyImmediate(texture);
            Array.Clear(mapTextures, 0, mapTextures.Length);
            mapSource = null;
        }

        public void Dispose()
        {
            Invalidate();
            utility?.Cleanup(); utility = null;
            if (surface) Object.DestroyImmediate(surface);
            if (wire) Object.DestroyImmediate(wire);
            if (lines) Object.DestroyImmediate(lines);
        }

        // ── 3D ──

        void DrawMesh(Data data)
        {
            using (new EditorGUILayout.HorizontalScope()) {
                stage = (Stage)EditorGUILayout.EnumPopup(stage, GUILayout.Width(90));
                wireframe = GUILayout.Toggle(wireframe, "Wire", EditorStyles.miniButtonLeft);
                shaded = GUILayout.Toggle(shaded, "Shaded", EditorStyles.miniButtonMid);
                textured = GUILayout.Toggle(textured, "Texture", EditorStyles.miniButtonMid);
                vertexColors = GUILayout.Toggle(vertexColors, "Vertex color", EditorStyles.miniButtonRight);
            }
            var mesh = data.meshes[(int)stage];
            Rect rect = GUILayoutUtility.GetAspectRect(1);
            HandleOrbit(rect);
            EditorGUILayout.LabelField(mesh ? $"{mesh.vertexCount:N0} vertices · {Triangles(mesh):N0} triangles" : "Run this stage to preview it.",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Drag to orbit, scroll to zoom.", EditorStyles.centeredGreyMiniLabel);
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));
            if (!mesh || !EnsureResources()) return;

            var bounds = mesh.bounds;
            float radius = Mathf.Max(bounds.extents.magnitude, 1e-4f);
            float distance = radius / Mathf.Sin(15 * Mathf.Deg2Rad) * zoom;
            var rotation = Quaternion.Euler(orbit.y, orbit.x, 0);
            utility.BeginPreview(rect, GUIStyle.none);
            var camera = utility.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 1);
            camera.transform.rotation = rotation;
            camera.transform.position = bounds.center - rotation * Vector3.forward * distance;
            camera.nearClipPlane = Mathf.Max(distance - radius * 2, distance * 0.001f);
            camera.farClipPlane = distance + radius * 2;
            if (shaded) {
                bool useTexture = textured && stage == Stage.Result && data.baseColor;
                surface.SetTexture("_MainTex", useTexture ? data.baseColor : null);
                surface.SetFloat("_UseTexture", useTexture ? 1 : 0);
                surface.SetFloat("_UseVertexColor", vertexColors && mesh.HasVertexAttribute(VertexAttribute.Color) ? 1 : 0);
                for (int sub = 0; sub < mesh.subMeshCount; ++sub) utility.DrawMesh(mesh, Matrix4x4.identity, surface, sub);
            }
            if (wireframe) {
                var edges = Wire(mesh);
                wire.SetColor("_Color", shaded ? new Color(0.05f, 0.05f, 0.05f, 1) : new Color(0.4f, 0.85f, 1f, 1));
                if (edges) utility.DrawMesh(edges, Matrix4x4.identity, wire, 0);
            }
            utility.Render();
            GUI.DrawTexture(rect, utility.EndPreview(), ScaleMode.StretchToFill, false);
        }

        void HandleOrbit(Rect rect)
        {
            int id = GUIUtility.GetControlID("RemeshPreviewOrbit".GetHashCode(), FocusType.Passive, rect);
            var e = Event.current;
            switch (e.GetTypeForControl(id)) {
                case EventType.MouseDown:
                    if (rect.Contains(e.mousePosition)) { GUIUtility.hotControl = id; e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    orbit += e.delta * 0.5f;
                    orbit.y = Mathf.Clamp(orbit.y, -89, 89);
                    e.Use(); RequestRepaint?.Invoke();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
                case EventType.ScrollWheel:
                    if (!rect.Contains(e.mousePosition)) break;
                    zoom = Mathf.Clamp(zoom * (1 + e.delta.y * 0.05f), 0.05f, 20);
                    e.Use(); RequestRepaint?.Invoke();
                    break;
            }
        }

        bool EnsureResources()
        {
            if (utility == null) utility = new PreviewRenderUtility { cameraFieldOfView = 30 };
            if (!surface || !wire) {
                var shader = Shader.Find("Hidden/MeshLab/RemeshPreview");
                if (!shader) return false;
                surface = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                wire = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                wire.SetFloat("_Lit", 0); wire.SetFloat("_DepthOffset", -1);
            }
            return true;
        }

        // One line per unique triangle edge, cached per mesh instance.
        Mesh Wire(Mesh mesh)
        {
            int key = mesh.GetInstanceID();
            if (wireCache.TryGetValue(key, out var cached) && cached) return cached;
            var tris = mesh.triangles;
            if (tris.Length / 3 > 1000000) return null;
            var seen = new HashSet<long>();
            var indices = new List<int>(tris.Length);
            long stride = mesh.vertexCount;
            for (int i = 0; i < tris.Length; i += 3)
                for (int k = 0; k < 3; ++k) {
                    int a = tris[i + k], b = tris[i + (k + 1) % 3];
                    if (seen.Add(Math.Min(a, b) * stride + Math.Max(a, b))) { indices.Add(a); indices.Add(b); }
                }
            var edges = new Mesh { name = mesh.name + "_Wire", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            edges.vertices = mesh.vertices;
            edges.SetIndices(indices, MeshTopology.Lines, 0);
            wireCache[key] = edges;
            return edges;
        }

        static long Triangles(Mesh mesh)
        {
            long total = 0;
            for (int sub = 0; sub < mesh.subMeshCount; ++sub) total += mesh.GetIndexCount(sub) / 3;
            return total;
        }

        // ── UV ──

        void DrawUv(Data data)
        {
            using (new EditorGUILayout.HorizontalScope()) {
                uvTexture = GUILayout.Toggle(uvTexture, "Base color", EditorStyles.miniButtonLeft);
                uvTint = GUILayout.Toggle(uvTint, "Tint islands", EditorStyles.miniButtonRight);
            }
            var g = data.geometry;
            Rect rect = GUILayoutUtility.GetAspectRect(1);
            if (g == null) EditorGUILayout.LabelField("Run Normals & UV to preview the layout.", EditorStyles.miniLabel);
            else {
                string coverage = data.maps != null ? $" · {100.0 * data.maps.covered / ((double)data.maps.size * data.maps.size):0.#}% texels used" : "";
                EditorGUILayout.LabelField($"{g.chartCount:N0} islands · {g.indices.Length / 3:N0} triangles{coverage}", EditorStyles.miniLabel);
            }
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));
            if (uvTexture && data.baseColor) GUI.DrawTexture(rect, data.baseColor, ScaleMode.StretchToFill, false);
            if (g == null || !EnsureLines()) return;
            GUI.BeginClip(rect);
            GL.PushMatrix();
            lines.SetPass(0);
            float w = rect.width, h = rect.height;
            int triangles = g.indices.Length / 3;
            if (uvTint && triangles <= 500000) {
                GL.Begin(GL.TRIANGLES);
                for (int f = 0; f < triangles; ++f) {
                    GL.Color(ChartColor(g.charts[g.indices[f * 3]]));
                    for (int k = 0; k < 3; ++k) {
                        var uv = g.uv[g.indices[f * 3 + k]];
                        GL.Vertex3(uv.x * w, (1 - uv.y) * h, 0);
                    }
                }
                GL.End();
            }
            if (triangles <= 500000) {
                GL.Begin(GL.LINES);
                GL.Color(new Color(1, 1, 1, 0.55f));
                for (int f = 0; f < triangles; ++f)
                    for (int k = 0; k < 3; ++k) {
                        var a = g.uv[g.indices[f * 3 + k]]; var b = g.uv[g.indices[f * 3 + (k + 1) % 3]];
                        GL.Vertex3(a.x * w, (1 - a.y) * h, 0); GL.Vertex3(b.x * w, (1 - b.y) * h, 0);
                    }
                GL.End();
            }
            GL.PopMatrix();
            GUI.EndClip();
        }

        bool EnsureLines()
        {
            if (lines) return true;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (!shader) return false;
            lines = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            lines.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            lines.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            lines.SetInt("_Cull", (int)CullMode.Off);
            lines.SetInt("_ZWrite", 0);
            lines.SetInt("_ZTest", (int)CompareFunction.Always);
            return true;
        }

        static Color ChartColor(int chart)
        {
            uint h = unchecked((uint)chart * 2654435761u);
            var color = Color.HSVToRGB((h & 0xffff) / 65535f, 0.55f, 0.95f);
            color.a = 0.35f;
            return color;
        }

        // ── Maps ──

        void DrawMaps(Data data)
        {
            channel = (Channel)EditorGUILayout.EnumPopup("Map", channel);
            Rect rect = GUILayoutUtility.GetAspectRect(1);
            if (data.maps == null) { EditorGUILayout.LabelField("Bake to preview the maps.", EditorStyles.miniLabel); return; }
            EditorGUILayout.LabelField($"{data.maps.size} × {data.maps.size}", EditorStyles.miniLabel);
            if (Event.current.type != EventType.Repaint) return;
            var texture = MapTexture(data.maps, channel);
            if (texture) EditorGUI.DrawPreviewTexture(rect, texture);
        }

        Texture2D MapTexture(RemeshBaker.Maps maps, Channel which)
        {
            if (!ReferenceEquals(maps, mapSource)) {
                foreach (var t in mapTextures) if (t) Object.DestroyImmediate(t);
                Array.Clear(mapTextures, 0, mapTextures.Length);
                mapSource = maps;
            }
            int index = (int)which;
            if (mapTextures[index]) return mapTextures[index];
            var texture = new Texture2D(maps.size, maps.size, TextureFormat.RGBA32, false, which != Channel.BaseColor) {
                hideFlags = HideFlags.HideAndDontSave };
            switch (which) {
                case Channel.BaseColor: texture.SetPixels32(maps.color); break;
                case Channel.Normal: texture.SetPixels32(maps.normal); break;
                case Channel.MetallicSmoothness: texture.SetPixels32(maps.metal); break;
                case Channel.Occlusion: texture.SetPixels32(maps.ao); break;
                default:
                    var pixels = new Color32[maps.emission.Length];
                    for (int i = 0; i < pixels.Length; ++i) {
                        var e = maps.emission[i];
                        pixels[i] = new Color(Mathf.Clamp01(e.r), Mathf.Clamp01(e.g), Mathf.Clamp01(e.b), 1).gamma;
                    }
                    texture.SetPixels32(pixels);
                    break;
            }
            texture.Apply();
            return mapTextures[index] = texture;
        }
    }
}
