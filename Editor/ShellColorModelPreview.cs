using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LightmapUvTool
{
    public static class ShellColorModelPreview
    {
        struct RendererBackup
        {
            public Renderer renderer;
            public Material[] origMaterials;
            public MeshFilter meshFilter;
            public Mesh origMeshFilterMesh;
            public SkinnedMeshRenderer skinnedRenderer;
            public Mesh origSkinnedMesh;
            public Mesh tempMesh;
        }

        public sealed class PreviewShellCache
        {
            readonly Dictionary<int, int[]> triangleShellIdsByMesh = new Dictionary<int, int[]>();

            public int[] GetOrBuild(Mesh mesh)
            {
                if (mesh == null) return null;

                int id = mesh.GetInstanceID();
                if (triangleShellIdsByMesh.TryGetValue(id, out var cached))
                    return cached;

                int[] mapping = BuildTriangleShellIds(mesh);
                triangleShellIdsByMesh[id] = mapping;
                return mapping;
            }

            public void Clear() => triangleShellIdsByMesh.Clear();

            static int[] BuildTriangleShellIds(Mesh mesh)
            {
                if (mesh == null) return null;

                int[] tris = mesh.triangles;
                int faceCount = tris != null ? tris.Length / 3 : 0;
                if (faceCount == 0) return new int[0];

                // Use UV2 (lightmap UV) for shell coloring when available.
                // UV2 is transferred from LOD0, so UV2-based shells have consistent
                // structure across LODs. UV0 topology changes between LODs due to
                // face removal during simplification, causing shells to split/merge.
                var uv2List = new List<Vector2>();
                mesh.GetUVs(1, uv2List);
                Vector2[] uv = uv2List.Count == mesh.vertexCount ? uv2List.ToArray() : mesh.uv;
                if (uv == null || uv.Length != mesh.vertexCount)
                    return new int[faceCount];

                List<UvShell> shells;
                try { shells = UvShellExtractor.Extract(uv, tris, computeDescriptors: true); }
                catch { return new int[faceCount]; }

                var triangleToShell = new int[faceCount];
                for (int i = 0; i < triangleToShell.Length; i++) triangleToShell[i] = -1;

                // Use UV bounding-box hash instead of shellId for stable colors across LODs.
                // shellId is extraction-order dependent; bbox corners (min/max) are determined
                // by extreme vertices which survive simplification, unlike centroids which
                // shift when interior vertices are removed non-uniformly.
                foreach (var shell in shells)
                {
                    Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 mx = new Vector2(float.MinValue, float.MinValue);
                    if (shell.vertexIndices != null)
                    {
                        foreach (int vi in shell.vertexIndices)
                        {
                            if (vi >= 0 && vi < uv.Length)
                            {
                                var u = uv[vi];
                                if (u.x < mn.x) mn.x = u.x; if (u.y < mn.y) mn.y = u.y;
                                if (u.x > mx.x) mx.x = u.x; if (u.y > mx.y) mx.y = u.y;
                            }
                        }
                    }
                    Vector2 center = (mn + mx) * 0.5f;
                    int qx = Mathf.RoundToInt(center.x * 100f);
                    int qy = Mathf.RoundToInt(center.y * 100f);
                    int qw = Mathf.RoundToInt((mx.x - mn.x) * 100f);
                    int qh = Mathf.RoundToInt((mx.y - mn.y) * 100f);
                    int stableKey = Mathf.Abs((qx * 73856093) ^ (qy * 19349663) ^ (qw * 83492791) ^ (qh * 41729381));

                    foreach (int faceIndex in shell.faceIndices)
                        if (faceIndex >= 0 && faceIndex < triangleToShell.Length)
                            triangleToShell[faceIndex] = stableKey;
                }

                return triangleToShell;
            }
        }

        static readonly List<RendererBackup> backups = new List<RendererBackup>();
        static Material vertexColorMaterial;
        static bool isActive;

        public static bool IsActive => isActive;

        /// <summary>
        /// Apply with pre-computed face color keys (same logic as 2D preview).
        /// </summary>
        public static void Apply(List<(Renderer renderer, Mesh sourceMesh, int[] faceColorKeys)> entries, Color32[] palette)
        {
            Restore();
            EnsureMaterial();
            if (vertexColorMaterial == null || entries == null || entries.Count == 0) return;

            backups.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                var renderer = entries[i].renderer;
                var sourceMesh = entries[i].sourceMesh;
                if (renderer == null || sourceMesh == null) continue;

                Mesh tempMesh = BuildColorizedClone(sourceMesh, palette, entries[i].faceColorKeys);
                if (tempMesh == null) continue;

                ApplyToRenderer(renderer, tempMesh);
            }

            isActive = backups.Count > 0;
            SceneView.RepaintAll();
        }

        public static void Apply(List<(Renderer renderer, Mesh sourceMesh)> entries, Color32[] palette, PreviewShellCache cache)
        {
            Restore();
            EnsureMaterial();
            if (vertexColorMaterial == null || entries == null || entries.Count == 0) return;

            backups.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                var renderer = entries[i].renderer;
                var sourceMesh = entries[i].sourceMesh;
                if (renderer == null || sourceMesh == null) continue;

                Mesh tempMesh = BuildColorizedClone(sourceMesh, palette, cache);
                if (tempMesh == null) continue;

                ApplyToRenderer(renderer, tempMesh);
            }

            isActive = backups.Count > 0;
            SceneView.RepaintAll();
        }

        public static void Restore()
        {
            for (int i = 0; i < backups.Count; i++)
            {
                var b = backups[i];
                if (b.renderer != null) b.renderer.sharedMaterials = b.origMaterials;
                if (b.meshFilter != null) b.meshFilter.sharedMesh = b.origMeshFilterMesh;
                if (b.skinnedRenderer != null) b.skinnedRenderer.sharedMesh = b.origSkinnedMesh;
                if (b.tempMesh != null) Object.DestroyImmediate(b.tempMesh);
            }

            backups.Clear();
            isActive = false;
            SceneView.RepaintAll();
        }

        static void ApplyToRenderer(Renderer renderer, Mesh tempMesh)
        {
            var backup = new RendererBackup
            {
                renderer = renderer,
                origMaterials = renderer.sharedMaterials,
                tempMesh = tempMesh
            };

            var mf = renderer.GetComponent<MeshFilter>();
            if (mf != null)
            {
                backup.meshFilter = mf;
                backup.origMeshFilterMesh = mf.sharedMesh;
                mf.sharedMesh = tempMesh;
            }

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                backup.skinnedRenderer = skinned;
                backup.origSkinnedMesh = skinned.sharedMesh;
                skinned.sharedMesh = tempMesh;
            }

            var mats = new Material[renderer.sharedMaterials.Length];
            for (int m = 0; m < mats.Length; m++) mats[m] = vertexColorMaterial;
            renderer.sharedMaterials = mats;
            backups.Add(backup);
        }

        static Mesh BuildColorizedClone(Mesh sourceMesh, Color32[] palette, int[] faceColorKeys)
        {
            int[] tris = sourceMesh.triangles;
            int faceCount = tris != null ? tris.Length / 3 : 0;
            if (faceCount == 0) return null;

            Mesh clone = Object.Instantiate(sourceMesh);
            clone.name = sourceMesh.name + "_ShellColorPreview";
            clone.hideFlags = HideFlags.HideAndDontSave;

            var colors = new Color32[clone.vertexCount];
            Color32 fallback = new Color32(100, 100, 100, 255);
            for (int i = 0; i < colors.Length; i++) colors[i] = fallback;

            for (int face = 0; face < faceCount; face++)
            {
                int colorKey = (faceColorKeys != null && face < faceColorKeys.Length)
                    ? faceColorKeys[face] : face;
                Color32 color = palette != null && palette.Length > 0
                    ? palette[Mathf.Abs(colorKey) % palette.Length]
                    : new Color32(255, 255, 255, 255);

                int triBase = face * 3;
                int a = tris[triBase];
                int b = tris[triBase + 1];
                int c = tris[triBase + 2];
                if (a >= 0 && a < colors.Length) colors[a] = color;
                if (b >= 0 && b < colors.Length) colors[b] = color;
                if (c >= 0 && c < colors.Length) colors[c] = color;
            }

            clone.colors32 = colors;
            return clone;
        }

        static Mesh BuildColorizedClone(Mesh sourceMesh, Color32[] palette, PreviewShellCache cache)
        {
            int[] tris = sourceMesh.triangles;
            int faceCount = tris != null ? tris.Length / 3 : 0;
            if (faceCount == 0) return null;

            Mesh clone = Object.Instantiate(sourceMesh);
            clone.name = sourceMesh.name + "_ShellColorPreview";
            clone.hideFlags = HideFlags.HideAndDontSave;

            var colors = new Color32[clone.vertexCount];
            Color32 fallback = new Color32(100, 100, 100, 255);
            for (int i = 0; i < colors.Length; i++) colors[i] = fallback;

            int[] shellIds = cache != null ? cache.GetOrBuild(sourceMesh) : null;
            if (shellIds == null || shellIds.Length != faceCount)
            {
                shellIds = new int[faceCount];
                for (int i = 0; i < shellIds.Length; i++) shellIds[i] = i;
            }

            for (int face = 0; face < faceCount; face++)
            {
                int shellId = shellIds[face];
                Color32 color = palette != null && palette.Length > 0
                    ? palette[Mathf.Abs(shellId) % palette.Length]
                    : new Color32(255, 255, 255, 255);

                int triBase = face * 3;
                int a = tris[triBase];
                int b = tris[triBase + 1];
                int c = tris[triBase + 2];
                if (a >= 0 && a < colors.Length) colors[a] = color;
                if (b >= 0 && b < colors.Length) colors[b] = color;
                if (c >= 0 && c < colors.Length) colors[c] = color;
            }

            clone.colors32 = colors;
            return clone;
        }

        static void EnsureMaterial()
        {
            if (vertexColorMaterial != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                UvtLog.Error("[ShellColorPreview] Shader 'Hidden/Internal-Colored' not found");
                return;
            }

            vertexColorMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            vertexColorMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            vertexColorMaterial.SetInt("_ZWrite", 1);
        }
    }
}
