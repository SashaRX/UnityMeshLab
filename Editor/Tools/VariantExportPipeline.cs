// VariantExportPipeline.cs — bake → FBX export → prefab clone for solid color variants.
// Used by VertexColorBakingTool to produce {baseName}_{suffix}.fbx + matching prefab.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class VariantExportPipeline
    {
        public enum ConflictPolicy { Overwrite, AutoIncrement, Cancel }

        public struct Variant
        {
            public Color  color;
            public string suffix;
        }

        public struct Result
        {
            public bool   ok;
            public string error;
            public string suffix;
            public Color  color;
            public string fbxPath;
            public string prefabPath;
        }

        public static bool ValidateSuffix(string suffix, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(suffix))
            {
                error = "Suffix is empty.";
                return false;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(suffix, @"^[A-Za-z0-9_]+$"))
            {
                error = "Suffix may contain only letters, digits, and underscore.";
                return false;
            }
            return true;
        }

        // Paint mesh.colors32 with a uniform Color32 across every mesh variant
        // of an entry (originalMesh, repackedMesh, transferredMesh, fbxMesh).
        // Skips collision meshes via MeshHygieneUtility. Returns the number of
        // distinct meshes painted.
        public static int BakeSolidColorOnEntries(IEnumerable<MeshEntry> entries, Color color)
        {
            if (entries == null) return 0;
            var color32 = (Color32)color;
            var painted = new HashSet<Mesh>();

            foreach (var entry in entries)
            {
                if (entry == null || entry.renderer == null || !entry.include) continue;
                if (MeshHygieneUtility.IsCollisionNodeName(entry.renderer.name)) continue;

                var meshes = new[] { entry.originalMesh, entry.repackedMesh, entry.transferredMesh, entry.fbxMesh };
                foreach (var mesh in meshes)
                {
                    if (mesh == null || mesh.vertexCount == 0) continue;
                    if (!painted.Add(mesh)) continue;

                    Undo.RecordObject(mesh, "Bake Solid Color");
                    var arr = new Color32[mesh.vertexCount];
                    for (int i = 0; i < arr.Length; i++) arr[i] = color32;
                    mesh.colors32 = arr;
                    EditorUtility.SetDirty(mesh);
                }
            }
            return painted.Count;
        }

        // Run a batch of variants against the same source FBX/prefab. All
        // disk writes are wrapped in StartAssetEditing/StopAssetEditing so
        // Unity refreshes once at the end.
        public static IList<Result> ExportVariants(
            LightmapTransferTool fbxExporter,
            string sourceFbxPath,
            GameObject sourcePrefab,
            IList<MeshEntry> entries,
            IList<Variant> variants,
            ConflictPolicy conflictPolicy)
        {
            var results = new List<Result>();

            if (fbxExporter == null)
            {
                results.Add(Fail("", default, "Internal error: fbxExporter is null."));
                return results;
            }
            if (string.IsNullOrEmpty(sourceFbxPath))
            {
                results.Add(Fail("", default, "Source FBX path is empty."));
                return results;
            }
            if (entries == null || entries.Count == 0)
            {
                results.Add(Fail("", default, "No mesh entries supplied."));
                return results;
            }
            if (variants == null || variants.Count == 0)
            {
                results.Add(Fail("", default, "No variants supplied."));
                return results;
            }

            // Validate all suffixes upfront; one bad suffix shouldn't cancel
            // the whole batch silently — surface every failure at once.
            bool anyInvalid = false;
            foreach (var v in variants)
            {
                if (!ValidateSuffix(v.suffix, out string err))
                {
                    results.Add(Fail(v.suffix, v.color, err));
                    anyInvalid = true;
                }
            }
            if (anyInvalid) return results;

            // Detect duplicate suffixes inside the batch — would either fight
            // for the same output path or auto-increment unpredictably.
            // Case-insensitive: case-insensitive filesystems (default on Windows
            // and macOS) collapse "Red" and "red" to the same FBX path, which
            // would silently clobber one variant's output with the other.
            var dupes = variants
                .GroupBy(v => v.suffix, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (dupes.Count > 0)
            {
                results.Add(Fail("", default, "Duplicate suffix(es) in batch: " + string.Join(", ", dupes)));
                return results;
            }

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var v in variants)
                    results.Add(ExportSingleVariant(fbxExporter, sourceFbxPath, sourcePrefab, entries, v, conflictPolicy));
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            return results;
        }

        static Result ExportSingleVariant(
            LightmapTransferTool fbxExporter,
            string sourceFbxPath,
            GameObject sourcePrefab,
            IList<MeshEntry> entries,
            Variant variant,
            ConflictPolicy policy)
        {
            if (!ResolveOutputPaths(sourceFbxPath, sourcePrefab, variant.suffix, policy,
                                    out string outFbxPath, out string outPrefabPath, out string err))
                return Fail(variant.suffix, variant.color, err);

            int painted = BakeSolidColorOnEntries(entries, variant.color);
            if (painted == 0)
                return Fail(variant.suffix, variant.color, "No paintable meshes (all collision, excluded, or empty).");

            bool fbxOk = fbxExporter.ExportVertexColorsToFbxAs(sourceFbxPath, outFbxPath, entries);
            if (!fbxOk)
                return Fail(variant.suffix, variant.color, "FBX export failed (see Console).");

            // Force-reimport so AssetDatabase exposes the new sub-meshes BEFORE
            // we try to bind them into the prefab clone below.
            AssetDatabase.ImportAsset(outFbxPath, ImportAssetOptions.ForceUpdate);

            string prefabPathOut = null;
            if (sourcePrefab != null && !string.IsNullOrEmpty(outPrefabPath))
            {
                try
                {
                    BuildPrefabClone(sourcePrefab, outFbxPath, outPrefabPath);
                    prefabPathOut = outPrefabPath;
                }
                catch (Exception ex)
                {
                    UvtLog.Error($"[VariantExport] Prefab clone failed for '{variant.suffix}': {ex.Message}");
                    return new Result
                    {
                        ok = false,
                        error = "Prefab clone failed: " + ex.Message,
                        suffix = variant.suffix,
                        color = variant.color,
                        fbxPath = outFbxPath,
                    };
                }
            }

            UvtLog.Info(
                $"[VariantExport] '{variant.suffix}' done — FBX={outFbxPath}" +
                (prefabPathOut != null ? $", Prefab={prefabPathOut}" : " (no prefab)"));
            return new Result
            {
                ok = true,
                suffix = variant.suffix,
                color = variant.color,
                fbxPath = outFbxPath,
                prefabPath = prefabPathOut,
            };
        }

        // Clone source prefab, swap every MeshFilter.sharedMesh to the matching
        // sub-mesh in the new FBX (matched by name), unpack so the result is a
        // standalone prefab (full clone, no variant relationship), and save.
        static void BuildPrefabClone(GameObject sourcePrefab, string newFbxPath, string outPrefabPath)
        {
            var newMeshesByName = new Dictionary<string, Mesh>(StringComparer.Ordinal);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(newFbxPath))
            {
                if (asset is Mesh m && !newMeshesByName.ContainsKey(m.name))
                    newMeshesByName[m.name] = m;
            }
            if (newMeshesByName.Count == 0)
                throw new Exception($"New FBX '{newFbxPath}' contains no meshes.");

            var clone = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
            if (clone == null)
                throw new Exception("PrefabUtility.InstantiatePrefab returned null.");

            try
            {
                // Unpack so we can save the result as a brand-new prefab root
                // instead of a variant of the source.
                PrefabUtility.UnpackPrefabInstance(clone, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                int swapped = 0;
                int unmatched = 0;
                foreach (var mf in clone.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (newMeshesByName.TryGetValue(mf.sharedMesh.name, out var newMesh))
                    {
                        mf.sharedMesh = newMesh;
                        swapped++;
                    }
                    else
                    {
                        unmatched++;
                    }
                }
                if (swapped == 0)
                    throw new Exception("No mesh references could be remapped to the new FBX (sub-mesh names must match).");
                if (unmatched > 0)
                    UvtLog.Warn($"[VariantExport] {unmatched} MeshFilter(s) in prefab clone had no matching sub-mesh in '{newFbxPath}' — left pointing at original.");

                PrefabUtility.SaveAsPrefabAsset(clone, outPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        static bool ResolveOutputPaths(
            string sourceFbxPath,
            GameObject sourcePrefab,
            string suffix,
            ConflictPolicy policy,
            out string outFbxPath,
            out string outPrefabPath,
            out string error)
        {
            outFbxPath = null;
            outPrefabPath = null;
            error = null;

            string fbxDir = System.IO.Path.GetDirectoryName(sourceFbxPath)?.Replace('\\', '/');
            string fbxBase = System.IO.Path.GetFileNameWithoutExtension(sourceFbxPath);
            if (string.IsNullOrEmpty(fbxDir) || string.IsNullOrEmpty(fbxBase))
            {
                error = $"Cannot derive output paths from source FBX '{sourceFbxPath}'.";
                return false;
            }

            string prefabDir = null, prefabBase = null;
            if (sourcePrefab != null)
            {
                string prefabSrcPath = AssetDatabase.GetAssetPath(sourcePrefab);
                if (!string.IsNullOrEmpty(prefabSrcPath) &&
                    prefabSrcPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabDir = System.IO.Path.GetDirectoryName(prefabSrcPath)?.Replace('\\', '/');
                    prefabBase = System.IO.Path.GetFileNameWithoutExtension(prefabSrcPath);
                }
            }

            string MakeFbx(string s) => $"{fbxDir}/{fbxBase}_{s}.fbx";
            string MakePrefab(string s) => prefabBase == null ? null : $"{prefabDir}/{prefabBase}_{s}.prefab";

            string finalSuffix = suffix;
            if (policy == ConflictPolicy.Cancel)
            {
                if (System.IO.File.Exists(MakeFbx(finalSuffix)))
                {
                    error = $"Output FBX exists: {MakeFbx(finalSuffix)}";
                    return false;
                }
                var pp = MakePrefab(finalSuffix);
                if (pp != null && System.IO.File.Exists(pp))
                {
                    error = $"Output Prefab exists: {pp}";
                    return false;
                }
            }
            else if (policy == ConflictPolicy.AutoIncrement)
            {
                // Test the current candidate, then bump if it conflicts. The
                // exhaustion check sits BEFORE the bump so the final candidate
                // (`{suffix}_99`) actually gets tested instead of being prepared
                // and then immediately discarded.
                int attempt = 2;
                while (true)
                {
                    bool fbxConflict = System.IO.File.Exists(MakeFbx(finalSuffix));
                    var pp = MakePrefab(finalSuffix);
                    bool prefabConflict = pp != null && System.IO.File.Exists(pp);
                    if (!fbxConflict && !prefabConflict) break;
                    if (attempt >= 100)
                    {
                        error = "Auto-increment exhausted after 99 attempts.";
                        return false;
                    }
                    finalSuffix = $"{suffix}_{attempt}";
                    attempt++;
                }
            }
            // Overwrite: nothing to check; caller accepted clobber.

            outFbxPath = MakeFbx(finalSuffix);
            outPrefabPath = MakePrefab(finalSuffix);
            return true;
        }

        static Result Fail(string suffix, Color color, string error)
        {
            return new Result { ok = false, error = error, suffix = suffix, color = color };
        }
    }
}
