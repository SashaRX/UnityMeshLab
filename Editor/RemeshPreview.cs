using System;
using System.Collections.Generic;
using System.Threading;
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
            public int sourceVertices, sourceTriangles;
            public Matrix4x4 spaceToWorld = Matrix4x4.identity;
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
            public RemeshBackfaces sourceBackfaces;
            public RemeshSource source;
        }

        static readonly string[] ViewNames = { "3D", "Maps" };
        public Action RequestRepaint;
        View view;
        Stage stage = Stage.Result;
        Channel channel;
        bool textured = true, bumpMap = true, cageView, trimMaskView = true;
        Material surface;
        Mesh cageOuter, cageInner; int cageMeshId; string cageKey;
        bool cageReady;
        RemeshNative.Geometry cageGeometryRef;
        readonly PreviewWork<CageData> cageWork = new PreviewWork<CageData>("[Remesh] Cage preview");
        RemeshSource cageSourceRef; TriangleBvh cageSourceBvh;
        RemeshBaker.Maps mapSource;
        readonly Texture2D[] mapTextures = new Texture2D[5];

        public void Show(Stage value) { stage = value; }
        internal bool IsSource => stage == Stage.Source;

        public void Draw(Data data)
        {
            view = (View)GUILayout.Toolbar((int)view, ViewNames, EditorStyles.toolbarButton);
            EditorGUILayout.Space(2);
            if (view == View.Mesh) DrawMesh(data); else DrawMaps(data);
        }

        /// <summary>Drop cached per-mesh wireframes; call before destroying preview meshes.</summary>
        public void Invalidate()
        {
            cageWork.Dispose(); cageReady = false; cageGeometryRef = null;
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
        }

        // ── 3D ──
        // The stage mesh is shown in the hub's shared 3D canvas (UV | 3D switch): this
        // panel only picks the stage and the surface/overlay toggles; Fill3D and
        // Overlay3D feed the viewport through the tool's IUvTool3D implementation.

        // Vertical, for a narrow right column beside the canvas. Only what is specific
        // to this tool lives here: the stage, the baked maps on the result, the trim
        // mask and the cage. Wire, shading modes, UV fill and island borders are the
        // canvas's own controls (status bar and the 3D view's shading row) and apply
        // to the stage mesh like to any other.
        void DrawMesh(Data data)
        {
            stage = (Stage)EditorGUILayout.EnumPopup("Stage", stage);
            bool result = stage == Stage.Result;
            using (new EditorGUI.DisabledScope(!result || !data.baseColor))
                textured = EditorGUILayout.ToggleLeft(new GUIContent("Baked base color", "Result stage: show the baked base color on the surface."), textured);
            using (new EditorGUI.DisabledScope(!result || data.maps == null))
                bumpMap = EditorGUILayout.ToggleLeft(new GUIContent("Baked normal map", "Result stage: shade with the baked tangent-space normal map (the saved material's look)."), bumpMap);
            using (new EditorGUI.DisabledScope(stage != Stage.Remesh || !data.trimMask))
                trimMaskView = EditorGUILayout.ToggleLeft(new GUIContent("Trim mask", "Remesh stage: colour the untrimmed remesh by what Trim to source surface did with each face."), trimMaskView);
            using (new EditorGUI.DisabledScope(!result || data.geometry == null))
                cageView = EditorGUILayout.ToggleLeft(new GUIContent("Cage shells", "Result stage: the projection limits — every corner pushed ±its reach along its cage direction; orange where the rays start, blue where they end."), cageView);
            if (cageView && result)
                EditorGUILayout.LabelField(cageWork.IsPending ? "Preparing cage…" : "", EditorStyles.miniLabel);
            var mesh = data.meshes[(int)stage];
            string meshSummary = mesh ? $"{mesh.vertexCount:N0} vertices · {Triangles(mesh):N0} triangles" : "Run this stage to preview it.";
            if (stage == Stage.Source && data.sourceVertices > 0)
                meshSummary = $"{data.sourceVertices:N0} vertices · {data.sourceTriangles:N0} triangles";
            EditorGUILayout.LabelField(meshSummary, EditorStyles.miniLabel);
            if (ShowTrimMask(data))
                EditorGUILayout.LabelField("Trim mask: green kept · red back of a sheet (opposite normal) · orange rim / no source within reach", EditorStyles.wordWrappedMiniLabel);
            var g = data.geometry;
            if (g != null) {
                string coverage = data.maps != null ? $" · {100.0 * data.maps.covered / ((double)data.maps.size * data.maps.size):0.#}% texels used" : "";
                EditorGUILayout.LabelField($"Atlas: {g.chartCount:N0} islands{coverage}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField("Wire, shading modes, UV fill and island borders: canvas controls (status bar, 3D shading row).", EditorStyles.wordWrappedMiniLabel);
            if (GUI.changed) RequestRepaint?.Invoke();
        }

        bool ShowTrimMask(Data data) => trimMaskView && stage == Stage.Remesh && data.trimMask;

        // The mesh the 3D view shows for the stage: the trim mask stands in for the
        // trimmed remesh while its toggle is on.
        internal Mesh DisplayMesh(Data data) => ShowTrimMask(data) ? data.trimMask : data.meshes[(int)stage];

        public bool Fill3D(Data data, List<MeshViewport3D.Item> items)
        {
            var mesh = DisplayMesh(data);
            if (!mesh) return false;
            if (!EnsureResources()) return false;
            bool trimMask = ShowTrimMask(data);
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
            surface.SetFloat("_UseVertexColor", trimMask && mesh.HasVertexAttribute(VertexAttribute.Color) ? 1 : 0);
            surface.SetColor("_Color", Color.white);
            var materials = new Material[mesh.subMeshCount];
            for (int sub = 0; sub < materials.Length; ++sub) materials[sub] = surface;
            items.Add(new MeshViewport3D.Item(mesh, data.spaceToWorld, materials));
            return true;
        }

        /// <summary>The cage overlay for the result in the shared 3D canvas (the wire is
        /// the canvas's own, drawn by the hub's UV layer for every item).</summary>
        public void Overlay3D(Data data, MeshViewport3D view)
        {
            var mesh = DisplayMesh(data);
            if (!mesh || !EnsureResources()) return;
            if (cageView && stage == Stage.Result && data.geometry != null && data.cageDistance > 0)
                DrawCage(view, mesh, data);
        }

        bool EnsureResources()
        {
            if (surface) return true;
            var shader = Shader.Find("Hidden/MeshLab/RemeshPreview");
            if (!shader) return false;
            surface = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        // The projection cage, drawn as its two limit shells: every face corner pushed
        // ±its reach along its cage direction (outer = ray origins, inner = ray ends),
        // with one line per welded side pair so a double-sided sheet shows both of its
        // shells. Rebuilt when the mesh, the distance, the smoothing, the fit or the
        // source changes; turn the surface Wire off to read a shell alone.
        void DrawCage(MeshViewport3D view, Mesh mesh, Data data)
        {
            PrepareCage(mesh, data);
            if (!cageReady) return;
            if (cageOuter) view.DrawLineMesh(cageOuter, data.spaceToWorld, new Color(1f, 0.55f, 0.15f, 0.9f));
            if (cageInner) view.DrawLineMesh(cageInner, data.spaceToWorld, new Color(0.35f, 0.6f, 1f, 0.45f));
        }

        internal void PrepareCage(Mesh mesh, Data data)
        {
            var geometry = data.geometry;
            int id = mesh.GetInstanceID();
            string key = $"{data.cageDistance:R}|{data.cageSmoothing:R}|{data.cageFit}|{data.sourceBackfaces}|{(data.source != null ? data.source.GetHashCode() : 0)}";
            if (id == cageMeshId && key == cageKey && ReferenceEquals(geometry, cageGeometryRef)) return;
            cageMeshId = id; cageKey = key; cageGeometryRef = geometry; cageReady = false;
            var source = data.cageFit ? data.source : null;
            float distance = data.cageDistance, smoothing = data.cageSmoothing;
            var sourceBackfaces = data.sourceBackfaces;
            cageWork.Enqueue(() => {
                if (cageOuter) Object.DestroyImmediate(cageOuter);
                if (cageInner) Object.DestroyImmediate(cageInner);
                cageOuter = cageInner = null;
                var cachedSource = ReferenceEquals(cageSourceRef, source) ? cageSourceBvh : null;
                return token => BuildCageData(geometry, source, cachedSource, distance, smoothing, sourceBackfaces, token);
            }, prepared => {
                if (!mesh) return;
                cageSourceRef = source; cageSourceBvh = prepared.source;
                cageOuter = UploadCage(mesh.name + "_CageOuter", prepared.outer, prepared.lines);
                cageInner = UploadCage(mesh.name + "_CageInner", prepared.inner, prepared.lines);
                cageReady = true;
                RequestRepaint?.Invoke();
            });
        }

        sealed class CageData { public Vector3[] outer, inner; public int[] lines; public TriangleBvh source; }

        static CageData BuildCageData(RemeshNative.Geometry geometry, RemeshSource source, TriangleBvh sourceBvh,
            float distance, float smoothing, RemeshBackfaces sourceBackfaces, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (sourceBvh == null && source?.indices != null && source.indices.Length > 0)
                sourceBvh = new TriangleBvh(source.positions, source.indices);
            Vector3[] sourceNormals = null; bool[] eitherSide = null;
            if (sourceBvh != null && source != null) {
                eitherSide = source.TwoSidedFaces(sourceBackfaces);
                sourceNormals = MeshGeometry.FaceNormals(source.positions, source.indices);
                int winding = RemeshBaker.ProbeWinding(sourceBvh, source.positions, sourceNormals, eitherSide);
                if (winding == 0) { sourceNormals = null; eitherSide = null; }
                else if (winding < 0)
                    for (int f = 0; f < sourceNormals.Length; ++f) sourceNormals[f] = -sourceNormals[f];
            }
            var cage = RemeshBaker.BuildCageWithFacing(geometry, distance, smoothing, sourceBvh, sourceNormals, eitherSide, token: token);
            token.ThrowIfCancellationRequested();
            var folds = new TriangleBvh(geometry.positions, geometry.indices);
            var outer = CageVertices(geometry, cage, 1f, folds, token);
            var inner = CageVertices(geometry, cage, -1f, folds, token);
            int corners = geometry.indices.Length;
            var lines = new List<int>(corners * 2);
            var seen = new HashSet<(int, int)>();
            for (int c = 0; c < corners; c += 3) {
                if ((c & 255) == 0) token.ThrowIfCancellationRequested();
                for (int k = 0; k < 3; ++k) {
                    int a = c + k, b = c + (k + 1) % 3;
                    int sa = cage.side[a], sb = cage.side[b];
                    if (sa == sb) continue;
                    var pair = sa < sb ? (sa, sb) : (sb, sa);
                    if (!seen.Add(pair)) continue;
                    lines.Add(a); lines.Add(b);
                }
            }
            return new CageData { outer = outer, inner = inner, lines = lines.ToArray(), source = sourceBvh };
        }

        static Vector3[] CageVertices(RemeshNative.Geometry geometry, RemeshBaker.Cage cage, float sign,
            TriangleBvh folds, CancellationToken token)
        {
            int corners = geometry.indices.Length;
            var vertices = new Vector3[corners];
            for (int c = 0; c < corners; ++c) {
                if ((c & 255) == 0) token.ThrowIfCancellationRequested();
                Vector3 p = geometry.positions[geometry.indices[c]];
                Vector3 dir = cage.directions[c] * sign;
                vertices[c] = p + dir * FoldLimitedOffset(folds, p, dir, cage.reach[c]);
            }
            return vertices;
        }

        static Mesh UploadCage(string name, Vector3[] vertices, int[] lines)
        {
            if (vertices.Length == 0) return null;
            var shell = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            shell.vertices = vertices;
            shell.SetIndices(lines, MeshTopology.Lines, 0);
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
            Color32[] pixels;
            switch (which) {
                case Channel.BaseColor: pixels = maps.color; break;
                case Channel.Normal: pixels = maps.normal; break;
                case Channel.MetallicSmoothness: pixels = maps.metal; break;
                case Channel.Occlusion: pixels = maps.ao; break;
                default:
                    pixels = new Color32[maps.emission.Length];
                    for (int i = 0; i < pixels.Length; ++i) {
                        var e = maps.emission[i];
                        pixels[i] = new Color(Mathf.Clamp01(e.r), Mathf.Clamp01(e.g), Mathf.Clamp01(e.b), 1).gamma;
                    }
                    break;
            }
            return mapTextures[index] = TextureAssets.FromPixels(pixels, maps.size, maps.size, linear: which != Channel.BaseColor);
        }
    }
}
