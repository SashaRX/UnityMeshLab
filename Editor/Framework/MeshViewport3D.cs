// MeshViewport3D.cs — the shared 3D viewport of Mesh Lab: an orbitable, lit view of
// any set of meshes rendered off-screen into the canvas area. Owned by the hub and
// offered to every tool through IUvTool3D: a tool can hand it content (what to show)
// and overlays (wire, lines, points on top), while the hub keeps the camera, the
// shading modes and the input in one place.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    public sealed class MeshViewport3D : IDisposable
    {
        /// <summary>How the content's surfaces are coloured.</summary>
        public enum Shading { Shaded, VertexColors, Normals, Tangents, UV0, UV1, UV2, UV3 }

        /// <summary>One mesh to show: its matrix (any common space) and the materials the
        /// Shaded mode draws it with (null or short arrays fall back to a lit grey).</summary>
        public struct Item
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material[] materials;
            public Item(Mesh mesh, Matrix4x4 matrix, Material[] materials = null) { this.mesh = mesh; this.matrix = matrix; this.materials = materials; }
        }

        public static readonly string[] ShadingNames = { "Shaded", "Vert Colors", "Normals", "Tangents", "UV0", "UV1", "UV2", "UV3" };

        public Shading Mode = Shading.Shaded;
        public bool Wireframe;
        public bool Lit = true;
        public bool ShowGrid = true, ShowAxes = true;
        public Color Background = new Color(0.16f, 0.19f, 0.24f, 1f);
        public Action RequestRepaint;

        // ── camera ──
        Vector3 pivot;
        Vector2 orbit = new Vector2(-135f, 20f);   // yaw, pitch (degrees)
        float distance = 5f, radius = 1f;
        int framedKey;
        bool framedOnce;

        PreviewRenderUtility utility;
        Material surface, flat, wire, points, translucent;
        Rect currentRect;
        bool drawing;
        readonly Dictionary<long, Mesh> encodedCache = new Dictionary<long, Mesh>();
        readonly Dictionary<int, Mesh> wireCache = new Dictionary<int, Mesh>();
        readonly List<Mesh> frameMeshes = new List<Mesh>();   // transient meshes built for this frame

        public Rect LastRect => currentRect;
        public Camera Camera => utility?.camera;

        // ═══════════════════════════════════════════════════════════
        //  Drawing
        // ═══════════════════════════════════════════════════════════

        /// <summary>Handles input in rect, then (on Repaint) renders the items with the
        /// current shading, calls overlay for the tool's additions and blits the result.</summary>
        public void Draw(Rect rect, IReadOnlyList<Item> items, Action<MeshViewport3D> overlay)
        {
            currentRect = rect;
            var bounds = BoundsOf(items, out int key, out bool any);
            if (any && (!framedOnce || key != framedKey)) { Frame(bounds); framedKey = key; framedOnce = true; }
            HandleInput(rect, any ? bounds : (Bounds?)null);
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, Background);
            if (!EnsureResources()) return;

            utility.BeginPreview(rect, GUIStyle.none);
            var camera = utility.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            var rotation = Quaternion.Euler(orbit.y, orbit.x, 0f);
            camera.transform.rotation = rotation;
            camera.transform.position = pivot - rotation * Vector3.forward * distance;
            camera.nearClipPlane = Mathf.Max(distance * 0.001f, 1e-4f);
            camera.farClipPlane = distance + radius * 8f + 1f;
            utility.lights[0].intensity = 1.1f; utility.lights[0].transform.rotation = rotation * Quaternion.Euler(30f, 30f, 0f);
            utility.lights[1].intensity = 0.6f; utility.lights[1].transform.rotation = rotation * Quaternion.Euler(-20f, -150f, 0f);
            utility.ambientColor = new Color(0.25f, 0.25f, 0.25f, 1f);

            drawing = true;
            try {
                if (items != null)
                    foreach (var item in items) {
                        if (!item.mesh) continue;
                        DrawItem(item);
                        if (Wireframe) DrawWire(item.mesh, item.matrix, Mode == Shading.Shaded ? new Color(0.05f, 0.05f, 0.05f, 1f) : new Color(0.4f, 0.85f, 1f, 1f));
                    }
                overlay?.Invoke(this);
                if (any) {
                    if (ShowGrid) DrawGrid(bounds);
                    if (ShowAxes) DrawAxes(bounds);
                }
                // Scene materials of a URP project render through URP, not the built-in fallback.
                utility.Render(true);
            }
            catch (Exception ex) { UvtLog.Warn("[3D] " + ex.Message); }
            finally {
                drawing = false;
                GUI.DrawTexture(rect, utility.EndPreview(), ScaleMode.StretchToFill, false);
                foreach (var mesh in frameMeshes) if (mesh) Object.DestroyImmediate(mesh);
                frameMeshes.Clear(); frameBlocks.Clear();
            }
        }

        void DrawItem(Item item)
        {
            var mesh = item.mesh;
            if (Mode == Shading.Shaded) {
                for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                    var material = item.materials != null && sub < item.materials.Length ? item.materials[sub] : null;
                    if (material) utility.DrawMesh(mesh, item.matrix, material, sub);
                    else { surface.SetFloat("_UseVertexColor", 0); surface.SetFloat("_Lit", Lit ? 1 : 0); surface.SetColor("_Color", new Color(0.72f, 0.72f, 0.72f, 1f)); utility.DrawMesh(mesh, item.matrix, surface, sub); }
                }
                return;
            }
            var encoded = Encoded(mesh, Mode);
            if (!encoded) return;
            surface.SetFloat("_UseVertexColor", 1); surface.SetColor("_Color", Color.white);
            // Data encodings read better unlit; the headlight stays for vertex colours.
            surface.SetFloat("_Lit", Lit && Mode == Shading.VertexColors ? 1 : 0);
            for (int sub = 0; sub < encoded.subMeshCount; ++sub) utility.DrawMesh(encoded, item.matrix, surface, sub);
        }

        // ═══════════════════════════════════════════════════════════
        //  Overlay API (valid inside the overlay callback)
        // ═══════════════════════════════════════════════════════════

        // Draws are queued and rendered together at the end of the frame, so per-draw
        // values (a colour, a texture) travel in property blocks, never on the shared
        // materials — otherwise the last value set would paint every queued draw.
        readonly List<MaterialPropertyBlock> frameBlocks = new List<MaterialPropertyBlock>();
        MaterialPropertyBlock Block() { var block = new MaterialPropertyBlock(); frameBlocks.Add(block); return block; }

        /// <summary>Draws a mesh with the given material (null = the viewport's lit grey),
        /// optionally with per-draw properties.</summary>
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material = null, int submesh = -1, MaterialPropertyBlock properties = null)
        {
            if (!drawing || !mesh) return;
            if (!material) { material = surface; surface.SetFloat("_UseVertexColor", 0); surface.SetFloat("_Lit", 1); surface.SetColor("_Color", new Color(0.72f, 0.72f, 0.72f, 1f)); }
            if (submesh >= 0) utility.DrawMesh(mesh, matrix, material, submesh, properties);
            else for (int sub = 0; sub < mesh.subMeshCount; ++sub) utility.DrawMesh(mesh, matrix, material, sub, properties);
        }

        /// <summary>Draws a mesh with the given material and a per-draw texture/tint pair
        /// (the UV layer: texture + white, a shell highlight: white texture + colour).</summary>
        public void DrawTextured(Mesh mesh, Matrix4x4 matrix, Material material, Texture texture, Color tint, int uvChannel)
        {
            if (!drawing || !mesh || !material) return;
            var block = Block();
            block.SetTexture("_MainTex", texture ? texture : Texture2D.whiteTexture);
            block.SetColor("_Color", tint);
            block.SetFloat("_UVChannel", uvChannel);
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
            var block = Block(); block.SetColor("_Color", color);
            utility.DrawMesh(lines, matrix, wire, 0, block);
        }

        /// <summary>The world-space ray under a GUI point of the last drawn rect, from the
        /// camera the next repaint will use. False when the point is outside the rect.</summary>
        public bool TryScreenRay(Vector2 guiPoint, out Vector3 origin, out Vector3 direction)
        {
            origin = direction = Vector3.zero;
            if (currentRect.width <= 0f || currentRect.height <= 0f || !currentRect.Contains(guiPoint)) return false;
            var rotation = Quaternion.Euler(orbit.y, orbit.x, 0f);
            origin = pivot - rotation * Vector3.forward * distance;
            float ndcX = (guiPoint.x - currentRect.x) / currentRect.width * 2f - 1f;
            float ndcY = 1f - (guiPoint.y - currentRect.y) / currentRect.height * 2f;
            float tanHalf = Mathf.Tan(15f * Mathf.Deg2Rad);
            float aspect = currentRect.width / currentRect.height;
            direction = (rotation * new Vector3(ndcX * tanHalf * aspect, ndcY * tanHalf, 1f)).normalized;
            return true;
        }

        /// <summary>Draws line segments given as consecutive point pairs.</summary>
        public void DrawLines(IList<Vector3> pairs, Matrix4x4 matrix, Color color)
        {
            if (!drawing || pairs == null || pairs.Count < 2) return;
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            var vertices = new Vector3[pairs.Count]; var indices = new int[pairs.Count - pairs.Count % 2];
            for (int i = 0; i < vertices.Length; ++i) vertices[i] = pairs[i];
            for (int i = 0; i < indices.Length; ++i) indices[i] = i;
            mesh.vertices = vertices; mesh.SetIndices(indices, MeshTopology.Lines, 0);
            frameMeshes.Add(mesh);
            DrawLineMesh(mesh, matrix, color);
        }

        /// <summary>Draws per-segment coloured lines: pairs[i*2..i*2+1] in colors[i].</summary>
        public void DrawLines(IList<Vector3> pairs, IList<Color> colors, Matrix4x4 matrix)
        {
            if (!drawing || pairs == null || pairs.Count < 2) return;
            int count = pairs.Count - pairs.Count % 2;
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            var vertices = new Vector3[count]; var vertexColors = new Color[count]; var indices = new int[count];
            for (int i = 0; i < count; ++i) { vertices[i] = pairs[i]; vertexColors[i] = colors != null && i / 2 < colors.Count ? colors[i / 2] : Color.white; indices[i] = i; }
            mesh.vertices = vertices; mesh.colors = vertexColors; mesh.SetIndices(indices, MeshTopology.Lines, 0);
            frameMeshes.Add(mesh);
            utility.DrawMesh(mesh, matrix, translucent, 0);
        }

        // A ground grid under the content: cells at a round step near a tenth of the
        // content's size, fading toward the rim, every fifth line brighter.
        void DrawGrid(Bounds bounds)
        {
            float step = NiceStep(Mathf.Max(bounds.size.x, bounds.size.z) / 8f);
            if (step <= 0f) return;
            int half = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(bounds.extents.x, bounds.extents.z) * 2.5f / step), 4, 60);
            float y = bounds.min.y - radius * 0.002f;
            float cx = Mathf.Round(bounds.center.x / step) * step, cz = Mathf.Round(bounds.center.z / step) * step;
            float reach = half * step;
            var pairs = new List<Vector3>(half * 8 + 4); var colors = new List<Color>(half * 4 + 2);
            var tone = new Color(0.62f, 0.70f, 0.82f);
            for (int i = -half; i <= half; ++i) {
                float o = i * step;
                float fade = 1f - Mathf.Abs(o) / reach;
                float a = (i % 5 == 0 ? 0.40f : 0.18f) * fade;
                var c = new Color(tone.r, tone.g, tone.b, a);
                pairs.Add(new Vector3(cx + o, y, cz - reach)); pairs.Add(new Vector3(cx + o, y, cz + reach)); colors.Add(c);
                pairs.Add(new Vector3(cx - reach, y, cz + o)); pairs.Add(new Vector3(cx + reach, y, cz + o)); colors.Add(c);
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
                float half = sizePixels * 0.5f * pixelsToWorld * Mathf.Max(1e-4f, Vector3.Dot(world - camera.transform.position, camera.transform.forward));
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
            flat.SetColor("_Color", color); flat.SetFloat("_Lit", lit ? 1 : 0); flat.SetFloat("_UseVertexColor", 0);
            return flat;
        }

        // ═══════════════════════════════════════════════════════════
        //  Camera
        // ═══════════════════════════════════════════════════════════

        /// <summary>Centres the camera on bounds at a distance that fits them.</summary>
        public void Frame(Bounds bounds)
        {
            pivot = bounds.center;
            radius = Mathf.Max(bounds.extents.magnitude, 1e-4f);
            distance = radius / Mathf.Sin(15f * Mathf.Deg2Rad) * 1.05f;
            RequestRepaint?.Invoke();
        }

        /// <summary>Re-frames the current content on the next draw.</summary>
        public void FrameContent() { framedOnce = false; RequestRepaint?.Invoke(); }

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
                        var rotation = Quaternion.Euler(orbit.y, orbit.x, 0f);
                        pivot -= (rotation * Vector3.right) * (e.delta.x * scale);
                        pivot += (rotation * Vector3.up) * (e.delta.y * scale);
                    }
                    else { orbit += e.delta * 0.5f; orbit.y = Mathf.Clamp(orbit.y, -89f, 89f); }
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

        static Bounds BoundsOf(IReadOnlyList<Item> items, out int key, out bool any)
        {
            var bounds = new Bounds(); any = false; key = 17;
            if (items == null) return bounds;
            foreach (var item in items) {
                if (!item.mesh) continue;
                key = unchecked(key * 31 + item.mesh.GetInstanceID());
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
            if (surface && flat && wire && points && translucent) return true;
            var shader = Shader.Find("Hidden/MeshLab/RemeshPreview");
            var overlayShader = Shader.Find("Hidden/MeshLab/UvOverlay");
            if (!shader || !overlayShader) return false;
            translucent = new Material(overlayShader) { hideFlags = HideFlags.HideAndDontSave };
            surface = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            flat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            flat.SetFloat("_DepthOffset", -1);
            wire = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            wire.SetFloat("_Lit", 0); wire.SetFloat("_DepthOffset", -1);
            points = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            points.SetFloat("_Lit", 0); points.SetFloat("_UseVertexColor", 1); points.SetFloat("_DepthOffset", -2);
            return true;
        }

        // A clone carrying the mode's data as vertex colours, cached per (mesh, mode).
        Mesh Encoded(Mesh mesh, Shading mode)
        {
            long key = ((long)mesh.GetInstanceID() << 8) | (byte)mode;
            if (encodedCache.TryGetValue(key, out var cached) && cached) return cached;
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

        Mesh WireOf(Mesh mesh)
        {
            int key = mesh.GetInstanceID();
            if (wireCache.TryGetValue(key, out var cached) && cached) return cached;
            var indices = EdgeIndices(mesh);
            Mesh edges = null;
            if (indices != null) {
                edges = new Mesh { name = mesh.name + "_Wire", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                edges.vertices = mesh.vertices;
                edges.SetIndices(indices, MeshTopology.Lines, 0);
            }
            wireCache[key] = edges;
            return edges;
        }

        /// <summary>Line-list indices of a mesh's unique triangle edges (null above 1M faces).</summary>
        public static List<int> EdgeIndices(Mesh mesh) => UvTopology.UniqueEdges(mesh.triangles);

        /// <summary>Drops cached encodings and wires (call when source meshes change).</summary>
        public void InvalidateCaches()
        {
            foreach (var mesh in encodedCache.Values) if (mesh) Object.DestroyImmediate(mesh);
            foreach (var mesh in wireCache.Values) if (mesh) Object.DestroyImmediate(mesh);
            encodedCache.Clear(); wireCache.Clear();
        }

        public void Dispose()
        {
            InvalidateCaches();
            utility?.Cleanup(); utility = null;
            if (surface) Object.DestroyImmediate(surface);
            if (flat) Object.DestroyImmediate(flat);
            if (wire) Object.DestroyImmediate(wire);
            if (points) Object.DestroyImmediate(points);
            if (translucent) Object.DestroyImmediate(translucent);
            surface = flat = wire = points = translucent = null;
        }
    }
}
