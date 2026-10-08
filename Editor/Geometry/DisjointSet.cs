using System;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// A union-find over the integers 0..Count-1. The representative of a set is its
    /// smallest member by default, so a labelling is the same whatever order the unions came in,
    /// and every find halves the path it walks. The one union-find for the connectivity
    /// passes (shells, components, welds, normal fans); never write a parent array in a tool.
    /// </summary>
    internal sealed class DisjointSet
    {
        readonly int[] parent;
        readonly int[] rank;

        internal DisjointSet(int count, bool preserveRankRoots = false)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            parent = new int[count];
            if (preserveRankRoots) rank = new int[count];
            for (int i = 0; i < count; ++i) parent[i] = i;
        }

        internal int Count => parent.Length;

        /// <summary>The representative; rank roots are retained only for legacy shell IDs.</summary>
        internal int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        /// <summary>Joins the sets of a and b; false when they were one set already.</summary>
        internal bool Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return false;
            if (rank != null) {
                if (rank[a] < rank[b]) { int swap = a; a = b; b = swap; }
                parent[b] = a;
                if (rank[a] == rank[b]) ++rank[a];
            }
            else if (a < b) parent[b] = a; else parent[a] = b;
            return true;
        }

        internal bool Connected(int a, int b) => Find(a) == Find(b);

        /// <summary>
        /// Makes <paramref name="x"/> a set of its own again. Members whose path ran
        /// through x follow it; the rest of its old set is left as it was.
        /// </summary>
        internal void Detach(int x) => parent[x] = x;

        /// <summary>
        /// A set id per element, 0..count-1 in order of each set's first element, so
        /// the ids are deterministic and dense without a dictionary of roots.
        /// </summary>
        internal int[] Labels(out int count)
        {
            var labels = new int[parent.Length];
            count = 0;
            var rootLabels = rank == null ? null : new System.Collections.Generic.Dictionary<int, int>();
            for (int i = 0; i < parent.Length; ++i) {
                int root = Find(i);
                if (rootLabels == null) labels[i] = root == i ? count++ : labels[root];
                else {
                    if (!rootLabels.TryGetValue(root, out int label)) rootLabels.Add(root, label = count++);
                    labels[i] = label;
                }
            }
            return labels;
        }
    }
}
