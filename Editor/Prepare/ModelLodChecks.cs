// ModelLodChecks.cs — the LOD chain of a model's parts and its node names, against
// ModelRequirements. A part is the render nodes one MeshNaming.GroupKey pairs
// (Seat_LOD0, Seat_LOD1 → Seat); each LOD must have fewer triangles than the one before
// it, and every part the same levels without gaps. A render node without a LOD suffix
// stands alone. Sibling nodes sharing a name are reported: steps pair nodes by name.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ModelLodChecks
    {
        /// <summary>
        /// Checks the LOD chain of the render nodes (name, triangle count) into
        /// <paramref name="report"/>: gaps and missing levels, levels past a LODGroup's eight,
        /// and each LOD's triangles against the LOD before it.
        /// </summary>
        internal static void CheckLods(IEnumerable<(string name, int triangles)> renderNodes, ModelRequirements requirements, PrepareReport report)
        {
            var parts = new SortedDictionary<string, SortedDictionary<int, int>>(StringComparer.Ordinal);
            var beyond = new List<string>();
            foreach (var (name, triangles) in renderNodes)
            {
                if (!MeshNaming.TryParseLodSuffix(name, out _, out int lod)) continue;
                if (lod >= MeshNaming.MaxLodLevels) { beyond.Add($"'{name}'"); continue; }
                string key = MeshNaming.GroupKey(name);
                if (!parts.TryGetValue(key, out var levels)) parts[key] = levels = new SortedDictionary<int, int>();
                levels.TryGetValue(lod, out int sum);
                levels[lod] = sum + triangles;
            }
            if (beyond.Count > 0)
                report.Add(requirements.lodChain, "LODs", "LOD chain",
                    $"{string.Join(", ", beyond)}: past the {MeshNaming.MaxLodLevels} levels a LODGroup holds, so no level takes {(beyond.Count == 1 ? "it" : "them")}");
            if (parts.Count == 0) return;

            int deepest = parts.Values.Max(levels => levels.Keys.Max());
            foreach (var part in parts)
            {
                string chain = ChainFinding(part.Value.Keys.ToList(), deepest);
                if (chain != null) report.Add(requirements.lodChain, $"'{part.Key}'", "LOD chain", chain);
                CheckTriangles(part.Key, part.Value, requirements, report);
            }
        }

        /// <summary>The levels a part misses up to the deepest level any part has; null when it has them all.</summary>
        internal static string ChainFinding(List<int> levels, int deepest)
        {
            var missing = Enumerable.Range(0, deepest + 1).Except(levels).ToList();
            if (missing.Count == 0) return null;
            string has = string.Join(", ", levels.Select(l => $"LOD{l}"));
            string lacks = string.Join(", ", missing.Select(l => $"LOD{l}"));
            return levels.Max() < deepest && missing.All(l => l > levels.Max())
                ? $"has {has}; other parts go to LOD{deepest}"
                : $"has {has}; missing {lacks}";
        }

        static void CheckTriangles(string part, SortedDictionary<int, int> levels, ModelRequirements requirements, PrepareReport report)
        {
            int previousLod = -1, previous = 0;
            foreach (var level in levels)
            {
                if (previousLod >= 0)
                {
                    int lod = level.Key, triangles = level.Value;
                    if (triangles >= previous)
                        report.Add(requirements.lodReduction, $"'{part}'", "LOD triangles",
                            $"LOD{lod} has {triangles} triangles, not fewer than LOD{previousLod}'s {previous}");
                    else if (triangles > requirements.maxLodRatio * previous)
                        report.Add(requirements.lodRatio, $"'{part}'", "LOD triangles",
                            $"LOD{lod} keeps {100.0 * triangles / previous:F0}% of LOD{previousLod}'s {previous} triangles (at most {requirements.maxLodRatio * 100f:F0}%)");
                }
                previousLod = level.Key; previous = level.Value;
            }
        }

        /// <summary>Sibling nodes sharing a name, listed with their parent's path.</summary>
        internal static void CheckNames(GameObject root, ModelRequirements requirements, PrepareReport report)
        {
            if (requirements.duplicateNames == RequirementLevel.Off) return;
            var duplicates = new List<string>();
            foreach (var parent in root.GetComponentsInChildren<Transform>(true))
            {
                var names = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Transform child in parent)
                {
                    names.TryGetValue(child.name, out int count);
                    names[child.name] = count + 1;
                }
                foreach (var name in names.Where(n => n.Value > 1))
                    duplicates.Add($"'{name.Key}' ×{name.Value} under '{parent.name}'");
            }
            if (duplicates.Count > 0)
                report.Add(requirements.duplicateNames, $"'{root.name}'", "Names",
                    $"sibling nodes share a name: {string.Join(", ", duplicates)}; steps that pair nodes by name cannot tell them apart");
        }
    }
}
