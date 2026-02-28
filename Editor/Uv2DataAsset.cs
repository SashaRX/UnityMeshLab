// Uv2DataAsset.cs — Sidecar ScriptableObject that stores UV2 data per mesh.
// Lives beside the FBX file as "ModelName_uv2data.asset".
// Read by Uv2AssetPostprocessor during import to inject UV2 into meshes.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    [Serializable]
    public class MeshUv2Entry
    {
        public string meshName;
        public Vector2[] uv2;
    }

    [CreateAssetMenu(menuName = "LightmapUvTool/UV2 Data (internal)", fileName = "uv2data")]
    public class Uv2DataAsset : ScriptableObject
    {
        public List<MeshUv2Entry> entries = new List<MeshUv2Entry>();

        /// <summary>Find entry by mesh name, or null.</summary>
        public MeshUv2Entry Find(string meshName)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].meshName == meshName) return entries[i];
            return null;
        }

        /// <summary>Set UV2 for a mesh name (add or overwrite).</summary>
        public void Set(string meshName, Vector2[] uv2)
        {
            var e = Find(meshName);
            if (e != null)
            {
                e.uv2 = uv2;
            }
            else
            {
                entries.Add(new MeshUv2Entry { meshName = meshName, uv2 = uv2 });
            }
        }

        /// <summary>Remove entry by mesh name. Returns true if found.</summary>
        public bool Remove(string meshName)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].meshName == meshName)
                {
                    entries.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Get the sidecar asset path for a given model asset path.</summary>
        public static string GetSidecarPath(string modelAssetPath)
        {
            // "Assets/Models/MyModel.fbx" → "Assets/Models/MyModel_uv2data.asset"
            string dir = System.IO.Path.GetDirectoryName(modelAssetPath);
            string name = System.IO.Path.GetFileNameWithoutExtension(modelAssetPath);
            return dir + "/" + name + "_uv2data.asset";
        }
    }
}
