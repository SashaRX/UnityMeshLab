// SubmeshTopology.cs — writes an edit of a mesh's triangulated indices back into its
// submeshes at each submesh's own topology.

using System;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The submeshes of a mesh as weld and symmetry-split passes edit them:
    /// <see cref="Triangles"/> is every submesh's <see cref="Mesh.GetTriangles(int)"/>
    /// concatenated, so a Quads submesh contributes six indices per quad.
    /// <see cref="Apply"/> writes an edited copy of that array back per submesh:
    /// a Quads submesh keeps its quads while both triangles of every quad still
    /// share the quad's corners, and becomes triangles only when an edit separated
    /// them. Slicing the triangulated array by the native index count (four per
    /// quad) instead shifts every later face onto its neighbours' corners.
    /// </summary>
    internal sealed class SubmeshTopology
    {
        readonly MeshTopology[] topologies;
        readonly int[][] native, split;

        /// <summary>Every submesh's triangles, concatenated in submesh order.</summary>
        internal readonly int[] Triangles;

        SubmeshTopology(MeshTopology[] topologies, int[][] native, int[][] split)
        {
            this.topologies = topologies; this.native = native; this.split = split;
            int length = 0;
            foreach (var triangles in split) length += triangles.Length;
            Triangles = new int[length];
            for (int s = 0, offset = 0; s < split.Length; offset += split[s].Length, ++s)
                Array.Copy(split[s], 0, Triangles, offset, split[s].Length);
        }

        /// <summary>
        /// Null when a submesh is neither triangles nor quads: lines and points have no
        /// triangles to edit, and their indices would not survive a vertex remap.
        /// </summary>
        internal static SubmeshTopology Capture(Mesh mesh)
        {
            int count = mesh.subMeshCount;
            var topologies = new MeshTopology[count];
            var native = new int[count][]; var split = new int[count][];
            for (int s = 0; s < count; ++s)
            {
                topologies[s] = mesh.GetTopology(s);
                if (topologies[s] != MeshTopology.Triangles && topologies[s] != MeshTopology.Quads) return null;
                native[s] = mesh.GetIndices(s);
                split[s] = mesh.GetTriangles(s);
            }
            return new SubmeshTopology(topologies, native, split);
        }

        /// <summary>
        /// Sets <paramref name="target"/>'s submeshes from <paramref name="edited"/>, an
        /// edit of <see cref="Triangles"/> with the same layout (vertex indices may change).
        /// </summary>
        internal void Apply(Mesh target, int[] edited)
        {
            if (edited.Length != Triangles.Length)
                throw new ArgumentException($"Edited triangles have {edited.Length} indices; the captured submeshes have {Triangles.Length}.");
            target.subMeshCount = topologies.Length;
            for (int s = 0, offset = 0; s < topologies.Length; offset += split[s].Length, ++s)
            {
                var triangles = new int[split[s].Length];
                Array.Copy(edited, offset, triangles, 0, triangles.Length);
                var quads = topologies[s] == MeshTopology.Quads ? Quads(s, triangles) : null;
                if (quads != null) target.SetIndices(quads, MeshTopology.Quads, s);
                else target.SetTriangles(triangles, s);
            }
        }

        /// <summary>The edited quads, or null when an edit gave the two triangles of a quad different corners.</summary>
        int[] Quads(int s, int[] edited)
        {
            var quads = native[s]; var triangles = split[s];
            if (quads.Length % 4 != 0 || triangles.Length != quads.Length / 4 * 6) return null;
            var result = new int[quads.Length];
            for (int q = 0; q < quads.Length / 4; ++q)
            {
                int start = 4 * q;
                for (int j = 0; j < 4; ++j) result[start + j] = -1;
                for (int c = 0; c < 6; ++c)
                {
                    int j = Array.IndexOf(quads, triangles[6 * q + c], start, 4) - start;
                    if (j < 0) return null;
                    int value = edited[6 * q + c];
                    if (result[start + j] < 0) result[start + j] = value;
                    else if (result[start + j] != value) return null;
                }
                // A corner repeating another corner's vertex takes that corner's edit.
                for (int j = 0; j < 4; ++j)
                    if (result[start + j] < 0)
                    {
                        int k = Array.IndexOf(quads, quads[start + j], start, 4) - start;
                        if (k == j || result[start + k] < 0) return null;
                        result[start + j] = result[start + k];
                    }
            }
            return result;
        }
    }
}
