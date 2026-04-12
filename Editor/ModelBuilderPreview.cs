// ModelBuilderPreview.cs — 3D scene preview helper for Model Builder tool.
// Manages mesh clone backup/restore and visualization modes:
//   Channel visualization (vertex colors, normals, tangents, UV channels)
//   Edge wireframe overlay (colored by EdgeAnalyzer classification)
//   Problem area highlighting (degenerate tris, weld candidates, unused verts)

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

namespace LightmapUvTool
{
    internal class ModelBuilderPreview
    {
        // ── Backup ──

        struct RendererBackup
        {
            public MeshFilter meshFilter;
            public Mesh originalMesh;
            public Material[] originalMats;
        }

        readonly List<RendererBackup> backups = new List<RendererBackup>();
        Material vertexColorMat;
        bool isActive;

        public bool IsActive => isActive;

        // ── Safety guard: static instance for domain reload cleanup ──

        internal static ModelBuilderPreview ActiveInstance { get; private set; }

        internal static void RestoreIfActive()
        {
            if (ActiveInstance != null && ActiveInstance.isActive)
                ActiveInstance.Restore();
        }

        // ── Edge wireframe data ──

        internal struct EdgeSegment
        {
            public Vector3 a, b;
            public EdgeAnalyzer.EdgeFlag flags;
        }

        List<(List<EdgeSegment> segments, Matrix4x4 localToWorld)> edgeOverlays;

        public bool HasEdgeOverlays => edgeOverlays != null && edgeOverlays.Count > 0;

        // ── Problem area data (for OnSceneGUI dots) ──

        internal struct UnusedVertData
        {
            public Vector3[] positions;
            public bool[] mask;
            public Matrix4x4 localToWorld;
        }

        List<UnusedVertData> unusedVertOverlays;

        public bool HasUnusedVertOverlays => unusedVertOverlays != null && unusedVertOverlays.Count > 0;

        // ── Material ──

        void EnsureMaterial()
        {
            if (vertexColorMat != null) return;
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            vertexColorMat = new Material(sh)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            vertexColorMat.SetInt("_SrcBlend", (int)BlendMode.One);
            vertexColorMat.SetInt("_DstBlend", (int)BlendMode.Zero);
            vertexColorMat.SetInt("_Cull", (int)CullMode.Back);
            vertexColorMat.SetInt("_ZWrite", 1);
        }

        // ═══════════════════════════════════════════════════════════
        //  Channel visualization modes
        // ═══════════════════════════════════════════════════════════

        /// <summary>Show existing vertex colors using Internal-Colored shader.</summary>
        public void ActivateVertexColorPreview(UvToolContext ctx)
        {
            Restore();
            EnsureMaterial();
            if (vertexColorMat == null || ctx?.MeshEntries == null) return;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null || e.meshFilter == null) continue;
                var mr = e.renderer as MeshRenderer;
                if (mr == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null) continue;

                backups.Add(new RendererBackup
                {
                    meshFilter = e.meshFilter,
                    originalMesh = e.meshFilter.sharedMesh,
                    originalMats = mr.sharedMaterials
                });

                // Ensure mesh has vertex colors; if not, add white
                var clone = Object.Instantiate(mesh);
                clone.hideFlags = HideFlags.HideAndDontSave;
                if (clone.colors32 == null || clone.colors32.Length != clone.vertexCount)
                {
                    var colors = new Color32[clone.vertexCount];
                    for (int i = 0; i < colors.Length; i++)
                        colors[i] = new Color32(200, 200, 200, 255);
                    clone.colors32 = colors;
                }
                e.meshFilter.sharedMesh = clone;

                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = vertexColorMat;
                mr.sharedMaterials = mats;
            }

