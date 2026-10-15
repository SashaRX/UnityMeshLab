// UvLayer3D.cs — the UV viewer laid over the model in the 3D canvas: the active fill
// mode and preview background rendered per mesh into a UV-space texture
// and drawn through the preview UV channel; UV borders and wire as true 3D
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
                return token => {
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
                };
            }, data => { cached.mesh = data.Upload(); repaint?.Invoke(); });
            return null;
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
