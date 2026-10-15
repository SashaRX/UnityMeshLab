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
        // Color32 inherits ValueType's equality/hash implementation. Comparing it
        // inside a tuple boxes on Mono; dense wire preparation must avoid that cost.
        sealed class SegmentComparer : IEqualityComparer<(Vector3 a, Vector3 b, Color32 ca, Color32 cb)>
        {
            internal static readonly SegmentComparer Instance = new SegmentComparer();
            static uint Packed(Color32 c) => (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);
            public bool Equals((Vector3 a, Vector3 b, Color32 ca, Color32 cb) x, (Vector3 a, Vector3 b, Color32 ca, Color32 cb) y) =>
                x.a.Equals(y.a) && x.b.Equals(y.b) && Packed(x.ca) == Packed(y.ca) && Packed(x.cb) == Packed(y.cb);
            public int GetHashCode((Vector3 a, Vector3 b, Color32 ca, Color32 cb) value) =>
                HashCode.Combine(value.a.x, value.a.y, value.a.z, value.b.x, value.b.y, value.b.z, Packed(value.ca), Packed(value.cb));
        }

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

        readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
        readonly List<long> stale = new List<long>();
        internal Action Repaint;

        internal Mesh Get(Mesh source, bool useColors = true)
        {
            if (!source) return null;
            long key = ((long)source.GetInstanceID() << 1) | (useColors ? 1L : 0L);
            if (entries.TryGetValue(key, out var cached)) {
                if (cached.source == source && cached.vertices == source.vertexCount) return cached.ribbons;
                Remove(source);
            }
            var entry = new Entry { source = source, vertices = source.vertexCount };
            entries.Add(key, entry);
            entry.work.Enqueue(() => {
                if (!source) return token => null;
                var positions = source.vertices;
                var colors = useColors ? source.colors32 : null;
                MeshViewport3D.SnapshotIndices(source, out var indices, out var topology);
                return token => Build(positions, MeshViewport3D.EdgeIndices(indices, topology, token), colors, token);
            }, data => {
                if (!source || data == null) return;
                entry.ribbons = data.Upload();
                Repaint?.Invoke();
            });
            return null;
        }

        internal void Remove(Mesh source)
        {
            if (!source) return;
            long key = (long)source.GetInstanceID() << 1;
            for (int color = 0; color < 2; ++color)
                if (entries.Remove(key + color, out var entry)) entry.Dispose();
        }

        internal void Prune()
        {
            stale.Clear();
            foreach (var item in entries) if (!item.Value.source) stale.Add(item.Key);
            foreach (long key in stale) { entries[key].Dispose(); entries.Remove(key); }
        }

        internal static Data Build(IList<Vector3> positions, IList<int> pairs, IList<Color32> colors,
            CancellationToken token = default)
        {
            var unique = new List<(int a, int b)>();
            // Split UV/normal vertices can describe the same visible segment. Remove
            // exact duplicates (including reversed edges), retaining different colours.
            var seen = new HashSet<(Vector3 a, Vector3 b, Color32 ca, Color32 cb)>(SegmentComparer.Instance);
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
