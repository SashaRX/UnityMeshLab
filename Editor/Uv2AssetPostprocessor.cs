// Uv2AssetPostprocessor.cs — Injects UV2 into imported meshes from sidecar data.
// Triggers on every FBX/model import. If a "_uv2data.asset" sidecar exists next
// to the model file, reads it and applies stored UV2 arrays to matching meshes.
// This mirrors Unity's built-in "Generate Lightmap UVs" approach: the FBX stays
// untouched on disk; UV2 lives only in the imported mesh inside Library/.

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
                var entry = data.Find(mesh.name);
                if (entry == null || entry.uv2 == null) continue;

                if (entry.uv2.Length == mesh.vertexCount)
                {
                    mesh.SetUVs(2, entry.uv2);
                    applied++;
                }
                else
                {
                    Debug.LogWarning($"[UV2 Postprocess] '{mesh.name}': vertex count mismatch " +
                                    $"(mesh={mesh.vertexCount}, uv2={entry.uv2.Length}). Skipped.");
                }
            }

            foreach (var smr in skinned)
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                var entry = data.Find(mesh.name);
                if (entry == null || entry.uv2 == null) continue;

                if (entry.uv2.Length == mesh.vertexCount)
                {
                    mesh.SetUVs(2, entry.uv2);
                    applied++;
                }
                else
                {
                    Debug.LogWarning($"[UV2 Postprocess] '{mesh.name}': vertex count mismatch " +
                                    $"(mesh={mesh.vertexCount}, uv2={entry.uv2.Length}). Skipped.");
                }
            }

            if (applied > 0)
                Debug.Log($"[UV2 Postprocess] '{modelPath}': injected UV2 into {applied} mesh(es)");
        }
    }
}
