// Uv2AssetPostprocessor.cs — Injects UV2 into imported meshes from sidecar data.
// Triggers on every FBX/model import. If a "_uv2data.asset" sidecar exists next
// to the model file, reads it and applies stored UV2 arrays to matching meshes.
// This mirrors Unity's built-in "Generate Lightmap UVs" approach: the FBX stays
// untouched on disk; UV2 lives only in the imported mesh inside Library/.

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

            // Load sidecar without triggering import loop
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null || data.entries.Count == 0) return;

            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int applied = 0;

            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                if (ApplyEntryToMesh(data, mesh)) applied++;
            }

            foreach (var smr in skinned)
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                if (ApplyEntryToMesh(data, mesh)) applied++;
            }

            if (applied > 0)
                Debug.Log($"[UV2 Postprocess] '{modelPath}': injected UV2 into {applied} mesh(es)");
        }

        /// <summary>Apply weld (if flagged) + UV2 to a single mesh. Returns true on success.</summary>
        static bool ApplyEntryToMesh(Uv2DataAsset data, Mesh mesh)
        {
            var entry = data.Find(mesh.name);
            if (entry == null || entry.uv2 == null) return false;

            // Phase 1: meshopt full pipeline (dedup + vertex cache + overdraw + fetch reorder)
            // Must match the tool's MeshOptimizer.Optimize exactly, not just WeldInPlace,
            // because meshopt_optimizeVertexFetch reorders vertices and UV2 indices depend on that order.
            if (entry.welded)
            {
                MeshOptimizer.Optimize(mesh);
            }

            // Phase 2: UV edge weld if flagged
            if (entry.edgeWelded)
            {
                var welded = Uv0Analyzer.UvEdgeWeld(mesh);
                if (welded != null && welded != mesh)
                {
                    // Copy welded data back into the mesh object
                    mesh.Clear();
                    mesh.vertices = welded.vertices;
                    mesh.normals = welded.normals;
                    mesh.tangents = welded.tangents;
                    mesh.colors = welded.colors;
                    for (int ch = 0; ch < 8; ch++)
                    {
                        var uvs = new List<Vector2>();
                        welded.GetUVs(ch, uvs);
                        if (uvs.Count > 0) mesh.SetUVs(ch, uvs);
                    }
                    mesh.boneWeights = welded.boneWeights;
                    mesh.bindposes = welded.bindposes;
                    mesh.subMeshCount = welded.subMeshCount;
                    for (int s = 0; s < welded.subMeshCount; s++)
                        mesh.SetTriangles(welded.GetTriangles(s), s);
                    mesh.RecalculateBounds();
                }
            }

            if (entry.uv2.Length != mesh.vertexCount)
            {
                Debug.LogWarning($"[UV2 Postprocess] '{mesh.name}': vertex count mismatch " +
                                $"(mesh={mesh.vertexCount}, uv2={entry.uv2.Length}). Skipped.");
                return false;
            }

            // Apply UV2 with position-based remapping if vertex order differs
            var uv2 = RemapUv2IfNeeded(entry, mesh);
            mesh.SetUVs(2, uv2);
            return true;
        }

        /// <summary>
        /// Returns the UV2 array, remapped to match the mesh's vertex order if needed.
        /// MeshOptimizer and UvEdgeWeld may reorder vertices differently between the editor
        /// workflow and the postprocessor (different starting vertex count/order from FBX reimport).
        /// This remaps UV2 by matching vertex positions so the correct UV2 reaches each vertex.
        /// </summary>
        static Vector2[] RemapUv2IfNeeded(MeshUv2Entry entry, Mesh mesh)
        {
            // No position data stored — backward compat, use UV2 as-is
            if (entry.vertPositions == null || entry.vertPositions.Length != entry.uv2.Length)
                return entry.uv2;

            var meshPos = mesh.vertices;
            int count = meshPos.Length;

            // Quick check: sample positions to detect reordering
            bool inOrder = true;
            int checkCount = Mathf.Min(count, 20);
            for (int i = 0; i < checkCount; i++)
            {
                if (Vector3.SqrMagnitude(meshPos[i] - entry.vertPositions[i]) > 1e-8f)
                {
                    inOrder = false;
                    break;
                }
            }

            if (inOrder)
                return entry.uv2;

            // Vertex order differs — remap UV2 by matching positions
            var meshUv0 = new List<Vector2>();
            mesh.GetUVs(0, meshUv0);
            bool hasUv0 = entry.vertUv0 != null &&
                          entry.vertUv0.Length == entry.uv2.Length &&
                          meshUv0.Count == count;

            // Build position → candidate sidecar indices lookup
            var posLookup = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < entry.vertPositions.Length; i++)
            {
                var key = QuantizePos(entry.vertPositions[i]);
                if (!posLookup.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    posLookup[key] = list;
                }
                list.Add(i);
            }

            var result = new Vector2[count];
            var used = new bool[entry.uv2.Length];
            int matched = 0;

            for (int i = 0; i < count; i++)
            {
                var key = QuantizePos(meshPos[i]);
                if (!posLookup.TryGetValue(key, out var candidates))
                {
                    // No match at this position — leave UV2 as zero
                    continue;
                }

                if (candidates.Count == 1)
                {
                    result[i] = entry.uv2[candidates[0]];
                    used[candidates[0]] = true;
                    matched++;
                }
                else if (hasUv0)
                {
                    // Multiple vertices at same position — disambiguate by closest UV0
                    float bestDist = float.MaxValue;
                    int bestIdx = -1;
                    foreach (int ci in candidates)
                    {
                        if (used[ci]) continue;
                        float d = Vector2.SqrMagnitude(meshUv0[i] - entry.vertUv0[ci]);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestIdx = ci;
                        }
                    }
                    if (bestIdx >= 0)
                    {
                        result[i] = entry.uv2[bestIdx];
                        used[bestIdx] = true;
                        matched++;
                    }
                }
                else
                {
                    // No UV0 data — pick first unused candidate
                    foreach (int ci in candidates)
                    {
                        if (!used[ci])
                        {
                            result[i] = entry.uv2[ci];
                            used[ci] = true;
                            matched++;
                            break;
                        }
                    }
                }
            }

            if (matched < count)
                Debug.LogWarning($"[UV2 Postprocess] '{mesh.name}': position remap {matched}/{count} " +
                                 $"(unmatched vertices will have zero UV2)");
            else
                Debug.Log($"[UV2 Postprocess] '{mesh.name}': remapped {count} vertices by position");

            return result;
        }

        static (int, int, int) QuantizePos(Vector3 pos)
        {
            return (
                Mathf.RoundToInt(pos.x * 10000f),
                Mathf.RoundToInt(pos.y * 10000f),
                Mathf.RoundToInt(pos.z * 10000f)
            );
        }
    }
}
