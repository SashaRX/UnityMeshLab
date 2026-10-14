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
    /// is found again by its file, local id and name. A renderer the registry does not know that
    /// shows an edit it does (the object was duplicated) is registered with that edit's import.
    /// Links go when their imported mesh is gone for good; beyond the capacity, the oldest links
    /// whose edit is gone go first, and a link to a living edit is never dropped.
    /// </summary>
    sealed class SceneEditRegistry : ScriptableSingleton<SceneEditRegistry>
    {
        /// <summary>
        /// Links the registry keeps before it evicts: beyond that, a new link takes the place of the
        /// oldest one whose edit is gone (destroyed, waiting for an Undo that may never come). Links to
        /// living edits are never evicted, so the registry grows past the capacity while they fill it.
        /// </summary>
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
            /// <summary>For a working copy: the scene edit it was taken from, which stays the baseline; null when it was taken from the import.</summary>
            public Mesh baseline;
        }

        [SerializeField] List<Link> links = new List<Link>();

        /// <summary>How many links the registry holds.</summary>
        internal static int Count => instance.links.Count;

        /// <summary>
        /// Records that <paramref name="edit"/> on <paramref name="filter"/> stands for the imported
        /// <paramref name="imported"/>; <paramref name="workingCopy"/> marks a pipeline copy rather than a
        /// scene edit, taken from the scene edit <paramref name="baseline"/> when there was one.
        /// </summary>
        internal static void Register(MeshFilter filter, Mesh imported, Mesh edit, bool workingCopy = false, Mesh baseline = null)
        {
            if (filter == null || imported == null || edit == null) return;
            var links = instance.links;
            var link = links.Find(candidate => candidate.filter == filter && candidate.edit == edit);
            if (link == null)
            {
                if (links.Count >= Capacity) Evict(links);
                link = new Link { filter = filter, edit = edit };
                links.Add(link);
            }
            link.imported = imported;
            link.workingCopy = workingCopy;
            link.baseline = workingCopy ? baseline : null;
            link.assetPath = AssetDatabase.GetAssetPath(imported);
            link.meshName = imported.name;
            link.localId = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(imported, out string _, out long localId) ? localId : 0;
        }

        /// <summary>The imported mesh behind <paramref name="shown"/>, when it is an edit registered for <paramref name="filter"/>.</summary>
        internal static bool TryGetImported(MeshFilter filter, Mesh shown, out Mesh imported) => TryGetImported(filter, shown, out imported, out _);

        /// <summary>As <see cref="TryGetImported(MeshFilter, Mesh, out Mesh)"/>, telling a pipeline working copy from a scene edit.</summary>
        internal static bool TryGetImported(MeshFilter filter, Mesh shown, out Mesh imported, out bool workingCopy)
            => TryGetImported(filter, shown, out imported, out workingCopy, out _);

        /// <summary>
        /// As <see cref="TryGetImported(MeshFilter, Mesh, out Mesh)"/>, telling a pipeline working copy from a
        /// scene edit and giving the scene edit a working copy was taken from (null for one taken from the import, or gone).
        /// </summary>
        internal static bool TryGetImported(MeshFilter filter, Mesh shown, out Mesh imported, out bool workingCopy, out Mesh baseline)
        {
            imported = null; workingCopy = false; baseline = null;
            if (filter == null || shown == null) return false;
            var link = instance.links.Find(candidate => candidate.filter == filter && candidate.edit == shown) ?? Adopt(filter, shown);
            if (link == null) return false;
            imported = Imported(link);
            workingCopy = link.workingCopy;
            baseline = workingCopy && link.baseline != null ? link.baseline : null;
            return imported != null;
        }

        /// <summary>
        /// Whether <paramref name="shown"/> stays on <paramref name="filter"/> through a relink to a
        /// reimported file: a scene edit, or a pipeline working copy taken from a living scene edit
        /// (its release returns the renderer to that edit). A working copy of the import itself is
        /// relinked by name like any copy.
        /// </summary>
        internal static bool KeepsRenderer(MeshFilter filter, Mesh shown)
            => TryGetImported(filter, shown, out _, out bool workingCopy, out var baseline) && (!workingCopy || baseline != null);

        /// <summary>
        /// The baseline a pipeline working copy on <paramref name="entry"/>'s renderer (an Undo
        /// brought it back after Cleanup promoted it) is released to when the entry's own is gone
        /// with the import a save recreated: the living scene edit the copy was taken from, else the
        /// imported mesh found again. The entry takes them as its scene edit and file reference.
        /// Null when the renderer shows anything else.
        /// </summary>
        internal static Mesh BaselineBehindWorkingCopy(MeshEntry entry)
        {
            if (entry?.meshFilter == null) return null;
            var shown = entry.meshFilter.sharedMesh;
            if (shown == null || !TryGetImported(entry.meshFilter, shown, out var imported, out bool workingCopy, out var baseline) || !workingCopy) return null;
            entry.fbxMesh = imported;
            if (baseline != null) entry.sceneEditMesh = baseline;
            return baseline != null ? baseline : imported;
        }

        /// <summary>
        /// Takes the edits the entries' renderers show into the registry and drops the links whose
        /// imported mesh is gone for good. An edit a renderer no longer shows (Merge replaced it) keeps
        /// the provenance it had, for the Undo that brings it back.
        /// </summary>
        internal static void Collect(IEnumerable<MeshEntry> entries)
        {
            foreach (var e in entries)
                if (e != null && e.sceneEditMesh != null && e.meshFilter != null && e.fbxMesh != null && e.sceneEditMesh != e.fbxMesh
                    && e.meshFilter.sharedMesh == e.sceneEditMesh)
                    Register(e.meshFilter, e.fbxMesh, e.sceneEditMesh);
            instance.links.RemoveAll(link => Imported(link) == null);
        }

        // A renderer the registry does not know showing an edit it does (the object was duplicated
        // with the edit on it): the edit stands for the same import there, and the renderer is
        // registered with it when every link of the edit names that import. A working copy is not
        // adopted: it belongs to the pipeline of the renderer it was made for.
        static Link Adopt(MeshFilter filter, Mesh shown)
        {
            var links = instance.links;
            Link template = null;
            foreach (var link in links)
            {
                if (link.edit != shown || link.workingCopy) continue;
                if (template == null) { template = link; continue; }
                if (link.assetPath != template.assetPath || link.localId != template.localId || link.meshName != template.meshName) return null;
            }
            if (template == null) return null;
            if (links.Count >= Capacity) Evict(links);
            var adopted = new Link { filter = filter, edit = shown, imported = template.imported,
                assetPath = template.assetPath, meshName = template.meshName, localId = template.localId };
            links.Add(adopted);
            return adopted;
        }

        // Drops the oldest link whose edit is gone. A living edit is what the next refresh asks about,
        // so the registry grows instead when every link has one.
        static void Evict(List<Link> links)
        {
            int index = links.FindIndex(link => link.edit == null);
            if (index >= 0) links.RemoveAt(index);
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
