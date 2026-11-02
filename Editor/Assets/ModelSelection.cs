// ModelSelection.cs — the model assets a Mesh Lab menu command acts on: the ones selected in
// the Project window, and the ones the objects selected in a scene (or Prefab Mode) are
// instances of, so a command under Tools ▸ Mesh Lab works on whichever is selected.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ModelSelection
    {
        /// <summary>The distinct asset paths of the selection that <paramref name="accept"/> takes.</summary>
        internal static List<string> Paths(Func<string, bool> accept)
        {
            var assets = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath);
            var instances = Selection.gameObjects.Where(go => go != null && go.scene.IsValid()).Select(SourcePath);
            return assets.Concat(instances)
                .Where(path => !string.IsNullOrEmpty(path) && accept(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        static string SourcePath(GameObject instance)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instance);
            return source != null ? AssetDatabase.GetAssetPath(source) : null;
        }
    }
}