            MarkActive();
        }

        /// <summary>Encode normals as RGB vertex colors: (normal * 0.5 + 0.5).</summary>
        public void ActivateNormalsPreview(UvToolContext ctx)
        {
            ActivateVectorChannelPreview(ctx, mesh =>
            {
                var normals = mesh.normals;
                if (normals == null || normals.Length != mesh.vertexCount) return null;
                var colors = new Color32[mesh.vertexCount];
                for (int i = 0; i < colors.Length; i++)
                {
                    var n = normals[i];
                    colors[i] = new Color32(
                        (byte)((n.x * 0.5f + 0.5f) * 255f),
                        (byte)((n.y * 0.5f + 0.5f) * 255f),
                        (byte)((n.z * 0.5f + 0.5f) * 255f),
                        255);
                }
                return colors;
            });
        }

        /// <summary>Encode tangents.xyz as RGB vertex colors: (tangent * 0.5 + 0.5).</summary>
        public void ActivateTangentsPreview(UvToolContext ctx)
        {
            ActivateVectorChannelPreview(ctx, mesh =>
            {
                var tangents = mesh.tangents;
                if (tangents == null || tangents.Length != mesh.vertexCount) return null;
                var colors = new Color32[mesh.vertexCount];
                for (int i = 0; i < colors.Length; i++)
                {
                    var t = tangents[i];
                    colors[i] = new Color32(
                        (byte)((t.x * 0.5f + 0.5f) * 255f),
                        (byte)((t.y * 0.5f + 0.5f) * 255f),
                        (byte)((t.z * 0.5f + 0.5f) * 255f),
                        (byte)((t.w * 0.5f + 0.5f) * 255f));
                }
                return colors;
            });
        }

        /// <summary>Encode UV channel as RG vertex colors: R=frac(U), G=frac(V).</summary>
        public void ActivateUvPreview(UvToolContext ctx, int channel)
        {
            ActivateVectorChannelPreview(ctx, mesh =>
            {
                var uvList = new List<Vector2>();
                mesh.GetUVs(channel, uvList);
                if (uvList.Count != mesh.vertexCount) return null;
                var colors = new Color32[mesh.vertexCount];
                for (int i = 0; i < colors.Length; i++)
                {
                    var uv = uvList[i];
                    float u = uv.x - Mathf.Floor(uv.x);
                    float v = uv.y - Mathf.Floor(uv.y);
                    colors[i] = new Color32(
                        (byte)(u * 255f),
                        (byte)(v * 255f),
                        0, 255);
                }
                return colors;
            });
        }

        /// <summary>Generic: clone mesh, compute colors via delegate, swap material.</summary>
        void ActivateVectorChannelPreview(UvToolContext ctx,
            System.Func<Mesh, Color32[]> computeColors)
        {
            Restore();
            EnsureMaterial();
            if (vertexColorMat == null || ctx?.MeshEntries == null) return;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null || e.meshFilter == null) continue;
                var mr = e.renderer as MeshRenderer;
                if (mr == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                var colors = computeColors(mesh);
                if (colors == null) continue;

                backups.Add(new RendererBackup
                {
                    meshFilter = e.meshFilter,
                    originalMesh = e.meshFilter.sharedMesh,
                    originalMats = mr.sharedMaterials
                });

                var clone = Object.Instantiate(mesh);
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.colors32 = colors;
                e.meshFilter.sharedMesh = clone;

                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = vertexColorMat;
                mr.sharedMaterials = mats;
            }

            MarkActive();
        }

        // ═══════════════════════════════════════════════════════════
        //  Problem area visualization
        // ═══════════════════════════════════════════════════════════

        static readonly Color32 ColorHealthy    = new Color32(60, 180, 80, 255);
        static readonly Color32 ColorDegenerate = new Color32(255, 40, 40, 255);
        static readonly Color32 ColorWeld       = new Color32(40, 220, 220, 255);

        /// <summary>
        /// Highlight degenerate triangles (red) and weld candidate vertices (cyan)
        /// via vertex colors. Unused vertices shown as dots in OnSceneGUI.
        /// </summary>
        public void ActivateProblemPreview(UvToolContext ctx)
        {
            Restore();
            EnsureMaterial();
            if (vertexColorMat == null || ctx?.MeshEntries == null) return;

            unusedVertOverlays = new List<UnusedVertData>();

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null || e.meshFilter == null) continue;
                var mr = e.renderer as MeshRenderer;
                if (mr == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                var colors = new Color32[mesh.vertexCount];
                for (int i = 0; i < colors.Length; i++) colors[i] = ColorHealthy;

                // Degenerate triangles → red
                var degMask = MeshHygieneUtility.GetDegenerateTriangleMask(mesh);
                if (degMask != null)
                {
                    int fi = 0;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var tris = mesh.GetTriangles(s);
                        for (int t = 0; t + 2 < tris.Length; t += 3, fi++)
                        {
                            if (!degMask[fi]) continue;
                            int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                            if (i0 >= 0 && i0 < colors.Length) colors[i0] = ColorDegenerate;
                            if (i1 >= 0 && i1 < colors.Length) colors[i1] = ColorDegenerate;
                            if (i2 >= 0 && i2 < colors.Length) colors[i2] = ColorDegenerate;
                        }
                    }
                }

                // Weld candidates → cyan (false seams via Uv0Analyzer)
                var seamVerts = Uv0Analyzer.GetFalseSeamVertices(mesh);
                if (seamVerts != null)
                {
                    foreach (int vi in seamVerts)
                        if (vi >= 0 && vi < colors.Length && colors[vi].r != ColorDegenerate.r)
                            colors[vi] = ColorWeld;
                }

                backups.Add(new RendererBackup
                {
                    meshFilter = e.meshFilter,
                    originalMesh = e.meshFilter.sharedMesh,
                    originalMats = mr.sharedMaterials
                });

                var clone = Object.Instantiate(mesh);
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.colors32 = colors;
                e.meshFilter.sharedMesh = clone;

                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = vertexColorMat;
                mr.sharedMaterials = mats;

                // Unused vertex data for scene dots
                var unusedMask = MeshHygieneUtility.GetUnusedVertexMask(mesh);
                if (unusedMask != null)
                {
                    bool hasAny = false;
                    for (int i = 0; i < unusedMask.Length; i++)
                        if (unusedMask[i]) { hasAny = true; break; }
                    if (hasAny)
                    {
                        unusedVertOverlays.Add(new UnusedVertData
                        {
                            positions = mesh.vertices,
                            mask = unusedMask,
                            localToWorld = e.renderer.transform.localToWorldMatrix
                        });
                    }
                }
            }

            MarkActive();
        }

        // ═══════════════════════════════════════════════════════════
        //  Split preview: color each submesh with a distinct palette color
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Show split preview: each submesh of multi-material meshes gets a
        /// distinct color from the palette, previewing what each output object
        /// would contain after split.
        /// </summary>
        public void ActivateSplitPreview(UvToolContext ctx,
            List<(Renderer renderer, Mesh mesh)> candidates)
        {
            Restore();
            EnsureMaterial();
            if (vertexColorMat == null || candidates == null) return;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                var (renderer, mesh) = candidates[ci];
                if (renderer == null || mesh == null || !mesh.isReadable) continue;

                var mf = renderer.GetComponent<MeshFilter>();
                var mr = renderer as MeshRenderer;
                if (mf == null || mr == null) continue;

                backups.Add(new RendererBackup
                {
                    meshFilter = mf,
                    originalMesh = mf.sharedMesh,
                    originalMats = mr.sharedMaterials
                });

                var colors = new Color32[mesh.vertexCount];
                // Assign palette color per submesh
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var tris = mesh.GetTriangles(s);
                    Color c = UvCanvasView.pal[s % UvCanvasView.pal.Length];
                    var c32 = new Color32(
                        (byte)(c.r * 255f), (byte)(c.g * 255f),
                        (byte)(c.b * 255f), 255);
                    for (int t = 0; t < tris.Length; t++)
                    {
                        int vi = tris[t];
                        if (vi >= 0 && vi < colors.Length)
                            colors[vi] = c32;
                    }
                }

                var clone = Object.Instantiate(mesh);
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.colors32 = colors;
                mf.sharedMesh = clone;

                var mats = new Material[mr.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = vertexColorMat;
                mr.sharedMaterials = mats;
            }

            MarkActive();
        }

        // ═══════════════════════════════════════════════════════════
        //  Edge wireframe overlay
        // ═══════════════════════════════════════════════════════════

        /// <summary>Build edge overlay data for all mesh entries (call once, draw every frame).</summary>
        public void BuildEdgeOverlays(UvToolContext ctx)
        {
            edgeOverlays = new List<(List<EdgeSegment>, Matrix4x4)>();
            if (ctx?.MeshEntries == null) return;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.renderer == null) continue;
                Mesh mesh = e.originalMesh ?? e.fbxMesh;
                if (mesh == null || !mesh.isReadable) continue;

                var verts = mesh.vertices;
                int vertCount = mesh.vertexCount;
                var posGroup = EdgeAnalyzer.BuildPositionGroups(verts, vertCount);

                // Map: group → first vertex index (for position lookup)
                var groupToVert = new Dictionary<int, int>();
                for (int i = 0; i < vertCount; i++)
                {
                    if (!groupToVert.ContainsKey(posGroup[i]))
                        groupToVert[posGroup[i]] = i;
                }

                EdgeAnalyzer.Analyze(mesh, out var edges);
                var segments = new List<EdgeSegment>(edges.Count);

                foreach (var kv in edges)
                {
                    var info = kv.Value;
                    if (!groupToVert.TryGetValue(info.vertA, out int vA)) continue;
                    if (!groupToVert.TryGetValue(info.vertB, out int vB)) continue;

                    segments.Add(new EdgeSegment
                    {
                        a = verts[vA],
                        b = verts[vB],
                        flags = info.flags
                    });
                }

                if (segments.Count > 0)
                    edgeOverlays.Add((segments, e.renderer.transform.localToWorldMatrix));
            }
        }

        /// <summary>Draw edge wireframe in scene view. Call from OnSceneGUI.</summary>
        public void DrawEdgeWireframe(SceneView sv)
        {
            if (edgeOverlays == null) return;

            foreach (var (segments, matrix) in edgeOverlays)
            {
                foreach (var seg in segments)
                {
                    Handles.color = GetEdgeColor(seg.flags);
                    Vector3 a = matrix.MultiplyPoint3x4(seg.a);
                    Vector3 b = matrix.MultiplyPoint3x4(seg.b);
                    Handles.DrawLine(a, b);
                }
            }
        }

        /// <summary>Draw yellow dots for unused vertices. Call from OnSceneGUI.</summary>
        public void DrawUnusedVertexDots()
        {
            if (unusedVertOverlays == null) return;

            Handles.color = new Color(1f, 0.86f, 0.16f, 0.9f);
            foreach (var data in unusedVertOverlays)
            {
                for (int i = 0; i < data.mask.Length && i < data.positions.Length; i++)
                {
                    if (!data.mask[i]) continue;
                    Vector3 world = data.localToWorld.MultiplyPoint3x4(data.positions[i]);
                    float size = HandleUtility.GetHandleSize(world) * 0.025f;
                    Handles.DotHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
                }
            }
        }

        static Color GetEdgeColor(EdgeAnalyzer.EdgeFlag flags)
        {
            // Priority: NonManifold > UvSeam > HardEdge > UvFoldover > Border > Interior
            if ((flags & EdgeAnalyzer.EdgeFlag.NonManifold) != 0)
                return new Color(1f, 0.2f, 0.2f, 0.9f);
            if ((flags & EdgeAnalyzer.EdgeFlag.UvSeam) != 0)
                return new Color(1f, 0.9f, 0.2f, 0.8f);
            if ((flags & EdgeAnalyzer.EdgeFlag.HardEdge) != 0)
                return new Color(0.3f, 0.5f, 1f, 0.8f);
            if ((flags & EdgeAnalyzer.EdgeFlag.UvFoldover) != 0)
                return new Color(1f, 0.3f, 0.9f, 0.8f);
            if ((flags & EdgeAnalyzer.EdgeFlag.Border) != 0)
                return new Color(1f, 1f, 1f, 0.9f);
            // Interior
            return new Color(0.4f, 0.4f, 0.4f, 0.15f);
        }

        // ═══════════════════════════════════════════════════════════
        //  Restore
        // ═══════════════════════════════════════════════════════════

        public void Restore()
        {
            foreach (var b in backups)
            {
                if (b.meshFilter == null) continue;
                // Destroy temp clone
                if (b.meshFilter.sharedMesh != null && b.meshFilter.sharedMesh != b.originalMesh)
                    Object.DestroyImmediate(b.meshFilter.sharedMesh);
                b.meshFilter.sharedMesh = b.originalMesh;
                var mr = b.meshFilter.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterials = b.originalMats;
            }
            backups.Clear();

            edgeOverlays = null;
            unusedVertOverlays = null;

            if (isActive)
            {
                isActive = false;
                if (ActiveInstance == this) ActiveInstance = null;
                SceneView.RepaintAll();
            }
        }

        void MarkActive()
        {
            isActive = true;
            ActiveInstance = this;
            SceneView.RepaintAll();
        }
    }
}
