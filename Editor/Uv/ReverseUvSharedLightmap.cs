using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Reverse UV for LODs that read the LOD0 lightmap (one atlas and scale/offset for a
    /// renderer and its LODs, as Bakery binds a LODGroup). A coarser island whose family
    /// continues on no LOD0 face would read texels where LOD0 has no surface, which bake
    /// empty, and would hold that empty space in the LOD0 lightmap. Such a family is not
    /// packed: its faces take the UV2 of the LOD0 surface under them, as the forward
    /// transfer's <see cref="SharedLightmapFallback"/> places a collapsed shell, and overlap
    /// other faces there by design, since they read the light of the surface they lie on.
    /// </summary>
    internal static class ReverseUvSharedLightmap
    {
        /// <summary>
        /// Removes from <paramref name="families"/> each family with no face at the finest LOD
        /// whose meshes all have a finest-LOD mesh among <paramref name="surfaces"/> to read, and
        /// marks its faces <see cref="ReverseUvTransfer.Face.readsLod0"/>. Every family stays when
        /// none would be left to pack.
        /// </summary>
        internal static void SetAside(List<ReverseUvTransfer.Surface[]> levels, ReverseUvTransfer.Surface[] surfaces,
            SortedDictionary<int, List<(ReverseUvTransfer.Surface surface, int corner)>> families, CancellationToken token)
        {
            foreach (var surface in surfaces)
                foreach (var face in surface.faces) face.readsLod0 = false;
            var finest = levels[levels.Count - 1];
            int lod0 = finest[0].lod;
            var included = new HashSet<ReverseUvTransfer.Surface>(surfaces);
            var keys = new Dictionary<ReverseUvTransfer.Surface, string>();
            var readable = new HashSet<string>(finest.Where(included.Contains).Select(s => Key(s, keys)));
            var orphans = new List<int>();
            foreach (var family in families)
            {
                token.ThrowIfCancellationRequested();
                if (family.Value.All(c => c.surface.lod != lod0 && readable.Contains(Key(c.surface, keys)))) orphans.Add(family.Key);
            }
            if (orphans.Count == 0 || orphans.Count == families.Count) return;
            var nodes = orphans.SelectMany(id => families[id].Select(c => (id, c.surface))).GroupBy(p => p.surface)
                .OrderByDescending(g => g.Key.lod).Select(g => $"LOD{g.Key.lod} '{g.Key.input.key}' {g.Select(p => p.id).Distinct().Count()} ({g.Count() / 3} faces)")
                .ToList();
            foreach (int id in orphans)
            {
                foreach (var corner in families[id]) corner.surface.faces[corner.corner / 3].readsLod0 = true;
                families.Remove(id);
            }
            UvtLog.Info(UvtLog.Category.Repack, $"[ReverseUV] Shared LOD0 lightmap: {orphans.Count} island(s) continue on no LOD0 face; "
                + $"they take no atlas space and read the LOD0 surface under them: {string.Join(", ", nodes)}.");
        }

        /// <summary>
        /// Gives each face marked <see cref="ReverseUvTransfer.Face.readsLod0"/> the UV2 of the
        /// finest-LOD surface of its mesh under it, in the pixels of its atlas of
        /// <paramref name="side"/>, once every atlas is packed.
        /// </summary>
        internal static void Place(List<ReverseUvTransfer.Surface[]> levels, Func<ReverseUvTransfer.Surface, int> side,
            ReverseUvTransfer.Report report, CancellationToken token)
        {
            var finest = levels[levels.Count - 1];
            var keys = new Dictionary<ReverseUvTransfer.Surface, string>();
            var placement = new SharedLightmapFallback.Report();
            foreach (var mesh in levels.Take(levels.Count - 1).SelectMany(l => l)
                .Where(s => s.faces.Any(f => f.readsLod0)).GroupBy(s => Key(s, keys)))
            {
                var surface = Lod0Surface(finest.Where(s => Key(s, keys) == mesh.Key), side, token);
                foreach (var target in mesh)
                    for (int f = 0; f < target.faces.Length; ++f)
                    {
                        if (!target.faces[f].readsLod0) continue;
                        token.ThrowIfCancellationRequested();
                        if (!surface.Place(Corners(target, f), placement, out var uv, out _, anyFacing: true))
                            throw new InvalidOperationException($"LOD{target.lod} '{target.input.key}' face {f} finds no LOD0 surface to read; no result was published.");
                        for (int k = 0; k < 3; ++k) target.pixels[f * 3 + Wound(target, k)] = uv[k] * side(target);
                    }
            }
            report.readsLod0Faces = placement.placed;
            if (placement.placed > 0)
                UvtLog.Info(UvtLog.Category.Repack, $"[ReverseUV] Shared LOD0 lightmap: {placement.placed} face(s) read the LOD0 surface under them "
                    + $"(farthest {placement.farthest:G3}; {placement.atPoint} at the one point under their centre; {placement.facingAway} on a LOD0 surface facing away).");
        }

        /// <summary>
        /// <paramref name="triangles"/> without the faces that read the LOD0 surface: those overlap
        /// by design and may meet at one point, so the atlas audits leave them out.
        /// </summary>
        internal static int[] Audited(int[] triangles, ReverseUvTransfer.Face[] faces)
        {
            if (faces == null || !faces.Any(f => f.readsLod0)) return triangles;
            var audited = new List<int>(triangles.Length);
            for (int f = 0; f < faces.Length; ++f)
                if (!faces[f].readsLod0)
                    for (int k = 0; k < 3; ++k) audited.Add(triangles[f * 3 + k]);
            return audited.ToArray();
        }

        static string Key(ReverseUvTransfer.Surface surface, Dictionary<ReverseUvTransfer.Surface, string> keys)
        {
            if (!keys.TryGetValue(surface, out var key)) keys.Add(surface, key = UvToolContext.ExtractGroupKey(surface.input.key ?? string.Empty));
            return key;
        }

        /// <summary>The corner of face slot <paramref name="k"/>, wound as in the shared frame: a mirrored transform reverses it.</summary>
        static int Wound(ReverseUvTransfer.Surface surface, int k) => surface.orientation < 0 && k > 0 ? 3 - k : k;

        static Vector3[] Corners(ReverseUvTransfer.Surface surface, int face)
        {
            var corners = new Vector3[3];
            for (int k = 0; k < 3; ++k) corners[k] = surface.positions[surface.indices[face * 3 + Wound(surface, k)]];
            return corners;
        }

        /// <summary>
        /// The finest-LOD meshes' packed faces in the shared frame, one vertex per position and
        /// UV2, so vertex-connected faces are one UV2 chart.
        /// </summary>
        static SharedLightmapFallback.Surface Lod0Surface(IEnumerable<ReverseUvTransfer.Surface> sources,
            Func<ReverseUvTransfer.Surface, int> side, CancellationToken token)
        {
            var positions = new List<Vector3>(); var uv2 = new List<Vector2>(); var triangles = new List<int>();
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                var vertices = new Dictionary<(int, Vector2), int>();
                float atlas = side(source);
                for (int f = 0; f < source.faces.Length; ++f)
                    for (int k = 0; k < 3; ++k)
                    {
                        int c = f * 3 + Wound(source, k);
                        var key = (source.indices[c], source.pixels[c]);
                        if (!vertices.TryGetValue(key, out int vertex))
                        {
                            vertex = positions.Count; vertices.Add(key, vertex);
                            positions.Add(source.positions[source.indices[c]]); uv2.Add(source.pixels[c] / atlas);
                        }
                        triangles.Add(vertex);
                    }
            }
            var uv = uv2.ToArray();
            return new SharedLightmapFallback.Surface(positions.ToArray(), triangles.ToArray(), uv, uv);
        }
    }
}
