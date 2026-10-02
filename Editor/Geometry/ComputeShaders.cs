using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Finds this package's compute shaders by name wherever the package is installed.</summary>
    internal static class ComputeShaders
    {
        public static ComputeShader Find(string name)
        {
            // 1. Editor default resources (user may have copied it there).
            var cs = (ComputeShader)EditorGUIUtility.Load(name + ".compute");
            if (cs != null) return cs;
            // 2. AssetDatabase search by type + name.
            foreach (var guid in AssetDatabase.FindAssets($"t:ComputeShader {name}"))
            {
                cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guid));
                if (cs != null) return cs;
            }
            // 3. Relative to this package's Editor folder (UPM packages outside Assets).
            foreach (var guid in AssetDatabase.FindAssets("t:Script MeshGeometry"))
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                string geometryDir = System.IO.Path.GetDirectoryName(scriptPath);
                string editorDir = System.IO.Path.GetDirectoryName(geometryDir);
                string packageRoot = System.IO.Path.GetDirectoryName(editorDir);
                cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(packageRoot + "/Shaders/" + name + ".compute");
                if (cs != null) return cs;
            }
            return null;
        }
    }
}
