// UvCanvasView.cs — UV canvas component for Mesh Lab.
// Handles GL rendering, zoom/pan, shell picking, fill modes.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace LightmapUvTool
{
    public class UvCanvasView
    {
        public struct FillModeEntry
        {
            public string name;
            public Action<UvCanvasView, float, float, float, Mesh, MeshEntry> drawCallback;
        }

        public enum PreviewMode { Off, Checker, Shells3D, Lightmap }

        public class ShellDebugHit
        {
            public MeshEntry entry;
            public Mesh mesh;
            public UvShell shell;
            public int shellId;
            public int uvChannel;
            public Vector2 hoverUv;
            public int tileU, tileV;
            public Vector2 localUv;
            public int drawIndex;
        }

        // Constants & Palette
        public const float UV_LO = -4f, UV_HI = 5f;
        public const int BATCH = 4000;
        public const int MAX_TRI = 500000;

        public static readonly Color[] pal = {
            new Color(.20f,.60f,1f),  new Color(1f,.40f,.20f),
            new Color(.30f,.85f,.40f),new Color(.90f,.25f,.60f),
            new Color(.95f,.85f,.20f),new Color(.55f,.30f,.90f),
            new Color(0f,.80f,.80f),  new Color(.85f,.55f,.20f),
            new Color(.60f,.90f,.20f),new Color(.90f,.20f,.20f),
            new Color(.40f,.40f,.90f),new Color(.90f,.70f,.40f),
        };

        public static readonly Color cAccept = new Color(.2f,.85f,.3f,.5f);
        public static readonly Color cAmbig  = new Color(.95f,.85f,.2f,.5f);
        public static readonly Color cMis    = new Color(.9f,.15f,.15f,.5f);
        public static readonly Color cReject = new Color(.4f,.4f,.4f,.5f);
        public static readonly Color cNone   = new Color(.3f,.3f,.3f,.3f);

        public static readonly Color cValClean    = new Color(.2f, .85f, .3f, .4f);
        public static readonly Color cValStretch  = new Color(.95f, .85f, .15f, .5f);
        public static readonly Color cValZero     = new Color(.7f, .2f, .9f, .5f);
        public static readonly Color cValOOB      = new Color(1f, .5f, .1f, .5f);
        public static readonly Color cValOverlap  = new Color(1f, .1f, .9f, .55f);
        public static readonly Color cValTexel    = new Color(.1f, .7f, .9f, .5f);

        // Callbacks
        public Action<ShellUvHit> OnDoubleClickShell;

        // Public state
        public float Zoom = 1f;
        public Vector2 Pan;
        public bool Panning;
        public Rect LastCanvasRect;
        public List<FillModeEntry> FillModes = new List<FillModeEntry>();
        public int ActiveFillModeIndex;
        public bool FillHidden;
        public bool ShowWireframe = true;
        public bool ShowBorder = true;
        public float FillAlpha = 0.25f;

        /// <summary>
        /// When non-zero, validation fill/overlay draws only triangles whose TriIssue
        /// intersects this mask. When zero (default) every triangle is drawn.
        /// Toggle bits via the Validation Overlay UI in LightmapTransferTool.
        /// </summary>
        public TransferValidator.TriIssue ValidationFilterMask = TransferValidator.TriIssue.None;

        // Spot mode
        public bool SpotMode;
        public bool LockSelection;
        public bool HoverHitValid;
        public int HoveredShellId = -1;
        public Vector2 UvSpot;
        public Vector3 HoverWorldPos;
        public bool CanvasSpotValid;
        public Vector2 CanvasSpotUv;
        public ShellUvHit HoveredShell;
        public ShellUvHit SelectedShell;
        public bool HasHoveredShell;
        public bool HasSelectedShell;
        public ShellDebugHit HoveredShellDebug;
        public ShellDebugHit SelectedShellDebug;

        // Preview
        public PreviewMode CurrentPreviewMode;
        public bool CheckerEnabled;
        public float LmExposure = 1f;

        // Materials
        public Material GlMat;
        public Material TexMat;
        public Material SpotMat;
        public Material ShellOverlayMat;
        RenderTexture canvasRT;

        // Request repaint
        public Action RequestRepaint;

        // Frame caches
        readonly Dictionary<int, int[]> cachedTriangles = new Dictionary<int, int[]>();
        readonly Dictionary<long, Vector2[]> cachedUvs = new Dictionary<long, Vector2[]>();

        // Hit tracking
        int lastHitMeshId = -1;
        int lastHitShellId = -1;
        const int TRI_PICK_BUDGET = 6000;

        public void Init()
        {
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            GlMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            GlMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            GlMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            GlMat.SetInt("_Cull", (int)CullMode.Off);
            GlMat.SetInt("_ZWrite", 0);

            var texShader = Shader.Find("Hidden/LightmapUvTool/TintedTexture");
            if (texShader == null) texShader = Shader.Find("Unlit/Transparent");
            if (texShader != null)
                TexMat = new Material(texShader) { hideFlags = HideFlags.HideAndDontSave };

            var spotShader = Shader.Find("Hidden/LightmapUvTool/SpotProjection");
            if (spotShader != null)
                SpotMat = new Material(spotShader) { hideFlags = HideFlags.HideAndDontSave };

            ShellOverlayMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            ShellOverlayMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            ShellOverlayMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            ShellOverlayMat.SetInt("_Cull", (int)CullMode.Back);
            ShellOverlayMat.SetInt("_ZWrite", 0);
            ShellOverlayMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        }

        public void Cleanup()
        {
            if (canvasRT) { canvasRT.Release(); UnityEngine.Object.DestroyImmediate(canvasRT); canvasRT = null; }
            if (GlMat) UnityEngine.Object.DestroyImmediate(GlMat);
            if (TexMat) UnityEngine.Object.DestroyImmediate(TexMat);
            if (SpotMat) UnityEngine.Object.DestroyImmediate(SpotMat);
            if (ShellOverlayMat) UnityEngine.Object.DestroyImmediate(ShellOverlayMat);
            GlMat = TexMat = SpotMat = ShellOverlayMat = null;
        }

        public void SetFillModes(List<FillModeEntry> modes)
        {
            FillModes = modes ?? new List<FillModeEntry>();
            ActiveFillModeIndex = Mathf.Clamp(ActiveFillModeIndex, 0, Mathf.Max(0, FillModes.Count - 1));
        }

        public void ClearHoverState(bool repaint = true)
        {
            HoverHitValid = false;
            HoveredShellId = -1;
            UvSpot = Vector2.zero;
            HoverWorldPos = Vector3.zero;
            CanvasSpotValid = false;
            HasHoveredShell = false;
            HasSelectedShell = false;
            HoveredShellDebug = null;
            SelectedShellDebug = null;
            lastHitMeshId = -1;
            lastHitShellId = -1;
            if (repaint) RequestRepaint?.Invoke();
        }

        // ════════════════════════════════════════════════════════════
        //  Main Draw
        // ════════════════════════════════════════════════════════════

        public void OnGUI(UvToolContext ctx, Action<UvCanvasView, float, float, float> toolOverlay)
        {
            var ee = ctx.ForLod(ctx.PreviewLod);
            if (ee.Count == 0) { EditorGUILayout.HelpBox("No meshes for this LOD.", MessageType.Info); HoveredShellDebug = null; return; }

            List<string> canvasGroupKeys = null;
            if (ctx.RepackPerMesh && ctx.IsolatedMeshGroup >= 0)
                canvasGroupKeys = ctx.BuildGroupKeys(ctx.PreviewLod);

            var draws = new List<ValueTuple<Mesh, MeshEntry, int>>();
            for (int i = 0; i < ee.Count; i++)
            {
                if (canvasGroupKeys != null && ctx.IsolatedMeshGroup >= 0 && ctx.IsolatedMeshGroup < canvasGroupKeys.Count)
                {
                    string eKey = ee[i].meshGroupKey ?? ee[i].renderer.name;
                    if (eKey != canvasGroupKeys[ctx.IsolatedMeshGroup]) continue;
                }
                Mesh m = ctx.DMesh(ee[i]);
                if (m != null) draws.Add(new ValueTuple<Mesh, MeshEntry, int>(m, ee[i], i));
            }
            if (draws.Count == 0) { HoveredShellDebug = null; return; }

            var canvasRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            LastCanvasRect = canvasRect;

            float baseSz = Mathf.Max(64, Mathf.Min(canvasRect.width, canvasRect.height));
            float sz = baseSz * Zoom;
            float cx = (canvasRect.width - sz) * 0.5f + Pan.x;
            float cy = (canvasRect.height - sz) * 0.5f + Pan.y;

            HoveredShellDebug = FindShellAtMouse(ctx, draws, canvasRect, cx, cy, sz);
            HandleCanvasInput(ctx, canvasRect, baseSz, sz, cx, cy);

            if (Event.current.type == EventType.Repaint && GlMat != null)
            {
                EditorGUI.DrawRect(canvasRect, new Color(.08f,.08f,.08f));

                int rtW = Mathf.Max(1, (int)canvasRect.width);
                int rtH = Mathf.Max(1, (int)canvasRect.height);
                if (canvasRT == null || canvasRT.width != rtW || canvasRT.height != rtH)
                {
                    if (canvasRT) { canvasRT.Release(); UnityEngine.Object.DestroyImmediate(canvasRT); }
                    canvasRT = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32);
                    canvasRT.hideFlags = HideFlags.HideAndDontSave;
                }

                var prevRT = RenderTexture.active;
                RenderTexture.active = canvasRT;
                GL.Clear(true, true, new Color(.08f,.08f,.08f, 1f));

                bool push = false;
                try
                {
                    GlMat.SetPass(0);
                    GL.PushMatrix(); push = true;
                    GL.LoadPixelMatrix(0, rtW, rtH, 0);

                    var occupiedTiles = (CurrentPreviewMode == PreviewMode.Lightmap)
                        ? new HashSet<Vector2Int> { new Vector2Int(0, 0) }
                        : GetOccupiedUdimTiles(ctx, draws, ctx.PreviewUvChannel);

                    GL.Begin(GL.QUADS);
                    GL.Color(new Color(.12f,.12f,.12f));
                    foreach (var tile in occupiedTiles)
                    {
                        float tx = cx + tile.x * sz, ty = cy - tile.y * sz;
                        GL.Vertex3(tx, ty, 0); GL.Vertex3(tx + sz, ty, 0);
                        GL.Vertex3(tx + sz, ty + sz, 0); GL.Vertex3(tx, ty + sz, 0);
                    }
                    GL.End();

                    Texture bgTex = ResolveUvPreviewBackgroundTexture(ctx, draws);
                    if (bgTex != null)
                    {
                        float bgAlpha = CheckerEnabled ? 0.33333f : (CurrentPreviewMode == PreviewMode.Lightmap ? 0.85f : 0.95f);
                        var bgTiles = (CurrentPreviewMode == PreviewMode.Lightmap)
                            ? new HashSet<Vector2Int> { new Vector2Int(0, 0) }
                            : occupiedTiles;
                        float bgExposure = CurrentPreviewMode == PreviewMode.Lightmap ? LmExposure : 1f;
                        GlTextureBg(cx, cy, sz, bgTex, Vector2.one, Vector2.zero, bgAlpha, bgTiles, bgExposure);
                        GlMat.SetPass(0);
                    }

                    if (bgTex == null && (CheckerEnabled || !FillHidden || ctx.PreviewUvChannel == 1))
                    {
                        float baseAlpha = ctx.PreviewUvChannel == 1 ? 0.24f : (CheckerEnabled ? 0.33333f : FillAlpha * 0.45f);
                        float checkerAlpha = Mathf.Clamp(baseAlpha, 0.06f, 0.33333f);
                        GlCheckerBg(cx, cy, sz, 8, checkerAlpha, ctx.PreviewUvChannel == 1, occupiedTiles);
                    }

                    GlGrid(cx, cy, sz, occupiedTiles);

                    ClearFrameCaches();
                    ctx.ShellColorKeyCacheDirty = false;

                    bool hasFill = !FillHidden && FillModes.Count > 0 && ActiveFillModeIndex >= 0 && ActiveFillModeIndex < FillModes.Count;

                    foreach (var item in draws)
                    {
                        Mesh mesh = item.Item1;
                        MeshEntry entry = item.Item2;
                        var uvs = RdUvCached(mesh, ctx.PreviewUvChannel);
                        var tri = GetTrianglesCached(mesh);
                        if (uvs == null || tri == null) continue;

                        if (CurrentPreviewMode == PreviewMode.Lightmap && ctx.PreviewUvChannel == 1 && entry.renderer != null && entry.renderer.lightmapIndex >= 0)
                        {
                            var so = entry.renderer.lightmapScaleOffset;
                            var transformed = new Vector2[uvs.Length];
                            for (int vi = 0; vi < uvs.Length; vi++)
                                transformed[vi] = new Vector2(uvs[vi].x * so.x + so.z, uvs[vi].y * so.y + so.w);
                            uvs = transformed;
                        }

                        int uN = uvs.Length, fN = tri.Length / 3;

                        if (hasFill)
                            FillModes[ActiveFillModeIndex].drawCallback?.Invoke(this, cx, cy, sz, mesh, entry);

                        HashSet<int> bdr = ShowBorder ? entry.transferState?.borderPrimitiveIds : null;
                        if (ShowBorder)
                        {
                            if (bdr != null && bdr.Count > 0) GlBdr(cx,cy,sz, uvs,tri,fN,uN, bdr);
                            else GlUvBoundary(ctx, cx, cy, sz, mesh, uvs, tri, uN);
                        }
                        if (ShowWireframe) GlWr(cx,cy,sz, uvs,tri,fN,uN);
                    }

                    toolOverlay?.Invoke(this, cx, cy, sz);

                    if (SpotMode) GlDrawUvSpot(cx, cy, sz);
                }
                catch (Exception ex) { UvtLog.Warn("[UV] GL: " + ex.Message); }
                finally { if (push) GL.PopMatrix(); }

                RenderTexture.active = prevRT;
                GUI.DrawTexture(canvasRect, canvasRT, ScaleMode.StretchToFill, false);
            }

            if (SpotMode)
                DrawShellDebugOverlay(canvasRect);
        }

        // ════════════════════════════════════════════════════════════
        //  Input Handling
        // ════════════════════════════════════════════════════════════

        void HandleCanvasInput(UvToolContext ctx, Rect canvasRect, float baseSz, float sz, float cx, float cy)
        {
            var e = Event.current;

            if (e.type == EventType.ScrollWheel && canvasRect.Contains(e.mousePosition))
            {
                float oldZoom = Zoom;
                float oldSz = baseSz * oldZoom;
                float factor = e.delta.y > 0 ? 0.9f : 1.1f;
                Zoom = Mathf.Clamp(Zoom * factor, 0.01f, 20f);
                float newSz = baseSz * Zoom;
                Vector2 local = e.mousePosition - canvasRect.position;
                float mx = local.x - cx, my = local.y - cy;
                Pan.x = local.x - mx * (newSz / oldSz) - (canvasRect.width - newSz) * 0.5f;
                Pan.y = local.y - my * (newSz / oldSz) - (canvasRect.height - newSz) * 0.5f;
                e.Use(); RequestRepaint?.Invoke();
            }

            bool startPan = canvasRect.Contains(e.mousePosition) &&
                ((e.type == EventType.MouseDown && e.button == 2) ||
                 (e.type == EventType.MouseDown && e.button == 0 && e.alt));
            if (startPan) { Panning = true; e.Use(); }
            if (e.type == EventType.MouseDrag && Panning) { Pan += e.delta; e.Use(); RequestRepaint?.Invoke(); }
            if (e.rawType == EventType.MouseUp && (e.button == 2 || e.button == 0)) Panning = false;

            if (e.type == EventType.MouseDown && e.button == 2 && e.clickCount == 2 && canvasRect.Contains(e.mousePosition))
            { FitToUvBounds(ctx); e.Use(); }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F && canvasRect.Contains(e.mousePosition))
            { FitToUvBounds(ctx); e.Use(); }

            if (!SpotMode) return;

            if (!canvasRect.Contains(e.mousePosition))
            {
                if (!LockSelection) HasHoveredShell = false;
                CanvasSpotValid = false;
                return;
            }

            Vector2 localPos = e.mousePosition - canvasRect.position;
            Vector2 uv = new Vector2((localPos.x - cx) / sz, 1f - ((localPos.y - cy) / sz));
            CanvasSpotUv = uv;
            CanvasSpotValid = true;

            if (!LockSelection)
            {
                HasHoveredShell = TryPickUvHit(ctx, uv, ref HoveredShell);
                if (!HasHoveredShell)
                {
                    HoveredShell.uvHit = uv;
                    HoveredShell.barycentric = new Vector3(1f/3f, 1f/3f, 1f/3f);
                }
            }

            RequestRepaint?.Invoke();
            SceneView.RepaintAll();

            if (SpotMode && e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (HasHoveredShell) { SelectedShell = HoveredShell; HasSelectedShell = true; }
                else if (!LockSelection) HasSelectedShell = false;
                if (HoveredShellDebug != null) SelectedShellDebug = CloneHit(HoveredShellDebug);
                else SelectedShellDebug = null;

                // Double-click → focus SceneView camera on shell
                if (e.clickCount == 2 && HasSelectedShell)
                {
                    try
                    {
                        OnDoubleClickShell?.Invoke(SelectedShell);
                    }
                    catch (Exception ex)
                    {
                        UvtLog.Error($"[UV Canvas] Double-click shell focus failed: {ex.Message}");
                    }
                }

                e.Use(); RequestRepaint?.Invoke(); SceneView.RepaintAll();
            }
        }

        public void FitToUvBounds(UvToolContext ctx)
        {
            var ee = ctx.ForLod(ctx.PreviewLod);
            float minU=float.MaxValue, minV=float.MaxValue, maxU=float.MinValue, maxV=float.MinValue;
            bool any = false;
            foreach (var entry in ee)
            {
                Mesh m = ctx.DMesh(entry);
                if (m == null) continue;
                var uvs = RdUv(m, ctx.PreviewUvChannel);
                if (uvs == null) continue;
                foreach (var u in uvs) { if (!UOk(u)) continue; minU=Mathf.Min(minU,u.x); maxU=Mathf.Max(maxU,u.x); minV=Mathf.Min(minV,u.y); maxV=Mathf.Max(maxV,u.y); any=true; }
            }
            if (!any) { Zoom=1f; Pan=Vector2.zero; RequestRepaint?.Invoke(); return; }
            FocusUvBounds(minU, minV, maxU, maxV);
        }

        public void FocusUvBounds(float minU, float minV, float maxU, float maxV)
        {
            float pad = 0.05f;
            minU -= pad; maxU += pad; minV -= pad; maxV += pad;
            float rangeU = Mathf.Max(maxU - minU, 0.1f);
            float rangeV = Mathf.Max(maxV - minV, 0.1f);
            float W = LastCanvasRect.width, H = LastCanvasRect.height;
            if (W < 64 || H < 64) { Zoom = 1f; Pan = Vector2.zero; RequestRepaint?.Invoke(); return; }
            float baseSz = Mathf.Max(64, Mathf.Min(W, H));
            float zoomU = W / (baseSz * rangeU);
            float zoomV = H / (baseSz * rangeV);
            Zoom = Mathf.Clamp(Mathf.Min(zoomU, zoomV) * 0.92f, 0.01f, 20f);
            float sz = baseSz * Zoom;
            float centerU = (minU + maxU) * 0.5f;
            float centerV = (minV + maxV) * 0.5f;
            Pan.x = sz * (0.5f - centerU);
            Pan.y = sz * (centerV - 0.5f);
            RequestRepaint?.Invoke();
        }

        // ════════════════════════════════════════════════════════════
        //  GL Helpers
        // ════════════════════════════════════════════════════════════

        public static bool UOk(Vector2 u) => u.x >= UV_LO && u.x <= UV_HI && u.y >= UV_LO && u.y <= UV_HI && !float.IsNaN(u.x) && !float.IsNaN(u.y) && !float.IsInfinity(u.x) && !float.IsInfinity(u.y);
        public static bool TOk(Vector2[] u, int n, int a, int b, int c) => a>=0&&a<n&&b>=0&&b<n&&c>=0&&c<n && UOk(u[a])&&UOk(u[b])&&UOk(u[c]);
        public static void Vx(float ox, float oy, float sz, Vector2 u) => GL.Vertex3(ox+u.x*sz, oy+(1f-u.y)*sz, 0);

        public static Vector2[] RdUv(Mesh m, int ch) { var l = new List<Vector2>(); m.GetUVs(ch, l); return l.Count > 0 ? l.ToArray() : null; }

        public Vector2[] RdUvCached(Mesh m, int ch)
        {
            long key = ((long)m.GetInstanceID() << 8) ^ (uint)ch;
            if (cachedUvs.TryGetValue(key, out var uv)) return uv;
            uv = RdUv(m, ch);
            cachedUvs[key] = uv;
            return uv;
        }

        public int[] GetTrianglesCached(Mesh m)
        {
            int id = m.GetInstanceID();
            if (cachedTriangles.TryGetValue(id, out var tri)) return tri;
            tri = m.triangles;
            cachedTriangles[id] = tri;
            return tri;
        }

        public void ClearFrameCaches() { cachedTriangles.Clear(); cachedUvs.Clear(); }

        public static Color SC(TriangleStatus s)
        {
            switch (s)
            {
                case TriangleStatus.Accepted: return cAccept;
                case TriangleStatus.Ambiguous: return cAmbig;
                case TriangleStatus.BorderRisk: return new Color(1f,.5f,.1f,.5f);
                case TriangleStatus.UnavoidableMismatch: return cMis;
                case TriangleStatus.Rejected: return cReject;
                default: return cNone;
            }
        }

        public static int VoteBestShell(int[] vertToShell, int vertCount, int i0, int i1, int i2)
        {
            int s0 = (i0 >= 0 && i0 < vertToShell.Length) ? vertToShell[i0] : -1;
            int s1 = (i1 >= 0 && i1 < vertToShell.Length) ? vertToShell[i1] : -1;
            int s2 = (i2 >= 0 && i2 < vertToShell.Length) ? vertToShell[i2] : -1;
            if (s0 >= 0 && s0 == s1) return s0;
            if (s0 >= 0 && s0 == s2) return s0;
            if (s1 >= 0 && s1 == s2) return s1;
            if (s0 >= 0) return s0;
            if (s1 >= 0) return s1;
            return s2;
        }

        // ════════════════════════════════════════════════════════════
        //  GL Draw Methods
        // ════════════════════════════════════════════════════════════

        public void GlGrid(float ox, float oy, float sz, HashSet<Vector2Int> occupiedTiles = null)
        {
            GL.Begin(GL.LINES);
            GL.Color(new Color(.25f,.25f,.25f));
            for (int g = 0; g <= 4; g++) { float p = g*.25f*sz; GL.Vertex3(ox+p,oy,0); GL.Vertex3(ox+p,oy+sz,0); GL.Vertex3(ox,oy+p,0); GL.Vertex3(ox+sz,oy+p,0); }
            GL.Color(new Color(.5f,.5f,.5f));
            GL.Vertex3(ox,oy,0); GL.Vertex3(ox+sz,oy,0);
            GL.Vertex3(ox+sz,oy,0); GL.Vertex3(ox+sz,oy+sz,0);
            GL.Vertex3(ox+sz,oy+sz,0); GL.Vertex3(ox,oy+sz,0);
            GL.Vertex3(ox,oy+sz,0); GL.Vertex3(ox,oy,0);
            GL.Color(new Color(.3f,.3f,.3f,.4f));
            if (occupiedTiles != null)
            {
                foreach (var tile in occupiedTiles)
                {
                    if (tile.x == 0 && tile.y == 0) continue;
                    float tx = ox + tile.x * sz, ty = oy - tile.y * sz;
                    GL.Vertex3(tx,ty,0); GL.Vertex3(tx+sz,ty,0);
                    GL.Vertex3(tx+sz,ty,0); GL.Vertex3(tx+sz,ty+sz,0);
                    GL.Vertex3(tx+sz,ty+sz,0); GL.Vertex3(tx,ty+sz,0);
                    GL.Vertex3(tx,ty+sz,0); GL.Vertex3(tx,ty,0);
                }
            }
            GL.End();
        }

        public void GlCheckerBg(float ox, float oy, float sz, int cells, float alpha, bool neutralGray = false, HashSet<Vector2Int> occupiedTiles = null)
        {
            if (cells <= 0 || alpha <= 0f) return;
            float cell = sz / cells;
            GL.Begin(GL.QUADS);
            Color darkColor = neutralGray ? new Color(.24f,.24f,.24f,alpha) : new Color(.20f,.20f,.20f,alpha);
            Color lightColor = neutralGray ? new Color(.32f,.32f,.32f,alpha) : new Color(.38f,.38f,.38f,alpha);
            var tilesToDraw = occupiedTiles ?? new HashSet<Vector2Int> { new Vector2Int(0, 0) };
            foreach (var tile in tilesToDraw)
            {
                float tox = ox + tile.x * sz, toy = oy - tile.y * sz;
                for (int y = 0; y < cells; y++)
                    for (int x = 0; x < cells; x++)
                    {
                        bool dark = ((x + y) & 1) == 0;
                        GL.Color(dark ? darkColor : lightColor);
                        float x0 = tox + x * cell, y0 = toy + y * cell;
                        GL.Vertex3(x0, y0, 0); GL.Vertex3(x0+cell, y0, 0); GL.Vertex3(x0+cell, y0+cell, 0); GL.Vertex3(x0, y0+cell, 0);
                    }
            }
            GL.End();
        }

        public void GlTextureBg(float ox, float oy, float sz, Texture tex, Vector2 tiling, Vector2 offset, float alpha, HashSet<Vector2Int> occupiedTiles = null, float exposure = 1f)
        {
            if (tex == null || TexMat == null) return;
            float e = Mathf.Clamp(exposure, 0f, 10f);
            TexMat.mainTexture = tex;
            TexMat.SetColor("_Color", new Color(e, e, e, Mathf.Clamp01(alpha)));
            TexMat.SetPass(0);
            var tilesToDraw = occupiedTiles ?? new HashSet<Vector2Int> { new Vector2Int(0, 0) };
            GL.Begin(GL.QUADS);
            foreach (var tile in tilesToDraw)
            {
                float tx = ox + tile.x * sz, ty = oy - tile.y * sz;
                GL.TexCoord2(offset.x, offset.y + tiling.y);
                GL.Vertex3(tx, ty, 0);
                GL.TexCoord2(offset.x + tiling.x, offset.y + tiling.y);
                GL.Vertex3(tx + sz, ty, 0);
                GL.TexCoord2(offset.x + tiling.x, offset.y);
                GL.Vertex3(tx + sz, ty + sz, 0);
                GL.TexCoord2(offset.x, offset.y);
                GL.Vertex3(tx, ty + sz, 0);
            }
            GL.End();
        }

        public void GlDrawUvSpot(float ox, float oy, float sz)
        {
            Vector2 drawUv;
            if (HoverHitValid) drawUv = UvSpot;
            else if (HasHoveredShell) drawUv = HoveredShell.uvHit;
            else if (CanvasSpotValid) drawUv = CanvasSpotUv;
            else return;

            float px = ox + drawUv.x * sz;
            float py = oy + (1f - drawUv.y) * sz;
            float crossR = Mathf.Max(0.012f * sz, 4f);
            float crossHalfW = Mathf.Max(1.5f, crossR * 0.12f);
            Color markerColor = new Color32(0xFF, 0xBC, 0x51, 0xFF);

            GL.Begin(GL.QUADS);
            GL.Color(markerColor);
            GL.Vertex3(px-crossR, py-crossHalfW, 0); GL.Vertex3(px+crossR, py-crossHalfW, 0);
            GL.Vertex3(px+crossR, py+crossHalfW, 0); GL.Vertex3(px-crossR, py+crossHalfW, 0);
            GL.Vertex3(px-crossHalfW, py-crossR, 0); GL.Vertex3(px+crossHalfW, py-crossR, 0);
            GL.Vertex3(px+crossHalfW, py+crossR, 0); GL.Vertex3(px-crossHalfW, py+crossR, 0);
            GL.End();
        }

        public void GlFillSh(UvToolContext ctx, float ox, float oy, float sz, Mesh mesh, int fN, int uN, MeshEntry entry, int hoverShellId, int selectedShellId, Vector2[] uvOverride = null)
        {
            var cache = GetPreviewShellCache(ctx, mesh, ctx.PreviewUvChannel);
            if (cache == null || cache.shells == null) return;
            var uv = uvOverride ?? cache.uvs;
            var t = cache.triangles;

            int tot=0, b=0;
            GL.Begin(GL.TRIANGLES);
            foreach (var s in cache.shells)
            {
                if (tot>=MAX_TRI) break;
                int colorKey = GetShellColorKey(ctx, s, entry);
                Color c = pal[colorKey % pal.Length];
                if (s.shellId == selectedShellId)
                    c = Color.Lerp(c, Color.white, 0.45f);
                c.a = s.shellId == selectedShellId ? Mathf.Clamp01(FillAlpha * 1.85f) : FillAlpha;
                GL.Color(c);
                foreach (int f in s.faceIndices)
                {
                    if (tot>=MAX_TRI) break;
                    int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                    if (!TOk(uv,uN,a0,a1,a2)) continue;
                    Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                    tot++; b++; if (b>=BATCH){GL.End();GL.Begin(GL.TRIANGLES);GL.Color(c);b=0;}
                }
            }
            GL.End();

            if (selectedShellId >= 0)
                GlOutlineShell(ox, oy, sz, uv, t, uN, cache, selectedShellId, new Color(1f, .95f, .2f, .95f));
            if (hoverShellId >= 0 && hoverShellId != selectedShellId)
                GlOutlineShell(ox, oy, sz, uv, t, uN, cache, hoverShellId, new Color(.25f, 1f, .95f, .85f));
        }

        public void GlFillSt(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, TriangleStatus[] st)
        {
            int tot=0,b=0; GL.Begin(GL.TRIANGLES);
            for (int f=0; f<fN&&tot<MAX_TRI; f++)
            {
                int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                GL.Color(f<st.Length ? SC(st[f]) : cAccept);
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                tot++; b++; if (b>=BATCH){GL.End();GL.Begin(GL.TRIANGLES);b=0;}
            }
            GL.End();
        }

        public void GlBdr(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, HashSet<int> bp)
        {
            int b=0; Color c=new Color(1f,.3f,0f,.35f);
            GL.Begin(GL.TRIANGLES); GL.Color(c);
            foreach (int f in bp)
            {
                if (f>=fN) continue;
                int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                b++; if (b>=BATCH){GL.End();GL.Begin(GL.TRIANGLES);GL.Color(c);b=0;}
            }
            GL.End();
        }

        public void GlWr(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN)
        {
            int tot=0,b=0; Color c=new Color(.3f,.8f,1f,.35f);
            GL.Begin(GL.LINES); GL.Color(c);
            for (int f=0; f<fN&&tot<MAX_TRI; f++)
            {
                int a0=t[f*3],a1=t[f*3+1],a2=t[f*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]);
                Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                Vx(ox,oy,sz,uv[a2]); Vx(ox,oy,sz,uv[a0]);
                tot++; b++; if (b>=BATCH){GL.End();GL.Begin(GL.LINES);GL.Color(c);b=0;}
            }
            GL.End();
        }

        public void GlUvBoundary(UvToolContext ctx, float ox, float oy, float sz, Mesh mesh, Vector2[] uv, int[] tri, int uN)
        {
            if (mesh == null || uv == null || tri == null || tri.Length < 3) return;
            int id = mesh.GetInstanceID();
            if (!ctx.BoundaryEdgeCache.TryGetValue(id, out int[] pairs))
            {
                pairs = BuildBoundaryEdgePairs(tri);
                ctx.BoundaryEdgeCache[id] = pairs;
            }
            if (pairs == null || pairs.Length == 0) return;
            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, .35f, .05f, .9f));
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                int a = pairs[i], b = pairs[i + 1];
                if (a < 0 || b < 0 || a >= uN || b >= uN) continue;
                if (!UOk(uv[a]) || !UOk(uv[b])) continue;
                Vx(ox, oy, sz, uv[a]); Vx(ox, oy, sz, uv[b]);
            }
            GL.End();
        }

        public void GlOutlineShell(float ox, float oy, float sz, Vector2[] uv, int[] t, int uN, PreviewShellData cache, int shellId, Color color)
        {
            if (cache == null || cache.shellById == null) return;
            if (!cache.shellById.TryGetValue(shellId, out var shell)) return;
            GL.Begin(GL.LINES);
            GL.Color(color);
            foreach (int fi in shell.faceIndices)
            {
                int a0=t[fi*3],a1=t[fi*3+1],a2=t[fi*3+2];
                if (!TOk(uv,uN,a0,a1,a2)) continue;
                Vx(ox,oy,sz,uv[a0]); Vx(ox,oy,sz,uv[a1]);
                Vx(ox,oy,sz,uv[a1]); Vx(ox,oy,sz,uv[a2]);
                Vx(ox,oy,sz,uv[a2]); Vx(ox,oy,sz,uv[a0]);
            }
            GL.End();
        }

        public void GlFillShellMatch(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, int[] vertShellMap)
        {
            if (vertShellMap == null) return;
            int tot = 0, b = 0;
            GL.Begin(GL.TRIANGLES);
            for (int f = 0; f < fN && tot < MAX_TRI; f++)
            {
                int a0 = t[f*3], a1 = t[f*3+1], a2 = t[f*3+2];
                if (!TOk(uv, uN, a0, a1, a2)) continue;
                int sh = VoteBestShell(vertShellMap, uN, a0, a1, a2);
                Color nc = sh < 0
                    ? new Color(0.3f, 0.3f, 0.3f, FillAlpha)
                    : new Color(pal[sh % pal.Length].r, pal[sh % pal.Length].g, pal[sh % pal.Length].b, FillAlpha * 1.5f);
                GL.Color(nc);
                Vx(ox, oy, sz, uv[a0]); Vx(ox, oy, sz, uv[a1]); Vx(ox, oy, sz, uv[a2]);
                tot++; b++;
                if (b >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); b = 0; }
            }
            GL.End();
        }

        public void GlFillValidation(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, TransferValidator.TriIssue[] perTri)
        {
            if (perTri == null) return;
            var mask = ValidationFilterMask;
            int tot = 0, b = 0;
            GL.Begin(GL.TRIANGLES);
            for (int f = 0; f < fN && tot < MAX_TRI; f++)
            {
                int a0 = t[f*3], a1 = t[f*3+1], a2 = t[f*3+2];
                if (!TOk(uv, uN, a0, a1, a2)) continue;
                var fl = (f < perTri.Length) ? perTri[f] : TransferValidator.TriIssue.None;
                if (mask != TransferValidator.TriIssue.None && (fl & mask) == 0) continue;
                Color nc;
                if      ((fl & TransferValidator.TriIssue.ZeroArea) != 0)    nc = cValZero;
                else if ((fl & TransferValidator.TriIssue.Stretched) != 0)   nc = cValStretch;
                else if ((fl & TransferValidator.TriIssue.Overlap) != 0)     nc = cValOverlap;
                else if ((fl & TransferValidator.TriIssue.OutOfBounds) != 0) nc = cValOOB;
                else if ((fl & TransferValidator.TriIssue.TexelDensity) != 0)nc = cValTexel;
                else nc = cValClean;
                GL.Color(nc);
                Vx(ox, oy, sz, uv[a0]); Vx(ox, oy, sz, uv[a1]); Vx(ox, oy, sz, uv[a2]);
                tot++; b++;
                if (b >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); b = 0; }
            }
            GL.End();
        }

        public void GlFillValidationOverlay(float ox, float oy, float sz, Vector2[] uv, int[] t, int fN, int uN, TransferValidator.TriIssue[] perTri)
        {
            if (perTri == null) return;
            var mask = ValidationFilterMask;
            int tot = 0, b = 0;
            GL.Begin(GL.TRIANGLES);
            for (int f = 0; f < fN && tot < MAX_TRI; f++)
            {
                var fl = (f < perTri.Length) ? perTri[f] : TransferValidator.TriIssue.None;
                if (fl == TransferValidator.TriIssue.None) continue;
                if (mask != TransferValidator.TriIssue.None && (fl & mask) == 0) continue;
                int a0 = t[f*3], a1 = t[f*3+1], a2 = t[f*3+2];
                if (!TOk(uv, uN, a0, a1, a2)) continue;
                Color nc;
                if      ((fl & TransferValidator.TriIssue.ZeroArea) != 0)    nc = cValZero;
                else if ((fl & TransferValidator.TriIssue.Stretched) != 0)   nc = cValStretch;
                else if ((fl & TransferValidator.TriIssue.Overlap) != 0)     nc = cValOverlap;
                else if ((fl & TransferValidator.TriIssue.OutOfBounds) != 0) nc = cValOOB;
                else if ((fl & TransferValidator.TriIssue.TexelDensity) != 0)nc = cValTexel;
                else continue;
                GL.Color(nc);
                Vx(ox, oy, sz, uv[a0]); Vx(ox, oy, sz, uv[a1]); Vx(ox, oy, sz, uv[a2]);
                tot++; b++;
                if (b >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); b = 0; }
            }
            GL.End();
        }

        // ════════════════════════════════════════════════════════════
        //  Shell Cache & Color
        // ════════════════════════════════════════════════════════════

        public PreviewShellData GetPreviewShellCache(UvToolContext ctx, Mesh mesh, int channel)
        {
            if (mesh == null) return null;
            long key = ((long)mesh.GetInstanceID() << 8) ^ (uint)channel;
            if (ctx.PreviewShellDataCache.TryGetValue(key, out var cached)) return cached;

            var uv = RdUvCached(mesh, channel);
            var triangles = GetTrianglesCached(mesh);
            if (uv == null || triangles == null || triangles.Length < 3) return null;

            List<UvShell> shells;
            try { shells = UvShellExtractor.Extract(uv, triangles, computeDescriptors: true); }
            catch { return null; }

            var faceToShell = new Dictionary<int, int>(triangles.Length / 3);
            var shellById = new Dictionary<int, UvShell>(shells.Count);
            var bounds = new Bounds[shells.Count];
            for (int i = 0; i < shells.Count; i++)
            {
                var shell = shells[i];
                shellById[shell.shellId] = shell;
                bool hasPoint = false;
                Bounds b = new Bounds(Vector3.zero, Vector3.zero);
                foreach (int fi in shell.faceIndices)
                {
                    faceToShell[fi] = shell.shellId;
                    int t0 = fi * 3;
                    if (t0 + 2 >= triangles.Length) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = triangles[t0 + k];
                        if (vi < 0 || vi >= uv.Length) continue;
                        Vector3 p = uv[vi];
                        if (!hasPoint) { b = new Bounds(p, Vector3.zero); hasPoint = true; }
                        else b.Encapsulate(p);
                    }
                }
                bounds[i] = b;
            }

            cached = new PreviewShellData { shells = shells, faceToShell = faceToShell, shellById = shellById, shellBounds = bounds, triangles = triangles, uvs = uv };
            ctx.PreviewShellDataCache[key] = cached;
            return cached;
        }

        public HashSet<Vector2Int> GetOccupiedUdimTiles(UvToolContext ctx, List<ValueTuple<Mesh, MeshEntry, int>> draws, int channel)
        {
            var tiles = new HashSet<Vector2Int>();
            foreach (var item in draws)
            {
                var mesh = item.Item1;
                long key = ((long)mesh.GetInstanceID() << 8) ^ (uint)channel;
                if (ctx.OccupiedTilesPerMesh.TryGetValue(key, out var c)) { foreach (var t in c) tiles.Add(t); continue; }
                var perMesh = new HashSet<Vector2Int>();
                var uvs = RdUvCached(mesh, channel);
                if (uvs != null)
                    for (int i = 0; i < uvs.Length; i++)
                    {
                        var u = uvs[i];
                        if (!UOk(u)) continue;
                        var tile = new Vector2Int(Mathf.FloorToInt(u.x), Mathf.FloorToInt(u.y));
                        perMesh.Add(tile); tiles.Add(tile);
                    }
                ctx.OccupiedTilesPerMesh[key] = perMesh;
            }
            return tiles;
        }

        /// <summary>
        /// Compute a UV bounding-box hash for the shell — stable across LODs because
        /// UV2 is transferred from source, and the bbox corners (min/max) are determined
        /// by extreme vertices which survive simplification, unlike the centroid which
        /// shifts when interior vertices are removed non-uniformly.
        /// </summary>
        static int ShellBBoxHash(UvShell shell, Mesh mesh, int uvChannel = 1)
        {
            if (shell?.vertexIndices == null || shell.vertexIndices.Count == 0 || mesh == null)
                return shell?.shellId ?? 0;
            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs.Count != mesh.vertexCount)
            {
                mesh.GetUVs(0, uvs);
                if (uvs.Count != mesh.vertexCount) return shell.shellId;
            }
            Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 mx = new Vector2(float.MinValue, float.MinValue);
            foreach (int vi in shell.vertexIndices)
            {
                if (vi < 0 || vi >= uvs.Count) continue;
                var uv = uvs[vi];
                if (uv.x < mn.x) mn.x = uv.x; if (uv.y < mn.y) mn.y = uv.y;
                if (uv.x > mx.x) mx.x = uv.x; if (uv.y > mx.y) mx.y = uv.y;
            }
            // Hash bbox center — quantize at 100x for robustness
            Vector2 center = (mn + mx) * 0.5f;
            int qx = Mathf.RoundToInt(center.x * 100f);
            int qy = Mathf.RoundToInt(center.y * 100f);
            // Include bbox size to distinguish overlapping shells of different sizes
            int qw = Mathf.RoundToInt((mx.x - mn.x) * 100f);
            int qh = Mathf.RoundToInt((mx.y - mn.y) * 100f);
            return Mathf.Abs((qx * 73856093) ^ (qy * 19349663) ^ (qw * 83492791) ^ (qh * 41729381));
        }

        public int GetShellColorKey(UvToolContext ctx, UvShell shell, MeshEntry entry)
        {
            int meshId = 0;
            var mesh = entry != null ? ctx.DMesh(entry) : null;
            if (mesh != null) meshId = mesh.GetInstanceID();
            long cacheKey = ((long)meshId << 32) | (uint)shell.shellId;
            if (!ctx.ShellColorKeyCacheDirty && ctx.ShellColorKeyCache.TryGetValue(cacheKey, out int cc)) return cc;

            // Always use UV bounding-box hash for maximum cross-LOD consistency.
            // Previous strategies (shellTransferResult, UV0 shell map) produced
            // extraction-order-dependent keys that changed between LODs.
            int result = ShellBBoxHash(shell, mesh);
            ctx.ShellColorKeyCache[cacheKey] = result;
            return result;
        }

        public (int[] vertToShell, ShellDescriptor[] descs) GetUv0ShellMap(UvToolContext ctx, Mesh mesh)
        {
            if (mesh == null) return (null, null);
            int id = mesh.GetInstanceID();
            if (ctx.Uv0ShellMapCache.TryGetValue(id, out var cached)) return cached;
            var uv0List = new List<Vector2>();
            mesh.GetUVs(0, uv0List);
            if (uv0List.Count != mesh.vertexCount) { ctx.Uv0ShellMapCache[id] = (null, null); return (null, null); }
            try
            {
                var uv0 = uv0List.ToArray();
                var shells = UvShellExtractor.Extract(uv0, mesh.triangles, computeDescriptors: true);
                var vertToShell = new int[mesh.vertexCount];
                for (int i = 0; i < vertToShell.Length; i++) vertToShell[i] = -1;
                var descs = new ShellDescriptor[shells.Count];
                for (int si = 0; si < shells.Count; si++)
                {
                    descs[si] = shells[si].descriptor;
                    foreach (int vi in shells[si].vertexIndices)
                        if (vi >= 0 && vi < vertToShell.Length) vertToShell[vi] = si;
                }
                ctx.Uv0ShellMapCache[id] = (vertToShell, descs);
                return (vertToShell, descs);
            }
            catch { ctx.Uv0ShellMapCache[id] = (null, null); return (null, null); }
        }

        // ════════════════════════════════════════════════════════════
        //  Shell Picking
        // ════════════════════════════════════════════════════════════

        public bool TryPickUvHit(UvToolContext ctx, Vector2 uv, ref ShellUvHit hit)
        {
            var ee = FilteredEntries(ctx);
            int checkedTri = 0;
            bool fallbackAssigned = false;
            ShellUvHit fallback = default;
            foreach (var entry in ee)
            {
                Mesh mesh = ctx.DMesh(entry);
                if (mesh == null) continue;
                var cache = GetPreviewShellCache(ctx, mesh, ctx.PreviewUvChannel);
                if (cache == null || cache.shells == null) continue;
                if (mesh.GetInstanceID() == lastHitMeshId && lastHitShellId >= 0)
                    if (TryPickInShell(entry, cache, uv, lastHitShellId, ref checkedTri, ref hit)) return true;
                for (int si = 0; si < cache.shells.Count; si++)
                {
                    var sb = cache.shellBounds[si];
                    if (sb.size == Vector3.zero) continue;
                    if (uv.x < sb.min.x || uv.x > sb.max.x || uv.y < sb.min.y || uv.y > sb.max.y) continue;
                    int shellId = cache.shells[si].shellId;
                    if (TryPickInShell(entry, cache, uv, shellId, ref checkedTri, ref hit)) return true;
                    if (!fallbackAssigned)
                    {
                        fallbackAssigned = true;
                        fallback = new ShellUvHit { meshEntry = entry, shellId = shellId, faceIndex = -1, uvHit = uv, barycentric = new Vector3(1f/3f, 1f/3f, 1f/3f) };
                    }
                }
                if (checkedTri >= TRI_PICK_BUDGET) break;
            }
            if (fallbackAssigned) { hit = fallback; var m = ctx.DMesh(fallback.meshEntry); lastHitMeshId = m != null ? m.GetInstanceID() : -1; lastHitShellId = fallback.shellId; return true; }
            return false;
        }

        bool TryPickInShell(MeshEntry entry, PreviewShellData cache, Vector2 uv, int shellId, ref int checkedTri, ref ShellUvHit hit)
        {
            if (!cache.shellById.TryGetValue(shellId, out var shell)) return false;
            foreach (int fi in shell.faceIndices)
            {
                if (checkedTri++ >= TRI_PICK_BUDGET) return false;
                int t0 = fi * 3;
                if (t0 + 2 >= cache.triangles.Length) continue;
                int a = cache.triangles[t0], b = cache.triangles[t0+1], c = cache.triangles[t0+2];
                if (!TOk(cache.uvs, cache.uvs.Length, a, b, c)) continue;
                if (TryBarycentric(uv, cache.uvs[a], cache.uvs[b], cache.uvs[c], out Vector3 bary))
                {
                    hit = new ShellUvHit { meshEntry = entry, shellId = shellId, faceIndex = fi, uvHit = uv, barycentric = bary };
                    var m = entry.originalMesh;
                    lastHitMeshId = m != null ? m.GetInstanceID() : -1;
                    lastHitShellId = shellId;
                    return true;
                }
            }
            return false;
        }

        public static bool TryBarycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 bary)
        {
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector2.Dot(v0, v0), d01 = Vector2.Dot(v0, v1), d11 = Vector2.Dot(v1, v1);
            float d20 = Vector2.Dot(v2, v0), d21 = Vector2.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-8f) { bary = default; return false; }
            float v = (d11 * d20 - d01 * d21) / denom;
            float w = (d00 * d21 - d01 * d20) / denom;
            float u = 1f - v - w;
            bary = new Vector3(u, v, w);
            const float eps = -1e-4f;
            return u >= eps && v >= eps && w >= eps;
        }

        List<MeshEntry> FilteredEntries(UvToolContext ctx)
        {
            var ee = ctx.ForLod(ctx.PreviewLod);
            if (!ctx.RepackPerMesh || ctx.IsolatedMeshGroup < 0) return ee;
            var keys = ctx.BuildGroupKeys(ctx.PreviewLod);
            if (ctx.IsolatedMeshGroup >= keys.Count) return ee;
            string isoKey = keys[ctx.IsolatedMeshGroup];
            return ee.Where(e => (e.meshGroupKey ?? e.renderer.name) == isoKey).ToList();
        }

        // ════════════════════════════════════════════════════════════
        //  Boundary Edges
        // ════════════════════════════════════════════════════════════

        public static int[] BuildBoundaryEdgePairs(int[] tri)
        {
            if (tri == null || tri.Length < 3) return Array.Empty<int>();
            var counts = new Dictionary<ulong, int>(tri.Length);
            var orient = new Dictionary<ulong, (int a, int b)>(tri.Length);
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                AddEdge(tri[i], tri[i+1], counts, orient);
                AddEdge(tri[i+1], tri[i+2], counts, orient);
                AddEdge(tri[i+2], tri[i], counts, orient);
            }
            var result = new List<int>();
            foreach (var kv in counts)
            {
                if (kv.Value != 1) continue;
                var e = orient[kv.Key];
                result.Add(e.a); result.Add(e.b);
            }
            return result.ToArray();
        }

        static void AddEdge(int a, int b, Dictionary<ulong, int> counts, Dictionary<ulong, (int a, int b)> orient)
        {
            if (a == b) return;
            int lo = a < b ? a : b, hi = a < b ? b : a;
            ulong key = ((ulong)(uint)lo << 32) | (uint)hi;
            counts.TryGetValue(key, out int c); counts[key] = c + 1;
            if (!orient.ContainsKey(key)) orient[key] = (a, b);
        }

        // ════════════════════════════════════════════════════════════
        //  Background Texture
        // ════════════════════════════════════════════════════════════

        Texture ResolveUvPreviewBackgroundTexture(UvToolContext ctx, List<ValueTuple<Mesh, MeshEntry, int>> draws)
        {
            if (CheckerEnabled) return CheckerTexturePreview.GetCheckerTexture();
            if (CurrentPreviewMode == PreviewMode.Lightmap)
            {
                foreach (var item in draws)
                {
                    var renderer = item.Item2.renderer;
                    if (renderer == null) continue;
                    int lmIdx = renderer.lightmapIndex;
                    if (lmIdx >= 0 && lmIdx < LightmapSettings.lightmaps.Length)
                    {
                        var lm = LightmapSettings.lightmaps[lmIdx];
                        if (lm.lightmapColor != null) return lm.lightmapColor;
                    }
                }
                return null;
            }
            if (ctx.PreviewUvChannel == 1) return null;
            foreach (var item in draws)
            {
                var renderer = item.Item2.renderer;
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                if (mats == null) continue;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null || !mat.HasProperty("_MainTex")) continue;
                    var tex = mat.mainTexture;
                    if (tex != null) return tex;
                }
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  Shell Debug
        // ════════════════════════════════════════════════════════════

        ShellDebugHit FindShellAtMouse(UvToolContext ctx, List<ValueTuple<Mesh, MeshEntry, int>> draws, Rect canvasRect, float cx, float cy, float sz)
        {
            var mouse = Event.current.mousePosition;
            if (!canvasRect.Contains(mouse)) return null;
            Vector2 local = mouse - canvasRect.position;
            var uvPoint = new Vector2((local.x - cx) / sz, 1f - ((local.y - cy) / sz));
            foreach (var item in draws)
            {
                Mesh mesh = item.Item1;
                if (uvPoint.x < UV_LO || uvPoint.x > UV_HI || uvPoint.y < UV_LO || uvPoint.y > UV_HI) continue;
                var cache = GetPreviewShellCache(ctx, mesh, ctx.PreviewUvChannel);
                if (cache == null || cache.shells == null) continue;
                for (int f = 0; f < cache.triangles.Length / 3; f++)
                {
                    int a = cache.triangles[f*3], b = cache.triangles[f*3+1], c = cache.triangles[f*3+2];
                    if (!TOk(cache.uvs, cache.uvs.Length, a, b, c)) continue;
                    if (!PointInTriangle(uvPoint, cache.uvs[a], cache.uvs[b], cache.uvs[c])) continue;
                    if (!cache.faceToShell.TryGetValue(f, out int shellId)) return null;
                    if (!cache.shellById.TryGetValue(shellId, out var shell)) return null;
                    return BuildHit(ctx, item.Item2, mesh, shell, uvPoint, item.Item3);
                }
            }
            return null;
        }

        ShellDebugHit BuildHit(UvToolContext ctx, MeshEntry entry, Mesh mesh, UvShell shell, Vector2 uvPoint, int drawIndex)
        {
            int tu = Mathf.FloorToInt(uvPoint.x), tv = Mathf.FloorToInt(uvPoint.y);
            return new ShellDebugHit
            {
                entry = entry, mesh = mesh, shell = shell, shellId = shell.shellId,
                uvChannel = ctx.PreviewUvChannel, hoverUv = uvPoint,
                tileU = tu, tileV = tv, localUv = new Vector2(uvPoint.x - tu, uvPoint.y - tv),
                drawIndex = drawIndex
            };
        }

        public static ShellDebugHit CloneHit(ShellDebugHit src)
        {
            if (src == null) return null;
            return new ShellDebugHit
            {
                entry = src.entry, mesh = src.mesh, shell = src.shell, shellId = src.shellId,
                uvChannel = src.uvChannel, hoverUv = src.hoverUv, tileU = src.tileU, tileV = src.tileV,
                localUv = src.localUv, drawIndex = src.drawIndex
            };
        }

        public static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float s1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            float s2 = (c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x);
            float s3 = (a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - c.x);
            return !((s1 < 0f || s2 < 0f || s3 < 0f) && (s1 > 0f || s2 > 0f || s3 > 0f));
        }

        void DrawShellDebugOverlay(Rect canvasRect)
        {
            var hit = SelectedShellDebug ?? HoveredShellDebug;
            if (hit == null) return;

            var lines = new List<string>();
            bool pinned = SelectedShellDebug != null;
            lines.Add(pinned ? "[Pinned]" : "[Hover]");
            lines.Add($"Shell #{hit.shellId}  ch={hit.uvChannel}");
            lines.Add($"UV: ({hit.hoverUv.x:F4}, {hit.hoverUv.y:F4})");
            lines.Add($"Tile: ({hit.tileU}, {hit.tileV})");
            lines.Add($"Local: ({hit.localUv.x:F4}, {hit.localUv.y:F4})");
            if (hit.shell != null)
            {
                lines.Add($"Faces: {hit.shell.faceIndices.Count}");
                lines.Add($"Verts: {hit.shell.vertexIndices.Count}");
            }
            if (hit.mesh != null) lines.Add($"Mesh: {hit.mesh.name}");
            if (hit.entry?.renderer != null) lines.Add($"Obj: {hit.entry.renderer.name}");

            float lineH = 14f, padX = 6f, padY = 4f;
            float maxW = 0f;
            var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(.85f,.85f,.85f) } };
            var pinnedStyle = new GUIStyle(style) { normal = { textColor = new Color(.4f,.85f,1f) } };
            foreach (var l in lines) { float w = style.CalcSize(new GUIContent(l)).x; if (w > maxW) maxW = w; }
            float panelW = maxW + padX * 2;
            float panelH = lines.Count * lineH + padY * 2;
            var panelRect = new Rect(canvasRect.xMax - panelW - 4, canvasRect.y + 4, panelW, panelH);
            EditorGUI.DrawRect(panelRect, new Color(0f, 0f, 0f, 0.75f));
            for (int i = 0; i < lines.Count; i++)
            {
                var lr = new Rect(panelRect.x + padX, panelRect.y + padY + i * lineH, panelW - padX * 2, lineH);
                GUI.Label(lr, lines[i], i == 0 && pinned ? pinnedStyle : style);
            }
        }

        public static Mesh MakeReadableCopy(Mesh src)
        {
            var dst = new Mesh();
            dst.indexFormat = src.indexFormat;
            dst.SetVertices(new List<Vector3>(src.vertices));
            if (src.normals != null && src.normals.Length > 0) dst.SetNormals(new List<Vector3>(src.normals));
            if (src.tangents != null && src.tangents.Length > 0) dst.SetTangents(new List<Vector4>(src.tangents));
            if (src.colors != null && src.colors.Length > 0) dst.SetColors(new List<Color>(src.colors));
            if (src.boneWeights != null && src.boneWeights.Length > 0) dst.boneWeights = src.boneWeights;
            if (src.bindposes != null && src.bindposes.Length > 0) dst.bindposes = src.bindposes;
            for (int ch = 0; ch < 8; ch++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (!src.HasVertexAttribute(attr)) continue;
                int dim = src.GetVertexAttributeDimension(attr);
                if (dim <= 2)
                {
                    var uv = new List<Vector2>(); src.GetUVs(ch, uv);
                    if (uv.Count > 0 && !IsAllZero2(uv)) dst.SetUVs(ch, uv);
                }
                else if (dim == 3)
                {
                    var uv = new List<Vector3>(); src.GetUVs(ch, uv);
                    if (uv.Count > 0) dst.SetUVs(ch, uv);
                }
                else
                {
                    var uv = new List<Vector4>(); src.GetUVs(ch, uv);
                    if (uv.Count > 0) dst.SetUVs(ch, uv);
                }
            }
            dst.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++) dst.SetTriangles(src.GetTriangles(s), s);
            dst.bounds = src.bounds;
            return dst;
        }

        static bool IsAllZero2(List<Vector2> uv)
        {
            for (int i = 0; i < uv.Count; i++)
                if (uv[i].x != 0f || uv[i].y != 0f) return false;
            return true;
        }
    }
}
