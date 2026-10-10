using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Controlled source preparation on owned copies of imported FBX sub-assets.</summary>
    internal static class TransferBenchmarkAssets
    {
        [Serializable] internal sealed class Case
        {
            public string asset, label, symmetry = "off";
            public string[] groups = Array.Empty<string>();
            public int sourceLod, textureWidth = 1, textureHeight = 1, resolution = 512;
            public bool correctSourceAspect = true, normalizeDensity = true, arap = true;
            public bool includeHigherDetailTargets;
            public bool weldUv0, preOptimize;
        }

        internal sealed class Pair
        {
            internal Case settings;
            internal Mesh source, target;
            internal string name;
        }

        internal static List<Pair> Pairs(Case settings)
        {
            if (settings == null || string.IsNullOrEmpty(settings.asset) || !settings.asset.StartsWith("Assets/", StringComparison.Ordinal)
                || settings.textureWidth < 1 || settings.textureHeight < 1 || settings.textureWidth > 65536 || settings.textureHeight > 65536
                || settings.resolution < 64 || settings.resolution > 4096 || settings.sourceLod < 0 || settings.sourceLod > 7
                || settings.groups == null || settings.groups.Length > TransferBenchmark.MaxCases
                || (settings.symmetry != "off" && settings.symmetry != "legacy" && settings.symmetry != "adaptive"))
                throw new InvalidDataException("Invalid imported-asset benchmark case.");
            var sources = new Dictionary<string, Mesh>(StringComparer.Ordinal);
            var targets = new List<Mesh>();
            CollectMeshes(settings, sources, targets);
            targets.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            var pairs = new List<Pair>();
            var groups = new HashSet<string>(settings.groups, StringComparer.Ordinal);
            var foundGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in targets) {
                string key = UvToolContext.ExtractGroupKey(target.name);
                if (groups.Count > 0 && !groups.Contains(key)) continue;
                if (!sources.TryGetValue(key, out var source))
                    throw new InvalidDataException("No matching source LOD for " + target.name);
                foundGroups.Add(key);
                pairs.Add(new Pair { settings = settings, source = source, target = target,
                    name = (string.IsNullOrEmpty(settings.label) ? Path.GetFileNameWithoutExtension(settings.asset) : settings.label) + "/" + target.name });
            }
            if (groups.Count > 0 && !groups.SetEquals(foundGroups)) throw new InvalidDataException("Some requested mesh groups have no LOD pairs.");
            if (pairs.Count == 0) throw new InvalidDataException("Asset has no matched Name_LOD{N} mesh pairs: " + settings.asset);
            return pairs;
        }

        static void CollectMeshes(Case settings, Dictionary<string, Mesh> sources, List<Mesh> targets)
        {
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(settings.asset)) {
                if (!(obj is Mesh mesh)) continue;
                int lod = MeshNaming.LodIndex(mesh.name);
                if (lod < 0) continue;
                if (lod == settings.sourceLod) {
                    string key = UvToolContext.ExtractGroupKey(mesh.name);
                    if (sources.ContainsKey(key)) throw new InvalidDataException("Ambiguous source group: " + key);
                    sources.Add(key, mesh);
                }
                else if (lod > settings.sourceLod || settings.includeHigherDetailTargets) targets.Add(mesh);
            }
        }

        internal static TransferBenchmark.Input Prepare(Pair pair)
        {
            var settings = pair.settings;
            var input = new TransferBenchmark.Input { name = pair.name,
                description = "Imported-asset preparation (not UI auto-tune): " + JsonUtility.ToJson(settings),
                capture = settings.asset, clamp = true };
            var mode = SymmetrySplitShells.CurrentThresholdMode;
            int fallback = SymmetrySplitShells.LastFallbackCount, total = SymmetrySplitShells.LastTotalSplitCount;
            try {
                input.source = MeshAccess.ReadableCopy(pair.source); input.target = MeshAccess.ReadableCopy(pair.target);
                input.source = PrepareWeld(input.source, settings);
                input.target = PrepareWeld(input.target, settings);
                if (settings.symmetry != "off") {
                    SymmetrySplitShells.CurrentThresholdMode = settings.symmetry == "adaptive" ? SymmetrySplitShells.ThresholdMode.Adaptive : SymmetrySplitShells.ThresholdMode.LegacyFixed;
                    var sourceShells = UvShellExtractor.Extract(input.source.uv, input.source.triangles, true);
                    SymmetrySplitShells.Split(input.source, sourceShells, out var parameters);
                    var targetShells = UvShellExtractor.Extract(input.target.uv, input.target.triangles, true);
                    SymmetrySplitShells.SplitWithParams(input.target, targetShells, parameters);
                }
                var metric = new SourceTextureUvMetric { uvScale = new Vector2(Mathf.Sqrt((float)settings.textureWidth / settings.textureHeight),
                    Mathf.Sqrt((float)settings.textureHeight / settings.textureWidth)) };
                var saved = metric.PrepareTemporaryMesh(input.source, settings.correctSourceAspect);
                try {
                    var options = RepackOptions.Default; options.resolution = (uint)settings.resolution;
                    options.normalizeTexelDensity = settings.normalizeDensity; options.reparameterizeStretchedShells = settings.arap;
                    var packed = XatlasRepack.RepackSingle(input.source, options);
                    if (!packed.ok) throw new InvalidDataException("Source repack failed: " + packed.error);
                    uint side = SourceTextureUvMetric.NormalizePackedLightmap(input.source, packed.atlasWidth, packed.atlasHeight);
                    input.atlasWidth = input.atlasHeight = (int)side;
                }
                finally { saved.Restore(input.source); }
                // A comparison baseline, explicitly not independent correspondence truth.
                var result = GroupedShellTransfer.Transfer(input.target, input.source, null, null, input.atlasWidth, input.atlasHeight);
                if (result.uv2 == null) throw new InvalidDataException("Baseline transfer failed for prepared asset.");
                input.reference = result.uv2; XatlasRepack.ClampUvsToUnit(input.reference);
                return input;
            }
            catch { input.Dispose(); throw; }
            finally {
                SymmetrySplitShells.CurrentThresholdMode = mode;
                SymmetrySplitShells.LastFallbackCount = fallback; SymmetrySplitShells.LastTotalSplitCount = total;
            }
        }

        static Mesh PrepareWeld(Mesh mesh, Case settings)
        {
            if (settings.preOptimize) {
                var optimized = MeshOptimizer.OptimizeUvTriangleOrder(mesh);
                if (!optimized.ok) throw new InvalidDataException("Pre-optimization failed: " + optimized.error);
            }
            if (!settings.weldUv0) return mesh;
            var welded = Uv0Analyzer.UvEdgeWeld(mesh);
            if (welded != null && welded != mesh) { UnityEngine.Object.DestroyImmediate(mesh); return welded; }
            return mesh;
        }
    }
}
