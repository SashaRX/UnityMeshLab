// UvPngWriter.cs — shared helper that renders a UV channel into a PNG via
// RenderTexture + Hidden/Internal-Colored. Used by FbxMetricsExporter for
// source-FBX baselines and by BenchmarkRecorder for per-cell result dumps.

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class UvPngWriter
    {
        public const int DefaultSize = 1024;
        const float UvLo = -0.1f, UvHi = 1.1f; // show OOB verts around the 0-1 box

        static readonly Color[] Palette =
        {
            new Color(0.9f, 0.3f, 0.3f), new Color(0.3f, 0.8f, 0.4f),
            new Color(0.3f, 0.6f, 0.95f), new Color(0.95f, 0.75f, 0.2f),
            new Color(0.8f, 0.4f, 0.9f), new Color(0.3f, 0.9f, 0.85f),
            new Color(0.95f, 0.55f, 0.2f), new Color(0.6f, 0.8f, 0.2f),
        };

        static Material s_mat;
        static Material GetMat()
        {
            if (s_mat != null) return s_mat;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            s_mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            s_mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            s_mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            s_mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            s_mat.SetInt("_ZWrite",   0);
            return s_mat;
        }

        /// <summary>
        /// Write a PNG snapshot of <paramref name="uv"/> + <paramref name="tris"/>.
        /// Triangles are filled per-shell (palette), edges drawn on top, 0-1 box in yellow.
        /// The view covers [-0.1, 1.1] so out-of-bounds verts are visible.
        /// </summary>
        public static bool Render(string path, Vector2[] uv, int[] tris, int size = DefaultSize)
        {
            if (string.IsNullOrEmpty(path) || uv == null || tris == null || tris.Length < 3) return false;
            var mat = GetMat();
            if (mat == null) return false;

            // Shell coloring is stable across multiple dumps of the same mesh.
            int[] faceToShell = null;
            try
            {
                var shells = UvShellExtractor.Extract(uv, tris);
                faceToShell = new int[tris.Length / 3];
                foreach (var sh in shells)
                    foreach (var fi in sh.faceIndices)
                        if (fi >= 0 && fi < faceToShell.Length) faceToShell[fi] = sh.shellId;
            }
            catch { }

            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                GL.Clear(true, true, new Color(0.08f, 0.08f, 0.09f, 1f));
                mat.SetPass(0);

                GL.PushMatrix();
                GL.LoadPixelMatrix(0, size, 0, size);

                // Fill triangles
                GL.Begin(GL.TRIANGLES);
                int fN = tris.Length / 3;
                for (int f = 0; f < fN; f++)
                {
                    int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                    if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
                    int sid = faceToShell != null ? faceToShell[f] : 0;
                    var col = Palette[Mathf.Abs(sid) % Palette.Length];
                    col.a = 0.5f;
                    GL.Color(col);
                    Vert(uv[a], size); Vert(uv[b], size); Vert(uv[c], size);
                }
                GL.End();

                // Wire
                GL.Begin(GL.LINES);
                GL.Color(new Color(1, 1, 1, 0.25f));
                for (int f = 0; f < fN; f++)
                {
                    int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                    if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
                    Vert(uv[a], size); Vert(uv[b], size);
                    Vert(uv[b], size); Vert(uv[c], size);
                    Vert(uv[c], size); Vert(uv[a], size);
                }
                GL.End();

                // 0-1 box
                GL.Begin(GL.LINES);
                GL.Color(new Color(1, 1, 0, 1));
                Vert(new Vector2(0, 0), size); Vert(new Vector2(1, 0), size);
                Vert(new Vector2(1, 0), size); Vert(new Vector2(1, 1), size);
                Vert(new Vector2(1, 1), size); Vert(new Vector2(0, 1), size);
                Vert(new Vector2(0, 1), size); Vert(new Vector2(0, 0), size);
                GL.End();

                GL.PopMatrix();

                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            return true;
        }

        /// <summary>Convenience: pull UV channel from mesh and call Render.</summary>
        public static bool Render(string path, Mesh mesh, int uvChannel, int size = DefaultSize)
        {
            if (mesh == null) return false;
            var list = new List<Vector2>();
            mesh.GetUVs(uvChannel, list);
            if (list.Count == 0) return false;
            return Render(path, list.ToArray(), mesh.triangles, size);
        }

        static void Vert(Vector2 uv, int size)
        {
            float u = (uv.x - UvLo) / (UvHi - UvLo);
            float v = (uv.y - UvLo) / (UvHi - UvLo);
            GL.Vertex3(u * size, v * size, 0);
        }
    }
}
