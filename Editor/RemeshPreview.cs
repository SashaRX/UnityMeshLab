using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Right-sidebar previews for Remesh &amp; Bake: the stage picker and surface toggles
    /// for the shared 3D canvas (Fill3D / Overlay3D), the result UV layout and the baked
    /// maps. Owns every Unity object it creates.
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
            // Ray travel of the bake projection (source diagonal × projection distance);
            // the Cage toggle draws the result mesh inflated by ±this along the cage
            // normals, i.e. the exact shells the projection rays start and end on.
            public float cageDistance;
        }

        static readonly string[] ViewNames = { "3D", "UV", "Maps" };
        public Action RequestRepaint;
        View view;
        Stage stage = Stage.Result;
        Channel channel;
        bool wireframe = true, shaded = true, textured = true, bumpMap = true, vertexColors, cageView, uvTexture = true, uvTint = true;
        Material surface, wire, lines;
        Mesh cageOuter, cageInner; int cageMeshId; float cageDistanceCached = -1;
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
            if (cageOuter) Object.DestroyImmediate(cageOuter);
            if (cageInner) Object.DestroyImmediate(cageInner);
            cageOuter = cageInner = null; cageMeshId = 0; cageDistanceCached = -1;
            foreach (var texture in mapTextures) if (texture) Object.DestroyImmediate(texture);
            Array.Clear(mapTextures, 0, mapTextures.Length);
            mapSource = null;
        }

        public void Dispose()
        {
            Invalidate();
            if (surface) Object.DestroyImmediate(surface);
            if (wire) Object.DestroyImmediate(wire);
            if (lines) Object.DestroyImmediate(lines);
        }

        // ── 3D ──
        // The stage mesh is shown in the hub's shared 3D canvas (UV | 3D switch): this
        // panel only picks the stage and the surface/overlay toggles; Fill3D and
        // Overlay3D feed the viewport through the tool's IUvTool3D implementation.

        void DrawMesh(Data data)
        {
            using (new EditorGUILayout.HorizontalScope()) {
                stage = (Stage)EditorGUILayout.EnumPopup(stage, GUILayout.Width(90));
                wireframe = GUILayout.Toggle(wireframe, "Wire", EditorStyles.miniButtonLeft);
                shaded = GUILayout.Toggle(shaded, "Shaded", EditorStyles.miniButtonMid);
                textured = GUILayout.Toggle(textured, "Texture", EditorStyles.miniButtonMid);
                bumpMap = GUILayout.Toggle(bumpMap, "Bump", EditorStyles.miniButtonMid);
                vertexColors = GUILayout.Toggle(vertexColors, "Vertex color", EditorStyles.miniButtonMid);
                cageView = GUILayout.Toggle(cageView, "Cage", EditorStyles.miniButtonRight);
            }
            var mesh = data.meshes[(int)stage];
            EditorGUILayout.LabelField(mesh ? $"{mesh.vertexCount:N0} vertices · {Triangles(mesh):N0} triangles" : "Run this stage to preview it.",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Shown in the canvas's 3D view (UV | 3D switch at its bottom).", EditorStyles.centeredGreyMiniLabel);
            if (GUI.changed) RequestRepaint?.Invoke();
        }

        /// <summary>The selected stage mesh for the shared 3D canvas, with the surface
        /// material this panel's toggles configure. False when no stage has run.</summary>
        public bool Fill3D(Data data, List<MeshViewport3D.Item> items)
        {
            var mesh = data.meshes[(int)stage];
            if (!mesh) return false;
            if (!EnsureResources()) return false;
            if (shaded) {
                bool useTexture = textured && stage == Stage.Result && data.baseColor;
                surface.SetTexture("_MainTex", useTexture ? data.baseColor : null);
                surface.SetFloat("_UseTexture", useTexture ? 1 : 0);
                // Only the result stage has tangents; its baked normal map is the same
                // data the saved material gets, so the preview shows the final shading.
                var bump = bumpMap && stage == Stage.Result && data.maps != null ? MapTexture(data.maps, Channel.Normal) : null;
                surface.SetTexture("_BumpMap", bump);
                surface.SetFloat("_UseBumpMap", bump ? 1 : 0);
                // Beauty maps already contain the lighting; shading them again would
                // double it, so the surface renders unlit exactly like the saved material.
                surface.SetFloat("_Lit", stage == Stage.Result && data.maps != null && data.maps.beauty ? 0 : 1);
                surface.SetFloat("_UseVertexColor", vertexColors && mesh.HasVertexAttribute(VertexAttribute.Color) ? 1 : 0);
                surface.SetColor("_Color", Color.white);
                var materials = new Material[mesh.subMeshCount];
                for (int sub = 0; sub < materials.Length; ++sub) materials[sub] = surface;
                items.Add(new MeshViewport3D.Item(mesh, Matrix4x4.identity, materials));
            }
            else items.Add(new MeshViewport3D.Item(mesh, Matrix4x4.identity, new Material[0]));
            return true;
        }

        /// <summary>Wire and cage overlays for the stage mesh in the shared 3D canvas.</summary>
        public void Overlay3D(Data data, MeshViewport3D view)
        {
            var mesh = data.meshes[(int)stage];
            if (!mesh || !EnsureResources()) return;
            if (wireframe) view.DrawWire(mesh, Matrix4x4.identity, shaded ? new Color(0.05f, 0.05f, 0.05f, 1) : new Color(0.4f, 0.85f, 1f, 1));
            if (cageView && stage == Stage.Result && data.geometry != null && data.cageDistance > 0)
                DrawCage(view, mesh, data.geometry, data.cageDistance);
        }

        bool EnsureResources()
        {
            if (surface && wire) return true;
            var shader = Shader.Find("Hidden/MeshLab/RemeshPreview");
            if (!shader) return false;
            surface = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            wire = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            wire.SetFloat("_Lit", 0); wire.SetFloat("_DepthOffset", -1);
            return true;
        }

        // The projection cage, drawn as its two limit shells: the result mesh inflated
        // by ±cageDistance along the same welded cage normals the bake rays follow
        // (outer = ray origins, inner = ray ends). Rebuilt when the mesh or the
        // distance changes; turn the surface Wire off to read a shell alone.
        void DrawCage(MeshViewport3D view, Mesh mesh, RemeshNative.Geometry geometry, float distance)
        {
            int id = mesh.GetInstanceID();
            if (id != cageMeshId || Mathf.Abs(distance - cageDistanceCached) > 1e-6f) {
                if (cageOuter) Object.DestroyImmediate(cageOuter);
                if (cageInner) Object.DestroyImmediate(cageInner);
                var cageNormals = RemeshBaker.BuildCageNormals(geometry, null);
                var indices = MeshViewport3D.EdgeIndices(mesh);
                if (indices != null) {
                    var folds = new TriangleBvh(geometry.positions, geometry.indices);
                    cageOuter = CageShell(mesh, geometry, cageNormals, distance, "Outer", folds);
                    cageInner = CageShell(mesh, geometry, cageNormals, -distance, "Inner", folds);
                }
                else cageOuter = cageInner = null;
                cageMeshId = id; cageDistanceCached = distance;
            }
            wire.SetColor("_Color", new Color(1f, 0.55f, 0.15f, 0.9f));
            if (cageOuter) view.DrawMesh(cageOuter, Matrix4x4.identity, wire, 0);
            wire.SetColor("_Color", new Color(0.35f, 0.6f, 1f, 0.45f));
            if (cageInner) view.DrawMesh(cageInner, Matrix4x4.identity, wire, 0);
        }

        static Mesh CageShell(Mesh mesh, RemeshNative.Geometry geometry, Vector3[] cageNormals, float distance, string suffix, TriangleBvh folds)
        {
            var vertices = new Vector3[geometry.positions.Length];
            for (int i = 0; i < vertices.Length; ++i) {
                Vector3 dir = distance >= 0 ? cageNormals[i] : -cageNormals[i];
                vertices[i] = geometry.positions[i] + dir * FoldLimitedOffset(folds, geometry.positions[i], dir, Mathf.Abs(distance));
            }
            var shell = new Mesh { name = mesh.name + "_Cage" + suffix, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            shell.vertices = vertices;
            shell.SetIndices(MeshViewport3D.EdgeIndices(mesh), MeshTopology.Lines, 0);
            return shell;
        }

        // A uniform offset folds through itself wherever the surface is tighter than the
        // ray travel (wheel wells, fender arches) — the shell then crosses to the far
        // side and reads as noise. The offset stops short of the fold instead: the
        // segment is cast against the surface itself and keeps 85% of the unobstructed
        // reach, floored at a quarter of the full distance so the limit stays visible.
        static float FoldLimitedOffset(TriangleBvh folds, Vector3 p, Vector3 dir, float distance)
        {
            float eps = distance * 0.01f;
            var hit = folds.Raycast(p + dir * eps, dir, distance);
            if (hit.triangleIndex < 0) return distance;
            return Mathf.Max(distance * 0.25f, (hit.t + eps) * 0.85f);
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
