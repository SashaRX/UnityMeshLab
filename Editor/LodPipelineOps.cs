// LodPipelineOps.cs — Shared LOD generation helper used by LodGenerationTool
// and PrefabBuilderTool's Build Pipeline. Encapsulates the meshoptimizer-driven
// simplification loop, prefab unpacking, LODGroup rebuild, scaleInLightmap
// propagation, and MeshEntry registration so both tools run identical logic.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    internal static class LodPipelineOps
    {
        static readonly System.Text.RegularExpressions.Regex LodSuffixRegex =
            new System.Text.RegularExpressions.Regex(
                @"(_wc|_repack|_uvTransfer|_optimized|_LOD\d+)+$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        static readonly System.Text.RegularExpressions.Regex TrailingLodRegex =
            new System.Text.RegularExpressions.Regex(
                @"[_\-\s]+LOD\d+$",
                System.Text.RegularExpressions.RegexOptions.Compiled |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal struct Options
        {
            public int count;
            public float[] ratios;
            public float targetError;
            public float uv2Weight;
            public float normalWeight;
            public bool lockBorder;
            public bool progressiveScaleInLightmap;
        }

        internal struct LodInfo
        {
            public string meshName;
            public int simplifiedTris;
            public int lodLevel;
            public float targetRatio;
            public float actualRatio;
            public bool hitErrorLimit;
        }

        internal class Result
        {
            public bool ok;
            public string error;
            public List<LodInfo> perLod = new List<LodInfo>();
            public List<GameObject> generatedObjects = new List<GameObject>();
        }

        internal static Result Generate(UvToolContext ctx, int startLod, Options opts)
        {
            var result = new Result();
            if (ctx?.LodGroup == null) { result.error = "No LODGroup"; return result; }
            if (opts.ratios == null || opts.count <= 0) { result.error = "No ratios"; return result; }

            var lgGo = ctx.LodGroup.gameObject;
            if (PrefabUtility.IsPartOfPrefabInstance(lgGo))
            {
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(lgGo);
                if (outer != null)
                {
                    UvtLog.Info($"[LodPipelineOps] Unpacking prefab instance '{outer.name}' before LOD regeneration.");
                    PrefabUtility.UnpackPrefabInstance(outer, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }
            }

            var sourceMeshes = new List<(MeshEntry entry, Mesh mesh)>();
            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include || e.lodIndex != ctx.SourceLodIndex) continue;
                Mesh src = e.repackedMesh ?? e.originalMesh;
                if (src != null) sourceMeshes.Add((e, src));
            }
            if (sourceMeshes.Count == 0) { result.error = "No source meshes found"; return result; }

            UvToolContext.CompactLodArray(ctx.LodGroup, removeEmptySlots: true);
            var lods = ctx.LodGroup.GetLODs();
            var newLods = new List<LOD>(lods);

            try
            {
                for (int lodIdx = 0; lodIdx < opts.count; lodIdx++)
                {
                    float ratio = opts.ratios[lodIdx];
                    var settings = new MeshSimplifier.SimplifySettings
                    {
                        targetRatio  = ratio,
                        targetError  = opts.targetError,
                        uv2Weight    = opts.uv2Weight,
                        normalWeight = opts.normalWeight,
                        lockBorder   = opts.lockBorder,
                        uvChannel    = 1
                    };

                    float progress = (float)lodIdx / opts.count;
                    EditorUtility.DisplayProgressBar("Generate LODs",
                        $"LOD{startLod + lodIdx} (ratio {ratio:P0})", progress);

                    var lodRenderers = new List<Renderer>();
                    int lodLevel = startLod + lodIdx;

                    var parentToContainer = new Dictionary<Transform, Transform>();

                    foreach (var (entry, srcMesh) in sourceMeshes)
                    {
                        var r = MeshSimplifier.Simplify(srcMesh, settings);
                        if (!r.ok) { UvtLog.Error($"[LodPipelineOps] Failed on {srcMesh.name}: {r.error}"); continue; }

                        int sourceTriCount = TriCount(srcMesh);
                        float actualRatio = sourceTriCount > 0
                            ? (float)r.simplifiedTriCount / sourceTriCount : 1f;
                        bool hitLimit = actualRatio > ratio * 1.2f;
                        if (hitLimit)
                            UvtLog.Warn($"[LodPipelineOps] LOD{lodLevel}: target {ratio:P0} but got {actualRatio:P0} — increase Target Error");

                        string baseName = entry.fbxMesh != null ? entry.fbxMesh.name : srcMesh.name;
                        baseName = LodSuffixRegex.Replace(baseName, "");
                        string meshName = baseName + "_LOD" + lodLevel;
                        r.simplifiedMesh.name = meshName;

                        UvtLog.Info($"[LodPipelineOps] {meshName}: {r.originalTriCount} → {r.simplifiedTriCount} tris ({actualRatio:P0})");

                        result.perLod.Add(new LodInfo
                        {
                            meshName = meshName,
                            simplifiedTris = r.simplifiedTriCount,
                            lodLevel = lodLevel,
                            targetRatio = ratio,
                            actualRatio = actualRatio,
                            hitErrorLimit = hitLimit
                        });

                        if (entry.renderer != null)
                        {
                            var go = new GameObject(meshName);

                            Transform srcParent = entry.renderer.transform.parent;
                            Transform lodGroupTransform = ctx.LodGroup.transform;

                            if (srcParent != lodGroupTransform && srcParent != null)
                            {
                                if (parentToContainer.TryGetValue(srcParent, out var container))
                                    go.transform.SetParent(container, false);
                                else
                                    go.transform.SetParent(lodGroupTransform, false);
                            }
                            else
                            {
                                go.transform.SetParent(lodGroupTransform, false);
                            }

                            parentToContainer[entry.renderer.transform] = go.transform;
                            go.transform.localPosition = entry.renderer.transform.localPosition;
                            go.transform.localRotation = entry.renderer.transform.localRotation;
                            go.transform.localScale    = entry.renderer.transform.localScale;
                            var mf = go.AddComponent<MeshFilter>();
                            mf.sharedMesh = r.simplifiedMesh;
                            var mr = go.AddComponent<MeshRenderer>();
                            LightmapTransferTool.CopyRendererSettings(entry.renderer, mr);

                            if (opts.progressiveScaleInLightmap && lodLevel > 0)
                            {
                                Undo.RecordObject(mr, "Set scaleInLightmap");
                                mr.scaleInLightmap = Mathf.Pow(0.5f, lodLevel);
                            }

                            GameObjectUtility.SetStaticEditorFlags(go,
                                GameObjectUtility.GetStaticEditorFlags(entry.renderer.gameObject));
                            Undo.RegisterCreatedObjectUndo(go, "Generate LOD");
                            result.generatedObjects.Add(go);
                            lodRenderers.Add(mr);
                        }
                    }

                    if (lodRenderers.Count > 0)
                    {
                        if (lodLevel < newLods.Count)
                        {
                            var oldRenderers = newLods[lodLevel].renderers;
                            if (oldRenderers != null)
                                foreach (var oldR in oldRenderers)
                                    if (oldR != null && oldR.gameObject != null)
                                        Undo.DestroyObjectImmediate(oldR.gameObject);
                            newLods[lodLevel] = new LOD(newLods[lodLevel].screenRelativeTransitionHeight, lodRenderers.ToArray());
                        }
                        else
                        {
                            float baseHeight = newLods.Count > 0 ? newLods[newLods.Count - 1].screenRelativeTransitionHeight : 0.5f;
                            newLods.Add(new LOD(baseHeight * 0.5f, lodRenderers.ToArray()));
                        }
                    }
                }

                ctx.LodGroup = LodGroupUtility.Rebuild(ctx.LodGroup.gameObject, newLods.ToArray());
                AssetDatabase.SaveAssets();
            }
            finally { EditorUtility.ClearProgressBar(); }

            foreach (var (entry, srcMesh) in sourceMeshes)
            {
                if (entry.renderer == null) continue;
                if (!TrailingLodRegex.IsMatch(entry.renderer.name))
                {
                    Undo.RecordObject(entry.renderer.gameObject, "Rename LOD0");
                    string newName = entry.renderer.gameObject.name + "_LOD0";
                    UvtLog.Info($"[LodPipelineOps] Renamed source: {entry.renderer.gameObject.name} → {newName}");
                    entry.renderer.gameObject.name = newName;
                }
            }

            RegisterNewLodEntries(ctx);
            ctx.ClearAllCaches();
            result.ok = true;
            return result;
        }

        static void RegisterNewLodEntries(UvToolContext ctx)
        {
            var currentLods = ctx.LodGroup.GetLODs();
            for (int li = 0; li < currentLods.Length; li++)
            {
                bool alreadyRegistered = false;
                foreach (var e in ctx.MeshEntries)
                    if (e.lodIndex == li) { alreadyRegistered = true; break; }
                if (alreadyRegistered) continue;

                if (currentLods[li].renderers == null) continue;
                foreach (var r in currentLods[li].renderers)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var fbm = mf.sharedMesh;
                    var uv2Check = new List<Vector2>();
                    fbm.GetUVs(1, uv2Check);
                    ctx.MeshEntries.Add(new MeshEntry
                    {
                        lodIndex = li,
                        renderer = r,
                        meshFilter = mf,
                        originalMesh = fbm,
                        fbxMesh = fbm,
                        hasExistingUv2 = uv2Check.Count > 0,
                        meshGroupKey = UvToolContext.ExtractGroupKey(r.name)
                    });
                }
            }
        }

        static int TriCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long indexCount = 0;
            int subMeshCount = mesh.subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
                indexCount += mesh.GetIndexCount(i);
            return (int)(indexCount / 3L);
        }
    }
}
