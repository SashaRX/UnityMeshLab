// FbxPolygonSplitFileTests.cs — the binary split remaps every per-corner and per-point array a
// layer holds, including LayerElementUserData's, which sit one record deeper than other layers'.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FbxPolygonSplitFileTests
    {
        sealed class Record
        {
            public string name; public byte[][] properties; public Record[] children;
            public Record(string name, byte[][] properties, params Record[] children) { this.name = name; this.properties = properties; this.children = children; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SplitRemapsUserDataInItsAttributeRecords(bool points)
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshLabUserDataSplit_" + Guid.NewGuid().ToString("N") + ".fbx");
            try
            {
                // One quad; the user data has a float and a two-double attribute per corner or per point.
                File.WriteAllBytes(path, Document(points ? "ByControlPoint" : "ByPolygonVertex"));
                if (points)
                    FbxPolygonSplitFile.ApplyUvPoints(path, new Dictionary<string, FbxUvPointSplit> {
                        ["PartMesh"] = new FbxUvPointSplit { sources = new[] { 0, 1, 2, 3, 2 }, corners = new[] { 0, 1, 4, 3 } } });
                else
                    FbxPolygonSplitFile.Apply(path, new Dictionary<string, FbxPolygonSplit> {
                        ["PartMesh"] = new FbxPolygonSplit { sizes = new[] { 3, 3 }, corners = new[] { 0, 1, 2, 0, 2, 3 }, polygons = new[] { 0, 0 } } });
                byte[] file = File.ReadAllBytes(path);
                int[] map = points ? new[] { 0, 1, 2, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
                CollectionAssert.AreEqual(map.Select(i => 10f + i), ReadArray(file, "Weight", "UserData", 4, BitConverter.ToSingle));
                CollectionAssert.AreEqual(map.SelectMany(i => new[] { i + .5, i + .25 }), ReadArray(file, "Offset", "UserData", 8, BitConverter.ToDouble));
                CollectionAssert.AreEqual(points ? new[] { 0, 1, 4, ~3 } : new[] { 0, 1, ~2, 0, 2, ~3 },
                    ReadArray(file, null, "PolygonVertexIndex", 4, BitConverter.ToInt32));
            }
            finally { File.Delete(path); }
        }

        // The array of the first record named <record> after the text <after> (an attribute's name).
        static T[] ReadArray<T>(byte[] file, string after, string record, int size, Func<byte[], int, T> read)
        {
            int at = after == null ? 0 : Find(file, Encoding.ASCII.GetBytes(after), 0);
            byte[] key = new[] { (byte)record.Length }.Concat(Encoding.ASCII.GetBytes(record)).ToArray();
            at = Find(file, key, at) + key.Length; // the record's only property follows its name
            Assert.AreEqual(0, BitConverter.ToInt32(file, at + 5), "the split writes its arrays uncompressed");
            int count = BitConverter.ToInt32(file, at + 1);
            return Enumerable.Range(0, count).Select(i => read(file, at + 13 + i * size)).ToArray();
        }

        static int Find(byte[] file, byte[] key, int from)
        {
            for (int i = from; i + key.Length <= file.Length; ++i)
                if (file.Skip(i).Take(key.Length).SequenceEqual(key)) return i;
            throw new AssertionException($"'{Encoding.ASCII.GetString(key)}' is missing from the split file.");
        }

        static byte[] Document(string mapping)
        {
            const int items = 4; // the quad's points, and its corners
            var userData = new Record("LayerElementUserData", new[] { Int(0) },
                new Record("Version", new[] { Int(101) }),
                new Record("Name", new[] { Text("UserData") }),
                new Record("MappingInformationType", new[] { Text(mapping) }),
                new Record("ReferenceInformationType", new[] { Text("Direct") }),
                new Record("UserDataArray", new byte[0][],
                    new Record("UserDataType", new[] { Text("Float") }),
                    new Record("UserDataName", new[] { Text("Weight") }),
                    new Record("UserData", new[] { ArrayOf('f', 4, Enumerable.Range(0, items).SelectMany(i => BitConverter.GetBytes(10f + i))) })),
                new Record("UserDataArray", new byte[0][],
                    new Record("UserDataType", new[] { Text("Vector2") }),
                    new Record("UserDataName", new[] { Text("Offset") }),
                    new Record("UserData", new[] { ArrayOf('d', 8, Enumerable.Range(0, items).SelectMany(i => BitConverter.GetBytes(i + .5).Concat(BitConverter.GetBytes(i + .25)))) })));
            var geometry = new Record("Geometry", new[] { Long(1), Text("PartMesh\0\u0001Geometry"), Text("Mesh") },
                new Record("Vertices", new[] { ArrayOf('d', 8, new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }.SelectMany(BitConverter.GetBytes)) }),
                new Record("PolygonVertexIndex", new[] { ArrayOf('i', 4, new[] { 0, 1, 2, ~3 }.SelectMany(BitConverter.GetBytes)) }),
                userData);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0")); writer.Write(7400);
            Write(writer, new Record("Objects", new byte[0][], geometry));
            Write(writer, new Record("Connections", new byte[0][]));
            writer.Write(new byte[13]); writer.Write(Encoding.ASCII.GetBytes("footer"));
            return stream.ToArray();
        }

        static void Write(BinaryWriter writer, Record record)
        {
            long start = writer.BaseStream.Position;
            byte[] name = Encoding.ASCII.GetBytes(record.name);
            writer.Write(0u); writer.Write((uint)record.properties.Length); writer.Write((uint)record.properties.Sum(p => p.Length));
            writer.Write((byte)name.Length); writer.Write(name);
            foreach (var property in record.properties) writer.Write(property);
            foreach (var child in record.children) Write(writer, child);
            if (record.children.Length > 0) writer.Write(new byte[13]);
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = start; writer.Write((uint)end); writer.BaseStream.Position = end;
        }

        static byte[] Int(int value) => new[] { (byte)'I' }.Concat(BitConverter.GetBytes(value)).ToArray();
        static byte[] Long(long value) => new[] { (byte)'L' }.Concat(BitConverter.GetBytes(value)).ToArray();
        static byte[] Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            return new[] { (byte)'S' }.Concat(BitConverter.GetBytes(bytes.Length)).Concat(bytes).ToArray();
        }
        static byte[] ArrayOf(char type, int size, IEnumerable<byte> values)
        {
            byte[] data = values.ToArray();
            return new[] { (byte)type }.Concat(BitConverter.GetBytes(data.Length / size)).Concat(BitConverter.GetBytes(0))
                .Concat(BitConverter.GetBytes(data.Length)).Concat(data).ToArray();
        }
    }
}
