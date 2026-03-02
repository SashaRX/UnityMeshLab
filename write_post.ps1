$content = @'
// Uv2AssetPostprocessor.cs — Injects UV2 into imported meshes from sidecar data.
// Triggers on every FBX/model import. If a "_uv2data.asset" sidecar exists next
// to the model file, reads it and applies stored UV2 arrays to matching meshes.
//
// Strategy: sidecar stores UV2 + vertex positions + UV0 from the tool's welded mesh.
// The FBX mesh may have MORE vertices (false seams, non-deterministic meshopt).
// We remap UV2 purely by position+UV0 matching — no meshopt/weld in postprocessor.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    public class Uv2AssetPostprocessor : AssetPostprocessor
    {
        void OnPostprocessModel(GameObject root)
        {
            string modelPath = assetPath;
            string sidecarPath = Uv2DataAsset.GetSidecarPath(modelPath);

            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null || data.entries.Count == 0) return;

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int applied = 0;

            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh != null && ApplyEntryToMesh(data, mesh))
                    applied++;
            }

            foreach (var smr in skinned)
            {
                var mesh = smr.sharedMesh;
                if (mesh != null && ApplyEntryToMesh(data, mesh))
                    applied++;
            }

            if (applied > 0)
                UvtLog.Info($"[UV2 Postprocess] '{modelPath}': applied UV2 to {applied} mesh(es)");
        }

        static bool ApplyEntryToMesh(Uv2DataAsset data, Mesh mesh)
        {
            var entry = data.Find(mesh.name);
            if (entry == null || entry.uv2 == null) return false;

            if (entry.vertPositions == null || entry.vertPositions.Length != entry.uv2.Length)
            {
                if (entry.uv2.Length == mesh.vertexCount)
                {
                    mesh.SetUVs(2, entry.uv2);
                    return true;
                }
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': no position data and count mismatch ({mesh.vertexCount} vs {entry.uv2.Length}). Skipped.");
                return false;
            }

            var meshPos = mesh.vertices;
            int meshCount = meshPos.Length;
            int sidecarCount = entry.uv2.Length;

            var posLookup = new Dictionary<long, List<int>>();
            for (int i = 0; i < sidecarCount; i++)
            {
                long key = PackPos(entry.vertPositions[i]);
                if (!posLookup.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    posLookup[key] = list;
                }
                list.Add(i);
            }

            var meshUv0 = new List<Vector2>();
            mesh.GetUVs(0, meshUv0);
            bool hasUv0 = entry.vertUv0 != null &&
                          entry.vertUv0.Length == sidecarCount &&
                          meshUv0.Count == meshCount;

            Vector2[] matchUv0 = null;
            if (hasUv0)
            {
                matchUv0 = meshUv0.ToArray();
                var tris = mesh.triangles;
                List<UvShell> shells; List<List<int>> overlap;
                UvShellExtractor.BuildPerFaceShellIds(matchUv0, tris, out shells, out overlap);
                XatlasRepack.NormalizeShellWinding(matchUv0, tris, shells);
            }

            var result = new Vector2[meshCount];
            int matched = 0;

            for (int i = 0; i < meshCount; i++)
            {
                long key = PackPos(meshPos[i]);
                if (!posLookup.TryGetValue(key, out var candidates))
                    continue;

                if (candidates.Count == 1)
                {
                    result[i] = entry.uv2[candidates[0]];
                    matched++;
                }
                else if (hasUv0)
                {
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    var muv = matchUv0[i];
                    foreach (int ci in candidates)
                    {
                        float d = SqrDist2(muv, entry.vertUv0[ci]);
                        if (d < bestDist) { bestDist = d; bestIdx = ci; }
                    }
                    if (bestIdx >= 0)
                    {
                        result[i] = entry.uv2[bestIdx];
                        matched++;
                    }
                }
                else
                {
                    result[i] = entry.uv2[candidates[0]];
                    matched++;
                }
            }

            if (matched < meshCount)
            {
                int fallback = 0;
                for (int i = 0; i < meshCount; i++)
                {
                    long key = PackPos(meshPos[i]);
                    if (posLookup.ContainsKey(key)) continue;

                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    for (int j = 0; j < sidecarCount; j++)
                    {
                        float d = SqrDist3(meshPos[i], entry.vertPositions[j]);
                        if (d < bestDist) { bestDist = d; bestIdx = j; }
                    }
                    if (bestIdx >= 0 && bestDist < 1e-4f)
                    {
                        result[i] = entry.uv2[bestIdx];
                        matched++;
                        fallback++;
                    }
                }
                if (fallback > 0)
                    UvtLog.Info($"[UV2 Postprocess] '{mesh.name}': {fallback} vertices matched by nearest-neighbor fallback");
            }

            if (matched < meshCount)
                UvtLog.Warn($"[UV2 Postprocess] '{mesh.name}': {matched}/{meshCount} vertices matched ({meshCount - matched} unmatched)");

            mesh.SetUVs(2, result);
            return true;
        }

        static long PackPos(Vector3 p)
        {
            long x = Mathf.RoundToInt(p.x * 10000f);
            long y = Mathf.RoundToInt(p.y * 10000f);
            long z = Mathf.RoundToInt(p.z * 10000f);
            return (x & 0x1FFFFF) | ((y & 0x1FFFFF) << 21) | ((z & 0x1FFFFF) << 42);
        }

        static float SqrDist2(Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        static float SqrDist3(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
'@
[System.IO.File]::WriteAllText('D:\sourceProject\repos\lightmap-uv-tool\Editor\Uv2AssetPostprocessor.cs', $content, [System.Text.UTF8Encoding]::new($false))
