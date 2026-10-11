using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Resolves a selected mesh family without changing LODGroup state or scene meshes.</summary>
    internal static class FoliageLodSelection
    {
        internal sealed class Target
        {
            internal MeshRenderer renderer;
            internal FoliageNormalsSettings settings;
            internal bool[] submeshes;
            internal bool proxySource;
            internal int lodLevel;
        }

        internal static List<Target> Resolve(GameObject root, FoliageNormalsSettings settings, CancellationToken token,
            Func<MeshRenderer, Mesh> meshFor = null)
        {
            meshFor ??= SharedMesh;
            var seeds = root.GetComponentsInChildren<MeshRenderer>()
                .Where(r => Eligible(r, meshFor(r)) && (!(settings.filterRenderers || settings.renderers.Length > 0) ||
                    Array.IndexOf(settings.renderers, r) >= 0))
                .OrderBy(r => Level(r, meshFor(r))).ToArray();
            var result = new List<Target>();
            var visited = new HashSet<MeshRenderer>();
            foreach (var seed in seeds)
            {
                token.ThrowIfCancellationRequested();
                if (visited.Contains(seed)) continue;
                var family = Family(seed, meshFor);
                if (family.Any(visited.Contains))
                    throw new InvalidOperationException($"Overlapping foliage LOD families for {seed.name}. Use distinct mesh family names.");
                visited.UnionWith(family);
                result.AddRange(ResolveFamily(seed, family, settings, meshFor, token));
            }
            return result;
        }

        static List<Target> ResolveFamily(MeshRenderer seed, List<MeshRenderer> family, FoliageNormalsSettings settings,
            Func<MeshRenderer, Mesh> meshFor, CancellationToken token)
        {
            var result = new List<Target>();
            foreach (var renderer in family)
            {
                token.ThrowIfCancellationRequested();
                var mesh = meshFor(renderer);
                if (!Eligible(renderer, mesh)) continue;
                var selected = Remap(seed, renderer, settings, meshFor);
                var mask = Mask(mesh, renderer.sharedMaterials, selected);
                if (!Enumerable.Range(0, mask.Length).Any(s => mask[s] && mesh.GetIndexCount(s) > 0)) continue;
                result.Add(new Target { renderer = renderer, settings = selected, submeshes = mask,
                    proxySource = result.Count == 0 || !settings.lod0Only, lodLevel = Level(renderer, mesh) });
            }
            return result;
        }

        internal static bool HasEligibleRenderer(GameObject root) => root && root.GetComponentsInChildren<MeshRenderer>()
            .Any(renderer => Eligible(renderer, SharedMesh(renderer)));

        internal static GameObject SelectionRoot(GameObject selected)
        {
            if (!HasEligibleRenderer(selected)) return null;
            var group = selected.GetComponentsInParent<LODGroup>().FirstOrDefault(g => g.gameObject == selected ||
                g.GetLODs().SelectMany(lod => lod.renderers).OfType<MeshRenderer>().Any(renderer =>
                    Eligible(renderer, SharedMesh(renderer)) && renderer.transform.IsChildOf(selected.transform)));
            return group ? group.gameObject : selected;
        }

        internal static Mesh SharedMesh(MeshRenderer renderer)
        {
            var filter = renderer ? renderer.GetComponent<MeshFilter>() : null;
            return filter ? filter.sharedMesh : null;
        }

        static bool Eligible(MeshRenderer renderer, Mesh mesh) => renderer && renderer.enabled &&
            renderer.gameObject.activeInHierarchy && mesh && !MeshNaming.IsCollision(renderer.name) && !MeshNaming.IsCollision(mesh.name);

        static bool[] Mask(Mesh mesh, Material[] materials, FoliageNormalsSettings settings) =>
            Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetTopology(s) == MeshTopology.Triangles &&
                FoliageTargetSnapshot.SelectsSubmesh(s, materials, settings)).ToArray();

        internal static int Level(MeshRenderer renderer, Mesh mesh)
        {
            var group = Group(renderer);
            if (group)
            {
                var lods = group.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                    if (Array.IndexOf(lods[i].renderers, renderer) >= 0) return i;
            }
            return NamedLevel(renderer, mesh);
        }

        static int NamedLevel(MeshRenderer renderer, Mesh mesh)
        {
            int level = MeshNaming.LodIndex(renderer.name);
            if (level >= 0) return level;
            return mesh ? MeshNaming.LodIndex(mesh.name) : -1;
        }

        static LODGroup Group(MeshRenderer renderer) => renderer.GetComponentsInParent<LODGroup>(true)
            .FirstOrDefault(g => g.GetLODs().Any(lod => Array.IndexOf(lod.renderers, renderer) >= 0));

        static List<MeshRenderer> Family(MeshRenderer anchor, Func<MeshRenderer, Mesh> meshFor)
        {
            var group = Group(anchor);
            if (group) return GroupFamily(anchor, group.GetLODs(), meshFor);
            var mesh = meshFor(anchor);
            var parent = anchor.transform.parent;
            if (!parent || NamedLevel(anchor, mesh) < 0) return new List<MeshRenderer> { anchor };
            return NamedFamily(anchor, parent, meshFor);
        }

        static string FamilyKey(MeshRenderer renderer, Mesh mesh, bool namedRenderer) =>
            UvToolContext.ExtractGroupKey(namedRenderer ? renderer.name : mesh?.name);

        static List<MeshRenderer> NamedFamily(MeshRenderer anchor, Transform parent, Func<MeshRenderer, Mesh> meshFor)
        {
            bool namedRenderer = MeshNaming.LodIndex(anchor.name) >= 0;
            string key = FamilyKey(anchor, meshFor(anchor), namedRenderer);
            var family = new List<MeshRenderer>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var candidate = parent.GetChild(i).GetComponent<MeshRenderer>();
                if (!candidate || Group(candidate)) continue;
                var candidateMesh = meshFor(candidate);
                if (!Eligible(candidate, candidateMesh) || NamedLevel(candidate, candidateMesh) < 0) continue;
                string candidateKey = FamilyKey(candidate, candidateMesh, namedRenderer);
                if (string.Equals(key, candidateKey, StringComparison.OrdinalIgnoreCase)) family.Add(candidate);
            }
            if (family.GroupBy(r => NamedLevel(r, meshFor(r))).Any(level => level.Count() > 1))
                throw new InvalidOperationException($"Ambiguous foliage LOD siblings for {anchor.name}. Use distinct mesh family names.");
            return family.OrderBy(r => NamedLevel(r, meshFor(r))).ToList();
        }

        static List<MeshRenderer> GroupFamily(MeshRenderer anchor, LOD[] lods, Func<MeshRenderer, Mesh> meshFor)
        {
            var origin = lods.First(lod => Array.IndexOf(lod.renderers, anchor) >= 0).renderers.Where(r => r).Distinct().ToArray();
            var result = new List<MeshRenderer>();
            foreach (var lod in lods)
            {
                var members = lod.renderers.Where(r => r).Distinct().ToArray();
                var match = MatchLevel(anchor, origin, members, meshFor);
                if (match && !result.Contains(match)) result.Add(match);
            }
            return result;
        }

        static MeshRenderer MatchLevel(MeshRenderer anchor, Renderer[] origin, Renderer[] members, Func<MeshRenderer, Mesh> meshFor)
        {
            if (Array.IndexOf(members, anchor) >= 0) return anchor;
            string rendererKey = UvToolContext.ExtractGroupKey(anchor.name);
            bool uniqueRendererKey = origin.Count(r => string.Equals(rendererKey,
                UvToolContext.ExtractGroupKey(r.name), StringComparison.OrdinalIgnoreCase)) == 1;
            var match = Unique(members.OfType<MeshRenderer>().Where(r => uniqueRendererKey && Eligible(r, meshFor(r)) &&
                string.Equals(rendererKey, UvToolContext.ExtractGroupKey(r.name), StringComparison.OrdinalIgnoreCase)), anchor);
            if (match) return match;
            string meshKey = UvToolContext.ExtractGroupKey(meshFor(anchor).name);
            bool uniqueMeshKey = origin.OfType<MeshRenderer>().Count(r => meshFor(r) &&
                string.Equals(meshKey, UvToolContext.ExtractGroupKey(meshFor(r).name), StringComparison.OrdinalIgnoreCase)) == 1;
            if (uniqueMeshKey)
                match = Unique(members.OfType<MeshRenderer>().Where(r => Eligible(r, meshFor(r)) &&
                    string.Equals(meshKey, UvToolContext.ExtractGroupKey(meshFor(r).name), StringComparison.OrdinalIgnoreCase)), anchor);
            if (!match && origin.Length == 1 && members.Length == 1) match = members[0] as MeshRenderer;
            return match;
        }

        static MeshRenderer Unique(IEnumerable<MeshRenderer> candidates, MeshRenderer anchor)
        {
            var matches = candidates.ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException($"Ambiguous foliage LOD match for {anchor.name}. Use distinct mesh family names.");
            return matches.FirstOrDefault();
        }

        static FoliageNormalsSettings Remap(MeshRenderer anchor, MeshRenderer renderer, FoliageNormalsSettings settings,
            Func<MeshRenderer, Mesh> meshFor)
        {
            if (settings.selection != FoliageFaceSelection.SelectedSubmeshIndices || renderer == anchor || meshFor(renderer) == meshFor(anchor))
                return settings;
            var source = meshFor(anchor);
            var materials = anchor.sharedMaterials;
            var selected = Mask(source, materials, settings);
            var mapped = settings.Snapshot();
            if (source.subMeshCount == 1 && meshFor(renderer).subMeshCount == 1)
            {
                return mapped;
            }
            var selectedMaterials = new HashSet<Material>();
            for (int s = 0; s < selected.Length; s++)
            {
                if (!selected[s]) continue;
                var material = s < materials.Length ? materials[s] : null;
                if (!material || Enumerable.Range(0, selected.Length).Any(other => !selected[other] && other < materials.Length && materials[other] == material))
                    throw new InvalidOperationException($"Cannot match selected submeshes from {anchor.name} to {renderer.name}: selected and excluded faces need distinct material references.");
                selectedMaterials.Add(material);
            }
            var targetMaterials = renderer.sharedMaterials;
            mapped.submeshes = Enumerable.Range(0, meshFor(renderer).subMeshCount)
                .Where(s => s < targetMaterials.Length && selectedMaterials.Contains(targetMaterials[s])).ToArray();
            return mapped;
        }
    }
}
