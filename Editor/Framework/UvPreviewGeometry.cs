using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    // Positions stay in UV space. Panning/zooming changes only DrawMeshNow's matrix;
    // colour/selection changes replace one batch, and topology invalidation clears all.
    internal sealed class UvPreviewGeometry : IDisposable
    {
        internal sealed class Builder
        {
            readonly CancellationToken token;
            internal Builder(CancellationToken token = default) { this.token = token; }
            readonly List<Vector3> positions = new List<Vector3>();
            readonly List<Color> colors = new List<Color>();
            readonly List<int> indices = new List<int>();
            readonly Dictionary<(Vector2, Color), int> vertices = new Dictionary<(Vector2, Color), int>();
            Color color = Color.white;
            internal void SetColor(Color value) => color = value;
            internal void Vertex(Vector2 uv)
            {
                if ((indices.Count & 4095) == 0) token.ThrowIfCancellationRequested();
                var key = (uv, color);
                if (!vertices.TryGetValue(key, out int index)) {
                    index = positions.Count; vertices.Add(key, index);
                    positions.Add(new Vector3(uv.x, 1f - uv.y, 0)); colors.Add(color);
                }
                indices.Add(index);
            }

            internal Mesh Upload(MeshTopology topology)
            {
                var mesh = new Mesh { name = "MeshLab UV batch", hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = IndexFormat.UInt32 };
                try {
                    mesh.SetVertices(positions); mesh.SetColors(colors);
                    mesh.SetIndices(indices, topology, 0);
                    mesh.UploadMeshData(true);
                    return mesh;
                }
                catch { Object.DestroyImmediate(mesh); throw; }
            }
        }

        sealed class Entry : IDisposable
        {
            internal object state;
            internal Mesh mesh;
            internal readonly PreviewWork<Builder> work = new PreviewWork<Builder>("UV draw buffers");
            public void Dispose() { work.Dispose(); if (mesh) Object.DestroyImmediate(mesh); }
        }
        readonly Dictionary<(object uv, object source, string kind), Entry> entries =
            new Dictionary<(object, object, string), Entry>();
        readonly Dictionary<object, int> revisions = new Dictionary<object, int>();
        int nextRevision;
        internal int Generation => nextRevision;
        internal int Revision(object uv) => uv != null && revisions.TryGetValue(uv, out int revision) ? revision : 0;

        internal Action Ready;
        internal Mesh Get(object uv, object source, string kind, object state, MeshTopology topology, Action<Builder> build, bool defer = false)
        {
            var key = (uv, source, kind);
            if (entries.TryGetValue(key, out var entry) && Equals(entry.state, state)) return entry.mesh;
            entry?.Dispose();
            entry = new Entry { state = state }; entries[key] = entry;
            if (defer) {
                entry.work.Enqueue(() => token => { var builder = new Builder(token); build(builder); return builder; },
                    builder => { entry.mesh = builder.Upload(topology); revisions[uv] = ++nextRevision; Ready?.Invoke(); });
            }
            else { var builder = new Builder(); build(builder); entry.mesh = builder.Upload(topology); }
            return entry.mesh;
        }

        internal void InvalidateUv(object uv)
        {
            var stale = new List<(object, object, string)>();
            foreach (var pair in entries) if (ReferenceEquals(pair.Key.uv, uv)) stale.Add(pair.Key);
            foreach (var key in stale) { entries[key].Dispose(); entries.Remove(key); }
            if (uv != null) revisions.Remove(uv);
        }

        internal static Matrix4x4 Matrix(float x, float y, float size) =>
            Matrix4x4.TRS(new Vector3(x, y, 0), Quaternion.identity, new Vector3(size, size, 1));

        public void Dispose()
        {
            foreach (var entry in entries.Values) entry.Dispose();
            entries.Clear();
            revisions.Clear();
        }
    }
}
