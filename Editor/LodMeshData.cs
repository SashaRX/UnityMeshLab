using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    internal enum LodReductionMode { Triangles, FullLoops, LoopsThenTriangles }

    // An immutable snapshot of working LOD0. Polygon corners refer to these render
    // vertices; connectivity uses separate source control-point IDs.
    internal sealed class LodMeshData
    {
        internal readonly Mesh source;
        internal readonly Vector3[] positions, normals;
        internal readonly Vector4[] tangents;
        internal readonly Color[] colors;
        internal readonly List<Vector4>[] uvs = new List<Vector4>[8];
        internal readonly int[] uvDimensions = new int[8];
        internal readonly float scale;

        internal LodMeshData(Mesh mesh)
        {
            source = mesh;
            positions = mesh.vertices;
            normals = mesh.normals;
            tangents = mesh.tangents;
            colors = mesh.colors;
            for (int ch = 0; ch < 8; ch++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (!mesh.HasVertexAttribute(attribute)) continue;
                uvDimensions[ch] = mesh.GetVertexAttributeDimension(attribute);
                uvs[ch] = new List<Vector4>();
                mesh.GetUVs(ch, uvs[ch]);
            }
            var bounds = new Bounds(positions[0], Vector3.zero);
            foreach (var p in positions) bounds.Encapsulate(p);
            scale = Mathf.Max(bounds.size.magnitude, 1e-8f);
        }

        internal bool SameAttributes(int a, int b)
        {
            if (normals.Length == positions.Length && !normals[a].Equals(normals[b])) return false;
            if (tangents.Length == positions.Length && !tangents[a].Equals(tangents[b])) return false;
            if (colors.Length == positions.Length && !colors[a].Equals(colors[b])) return false;
            for (int ch = 0; ch < 8; ch++)
                if (uvs[ch] != null && !uvs[ch][a].Equals(uvs[ch][b])) return false;
            return true;
        }

        internal Mesh CreateMesh(List<LodSourceTopology.Face> faces)
            => CreateMesh(faces, out _);

        internal Mesh CreateMesh(List<LodSourceTopology.Face> faces, out Dictionary<int, int> remap, bool ensureChannels = true)
        {
            var indices = new List<int>[source.subMeshCount];
            for (int s = 0; s < indices.Length; s++) indices[s] = new List<int>();
            var used = new SortedSet<int>();
            foreach (var face in faces)
            {
                indices[face.submesh].AddRange(face.triangles);
                foreach (int v in face.triangles) used.Add(v);
            }
            var vertices = new List<int>(used);
            remap = new Dictionary<int, int>();
            for (int i = 0; i < vertices.Count; i++) remap[vertices[i]] = i;
            var mesh = new Mesh { name = source.name, indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : source.indexFormat };
            try
            {
                mesh.SetVertices(vertices.ConvertAll(v => positions[v]));
                if (normals.Length == positions.Length) mesh.SetNormals(vertices.ConvertAll(v => normals[v]));
                if (tangents.Length == positions.Length) mesh.SetTangents(vertices.ConvertAll(v => tangents[v]));
                if (colors.Length == positions.Length)
                {
                    if (source.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                        mesh.SetColors(vertices.ConvertAll(v => (Color32)colors[v]));
                    else mesh.SetColors(vertices.ConvertAll(v => colors[v]));
                }
                for (int ch = 0; ch < 8; ch++)
                {
                    if (uvs[ch] == null) continue;
                    int channel = ch;
                    if (uvDimensions[ch] == 2) mesh.SetUVs(ch, vertices.ConvertAll(v => (Vector2)uvs[channel][v]));
                    else if (uvDimensions[ch] == 3) mesh.SetUVs(ch, vertices.ConvertAll(v => (Vector3)uvs[channel][v]));
                    else mesh.SetUVs(ch, vertices.ConvertAll(v => uvs[channel][v]));
                }
                mesh.subMeshCount = indices.Length;
                var mapping = remap;
                for (int s = 0; s < indices.Length; s++) mesh.SetTriangles(indices[s].ConvertAll(v => mapping[v]), s);
                if (ensureChannels) MeshGeometry.EnsureMeshChannels(mesh);
                if (MeshUvState.IsDraft(source)) MeshUvState.SetDraft(mesh, true);
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }

        internal static int[] Triangles(Mesh mesh, int submesh)
        {
            var topology = mesh.GetTopology(submesh);
            var indices = mesh.GetIndices(submesh);
            if (topology == MeshTopology.Triangles) return indices;
            if (topology != MeshTopology.Quads) throw new InvalidOperationException("LOD generation requires triangle or quad faces.");
            var result = new List<int>();
            for (int i = 0; i < indices.Length; i += 4)
                result.AddRange(new[] { indices[i], indices[i + 1], indices[i + 2], indices[i], indices[i + 2], indices[i + 3] });
            return result.ToArray();
        }

        internal static int TriangleCount(Mesh mesh)
        {
            int count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                count += (int)mesh.GetIndexCount(s) * (mesh.GetTopology(s) == MeshTopology.Quads ? 2 : 1) /
                    (mesh.GetTopology(s) == MeshTopology.Quads ? 4 : 3);
            return count;
        }
    }
}
