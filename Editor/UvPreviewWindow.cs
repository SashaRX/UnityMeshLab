// UvPreviewWindow.cs — UV preview: fill + wireframe + degenerate overlay
// ALL rendering via GL (no Handles.DrawLine — that causes native crashes on large meshes)
// Menu: Tools → Xatlas → UV Preview
// Place in Assets/Editor/

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class UvPreviewWindow : EditorWindow
    {
        Mesh mesh;
        Vector2 scrollPos;
        float zoom = 1f;
        int uvChannel = 1;
        bool showFill = true;
        bool showWire = true;
        bool showDegen;
        float fillAlpha = 0.25f;
        float degThreshold = 0.25f;

        // Shell cache
        List<UvShell> cachedShells;
        Mesh cachedMesh;
        int cachedUvCh = -1;

        // Degenerate cache
        List<int> cachedDegenFaces;
        float cachedDegTh = -1f;
        Mesh cachedDegMesh;
        int cachedDegCh = -1;

        Material glMat;

        // UV range filter: skip any tri with UV outside this
        const float UV_MIN = -0.5f;
        const float UV_MAX = 1.5f;

        // Max tris per GL.Begin/End batch
        const int BATCH = 800;

        // Total max tris to render per layer
        const int MAX_RENDER = 6000;

        static readonly Color[] palette =
        {
            new Color(0.20f, 0.60f, 1.00f),
            new Color(1.00f, 0.40f, 0.20f),
            new Color(0.30f, 0.85f, 0.40f),
            new Color(0.90f, 0.25f, 0.60f),
            new Color(0.95f, 0.85f, 0.20f),
            new Color(0.55f, 0.30f, 0.90f),
            new Color(0.00f, 0.80f, 0.80f),
            new Color(0.85f, 0.55f, 0.20f),
            new Color(0.60f, 0.90f, 0.20f),
            new Color(0.90f, 0.20f, 0.20f),
        };

        [MenuItem("Tools/Xatlas/UV Preview")]
        static void Open() => GetWindow<UvPreviewWindow>("UV Preview");

        void OnEnable()
        {
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            glMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            glMat.SetInt("_ZWrite", 0);
        }

        void OnDisable()
        {
            if (glMat) DestroyImmediate(glMat);
            glMat = null;
            ClearCache();
        }

        void ClearCache()
        {
            cachedShells = null; cachedMesh = null; cachedUvCh = -1;
            cachedDegenFaces = null; cachedDegMesh = null; cachedDegTh = -1; cachedDegCh = -1;
        }

        void OnSelectionChange()
        {
            mesh = null;
            ClearCache();
            try
            {
                var go = Selection.activeGameObject;
                if (go)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf && mf.sharedMesh) mesh = mf.sharedMesh;
                }
            }
            catch { mesh = null; }
            Repaint();
        }

        // Is UV in renderable range?
        static bool UvOk(Vector2 uv)
        {
            return uv.x >= UV_MIN && uv.x <= UV_MAX &&
                   uv.y >= UV_MIN && uv.y <= UV_MAX &&
                   !float.IsNaN(uv.x) && !float.IsNaN(uv.y) &&
                   !float.IsInfinity(uv.x) && !float.IsInfinity(uv.y);
        }

        static bool TriOk(Vector2[] uvs, int uvLen, int i0, int i1, int i2)
        {
            if (i0 < 0 || i0 >= uvLen) return false;
            if (i1 < 0 || i1 >= uvLen) return false;
            if (i2 < 0 || i2 >= uvLen) return false;
            return UvOk(uvs[i0]) && UvOk(uvs[i1]) && UvOk(uvs[i2]);
        }

        void OnGUI()
        {
            // ── Toolbar ──
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            uvChannel = GUILayout.Toolbar(uvChannel, new[] { "UV0", "UV2" },
                EditorStyles.toolbarButton, GUILayout.Width(120));
            zoom = EditorGUILayout.Slider("Zoom", zoom, 0.5f, 4f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            showFill  = GUILayout.Toggle(showFill,  "Fill",  EditorStyles.toolbarButton, GUILayout.Width(45));
            showWire  = GUILayout.Toggle(showWire,  "Wire",  EditorStyles.toolbarButton, GUILayout.Width(45));
            showDegen = GUILayout.Toggle(showDegen, "Degen", EditorStyles.toolbarButton, GUILayout.Width(50));
            if (showFill)
                fillAlpha = EditorGUILayout.Slider(fillAlpha, 0.05f, 0.6f);
            if (showDegen)
                degThreshold = EditorGUILayout.Slider(degThreshold, 0.05f, 0.5f);
            EditorGUILayout.EndHorizontal();

            // ── Mesh guard ──
            if (mesh == null || !mesh)
            {
                mesh = null; ClearCache();
                EditorGUILayout.HelpBox("Select a GameObject with MeshFilter.", MessageType.Info);
                return;
            }

            Vector2[] uvs; int[] tris;
            try { uvs = uvChannel == 0 ? mesh.uv : mesh.uv2; tris = mesh.triangles; }
            catch { mesh = null; ClearCache(); return; }

            if (uvs == null || uvs.Length == 0)
            {
                EditorGUILayout.HelpBox($"No {(uvChannel == 0 ? "UV0" : "UV2")} on '{mesh.name}'.", MessageType.Warning);
                return;
            }
            if (tris == null || tris.Length < 3) return;

            int uvLen = uvs.Length;
            int faceCount = tris.Length / 3;

            // ── Shell cache ──
            if (cachedShells == null || cachedMesh != mesh || cachedUvCh != uvChannel)
            {
                try { cachedShells = UvShellExtractor.Extract(uvs, tris); }
                catch { cachedShells = new List<UvShell>(); }
                cachedMesh = mesh; cachedUvCh = uvChannel;
            }

            // ── Degen cache ──
            if (showDegen)
            {
                bool need = cachedDegenFaces == null || cachedDegMesh != mesh
                    || cachedDegCh != uvChannel || Mathf.Abs(cachedDegTh - degThreshold) > 0.001f;
                if (need)
                {
                    cachedDegenFaces = new List<int>();
                    for (int f = 0; f < faceCount && cachedDegenFaces.Count < MAX_RENDER; f++)
                    {
                        int i0 = tris[f*3], i1 = tris[f*3+1], i2 = tris[f*3+2];
                        if (!TriOk(uvs, uvLen, i0, i1, i2)) continue;
                        float d01 = (uvs[i0] - uvs[i1]).magnitude;
                        float d12 = (uvs[i1] - uvs[i2]).magnitude;
                        float d20 = (uvs[i2] - uvs[i0]).magnitude;
                        if (d01 > degThreshold || d12 > degThreshold || d20 > degThreshold)
                            cachedDegenFaces.Add(f);
                    }
                    cachedDegMesh = mesh; cachedDegCh = uvChannel; cachedDegTh = degThreshold;
                }
            }

            // ── Canvas ──
            float size = Mathf.Min(position.width - 20, position.height - 140) * zoom;
            if (size < 64) size = 64;

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            var rect = GUILayoutUtility.GetRect(size + 20, size + 20);
            float ox = rect.x + 10, oy = rect.y + 10;

            if (Event.current.type == EventType.Repaint && glMat != null)
            {
                // Background
                EditorGUI.DrawRect(new Rect(ox, oy, size, size), new Color(0.12f, 0.12f, 0.12f));

                bool pushed = false;
                try
                {
                    glMat.SetPass(0);
                    GL.PushMatrix();
                    pushed = true;
                    GL.LoadPixelMatrix();

                    // ── Grid (GL.LINES) ──
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(0.25f, 0.25f, 0.25f));
                    for (int g = 0; g <= 4; g++)
                    {
                        float p = g * 0.25f * size;
                        GL.Vertex3(ox + p, oy, 0); GL.Vertex3(ox + p, oy + size, 0);
                        GL.Vertex3(ox, oy + p, 0); GL.Vertex3(ox + size, oy + p, 0);
                    }
                    // Border
                    GL.Color(new Color(0.5f, 0.5f, 0.5f));
                    GL.Vertex3(ox, oy, 0);        GL.Vertex3(ox + size, oy, 0);
                    GL.Vertex3(ox + size, oy, 0);  GL.Vertex3(ox + size, oy + size, 0);
                    GL.Vertex3(ox + size, oy + size, 0); GL.Vertex3(ox, oy + size, 0);
                    GL.Vertex3(ox, oy + size, 0);  GL.Vertex3(ox, oy, 0);
                    GL.End();

                    // ── Shell fills (GL.TRIANGLES, batched) ──
                    if (showFill && cachedShells != null)
                    {
                        int total = 0;
                        int batch = 0;
                        GL.Begin(GL.TRIANGLES);
                        foreach (var shell in cachedShells)
                        {
                            if (total >= MAX_RENDER) break;
                            Color col = palette[shell.shellId % palette.Length];
                            col.a = fillAlpha;
                            GL.Color(col);
                            foreach (int f in shell.faceIndices)
                            {
                                if (total >= MAX_RENDER) break;
                                int i0 = tris[f*3], i1 = tris[f*3+1], i2 = tris[f*3+2];
                                if (!TriOk(uvs, uvLen, i0, i1, i2)) continue;
                                V(ox, oy, size, uvs[i0]);
                                V(ox, oy, size, uvs[i1]);
                                V(ox, oy, size, uvs[i2]);
                                total++; batch++;
                                if (batch >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); GL.Color(col); batch = 0; }
                            }
                        }
                        GL.End();
                    }

                    // ── Degenerate (red triangles, batched) ──
                    if (showDegen && cachedDegenFaces != null && cachedDegenFaces.Count > 0)
                    {
                        int batch = 0;
                        GL.Begin(GL.TRIANGLES);
                        GL.Color(new Color(1, 0, 0, 0.5f));
                        int max = Mathf.Min(cachedDegenFaces.Count, MAX_RENDER);
                        for (int d = 0; d < max; d++)
                        {
                            int f = cachedDegenFaces[d];
                            int i0 = tris[f*3], i1 = tris[f*3+1], i2 = tris[f*3+2];
                            if (!TriOk(uvs, uvLen, i0, i1, i2)) continue;
                            V(ox, oy, size, uvs[i0]);
                            V(ox, oy, size, uvs[i1]);
                            V(ox, oy, size, uvs[i2]);
                            batch++;
                            if (batch >= BATCH) { GL.End(); GL.Begin(GL.TRIANGLES); GL.Color(new Color(1,0,0,0.5f)); batch = 0; }
                        }
                        GL.End();
                    }

                    // ── Wireframe (GL.LINES, batched) ──
                    if (showWire)
                    {
                        int total = 0, batch = 0;
                        GL.Begin(GL.LINES);
                        GL.Color(new Color(0.3f, 0.8f, 1f, 0.5f));
                        for (int f = 0; f < faceCount && total < MAX_RENDER; f++)
                        {
                            int i0 = tris[f*3], i1 = tris[f*3+1], i2 = tris[f*3+2];
                            if (!TriOk(uvs, uvLen, i0, i1, i2)) continue;
                            // 3 edges = 6 vertices
                            V(ox, oy, size, uvs[i0]); V(ox, oy, size, uvs[i1]);
                            V(ox, oy, size, uvs[i1]); V(ox, oy, size, uvs[i2]);
                            V(ox, oy, size, uvs[i2]); V(ox, oy, size, uvs[i0]);
                            total++; batch++;
                            if (batch >= BATCH) { GL.End(); GL.Begin(GL.LINES); GL.Color(new Color(0.3f,0.8f,1f,0.5f)); batch = 0; }
                        }
                        GL.End();
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[UvPreview] GL: {e.Message}");
                }
                finally
                {
                    if (pushed) GL.PopMatrix();
                }
            }

            EditorGUILayout.EndScrollView();

            // ── Info ──
            int sc = cachedShells != null ? cachedShells.Count : 0;
            int oc = 0;
            if (cachedShells != null && cachedShells.Count > 1)
                try { oc = UvShellExtractor.FindOverlapGroups(cachedShells).Count; } catch { }
            int dc = cachedDegenFaces != null ? cachedDegenFaces.Count : 0;

            EditorGUILayout.LabelField(
                $"Mesh: {mesh.name}  |  V: {mesh.vertexCount}  |  T: {faceCount}  |  " +
                $"Shells: {sc}  |  Overlaps: {oc}" +
                (showDegen ? $"  |  Degen: {dc}" : ""));

            // Legend
            if (showFill && cachedShells != null && cachedShells.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Shells:", GUILayout.Width(45));
                int n = Mathf.Min(cachedShells.Count, 20);
                for (int i = 0; i < n; i++)
                {
                    var r = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
                    EditorGUI.DrawRect(r, palette[i % palette.Length]);
                }
                if (cachedShells.Count > n)
                    EditorGUILayout.LabelField($"+{cachedShells.Count - n}", GUILayout.Width(40));
                EditorGUILayout.EndHorizontal();
            }
        }

        // Emit one GL vertex from UV coords
        static void V(float ox, float oy, float size, Vector2 uv)
        {
            GL.Vertex3(ox + uv.x * size, oy + (1f - uv.y) * size, 0);
        }
    }
}
