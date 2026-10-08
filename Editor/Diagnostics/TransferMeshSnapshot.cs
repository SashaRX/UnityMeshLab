using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Binary floats preserve the exact inputs used by the transfer worker.</summary>
    internal static class TransferMeshSnapshot
    {
        const int Magic = 0x554D4C54, Version = 1, MaxVertices = 5000000, MaxIndices = 15000000;
        internal const long MaxFileBytes = 512L * 1024 * 1024;

        internal static byte[] Capture(Mesh mesh)
        {
            if (!mesh || !mesh.isReadable) throw new InvalidOperationException("Capture requires a readable working mesh.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(Version); writer.Write(mesh.name);
            Write(writer, mesh.vertices); Write(writer, mesh.normals);
            var tangents = mesh.tangents; writer.Write(tangents.Length);
            foreach (var value in tangents) Write(writer, value);
            var colors = mesh.colors; writer.Write(colors.Length);
            foreach (var value in colors) Write(writer, new Vector4(value.r, value.g, value.b, value.a));
            for (int channel = 0; channel < 8; ++channel) {
                var values = new List<Vector4>(); mesh.GetUVs(channel, values);
                writer.Write(values.Count == 0 ? 0 : mesh.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)));
                writer.Write(values.Count);
                foreach (var value in values) Write(writer, value);
            }
            writer.Write(mesh.subMeshCount);
            for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                writer.Write((int)mesh.GetTopology(sub));
                var indices = mesh.GetIndices(sub); writer.Write(indices.Length);
                foreach (int index in indices) writer.Write(index);
            }
            writer.Flush();
            if (stream.Length > MaxFileBytes) throw new InvalidOperationException("Transfer snapshot exceeds the size limit.");
            return stream.ToArray();
        }

        internal static Mesh Restore(byte[] data)
        {
            if (data.LongLength > MaxFileBytes) throw new InvalidDataException("Transfer snapshot exceeds the size limit.");
            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                throw new InvalidDataException("Unsupported transfer snapshot format.");
            var mesh = new Mesh { name = reader.ReadString(), hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            try {
                mesh.vertices = ReadVectors(reader); mesh.normals = ReadVectors(reader);
                int count = Count(reader, MaxVertices);
                var tangents = new Vector4[count];
                for (int i = 0; i < count; ++i) tangents[i] = ReadVector4(reader);
                if (count > 0) mesh.tangents = tangents;
                count = Count(reader, MaxVertices);
                var colors = new Color[count];
                for (int i = 0; i < count; ++i) { var value = ReadVector4(reader); colors[i] = new Color(value.x, value.y, value.z, value.w); }
                if (count > 0) mesh.colors = colors;
                for (int channel = 0; channel < 8; ++channel) {
                    int dimension = Count(reader, 4); count = Count(reader, MaxVertices);
                    if (count == 0 && dimension == 0) continue;
                    if (count != mesh.vertexCount || dimension < 2) throw new InvalidDataException("Invalid UV channel size.");
                    var values = new List<Vector4>(count);
                    for (int i = 0; i < count; ++i) values.Add(ReadVector4(reader));
                    if (dimension == 2) mesh.SetUVs(channel, values.ConvertAll(value => new Vector2(value.x, value.y)));
                    else if (dimension == 3) mesh.SetUVs(channel, values.ConvertAll(value => new Vector3(value.x, value.y, value.z)));
                    else mesh.SetUVs(channel, values);
                }
                mesh.subMeshCount = Count(reader, 65536);
                for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                    var topology = (MeshTopology)reader.ReadInt32();
                    if (!Enum.IsDefined(typeof(MeshTopology), topology)) throw new InvalidDataException("Invalid mesh topology.");
                    count = Count(reader, MaxIndices); var indices = new int[count];
                    for (int i = 0; i < count; ++i) {
                        int index = reader.ReadInt32();
                        if (index < 0 || index >= mesh.vertexCount) throw new InvalidDataException("Mesh index is outside the vertex buffer.");
                        indices[i] = index;
                    }
                    mesh.SetIndices(indices, topology, sub);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected snapshot trailing data.");
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }

        internal static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        internal static string UvHash(Vector2[] uv)
        {
            if (uv == null) return "";
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            foreach (var value in uv) { writer.Write(value.x); writer.Write(value.y); }
            writer.Flush(); return Hash(stream.ToArray());
        }

        static int Count(BinaryReader reader, int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid snapshot element count.");
            return count;
        }
        static void Write(BinaryWriter writer, Vector3[] values)
        { writer.Write(values.Length); foreach (var value in values) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); } }
        static void Write(BinaryWriter writer, Vector4 value)
        { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
        static Vector4 ReadVector4(BinaryReader reader) => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        static Vector3[] ReadVectors(BinaryReader reader)
        {
            var values = new Vector3[Count(reader, MaxVertices)];
            for (int i = 0; i < values.Length; ++i) values[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return values;
        }
    }
}
