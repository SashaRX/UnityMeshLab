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
        internal enum View { Mesh, Maps }
        internal enum Stage { Source, Remesh, Simplified, Result }
        internal enum Channel { BaseColor, Normal, MetallicSmoothness, Occlusion, Emission }

        internal sealed class Data
        {
            public readonly Mesh[] meshes = new Mesh[4]; // indexed by Stage
            public RemeshNative.Geometry geometry;
            public RemeshBaker.Maps maps;
            public Texture2D baseColor;
            // Remesh stage: the untrimmed remesh coloured per trim class (null without a trim).
            public Mesh trimMask;
            // Ray travel of the bake projection (source diagonal × projection distance);
            // the Cage toggle draws the result mesh inflated by ±this along the cage
            // directions, i.e. the exact shells the projection rays start and end on.
            // Smoothing and fit mirror the bake settings; the source feeds the fit.
            public float cageDistance, cageSmoothing;
            public bool cageFit;
            public RemeshSource source;
        }

        static readonly string[] ViewNames = { "3D", "Maps" };
        public Action RequestRepaint;
        View view;
        Stage stage = Stage.Result;
        Channel channel;
        bool wireframe = true, shaded = true, textured = true, bumpMap = true, vertexColors, cageView, trimMaskView = true;
        Material surface, wire;
        Mesh cageOuter, cageInner; int cageMeshId; string cageKey;
        RemeshSource cageSourceRef; TriangleBvh cageSourceBvh;
        RemeshBaker.Maps mapSource;
        readonly Texture2D[] mapTextures = new Texture2D[5];

        public void Show(Stage value) { stage = value; }

        public void Draw(Data data)
        {
            view = (View)GUILayout.Toolbar((int)view, ViewNames, EditorStyles.toolbarButton);
            EditorGUILayout.Space(2);
            if (view == View.Mesh) DrawMesh(data); else DrawMaps(data);
        }

        /// <summary>Drop cached per-mesh wireframes; call before destroying preview meshes.</summary>
        public void Invalidate()
        {
            if (cageOuter) Object.DestroyImmediate(cageOuter);
            if (cageInner) Object.DestroyImmediate(cageInner);
            cageOuter = cageInner = null; cageMeshId = 0; cageKey = null;
            cageSourceRef = null; cageSourceBvh = null;
            foreach (var texture in mapTextures) if (texture) Object.DestroyImmediate(texture);
            Array.Clear(mapTextures, 0, mapTextures.Length);
            mapSource = null;
        }

        public void Dispose()
        {
            Invalidate();
            if (surface) Object.DestroyImmediate(surface);
            if (wire) Object.DestroyImmediate(wire);
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
                trimMaskView = GUILayout.Toggle(trimMaskView, new GUIContent("Trim", "Remesh stage: colour the untrimmed remesh by what Trim to source surface did with each face."), EditorStyles.miniButtonMid);
                cageView = GUILayout.Toggle(cageView, "Cage", EditorStyles.miniButtonRight);
            }
            var mesh = data.meshes[(int)stage];
            EditorGUILayout.LabelField(mesh ? $"{mesh.vertexCount:N0} vertices · {Triangles(mesh):N0} triangles" : "Run this stage to preview it.",
                EditorStyles.miniLabel);
            if (ShowTrimMask(data))
                EditorGUILayout.LabelField("Trim mask: green kept · red back of a sheet (opposite normal) · orange rim / no source within reach", EditorStyles.miniLabel);
            var g = data.geometry;
            if (g != null) {
                string coverage = data.maps != null ? $" · {100.0 * data.maps.covered / ((double)data.maps.size * data.maps.size):0.#}% texels used" : "";
                EditorGUILayout.LabelField($"Atlas: {g.chartCount:N0} islands{coverage}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField("The canvas shows the stage in 3D and the result's atlas in UV (switch at its bottom).", EditorStyles.centeredGreyMiniLabel);
            if (GUI.changed) RequestRepaint?.Invoke();
        }

        /// <summary>The selected stage mesh for the shared 3D canvas, with the surface
        /// material this panel's toggles configure. False when no stage has run.</summary>
        bool ShowTrimMask(Data data) => trimMaskView && stage == Stage.Remesh && data.trimMask;

        // The mesh the 3D view shows for the stage: the trim mask stands in for the
        // trimmed remesh while its toggle is on.
        Mesh DisplayMesh(Data data) => ShowTrimMask(data) ? data.trimMask : data.meshes[(int)stage];

        public bool Fill3D(Data data, List<MeshViewport3D.Item> items)
        {
            var mesh = DisplayMesh(data);
            if (!mesh) return false;
            if (!EnsureResources()) return false;
            bool trimMask = ShowTrimMask(data);
            if (shaded || trimMask) {
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
                surface.SetFloat("_UseVertexColor", (vertexColors || trimMask) && mesh.HasVertexAttribute(VertexAttribute.Color) ? 1 : 0);
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
            var mesh = DisplayMesh(data);
            if (!mesh || !EnsureResources()) return;
            if (wireframe) view.DrawWire(mesh, Matrix4x4.identity, shaded ? new Color(0.05f, 0.05f, 0.05f, 1) : new Color(0.4f, 0.85f, 1f, 1));
            if (cageView && stage == Stage.Result && data.geometry != null && data.cageDistance > 0)
                DrawCage(view, mesh, data);
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

        // The projection cage, drawn as its two limit shells: every face corner pushed
        // ±its reach along its cage direction (outer = ray origins, inner = ray ends),
        // with one line per welded side pair so a double-sided sheet shows both of its
        // shells. Rebuilt when the mesh, the distance, the smoothing, the fit or the
        // source changes; turn the surface Wire off to read a shell alone.
        void DrawCage(MeshViewport3D view, Mesh mesh, Data data)
        {
            var geometry = data.geometry;
            int id = mesh.GetInstanceID();
            string key = $"{data.cageDistance:R}|{data.cageSmoothing:R}|{data.cageFit}|{(data.source != null ? data.source.GetHashCode() : 0)}";
            if (id != cageMeshId || key != cageKey) {
                if (cageOuter) Object.DestroyImmediate(cageOuter);
                if (cageInner) Object.DestroyImmediate(cageInner);
                TriangleBvh source = null;
                if (data.cageFit && data.source != null && data.source.indices != null && data.source.indices.Length > 0) {
                    if (!ReferenceEquals(cageSourceRef, data.source) || cageSourceBvh == null) {
                        cageSourceBvh = new TriangleBvh(data.source.positions, data.source.indices);
                        cageSourceRef = data.source;
                    }
                    source = cageSourceBvh;
                }
                var cage = RemeshBaker.BuildCage(geometry, data.cageDistance, data.cageSmoothing, source);
                var folds = new TriangleBvh(geometry.positions, geometry.indices);
                cageOuter = CageShell(mesh, geometry, cage, 1f, "Outer", folds);
                cageInner = CageShell(mesh, geometry, cage, -1f, "Inner", folds);
                cageMeshId = id; cageKey = key;
            }
            if (cageOuter) view.DrawLineMesh(cageOuter, Matrix4x4.identity, new Color(1f, 0.55f, 0.15f, 0.9f));
            if (cageInner) view.DrawLineMesh(cageInner, Matrix4x4.identity, new Color(0.35f, 0.6f, 1f, 0.45f));
        }

        static Mesh CageShell(Mesh mesh, RemeshNative.Geometry geometry, RemeshBaker.Cage cage, float sign, string suffix, TriangleBvh folds)
        {
            int corners = geometry.indices.Length;
            if (corners == 0) return null;
            var vertices = new Vector3[corners];
            for (int c = 0; c < corners; ++c) {
                Vector3 p = geometry.positions[geometry.indices[c]];
                Vector3 dir = cage.directions[c] * sign;
                vertices[c] = p + dir * FoldLimitedOffset(folds, p, dir, cage.reach[c]);
            }
            // One line per pair of welded sides: the wire of each side's shell, drawn once.
            var lines = new List<int>(corners * 2);
            var seen = new HashSet<(int, int)>();
            for (int c = 0; c < corners; c += 3)
                for (int k = 0; k < 3; ++k) {
                    int a = c + k, b = c + (k + 1) % 3;
                    int sa = cage.side[a], sb = cage.side[b];
                    if (sa == sb) continue;
                    var pair = sa < sb ? (sa, sb) : (sb, sa);
                    if (!seen.Add(pair)) continue;
                    lines.Add(a); lines.Add(b);
                }
            var shell = new Mesh { name = mesh.name + "_Cage" + suffix, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            shell.vertices = vertices;
            shell.SetIndices(lines.ToArray(), MeshTopology.Lines, 0);
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
