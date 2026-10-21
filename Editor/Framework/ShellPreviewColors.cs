using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    // Canonical connected UV0 islands of the source LOD own the palette IDs.
    // Explicit transfer provenance wins; otherwise sample source surfaces in 3D.
    // Matching is preview-only, performed once on workers, never in a paint loop.
    internal sealed class ShellPreviewColors : IDisposable
    {
        internal sealed class SourceData
        {
            internal PreviewShellData shells;
            internal TriangleBvh bvh;
            internal SourceData(Vector3[] positions, Vector2[] uv, int[] triangles)
            {
                shells = UvTopology.BuildShellData(uv, triangles, false);
                bvh = shells == null ? null : new TriangleBvh(positions, triangles);
            }
        }

        sealed class Source : IDisposable
        {
            internal SourceData data;
            internal readonly PreviewWork<SourceData> work = new PreviewWork<SourceData>("Shell colour source");
            public void Dispose() => work.Dispose();
        }

        sealed class Binding : IDisposable
        {
            internal Mesh mesh;
            internal SourceData source;
            internal PreviewShellData shells;
            internal int[] provenance;
            internal Dictionary<int, int> colors;
            internal int revision;
            internal readonly PreviewWork<Dictionary<int, int>> work = new PreviewWork<Dictionary<int, int>>("LOD shell colours");
            public void Dispose() => work.Dispose();
        }

        readonly Dictionary<(Mesh, int), Source> sources = new Dictionary<(Mesh, int), Source>();
        readonly Dictionary<(MeshEntry, int), Binding> bindings = new Dictionary<(MeshEntry, int), Binding>();
        readonly Dictionary<string, MeshEntry> referenceGroups = new Dictionary<string, MeshEntry>();
        readonly HashSet<MeshEntry> members = new HashSet<MeshEntry>();
        internal Action Ready;
        int revision;
        UvToolContext context;
        int contextVersion, sourceLod;
        internal int Revision(MeshEntry entry, int channel) => entry != null && bindings.TryGetValue((entry, channel), out var b) ? b.revision : 0;

        internal Dictionary<int, int> Get(UvToolContext ctx, Mesh mesh, PreviewShellData shells, MeshEntry entry, out bool ready, bool immediate = false)
        {
            RefreshContext(ctx);
            ready = true;
            MeshEntry reference = null;
            if (entry != null && members.Contains(entry)) referenceGroups.TryGetValue(Group(entry), out reference);
            if (reference == null || (reference == entry && ctx.PreviewUvChannel == 0)) return shells.colorKeys;
            var sourceMesh = reference.originalMesh;
            if (!sourceMesh) return shells.colorKeys;
            int channel = sourceMesh.HasVertexAttribute(VertexAttribute.TexCoord0) ? 0 : ctx.PreviewUvChannel;
            var source = GetSource(sourceMesh, channel, immediate && sourceMesh.vertexCount <= 2048);
            if (source.data == null) { ready = false; return shells.colorKeys; }
            if (source.data.shells == null) return shells.colorKeys;
            var key = (entry, ctx.PreviewUvChannel);
            var provenance = entry.shellTransferResult?.vertexToSourceShell;
            if (bindings.TryGetValue(key, out var binding) && binding.mesh == mesh &&
                ReferenceEquals(binding.shells, shells) && ReferenceEquals(binding.source, source.data) && ReferenceEquals(binding.provenance, provenance)) {
                ready = binding.colors != null;
                return binding.colors ?? shells.colorKeys;
            }
            binding?.Dispose();
            binding = new Binding { mesh = mesh, shells = shells, source = source.data, provenance = provenance };
            bindings[key] = binding;
            return PrepareBinding(ctx, reference, entry, binding, immediate, out ready);
        }

        Dictionary<int, int> PrepareBinding(UvToolContext ctx, MeshEntry reference, MeshEntry entry, Binding binding, bool immediate, out bool ready)
        {
            var mesh = binding.mesh;
            var shells = binding.shells;
            var provenance = binding.provenance;
            ready = false;
            // Scene movement does not invalidate correspondence. The relative pose is
            // captured once, while reference/mesh/topology changes rebuild the binding.
            var matrix = PreviewMatrix(ctx, reference).inverse * PreviewMatrix(ctx, entry);
            if (immediate) {
                var positions = shells.positions ?? mesh.vertices;
                var map = provenance != null && provenance.Length == positions.Length ? provenance : null;
                binding.colors = Match(binding.source, shells, positions, matrix, map);
                binding.revision = ++revision; ready = true;
                return binding.colors;
            }
            binding.work.Enqueue(() => {
                if (!mesh) throw new OperationCanceledException();
                var positions = mesh.vertices;
                var map = provenance != null && provenance.Length == positions.Length ? (int[])provenance.Clone() : null;
                return token => Match(binding.source, shells, positions, matrix, map, token);
            }, colors => {
                binding.colors = colors; binding.revision = ++revision; Ready?.Invoke();
            });
            return shells.colorKeys;
        }

        void RefreshContext(UvToolContext ctx)
        {
            if (context == ctx && contextVersion == ctx.PreviewCacheVersion && sourceLod == ctx.SourceLodIndex) return;
            Dispose(); context = ctx; contextVersion = ctx.PreviewCacheVersion; sourceLod = ctx.SourceLodIndex;
            foreach (var candidate in ctx.MeshEntries) {
                members.Add(candidate);
                string group = Group(candidate);
                if (candidate.lodIndex == sourceLod && !referenceGroups.ContainsKey(group)) referenceGroups.Add(group, candidate);
            }
        }

        static Matrix4x4 PreviewMatrix(UvToolContext ctx, MeshEntry entry) =>
            entry.renderer ? ctx.PreviewMatrix(entry.renderer.transform) : Matrix4x4.identity;

        Source GetSource(Mesh mesh, int channel, bool immediate)
        {
            var key = (mesh, channel);
            if (sources.TryGetValue(key, out var source)) return source;
            source = new Source(); sources.Add(key, source);
            if (immediate) {
                var readable = MeshAccess.Readable(mesh, out bool copy);
                try { source.data = new SourceData(readable.vertices, UvTopology.ReadUv(readable, channel), readable.triangles); }
                finally { if (copy) Object.DestroyImmediate(readable); }
                return source;
            }
            source.work.Enqueue(() => {
                if (!mesh) throw new OperationCanceledException();
                var readable = MeshAccess.Readable(mesh, out bool copy);
                try {
                    var positions = readable.vertices; var uv = UvTopology.ReadUv(readable, channel); var triangles = readable.triangles;
                    return token => { token.ThrowIfCancellationRequested(); return new SourceData(positions, uv, triangles); };
                }
                finally { if (copy) Object.DestroyImmediate(readable); }
            }, data => { source.data = data; Ready?.Invoke(); });
            return source;
        }

        static string Group(MeshEntry entry)
        {
            if (entry.meshGroupKey != null) return entry.meshGroupKey;
            if (entry.renderer) return UvToolContext.ExtractGroupKey(entry.renderer.name);
            return UvToolContext.ExtractGroupKey(entry.originalMesh ? entry.originalMesh.name : "");
        }

        internal static Dictionary<int, int> Match(SourceData source, PreviewShellData target, Vector3[] positions,
            Matrix4x4 toSource, int[] provenance, CancellationToken token = default)
        {
            var result = new Dictionary<int, int>();
            var votes = new Dictionary<int, int>();
            foreach (var shell in target.shells) {
                token.ThrowIfCancellationRequested(); votes.Clear();
                VoteProvenance(source, shell, provenance, votes);
                if (votes.Count == 0) VoteNearestFaces(source, target, shell, positions, toSource, votes);
                int best = -1, frequency = 0;
                foreach (var pair in votes)
                    if (pair.Value > frequency || (pair.Value == frequency && pair.Key < best)) { best = pair.Key; frequency = pair.Value; }
                result[shell.shellId] = best >= 0 ? best : shell.shellId;
            }
            return result;
        }

        static void VoteProvenance(SourceData source, UvShell shell, int[] provenance, Dictionary<int, int> votes)
        {
            if (provenance == null) return;
            foreach (int vertex in shell.vertexIndices)
                if (vertex >= 0 && vertex < provenance.Length && source.shells.shellById.ContainsKey(provenance[vertex])) Vote(votes, provenance[vertex]);
        }

        static void VoteNearestFaces(SourceData source, PreviewShellData target, UvShell shell, Vector3[] positions,
            Matrix4x4 toSource, Dictionary<int, int> votes)
        {
            int count = Math.Min(32, shell.faceIndices.Count);
            for (int i = 0; i < count; ++i) {
                int face = shell.faceIndices[(int)((long)i * shell.faceIndices.Count / count)];
                int t = face * 3;
                var p = (positions[target.triangles[t]] + positions[target.triangles[t + 1]] + positions[target.triangles[t + 2]]) / 3f;
                var hit = source.bvh.FindNearest(toSource.MultiplyPoint3x4(p));
                if (source.shells.faceToShell.TryGetValue(hit.triangleIndex, out int id)) Vote(votes, id);
            }
        }

        static void Vote(Dictionary<int, int> votes, int id) { votes.TryGetValue(id, out int count); votes[id] = count + 1; }

        public void Dispose()
        {
            foreach (var source in sources.Values) source.Dispose();
            sources.Clear();
            foreach (var binding in bindings.Values) binding.Dispose();
            bindings.Clear();
            referenceGroups.Clear(); members.Clear(); context = null;
        }
    }
}
