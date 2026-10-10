using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Selected source LOD only. Authored renderer nodes are independent
    /// boundaries, including instances sharing a mesh asset under different parents.</summary>
    internal static class CollisionHierarchyAnalyzer
    {
        // Root-relative, escaped names with ordinals among same-named siblings.
        // Unrelated collision/LOD nodes added by an export do not shift these ordinals.
        internal static string RelativePath(Transform root, Transform node)
        {
            if (root == null || node == null) return null;
            var parts = new List<string>();
            while (node != null && node != root)
            {
                int ordinal = 0;
                if (node.parent != null)
                    for (int i = 0; i < node.GetSiblingIndex(); i++)
                        if (node.parent.GetChild(i).name == node.name) ordinal++;
                parts.Add(PathPart(node.name, ordinal));
                node = node.parent;
            }
            if (node != root) return null;
            parts.Reverse();
            return parts.Count == 0 ? "." : string.Join("/", parts);
        }

        internal static string PathPart(string name, int ordinal)
            => $"{System.Uri.EscapeDataString(name)}[{ordinal}]";

        internal static string InstanceKey(string meshName, string path)
        {
            if (string.IsNullOrEmpty(path)) return meshName;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path));
                return meshName + "_Instance" + System.BitConverter.ToString(bytes, 0, 8).Replace("-", "");
            }
        }

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
