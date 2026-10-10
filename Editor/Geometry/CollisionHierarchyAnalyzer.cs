using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Selected source LOD only. Authored renderer nodes are independent
    /// boundaries, including instances sharing a mesh asset under different parents.</summary>
    internal static class CollisionHierarchyAnalyzer
    {
        internal sealed class Source
        {
            internal MeshEntry entry;
            internal string path;
            internal int depth;
            internal bool visited;
        }

        internal static List<Source> Analyze(Transform root, List<MeshEntry> entries, int lod)
        {
            var selected = new List<Source>();
            var nodes = new Dictionary<Transform, List<Source>>();
            foreach (var entry in entries)
            {
                if (!entry.include || entry.lodIndex != lod) continue;
                var source = new Source { entry = entry };
                selected.Add(source);
                if (entry.renderer == null) continue;
                var node = entry.renderer.transform;
                if (!nodes.TryGetValue(node, out var list)) nodes[node] = list = new List<Source>();
                list.Add(source);
            }
            var result = new List<Source>();
            if (root != null) Visit(root, root.name, 0, nodes, result);
            // Detached sources are unusual but retain the existing API behavior.
            foreach (var source in selected) if (!source.visited) {
                var entry = source.entry;
                source.path = entry.renderer != null ? entry.renderer.name : (entry.fbxMesh != null ? entry.fbxMesh.name : entry.originalMesh?.name);
                result.Add(source);
            }
            return result;
        }

        static void Visit(Transform node, string path, int depth, Dictionary<Transform, List<Source>> nodes, List<Source> output)
        {
            if (nodes.TryGetValue(node, out var sources)) foreach (var source in sources) {
                source.path = path; source.depth = depth; source.visited = true; output.Add(source);
            }
            for (int i = 0; i < node.childCount; i++) {
                var child = node.GetChild(i);
                Visit(child, path + "/" + child.name + "[" + i + "]", depth + 1, nodes, output);
            }
        }
    }
}
