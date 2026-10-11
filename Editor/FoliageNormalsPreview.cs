using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Read-only source context and face paint, independent of a completed capture.</summary>
    internal sealed class FoliageNormalsPreview : IDisposable
    {
        internal static readonly Color32 ProxySourceColor = new Color32(60, 220, 95, 255);
        internal static readonly Color32 TransferOnlyColor = new Color32(245, 180, 45, 255);
        internal static readonly Color32 ExcludedColor = new Color32(80, 85, 95, 255);
        internal sealed class Source
        {
            internal MeshRenderer renderer;
            internal Mesh mesh, mask;
            internal Matrix4x4 matrix;
            internal Material[] materials;
            internal int lod;
            internal Vector3[] normalLines = Array.Empty<Vector3>();
        }
        sealed class Input
        {
            internal Vector3[] positions;
            internal Vector3[] normals;
            internal Matrix4x4 matrix;
            internal int[][] indices;
            internal MeshTopology[] topology;
            internal bool[] selected;
            internal bool proxySource;
        }
        sealed class Paint
        {
            internal Vector3[] positions;
            internal Color32[] colors;
            internal Vector3[] normalLines;
        }
        readonly List<Source> sources = new List<Source>();
        readonly List<Mesh> owned = new List<Mesh>();
        readonly PreviewWork<List<Paint>> work = new PreviewWork<List<Paint>>("[Foliage] Selection preview");
        internal int[] Levels { get; private set; } = Array.Empty<int>();
        internal string Error { get; private set; }
        internal bool IsPending => work.IsPending;
        internal Action Repaint;

        internal IEnumerable<Source> Sources(int lod)
        {
            if (Levels.Length > 0 && Array.IndexOf(Levels, lod) < 0) lod = Levels[0];
            return sources.Where(s => s.renderer && (s.lod < 0 || s.lod == lod));
        }

        internal void Refresh(GameObject root, FoliageNormalsSettings settings, FoliageNormalsPipeline pipeline)
        {
            ClearMeshes();
            Error = null;
            var options = settings.Snapshot();
            work.Enqueue(() => Prepare(root, options, pipeline), Upload);
        }

        internal bool ContainsSource(Mesh mesh) => sources.Any(source =>
            source.renderer && FoliageLodSelection.SharedMesh(source.renderer) == mesh);

        Func<CancellationToken, List<Paint>> Prepare(GameObject root, FoliageNormalsSettings options, FoliageNormalsPipeline pipeline)
        {
            var inputs = new List<Input>();
            if (!root) return _ => new List<Paint>();
            // A transferred/applied mesh never replaces the captured "before" view.
            Mesh MeshFor(MeshRenderer renderer) => pipeline.Targets.FirstOrDefault(t => t.renderer == renderer)?.original
                ?? FoliageLodSelection.SharedMesh(renderer);
            List<FoliageLodSelection.Target> plan;
            try { plan = FoliageLodSelection.Resolve(root, options, CancellationToken.None, MeshFor); }
            catch (InvalidOperationException e) { Error = e.Message; plan = new List<FoliageLodSelection.Target>(); }
            var renderers = root.GetComponentsInChildren<MeshRenderer>().Concat(plan.Select(t => t.renderer)).Distinct();
            try
            {
                foreach (var renderer in renderers)
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    var mesh = MeshFor(renderer);
                    if (!mesh) continue;
                    var readable = MeshAccess.Readable(mesh, out bool copy, preserveVertexLayout: true);
                    if (!copy)
                    {
                        readable = UnityEngine.Object.Instantiate(readable);
                        readable.hideFlags = HideFlags.HideAndDontSave;
                    }
                    readable.name = mesh.name;
                    owned.Add(readable);
                    var target = plan.FirstOrDefault(t => t.renderer == renderer);
                    var matrix = root.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                    sources.Add(new Source { renderer = renderer, mesh = readable, materials = renderer.sharedMaterials,
                        matrix = matrix,
                        lod = FoliageLodSelection.Level(renderer, mesh) });
                    inputs.Add(Snapshot(readable, target, matrix));
                }
                Levels = sources.Select(s => s.lod).Where(lod => lod >= 0).Distinct().OrderBy(lod => lod).ToArray();
            }
            catch { ClearMeshes(); throw; }
            Repaint?.Invoke();
            return token => inputs.Select(input => BuildPaint(input, token)).ToList();
        }

        static Input Snapshot(Mesh mesh, FoliageLodSelection.Target target, Matrix4x4 matrix)
        {
            var input = new Input { positions = mesh.vertices, indices = new int[mesh.subMeshCount][],
                normals = mesh.normals, matrix = matrix,
                topology = new MeshTopology[mesh.subMeshCount], selected = target?.submeshes, proxySource = target?.proxySource ?? false };
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                input.indices[sub] = mesh.GetIndices(sub);
                input.topology[sub] = mesh.GetTopology(sub);
            }
            return input;
        }

        static Paint BuildPaint(Input input, CancellationToken token)
        {
            var positions = new List<Vector3>();
            var colors = new List<Color32>();
            var selectedVertices = new bool[input.positions.Length];
            for (int sub = 0; sub < input.indices.Length; sub++)
            {
                token.ThrowIfCancellationRequested();
                if (input.topology[sub] != MeshTopology.Triangles) continue;
                AppendFaceCorners(input, sub, positions, colors, selectedVertices, token);
            }
            return new Paint { positions = positions.ToArray(), colors = colors.ToArray(), normalLines = SourceNormalLines(input, selectedVertices, token) };
        }

        static void AppendFaceCorners(Input input, int sub, List<Vector3> positions, List<Color32> colors,
            bool[] selectedVertices, CancellationToken token)
        {
            bool selected = input.selected != null && input.selected[sub];
            var color = ExcludedColor;
            if (selected) color = input.proxySource ? ProxySourceColor : TransferOnlyColor;
            // Separate face corners keep shared selected/excluded vertices distinct.
            foreach (int index in input.indices[sub])
            {
                if ((positions.Count & 4095) == 0) token.ThrowIfCancellationRequested();
                positions.Add(input.positions[index]);
                colors.Add(color);
                if (selected) selectedVertices[index] = true;
            }
        }

        static Vector3[] SourceNormalLines(Input input, bool[] selected, CancellationToken token)
        {
            if (input.normals.Length != input.positions.Length) return Array.Empty<Vector3>();
            var lines = new List<Vector3>();
            var normalMatrix = input.matrix.inverse.transpose;
            var vertices = new List<int>();
            for (int i = 0; i < selected.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (selected[i]) vertices.Add(i);
            }
            int stride = Math.Max(1, (vertices.Count + 255) / 256);
            for (int v = 0; v < vertices.Count; v += stride)
            {
                token.ThrowIfCancellationRequested();
                int i = vertices[v];
                var direction = MeshGeometry.UnitDirection(normalMatrix.MultiplyVector(input.normals[i]));
                if (!MeshGeometry.UsableNormal(direction)) continue;
                var point = input.matrix.MultiplyPoint3x4(input.positions[i]);
                lines.Add(point); lines.Add(point + direction);
            }
            return lines.ToArray();
        }

        internal Bounds Bounds(int lod)
        {
            var bounds = new Bounds(); bool any = false;
            foreach (var source in Sources(lod))
            {
                var next = MeshGeometry.TransformBounds(source.mesh.bounds, source.matrix);
                if (any) bounds.Encapsulate(next); else { bounds = next; any = true; }
            }
            return bounds;
        }

        internal static void AppendSourceNormalLines(Source source, float length, List<Vector3> lines)
        {
            for (int i = 0; i < source.normalLines.Length; i += 2)
            {
                var point = source.normalLines[i];
                lines.Add(point); lines.Add(point + (source.normalLines[i + 1] - point) * length);
            }
        }

        void Upload(List<Paint> paints)
        {
            try
            {
                for (int i = 0; i < paints.Count; i++)
                {
                    var paint = paints[i];
                    var mesh = new Mesh { name = "Foliage selection mask", hideFlags = HideFlags.HideAndDontSave,
                        indexFormat = paint.positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                    owned.Add(mesh);
                    mesh.vertices = paint.positions;
                    mesh.colors32 = paint.colors;
                    mesh.triangles = Enumerable.Range(0, paint.positions.Length).ToArray();
                    mesh.RecalculateBounds();
                    sources[i].mask = mesh;
                    sources[i].normalLines = paint.normalLines;
                }
            }
            catch { ClearMeshes(); throw; }
            Repaint?.Invoke();
        }

        void ClearMeshes()
        {
            foreach (var mesh in owned)
            {
                VertexChannels.RaiseChanged(mesh);
                FoliageTargetSnapshot.Destroy(mesh);
            }
            owned.Clear(); sources.Clear(); Levels = Array.Empty<int>();
        }

        public void Dispose()
        {
            work.Dispose();
            ClearMeshes();
        }

        internal static void AppendNormalLines(FoliageTargetSnapshot target, bool after, Vector3 offset, float length, List<Vector3> lines)
        {
            var normals = after && target.working ? target.working.normals : target.localNormals;
            int stride = Math.Max(1, (target.selectedVertices.Count(selected => selected) + 255) / 256);
            int nextDuplicate = target.localPositions.Length, selectedOrdinal = 0;
            for (int i = 0; i < target.positions.Length; i++)
            {
                if (!target.selectedVertices[i]) continue;
                int v = target.excludedVertices[i] && target.working ? nextDuplicate++ : i;
                if (selectedOrdinal++ % stride != 0) continue;
                var start = target.positions[i] + offset;
                var direction = after && target.working ? MeshGeometry.UnitDirection(target.normalLocalToCapture.MultiplyVector(normals[v]))
                    : target.captureNormals[i];
                lines.Add(start); lines.Add(start + direction * length);
            }
        }
    }
}
