using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Shared, read-only element picking and hole navigation for every tool stage.</summary>
    internal sealed class MeshTopologyPreview : IDisposable
    {
        internal enum Element { Off, Polygon, Edge, Border, Vertex }
        internal Element Mode;
        internal bool ShowHoles;
        internal Action Repaint;
        internal struct Hit
        {
            internal Mesh mesh;
            internal int index, item;
            internal Matrix4x4 matrix;
            internal bool Matches(MeshViewport3D.Item candidate, int slot) =>
                mesh && mesh == candidate.mesh && item == slot && matrix == candidate.matrix;
        }
        sealed class Cache : IDisposable
        {
            internal ViewportTopology data;
            internal Mesh boundaries, defects;
            internal Mesh vertices;
            internal bool pointsRequested;
            internal readonly PreviewWork<PointData> pointsWork = new PreviewWork<PointData>("[3D] Vertex preview");
            internal readonly PreviewWork<ViewportTopology> work = new PreviewWork<ViewportTopology>("[3D] Hole search");
            public void Dispose()
            {
                work.Dispose();
                pointsWork.Dispose();
                if (boundaries) Object.DestroyImmediate(boundaries);
                if (defects) Object.DestroyImmediate(defects);
                if (vertices) Object.DestroyImmediate(vertices);
            }
        }
        sealed class PointData { internal Vector3[] positions; internal Vector2[] corners; internal int[] indices; }
        readonly Dictionary<Mesh, Cache> caches = new Dictionary<Mesh, Cache>();
        Hit hover, selected;
        Mesh hoverFace, selectedFace;
        string search = "";
        int selectedRim = -1;
        readonly List<(Mesh mesh, int item, int rim)> shownRims = new List<(Mesh, int, int)>();
        internal MeshTopologyPreview() { VertexChannels.Changed += InvalidateMesh; }
        internal bool Active => Mode != Element.Off || ShowHoles;

        internal void Toolbar(IReadOnlyList<MeshViewport3D.Item> items, MeshViewport3D viewport)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label(new GUIContent("Pick", "Inspect geometry without changing the mesh. Border means a geometry boundary; UV seams are separate."), EditorStyles.miniLabel, GUILayout.Width(25));
            var next = (Element)EditorGUILayout.EnumPopup(Mode, EditorStyles.toolbarPopup, GUILayout.Width(85));
            if (next != Mode) { Mode = next; hover = selected = default; }
            ShowHoles = GUILayout.Toggle(ShowHoles, new GUIContent("Holes", "Find geometry boundaries after position weld; UV seams are excluded."), EditorStyles.toolbarButton, GUILayout.Width(48));
            viewport.XRay = GUILayout.Toggle(viewport.XRay, new GUIContent("X-ray", "Show highlighted polygons, edges and vertices through the model."), EditorStyles.toolbarButton, GUILayout.Width(48));
            GUILayout.Label("Opacity", EditorStyles.miniLabel, GUILayout.Width(44));
            viewport.SurfaceOpacity = GUILayout.HorizontalSlider(viewport.SurfaceOpacity, .05f, 1f, GUILayout.Width(70));
            if (GUILayout.Button("Solid", EditorStyles.toolbarButton, GUILayout.Width(40))) viewport.SurfaceOpacity = 1;
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(40))) { selected = hover = default; selectedRim = -1; }
            GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal();
            Prepare(items);
            if (ShowHoles) HoleToolbar(items, viewport);
        }

        internal ViewportTopology Data(Mesh mesh) => mesh && caches.TryGetValue(mesh, out var cache) ? cache.data : null;

        internal void Prepare(IReadOnlyList<MeshViewport3D.Item> items)
        {
            var active = new HashSet<Mesh>(items.Where(i => i.mesh).Select(i => i.mesh));
            foreach (var mesh in caches.Keys.Where(m => !m || !active.Contains(m)).ToArray()) InvalidateMesh(mesh);
            if (selected.mesh && !MatchesCurrentItem(selected,items)) selected = default;
            if (hover.mesh && !MatchesCurrentItem(hover,items)) hover = default;
            if (!Active) return;
            foreach (var item in items) {
                var mesh = item.mesh;
                if (!mesh || caches.ContainsKey(mesh) || !mesh.isReadable) continue;
                var cache = new Cache(); caches.Add(mesh, cache);
                cache.work.Enqueue(() => {
                    var positions = mesh.vertices; var triangles = new List<int>();
                    for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                        var topology = mesh.GetTopology(sub);
                        if (topology == MeshTopology.Triangles) triangles.AddRange(mesh.GetIndices(sub));
                        else if (topology == MeshTopology.Quads) {
                            var indices = mesh.GetIndices(sub);
                            for (int j = 0; j < indices.Length; j += 4) triangles.AddRange(new[] { indices[j], indices[j+1], indices[j+2], indices[j], indices[j+2], indices[j+3] });
                        }
                    }
                    var snapshot = triangles.ToArray();
                    return token => ViewportTopology.Build(positions, snapshot, token);
                }, data => {
                    cache.data = data;
                    cache.boundaries = Lines(data, e => e.uses == 1);
                    cache.defects = Lines(data, e => e.uses > 2);
                    Repaint?.Invoke();
                });
            }
        }

        static Mesh Lines(ViewportTopology data, Predicate<ViewportTopology.Edge> include)
        {
            var indices = new List<int>();
            foreach (var edge in data.edges) if (include(edge)) { indices.Add(edge.a); indices.Add(edge.b); }
            if (indices.Count == 0) return null;
            var mesh = new Mesh { name = "Viewport geometry boundaries", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            mesh.vertices = data.positions; mesh.SetIndices(indices, MeshTopology.Lines, 0); return mesh;
        }

        void HoleToolbar(IReadOnlyList<MeshViewport3D.Item> items, MeshViewport3D viewport)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            string nextSearch = GUILayout.TextField(search, EditorStyles.toolbarTextField, GUILayout.Width(110));
            if (nextSearch != search) { search = nextSearch; selectedRim = -1; }
            shownRims.Clear(); var names = new List<string>(); int defects = 0;
            for (int item = 0; item < items.Count; ++item) {
                var mesh = items[item].mesh;
                if (!mesh || !caches.TryGetValue(mesh, out var cache) || cache.data == null) continue;
                defects += cache.data.nonmanifoldEdges;
                for (int rim = 0; rim < cache.data.rims.Length; ++rim) {
                    var loop = cache.data.rims[rim];
                    string name = $"{mesh.name} / #{rim} · {loop.edges.Length} edges · {(loop.closed ? "loop" : "open / branched")}";
                    if (search.Length > 0 && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    names.Add(name); shownRims.Add((mesh, item, rim));
                }
            }
            if (selectedRim >= shownRims.Count) selectedRim = -1;
            if (names.Count > 0) {
                var labels = new[] { $"All {names.Count} boundaries" }.Concat(names).ToArray();
                selectedRim = EditorGUILayout.Popup(selectedRim + 1, labels, EditorStyles.toolbarPopup) - 1;
                if (GUILayout.Button("<", EditorStyles.toolbarButton, GUILayout.Width(22))) selectedRim = (selectedRim + names.Count - 1) % names.Count;
                if (GUILayout.Button(">", EditorStyles.toolbarButton, GUILayout.Width(22))) selectedRim = (selectedRim + 1) % names.Count;
                using (new EditorGUI.DisabledScope(selectedRim < 0))
                    if (GUILayout.Button("Frame hole", EditorStyles.toolbarButton, GUILayout.Width(72))) {
                        var choice = shownRims[selectedRim];
                        var data = caches[choice.mesh].data;
                        var rim = data.rims[choice.rim];
                        var bounds = new Bounds(items[choice.item].matrix.MultiplyPoint3x4(data.positions[data.edges[rim.edges[0]].a]), Vector3.zero);
                        foreach (int e in rim.edges) { bounds.Encapsulate(items[choice.item].matrix.MultiplyPoint3x4(data.positions[data.edges[e].a])); bounds.Encapsulate(items[choice.item].matrix.MultiplyPoint3x4(data.positions[data.edges[e].b])); }
                        viewport.Frame(bounds);
                    }
            }
            else GUILayout.Label(caches.Values.Any(c => c.work.IsPending) ? "Finding holes…" : "No matching boundaries", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            if (selectedRim >= 0) {
                var choice = shownRims[selectedRim]; var rim = caches[choice.mesh].data.rims[choice.rim];
                EditorGUILayout.LabelField($"Perimeter {rim.length:G4} · {rim.Reason}", EditorStyles.miniLabel);
            }
            if (defects > 0) EditorGUILayout.LabelField($"{defects} non-manifold edges (red). They are separate from hole loops.", EditorStyles.miniLabel);
        }

        internal void Input(MeshViewport3D viewport, Event e, IReadOnlyList<MeshViewport3D.Item> items)
        {
            if (e.type == EventType.MouseLeaveWindow) { hover = default; Repaint?.Invoke(); return; }
            if (!Active || e.alt || e.control || e.shift || (e.type != EventType.MouseMove && e.type != EventType.MouseDown)) return;
            if (!viewport.LastRect.Contains(e.mousePosition)) { hover = default; return; }
            hover = Pick(viewport, e.mousePosition, items);
            if (e.type == EventType.MouseDown && e.button == 0) {
                selected = hover;
                if (ShowHoles && Mode == Element.Off && hover.mesh) selectedRim = shownRims.FindIndex(r => r.mesh == hover.mesh && r.item == hover.item && r.rim == hover.index);
                e.Use();
            }
            Repaint?.Invoke();
        }

        Hit Pick(MeshViewport3D viewport, Vector2 pointer, IReadOnlyList<MeshViewport3D.Item> items)
        {
            if (!viewport.TryScreenRay(pointer, out var origin, out var direction)) return default;
            Hit hit = default; float best = 100, nearest = float.MaxValue;
            for (int itemIndex = 0; itemIndex < items.Count; ++itemIndex) {
                var item = items[itemIndex];
                if (!item.mesh || !caches.TryGetValue(item.mesh, out var cache) || cache.data?.bvh == null) continue;
                var inverse = item.matrix.inverse;
                var localOrigin = inverse.MultiplyPoint3x4(origin); var localDirection = inverse.MultiplyVector(direction).normalized;
                var ray = cache.data.bvh.Raycast(localOrigin, localDirection, float.MaxValue);
                if (ray.triangleIndex >= 0) {
                    float distance = Vector3.Distance(origin, item.matrix.MultiplyPoint3x4(localOrigin + localDirection * ray.t));
                    if (distance < nearest) { nearest = distance; if (Mode == Element.Polygon) hit = new Hit { mesh = item.mesh, index = ray.triangleIndex, item = itemIndex, matrix = item.matrix }; }
                }
            }
            if (Mode == Element.Polygon) return hit;
            for (int itemIndex = 0; itemIndex < items.Count; ++itemIndex) {
                var item = items[itemIndex];
                if (!item.mesh || !caches.TryGetValue(item.mesh, out var cache) || cache.data == null) continue;
                var data = cache.data;
                if (Mode == Element.Vertex) {
                    foreach (int vertex in data.vertexRepresentatives) {
                        var world = item.matrix.MultiplyPoint3x4(data.positions[vertex]);
                        if (!viewport.TryProject(world, out var screen, out _)) continue;
                        float score = (screen - pointer).sqrMagnitude;
                        if (score < best && Visible(world)) { best = score; hit = new Hit { mesh = item.mesh, index = vertex, item = itemIndex, matrix = item.matrix }; }
                    }
                }
                else {
                    for (int i = 0; i < data.edges.Length; ++i) {
                        var edge = data.edges[i];
                        if ((Mode == Element.Border || Mode == Element.Off) && edge.uses != 1) continue;
                        var a = item.matrix.MultiplyPoint3x4(data.positions[edge.a]); var b = item.matrix.MultiplyPoint3x4(data.positions[edge.b]);
                        if (!viewport.TryProject(a, out var sa, out _) || !viewport.TryProject(b, out var sb, out _)) continue;
                        var ab = sb - sa; float t = Mathf.Clamp01(Vector2.Dot(pointer - sa, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
                        float score = (sa + ab * t - pointer).sqrMagnitude;
                        if (score >= best || !Visible(Vector3.Lerp(a,b,t))) continue;
                        int index = i;
                        if (Mode == Element.Off) index = Array.FindIndex(data.rims, r => Array.IndexOf(r.edges, i) >= 0);
                        best = score; hit = new Hit { mesh = item.mesh, index = index, item = itemIndex, matrix = item.matrix };
                    }
                }
            }
            bool Visible(Vector3 world) => viewport.XRay || Vector3.Distance(origin, world) <= nearest + Mathf.Max(.001f, nearest * .005f);
            return hit;
        }

        internal void Draw(MeshViewport3D viewport, IReadOnlyList<MeshViewport3D.Item> items)
        {
            if (!Active) return;
            for (int itemIndex = 0; itemIndex < items.Count; ++itemIndex) {
                var item = items[itemIndex];
                if (!item.mesh || !caches.TryGetValue(item.mesh, out var cache) || cache.data == null) continue;
                if (Mode == Element.Border && !ShowHoles) viewport.DrawLineMesh(cache.boundaries, item.matrix, ViewportHighlight.Border, ViewportHighlight.EdgeWidth);
                if (Mode == Element.Edge) viewport.DrawWire(item.mesh, item.matrix, viewport.WireColor);
                if (Mode == Element.Vertex) {
                    PreparePoints(cache);
                    viewport.DrawPointMesh(cache.vertices, item.matrix, ViewportHighlight.Border, 3f);
                }
                if (ShowHoles) {
                    viewport.DrawLineMesh(cache.boundaries, item.matrix, ViewportHighlight.Hole, ViewportHighlight.EdgeWidth);
                    viewport.DrawLineMesh(cache.defects, item.matrix, ViewportHighlight.Refused, ViewportHighlight.EdgeWidth);
                    if (selectedRim >= 0 && selectedRim < shownRims.Count && shownRims[selectedRim].mesh == item.mesh && shownRims[selectedRim].item == itemIndex)
                        DrawRim(viewport, item.matrix, cache.data, shownRims[selectedRim].rim, ViewportHighlight.Selected);
                }
                DrawHit(viewport, item, itemIndex, cache.data, selected, ViewportHighlight.Selected, ref selectedFace);
                if (hover.mesh != selected.mesh || hover.index != selected.index || hover.item != selected.item || hover.matrix != selected.matrix)
                    DrawHit(viewport, item, itemIndex, cache.data, hover, ViewportHighlight.Hover, ref hoverFace);
            }
        }

        void PreparePoints(Cache cache)
        {
            if (cache.pointsRequested) return;
            cache.pointsRequested = true;
            cache.pointsWork.Enqueue(() => {
                var positions = cache.data.positions; var representatives = cache.data.vertexRepresentatives;
                return token => {
                    var data = new PointData { positions = new Vector3[representatives.Length * 4], corners = new Vector2[representatives.Length * 4], indices = new int[representatives.Length * 6] };
                    var corners = new[] { new Vector2(-.5f,-.5f), new Vector2(.5f,-.5f), new Vector2(.5f,.5f), new Vector2(-.5f,.5f) };
                    for (int i = 0; i < representatives.Length; ++i) {
                        token.ThrowIfCancellationRequested();
                        for (int k = 0; k < 4; ++k) { data.positions[i*4+k] = positions[representatives[i]]; data.corners[i*4+k] = corners[k]; }
                        int v = i * 4, t = i * 6;
                        data.indices[t] = v; data.indices[t+1] = v+1; data.indices[t+2] = v+2; data.indices[t+3] = v; data.indices[t+4] = v+2; data.indices[t+5] = v+3;
                    }
                    return data;
                };
            }, data => {
                cache.vertices = new Mesh { name = "Viewport vertices", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                cache.vertices.vertices = data.positions; cache.vertices.uv = data.corners;
                cache.vertices.SetIndices(data.indices, MeshTopology.Triangles, 0); Repaint?.Invoke();
            });
        }

        static bool MatchesCurrentItem(Hit hit, IReadOnlyList<MeshViewport3D.Item> items) =>
            hit.item >= 0 && hit.item < items.Count && hit.Matches(items[hit.item],hit.item);

        void DrawHit(MeshViewport3D viewport, MeshViewport3D.Item item, int itemIndex, ViewportTopology data, Hit hit, Color color, ref Mesh face)
        {
            if (!hit.Matches(item,itemIndex)) return;
            if (Mode == Element.Vertex) viewport.DrawPoints(new[] { data.positions[hit.index] }, item.matrix, color, ViewportHighlight.VertexSize);
            else if (Mode == Element.Polygon) {
                var p = new[] { data.positions[data.triangles[hit.index*3]], data.positions[data.triangles[hit.index*3+1]], data.positions[data.triangles[hit.index*3+2]] };
                if (!face) face = new Mesh { name = "Viewport polygon highlight", hideFlags = HideFlags.HideAndDontSave };
                face.vertices = p; face.triangles = new[] { 0,1,2 };
                viewport.DrawHighlightMesh(face, item.matrix, ViewportHighlight.Fill(color));
                viewport.DrawLines(new[] { p[0],p[1],p[1],p[2],p[2],p[0] }, item.matrix, color, ViewportHighlight.ActiveWidth);
            }
            else if (Mode == Element.Off) DrawRim(viewport, item.matrix, data, hit.index, color);
            else {
                var edge = data.edges[hit.index];
                viewport.DrawLines(new[] { data.positions[edge.a],data.positions[edge.b] }, item.matrix, color, ViewportHighlight.ActiveWidth);
                viewport.DrawPoints(new[] { data.positions[edge.a],data.positions[edge.b] }, item.matrix, color, ViewportHighlight.VertexSize);
            }
        }
        static void DrawRim(MeshViewport3D viewport, Matrix4x4 matrix, ViewportTopology data, int rim, Color color)
        {
            if (rim < 0 || rim >= data.rims.Length) return;
            var pairs = new List<Vector3>();
            foreach (int i in data.rims[rim].edges) { pairs.Add(data.positions[data.edges[i].a]); pairs.Add(data.positions[data.edges[i].b]); }
            viewport.DrawLines(pairs, matrix, color, ViewportHighlight.ActiveWidth);
            viewport.DrawPoints(pairs, matrix, color, ViewportHighlight.VertexSize);
        }
        void InvalidateMesh(Mesh mesh)
        {
            if (ReferenceEquals(mesh, null)) return;
            if (caches.TryGetValue(mesh, out var cache)) { cache.Dispose(); caches.Remove(mesh); }
            if (hover.mesh == mesh) hover = default;
            if (selected.mesh == mesh) selected = default;
            selectedRim = -1;
        }
        internal void Clear()
        {
            foreach (var mesh in caches.Keys.ToArray()) InvalidateMesh(mesh);
            shownRims.Clear();
        }
        public void Dispose()
        {
            VertexChannels.Changed -= InvalidateMesh; Clear();
            if (hoverFace) Object.DestroyImmediate(hoverFace);
            if (selectedFace) Object.DestroyImmediate(selectedFace);
        }
    }
}
