using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    // Camera-independent line ribbons. Expansion and coverage are evaluated in render
    // target pixels by PreviewLines.shader; orbiting never rebuilds these buffers.
    internal sealed class PreviewLines : IDisposable
    {
        internal sealed class Data
        {
            internal Vector3[] starts, ends;
            internal Vector2[] corners;
            internal Color32[] colors;
            internal int[] indices;
            internal Bounds bounds;

            internal Mesh Upload()
            {
                var mesh = new Mesh { name = "Preview line ribbons", hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = IndexFormat.UInt32 };
                try {
                    mesh.vertices = starts;
                    mesh.SetUVs(1, ends);
                    mesh.uv = corners;
                    mesh.colors32 = colors;
                    mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
                    mesh.bounds = bounds;
                    mesh.UploadMeshData(true);
                    return mesh;
                }
                catch { Object.DestroyImmediate(mesh); throw; }
            }
        }

        sealed class Entry : IDisposable
        {
            internal Mesh source, ribbons;
            internal int vertices;
            internal readonly PreviewWork<Data> work = new PreviewWork<Data>("[3D] Line ribbons");
            public void Dispose() { work.Dispose(); if (ribbons) Object.DestroyImmediate(ribbons); }
        }

        readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        readonly List<int> stale = new List<int>();
        internal Action Repaint;

        internal Mesh Get(Mesh source)
        {
            if (!source) return null;
            int key = source.GetInstanceID();
            if (entries.TryGetValue(key, out var cached)) {
                if (cached.source == source && cached.vertices == source.vertexCount) return cached.ribbons;
                Remove(source);
            }
            var entry = new Entry { source = source, vertices = source.vertexCount };
            entries.Add(key, entry);
            entry.work.Enqueue(() => {
                if (!source) return token => null;
                var positions = source.vertices;
                var colors = source.colors32;
                var pairs = MeshViewport3D.EdgeIndices(source);
                return token => Build(positions, pairs, colors, token);
            }, data => {
                if (!source || data == null) return;
                entry.ribbons = data.Upload();
                Repaint?.Invoke();
            });
            return null;
        }

        internal void Remove(Mesh source)
        {
            if (!source || !entries.Remove(source.GetInstanceID(), out var entry)) return;
            entry.Dispose();
        }

        internal void Prune()
        {
            stale.Clear();
            foreach (var item in entries) if (!item.Value.source) stale.Add(item.Key);
            foreach (int key in stale) { entries[key].Dispose(); entries.Remove(key); }
        }

        internal static Data Build(IList<Vector3> positions, IList<int> pairs, IList<Color32> colors,
            CancellationToken token = default)
        {
            var unique = new List<(int a, int b)>();
            // Split UV/normal vertices can describe the same visible segment. Remove
            // exact duplicates (including reversed edges), retaining different colours.
            var seen = new HashSet<(Vector3 a, Vector3 b, Color32 ca, Color32 cb)>();
            Color32 White(int i) => colors != null && i < colors.Count ? colors[i] : new Color32(255, 255, 255, 255);
            for (int p = 0; p + 1 < pairs.Count; p += 2) {
                if ((p & 4095) == 0) token.ThrowIfCancellationRequested();
                int a = pairs[p], b = pairs[p + 1];
                var pa = positions[a]; var pb = positions[b];
                if (pa.Equals(pb)) continue;
                var ca = White(a); var cb = White(b);
                if (seen.Contains((pb, pa, cb, ca)) || !seen.Add((pa, pb, ca, cb))) continue;
                unique.Add((a, b));
            }
            int count = checked(unique.Count * 4);
            var data = new Data { starts = new Vector3[count], ends = new Vector3[count],
                corners = new Vector2[count], colors = new Color32[count], indices = new int[checked(unique.Count * 6)] };
            if (unique.Count > 0) data.bounds = new Bounds(positions[unique[0].a], Vector3.zero);
            for (int e = 0; e < unique.Count; ++e) {
                if ((e & 4095) == 0) token.ThrowIfCancellationRequested();
                var pair = unique[e]; int v = e * 4, t = e * 6;
                var a = positions[pair.a]; var b = positions[pair.b];
                data.bounds.Encapsulate(a); data.bounds.Encapsulate(b);
                for (int c = 0; c < 4; ++c) {
                    data.starts[v + c] = a; data.ends[v + c] = b;
                    data.corners[v + c] = new Vector2(c / 2, c % 2 == 0 ? -1 : 1);
                    data.colors[v + c] = White(c < 2 ? pair.a : pair.b);
                }
                data.indices[t] = v; data.indices[t + 1] = v + 1; data.indices[t + 2] = v + 2;
                data.indices[t + 3] = v + 2; data.indices[t + 4] = v + 1; data.indices[t + 5] = v + 3;
            }
            return data;
        }

        public void Dispose()
        {
            foreach (var entry in entries.Values) entry.Dispose();
            entries.Clear(); stale.Clear();
        }
    }
}
