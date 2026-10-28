// MeshInstances.cs — Renderers showing copies of one FBX mesh.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Copies of one FBX mesh. Unity imports a mesh that several nodes instance once per
    /// node, every copy under the mesh's name, while the file keeps one mesh: all copies
    /// can take only one edit. The pipeline processes one copy and gives its result to the others.
    /// </summary>
    internal static class MeshInstances
    {
        /// <summary>
        /// The included entries that copy another, each with the entry it copies: a mesh of the
        /// same asset, under the same name, with the same baseline content. The copied entry is
        /// in the source LOD when one of them is, else in the lowest LOD; entry order breaks ties.
        /// </summary>
        internal static Dictionary<MeshEntry, MeshEntry> Copies(IEnumerable<MeshEntry> entries, int sourceLod)
            => Copies(entries, sourceLod, AssetDatabase.GetAssetPath);

        internal static Dictionary<MeshEntry, MeshEntry> Copies(IEnumerable<MeshEntry> entries, int sourceLod, Func<Mesh, string> assetPath)
        {
            var copies = new Dictionary<MeshEntry, MeshEntry>();
            var named = entries.Where(e => e != null && e.include && e.fbxMesh != null && e.Baseline != null)
                .GroupBy(e => (path: assetPath(e.fbxMesh), name: e.fbxMesh.name))
                .Where(g => !string.IsNullOrEmpty(g.Key.path) && g.Skip(1).Any());
            foreach (var group in named)
            {
                var first = new Dictionary<string, MeshEntry>(StringComparer.Ordinal);
                foreach (var e in group.OrderBy(e => e.lodIndex == sourceLod ? -1 : e.lodIndex))
                {
                    string content = Content(e.Baseline);
                    if (content == null) continue;
                    if (first.TryGetValue(content, out var copied)) copies[e] = copied;
                    else first[content] = e;
                }
            }
            return copies;
        }

        /// <summary>True when both meshes store the same content (<see cref="MeshAccess.Fingerprint"/>), false when either cannot be read.</summary>
        internal static bool SameContent(Mesh a, Mesh b)
        {
            string content = Content(a);
            return content != null && content == Content(b);
        }

        static string Content(Mesh mesh)
        {
            var fingerprint = MeshAccess.Fingerprint(mesh);
            return fingerprint == null ? null : Convert.ToBase64String(fingerprint);
        }
    }
}
