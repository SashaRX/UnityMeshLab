// FbxUvSetNames.cs — the names of each mesh's UV sets, and whether it has smoothing
// groups, read from the FBX file itself. The FBX SDK's C# wrapper exposes neither a
// layer element's name nor a smoothing element: a material's texture finds its UV set
// by that name, so a mesh MeshLab rebuilds must carry the names its source had, and a
// new normal element's layout follows the smoothing (see WriteNormals). Reads FBX 7.x
// binary (32- and 64-bit records) and ASCII.
//
// Plain bytes in, names out: no UnityEngine and no FBX SDK types.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SashaRX.UnityMeshLab
{
    internal static class FbxUvSetNames
    {
        const string BinaryMagic = "Kaydara FBX Binary";

        /// <summary>
        /// UV set names by mesh (geometry) name, in element order. A mesh whose name another
        /// mesh shares is left out: its names cannot be told apart.
        /// </summary>
        internal static Dictionary<string, List<string>> Read(string path) => ReadLayers(path).uvSetNames;

        /// <summary>
        /// The UV set names (as <see cref="Read"/>) and whether each mesh has a smoothing
        /// element, by mesh name; a shared name is in neither.
        /// </summary>
        internal static (Dictionary<string, List<string>> uvSetNames, Dictionary<string, bool> smoothing) ReadLayers(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var found = new Found();
            if (bytes.Length > 27 && Encoding.ASCII.GetString(bytes, 0, BinaryMagic.Length) == BinaryMagic)
                ReadBinary(bytes, found);
            else
                ReadAscii(Encoding.UTF8.GetString(bytes), found);
            return (found.Result(), found.Smoothing());
        }

        sealed class Found
        {
            readonly Dictionary<string, SortedDictionary<int, string>> names = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
            readonly HashSet<string> shared = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<SortedDictionary<int, string>> smoothed = new HashSet<SortedDictionary<int, string>>();

            public SortedDictionary<int, string> Mesh(string name)
            {
                var sets = new SortedDictionary<int, string>();
                if (names.ContainsKey(name)) shared.Add(name);
                else names[name] = sets;
                return sets;
            }

            // The mesh whose UV sets are `sets` has a smoothing element.
            public void Smoothed(SortedDictionary<int, string> sets) => smoothed.Add(sets);

            public Dictionary<string, bool> Smoothing()
            {
                var result = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var kv in names)
                    if (!shared.Contains(kv.Key)) result[kv.Key] = smoothed.Contains(kv.Value);
                return result;
            }

            public Dictionary<string, List<string>> Result()
            {
                var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var kv in names)
                    if (!shared.Contains(kv.Key)) result[kv.Key] = new List<string>(kv.Value.Values);
                return result;
            }
        }

        // ── Binary ──

        struct Node
        {
            public string name;
            public int properties, count, children, end;
        }

        static void ReadBinary(byte[] b, Found found)
        {
            bool wide = BitConverter.ToUInt32(b, 23) >= 7500;
            foreach (var objects in Nodes(b, 27, b.Length, wide))
            {
                if (objects.name != "Objects") continue;
                foreach (var geometry in Nodes(b, objects.children, objects.end, wide))
                {
                    if (geometry.name != "Geometry" || !(Property(b, geometry, 2) is string kind) || kind != "Mesh") continue;
                    if (!(Property(b, geometry, 1) is string fullName)) continue;
                    int separator = fullName.IndexOf("\0\u0001", StringComparison.Ordinal);
                    var sets = found.Mesh(separator >= 0 ? fullName.Substring(0, separator) : fullName);
                    foreach (var element in Nodes(b, geometry.children, geometry.end, wide))
                    {
                        if (element.name == "LayerElementSmoothing") found.Smoothed(sets);
                        if (element.name != "LayerElementUV" || !(Property(b, element, 0) is int index)) continue;
                        foreach (var child in Nodes(b, element.children, element.end, wide))
                            if (child.name == "Name" && Property(b, child, 0) is string name) sets[index] = name;
                    }
                }
            }
        }

        // The records from at to end; a null record (or a malformed one) ends them.
        static IEnumerable<Node> Nodes(byte[] b, int at, int end, bool wide)
        {
            int head = wide ? 25 : 13;
            while (at + head <= end)
            {
                long nodeEnd = wide ? BitConverter.ToInt64(b, at) : BitConverter.ToUInt32(b, at);
                if (nodeEnd == 0) yield break;
                long count = wide ? BitConverter.ToInt64(b, at + 8) : BitConverter.ToUInt32(b, at + 4);
                long listLength = wide ? BitConverter.ToInt64(b, at + 16) : BitConverter.ToUInt32(b, at + 8);
                int nameLength = b[at + head - 1];
                long properties = at + head + nameLength;
                if (nodeEnd <= at || nodeEnd > end || properties + listLength > nodeEnd || count > int.MaxValue) yield break;
                yield return new Node
                {
                    name = Encoding.ASCII.GetString(b, at + head, nameLength),
                    properties = (int)properties, count = (int)count,
                    children = (int)(properties + listLength), end = (int)nodeEnd,
                };
                at = (int)nodeEnd;
            }
        }

        // Property index of a record: a string (S) or an integer (I); null for any other type.
        static object Property(byte[] b, Node node, int index)
        {
            int at = node.properties;
            for (int i = 0; i < node.count && at < node.children; i++)
            {
                char type = (char)b[at++];
                long size;
                switch (type)
                {
                    case 'C': size = 1; break;
                    case 'Y': size = 2; break;
                    case 'I': case 'F': size = 4; break;
                    case 'D': case 'L': size = 8; break;
                    case 'S': case 'R': size = 4L + BitConverter.ToUInt32(b, at); break;
                    case 'f': case 'd': case 'l': case 'i': case 'b': size = 12L + BitConverter.ToUInt32(b, at + 8); break;
                    default: return null;
                }
                if (at + size > node.children) return null;
                if (i == index)
                {
                    if (type == 'S') return Encoding.UTF8.GetString(b, at + 4, (int)size - 4);
                    if (type == 'I') return BitConverter.ToInt32(b, at);
                    return null;
                }
                at += (int)size;
            }
            return null;
        }

        // ── ASCII ──

        // Geometry: 123, "Geometry::Plane", "Mesh" {
        //     LayerElementUV: 0 {
        //         Name: "map1"
        static void ReadAscii(string text, Found found)
        {
            int depth = 0, geometryDepth = -1, uvDepth = -1, uvIndex = 0;
            SortedDictionary<int, string> sets = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (geometryDepth < 0 && line.StartsWith("Geometry:", StringComparison.Ordinal) && line.EndsWith("{", StringComparison.Ordinal)
                    && line.Contains("\"Mesh\""))
                {
                    string fullName = FirstQuoted(line);
                    if (fullName != null)
                    {
                        int separator = fullName.IndexOf("::", StringComparison.Ordinal);
                        sets = found.Mesh(separator >= 0 ? fullName.Substring(separator + 2) : fullName);
                        geometryDepth = depth;
                    }
                }
                else if (geometryDepth >= 0 && uvDepth < 0 && depth == geometryDepth + 1
                    && line.StartsWith("LayerElementUV:", StringComparison.Ordinal) && line.EndsWith("{", StringComparison.Ordinal)
                    && int.TryParse(line.Substring(15, line.Length - 16).Trim(), out uvIndex))
                {
                    uvDepth = depth;
                }
                else if (geometryDepth >= 0 && uvDepth < 0 && depth == geometryDepth + 1
                    && line.StartsWith("LayerElementSmoothing:", StringComparison.Ordinal))
                {
                    found.Smoothed(sets);
                }
                else if (uvDepth >= 0 && depth == uvDepth + 1 && line.StartsWith("Name:", StringComparison.Ordinal))
                {
                    string name = FirstQuoted(line);
                    if (name != null) sets[uvIndex] = name;
                }
                depth += BraceBalance(line);
                if (uvDepth >= 0 && depth <= uvDepth) uvDepth = -1;
                if (geometryDepth >= 0 && depth <= geometryDepth) geometryDepth = -1;
            }
        }

        static string FirstQuoted(string line)
        {
            int open = line.IndexOf('"');
            int close = open >= 0 ? line.IndexOf('"', open + 1) : -1;
            return close > open ? line.Substring(open + 1, close - open - 1) : null;
        }

        // Opening minus closing braces outside quoted strings.
        static int BraceBalance(string line)
        {
            int balance = 0;
            bool quoted = false;
            foreach (char c in line)
            {
                if (c == '"') quoted = !quoted;
                else if (!quoted && c == '{') balance++;
                else if (!quoted && c == '}') balance--;
            }
            return balance;
        }
    }
}
