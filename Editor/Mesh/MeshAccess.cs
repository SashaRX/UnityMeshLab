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
            dst.indexFormat = src.indexFormat;
            if (!src.isReadable) {
                try {
                    return MakeReadableCopyFromMeshData(src, dst);
                }
                catch (Exception e) when (e.Message != null && e.Message.IndexOf("isReadable", StringComparison.OrdinalIgnoreCase) >= 0) {
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

        static void FillReadableCopy(Mesh src, Mesh dst)
        {
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
                    if (uv.Count > 0 && !IsAllZero2(uv)) dst.SetUVs(ch, uv);
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
            dst.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++) dst.SetTriangles(src.GetTriangles(s), s);
            dst.bounds = src.bounds;
        }

        // The classic vertex getters log "Not allowed to access" and return EMPTY arrays
        // on a Read/Write-disabled import (the capture then finds no triangles at all);
        // MeshData is served by the engine regardless of the readable flag. Bone weights
        // have no MeshData accessor — skinned capture bakes through the renderer instead.
        static Mesh MakeReadableCopyFromMeshData(Mesh src, Mesh dst)
        {
            using (var dataArray = Mesh.AcquireReadOnlyMeshData(src))
            {
                var md = dataArray[0];
                int count = md.vertexCount;
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
                        if (uv.Length > 0 && !IsAllZero2(new List<Vector2>(uv))) dst.SetUVs(ch, new List<Vector2>(uv));
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
                    dst.SetIndices(ReadIndices(md, s), md.GetSubMesh(s).topology, s, calculateBounds: false);
            }
            dst.bounds = src.bounds;
            return dst;
        }

        // MeshData's typed getters all take NativeArray buffers; each helper copies out
        // to a managed array and disposes the scratch. applyBaseVertex folds the
        // submesh's base vertex back into the indices, as the classic getters do.
        static Vector3[] ReadVertices(Mesh.MeshData md, int count)
        {
            using (var buffer = new NativeArray<Vector3>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetVertices(buffer);
                return buffer.ToArray();
            }
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

        static Color32[] ReadColors(Mesh.MeshData md, int count)
        {
            using (var buffer = new NativeArray<Color32>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
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

        static int[] ReadIndices(Mesh.MeshData md, int submesh)
        {
            var sub = md.GetSubMesh(submesh);
            using (var buffer = new NativeArray<int>(sub.indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
            {
                md.GetIndices(buffer, submesh, true);
                return buffer.ToArray();
            }
        }

        static bool IsAllZero2(List<Vector2> uv)
        {
            for (int i = 0; i < uv.Count; i++)
                if (uv[i].x != 0f || uv[i].y != 0f) return false;
            return true;
        }
    }
}
