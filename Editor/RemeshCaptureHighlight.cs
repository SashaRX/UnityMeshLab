using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Scene-view paint of what the remesh stage will capture under the source root,
    /// built from a geometry-only capture (no material or texture reads): kept parts
    /// green, parts the size filter drops orange, rods the section filter drops red,
    /// renderers the capture skips (LOD1+, collision, disabled) grey. Drawn over the
    /// scene with a depth offset so the paint wins the z-fight against the originals.
    /// </summary>
    internal sealed class RemeshCaptureHighlight : IDisposable
    {
        static readonly Color Kept = new Color(0.25f, 0.85f, 0.35f);
        static readonly Color Small = new Color(1f, 0.6f, 0.1f);
        static readonly Color Rod = new Color(0.95f, 0.2f, 0.2f);
        static readonly Color Excluded = new Color(0.4f, 0.4f, 0.4f);

        Mesh painted;
        Material paint, grey;
        readonly List<(Mesh mesh, Matrix4x4 matrix)> excluded = new List<(Mesh, Matrix4x4)>();
        public string Key { get; private set; }
        public string Summary { get; private set; } = "";
        public string Error { get; private set; }

        public static string KeyFor(GameObject root, RemeshSettings s) =>
            $"{(root ? root.GetInstanceID() : 0)}|{s.lod0Only}|{s.keepHierarchy}|{s.sourceShape}|{s.voxelResolution}|{s.hullResolution}|{s.minPartSize:F4}|{s.minRodVoxels:F3}";

        /// <summary>Rebuilds the paint for root under the given settings (main thread).</summary>
        public void Build(GameObject root, RemeshSettings settings)
        {
            Clear();
            Key = KeyFor(root, settings);
            Error = null;
            if (!root) return;
            try {
                var included = RemeshSource.CollectRenderers(root, settings.lod0Only);
                var includedSet = new HashSet<Renderer>(included);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>()) {
                    if (includedSet.Contains(renderer)) continue;
                    var mesh = MeshOf(renderer);
                    if (mesh) excluded.Add((mesh, renderer.localToWorldMatrix));
                }
                int gridResolution = settings.sourceShape == RemeshShape.Hull ? settings.hullResolution : settings.voxelResolution;
                var vertices = new List<Vector3>(); var colors = new List<Color32>(); var indices = new List<int>();
                long keptFaces = 0, smallFaces = 0, rodFaces = 0; int skippedNodes = 0;
                void Add(RemeshSource source, Matrix4x4 spaceToWorld, byte[] cls, bool nodeSkipped)
                {
                    int baseIndex = vertices.Count;
                    var vertexClass = new byte[source.positions.Length];
                    int faceCount = source.indices.Length / 3;
                    for (int f = 0; f < faceCount; ++f) {
                        byte c = nodeSkipped ? (byte)255 : cls != null ? cls[f] : RemeshSource.PartKept;
                        if (c == RemeshSource.PartKept) ++keptFaces; else if (c == RemeshSource.PartSmall) ++smallFaces; else if (c == RemeshSource.PartRod) ++rodFaces;
                        for (int k = 0; k < 3; ++k) vertexClass[source.indices[f * 3 + k]] = c;
                    }
                    for (int i = 0; i < source.positions.Length; ++i) {
                        vertices.Add(spaceToWorld.MultiplyPoint3x4(source.positions[i]));
                        byte c = vertexClass[i];
                        colors.Add(c == RemeshSource.PartKept ? Kept : c == RemeshSource.PartSmall ? Small : c == RemeshSource.PartRod ? Rod : Excluded);
                    }
                    foreach (var index in source.indices) indices.Add(baseIndex + index);
                }
                if (!settings.keepHierarchy) {
                    var source = RemeshSource.Capture(root.transform.worldToLocalMatrix, included, required: false, geometryOnly: true);
                    if (source != null) {
                        var cls = source.ClassifyParts(settings.minPartSize, settings.minRodVoxels, gridResolution);
                        // The pipeline refuses a filter that would empty the weld: everything stays.
                        if (cls != null && Array.TrueForAll(cls, c => c != RemeshSource.PartKept)) cls = null;
                        Add(source, root.transform.localToWorldMatrix, cls, false);
                    }
                }
                else {
                    foreach (var renderer in included) {
                        var source = RemeshSource.Capture(renderer.transform.worldToLocalMatrix, new[] { renderer }, required: false, geometryOnly: true);
                        if (source == null) continue;
                        var cls = source.ClassifyParts(settings.minPartSize, settings.minRodVoxels, gridResolution);
                        // A node whose every part fails the filter is skipped by the pipeline.
                        bool skipped = cls != null && Array.TrueForAll(cls, c => c != RemeshSource.PartKept);
                        if (skipped) ++skippedNodes;
                        Add(source, renderer.transform.localToWorldMatrix, cls, skipped);
                    }
                }
                if (indices.Count > 0) {
                    painted = new Mesh { name = "RemeshCaptureHighlight", hideFlags = HideFlags.HideAndDontSave,
                        indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                    painted.SetVertices(vertices); painted.SetColors(colors);
                    painted.SetIndices(indices, MeshTopology.Triangles, 0, false);
                    painted.RecalculateNormals(); painted.RecalculateBounds();
                }
                Summary = $"{keptFaces:N0} triangles captured · {smallFaces:N0} in small parts · {rodFaces:N0} in rods · {excluded.Count:N0} renderer(s) excluded" +
                    (skippedNodes > 0 ? $" · {skippedNodes:N0} node(s) fully filtered" : "");
            }
            catch (Exception e) { Clear(); Error = e.Message; }
        }

        /// <summary>Draws the paint in the current Scene view (Repaint event only).</summary>
        public void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!EnsureMaterials()) return;
            if (painted) { paint.SetPass(0); Graphics.DrawMeshNow(painted, Matrix4x4.identity); }
            if (excluded.Count == 0) return;
            grey.SetPass(0);
            foreach (var (mesh, matrix) in excluded) {
                if (!mesh) continue;
                for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                    if (mesh.GetTopology(sub) == MeshTopology.Triangles) Graphics.DrawMeshNow(mesh, matrix, sub);
            }
        }

        static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skin) return skin.sharedMesh;
            if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter)) return filter.sharedMesh;
            return null;
        }

        bool EnsureMaterials()
        {
            if (paint && grey) return true;
            var shader = Shader.Find("Hidden/MeshLab/RemeshPreview");
            if (!shader) return false;
            paint = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            paint.SetFloat("_UseVertexColor", 1); paint.SetFloat("_Lit", 1); paint.SetFloat("_DepthOffset", -1);
            grey = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            grey.SetColor("_Color", Excluded); grey.SetFloat("_Lit", 1); grey.SetFloat("_DepthOffset", -1);
            return true;
        }

        public void Clear()
        {
            if (painted) Object.DestroyImmediate(painted);
            painted = null; excluded.Clear(); Summary = ""; Key = null;
        }

        public void Dispose()
        {
            Clear();
            if (paint) Object.DestroyImmediate(paint);
            if (grey) Object.DestroyImmediate(grey);
            paint = grey = null;
        }
    }
}
