// MeshViewport3D.cs — the shared 3D viewport of Mesh Lab: an orbitable, lit view of
// any set of meshes rendered off-screen into the canvas area. Owned by the hub and
// offered to every tool through IUvTool3D: a tool can hand it content (what to show)
// and overlays (wire, lines, points on top), while the hub keeps the camera, the
// shading modes and the input in one place.

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    public sealed class MeshViewport3D : IDisposable
    {
        /// <summary>How the content's surfaces are coloured.</summary>
        public enum Shading { Shaded, VertexColors, Normals, Tangents, UV0, UV1, UV2, UV3,
            UV4, UV5, UV6, UV7, Positions, TangentSign, ColorAlpha, BoneWeights, BoneIndices }
        public enum Projection { Perspective, XY, XZ, YZ }
        internal enum UpAxis { X, Y, Z }

        /// <summary>One mesh to show: its matrix (any common space) and the materials the
        /// Shaded mode draws it with (null or short arrays fall back to a lit grey).</summary>
        public struct Item
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material[] materials;
            public Item(Mesh mesh, Matrix4x4 matrix, Material[] materials = null) { this.mesh = mesh; this.matrix = matrix; this.materials = materials; }
        }

        public static readonly string[] ShadingNames = { "Surface", "Vertex colors", "Normals", "Tangents", "UV0", "UV1", "UV2", "UV3",
            "UV4", "UV5", "UV6", "UV7", "Positions", "Tangent sign", "Color alpha", "Dominant bone weight", "Dominant bone index" };

        public Shading Mode = Shading.Shaded;
        public Projection ViewProjection;
        internal UpAxis Up = UpAxis.Y;
        public bool Wireframe;
        public bool Lit = true;
        public bool ShowGrid = true, ShowAxes = true;
        public Color Background = new Color(0.16f, 0.19f, 0.24f, 1f);
        public Action RequestRepaint;
        /// <summary>The wire colour that reads on the current shading: near-black on the lit
        /// grey (or scene materials) of Shaded, light on the attribute encodings.</summary>
        public Color WireColor => Mode == Shading.Shaded ? new Color(0.05f, 0.05f, 0.05f, 1f) : new Color(0.4f, 0.85f, 1f, 1f);
        // MSAA smooths surface silhouettes; line coverage is handled by PreviewLines.
        // Internal override also lets render regressions exercise 1x/2x/4x identically.
        internal int MsaaSamples = 4;

        // ── camera ──
        Vector3 pivot;
        Vector2 orbit = new Vector2(-135f, 20f);   // yaw, pitch (degrees)
        float distance = 5f, radius = 1f;
        bool frameRequested;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int UseVertexColorId = Shader.PropertyToID("_UseVertexColor");

        // The orbit stores the drag: x turns around the vertical axis (yaw), y tilts (pitch).
        Quaternion OrbitRotation()
        {
            if (ViewProjection == Projection.XY) return Quaternion.identity;
            if (ViewProjection == Projection.XZ) return Quaternion.Euler(90, 0, 0);
            if (ViewProjection == Projection.YZ) return Quaternion.Euler(0, -90, 0);
            float pitch = orbit.y, yaw = orbit.x;
            return Quaternion.FromToRotation(Vector3.up, UpDirection) * Quaternion.Euler(pitch, yaw, 0f);
        }

        internal Vector3 UpDirection => Up == UpAxis.X ? Vector3.right : Up == UpAxis.Z ? Vector3.forward : Vector3.up;

        // Planar views keep their named coordinate plane; only the perspective
        // floor follows the chosen vertical axis.
        internal Projection GridProjection => ViewProjection != Projection.Perspective ? ViewProjection :
            Up == UpAxis.X ? Projection.YZ : Up == UpAxis.Z ? Projection.XY : Projection.XZ;

        internal Vector3 GridPoint(Bounds bounds, float x, float y)
        {
            float offset = radius * .002f;
            if (GridProjection == Projection.XY)
                return new Vector3(x, y, ViewProjection == Projection.Perspective ? bounds.min.z - offset : bounds.max.z + offset);
            if (GridProjection == Projection.YZ) return new Vector3(bounds.min.x - offset, y, x);
            return new Vector3(x, bounds.min.y - offset, y);
        }

        PreviewRenderUtility utility;
        Material surface, flat, wire, points;
        RenderTexture offscreen;   // owned; replaced only when the preview target's size, format or sample count changes
        Rect currentRect;
        bool drawing;
        readonly Dictionary<long, Mesh> encodedCache = new Dictionary<long, Mesh>();
        sealed class WirePreview
        {
            public Mesh mesh;
            public int vertexCount;
            public readonly PreviewWork<WireData> work = new PreviewWork<WireData>("[3D] Wire preview");
        }
        sealed class WireData { public Vector3[] vertices; public List<int> indices; public string name; }
        readonly Dictionary<int, WirePreview> wireCache = new Dictionary<int, WirePreview>();
        readonly PreviewLines lineRibbons = new PreviewLines();
        readonly List<Mesh> frameMeshes = new List<Mesh>();   // transient meshes built for this frame

        public MeshViewport3D() { VertexChannels.Changed += InvalidateMesh; }

        public Rect LastRect => currentRect;
        public Camera Camera => utility?.camera;
        internal LightmapData[] SceneLightmaps { get; private set; }

        // GUILayout supplies a placeholder during Layout. Keep the usable rect for
        // async Spot completion, and refresh it before picking an input event.
        internal void PrepareRect(Rect rect, EventType eventType)
        {
            if (eventType != EventType.Layout) currentRect = rect;
        }

        // ═══════════════════════════════════════════════════════════
        //  Drawing
        // ═══════════════════════════════════════════════════════════

        /// <summary>Handles input in rect, then (on Repaint) renders the items with the
        /// current shading, calls overlay for the tool's additions and blits the result.</summary>
        public void Draw(Rect rect, IReadOnlyList<Item> items, Action<MeshViewport3D> overlay)
        {
            PrepareRect(rect, Event.current.type);
            var bounds = BoundsOf(items, out bool any);
            FrameIfRequested(bounds, any);
            HandleInput(rect, any ? bounds : (Bounds?)null);
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, Background);
            if (!EnsureResources()) return;

            // BeginPreview switches to an isolated scene with its own empty lightmaps.
            // UV overlays need the authored scene's atlas references during that render.
            SceneLightmaps = LightmapSettings.lightmaps;
            utility.BeginPreview(rect, GUIStyle.none);
            var camera = utility.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.orthographic = ViewProjection != Projection.Perspective;
            camera.orthographicSize = distance * Mathf.Tan(15f * Mathf.Deg2Rad);
            var rotation = OrbitRotation();
            camera.transform.rotation = rotation;
            camera.transform.position = pivot - rotation * Vector3.forward * distance;
            camera.nearClipPlane = Mathf.Max(distance * 0.001f, 1e-4f);
            camera.farClipPlane = distance + radius * 8f + 1f;
            utility.lights[0].intensity = 1.1f; utility.lights[0].transform.rotation = rotation * Quaternion.Euler(30f, 30f, 0f);
            utility.lights[1].intensity = 0.6f; utility.lights[1].transform.rotation = rotation * Quaternion.Euler(-20f, -150f, 0f);
            utility.ambientColor = new Color(0.25f, 0.25f, 0.25f, 1f);

            drawing = true;
            lineRibbons.Repaint = RequestRepaint;
            lineRibbons.Prune();
            // The preview utility's own target has no anti-aliasing and a 16-bit depth
            // buffer: one-pixel lines come out jagged, and the wire fights the surface it
            // outlines. The frame renders into a 24-bit-depth target of the same size and
            // goes into the utility's, which EndPreview then shows: multisampled and
            // resolved under Built-in, or single-sampled and copied under SRP.
            var target = camera.targetTexture;
            var pipeline = GraphicsSettings.currentRenderPipeline;
            // URP 17's render graph can fail its final depth resolve into an imported
            // multisampled preview target even when the asset matches it. Keep the
            // SRP target single-sampled and never mutate the shared pipeline asset.
            // Analytic line coverage works at 1x; Built-in retains silhouette MSAA.
            int samples = pipeline == null ? MsaaSamples : 1;
            bool own = target && Offscreen(target, samples);
            try {
                if (own) camera.targetTexture = offscreen;
                if (items != null)
                    foreach (var item in items) {
                        if (!item.mesh) continue;
                        DrawItem(item);
                        if (Wireframe) DrawWire(item.mesh, item.matrix, WireColor);
                    }
                overlay?.Invoke(this);
                if (any) {
                    if (ShowGrid) DrawGrid(bounds);
                    if (ShowAxes) DrawAxes(bounds);
                }
                // Scene materials of a URP project render through URP, not the built-in fallback.
                camera.allowMSAA = samples > 1;
                utility.Render(true);
                if (own) ResolveFrame(offscreen, target);
            }
            catch (Exception ex) { UvtLog.Warn("[3D] " + ex.Message); }
            finally {
                drawing = false;
                SceneLightmaps = null;
                if (own) camera.targetTexture = target;
                GUI.DrawTexture(rect, utility.EndPreview(), ScaleMode.StretchToFill, false);
                foreach (var mesh in frameMeshes) if (mesh) Object.DestroyImmediate(mesh);
                frameMeshes.Clear(); frameBlocks.Clear();
            }
        }

        internal static void ResolveFrame(RenderTexture source, RenderTexture target)
        {
            if (source.antiAliasing == 1) { Graphics.CopyTexture(source, 0, 0, target, 0, 0); return; }
            // Sampling a non-bindMS texture performs Unity's automatic resolve. The
            // explicit ResolveAntiAliasedSurface(destination) path asserts on GLCore
            // after Camera.Render restores its framebuffer. Blit also works on D3D.
            bool previous = GL.sRGBWrite;
            try { GL.sRGBWrite = target.sRGB; Graphics.Blit(source, target); }
            finally { GL.sRGBWrite = previous; }
        }

        // The frame target matching the preview utility's: kept across frames and replaced
        // only when the size, format or sample count changes. A temporary per frame would
        // leave one differently sized multisampled texture in Unity's pool per repaint of a resize
        // drag, which the pool frees only after several frames.
        bool Offscreen(RenderTexture target, int samples)
        {
            var descriptor = target.descriptor;
            descriptor.msaaSamples = samples; descriptor.depthBufferBits = 24;
            descriptor.bindMS = false;
            if (offscreen && (offscreen.width != descriptor.width || offscreen.height != descriptor.height
                              || offscreen.graphicsFormat != descriptor.graphicsFormat || offscreen.antiAliasing != samples))
                ReleaseOffscreen();
            if (!offscreen)
                offscreen = new RenderTexture(descriptor) { name = "MeshViewport3D frame", hideFlags = HideFlags.HideAndDontSave };
            return offscreen.IsCreated() || offscreen.Create();
        }

        void ReleaseOffscreen()
        {
            if (!offscreen) return;
            offscreen.Release();
            Object.DestroyImmediate(offscreen);
            offscreen = null;
        }

        void DrawItem(Item item)
        {
            var mesh = item.mesh;
            if (Mode == Shading.Shaded) {
                for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                    var material = item.materials != null && sub < item.materials.Length ? item.materials[sub] : null;
                    if (material) utility.DrawMesh(mesh, item.matrix, material, sub);
                    else utility.DrawMesh(mesh, item.matrix, surface, sub, SurfaceBlock(mesh, false));
                }
                return;
            }
            var encoded = Encoded(mesh, Mode);
            if (!encoded) {
                // The mesh lacks the mode's data (a mesh without tangents next to ones that
                // have them): draw it as the neutral grey Shaded uses, never let it vanish.
                var fallback = SurfaceBlock(mesh, false);
                for (int sub = 0; sub < mesh.subMeshCount; ++sub) utility.DrawMesh(mesh, item.matrix, surface, sub, fallback);
                return;
            }
            var data = SurfaceBlock(mesh, true);
            for (int sub = 0; sub < encoded.subMeshCount; ++sub) utility.DrawMesh(encoded, item.matrix, surface, sub, data);
        }

        // ═══════════════════════════════════════════════════════════
        //  Overlay API (valid inside the overlay callback)
        // ═══════════════════════════════════════════════════════════

        // Draws are queued and rendered together at the end of the frame, so per-draw
        // values (a colour, a texture) travel in property blocks, never on the shared
        // materials — otherwise the last value set would paint every queued draw.
        readonly List<MaterialPropertyBlock> frameBlocks = new List<MaterialPropertyBlock>();
        MaterialPropertyBlock Block() { var block = new MaterialPropertyBlock(); frameBlocks.Add(block); return block; }
        MaterialPropertyBlock SurfaceBlock(Mesh mesh, bool encoded)
        {
            var block = Block();
            block.SetFloat(UseVertexColorId, encoded ? 1 : 0);
            block.SetFloat("_Lit", !encoded && Lit && mesh.HasVertexAttribute(VertexAttribute.Normal) ? 1 : 0);
            block.SetColor(ColorId, encoded ? Color.white : new Color(.72f, .72f, .72f, 1));
            return block;
        }

        /// <summary>Draws a mesh with the given material (null = the viewport's lit grey),
        /// optionally with per-draw properties.</summary>
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material = null, int submesh = -1, MaterialPropertyBlock properties = null)
        {
            if (!drawing || !mesh) return;
            if (!material) { material = surface; properties = properties ?? SurfaceBlock(mesh, false); }
            if (submesh >= 0) utility.DrawMesh(mesh, matrix, material, submesh, properties);
            else for (int sub = 0; sub < mesh.subMeshCount; ++sub) utility.DrawMesh(mesh, matrix, material, sub, properties);
        }

        /// <summary>Draws a mesh with the given material and a per-draw texture/tint pair
        /// (the UV layer: texture + white, a shell highlight: white texture + colour).</summary>
        public void DrawTextured(Mesh mesh, Matrix4x4 matrix, Material material, Texture texture, Color tint, int uvChannel)
            => DrawTextured(mesh, matrix, material, texture, tint, uvChannel, new Vector4(1, 1, 0, 0));

        internal void DrawTextured(Mesh mesh, Matrix4x4 matrix, Material material, Texture texture, Color tint, int uvChannel, Vector4 uvScaleOffset)
        {
            if (!drawing || !mesh || !material) return;
            var block = Block();
            block.SetTexture("_MainTex", texture ? texture : Texture2D.whiteTexture);
            block.SetColor(ColorId, tint);
            block.SetFloat("_UVChannel", uvChannel);
            block.SetVector("_UvScaleOffset", uvScaleOffset);
            for (int sub = 0; sub < mesh.subMeshCount; ++sub) utility.DrawMesh(mesh, matrix, material, sub, block);
        }

        /// <summary>Draws the mesh's unique triangle edges as lines (cached per mesh).</summary>
        public void DrawWire(Mesh mesh, Matrix4x4 matrix, Color color)
        {
            if (!drawing || !mesh) return;
            var edges = WireOf(mesh);
            if (edges) DrawLineMesh(edges, matrix, color);
        }

        /// <summary>Draws a prebuilt line-topology mesh in one colour (cage shells, edge sets).</summary>
        public void DrawLineMesh(Mesh lines, Matrix4x4 matrix, Color color)
        {
            if (!drawing || !lines) return;
            DrawRibbons(lineRibbons.Get(lines), matrix, color);
        }

        void DrawRibbons(Mesh ribbons, Matrix4x4 matrix, Color color)
        {
            if (!ribbons) return;
            var target = utility.camera.targetTexture;
            var block = Block(); block.SetColor(ColorId, color);
            block.SetVector("_ViewportSize", new Vector4(target.width, target.height, 0, 0));
            utility.DrawMesh(ribbons, matrix, wire, 0, block);
        }

        /// <summary>The world-space ray under a GUI point of the last drawn rect, from the
        /// camera the next repaint will use. False when the point is outside the rect.</summary>
        public bool TryScreenRay(Vector2 guiPoint, out Vector3 origin, out Vector3 direction)
        {
            origin = direction = Vector3.zero;
            if (currentRect.width <= 0f || currentRect.height <= 0f || !currentRect.Contains(guiPoint)) return false;
            var rotation = OrbitRotation();
            origin = pivot - rotation * Vector3.forward * distance;
            float ndcX = (guiPoint.x - currentRect.x) / currentRect.width * 2f - 1f;
            float ndcY = 1f - (guiPoint.y - currentRect.y) / currentRect.height * 2f;
            float tanHalf = Mathf.Tan(15f * Mathf.Deg2Rad);
            float aspect = currentRect.width / currentRect.height;
            if (ViewProjection != Projection.Perspective) {
                origin += rotation * new Vector3(ndcX * distance * tanHalf * aspect, ndcY * distance * tanHalf, 0);
                direction = rotation * Vector3.forward;
                return true;
            }
            direction = (rotation * new Vector3(ndcX * tanHalf * aspect, ndcY * tanHalf, 1f)).normalized;
            return true;
        }

        /// <summary>Draws line segments given as consecutive point pairs.</summary>
        public void DrawLines(IList<Vector3> pairs, Matrix4x4 matrix, Color color)
        {
            if (!drawing || pairs == null || pairs.Count < 2) return;
            var indices = new int[pairs.Count - pairs.Count % 2];
            for (int i = 0; i < indices.Length; ++i) indices[i] = i;
            var mesh = PreviewLines.Build(pairs, indices, null).Upload();
            frameMeshes.Add(mesh);
            DrawRibbons(mesh, matrix, color);
        }

        /// <summary>Draws per-segment coloured lines: pairs[i*2..i*2+1] in colors[i].</summary>
        public void DrawLines(IList<Vector3> pairs, IList<Color> colors, Matrix4x4 matrix)
        {
            if (!drawing || pairs == null || pairs.Count < 2) return;
            int count = pairs.Count - pairs.Count % 2;
            var vertexColors = new Color32[count]; var indices = new int[count];
            for (int i = 0; i < count; ++i) { vertexColors[i] = colors != null && i / 2 < colors.Count ? colors[i / 2] : Color.white; indices[i] = i; }
            var mesh = PreviewLines.Build(pairs, indices, vertexColors).Upload();
            frameMeshes.Add(mesh);
            DrawRibbons(mesh, matrix, Color.white);
        }

        // A ground grid under the content: cells at a round step near a tenth of the
        // content's size, fading toward the rim, every fifth line brighter.
        void DrawGrid(Bounds bounds)
        {
            bool xy = GridProjection == Projection.XY, yz = GridProjection == Projection.YZ;
            float sx = yz ? bounds.size.z : bounds.size.x, sy = xy || yz ? bounds.size.y : bounds.size.z;
            float ex = yz ? bounds.extents.z : bounds.extents.x, ey = xy || yz ? bounds.extents.y : bounds.extents.z;
            float step = NiceStep(Mathf.Max(sx, sy) / 8f);
            if (step <= 0f) return;
            int half = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(ex, ey) * 2.5f / step), 4, 60);
            float cx = Mathf.Round((yz ? bounds.center.z : bounds.center.x) / step) * step;
            float cz = Mathf.Round((xy || yz ? bounds.center.y : bounds.center.z) / step) * step;
            float reach = half * step;
            var pairs = new List<Vector3>(half * 8 + 4); var colors = new List<Color>(half * 4 + 2);
            var tone = new Color(0.62f, 0.70f, 0.82f);
            for (int i = -half; i <= half; ++i) {
                float o = i * step;
                float fade = 1f - Mathf.Abs(o) / reach;
                float a = (i % 5 == 0 ? 0.40f : 0.18f) * fade;
                var c = new Color(tone.r, tone.g, tone.b, a);
                pairs.Add(GridPoint(bounds, cx + o, cz - reach)); pairs.Add(GridPoint(bounds, cx + o, cz + reach)); colors.Add(c);
                pairs.Add(GridPoint(bounds, cx - reach, cz + o)); pairs.Add(GridPoint(bounds, cx + reach, cz + o)); colors.Add(c);
            }
            DrawLines(pairs, colors, Matrix4x4.identity);
        }

        // The pivot's axes: X red, Y green, Z blue, a quarter of the content radius long.
        void DrawAxes(Bounds bounds)
        {
            float length = radius * 0.25f;
            if (length <= 0f) return;
            Vector3 o = bounds.center;
            float head = length * 0.12f;
            Vector3[] dirs = { Vector3.right, Vector3.up, Vector3.forward };
            Color[] cols = { new Color(0.95f, 0.3f, 0.3f), new Color(0.45f, 0.9f, 0.3f), new Color(0.3f, 0.55f, 1f) };
            for (int i = 0; i < 3; ++i) {
                Vector3 d = dirs[i], tip = o + d * length;
                Vector3 side = i == 1 ? Vector3.right : Vector3.up;
                var pairs = new List<Vector3> { o, tip, tip, tip - d * head + side * head * 0.5f, tip, tip - d * head - side * head * 0.5f };
                DrawLines(pairs, Matrix4x4.identity, cols[i]);
            }
        }

        static float NiceStep(float raw)
        {
            if (raw <= 0f || float.IsNaN(raw) || float.IsInfinity(raw)) return 0f;
            float power = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
            float m = raw / power;
            float nice = m < 1.5f ? 1f : m < 3.5f ? 2f : m < 7.5f ? 5f : 10f;
            return nice * power;
        }

        /// <summary>Draws camera-facing dots of a screen-constant size (pixels).</summary>
        public void DrawPoints(IList<Vector3> positions, Matrix4x4 matrix, Color color, float sizePixels = 5f)
        {
            if (!drawing || positions == null || positions.Count == 0) return;
            var camera = utility.camera;
            Vector3 right = camera.transform.right, up = camera.transform.up;
            float pixelsToWorld = 2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, currentRect.height);
            var vertices = new Vector3[positions.Count * 4]; var vertexColors = new Color[vertices.Length]; var indices = new int[positions.Count * 6];
            for (int i = 0; i < positions.Count; ++i) {
                Vector3 world = matrix.MultiplyPoint3x4(positions[i]);
                float half = camera.orthographic ? sizePixels * camera.orthographicSize / Mathf.Max(1f, currentRect.height)
                    : sizePixels * 0.5f * pixelsToWorld * Mathf.Max(1e-4f, Vector3.Dot(world - camera.transform.position, camera.transform.forward));
                int v = i * 4, t = i * 6;
                vertices[v] = world - right * half - up * half; vertices[v + 1] = world + right * half - up * half;
                vertices[v + 2] = world + right * half + up * half; vertices[v + 3] = world - right * half + up * half;
                for (int k = 0; k < 4; ++k) vertexColors[v + k] = color;
                indices[t] = v; indices[t + 1] = v + 2; indices[t + 2] = v + 1; indices[t + 3] = v; indices[t + 4] = v + 3; indices[t + 5] = v + 2;
            }
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices; mesh.colors = vertexColors; mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            frameMeshes.Add(mesh);
            utility.DrawMesh(mesh, Matrix4x4.identity, points, 0);
        }

        /// <summary>A material of the viewport's own shader: flat colour, optional
        /// headlight, drawn on top of coincident geometry.</summary>
        public Material FlatMaterial(Color color, bool lit)
        {
            if (!EnsureResources()) return null;
            flat.SetColor(ColorId, color); flat.SetFloat("_Lit", lit ? 1 : 0); flat.SetFloat(UseVertexColorId, 0);
            return flat;
        }

        // ═══════════════════════════════════════════════════════════
        //  Camera
        // ═══════════════════════════════════════════════════════════

        void FrameIfRequested(Bounds bounds, bool any)
        {
            if (any && frameRequested) Frame(bounds);
        }

        /// <summary>Centres the camera on bounds at a distance that fits them.</summary>
        public void Frame(Bounds bounds)
        {
            frameRequested = false;
            pivot = bounds.center;
            radius = Mathf.Max(bounds.extents.magnitude, 1e-4f);
            distance = radius / Mathf.Sin(15f * Mathf.Deg2Rad) * 1.05f;
            RequestRepaint?.Invoke();
        }

        /// <summary>Re-frames the current content on the next draw.</summary>
        public void FrameContent() { frameRequested = true; RequestRepaint?.Invoke(); }

        void HandleInput(Rect rect, Bounds? content)
        {
            int id = GUIUtility.GetControlID("MeshViewport3D".GetHashCode(), FocusType.Passive, rect);
            var e = Event.current;
            switch (e.GetTypeForControl(id)) {
                case EventType.MouseDown:
                    if (!rect.Contains(e.mousePosition)) break;
                    if (e.button == 2 && e.clickCount == 2 && content.HasValue) { Frame(content.Value); e.Use(); break; }
                    GUIUtility.hotControl = id; e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    if (e.button == 2 || (e.button == 0 && e.alt)) {
                        // Pan: screen pixels to world at the pivot's depth.
                        float scale = 2f * distance * Mathf.Tan(15f * Mathf.Deg2Rad) / Mathf.Max(1f, rect.height);
                        var rotation = OrbitRotation();
                        pivot -= (rotation * Vector3.right) * (e.delta.x * scale);
                        pivot += (rotation * Vector3.up) * (e.delta.y * scale);
                    }
                    else if (ViewProjection == Projection.Perspective) { orbit += e.delta * 0.5f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); }
                    e.Use(); RequestRepaint?.Invoke();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
                case EventType.ScrollWheel:
                    if (!rect.Contains(e.mousePosition)) break;
                    distance = Mathf.Clamp(distance * (1f + e.delta.y * 0.05f), radius * 0.05f, radius * 200f);
                    e.Use(); RequestRepaint?.Invoke();
                    break;
                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.F && rect.Contains(e.mousePosition) && content.HasValue) { Frame(content.Value); e.Use(); }
                    break;
            }
        }

        static Bounds BoundsOf(IReadOnlyList<Item> items, out bool any)
        {
            var bounds = new Bounds(); any = false;
            if (items == null) return bounds;
            foreach (var item in items) {
                if (!item.mesh) continue;
                var local = item.mesh.bounds;
                // Transform the eight corners so a rotated item still fits.
                for (int c = 0; c < 8; ++c) {
                    var corner = new Vector3((c & 1) == 0 ? local.min.x : local.max.x, (c & 2) == 0 ? local.min.y : local.max.y, (c & 4) == 0 ? local.min.z : local.max.z);
                    var world = item.matrix.MultiplyPoint3x4(corner);
                    if (!any) { bounds = new Bounds(world, Vector3.zero); any = true; } else bounds.Encapsulate(world);
                }
            }
            return bounds;
        }

        // ═══════════════════════════════════════════════════════════
        //  Resources
        // ═══════════════════════════════════════════════════════════

        bool EnsureResources()
        {
            if (utility == null) utility = new PreviewRenderUtility { cameraFieldOfView = 30f };
            if (surface && flat && wire && points) return true;
            var shader = Shader.Find("Hidden/MeshLab/RemeshPreview");
            var lineShader = Shader.Find("Hidden/MeshLab/PreviewLines");
            if (!shader || !lineShader) return false;
            surface = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            flat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            flat.SetFloat("_DepthOffset", -1);
            // Lines sit above the flat overlays and the UV layer (both at -1): at the same
            // offset the layer, drawn later, covered the wire wherever it was opaque.
            wire = new Material(lineShader) { hideFlags = HideFlags.HideAndDontSave };
            points = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            points.SetFloat("_Lit", 0); points.SetFloat(UseVertexColorId, 1); points.SetFloat("_DepthOffset", -3);
            return true;
        }

        // A clone carrying the mode's data as vertex colours, cached per (mesh, mode).
        Mesh Encoded(Mesh mesh, Shading mode)
        {
            long key = ((long)mesh.GetInstanceID() << 8) | (byte)mode;
            if (encodedCache.TryGetValue(key, out var cached) && cached) {
                // A mesh rebuilt under the same instance (a compaction, a re-import) changes
                // its vertex count; the clone made from the old vertices is stale.
                if (cached.vertexCount == mesh.vertexCount) return cached;
                Object.DestroyImmediate(cached);
            }
            var colors = EncodeColors(mesh, mode);
            if (colors == null) { encodedCache[key] = null; return null; }
            var clone = Object.Instantiate(mesh);
            clone.name = mesh.name + "_" + mode; clone.hideFlags = HideFlags.HideAndDontSave;
            clone.colors32 = colors;
            encodedCache[key] = clone;
            return clone;
        }

        /// <summary>The colour encoding of a shading mode (null when the mesh lacks the
        /// data): vertex colours as they are, normals and tangents as xyz×0.5+0.5, UVs as
        /// frac(u), frac(v) in red and green.</summary>
        public static Color32[] EncodeColors(Mesh mesh, Shading mode)
        {
            int n = mesh.vertexCount;
            if (n == 0) return null;
            var colors = new Color32[n];
            switch (mode) {
                case Shading.Shaded: return null;
                case Shading.Positions: {
                    var positions = mesh.vertices; var bounds = mesh.bounds;
                    for (int i = 0; i < n; ++i) {
                        var p = positions[i] - bounds.min; var size = bounds.size;
                        colors[i] = new Color(size.x > 0 ? p.x / size.x : .5f,
                            size.y > 0 ? p.y / size.y : .5f, size.z > 0 ? p.z / size.z : .5f, 1);
                    }
                    return colors;
                }
                case Shading.ColorAlpha: {
                    var c = mesh.colors;
                    if (c.Length != n) return null;
                    for (int i = 0; i < n; ++i) colors[i] = new Color(c[i].a, c[i].a, c[i].a, 1);
                    return colors;
                }
                case Shading.TangentSign: {
                    var t = mesh.tangents;
                    if (t.Length != n) return null;
                    for (int i = 0; i < n; ++i) colors[i] = t[i].w < 0 ? new Color32(255, 80, 40, 255) : new Color32(40, 180, 255, 255);
                    return colors;
                }
                case Shading.BoneWeights:
                case Shading.BoneIndices:
                    return MeshInspection.SkinColors(mesh, mode == Shading.BoneIndices);
                case Shading.VertexColors: {
                    var c = mesh.colors32;
                    if (c == null || c.Length != n) for (int i = 0; i < n; ++i) colors[i] = new Color32(200, 200, 200, 255);
                    else Array.Copy(c, colors, n);
                    return colors;
                }
                case Shading.Normals: {
                    var normals = mesh.normals;
                    if (normals == null || normals.Length != n) return null;
                    for (int i = 0; i < n; ++i) colors[i] = new Color32(Byte(normals[i].x), Byte(normals[i].y), Byte(normals[i].z), 255);
                    return colors;
                }
                case Shading.Tangents: {
                    var tangents = mesh.tangents;
                    if (tangents == null || tangents.Length != n) return null;
                    for (int i = 0; i < n; ++i) colors[i] = new Color32(Byte(tangents[i].x), Byte(tangents[i].y), Byte(tangents[i].z), 255);
                    return colors;
                }
                default: {
                    int channel = mode - Shading.UV0;
                    if (channel < 0 || channel > 7) return null;
                    var uv = new List<Vector2>(); mesh.GetUVs(channel, uv);
                    if (uv.Count != n) return null;
                    for (int i = 0; i < n; ++i) {
                        float u = uv[i].x - Mathf.Floor(uv[i].x), v = uv[i].y - Mathf.Floor(uv[i].y);
                        colors[i] = new Color32((byte)(u * 255f), (byte)(v * 255f), 0, 255);
                    }
                    return colors;
                }
            }
        }

        static byte Byte(float signed) => (byte)Mathf.Clamp(Mathf.RoundToInt((signed * 0.5f + 0.5f) * 255f), 0, 255);

        internal Mesh WireOf(Mesh mesh)
        {
            int key = mesh.GetInstanceID();
            if (wireCache.TryGetValue(key, out var cached)) {
                if (cached.vertexCount == mesh.vertexCount) return cached.mesh;
                cached.work.Dispose();
                if (cached.mesh) Object.DestroyImmediate(cached.mesh);
            }
            var preview = new WirePreview { vertexCount = mesh.vertexCount };
            wireCache[key] = preview;
            preview.work.Enqueue(() => {
                if (!mesh) return token => null;
                var vertices = mesh.vertices;
                string name = mesh.name + "_Wire";
                SnapshotIndices(mesh, out var submeshes, out var topology);
                return token => new WireData { name = name, vertices = vertices,
                    indices = EdgeIndices(submeshes, topology, token) };
            }, data => {
                if (!mesh || data == null) return;
                var edges = new Mesh { name = data.name, hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                preview.mesh = edges;
                edges.vertices = data.vertices;
                edges.SetIndices(data.indices, MeshTopology.Lines, 0);
                RequestRepaint?.Invoke();
            });
            return null;
        }

        /// <summary>Unique line-list edges across submeshes. Triangle submeshes above 1M faces are skipped.</summary>
        public static List<int> EdgeIndices(Mesh mesh)
        {
            SnapshotIndices(mesh, out var indices, out var topology);
            return EdgeIndices(indices, topology, CancellationToken.None);
        }

        static void SnapshotIndices(Mesh mesh, out int[][] indices, out MeshTopology[] topology)
        {
            indices = new int[mesh.subMeshCount][];
            topology = new MeshTopology[indices.Length];
            for (int sub = 0; sub < indices.Length; ++sub) {
                indices[sub] = mesh.GetIndices(sub);
                topology[sub] = mesh.GetTopology(sub);
            }
        }

        static List<int> EdgeIndices(int[][] submeshes, MeshTopology[] topology, CancellationToken token)
        {
            var pairs = new List<int>();
            for (int sub = 0; sub < submeshes.Length; ++sub) {
                token.ThrowIfCancellationRequested();
                AppendEdges(pairs, submeshes[sub], topology[sub]);
            }
            var unique = new List<int>();
            var seen = new HashSet<ulong>();
            for (int i = 0; i + 1 < pairs.Count; i += 2) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                uint a = (uint)Mathf.Min(pairs[i], pairs[i + 1]), b = (uint)Mathf.Max(pairs[i], pairs[i + 1]);
                if (seen.Add(((ulong)a << 32) | b)) { unique.Add(pairs[i]); unique.Add(pairs[i + 1]); }
            }
            return unique;
        }

        static void AppendEdges(List<int> pairs, int[] indices, MeshTopology topology)
        {
            switch (topology) {
                case MeshTopology.Triangles:
                    var edges = UvTopology.UniqueEdges(indices);
                    if (edges != null) pairs.AddRange(edges);
                    break;
                case MeshTopology.Lines: pairs.AddRange(indices); break;
                case MeshTopology.LineStrip:
                    for (int i = 1; i < indices.Length; ++i) { pairs.Add(indices[i - 1]); pairs.Add(indices[i]); }
                    break;
                case MeshTopology.Quads:
                    for (int i = 0; i + 3 < indices.Length; i += 4)
                        for (int k = 0; k < 4; ++k) { pairs.Add(indices[i + k]); pairs.Add(indices[i + (k + 1) % 4]); }
                    break;
            }
        }

        /// <summary>Drops cached encodings and wires (call when source meshes change).</summary>
        public void InvalidateCaches()
        {
            lineRibbons.Dispose();
            foreach (var mesh in encodedCache.Values) if (mesh) Object.DestroyImmediate(mesh);
            foreach (var preview in wireCache.Values) {
                preview.work.Dispose();
                if (preview.mesh) Object.DestroyImmediate(preview.mesh);
            }
            encodedCache.Clear(); wireCache.Clear();
        }

        /// <summary>Drops the cached encodings of one mesh (its colours or UVs were written),
        /// so the next frame encodes the new data instead of showing the pre-edit clone.</summary>
        public void InvalidateMesh(Mesh mesh)
        {
            if (!mesh) return;
            lineRibbons.Remove(mesh);
            if (wireCache.TryGetValue(mesh.GetInstanceID(), out var preview)) {
                preview.work.Dispose();
                lineRibbons.Remove(preview.mesh);
                if (preview.mesh) Object.DestroyImmediate(preview.mesh);
                wireCache.Remove(mesh.GetInstanceID());
            }
            long id = (long)mesh.GetInstanceID() << 8;
            for (int mode = 0; mode < ShadingNames.Length; ++mode) {
                long key = id | (byte)mode;
                if (!encodedCache.TryGetValue(key, out var clone)) continue;
                if (clone) Object.DestroyImmediate(clone);
                encodedCache.Remove(key);
            }
        }

        public void Dispose()
        {
            VertexChannels.Changed -= InvalidateMesh;
            InvalidateCaches();
            utility?.Cleanup(); utility = null;
            ReleaseOffscreen();
            if (surface) Object.DestroyImmediate(surface);
            if (flat) Object.DestroyImmediate(flat);
            if (wire) Object.DestroyImmediate(wire);
            if (points) Object.DestroyImmediate(points);
            surface = flat = wire = points = null;
        }
    }
}
