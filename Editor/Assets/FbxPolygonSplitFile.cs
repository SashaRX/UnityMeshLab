// Surgical topology edits in an SDK-written binary scratch document. Opaque properties,
// connections, deformers and unsupported layers are copied verbatim. Only indexed data
// affected by the local polygon split is remapped; an unsupported mapping fails closed.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SashaRX.UnityMeshLab
{
    internal sealed class FbxUvPointSplit
    {
        internal int[] sources, corners;
    }

    internal static class FbxPolygonSplitFile
    {
        const string IndexName = "Index";

        sealed class Node
        {
            public string name;
            public readonly List<byte[]> properties = new List<byte[]>();
            public readonly List<Node> children = new List<Node>();
            public bool terminated;
            public HashSet<(long child, long parent)> parentConnections;
            public Node Child(string key) => children.SingleOrDefault(n => n.name == key);
            public string Text => properties.Count == 1 && properties[0][0] == 'S'
                ? Encoding.UTF8.GetString(properties[0], 5, BitConverter.ToInt32(properties[0], 1)) : null;
        }

        public static void Apply(string path, IReadOnlyDictionary<string, FbxPolygonSplit> plans)
            => Rewrite(path, (objects, meshes) => {
                foreach (var pair in plans) Split(meshes(pair.Key), pair.Value);
            });

        internal static void ApplyUvPoints(string path, IReadOnlyDictionary<string, FbxUvPointSplit> plans)
            => Rewrite(path, (objects, meshes) => {
                foreach (var pair in plans) SplitPoints(objects, meshes(pair.Key), pair.Value);
            });

        /// <summary>
        /// Gives each named mesh without smoothing an edge smoothing layer that follows its
        /// normals: an edge is smooth when each of its two ends keeps one normal across the
        /// polygons that share it. A mesh written whole from Unity's triangles carries normals
        /// but no smoothing, and a DCC needs smoothing to keep its hard edges once it is edited.
        /// </summary>
        internal static void ApplyNormalSmoothing(string path, IReadOnlyDictionary<string, bool> meshes)
            => Rewrite(path, (objects, find) => {
                foreach (var name in meshes.Keys) SmoothFromNormals(find(name));
            });

        static void SmoothFromNormals(Node mesh)
        {
            if (mesh.children.Any(n => n.name == "LayerElementSmoothing")) return;
            var polygonNode = mesh.Child("PolygonVertexIndex");
            var normalLayer = mesh.children.FirstOrDefault(n => n.name == "LayerElementNormal");
            var layer = mesh.children.FirstOrDefault(n => n.name == "Layer" && n.properties.Count > 0 && n.properties[0][0] == 'I'
                && BitConverter.ToInt32(n.properties[0], 1) == 0);
            if (polygonNode == null || normalLayer == null || layer == null) return;
            var (points, next, _) = ReadPolygons(polygonNode);
            var normals = CornerNormals(normalLayer, points);
            if (normals == null) return;

            var edgesNode = mesh.Child("Edges");
            int[] edges;
            if (edgesNode != null) edges = IntArray(edgesNode);
            else
            {
                var seen = new HashSet<(int, int)>(); var first = new List<int>();
                for (int c = 0; c < points.Length; ++c) if (seen.Add(Edge(points[c], points[next[c]]))) first.Add(c);
                edges = first.ToArray();
                edgesNode = new Node { name = "Edges" }; edgesNode.properties.Add(IntsProperty(edges));
                mesh.children.Insert(mesh.children.IndexOf(polygonNode) + 1, edgesNode);
            }
            var sides = new Dictionary<(int, int), List<int>>();
            for (int c = 0; c < points.Length; ++c)
            {
                var key = Edge(points[c], points[next[c]]);
                if (!sides.TryGetValue(key, out var corners)) sides[key] = corners = new List<int>(2);
                corners.Add(c);
            }
            var smoothing = new int[edges.Length];
            for (int e = 0; e < edges.Length; ++e)
            {
                int c = edges[e];
                if (c < 0 || c >= points.Length) throw new IOException("FBX has an invalid edge index.");
                var key = Edge(points[c], points[next[c]]);
                smoothing[e] = KeepsNormals(sides[key], key.Item1, points, next, normals)
                    && KeepsNormals(sides[key], key.Item2, points, next, normals) ? 1 : 0;
            }

            var element = Parent("LayerElementSmoothing", IntProperty(0),
                Leaf("Version", IntProperty(102)), Leaf("Name", StringProperty("")),
                Leaf("MappingInformationType", StringProperty("ByEdge")),
                Leaf("ReferenceInformationType", StringProperty("Direct")),
                Leaf("Smoothing", IntsProperty(smoothing)));
            int lastElement = mesh.children.FindLastIndex(n => n.name.StartsWith("LayerElement", StringComparison.Ordinal));
            mesh.children.Insert(lastElement + 1, element);
            layer.children.Add(Parent("LayerElement", null,
                Leaf("Type", StringProperty("LayerElementSmoothing")), Leaf("TypedIndex", IntProperty(0))));
        }

        // Whether every polygon sharing the edge has the same normal at its end `point`.
        static bool KeepsNormals(List<int> corners, int point, int[] points, int[] next, double[] normals)
        {
            int first = -1;
            foreach (int c in corners)
            {
                int at = points[c] == point ? c : next[c];
                if (first < 0) first = at;
                else if (!SameNormal(normals, first, at)) return false;
            }
            return true;
        }

        static bool SameNormal(double[] normals, int a, int b)
        {
            double ax = normals[a * 3], ay = normals[a * 3 + 1], az = normals[a * 3 + 2];
            double bx = normals[b * 3], by = normals[b * 3 + 1], bz = normals[b * 3 + 2];
            double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            // A missing normal has no direction to disagree with.
            if (!(la > 1e-12) || !(lb > 1e-12)) return true;
            return (ax * bx + ay * by + az * bz) / (la * lb) > 1 - 1e-6;
        }

        // xyz per corner, or null when the layer's mapping does not give one.
        static double[] CornerNormals(Node layer, int[] points)
        {
            string mapping = layer.Child("MappingInformationType")?.Text;
            string reference = layer.Child("ReferenceInformationType")?.Text;
            var data = layer.Child("Normals");
            if (data == null || !IsArray(data)) return null;
            var values = Doubles(data);
            int[] index = null;
            if (reference == "IndexToDirect" || reference == IndexName)
            {
                var indexNode = layer.Child("NormalsIndex");
                if (indexNode == null) return null;
                index = IntArray(indexNode);
            }
            else if (reference != "Direct") return null;
            bool perCorner = mapping == "ByPolygonVertex";
            if (!perCorner && mapping != "ByVertice" && mapping != "ByControlPoint") return null;
            var result = new double[points.Length * 3];
            for (int c = 0; c < points.Length; ++c)
            {
                int item = perCorner ? c : points[c];
                if (index != null) item = item < index.Length ? index[item] : -1;
                if (item < 0 || item * 3 + 2 >= values.Length) return null;
                Array.Copy(values, item * 3, result, c * 3, 3);
            }
            return result;
        }

        static double[] Doubles(Node node)
        {
            char type = (char)node.properties[0][0];
            byte[] bytes = ArrayData(node.properties[0]);
            if (type == 'd') { var d = new double[bytes.Length / 8]; Buffer.BlockCopy(bytes, 0, d, 0, bytes.Length); return d; }
            if (type == 'f') { var f = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length); return Array.ConvertAll(f, v => (double)v); }
            throw new IOException("FBX normals are not a floating-point array.");
        }

        static Node Leaf(string name, byte[] property)
        {
            var node = new Node { name = name }; node.properties.Add(property); return node;
        }

        static Node Parent(string name, byte[] property, params Node[] children)
        {
            var node = new Node { name = name, terminated = true };
            if (property != null) node.properties.Add(property);
            node.children.AddRange(children);
            return node;
        }

        static byte[] IntProperty(int value)
        {
            var bytes = new byte[5]; bytes[0] = (byte)'I'; Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, 1, 4); return bytes;
        }

        static byte[] StringProperty(string value)
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            var bytes = new byte[5 + text.Length]; bytes[0] = (byte)'S';
            Buffer.BlockCopy(BitConverter.GetBytes(text.Length), 0, bytes, 1, 4); Buffer.BlockCopy(text, 0, bytes, 5, text.Length);
            return bytes;
        }

        static byte[] IntsProperty(int[] values)
        {
            var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return ArrayProperty('i', bytes);
        }

        /// <summary>The named mesh's edge smoothing in a binary file, by control-point pair; null when it has none.</summary>
        internal static Dictionary<(int, int), int> ReadEdgeSmoothing(string path, string meshName)
        {
            var (_, _, roots, _) = Parse(path);
            var mesh = roots.Single(n => n.name == "Objects").children.Single(n => n.name == "Geometry"
                && n.properties.Count >= 3 && GeometryName(n.properties[1]) == meshName);
            var layer = mesh.children.FirstOrDefault(n => n.name == "LayerElementSmoothing");
            if (layer == null || layer.Child("MappingInformationType")?.Text != "ByEdge") return null;
            var (points, next, _) = ReadPolygons(mesh.Child("PolygonVertexIndex"));
            var edges = IntArray(mesh.Child("Edges"));
            var values = IntArray(layer.Child("Smoothing"));
            var result = new Dictionary<(int, int), int>();
            for (int e = 0; e < edges.Length; ++e) result[Edge(points[edges[e]], points[next[edges[e]]])] = values[e];
            return result;
        }

        static (byte[] file, bool wide, List<Node> roots, int end) Parse(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 27 || Encoding.ASCII.GetString(file, 0, 18) != "Kaydara FBX Binary")
                throw new IOException("FBX polygon split requires a binary scratch document.");
            bool wide = BitConverter.ToUInt32(file, 23) >= 7500;
            int position = 27;
            var roots = ReadNodes(file, ref position, file.Length, wide);
            return (file, wide, roots, position);
        }

        static void Rewrite(string path, Action<Node, Func<string, Node>> edit)
        {
            var (file, wide, roots, position) = Parse(path);
            var objects = roots.Single(n => n.name == "Objects");
            objects.parentConnections = roots.Single(n => n.name == "Connections").children
                .Where(n => n.name == "C" && n.properties.Count >= 3 && StringValue(n.properties[0]) == "OO")
                .Select(n => (BitConverter.ToInt64(n.properties[1],1), BitConverter.ToInt64(n.properties[2],1))).ToHashSet();
            Node Mesh(string name) {
                var meshes = objects.children.Where(n => n.name == "Geometry" && n.properties.Count >= 3
                    && StringValue(n.properties[2]) == "Mesh" && GeometryName(n.properties[1]) == name).ToArray();
                if (meshes.Length != 1) throw new InvalidOperationException($"FBX split cannot uniquely find '{name}'.");
                return meshes[0];
            }
            edit(objects, Mesh);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(file, 0, 27);
            foreach (var root in roots) WriteNode(writer, root, wide);
            writer.Write(file, position, file.Length - position);
            File.WriteAllBytes(path, stream.ToArray());
        }

        static string StringValue(byte[] property) => property[0] == 'S'
            ? Encoding.UTF8.GetString(property, 5, BitConverter.ToInt32(property, 1)) : null;

        static string GeometryName(byte[] property)
        {
            string name = StringValue(property);
            int separator = name?.IndexOf("\0\u0001", StringComparison.Ordinal) ?? -1;
            return separator >= 0 ? name.Substring(0, separator) : name;
        }

        static void SplitPoints(Node objects, Node mesh, FbxUvPointSplit plan)
        {
            var vertices = mesh.Child("Vertices") ?? throw new IOException("FBX has no control points.");
            int oldCount = BitConverter.ToInt32(vertices.properties[0], 1)/3;
            RemapArray(vertices, plan.sources, oldCount, false);
            var polygons = mesh.Child("PolygonVertexIndex");
            var original = IntArray(polygons);
            if (original.Length != plan.corners.Length) throw new IOException("FBX UV point split changed the polygon count.");
            SetInts(polygons, original.Select((v,c) => v < 0 ? ~plan.corners[c] : plan.corners[c]).ToArray());
            SplitPointEdges(mesh, original, plan.corners);
            foreach (var layer in mesh.children.Where(n => n.name.StartsWith("LayerElement", StringComparison.Ordinal)))
                SplitPointLayer(layer, plan.sources, oldCount);
            SplitPointBindings(objects, BitConverter.ToInt64(mesh.properties[0], 1), plan, oldCount);
        }

        static void SplitPointLayer(Node layer, int[] sources, int oldCount)
        {
            string mapping = layer.Child("MappingInformationType")?.Text;
            if (mapping != "ByVertice" && mapping != "ByControlPoint") return;
            string reference = layer.Child("ReferenceInformationType")?.Text;
            if (reference != "Direct" && reference != "IndexToDirect" && reference != IndexName)
                throw new IOException($"FBX UV point split cannot preserve {layer.name} reference '{reference}'.");
            bool indexed = reference == "IndexToDirect" || reference == IndexName;
            bool remapped = false;
            foreach (var data in LayerArrays(layer)) {
                bool index = data.name.EndsWith(IndexName, StringComparison.Ordinal) || data.name.EndsWith("Indices", StringComparison.Ordinal);
                if (indexed && !index) continue;
                RemapArray(data, sources, oldCount, false); remapped = true;
            }
            if (!remapped) throw new IOException($"FBX UV point split cannot preserve {layer.name}.");
        }

        // Bindings follow new point identities. Refuse unsupported caches before
        // replacing scratch documents rather than losing their data.
        static void SplitPointBindings(Node objects, long meshId, FbxUvPointSplit plan, int oldCount)
        {
            var pending = new Queue<long>(); pending.Enqueue(meshId);
            var seen = new HashSet<long> { meshId };
            while (pending.Count > 0) {
                long parent = pending.Dequeue();
                foreach (var child in BoundPointNodes(objects, parent)) {
                    long id = BitConverter.ToInt64(child.properties[0],1); if (!seen.Add(id)) continue;
                    pending.Enqueue(id);
                    SplitPointBinding(child, plan, oldCount);
                }
            }
        }

        static IEnumerable<Node> BoundPointNodes(Node objects, long parent) => objects.children.Where(n => n.properties.Count >= 3 &&
            (n.name == "Deformer" || n.name == "Geometry") && objects.parentConnections.Contains((BitConverter.ToInt64(n.properties[0],1), parent)));

        static void SplitPointBinding(Node child, FbxUvPointSplit plan, int oldCount)
        {
            string kind = StringValue(child.properties[2]);
            if (kind == "Skin" || kind == "BlendShape" || kind == "BlendShapeChannel") return;
            if (kind != "Cluster" && kind != "Shape") throw new IOException($"FBX UV point split cannot preserve '{kind}' deformer.");
            var indexNode = child.Child("Indexes"); if (indexNode == null) return;
            var indices = IntArray(indexNode);
            if (indices.Length == 0) return;
            var map = Enumerable.Range(0, indices.Length).ToList();
            var newIndices = indices.ToList(); var slots = indices.Select((v,i)=>(v,i)).ToDictionary(x=>x.v,x=>x.i);
            for (int point = oldCount; point < plan.sources.Length; ++point)
                if (slots.TryGetValue(plan.sources[point], out int slot)) { newIndices.Add(point); map.Add(slot); }
            foreach (string field in kind == "Cluster" ? new[] { "Weights" } : new[] { "Vertices", "Normals" }) {
                var array = child.Child(field); if (array != null) RemapArray(array, map.ToArray(), indices.Length, false);
            }
            SetInts(indexNode, newIndices.ToArray());
        }

        static void SplitPointEdges(Node mesh, int[] original, int[] points)
        {
            var edgesNode = mesh.Child("Edges");
            var layers = mesh.children.Where(n => n.name.StartsWith("LayerElement", StringComparison.Ordinal)
                && n.Child("MappingInformationType")?.Text == "ByEdge").ToArray();
            if (edgesNode == null) {
                if (layers.Length > 0) throw new IOException("FBX edge-mapped data has no edge table.");
                return;
            }
            var next = PointCornerSuccessors(original);
            var (oldSlots, used, edges, sources) = OriginalPointEdges(IntArray(edgesNode), original, points, next);
            for (int c = 0; c < points.Length; ++c) {
                if (!used.Add(Edge(points[c], points[next[c]]))) continue;
                if (!oldSlots.TryGetValue(Edge(original[c], original[next[c]]), out int source))
                    throw new IOException("FBX point split has no source edge for a seam.");
                edges.Add(c); sources.Add(source);
            }
            var plan = new FbxPolygonSplit();
            foreach (var layer in layers) RemapLayer(layer, plan, original.Length, 0, sources.ToArray(), oldSlots.Count);
            SetInts(edgesNode, edges.ToArray());
        }

        static int[] PointCornerSuccessors(int[] original)
        {
            var next = new int[original.Length]; int start = 0;
            for (int c = 0; c < original.Length; ++c) {
                bool end = original[c] < 0; next[c] = end ? start : c+1;
                original[c] = end ? ~original[c] : original[c]; if (end) start = c+1;
            }
            return next;
        }

        static (Dictionary<(int,int),int> slots, HashSet<(int,int)> used, List<int> edges, List<int> sources)
            OriginalPointEdges(int[] oldEdges, int[] original, int[] points, int[] next)
        {
            var slots = new Dictionary<(int,int),int>(); var used = new HashSet<(int,int)>();
            var edges = new List<int>(); var sources = new List<int>();
            for (int e = 0; e < oldEdges.Length; ++e) {
                int c = oldEdges[e];
                if (c < 0 || c >= original.Length) throw new IOException("FBX has an invalid edge index.");
                if (!slots.TryAdd(Edge(original[c], original[next[c]]), e))
                    throw new IOException("FBX point split has ambiguous source edges.");
                if (!used.Add(Edge(points[c], points[next[c]]))) throw new IOException("FBX point split cannot preserve source edges.");
                edges.Add(c); sources.Add(e);
            }
            return (slots, used, edges, sources);
        }

        static void Split(Node mesh, FbxPolygonSplit plan)
        {
            var polygonNode = mesh.Child("PolygonVertexIndex") ?? throw new IOException("FBX has no polygon indices.");
            var (oldPoints, next, polygonCount) = ReadPolygons(polygonNode);
            var points = plan.corners.Select(c => oldPoints[c]).ToArray();
            var newNext = CornerSuccessors(plan.sizes, points.Length);
            var (edgeMap, oldEdgeCount) = RemapEdges(mesh.Child("Edges"), oldPoints, next, points, newNext);
            foreach (var layer in mesh.children.Where(n => n.name.StartsWith("LayerElement", StringComparison.Ordinal)))
                RemapLayer(layer, plan, oldPoints.Length, polygonCount, edgeMap, oldEdgeCount);
            int at = 0;
            foreach (int size in plan.sizes) { at += size; points[at - 1] = ~points[at - 1]; }
            SetInts(polygonNode, points);
        }

        static (int[] points, int[] next, int count) ReadPolygons(Node polygonNode)
        {
            var points = IntArray(polygonNode);
            var next = new int[points.Length];
            int start = 0, count = 0;
            for (int c = 0; c < points.Length; ++c)
            {
                bool last = points[c] < 0;
                points[c] = last ? ~points[c] : points[c];
                next[c] = last ? start : c + 1;
                if (last) { start = c + 1; ++count; }
            }
            if (start != points.Length) throw new IOException("FBX polygon split indices are incomplete.");
            return (points, next, count);
        }

        static int[] CornerSuccessors(int[] sizes, int count)
        {
            var next = new int[count];
            int at = 0;
            foreach (int size in sizes)
            {
                for (int k = 0; k < size; ++k) next[at + k] = at + (k + 1) % size;
                at += size;
            }
            if (at != count) throw new IOException("FBX polygon split indices are incomplete.");
            return next;
        }

        static (int[] map, int count) RemapEdges(Node node, int[] oldPoints, int[] oldNext, int[] points, int[] next)
        {
            if (node == null) return (null, 0);
            var oldEdges = IntArray(node);
            var firstCorner = new Dictionary<(int, int), int>();
            for (int c = 0; c < points.Length; ++c) firstCorner.TryAdd(Edge(points[c], points[next[c]]), c);
            var newEdges = new List<int>();
            var originals = new List<int>();
            var used = new HashSet<(int, int)>();
            for (int e = 0; e < oldEdges.Length; ++e)
            {
                int corner = oldEdges[e];
                if (corner < 0 || corner >= oldPoints.Length) throw new IOException("FBX has an invalid edge index.");
                var key = Edge(oldPoints[corner], oldPoints[oldNext[corner]]);
                if (!used.Add(key) || !firstCorner.TryGetValue(key, out int representative))
                    throw new IOException("FBX split cannot preserve the source edge table.");
                newEdges.Add(representative); originals.Add(e);
            }
            foreach (var pair in firstCorner.Where(pair => !used.Contains(pair.Key))) {
                used.Add(pair.Key); newEdges.Add(pair.Value); originals.Add(-1);
            }
            SetInts(node, newEdges.ToArray());
            return (originals.ToArray(), oldEdges.Length);
        }

        static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

        static void RemapLayer(Node layer, FbxPolygonSplit plan, int corners, int polygons, int[] edges, int edgeCount)
        {
            string mapping = layer.Child("MappingInformationType")?.Text;
            if (mapping == "ByVertice" || mapping == "ByControlPoint" || mapping == "AllSame" || mapping == "None") return;
            var (map, count) = LayerMap(layer, mapping, plan, corners, polygons, edges, edgeCount);
            string reference = layer.Child("ReferenceInformationType")?.Text;
            bool indexed = reference == "IndexToDirect" || reference == IndexName;
            if (!indexed && reference != "Direct") throw new IOException($"FBX split does not support '{reference}' references.");
            bool remapped = false;
            foreach (var data in LayerArrays(layer))
                remapped |= RemapLayerArray(layer, data, mapping, indexed, map, count);
            if (!remapped) throw new IOException($"FBX split found no safely remappable data for {layer.name}.");
        }

        static (int[] map, int count) LayerMap(Node layer, string mapping, FbxPolygonSplit plan,
            int corners, int polygons, int[] edges, int edgeCount)
        {
            switch (mapping)
            {
                case "ByPolygonVertex": return (plan.corners, corners);
                case "ByPolygon": return (plan.polygons, polygons);
                case "ByEdge":
                    if (edges == null) throw new IOException("FBX edge-mapped data has no edge table.");
                    if (layer.name != "LayerElementSmoothing" && layer.name != "LayerElementVisibility" && layer.name != "LayerElementEdgeCrease")
                        throw new IOException($"FBX split cannot invent diagonal values for {layer.name}.");
                    return (edges, edgeCount);
                default: throw new IOException($"FBX split does not support {layer.name} mapping '{mapping}'.");
            }
        }

        static bool RemapLayerArray(Node layer, Node data, string mapping, bool indexed, int[] map, int count)
        {
            bool index = data.name.EndsWith(IndexName, StringComparison.Ordinal) || data.name.EndsWith("Indices", StringComparison.Ordinal)
                || layer.name == "LayerElementMaterial" || layer.name == "LayerElementTexture";
            if (indexed && !index) return false;
            if (mapping == "ByEdge" && indexed && map.Any(e => e < 0))
                throw new IOException($"FBX split cannot invent indexed diagonal values for {layer.name}.");
            RemapArray(data, map, count, layer.name == "LayerElementSmoothing");
            return true;
        }

        static bool IsArray(Node node) => node.properties.Count == 1 && "fdlibc".IndexOf((char)node.properties[0][0]) >= 0;

        // A layer's data arrays, wherever they sit: LayerElementUserData keeps one array per
        // attribute inside its own UserDataArray record instead of directly under the layer.
        static IEnumerable<Node> LayerArrays(Node layer)
            => layer.children.SelectMany(child => IsArray(child) ? new[] { child } : LayerArrays(child));

        static void RemapArray(Node node, int[] map, int oldCount, bool smoothDiagonal)
        {
            byte[] property = node.properties[0];
            int length = BitConverter.ToInt32(property, 1), itemBytes = ItemBytes((char)property[0]);
            if (oldCount <= 0 || length % oldCount != 0) throw new IOException($"FBX split array '{node.name}' has an unexpected size.");
            int stride = length / oldCount * itemBytes;
            byte[] original = ArrayData(property);
            var result = new byte[checked(map.Length * stride)];
            for (int i = 0; i < map.Length; ++i)
                CopyMappedValue(property[0], original, result, map[i], i, stride, itemBytes, smoothDiagonal);
            node.properties[0] = ArrayProperty((char)property[0], result);
        }

        static void CopyMappedValue(byte type, byte[] original, byte[] result, int source, int target,
            int stride, int itemBytes, bool smoothDiagonal)
        {
            if (source >= 0) { Buffer.BlockCopy(original, source * stride, result, target * stride, stride); return; }
            if (!smoothDiagonal) return;
            if (stride != itemBytes) throw new IOException("FBX smoothing must have one value per edge.");
            byte[] value;
            if (type == 'd') value = BitConverter.GetBytes(1d);
            else if (type == 'f') value = BitConverter.GetBytes(1f);
            else { value = new byte[itemBytes]; value[0] = 1; }
            Buffer.BlockCopy(value, 0, result, target * stride, stride);
        }

        static int ItemBytes(char type)
        {
            switch (type)
            {
                case 'd': case 'l': return 8;
                case 'f': case 'i': return 4;
                case 'b': case 'c': return 1;
                default: throw new IOException("Unsupported FBX array type.");
            }
        }

        static byte[] ArrayData(byte[] property)
        {
            int expected = checked(BitConverter.ToInt32(property, 1) * ItemBytes((char)property[0]));
            int encoding = BitConverter.ToInt32(property, 5), size = BitConverter.ToInt32(property, 9);
            byte[] result;
            if (encoding == 0) result = property.Skip(13).ToArray();
            else if (encoding == 1 && size >= 6)
            {
                // FBX stores a zlib stream; DeflateStream reads its payload after the two-byte header.
                using var input = new MemoryStream(property, 15, size - 6);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output); result = output.ToArray();
            }
            else throw new IOException("Unsupported FBX array encoding.");
            if (result.Length != expected) throw new IOException("FBX array data length differs from its declaration.");
            return result;
        }

        static byte[] ArrayProperty(char type, byte[] values)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)type); writer.Write(values.Length / ItemBytes(type)); writer.Write(0); writer.Write(values.Length); writer.Write(values);
            return stream.ToArray();
        }

        static int[] IntArray(Node node)
        {
            if (node.properties.Count != 1 || node.properties[0][0] != 'i') throw new IOException("FBX topology is not an integer array.");
            byte[] bytes = ArrayData(node.properties[0]);
            var values = new int[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values;
        }

        static void SetInts(Node node, int[] values) => node.properties[0] = IntsProperty(values);

        static List<Node> ReadNodes(byte[] file, ref int at, int limit, bool wide)
        {
            var nodes = new List<Node>();
            int header = wide ? 25 : 13;
            while (at + header <= limit)
            {
                long end = wide ? BitConverter.ToInt64(file, at) : BitConverter.ToUInt32(file, at);
                if (end == 0) break;
                nodes.Add(ReadNode(file, ref at, limit, wide, end, header));
            }
            return nodes;
        }

        static Node ReadNode(byte[] file, ref int at, int limit, bool wide, long end, int header)
        {
            long count = wide ? BitConverter.ToInt64(file, at + 8) : BitConverter.ToUInt32(file, at + 4);
            long bytes = wide ? BitConverter.ToInt64(file, at + 16) : BitConverter.ToUInt32(file, at + 8);
            int nameLength = file[at + header - 1];
            if (end > limit || end <= at || count > int.MaxValue || bytes > int.MaxValue) throw new IOException("Malformed FBX record.");
            var node = new Node { name = Encoding.UTF8.GetString(file, at + header, nameLength) };
            at += header + nameLength;
            ReadProperties(file, ref at, node, (int)count, (int)bytes, end);
            node.children.AddRange(ReadNodes(file, ref at, (int)end, wide));
            node.terminated = at < end;
            at = (int)end;
            return node;
        }

        static void ReadProperties(byte[] file, ref int at, Node node, int count, int bytes, long end)
        {
            int propertyEnd = checked(at + bytes);
            if (propertyEnd > end) throw new IOException("FBX properties extend beyond their record.");
            for (int p = 0; p < count; ++p)
            {
                int size = PropertySize(file, at);
                if (at + size > propertyEnd) throw new IOException("Malformed FBX property.");
                var property = new byte[size]; Buffer.BlockCopy(file, at, property, 0, size);
                node.properties.Add(property); at += size;
            }
            if (at != propertyEnd) throw new IOException("FBX property count differs from its byte count.");
        }

        static int PropertySize(byte[] file, int at)
        {
            switch ((char)file[at])
            {
                case 'Y': return 3;
                case 'C': return 2;
                case 'I': case 'F': return 5;
                case 'D': case 'L': return 9;
                case 'S': case 'R': return checked(5 + BitConverter.ToInt32(file, at + 1));
                case 'f': case 'd': case 'l': case 'i': case 'b': case 'c': return checked(13 + BitConverter.ToInt32(file, at + 9));
                default: throw new IOException("Unknown FBX property type.");
            }
        }

        static void WriteNode(BinaryWriter writer, Node node, bool wide)
        {
            long start = writer.BaseStream.Position;
            byte[] name = Encoding.UTF8.GetBytes(node.name);
            int bytes = node.properties.Sum(p => p.Length);
            WriteHeader(writer, 0, node.properties.Count, bytes, name.Length, wide);
            writer.Write(name);
            foreach (var property in node.properties) writer.Write(property);
            foreach (var child in node.children) WriteNode(writer, child, wide);
            if (node.terminated) writer.Write(new byte[wide ? 25 : 13]);
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = start;
            WriteHeader(writer, end, node.properties.Count, bytes, name.Length, wide);
            writer.BaseStream.Position = end;
        }

        static void WriteHeader(BinaryWriter writer, long end, int count, int bytes, int nameLength, bool wide)
        {
            if (wide) { writer.Write(end); writer.Write((long)count); writer.Write((long)bytes); }
            else { writer.Write(checked((uint)end)); writer.Write((uint)count); writer.Write((uint)bytes); }
            writer.Write(checked((byte)nameLength));
        }
    }
}
