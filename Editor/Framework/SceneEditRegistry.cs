// SceneEditRegistry.cs — The scene edits (MeshEntry.sceneEditMesh) by renderer, for the editor session.

using System;
using System.Collections.Generic;
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
    /// import. A renderer that is gone takes its link with it; an edit destroyed by Undo keeps
    /// its link for Redo.
    /// </summary>
    sealed class SceneEditRegistry : ScriptableSingleton<SceneEditRegistry>
    {
        [Serializable]
        sealed class Link
        {
            public MeshFilter filter;
            public Mesh imported, edit;
        }

        [SerializeField] List<Link> links = new List<Link>();

        /// <summary>Records that <paramref name="edit"/> on <paramref name="filter"/> replaces the imported <paramref name="imported"/>.</summary>
        internal static void Register(MeshFilter filter, Mesh imported, Mesh edit)
        {
            if (filter == null || imported == null || edit == null) return;
            var links = instance.links;
            int at = links.FindIndex(link => link.filter == filter);
            if (at < 0) links.Add(new Link { filter = filter, imported = imported, edit = edit });
            else { links[at].imported = imported; links[at].edit = edit; }
        }

        /// <summary>The imported mesh behind <paramref name="shown"/>, when it is the edit registered for <paramref name="filter"/>.</summary>
        internal static bool TryGetImported(MeshFilter filter, Mesh shown, out Mesh imported)
        {
            imported = null;
            if (filter == null || shown == null) return false;
            var link = instance.links.Find(candidate => candidate.filter == filter);
            if (link == null || link.edit == null || link.edit != shown || link.imported == null) return false;
            imported = link.imported;
            return true;
        }

        /// <summary>Takes the edits the entries carry into the registry and drops the links whose renderer or imported mesh is gone.</summary>
        internal static void Collect(IEnumerable<MeshEntry> entries)
        {
            foreach (var e in entries)
                if (e != null && e.sceneEditMesh != null && e.meshFilter != null && e.fbxMesh != null && e.sceneEditMesh != e.fbxMesh)
                    Register(e.meshFilter, e.fbxMesh, e.sceneEditMesh);
            instance.links.RemoveAll(link => link.filter == null || link.imported == null);
        }

        /// <summary>Drops the link of <paramref name="filter"/>: its edit was written into the file or given up.</summary>
        internal static void Forget(MeshFilter filter)
        {
            if (filter == null) return;
            instance.links.RemoveAll(link => link.filter == filter);
        }
    }
}
