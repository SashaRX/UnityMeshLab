using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Detached input preparation. Only exactly zero-area local triangles
    /// are removed; vertex streams, material slots and source face identity survive.</summary>
    internal sealed class ReverseUvInputs : IDisposable
    {
        internal readonly ReverseUvTransfer.Level[] levels;
        readonly List<Mesh> owned = new List<Mesh>();

        ReverseUvInputs(int count) => levels = new ReverseUvTransfer.Level[count];

        internal static ReverseUvInputs Prepare(ReverseUvTransfer.Level[] sources, CancellationToken token = default)
        {
            var result = new ReverseUvInputs(sources.Length);
            try
            {
                for (int level = 0; level < sources.Length; ++level)
                {
                    var source = sources[level];
                    var inputs = new ReverseUvTransfer.Input[source.inputs.Length];
                    result.levels[level] = new ReverseUvTransfer.Level { lod = source.lod, inputs = inputs };
                    for (int node = 0; node < inputs.Length; ++node)
                    {
                        token.ThrowIfCancellationRequested();
                        inputs[node] = result.Clean(source.inputs[node], source.lod, token);
                    }
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        ReverseUvTransfer.Input Clean(ReverseUvTransfer.Input source, int lod, CancellationToken token)
        {
            if (source == null || !source.mesh || !source.mesh.isReadable)
                throw new InvalidOperationException("Reverse UV cleanup requires readable working meshes.");
            var mesh = source.mesh;
            var positions = mesh.vertices;
            var parts = new int[mesh.subMeshCount][];
            var retained = new List<int>(); var removed = new List<int>();
            int face = 0;
            for (int sub = 0; sub < parts.Length; ++sub)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                    throw new InvalidOperationException("Reverse UV supports triangle submeshes only.");
                var indices = mesh.GetTriangles(sub);
                var kept = new List<int>(indices.Length);
                for (int t = 0; t < indices.Length; t += 3, ++face)
                {
                    if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                    int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                    if (!Finite(positions[a]) || !Finite(positions[b]) || !Finite(positions[c]))
                        throw new InvalidOperationException($"Reverse UV input '{source.key ?? mesh.name}' LOD{lod} face {face} has nonfinite positions; cleanup was not published.");
                    if (!MeshGeometry.HasArea(positions[a], positions[b], positions[c])) { removed.Add(face); continue; }
                    retained.Add(face); kept.Add(a); kept.Add(b); kept.Add(c);
                }
                parts[sub] = kept.ToArray();
            }
            if (retained.Count == 0)
                throw new InvalidOperationException($"Reverse UV input '{source.key ?? mesh.name}' LOD{lod} has no nonzero-area triangles; cleanup was not published.");
            // Instantiate retains raw streams, skin weights and every blend shape.
            // No vertex compaction: the only mutation is each submesh's index list.
            var copy = UnityEngine.Object.Instantiate(mesh);
            owned.Add(copy); copy.hideFlags = HideFlags.HideAndDontSave;
            copy.name = mesh.name + "_reverseInput";
            for (int sub = 0; sub < parts.Length; ++sub) copy.SetTriangles(parts[sub], sub, false);
            copy.bounds = mesh.bounds;
            MeshUvState.SetDraft(copy, MeshUvState.IsDraft(mesh));
            return new ReverseUvTransfer.Input { mesh = copy, key = source.key, toWorld = source.toWorld,
                sourceFaces = retained.ToArray(), removedSourceFaces = removed.ToArray() };
        }

        internal int PrepareSeed(int resolution, int padding, float density, CancellationToken token)
        {
            var inputs = levels[0].inputs;
            var meshes = ReverseUvSeed.Prepare(inputs, resolution, padding, density, token, out int size);
            owned.AddRange(meshes);
            for (int i = 0; i < meshes.Length; ++i) inputs[i].mesh = meshes[i];
            return size;
        }

        static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x)
            && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);

        public void Dispose()
        {
            foreach (var mesh in owned) if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
            owned.Clear();
        }
    }
}
