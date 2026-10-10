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
        internal enum Stage { Source, Remesh, Simplified, Result, Closure }
        internal enum Channel { BaseColor, Normal, MetallicSmoothness, Occlusion, Emission }

        internal sealed class Data
        {
            public readonly Mesh[] meshes = new Mesh[5]; // indexed by Stage; old persisted values stay unchanged
            public Mesh closureRims;
            public string[] closureContourNames;
            public Vector3[][] closureContourEdges;
            public Color[] closureContourColors;
            public string[] closureContourReasons;
            public string closureSummary;
            public string closureSelectionWarning, closureLoopRanges;
            public bool closureReady, closureStale;
            public int sourceVertices, sourceTriangles;
            public Matrix4x4 spaceToWorld = Matrix4x4.identity;
            public RemeshNative.Geometry geometry;
            public RemeshBaker.Maps maps;
            public Texture2D baseColor;
            // Remesh stage: the untrimmed remesh coloured per trim class (null without a trim).
            public Mesh trimMask;
            public Mesh syntheticMask;
            // Ray travel of the bake projection (source diagonal × projection distance);
            // the Cage toggle draws the result mesh inflated by ±this along the cage
            // directions, i.e. the exact shells the projection rays start and end on.
            // Smoothing and fit mirror the bake settings; the source feeds the fit.
            public float cageDistance, cageSmoothing;
            public bool cageFit;
            public RemeshBackfaces sourceBackfaces;
            public RemeshSource source;
            public bool twoSided;
            public bool remeshReady, simplifyReady, unwrapReady, bakeReady;
            public bool remeshStale, simplifyStale, unwrapStale, bakeStale;
            public RemeshPipeline.Stage? runningStage;
            public float progress = -1;
        }

        static readonly string[] ViewNames = { "3D", "Maps" };
        public Action RequestRepaint;
        View view;
        Stage stage = Stage.Result;
        Channel channel;
        bool textured = true, bumpMap = true, cageView, trimMaskView = true, syntheticMaskView = true;
        int closureContour;
        Mesh closureSelectionMesh;

        [Serializable]
        sealed class WindowSettings
        {
            public View view;
            public Stage stage = Stage.Result;
            public Channel channel;
            public bool textured = true, bumpMap = true, cageView, trimMaskView = true, syntheticMaskView = true;
        }

        internal void RestoreWindowSettings()
        {
            var state = MeshLabWindowPreferences.Load<WindowSettings>("RemeshPreview");
            view = MeshLabWindowPreferences.ValidEnum(state.view, View.Mesh);
            stage = MeshLabWindowPreferences.ValidEnum(state.stage, Stage.Result);
            channel = MeshLabWindowPreferences.ValidEnum(state.channel, Channel.BaseColor);
            textured = state.textured; bumpMap = state.bumpMap;
            cageView = state.cageView; trimMaskView = state.trimMaskView;
            syntheticMaskView = state.syntheticMaskView;
        }

        internal void SaveWindowSettings()
            => MeshLabWindowPreferences.Save("RemeshPreview", new WindowSettings {
                view = view, stage = stage, channel = channel, textured = textured,
                bumpMap = bumpMap, cageView = cageView, trimMaskView = trimMaskView, syntheticMaskView = syntheticMaskView
            });

        Material surface;
        Material resultSurface;
        Texture2D emissionTexture;
        RenderTexture litNormalTexture;
        bool litNormalUrp;
        BuildTarget litNormalTarget;
        NormalMapEncoding litNormalEncoding;
        string resultShaderWarning;
        Mesh cageOuter, cageInner; int cageMeshId; string cageKey;
        bool cageReady;
        RemeshNative.Geometry cageGeometryRef;
        readonly PreviewWork<CageData> cageWork = new PreviewWork<CageData>("[Remesh] Cage preview");
        RemeshSource cageSourceRef; TriangleBvh cageSourceBvh;
        RemeshBaker.Maps mapSource;
        readonly Texture2D[] mapTextures = new Texture2D[5];

        public void Show(Stage value) { stage = value; }
        internal bool IsSource => stage == Stage.Source;
        internal bool ShowsSyntheticFaces => stage == Stage.Simplified && syntheticMaskView;

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
            if (emissionTexture) Object.DestroyImmediate(emissionTexture);
            emissionTexture = null;
            ReleaseLitNormal();
            if (resultSurface) Object.DestroyImmediate(resultSurface);
            resultSurface = null;
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
            DrawStageButtons(data);
            bool result = stage == Stage.Result;
            using (new EditorGUI.DisabledScope(!result || !data.baseColor))
                textured = EditorGUILayout.ToggleLeft(new GUIContent("Baked base color", "Result stage: show the baked base color on the surface."), textured);
            using (new EditorGUI.DisabledScope(!result || data.maps == null))
                bumpMap = EditorGUILayout.ToggleLeft(new GUIContent("Baked normal map", "Result stage: shade with the baked tangent-space normal map using the saved material's shader and the viewport lights."), bumpMap);
            using (new EditorGUI.DisabledScope(stage != Stage.Remesh || !data.trimMask))
                trimMaskView = EditorGUILayout.ToggleLeft(new GUIContent("Trim mask", "Remesh stage: colour the untrimmed remesh by what Trim to source surface did with each face."), trimMaskView);
            using (new EditorGUI.DisabledScope(stage != Stage.Simplified || !data.syntheticMask))
                syntheticMaskView = EditorGUILayout.ToggleLeft(new GUIContent("Cap / Bridge faces", "Simplified stage: geometric association to closure patches. Orange faces mix original and synthetic surface; inspect them before removal."), syntheticMaskView);
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
            if (ShowsSyntheticFaces && data.syntheticMask)
                EditorGUILayout.LabelField("Purple: closure · orange: mixed / uncertain · red: selected. Shift-click to select a connected region.", EditorStyles.wordWrappedMiniLabel);
            if (stage == Stage.Closure) {
                if (closureSelectionMesh != mesh) { closureSelectionMesh = mesh; closureContour = 0; }
                if (data.closureContourNames != null && data.closureContourNames.Length > 1)
                    closureContour = EditorGUILayout.Popup("Hole rim", Mathf.Clamp(closureContour, 0, data.closureContourNames.Length - 1), data.closureContourNames);
                EditorGUILayout.LabelField("Grey: original surface · orange / purple: accepted patches · cyan: original rims · red: refused contours.", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField(data.closureSummary ?? "Prepare closure to inspect it before Remesh.", EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(data.closureLoopRanges))
                    EditorGUILayout.LabelField("Available loops: " + data.closureLoopRanges, EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(data.closureSelectionWarning))
                    EditorGUILayout.HelpBox(data.closureSelectionWarning, MessageType.Warning);
                if (data.closureContourReasons != null) {
                    if (closureContour > 0 && closureContour <= data.closureContourReasons.Length) {
                        string reason = data.closureContourReasons[closureContour - 1];
                        if (!string.IsNullOrEmpty(reason)) EditorGUILayout.HelpBox(reason, MessageType.Warning);
                    }
                    else {
                        int shown = 0, refused = 0;
                        for (int i = 0; i < data.closureContourReasons.Length; ++i) {
                            if (string.IsNullOrEmpty(data.closureContourReasons[i])) continue;
                            ++refused;
                            if (shown++ < 3) EditorGUILayout.LabelField(data.closureContourNames[i + 1] + ": " + data.closureContourReasons[i], EditorStyles.wordWrappedMiniLabel);
                        }
                        if (refused > 3) EditorGUILayout.LabelField($"{refused - 3} more refused contours. Select a Hole rim to inspect its reason.", EditorStyles.wordWrappedMiniLabel);
                    }
                }
            }
            if (result && data.maps != null && data.maps.beauty && textured)
                EditorGUILayout.LabelField("Beauty contains baked scene lighting and renders unlit.", EditorStyles.wordWrappedMiniLabel);
            if (result && data.twoSided && resultSurface && !resultSurface.HasProperty("_Cull"))
                EditorGUILayout.HelpBox("This result shader renders only front faces, including after export. Use a two-sided shader to show the back of a sheet.", MessageType.Warning);
            var g = data.geometry;
            if (g != null) {
                string coverage = data.maps != null ? $" · {100.0 * data.maps.covered / ((double)data.maps.size * data.maps.size):0.#}% texels used" : "";
                EditorGUILayout.LabelField($"Atlas: {g.chartCount:N0} islands{coverage}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField("Wire, shading modes, UV fill and island borders: canvas controls (status bar, 3D shading row).", EditorStyles.wordWrappedMiniLabel);
            if (GUI.changed) RequestRepaint?.Invoke();
        }

        internal static Stage? PreviewStage(RemeshPipeline.Stage? running)
            => !running.HasValue ? (Stage?)null : running == RemeshPipeline.Stage.Prepare ? Stage.Closure : running == RemeshPipeline.Stage.Remesh ? Stage.Remesh :
                running == RemeshPipeline.Stage.Simplify ? Stage.Simplified : Stage.Result;

        void DrawStageButtons(Data data)
        {
            const float buttonHeight = 44, gap = 6;
            var area = GUILayoutUtility.GetRect(0, buttonHeight * 3 + gap * 2, GUILayout.ExpandWidth(true));
            var running = PreviewStage(data.runningStage);
            var stages = new[] { Stage.Source, Stage.Closure, Stage.Remesh, Stage.Simplified, Stage.Result };
            var labels = new[] { "Source", "Cap / Bridge", "Remesh", "Simplify", "Result" };
            var ready = new[] { data.sourceVertices > 0 || data.meshes[0], data.closureReady, data.remeshReady, data.simplifyReady, data.unwrapReady };
            var stale = new[] { false, data.closureStale, data.remeshStale, data.simplifyStale, data.unwrapStale || data.bakeStale };
            var titleStyle = new GUIStyle(EditorStyles.label) {
                alignment = TextAnchor.MiddleCenter, fontSize = 12, fontStyle = FontStyle.Bold,
                padding = new RectOffset(), margin = new RectOffset(), fixedHeight = 0, wordWrap = false
            };
            var stateStyle = new GUIStyle(titleStyle) { fontSize = 11, fontStyle = FontStyle.Normal };
            for (int i = 0; i < stages.Length; i++) {
                var value = stages[i];
                var r = new Rect(area.x + (i % 2) * (area.width + gap) * .5f,
                    area.y + (i / 2) * (buttonHeight + gap), (area.width - gap) * .5f, buttonHeight);
                bool working = running == value;
                var color = working ? new Color(.12f, .38f, .62f) : stale[i] ? new Color(.48f, .32f, .12f) :
                    ready[i] ? new Color(.18f, .38f, .25f) : new Color(.25f, .25f, .25f);
                EditorGUI.DrawRect(r, color);
                if (working) {
                    float width = data.progress >= 0 ? r.width * Mathf.Clamp01(data.progress) : r.width * .25f;
                    float offset = data.progress >= 0 ? 0 : (r.width - width) * Mathf.PingPong((float)EditorApplication.timeSinceStartup, 1);
                    EditorGUI.DrawRect(new Rect(r.x + offset, r.yMax - 5, width, 3), new Color(.35f, .75f, 1));
                    RequestRepaint?.Invoke();
                }
                if (stage == value) {
                    var border = new Color(1f, .55f, .15f);
                    EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 2), border);
                    EditorGUI.DrawRect(new Rect(r.x, r.yMax - 2, r.width, 2), border);
                }
                string state = working ? (data.runningStage == RemeshPipeline.Stage.Unwrap ? "Unwrapping…" : data.runningStage == RemeshPipeline.Stage.Bake ? "Baking…" : "Working…") :
                    stale[i] ? "Settings changed" : ready[i] ? (value == Stage.Result ? data.bakeReady ? "Baked" : "UV ready" : "Ready") : "Not built";
                if (working && data.progress >= 0) state += $" {data.progress * 100:0}%";
                titleStyle.normal.textColor = ready[i] || working ? Color.white : new Color(.75f, .75f, .75f);
                stateStyle.normal.textColor = new Color(.8f, .8f, .8f);
                GUI.Label(new Rect(r.x + 4, r.y + 5, Mathf.Max(0, r.width - 8), 17), labels[i], titleStyle);
                GUI.Label(new Rect(r.x + 4, r.y + 23, Mathf.Max(0, r.width - 8), 15), state, stateStyle);
                using (new EditorGUI.DisabledScope(!ready[i]))
                    if (GUI.Button(r, new GUIContent("", labels[i] + ": " + state + ". Select the preview stage. Orange border: selected; green: ready; blue: running; amber: changed settings."), GUIStyle.none)) {
                        stage = value; RequestRepaint?.Invoke();
                    }
            }
        }

        bool ShowTrimMask(Data data) => trimMaskView && stage == Stage.Remesh && data.trimMask;

        // The mesh the 3D view shows for the stage: the trim mask stands in for the
        // trimmed remesh while its toggle is on.
        internal Mesh DisplayMesh(Data data) => ShowTrimMask(data) ? data.trimMask : ShowsSyntheticFaces && data.syntheticMask ? data.syntheticMask : data.meshes[(int)stage];

        public bool Fill3D(Data data, List<MeshViewport3D.Item> items)
        {
            var mesh = DisplayMesh(data);
            if (!mesh) return false;
            if (!EnsureResources()) return false;
            // Source uses its original materials under the viewport lights. Result
            // must use the exported PBR shader under those same lights, not the
            // geometry/trim preview's camera-facing light approximation.
            if (stage == Stage.Result) {
                var material = ResultMaterial(data);
                if (material) {
                    var resultMaterials = new Material[mesh.subMeshCount];
                    for (int sub = 0; sub < resultMaterials.Length; ++sub) resultMaterials[sub] = material;
                    items.Add(new MeshViewport3D.Item(mesh, data.spaceToWorld, resultMaterials));
                    return true;
                }
            }
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
            surface.SetFloat("_UseVertexColor", (trimMask || ShowsSyntheticFaces || stage == Stage.Closure) && mesh.HasVertexAttribute(VertexAttribute.Color) ? 1 : 0);
            surface.SetColor("_Color", Color.white);
            var materials = new Material[mesh.subMeshCount];
            for (int sub = 0; sub < materials.Length; ++sub) materials[sub] = surface;
            items.Add(new MeshViewport3D.Item(mesh, data.spaceToWorld, materials));
            return true;
        }

        Material ResultMaterial(Data data)
        {
            bool unlit = textured && data.baseColor && data.maps != null && data.maps.beauty;
            Shader shader;
            bool urp;
            try { shader = RemeshExporter.ResolveShader(unlit, out urp); }
            catch (InvalidOperationException ex) {
                if (resultShaderWarning != ex.Message) UvtLog.Warn("[Remesh preview] " + ex.Message);
                resultShaderWarning = ex.Message;
                return null;
            }
            resultShaderWarning = null;
            if (!resultSurface || resultSurface.shader != shader) {
                if (resultSurface) Object.DestroyImmediate(resultSurface);
                resultSurface = new Material(shader) { name = "Remesh result preview", hideFlags = HideFlags.HideAndDontSave };
            }
            SyncMapSource(data.maps);
            var maps = new Texture[5];
            maps[0] = textured ? data.baseColor : null;
            if (data.maps != null && !unlit) {
                maps[1] = bumpMap ? LitNormalTexture(data.maps, urp) : null;
                maps[2] = MapTexture(data.maps, Channel.MetallicSmoothness);
                maps[3] = MapTexture(data.maps, Channel.Occlusion);
                maps[4] = EmissionTexture(data.maps);
            }
            RemeshExporter.ConfigureMaterial(resultSurface, urp, unlit, maps);
            if (resultSurface.HasProperty("_Cull"))
                resultSurface.SetFloat("_Cull", (float)(data.twoSided ? CullMode.Off : CullMode.Back));
            return resultSurface;
        }

        /// <summary>The cage overlay for the result in the shared 3D canvas (the wire is
        /// the canvas's own, drawn by the hub's UV layer for every item).</summary>
        public void Overlay3D(Data data, MeshViewport3D view)
        {
            var mesh = DisplayMesh(data);
            if (!mesh || !EnsureResources()) return;
            if (stage == Stage.Closure && data.closureRims) {
                var color = Color.white;
                if (closureSelectionMesh == mesh && closureContour > 0 && data.closureContourEdges != null && closureContour <= data.closureContourEdges.Length)
                    view.DrawLines(data.closureContourEdges[closureContour - 1], data.spaceToWorld,
                        data.closureContourColors != null && closureContour <= data.closureContourColors.Length
                            ? data.closureContourColors[closureContour - 1] : new Color(.2f, .85f, 1f, .95f));
                else view.DrawLineMesh(data.closureRims, data.spaceToWorld, color);
            }
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
            SyncMapSource(maps);
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
            var texture = TextureAssets.FromPixels(pixels, maps.size, maps.size, linear: which != Channel.BaseColor);
            if (texture) texture.wrapMode = TextureWrapMode.Clamp;
            return mapTextures[index] = texture;
        }

        void SyncMapSource(RemeshBaker.Maps maps)
        {
            if (ReferenceEquals(maps, mapSource)) return;
            foreach (var t in mapTextures) if (t) Object.DestroyImmediate(t);
            Array.Clear(mapTextures, 0, mapTextures.Length);
            if (emissionTexture) Object.DestroyImmediate(emissionTexture);
            emissionTexture = null;
            ReleaseLitNormal();
            mapSource = maps;
        }

        Texture LitNormalTexture(RemeshBaker.Maps maps, bool urp)
        {
            var canonical = MapTexture(maps, Channel.Normal);
            if (!canonical) return null;
            var target = EditorUserBuildSettings.activeBuildTarget;
            var encoding = RemeshNormalPreviewPacking.ActiveEncoding();
            if (litNormalTexture && litNormalUrp == urp && litNormalTarget == target && litNormalEncoding == encoding) return litNormalTexture;
            ReleaseLitNormal();
            litNormalTexture = RemeshNormalPreviewPacking.Create(canonical, urp);
            litNormalUrp = urp; litNormalTarget = target; litNormalEncoding = encoding;
            return litNormalTexture;
        }

        void ReleaseLitNormal()
        {
            if (!litNormalTexture) return;
            litNormalTexture.Release();
            Object.DestroyImmediate(litNormalTexture);
            litNormalTexture = null;
        }

        Texture2D EmissionTexture(RemeshBaker.Maps maps)
        {
            if (emissionTexture) return emissionTexture;
            if (maps.emission == null || maps.emission.Length != maps.size * maps.size) return null;
            // Emission is linear HDR, unlike the clipped swatch in the Maps panel.
            emissionTexture = new Texture2D(maps.size, maps.size, TextureFormat.RGBAHalf, false, true) {
                name = "Remesh emission preview", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp
            };
            emissionTexture.SetPixels(maps.emission);
            emissionTexture.Apply();
            return emissionTexture;
        }
    }
}
