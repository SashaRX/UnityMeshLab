// SceneEditRegistry.cs — The scene edits (MeshEntry.sceneEditMesh) by renderer, for the editor session.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Which imported mesh each scene edit (<see cref="MeshEntry.sceneEditMesh"/>) stands for, by
    /// renderer. An edit lives as long as the editor session: it survives a domain reload and the
    /// window's close and reopen, and goes with a scene load or an editor restart. The registry
    /// lives exactly as long, so a context built after a reload or a reopen still rebuilds the
    /// entry with the imported mesh as its file reference instead of taking the edit for the
    /// import. A link outlives its objects: an edit or a renderer destroyed by Undo, by a save or
    /// by Split comes back through Undo as what it was, and an imported mesh a reimport recreated
    /// is found again by its file, local id and name. Links go only when the registry is full,
    /// oldest first, or when their imported mesh is gone for good.
    /// </summary>
    sealed class SceneEditRegistry : ScriptableSingleton<SceneEditRegistry>
    {
        /// <summary>Links the registry keeps; beyond that the oldest go first.</summary>
        internal const int Capacity = 1024;

        [Serializable]
        sealed class Link
        {
            public MeshFilter filter;
            public Mesh imported, edit;
            /// <summary>The imported mesh's file, local id and name: how it is found again after a reimport recreated it.</summary>
            public string assetPath, meshName;
            public long localId;
            /// <summary>The edit is a pipeline working copy an Undo brought back, not a scene edit: a refresh takes it as the working mesh, not the baseline.</summary>
            public bool workingCopy;
        }

        [SerializeField] List<Link> links = new List<Link>();

        /// <summary>
        /// Records that <paramref name="edit"/> on <paramref name="filter"/> stands for the imported
        /// <paramref name="imported"/>; <paramref name="workingCopy"/> marks a pipeline copy rather than a scene edit.
        /// </summary>
        internal static void Register(MeshFilter filter, Mesh imported, Mesh edit, bool workingCopy = false)
        {
            if (filter == null || imported == null || edit == null) return;
            var links = instance.links;
            var link = links.Find(candidate => candidate.filter == filter && candidate.edit == edit);
            if (link == null)
            {
                if (links.Count >= Capacity) links.RemoveAt(0);
                link = new Link { filter = filter, edit = edit };
                links.Add(link);
            }
            link.imported = imported;
            link.workingCopy = workingCopy;
            link.assetPath = AssetDatabase.GetAssetPath(imported);
            link.meshName = imported.name;
            link.localId = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(imported, out string _, out long localId) ? localId : 0;
        }

        /// <summary>The imported mesh behind <paramref name="shown"/>, when it is an edit registered for <paramref name="filter"/>.</summary>
        internal static bool TryGetImported(MeshFilter filter, Mesh shown, out Mesh imported) => TryGetImported(filter, shown, out imported, out _);

        /// <summary>As <see cref="TryGetImported(MeshFilter, Mesh, out Mesh)"/>, telling a pipeline working copy from a scene edit.</summary>
        internal static bool TryGetImported(MeshFilter filter, Mesh shown, out Mesh imported, out bool workingCopy)
        {
            imported = null; workingCopy = false;
            if (filter == null || shown == null) return false;
            var link = instance.links.Find(candidate => candidate.filter == filter && candidate.edit == shown);
            if (link == null) return false;
            imported = Imported(link);
            workingCopy = link.workingCopy;
            return imported != null;
        }

        /// <summary>Takes the edits the entries carry into the registry and drops the links whose imported mesh is gone for good.</summary>
        internal static void Collect(IEnumerable<MeshEntry> entries)
        {
            foreach (var e in entries)
                if (e != null && e.sceneEditMesh != null && e.meshFilter != null && e.fbxMesh != null && e.sceneEditMesh != e.fbxMesh)
                    Register(e.meshFilter, e.fbxMesh, e.sceneEditMesh);
            instance.links.RemoveAll(link => Imported(link) == null);
        }

        // The link's imported mesh, found again by file, local id and name when a reimport recreated it.
        static Mesh Imported(Link link)
        {
            if (link.imported != null) return link.imported;
            if (string.IsNullOrEmpty(link.assetPath)) return null;
            link.imported = FindImported(link.assetPath, link.localId, link.meshName);
            return link.imported;
        }

        /// <summary>
        /// The mesh sub-asset of <paramref name="assetPath"/> with <paramref name="localId"/>, else the one
        /// of that <paramref name="name"/> when the file has exactly one, else null. Importers can
        /// regenerate the ids of a model's meshes; the name then settles it when it is unique in the file.
        /// </summary>
        internal static Mesh FindImported(string assetPath, long localId, string name)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var meshes = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Mesh>().ToArray();
            if (localId != 0)
            {
                var byId = meshes.FirstOrDefault(mesh => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string _, out long id) && id == localId);
                if (byId != null) return byId;
            }
            var named = meshes.Where(mesh => mesh.name == name).ToArray();
            return named.Length == 1 ? named[0] : null;
        }
    }
}
