using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal sealed class PreviewPoints : IDisposable
    {
        internal sealed class Data
        {
            internal Vector3[] positions;
            internal Vector2[] corners;
            internal int[] indices;
            internal Mesh Upload()
            {
                var mesh = new Mesh { name = "MeshLab point sprites", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                try { mesh.vertices = positions; mesh.uv = corners; mesh.SetIndices(indices, MeshTopology.Triangles, 0); mesh.UploadMeshData(true); return mesh; }
                catch { Object.DestroyImmediate(mesh); throw; }
            }
        }
        sealed class Entry : IDisposable
        {
            internal Mesh mesh;
            internal int vertices;
            internal readonly PreviewWork<Data> work = new PreviewWork<Data>("Point preview");
            public void Dispose() { work.Dispose(); if (mesh) Object.DestroyImmediate(mesh); }
        }
        readonly Dictionary<Mesh, Entry> entries = new Dictionary<Mesh, Entry>();
        internal Action Ready;
        internal Mesh Get(Mesh source)
        {
            if (!source) return null;
            if (entries.TryGetValue(source, out var entry) && entry.vertices == source.vertexCount) return entry.mesh;
            entry?.Dispose(); entry = new Entry { vertices = source.vertexCount }; entries[source] = entry;
            entry.work.Enqueue(() => {
                if (!source) return token => null;
                var points = source.vertices; return token => Build(points, token);
            }, data => {
                if (!source || data == null) return;
                entry.mesh = data.Upload(); Ready?.Invoke();
            });
            return null;
        }
        internal static Data Build(Vector3[] points, CancellationToken token = default)
        {
            var data = new Data { positions = new Vector3[checked(points.Length * 4)], corners = new Vector2[checked(points.Length * 4)], indices = new int[checked(points.Length * 6)] };
            for (int p = 0; p < points.Length; ++p) {
                if ((p & 4095) == 0) token.ThrowIfCancellationRequested();
                int v = p * 4, t = p * 6;
                for (int c = 0; c < 4; ++c) { data.positions[v + c] = points[p]; data.corners[v + c] = new Vector2((c & 1) == 0 ? -.5f : .5f, (c & 2) == 0 ? -.5f : .5f); }
                data.indices[t] = v; data.indices[t + 1] = v + 1; data.indices[t + 2] = v + 2;
                data.indices[t + 3] = v + 2; data.indices[t + 4] = v + 1; data.indices[t + 5] = v + 3;
            }
            return data;
        }
        internal void Remove(Mesh source) { if (source && entries.Remove(source, out var entry)) entry.Dispose(); }
        internal void Prune()
        {
            var stale = new List<Mesh>();
            foreach (var pair in entries) if (!pair.Key) stale.Add(pair.Key);
            foreach (var key in stale) { entries[key].Dispose(); entries.Remove(key); }
        }
        public void Dispose()
        {
            foreach (var entry in entries.Values) entry.Dispose();
            entries.Clear();
        }
    }
}
