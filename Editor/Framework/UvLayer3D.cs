// UvLayer3D.cs — the UV viewer laid over the model in the 3D canvas: the active fill
// mode, borders and preview background rendered per mesh into a UV-space texture
// and drawn through the preview UV channel; the canvas's wire toggle as true 3D
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
        sealed class Layer { public RenderTexture texture; public string key; }

        public int LayerSize = 1024;
        static readonly Color HoverColor = new Color(.25f, 1f, .95f, .35f);
        static readonly Color SelectedColor = new Color(1f, .95f, .2f, .4f);
        static readonly Color WireShaded = new Color(0.05f, 0.05f, 0.05f, 1f);

        readonly Dictionary<int, Layer> layers = new Dictionary<int, Layer>();
        readonly Dictionary<int, TriangleBvh> bvhs = new Dictionary<int, TriangleBvh>();
        readonly Dictionary<long, Mesh> shellMeshes = new Dictionary<long, Mesh>();
        Material overlay;

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
            (!canvas.FillHidden && canvas.FillModes.Count > 0) || canvas.ShowBorder || canvas.CheckerEnabled ||
            canvas.CurrentPreviewMode != UvCanvasView.PreviewMode.Off;

        static string LayerKey(UvCanvasView canvas, UvToolContext ctx, Mesh mesh, MeshEntry entry)
        {
            int selected = canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry ? canvas.SelectedShell.shellId : -1;
            return $"{mesh.GetInstanceID()}|{canvas.ActiveFillModeIndex}|{canvas.FillHidden}|{canvas.FillAlpha:F3}|{canvas.ShowBorder}|{canvas.CurrentPreviewMode}|" +
                   $"{canvas.CheckerEnabled}|{canvas.LmExposure:F2}|{ctx.PreviewUvChannel}|{(int)canvas.ValidationFilterMask}|{selected}";
        }

        /// <summary>Draws the UV viewer over every context item (an item without an entry is
        /// tool content and gets only the wire). Call from the viewport's overlay callback.</summary>
        public void Draw(MeshViewport3D view, UvCanvasView canvas, UvToolContext ctx,
            IReadOnlyList<MeshViewport3D.Item> items, IReadOnlyList<MeshEntry> entries)
        {
            bool layerVisible = LayerVisible(canvas) && EnsureMaterial();
            // Many meshes share the window's memory budget: smaller layers past two dozen.
            int size = items.Count > 24 ? 512 : LayerSize;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!item.mesh) continue;
                var entry = i < entries.Count ? entries[i] : null;
                // The status bar's Wire is the one wireframe toggle of both canvas modes,
                // tool content included; the UV layer and spot picking need an entry.
                if (canvas.ShowWireframe) view.DrawWire(item.mesh, item.matrix, WireShaded);
                if (entry == null) continue;
                if (layerVisible)
                {
                    int id = item.mesh.GetInstanceID();
                    string key = LayerKey(canvas, ctx, item.mesh, entry);
                    if (!layers.TryGetValue(id, out var layer)) layers[id] = layer = new Layer();
                    if (layer.key != key || layer.texture == null)
                    {
                        layer.texture = canvas.RenderUvLayer(ctx, item.mesh, entry, layer.texture, size);
                        layer.key = key;
                    }
                    if (layer.texture) view.DrawTextured(item.mesh, item.matrix, overlay, layer.texture, Color.white, ctx.PreviewUvChannel);
                }
                if (canvas.SpotMode && EnsureMaterial())
                {
                    if (canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry)
                        DrawShell(view, canvas, ctx, item, canvas.SelectedShell.shellId, SelectedColor);
                    if (canvas.HasHoveredShell && canvas.HoveredShell.meshEntry == entry &&
                        !(canvas.HasSelectedShell && canvas.SelectedShell.meshEntry == entry && canvas.SelectedShell.shellId == canvas.HoveredShell.shellId))
                        DrawShell(view, canvas, ctx, item, canvas.HoveredShell.shellId, HoverColor);
                }
            }
        }

        void DrawShell(MeshViewport3D view, UvCanvasView canvas, UvToolContext ctx, MeshViewport3D.Item item, int shellId, Color color)
        {
            var mesh = ShellMesh(canvas, ctx, item.mesh, shellId);
            if (mesh) view.DrawTextured(mesh, item.matrix, overlay, null, color, 0);
        }

        // The faces of one shell as a mesh of their own (positions only), cached.
        Mesh ShellMesh(UvCanvasView canvas, UvToolContext ctx, Mesh source, int shellId)
        {
            long key = ((long)source.GetInstanceID() << 20) ^ (uint)shellId ^ ((long)ctx.PreviewUvChannel << 40);
            if (shellMeshes.TryGetValue(key, out var cached) && cached) return cached;
            var cache = canvas.GetPreviewShellCache(ctx, source, ctx.PreviewUvChannel);
            if (cache == null || !cache.shellById.TryGetValue(shellId, out var shell)) return null;
            var vertices = source.vertices;
            var indices = new List<int>(shell.faceIndices.Count * 3);
            foreach (int f in shell.faceIndices)
            {
                int t0 = f * 3;
                if (t0 + 2 >= cache.triangles.Length) continue;
                indices.Add(cache.triangles[t0]); indices.Add(cache.triangles[t0 + 1]); indices.Add(cache.triangles[t0 + 2]);
            }
            var mesh = new Mesh { name = source.name + "_Shell" + shellId, hideFlags = HideFlags.HideAndDontSave,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            shellMeshes[key] = mesh;
            return mesh;
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
            for (int i = 0; i < items.Count && i < entries.Count; i++)
            {
                var item = items[i];
                if (!item.mesh || entries[i] == null) continue;
                var toLocal = item.matrix.inverse;
                Vector3 localOrigin = toLocal.MultiplyPoint3x4(origin);
                Vector3 localDir = toLocal.MultiplyVector(direction);
                float scale = localDir.magnitude;
                if (scale < 1e-12f) continue;
                localDir /= scale;
                var bvh = BvhOf(canvas, item.mesh);
                if (bvh == null) continue;
                var ray = bvh.Raycast(localOrigin, localDir, float.MaxValue);
                if (ray.triangleIndex < 0) continue;
                Vector3 hitWorld = item.matrix.MultiplyPoint3x4(localOrigin + localDir * ray.t);
                float distance = (hitWorld - origin).magnitude;
                if (distance < bestDistance) { bestDistance = distance; bestItem = i; bestFace = ray.triangleIndex; bestBary = ray.barycentric; world = hitWorld; }
            }
            if (bestItem < 0) return false;
            var mesh = items[bestItem].mesh; var entry = entries[bestItem];
            var uvs = canvas.RdUvCached(mesh, ctx.PreviewUvChannel);
            var tris = canvas.GetTrianglesCached(mesh);
            if (uvs == null || tris == null || bestFace * 3 + 2 >= tris.Length) return false;
            int a = tris[bestFace * 3], b = tris[bestFace * 3 + 1], c = tris[bestFace * 3 + 2];
            if (a >= uvs.Length || b >= uvs.Length || c >= uvs.Length) return false;
            Vector2 uv = uvs[a] * bestBary.x + uvs[b] * bestBary.y + uvs[c] * bestBary.z;
            var cache = canvas.GetPreviewShellCache(ctx, mesh, ctx.PreviewUvChannel);
            int shellId = -1; UvShell shell = null;
            if (cache != null && cache.faceToShell.TryGetValue(bestFace, out shellId)) cache.shellById.TryGetValue(shellId, out shell);
            hit = new ShellUvHit { meshEntry = entry, shellId = shellId, faceIndex = bestFace, uvHit = uv, barycentric = bestBary };
            debug = shell != null ? canvas.MakeHit(ctx, entry, mesh, shell, uv) : null;
            return true;
        }

        TriangleBvh BvhOf(UvCanvasView canvas, Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (bvhs.TryGetValue(id, out var bvh)) return bvh;
            var tris = canvas.GetTrianglesCached(mesh);
            bvh = tris != null && tris.Length >= 3 ? new TriangleBvh(mesh.vertices, tris) : null;
            bvhs[id] = bvh;
            return bvh;
        }

        /// <summary>Drops everything derived from the meshes (call when the mesh entries change).</summary>
        public void Invalidate()
        {
            foreach (var layer in layers.Values) if (layer.texture) { layer.texture.Release(); Object.DestroyImmediate(layer.texture); }
            layers.Clear();
            bvhs.Clear();
            foreach (var mesh in shellMeshes.Values) if (mesh) Object.DestroyImmediate(mesh);
            shellMeshes.Clear();
        }

        public void Dispose()
        {
            Invalidate();
            if (overlay) Object.DestroyImmediate(overlay);
            overlay = null;
        }
    }
}
