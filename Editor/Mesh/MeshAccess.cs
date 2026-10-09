using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The one way this package reads a mesh it may not be allowed to read. Unity's
    /// classic vertex getters log "Not allowed to access" and return EMPTY arrays on a
    /// Read/Write-disabled import; <see cref="Mesh.AcquireReadOnlyMeshData"/> is served
    /// by the engine regardless of the flag, so <see cref="ReadableCopy"/> copies every
    /// attribute through it, and only when even that is refused (some Unity 6000.2
    /// imports) flips the file's importer for one read and puts it back. No tool
    /// toggles Read/Write on an importer for its own reading any more. Main thread.
    /// </summary>
    internal static class MeshAccess
    {
        /// <summary>
        /// The mesh itself when it is readable, else a readable copy (HideAndDontSave,
        /// every attribute); <paramref name="isCopy"/> tells the caller what to destroy.
        /// </summary>
        public static Mesh Readable(Mesh mesh, out bool isCopy)
        {
            isCopy = false;
            if (mesh == null || mesh.isReadable) return mesh;
            var copy = ReadableCopy(mesh);
            copy.name = mesh.name;
            copy.hideFlags = HideFlags.HideAndDontSave;
            isCopy = true;
            return copy;
        }

        /// <summary>A new readable mesh with every attribute of <paramref name="src"/> (the caller owns it).</summary>
        public static Mesh ReadableCopy(Mesh src)
        {
            var dst = new Mesh();
            try { return ReadableCopyCore(src, dst); }
            catch {
                UnityEngine.Object.DestroyImmediate(dst);
                throw;
            }
        }

        static Mesh ReadableCopyCore(Mesh src, Mesh dst)
        {
            if (src.vertexCount > 0 && !src.HasVertexAttribute(VertexAttribute.Position))
                throw new InvalidOperationException($"Mesh '{src.name}' has vertices but no Position vertex component.");
            dst.indexFormat = src.indexFormat;
            MeshUvState.SetDraft(dst, MeshUvState.IsDraft(src));
            if (src.vertexCount == 0) {
                var layout = src.GetVertexAttributes();
                if (layout.Length > 0) dst.SetVertexBufferParams(0, layout);
                dst.subMeshCount = src.subMeshCount;
                for (int sub = 0; sub < src.subMeshCount; ++sub) {
                    if (src.GetIndexCount(sub) != 0)
                        throw new InvalidOperationException($"Mesh '{src.name}' has indices but no vertices.");
                    dst.SetIndices(Array.Empty<int>(), src.GetTopology(sub), sub, calculateBounds: false);
                }
                dst.bounds = src.bounds;
                return dst;
            }
            if (!src.isReadable) {
                try {
                    return MakeReadableCopyFromMeshData(src, dst);
                }
                catch (Exception e) when (e is MeshDataUnavailableException || (e.Message != null && e.Message.IndexOf("isReadable", StringComparison.OrdinalIgnoreCase) >= 0)) {
                    // Unity 6000.2's read-only MeshData still refuses some Read/Write-disabled
                    // imports. The only sanctioned read left is through the importer itself:
                    // flip THIS file's Read/Write for the read and put it back afterwards —
                    // a reimport pair only for files the MeshData path cannot serve, never
                    // for the readable or MeshData-served majority.
                    string path = AssetDatabase.GetAssetPath(src);
                    if (!(AssetImporter.GetAtPath(path) is ModelImporter model))
                        throw new InvalidOperationException(src.name + " is Read/Write-disabled with no importer to read it through; enable Read/Write on its import or drop it from the capture.", e);
                    model.isReadable = true;
                    model.SaveAndReimport();
                    try {
                        FillReadableCopy(src, dst);
                        return dst;
                    }
                    finally {
                        if (AssetImporter.GetAtPath(path) is ModelImporter restore) {
                            restore.isReadable = false;
                            restore.SaveAndReimport();
                        }
                    }
                }
            }
            FillReadableCopy(src, dst);
            return dst;
        }

        // Vector getters/SetUVs change scalar channels and non-Float32 UV formats.
        // Copy raw buffers for these meshes to keep their original layout.
        static Mesh CopyRawUvMesh(Mesh src, Mesh dst)
        {
            using (var data = Mesh.AcquireReadOnlyMeshData(src)) {
                var meshData = data[0];
                ValidateMeshData(src, meshData);
                dst.SetVertexBufferParams(meshData.vertexCount, src.GetVertexAttributes());
                for (int stream = 0; stream < src.vertexBufferCount; ++stream) {
                    var bytes = meshData.GetVertexData<byte>(stream);
                    dst.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream);
                }
                dst.subMeshCount = meshData.subMeshCount;
                for (int sub = 0; sub < meshData.subMeshCount; ++sub)
                    dst.SetIndices(ReadIndices(src, meshData, sub), meshData.GetSubMesh(sub).topology, sub, calculateBounds: false);
            }
            if (src.isReadable) dst.bindposes = src.bindposes;
            else if (src.HasVertexAttribute(VertexAttribute.BlendWeight) || src.HasVertexAttribute(VertexAttribute.BlendIndices))
                UvtLog.Warn($"'{src.name}': copied skin vertex data without readable bindposes; enable Read/Write before using this copy for skinning.");
            dst.bounds = src.bounds;
            return dst;
        }

        static bool NeedsRawUvCopy(Mesh mesh)
        {
            for (int channel = 0; channel < 8; ++channel) {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (mesh.HasVertexAttribute(attribute) && (mesh.GetVertexAttributeDimension(attribute) == 1
                    || mesh.GetVertexAttributeFormat(attribute) != VertexAttributeFormat.Float32)) return true;
            }
            return false;
        }

        /// <summary>Exact channel bytes and layout, restored without discarding newly generated UV2.</summary>
        internal sealed class RawVertexAttribute
        {
            readonly VertexAttributeDescriptor descriptor;
            readonly VertexAttributeDescriptor[] originalLayout;
            readonly byte[] bytes;
            readonly int vertexCount;
            internal int Dimension => descriptor.dimension;

            RawVertexAttribute(Mesh mesh, VertexAttribute attribute)
            {
                originalLayout = mesh.GetVertexAttributes();
                descriptor = Array.Find(originalLayout, item => item.attribute == attribute);
                vertexCount = mesh.vertexCount;
                using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
                    bytes = ReadAttribute(mesh, data[0], descriptor);
            }

            internal static RawVertexAttribute Capture(Mesh mesh, VertexAttribute attribute)
                => mesh.HasVertexAttribute(attribute) ? new RawVertexAttribute(mesh, attribute) : null;

            internal void Restore(Mesh mesh)
            {
                if (mesh.vertexCount != vertexCount)
                    throw new InvalidOperationException("Cannot restore a vertex channel after changing the vertex count.");
                var layout = mesh.GetVertexAttributes();
                var channels = new byte[layout.Length][];
                using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) {
                    for (int i = 0; i < layout.Length; ++i) {
                        if (layout[i].attribute == descriptor.attribute) {
                            channels[i] = bytes;
                            layout[i] = descriptor;
                        } else {
                            channels[i] = ReadAttribute(mesh, data[0], layout[i]);
                            // SetUVs can rearrange streams. Keep the original stream for existing attributes.
                            var original = Array.Find(originalLayout, item => item.attribute == layout[i].attribute);
                            if (original.dimension > 0) layout[i].stream = original.stream;
                        }
                    }
                }
                var bounds = mesh.bounds;
                mesh.SetVertexBufferParams(vertexCount, layout);
                var streams = new byte[mesh.vertexBufferCount][];
                for (int stream = 0; stream < streams.Length; ++stream)
                    streams[stream] = new byte[checked(vertexCount * mesh.GetVertexBufferStride(stream))];
                for (int i = 0; i < layout.Length; ++i) {
                    var attribute = layout[i].attribute;
                    int stream = mesh.GetVertexAttributeStream(attribute), stride = mesh.GetVertexBufferStride(stream);
                    int offset = mesh.GetVertexAttributeOffset(attribute), size = AttributeSize(layout[i]);
                    for (int vertex = 0; vertex < vertexCount; ++vertex)
                        Buffer.BlockCopy(channels[i], vertex * size, streams[stream], vertex * stride + offset, size);
                }
                for (int stream = 0; stream < streams.Length; ++stream)
                    mesh.SetVertexBufferData(streams[stream], 0, 0, streams[stream].Length, stream);
                mesh.bounds = bounds;
            }

            static byte[] ReadAttribute(Mesh mesh, Mesh.MeshData data, VertexAttributeDescriptor attribute)
            {
                int size = AttributeSize(attribute), stride = mesh.GetVertexBufferStride(attribute.stream);
                int offset = mesh.GetVertexAttributeOffset(attribute.attribute);
                var stream = data.GetVertexData<byte>(attribute.stream);
                var channel = new byte[checked(data.vertexCount * size)];
                for (int vertex = 0; vertex < data.vertexCount; ++vertex)
                    NativeArray<byte>.Copy(stream, vertex * stride + offset, channel, vertex * size, size);
                return channel;
            }

            static int AttributeSize(VertexAttributeDescriptor attribute)
            {
                int size;
                switch (attribute.format) {
                    case VertexAttributeFormat.Float32:
                    case VertexAttributeFormat.UInt32:
                    case VertexAttributeFormat.SInt32: size = 4; break;
                    case VertexAttributeFormat.Float16:
                    case VertexAttributeFormat.UNorm16:
                    case VertexAttributeFormat.SNorm16:
                    case VertexAttributeFormat.UInt16:
                    case VertexAttributeFormat.SInt16: size = 2; break;
                    case VertexAttributeFormat.UNorm8:
                    case VertexAttributeFormat.SNorm8:
                    case VertexAttributeFormat.UInt8:
                    case VertexAttributeFormat.SInt8: size = 1; break;
                    default: throw new ArgumentOutOfRangeException(nameof(attribute));
                }
                return size * attribute.dimension;
            }
        }

        static void FillReadableCopy(Mesh src, Mesh dst)
        {
            if (NeedsRawUvCopy(src)) { CopyRawUvMesh(src, dst); return; }
            dst.SetVertices(new List<Vector3>(src.vertices));
            if (src.normals != null && src.normals.Length > 0) dst.SetNormals(new List<Vector3>(src.normals));
            if (src.tangents != null && src.tangents.Length > 0) dst.SetTangents(new List<Vector4>(src.tangents));
            if (src.colors != null && src.colors.Length > 0) dst.SetColors(new List<Color>(src.colors));
            if (src.boneWeights != null && src.boneWeights.Length > 0) dst.boneWeights = src.boneWeights;
            if (src.bindposes != null && src.bindposes.Length > 0) dst.bindposes = src.bindposes;
            for (int ch = 0; ch < 8; ch++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (!src.HasVertexAttribute(attr)) continue;
                int dim = src.GetVertexAttributeDimension(attr);
                if (dim <= 2)
                {
                    var uv = new List<Vector2>(); src.GetUVs(ch, uv);
                    if (uv.Count > 0) dst.SetUVs(ch, uv);
                }
                else if (dim == 3)
                {
                    var uv = new List<Vector3>(); src.GetUVs(ch, uv);
                    if (uv.Count > 0) dst.SetUVs(ch, uv);
                }
                else
                {
                    var uv = new List<Vector4>(); src.GetUVs(ch, uv);
                    if (uv.Count > 0) dst.SetUVs(ch, uv);
                }
            }
            // Indices at their own topology: a quad or line submesh stays one.
            dst.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
                dst.SetIndices(src.GetIndices(s), src.GetTopology(s), s, calculateBounds: false);
            dst.bounds = src.bounds;
        }

        // The classic vertex getters log "Not allowed to access" and return EMPTY arrays
        // on a Read/Write-disabled import (the capture then finds no triangles at all);
        // MeshData is served by the engine regardless of the readable flag. Bone weights
        // and bind poses have no MeshData accessor, so a skinned Read/Write-disabled
        // mesh copies without its skinning — said once per copy, since nothing here can
        // do better (skinned capture bakes through the renderer instead).
        static Mesh MakeReadableCopyFromMeshData(Mesh src, Mesh dst)
        {
            if (NeedsRawUvCopy(src)) return CopyRawUvMesh(src, dst);
            using (var dataArray = Mesh.AcquireReadOnlyMeshData(src))
            {
                var md = dataArray[0];
                ValidateMeshData(src, md);
                int count = md.vertexCount;
                if (md.HasVertexAttribute(VertexAttribute.BlendWeight) || md.HasVertexAttribute(VertexAttribute.BlendIndices))
                    UvtLog.Warn($"[MeshAccess] '{src.name}' is skinned and Read/Write-disabled: the readable copy carries no bone weights or bind poses. Enable Read/Write on its importer if the copy must stay skinned.");
                dst.SetVertices(ReadVertices(md, count));
                if (md.HasVertexAttribute(VertexAttribute.Normal)) dst.SetNormals(ReadVectors3(md, count));
                if (md.HasVertexAttribute(VertexAttribute.Tangent)) dst.SetTangents(ReadVectors4(md, count));
                if (md.HasVertexAttribute(VertexAttribute.Color)) dst.SetColors(ReadColors(md, count));
                for (int ch = 0; ch < 8; ch++)
                {
                    var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                    if (!md.HasVertexAttribute(attr)) continue;
                    int dim = md.GetVertexAttributeDimension(attr);
                    if (dim <= 2)
                    {
                        var uv = ReadUV2(md, ch, count);
                        if (uv.Length > 0) dst.SetUVs(ch, new List<Vector2>(uv));
                    }
                    else if (dim == 3)
                    {
                        var uv = ReadUV3(md, ch, count);
                        if (uv.Length > 0) dst.SetUVs(ch, new List<Vector3>(uv));
                    }
                    else
                    {
                        var uv = ReadUV4(md, ch, count);
                        if (uv.Length > 0) dst.SetUVs(ch, new List<Vector4>(uv));
                    }
                }
                dst.subMeshCount = md.subMeshCount;
                for (int s = 0; s < md.subMeshCount; s++)
                    dst.SetIndices(ReadIndices(src, md, s), md.GetSubMesh(s).topology, s, calculateBounds: false);
            }
            dst.bounds = src.bounds;
            return dst;
        }

        // MeshData's typed getters all take NativeArray buffers; each helper copies out
        // to a managed array and disposes the scratch. applyBaseVertex folds the
        // submesh's base vertex back into the indices, as the classic getters do.
        static Vector3[] ReadVertices(Mesh.MeshData md, int count)
        {
            // Empty meshes have no Position layout. GetVertices still requires
            // that attribute, even when the requested buffer has zero elements.
            if (count == 0) return Array.Empty<Vector3>();
            using (var buffer = new NativeArray<Vector3>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetVertices(buffer);
                return buffer.ToArray();
            }
        }

        sealed class MeshDataUnavailableException : InvalidOperationException
        {
            internal MeshDataUnavailableException(string message) : base(message) { }
        }

        static void ValidateMeshData(Mesh source, Mesh.MeshData data)
        {
            if (data.vertexCount != source.vertexCount || (source.vertexCount > 0 && !data.HasVertexAttribute(VertexAttribute.Position)))
                throw new MeshDataUnavailableException($"MeshData for '{source.name}' does not expose its source vertex buffer (isReadable={source.isReadable}).");
        }

        static Vector3[] ReadVectors3(Mesh.MeshData md, int count)
        {
            using (var buffer = new NativeArray<Vector3>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetNormals(buffer);
                return buffer.ToArray();
            }
        }

        static Vector4[] ReadVectors4(Mesh.MeshData md, int count)
        {
            using (var buffer = new NativeArray<Vector4>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetTangents(buffer);
                return buffer.ToArray();
            }
        }

        // Float colours: a Color32 read would quantise a float or HDR colour stream.
        static Color[] ReadColors(Mesh.MeshData md, int count)
        {
            using (var buffer = new NativeArray<Color>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetColors(buffer);
                return buffer.ToArray();
            }
        }

        static Vector2[] ReadUV2(Mesh.MeshData md, int channel, int count)
        {
            using (var buffer = new NativeArray<Vector2>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetUVs(channel, buffer);
                return buffer.ToArray();
            }
        }

        static Vector3[] ReadUV3(Mesh.MeshData md, int channel, int count)
        {
            using (var buffer = new NativeArray<Vector3>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetUVs(channel, buffer);
                return buffer.ToArray();
            }
        }

        static Vector4[] ReadUV4(Mesh.MeshData md, int channel, int count)
        {
            using (var buffer = new NativeArray<Vector4>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetUVs(channel, buffer);
                return buffer.ToArray();
            }
        }

        static int[] ReadIndices(Mesh mesh, Mesh.MeshData md, int submesh)
        {
            // MeshData's descriptor includes backing ranges of every internal Mesh LOD
            // in Unity 6000.2. GetIndices fills only the active range reported by Mesh;
            // copying the descriptor-sized tail would emit uninitialized indices.
            int count = checked((int)mesh.GetIndexCount(submesh));
            using (var buffer = new NativeArray<int>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetIndices(buffer, submesh, true);
                return buffer.ToArray();
            }
        }

    }
}
