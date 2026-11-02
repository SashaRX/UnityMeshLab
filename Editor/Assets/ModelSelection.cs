// ModelSelection.cs — the model assets a Mesh Lab menu command acts on: the ones selected in
// the Project window, and the ones the objects selected in a scene (or Prefab Mode) are
// instances of, so a command under Tools ▸ Mesh Lab works on whichever is selected: the model
// for a command that writes the FBX, the nearest prefab for one that checks what the object is.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ModelSelection
    {
        /// <summary>
        /// The distinct asset paths of the selection that <paramref name="accept"/> takes. A scene
        /// object stands for the asset it is an instance of: with <paramref name="original"/>, the
        /// model at the end of its source chain (the FBX a command writes); without, the nearest
        /// prefab (a variant itself, as the Project window would select it).
        /// </summary>
        internal static List<string> Paths(Func<string, bool> accept, bool original)
        {
            var assets = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath);
            var instances = Selection.gameObjects.Where(go => go != null && go.scene.IsValid())
                .Select(go => original ? OriginalPath(go) : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go));
            return assets.Concat(instances)
                .Where(path => !string.IsNullOrEmpty(path) && accept(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        static string OriginalPath(GameObject instance)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instance);
            return source != null ? AssetDatabase.GetAssetPath(source) : null;
        }
    }
}
