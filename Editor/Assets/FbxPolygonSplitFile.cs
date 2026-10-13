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

        static void Rewrite(string path, Action<Node, Func<string, Node>> edit)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 27 || Encoding.ASCII.GetString(file, 0, 18) != "Kaydara FBX Binary")
                throw new IOException("FBX polygon split requires a binary scratch document.");
            bool wide = BitConverter.ToUInt32(file, 23) >= 7500;
            int position = 27;
            var roots = ReadNodes(file, ref position, file.Length, wide);
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
            foreach (var layer in mesh.children.Where(n => n.name.StartsWith("LayerElement", StringComparison.Ordinal))) {
                string mapping = layer.Child("MappingInformationType")?.Text;
                if (mapping != "ByVertice" && mapping != "ByControlPoint") continue;
                string reference = layer.Child("ReferenceInformationType")?.Text;
                if (reference != "Direct" && reference != "IndexToDirect" && reference != "Index")
                    throw new IOException($"FBX UV point split cannot preserve {layer.name} reference '{reference}'.");
                bool indexed = reference == "IndexToDirect" || reference == "Index";
                bool remapped = false;
                foreach (var data in layer.children.Where(IsArray)) {
                    bool index = data.name.EndsWith("Index", StringComparison.Ordinal) || data.name.EndsWith("Indices", StringComparison.Ordinal);
                    if (indexed && !index) continue;
                    RemapArray(data, plan.sources, oldCount, false); remapped = true;
                }
                if (!remapped) throw new IOException($"FBX UV point split cannot preserve {layer.name}.");
            }
            // Bindings must also follow new point identities. Refuse unsupported
            // caches before replacing the scratch document, rather than losing data.
            long meshId = BitConverter.ToInt64(mesh.properties[0], 1);
            var connections = objects.parentConnections;
            var pending = new Queue<long>(); pending.Enqueue(meshId);
            var seen = new HashSet<long> { meshId };
            while (pending.Count > 0) {
                long parent = pending.Dequeue();
                foreach (var child in objects.children.Where(n => n.properties.Count >= 3 &&
                    (n.name == "Deformer" || n.name == "Geometry") && connections.Contains((BitConverter.ToInt64(n.properties[0],1), parent)))) {
                    long id = BitConverter.ToInt64(child.properties[0],1); if (!seen.Add(id)) continue;
                    pending.Enqueue(id);
                    string kind = StringValue(child.properties[2]);
                    if (kind == "Skin" || kind == "BlendShape" || kind == "BlendShapeChannel") continue;
                    if (kind != "Cluster" && kind != "Shape") throw new IOException($"FBX UV point split cannot preserve '{kind}' deformer.");
                    var indexNode = child.Child("Indexes"); if (indexNode == null) continue;
                    var indices = IntArray(indexNode);
                    if (indices.Length == 0) continue;
                    var map = Enumerable.Range(0, indices.Length).ToList();
                    var newIndices = indices.ToList(); var slots = indices.Select((v,i)=>(v,i)).ToDictionary(x=>x.v,x=>x.i);
                    for (int point = oldCount; point < plan.sources.Length; ++point)
                        if (slots.TryGetValue(plan.sources[point], out int slot)) { newIndices.Add(point); map.Add(slot); }
                    foreach (string field in kind == "Cluster" ? new[] { "Weights" } : new[] { "Vertices", "Normals" }) {
                        var array = child.Child(field); if (array != null) RemapArray(array, map.ToArray(), indices.Length, false);
                    }
                    SetInts(indexNode, newIndices.ToArray());
                }
            }
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
            var oldEdges = IntArray(edgesNode); var next = new int[original.Length]; int start = 0;
            for (int c = 0; c < original.Length; ++c) {
                bool end = original[c] < 0; next[c] = end ? start : c+1;
                original[c] = end ? ~original[c] : original[c]; if (end) start = c+1;
            }
            var oldSlots = new Dictionary<(int,int), int>();
            var used = new HashSet<(int,int)>(); var edges = new List<int>(); var sources = new List<int>();
            for (int e = 0; e < oldEdges.Length; ++e) {
                int c = oldEdges[e];
                if (c < 0 || c >= original.Length) throw new IOException("FBX has an invalid edge index.");
                if (!oldSlots.TryAdd(Edge(original[c], original[next[c]]), e))
                    throw new IOException("FBX point split has ambiguous source edges.");
                if (!used.Add(Edge(points[c], points[next[c]]))) throw new IOException("FBX point split cannot preserve source edges.");
                edges.Add(c); sources.Add(e);
            }
            for (int c = 0; c < points.Length; ++c) {
                if (!used.Add(Edge(points[c], points[next[c]]))) continue;
                if (!oldSlots.TryGetValue(Edge(original[c], original[next[c]]), out int source))
                    throw new IOException("FBX point split has no source edge for a seam.");
                edges.Add(c); sources.Add(source);
            }
            var plan = new FbxPolygonSplit();
            foreach (var layer in layers) RemapLayer(layer, plan, original.Length, 0, sources.ToArray(), oldEdges.Length);
            SetInts(edgesNode, edges.ToArray());
        }

        static void Split(Node mesh, FbxPolygonSplit plan)
        {
            var polygonNode = mesh.Child("PolygonVertexIndex") ?? throw new IOException("FBX has no polygon indices.");
            int[] oldPoints = IntArray(polygonNode);
            var next = new int[oldPoints.Length];
            int start = 0, polygonCount = 0;
            for (int c = 0; c < oldPoints.Length; c++)
            {
                bool last = oldPoints[c] < 0;
                oldPoints[c] = last ? ~oldPoints[c] : oldPoints[c];
                next[c] = last ? start : c + 1;
                if (last) { start = c + 1; polygonCount++; }
            }
            var points = plan.corners.Select(c => oldPoints[c]).ToArray();
            var newNext = new int[points.Length];
            int at = 0;
            foreach (int size in plan.sizes)
            {
                for (int k = 0; k < size; k++) newNext[at + k] = at + (k + 1) % size;
                at += size;
            }
            if (start != oldPoints.Length || at != points.Length) throw new IOException("FBX polygon split indices are incomplete.");
            var edgesNode = mesh.Child("Edges");
            int[] edgeMap = null;
            int oldEdgeCount = 0;
            if (edgesNode != null)
            {
                var oldEdges = IntArray(edgesNode);
                oldEdgeCount = oldEdges.Length;
                var firstCorner = new Dictionary<(int, int), int>();
                for (int c = 0; c < points.Length; c++) firstCorner.TryAdd(Edge(points[c], points[newNext[c]]), c);
                var newEdges = new List<int>();
                var originals = new List<int>();
                var used = new HashSet<(int, int)>();
                for (int e = 0; e < oldEdges.Length; e++)
                {
                    int corner = oldEdges[e];
                    if (corner < 0 || corner >= oldPoints.Length) throw new IOException("FBX has an invalid edge index.");
                    var key = Edge(oldPoints[corner], oldPoints[next[corner]]);
                    if (!used.Add(key) || !firstCorner.TryGetValue(key, out int representative))
                        throw new IOException("FBX split cannot preserve the source edge table.");
                    newEdges.Add(representative); originals.Add(e);
                }
                foreach (var pair in firstCorner)
                    if (used.Add(pair.Key)) { newEdges.Add(pair.Value); originals.Add(-1); }
                SetInts(edgesNode, newEdges.ToArray());
                edgeMap = originals.ToArray();
            }
            foreach (var layer in mesh.children.Where(n => n.name.StartsWith("LayerElement", StringComparison.Ordinal)))
                RemapLayer(layer, plan, oldPoints.Length, polygonCount, edgeMap, oldEdgeCount);
            at = 0;
            foreach (int size in plan.sizes) { at += size; points[at - 1] = ~points[at - 1]; }
            SetInts(polygonNode, points);
        }

        static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

        static void RemapLayer(Node layer, FbxPolygonSplit plan, int corners, int polygons, int[] edges, int edgeCount)
        {
            string mapping = layer.Child("MappingInformationType")?.Text;
            if (mapping == "ByVertice" || mapping == "ByControlPoint" || mapping == "AllSame" || mapping == "None") return;
            int[] map;
            int count;
            switch (mapping)
            {
                case "ByPolygonVertex": map = plan.corners; count = corners; break;
                case "ByPolygon": map = plan.polygons; count = polygons; break;
                case "ByEdge":
                    if (edges == null) throw new IOException("FBX edge-mapped data has no edge table.");
                    if (layer.name != "LayerElementSmoothing" && layer.name != "LayerElementVisibility" && layer.name != "LayerElementEdgeCrease")
                        throw new IOException($"FBX split cannot invent diagonal values for {layer.name}.");
                    map = edges; count = edgeCount; break;
                default: throw new IOException($"FBX split does not support {layer.name} mapping '{mapping}'.");
            }
            string reference = layer.Child("ReferenceInformationType")?.Text;
            bool indexed = reference == "IndexToDirect" || reference == "Index";
            if (!indexed && reference != "Direct") throw new IOException($"FBX split does not support '{reference}' references.");
            bool remapped = false;
            foreach (var data in layer.children.Where(IsArray))
            {
                bool index = data.name.EndsWith("Index", StringComparison.Ordinal) || data.name.EndsWith("Indices", StringComparison.Ordinal)
                    || layer.name == "LayerElementMaterial" || layer.name == "LayerElementTexture";
                if (indexed && !index) continue;
                if (mapping == "ByEdge" && indexed && map.Any(e => e < 0))
                    throw new IOException($"FBX split cannot invent indexed diagonal values for {layer.name}.");
                RemapArray(data, map, count, layer.name == "LayerElementSmoothing");
                remapped = true;
            }
            if (!remapped) throw new IOException($"FBX split found no safely remappable data for {layer.name}.");
        }

        static bool IsArray(Node node) => node.properties.Count == 1 && "fdlibc".IndexOf((char)node.properties[0][0]) >= 0;

        static void RemapArray(Node node, int[] map, int oldCount, bool smoothDiagonal)
        {
            byte[] property = node.properties[0];
            int length = BitConverter.ToInt32(property, 1), itemBytes = ItemBytes((char)property[0]);
            if (oldCount <= 0 || length % oldCount != 0) throw new IOException($"FBX split array '{node.name}' has an unexpected size.");
            int stride = length / oldCount * itemBytes;
            byte[] original = ArrayData(property);
            var result = new byte[checked(map.Length * stride)];
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] >= 0) Buffer.BlockCopy(original, map[i] * stride, result, i * stride, stride);
                else if (smoothDiagonal)
                {
                    if (stride != itemBytes) throw new IOException("FBX smoothing must have one value per edge.");
                    byte[] value = property[0] == 'd' ? BitConverter.GetBytes(1d)
                        : property[0] == 'f' ? BitConverter.GetBytes(1f) : new byte[itemBytes];
                    if (property[0] != 'd' && property[0] != 'f') value[0] = 1;
                    Buffer.BlockCopy(value, 0, result, i * stride, stride);
                }
            }
            node.properties[0] = ArrayProperty((char)property[0], result);
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

        static void SetInts(Node node, int[] values)
        {
            var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            node.properties[0] = ArrayProperty('i', bytes);
        }

        static List<Node> ReadNodes(byte[] file, ref int at, int limit, bool wide)
        {
            var nodes = new List<Node>();
            int header = wide ? 25 : 13;
            while (at + header <= limit)
            {
                long end = wide ? BitConverter.ToInt64(file, at) : BitConverter.ToUInt32(file, at);
                if (end == 0) break;
                long count = wide ? BitConverter.ToInt64(file, at + 8) : BitConverter.ToUInt32(file, at + 4);
                long bytes = wide ? BitConverter.ToInt64(file, at + 16) : BitConverter.ToUInt32(file, at + 8);
                int nameLength = file[at + header - 1];
                if (end > limit || end <= at || count > int.MaxValue || bytes > int.MaxValue) throw new IOException("Malformed FBX record.");
                var node = new Node { name = Encoding.UTF8.GetString(file, at + header, nameLength) };
                at += header + nameLength;
                int propertyEnd = checked(at + (int)bytes);
                if (propertyEnd > end) throw new IOException("FBX properties extend beyond their record.");
                for (int p = 0; p < count; p++)
                {
                    int size = PropertySize(file, at);
                    if (at + size > propertyEnd) throw new IOException("Malformed FBX property.");
                    var property = new byte[size]; Buffer.BlockCopy(file, at, property, 0, size);
                    node.properties.Add(property); at += size;
                }
                if (at != propertyEnd) throw new IOException("FBX property count differs from its byte count.");
                node.children.AddRange(ReadNodes(file, ref at, (int)end, wide));
                node.terminated = at < end;
                at = (int)end; nodes.Add(node);
            }
            return nodes;
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
