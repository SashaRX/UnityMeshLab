// UvLayer3D.cs — the UV viewer laid over the model in the 3D canvas: the active fill
// mode and preview background. Shell colours use cached face geometry; other
// modes use a UV-space texture through the preview channel. Borders and wire are 3D
// lines; spot-mode picking (ray → face → shell) feeding the same hover/selection
// state the UV canvas and the tools read; hovered/selected shells highlighted on
// the surface.

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    public sealed class UvLayer3D : IDisposable
    {
        readonly struct LayerState : IEquatable<LayerState>
        {
            readonly (object data, int colors, int buffers, int fill, bool hidden, float alpha, int mode, bool checker,
                bool colorChecker, bool r, bool g, float exposure, int channel, int mask, int selected, int size) state;
            internal LayerState(UvCanvasView canvas, UvToolContext ctx, Mesh mesh, MeshEntry entry, int size)
            {
                state = (UvCanvasView.PreparedShells(ctx, mesh, ctx.PreviewUvChannel), canvas.ShellColorRevision(entry, ctx.PreviewUvChannel), canvas.DrawBufferRevision,
                    canvas.ActiveFillModeIndex, canvas.FillHidden, canvas.FillAlpha, (int)canvas.CurrentPreviewMode,
                    canvas.CheckerEnabled, canvas.CheckerColorMode, canvas.CheckerShowR, canvas.CheckerShowG, canvas.LmExposure,
                    ctx.PreviewUvChannel, (int)canvas.ValidationFilterMask,
                    canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry ? canvas.SelectedShell.shellId : -1, size);
            }
            public bool Equals(LayerState other) => state.Equals(other.state);
        }
        sealed class Layer { public RenderTexture texture; public LayerState key; }
        sealed class FillPreview : IDisposable
        {
            internal Mesh mesh;
            internal (PreviewShellData data, object colors, int channel, int selected, bool solid, object issues, int mask) key;
            internal readonly PreviewWork<FillData> work = new PreviewWork<FillData>("Shell fill");
            public void Dispose() { work.Dispose(); if (mesh) Object.DestroyImmediate(mesh); }
        }
        internal sealed class FillData
        {
            internal readonly List<Vector3> positions = new List<Vector3>();
            internal readonly List<Color> colors = new List<Color>();
            internal readonly List<Vector4> validation = new List<Vector4>();
            internal readonly List<Vector3> neighbors = new List<Vector3>(), barycentrics = new List<Vector3>(), normals = new List<Vector3>();
            internal readonly List<int> indices = new List<int>();
            internal void AppendFace(int[] triangles, Vector3[] sourcePositions, int face, Color color, Color issue,
                (Vector3[] neighbors, Vector3[] normals) seams, Dictionary<(int, Color, Color), int> vertices)
            {
                var neighbor = seams.neighbors[face];
                bool antialias = neighbor.x >= 0 || neighbor.y >= 0 || neighbor.z >= 0;
                for (int corner = 0; corner < 3; ++corner) {
                    int index = triangles[face * 3 + corner];
                    var key = (index, color, issue);
                    int compact;
                    if (antialias || !vertices.TryGetValue(key, out compact)) {
                        compact = positions.Count; if (!antialias) vertices.Add(key, compact);
                        positions.Add(sourcePositions[index]); colors.Add(color); validation.Add(issue);
                        neighbors.Add(neighbor); normals.Add(antialias ? seams.normals[face] : Vector3.zero);
                        var barycentric = Vector3.zero;
                        if (antialias) barycentric[corner] = 1;
                        barycentrics.Add(barycentric);
                    }
                    indices.Add(compact);
                }
            }
            internal Mesh Upload()
            {
                var mesh = new Mesh { name = "MeshLab shell fill", hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                try {
                    mesh.SetVertices(positions); mesh.SetColors(colors); mesh.SetNormals(normals);
                    mesh.SetUVs(1, validation); mesh.SetUVs(2, neighbors); mesh.SetUVs(3, barycentrics); mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                    mesh.UploadMeshData(true); return mesh;
                }
                catch { Object.DestroyImmediate(mesh); throw; }
            }
        }
        sealed class ShellPreview : IDisposable
        {
            internal Mesh mesh;
            internal PreviewShellData data;
            internal int shell;
            internal readonly PreviewWork<ShellData> work = new PreviewWork<ShellData>("Shell highlight");
            public void Dispose() { work.Dispose(); if (mesh) Object.DestroyImmediate(mesh); }
        }
        internal sealed class ShellData
        {
            internal readonly List<Vector3> positions = new List<Vector3>();
            internal readonly List<int> indices = new List<int>();
            internal Mesh Upload()
            {
                var mesh = new Mesh { name = "MeshLab compact shell highlight", hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                try { mesh.SetVertices(positions); mesh.SetIndices(indices, MeshTopology.Triangles, 0); mesh.UploadMeshData(true); return mesh; }
                catch { Object.DestroyImmediate(mesh); throw; }
            }
        }

        public int LayerSize = 1024;

        readonly Dictionary<(int, MeshEntry), Layer> layers = new Dictionary<(int, MeshEntry), Layer>();
        readonly Dictionary<(int, MeshEntry), FillPreview> fills = new Dictionary<(int, MeshEntry), FillPreview>();
        readonly Dictionary<(int, int, bool), ShellPreview> shellMeshes = new Dictionary<(int, int, bool), ShellPreview>();
        readonly Dictionary<long, Mesh> boundaryMeshes = new Dictionary<long, Mesh>();
        Material overlay;

        public UvLayer3D() { VertexChannels.Changed += InvalidateMesh; }
        void InvalidateMesh(Mesh mesh) => Invalidate();

        bool EnsureMaterial()
        {
            if (overlay) return true;
            var shader = Shader.Find("Hidden/MeshLab/UvOverlay");
            if (!shader) return false;
            overlay = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            overlay.SetFloat("_Premultiplied", 1);
            return true;
        }

        /// <summary>True when any part of the UV viewer would show on the model.</summary>
        static bool LayerVisible(UvCanvasView canvas) =>
            (!canvas.FillHidden && canvas.FillModes.Count > 0) || canvas.CheckerEnabled ||
            canvas.CurrentPreviewMode != UvCanvasView.PreviewMode.Off;

        /// <summary>Draws the UV viewer over every context item (an item without an entry is
        /// tool content and gets only the wire). Call from the viewport's overlay callback.</summary>
        public void Draw(MeshViewport3D view, UvCanvasView canvas, UvToolContext ctx,
            IReadOnlyList<MeshViewport3D.Item> items, IReadOnlyList<MeshEntry> entries)
        {
            bool layerVisible = view.Mode == MeshViewport3D.Shading.Shaded && LayerVisible(canvas) && EnsureMaterial();
            // Many meshes share the window's memory budget: smaller layers past two dozen.
            int size = items.Count > 24 ? 512 : LayerSize;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!item.mesh) continue;
                var entry = i < entries.Count ? entries[i] : null;
                // The status bar's Wire is the one wireframe toggle of both canvas modes,
                // tool content included; the UV layer and spot picking need an entry.
                if (canvas.ShowWireframe) view.DrawWire(item.mesh, item.matrix, view.WireColor);
                if (entry == null) continue;
                if (layerVisible && UvTopology.HasUv(item.mesh, ctx.PreviewUvChannel) && MeshInspection.HasOnlyTriangles(item.mesh))
                {
                    bool geometryFill = canvas.GeometryShellFill;
                    if (geometryFill)
                    {
                        var id = (item.mesh.GetInstanceID(), entry);
                        if (layers.TryGetValue(id, out var previous)) {
                            if (previous.texture) { previous.texture.Release(); Object.DestroyImmediate(previous.texture); }
                            layers.Remove(id);
                        }
                        var fill = FillMesh(canvas, ctx, item.mesh, entry, view.RequestRepaint);
                        float alpha = canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Shells3D ? 1 : Mathf.Clamp01(canvas.FillAlpha);
                        if (fill) view.DrawShellFillMesh(fill, item.matrix, alpha);
                    }
                    else DrawTextureLayer(view, canvas, ctx, item, entry, size);
                }
                if (canvas.ShowBorder) {
                    long boundaryKey = ((long)item.mesh.GetInstanceID() << 8) ^ (uint)ctx.PreviewUvChannel;
                    if (!boundaryMeshes.TryGetValue(boundaryKey, out var boundary)) {
                        var pairs = canvas.GetPreviewBoundary(ctx, item.mesh, ctx.PreviewUvChannel);
                        if (pairs != null) boundaryMeshes[boundaryKey] = boundary = BuildBoundaryMesh(item.mesh, pairs);
                    }
                    if (boundary) view.DrawLineMesh(boundary, item.matrix, ViewportHighlight.Border, ViewportHighlight.EdgeWidth);
                }
                if (canvas.SpotMode && EnsureMaterial())
                {
                    if (canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry)
                        DrawShell(view, canvas, ctx, item, canvas.SelectedShell.shellId, ViewportHighlight.Fill(ViewportHighlight.Selected), true);
                    if (canvas.HasHoveredShell && canvas.HoveredShell.meshEntry == entry &&
                        !(canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry && canvas.SelectedShell.shellId == canvas.HoveredShell.shellId))
                        DrawShell(view, canvas, ctx, item, canvas.HoveredShell.shellId, ViewportHighlight.Fill(ViewportHighlight.Hover), false);
                }
            }
        }

        void DrawTextureLayer(MeshViewport3D view, UvCanvasView canvas, UvToolContext ctx, MeshViewport3D.Item item, MeshEntry entry, int size)
        {
            var id = (item.mesh.GetInstanceID(), entry);
            var key = new LayerState(canvas, ctx, item.mesh, entry, size);
            if (!layers.TryGetValue(id, out var layer)) layers[id] = layer = new Layer();
            if (!layer.key.Equals(key) || layer.texture == null)
            {
                layer.texture = canvas.RenderUvLayer(ctx, item.mesh, entry, layer.texture, size, false, view.SceneLightmaps);
                layer.key = key;
            }
            var uvScaleOffset = canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Lightmap &&
                                ctx.PreviewUvChannel == 1 && entry.renderer && entry.renderer.lightmapIndex >= 0
                ? entry.renderer.lightmapScaleOffset : new Vector4(1, 1, 0, 0);
            if (layer.texture) view.DrawTextured(item.mesh, item.matrix, overlay, layer.texture, Color.white, ctx.PreviewUvChannel, uvScaleOffset);
        }

        internal Mesh FillMesh(UvCanvasView canvas, UvToolContext ctx, Mesh source, MeshEntry entry, Action repaint)
        {
            var data = canvas.GetPreviewShellCache(ctx, source, ctx.PreviewUvChannel);
            if (data == null) return null;
            var colors = canvas.ShellColorKeys(ctx, source, data, entry, out bool ready);
            if (!ready) return null;
            bool solid = canvas.CurrentPreviewMode == UvCanvasView.PreviewMode.Shells3D;
            int selected = !solid && canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry ? canvas.SelectedShell.shellId : -1;
            var issues = solid ? null : entry.validationReport?.perTriangle;
            var key = (data, (object)colors, ctx.PreviewUvChannel, selected, solid, (object)issues, (int)canvas.ValidationFilterMask);
            var id = (source.GetInstanceID(), entry);
            if (fills.TryGetValue(id, out var cached) && cached.key.Equals(key)) return cached.mesh;
            cached?.Dispose(); cached = new FillPreview {key = key}; fills[id] = cached;
            var result = cached;
            result.work.Enqueue(() => {
                var positions = data.positions ?? source.vertices;
                var mask = canvas.ValidationFilterMask;
                return token => BuildFillData(data, positions, colors, selected, solid, token, issues, mask);
            }, built => { result.mesh = built.Upload(); repaint?.Invoke(); });
            return null;
        }

        internal static FillData BuildFillData(PreviewShellData data, Vector3[] positions, Dictionary<int, int> colors,
            int selected, bool solid, System.Threading.CancellationToken token = default,
            TransferValidator.TriIssue[] issues = null, TransferValidator.TriIssue mask = TransferValidator.TriIssue.None)
        {
            var styles = PrepareFillStyles(data, colors, selected, solid, token, issues, mask);
            var seams = ShellFillSeams.Build(data.triangles,positions,styles,token);
            return BuildFillGeometry(data, positions, colors, selected, solid, token, issues, mask, seams);
        }

        static int ShellColorKey(UvShell shell, Dictionary<int, int> colors) =>
            colors != null && colors.TryGetValue(shell.shellId, out int mapped) ? mapped : shell.shellId;

        static int[] PrepareFillStyles(PreviewShellData data, Dictionary<int, int> colors, int selected, bool solid,
            System.Threading.CancellationToken token, TransferValidator.TriIssue[] issues, TransferValidator.TriIssue mask)
        {
            var styles = new int[data.triangles.Length/3];
            for (int i = 0; i < styles.Length; ++i) styles[i] = -1;
            int prepared = 0;
            foreach (var shell in data.shells) {
                int key = ShellColorKey(shell, colors);
                int style = UvHashUtil.NonNegativeColorKey(key) % UvCanvasView.pal.Length;
                if (shell.shellId == selected) style += 16 + (solid ? 0 : 32);
                PrepareShellStyles(shell, styles, style, ref prepared, token, issues, mask);
                if (prepared >= UvCanvasView.MAX_TRI) break;
            }
            return styles;
        }

        static void PrepareShellStyles(UvShell shell, int[] styles, int style, ref int prepared,
            System.Threading.CancellationToken token, TransferValidator.TriIssue[] issues, TransferValidator.TriIssue mask)
        {
            foreach (int face in shell.faceIndices) {
                token.ThrowIfCancellationRequested();
                if (prepared++ >= UvCanvasView.MAX_TRI) break;
                int issue = issues != null && face < issues.Length ? UvCanvasView.ValidationOverlayIndex(issues[face],mask) : 0;
                styles[face] = style + issue * 64;
            }
        }

        static FillData BuildFillGeometry(PreviewShellData data, Vector3[] positions, Dictionary<int, int> colors,
            int selected, bool solid, System.Threading.CancellationToken token, TransferValidator.TriIssue[] issues,
            TransferValidator.TriIssue mask, (Vector3[] neighbors, Vector3[] normals) seams)
        {
            var result = new FillData(); var vertices = new Dictionary<(int, Color, Color), int>();
            int total = 0;
            foreach (var shell in data.shells) {
                Color color = FillColor(shell, colors, selected, solid);
                foreach (int face in shell.faceIndices) {
                    token.ThrowIfCancellationRequested();
                    if (total++ >= UvCanvasView.MAX_TRI) return result;
                    var issue = issues != null && face < issues.Length ? UvCanvasView.ValidationOverlayColor(issues[face], mask) : Color.clear;
                    result.AppendFace(data.triangles, positions, face, color, issue, seams, vertices);
                }
            }
            return result;
        }

        static Color FillColor(UvShell shell, Dictionary<int, int> colors, int selected, bool solid)
        {
            int colorKey = ShellColorKey(shell, colors);
            Color color = UvCanvasView.pal[UvHashUtil.NonNegativeColorKey(colorKey) % UvCanvasView.pal.Length];
            if (shell.shellId == selected) color = Color.Lerp(color, Color.white, .45f);
            color.a = !solid && shell.shellId == selected ? 1.85f : 1;
            return color;
        }

        void DrawShell(MeshViewport3D view, UvCanvasView canvas, UvToolContext ctx, MeshViewport3D.Item item, int shellId, Color color, bool selected)
        {
            var mesh = ShellMesh(canvas, ctx, item.mesh, shellId, selected, view.RequestRepaint);
            if (mesh) view.DrawHighlightMesh(mesh, item.matrix, color);
        }

        internal static Mesh BuildBoundaryMesh(Mesh source, int channel)
        {
            if (!MeshInspection.HasOnlyTriangles(source)) return null;
            return BuildBoundaryMesh(source, UvTopology.UvBoundaryEdgePairs(source, channel));
        }

        static Mesh BuildBoundaryMesh(Mesh source, int[] pairs)
        {
            if (pairs.Length == 0) return null;
            var mesh = new Mesh { name = source.name + "_UvBoundary", hideFlags = HideFlags.HideAndDontSave,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = source.vertices;
            mesh.SetIndices(pairs, MeshTopology.Lines, 0);
            return mesh;
        }

        // The faces of one shell as a mesh of their own (positions only), cached.
        internal Mesh ShellMesh(UvCanvasView canvas, UvToolContext ctx, Mesh source, int shellId, bool selected, Action repaint)
        {
            var key = (source.GetInstanceID(), ctx.PreviewUvChannel, selected);
            var cache = canvas.GetPreviewShellCache(ctx, source, ctx.PreviewUvChannel);
            if (cache == null || !cache.shellById.TryGetValue(shellId, out var shell)) return null;
            if (shellMeshes.TryGetValue(key, out var cached) && cached.data == cache && cached.shell == shellId) return cached.mesh;
            cached?.Dispose();
            cached = new ShellPreview { data = cache, shell = shellId }; shellMeshes[key] = cached;
            cached.work.Enqueue(() => {
                var positions = cache.positions ?? source.vertices;
                return token => BuildShellData(cache, shell, positions, token);
            }, data => { cached.mesh = data.Upload(); repaint?.Invoke(); });
            return null;
        }

        static ShellData BuildShellData(PreviewShellData cache, UvShell shell, Vector3[] positions, System.Threading.CancellationToken token)
        {
            var data = new ShellData(); var remap = new Dictionary<int, int>();
            foreach (int f in shell.faceIndices) {
                token.ThrowIfCancellationRequested();
                for (int c = 0; c < 3; ++c) {
                    int index = cache.triangles[f * 3 + c];
                    if (!remap.TryGetValue(index, out int compact)) {
                        compact = data.positions.Count; remap.Add(index, compact); data.positions.Add(positions[index]);
                    }
                    data.indices.Add(compact);
                }
            }
            return data;
        }

        /// <summary>The face under a GUI point: the nearest triangle of any context item
        /// along the viewport's camera ray, with its shell for the preview UV channel.</summary>
        public bool Pick(MeshViewport3D view, UvCanvasView canvas, UvToolContext ctx, Vector2 guiPoint,
            IReadOnlyList<MeshViewport3D.Item> items, IReadOnlyList<MeshEntry> entries,
            out ShellUvHit hit, out UvCanvasView.ShellDebugHit debug, out Vector3 world)
        {
            hit = default; debug = null; world = Vector3.zero;
            if (!view.TryScreenRay(guiPoint, out var origin, out var direction)) return false;
            float bestDistance = float.MaxValue; int bestItem = -1, bestFace = -1; Vector3 bestBary = Vector3.zero;
            bool pending = false;
            for (int i = 0; i < items.Count && i < entries.Count; i++)
            {
                var item = items[i];
                if (!item.mesh || item.WireOnly || entries[i] == null || !MeshInspection.HasOnlyTriangles(item.mesh) || !UvTopology.HasUv(item.mesh, ctx.PreviewUvChannel)) continue;
                var toLocal = item.matrix.inverse;
                Vector3 localOrigin = toLocal.MultiplyPoint3x4(origin);
                Vector3 localDir = toLocal.MultiplyVector(direction);
                float scale = localDir.magnitude;
                if (scale < 1e-12f) continue;
                localDir /= scale;
                var bvh = canvas.GetPreviewBvh(ctx, item.mesh, ctx.PreviewUvChannel);
                if (bvh == null || canvas.GetPreviewShellCache(ctx, item.mesh, ctx.PreviewUvChannel) == null) {
                    long previewKey = ((long)item.mesh.GetInstanceID() << 8) ^ (uint)ctx.PreviewUvChannel;
                    // A cached null is a completed, unusable result, not a pending worker.
                    if (!ctx.PreviewShellDataCache.ContainsKey(previewKey)) pending = true;
                    continue;
                }
                var ray = bvh.Raycast(localOrigin, localDir, float.MaxValue);
                if (ray.triangleIndex < 0) continue;
                Vector3 hitWorld = item.matrix.MultiplyPoint3x4(localOrigin + localDir * ray.t);
                float distance = (hitWorld - origin).magnitude;
                if (distance < bestDistance) { bestDistance = distance; bestItem = i; bestFace = ray.triangleIndex; bestBary = ray.barycentric; world = hitWorld; }
            }
            // A pending foreground mesh must not let a click lock an occluded rear mesh.
            if (pending || bestItem < 0) return false;
            var mesh = items[bestItem].mesh; var entry = entries[bestItem];
            var uvs = canvas.RdUvCached(mesh, ctx.PreviewUvChannel);
            var tris = canvas.GetTrianglesCached(mesh);
            if (uvs == null || tris == null || bestFace * 3 + 2 >= tris.Length) return false;
            int a = tris[bestFace * 3], b = tris[bestFace * 3 + 1], c = tris[bestFace * 3 + 2];
            if (a >= uvs.Length || b >= uvs.Length || c >= uvs.Length) return false;
            Vector2 uv = uvs[a] * bestBary.x + uvs[b] * bestBary.y + uvs[c] * bestBary.z;
            var cache = canvas.GetPreviewShellCache(ctx, mesh, ctx.PreviewUvChannel);
            if (cache == null || !cache.faceToShell.TryGetValue(bestFace, out int shellId) ||
                !cache.shellById.TryGetValue(shellId, out var shell)) return false;
            hit = new ShellUvHit { meshEntry = entry, shellId = shellId, faceIndex = bestFace, uvHit = uv, barycentric = bestBary };
            debug = shell != null ? canvas.MakeHit(ctx, entry, mesh, shell, uv) : null;
            return true;
        }

        /// <summary>Drops everything derived from the meshes (call when the mesh entries change).</summary>
        public void Invalidate()
        {
            foreach (var fill in fills.Values) fill.Dispose();
            fills.Clear();
            foreach (var layer in layers.Values) if (layer.texture) { layer.texture.Release(); Object.DestroyImmediate(layer.texture); }
            layers.Clear();
            foreach (var mesh in shellMeshes.Values) mesh.Dispose();
            shellMeshes.Clear();
            foreach (var mesh in boundaryMeshes.Values) if (mesh) Object.DestroyImmediate(mesh);
            boundaryMeshes.Clear();
        }

        public void Dispose()
        {
            VertexChannels.Changed -= InvalidateMesh;
            Invalidate();
            if (overlay) Object.DestroyImmediate(overlay);
            overlay = null;
        }
    }
}
