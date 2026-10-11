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

        internal bool SameAttributes(int a, int b) => SameAttributes(a, b, true);

        internal bool SameAttributes(int a, int b, bool includeColors)
        {
            if (normals.Length == positions.Length && !normals[a].Equals(normals[b])) return false;
            if (tangents.Length == positions.Length && !tangents[a].Equals(tangents[b])) return false;
            if (includeColors && colors.Length == positions.Length && !colors[a].Equals(colors[b])) return false;
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
                SetCornerFields(mesh, vertices);
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

        // Geometry and every authored corner channel are retained. Separate
        // render corners let a verified far-color fit express discontinuities.
        internal Mesh ExpandTriangleCorners()
        {
            var corners = new List<int>(); var indices = new int[source.subMeshCount][];
            for (int s = 0; s < indices.Length; s++)
            {
                var faces = Triangles(source, s); indices[s] = new int[faces.Length];
                for (int i = 0; i < faces.Length; i++) { indices[s][i] = corners.Count; corners.Add(faces[i]); }
            }
            return CreateCornerMesh(corners, indices);
        }

        // Share only identical authored render fields within a material slot.
        // A bounded color diameter prevents transitive clusters from smearing a seam.
        // The caller verifies approximate color sharing against the source views.
        internal Mesh ShareRenderCorners(float colorTolerance, Func<bool> cancelled = null)
        {
            if (float.IsNaN(colorTolerance) || float.IsInfinity(colorTolerance) || colorTolerance < 0)
                throw new ArgumentOutOfRangeException(nameof(colorTolerance));
            if (source.blendShapeCount > 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight)) return null;
            var sharing = new CornerSharing(this, colorTolerance, cancelled);
            var indices = sharing.Indices(cancelled);
            if (cancelled?.Invoke() == true) throw new OperationCanceledException();
            if (sharing.representatives.Count >= positions.Length) return null;
            var result = CreateCornerMesh(sharing.representatives, indices);
            try
            {
                sharing.SetAveragedColors(result);
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }

        sealed class CornerSharing
        {
            readonly LodMeshData data;
            readonly float tolerance;
            readonly int[] owners;
            readonly bool hasColors;
            internal readonly List<int> representatives = new List<int>();
            readonly List<Vector4> minima = new List<Vector4>(), maxima = new List<Vector4>(), sums = new List<Vector4>();
            readonly List<int> counts = new List<int>();
            readonly Dictionary<int, List<int>> buckets = new Dictionary<int, List<int>>();
            internal CornerSharing(LodMeshData data, float tolerance, Func<bool> cancelled)
            {
                this.data = data; this.tolerance = tolerance;
                owners = data.RenderCornerOwners(cancelled);
                hasColors = data.colors.Length == data.positions.Length;
            }
            internal int[][] Indices(Func<bool> cancelled)
            {
                var indices = new int[data.source.subMeshCount][];
                for (int slot = 0; slot < indices.Length; slot++)
                {
                    var faces = Triangles(data.source, slot); indices[slot] = new int[faces.Length];
                    for (int i = 0; i < faces.Length; i++)
                    {
                        if ((i & 255) == 0 && cancelled?.Invoke() == true) throw new OperationCanceledException();
                        indices[slot][i] = FindOrAdd(faces[i], slot);
                    }
                }
                return indices;
            }
            int FindOrAdd(int vertex, int slot)
            {
                int hash = data.CornerHash(vertex, slot) * 397 ^ owners[vertex];
                var color = hasColors ? (Vector4)data.colors[vertex] : Vector4.zero;
                if (!buckets.TryGetValue(hash, out var bucket)) { bucket = new List<int>(); buckets.Add(hash, bucket); }
                int found = bucket.FindIndex(candidate => TryShare(vertex,candidate,color));
                if (found >= 0) return bucket[found];
                int added = representatives.Count; representatives.Add(vertex); bucket.Add(added);
                minima.Add(color); maxima.Add(color); sums.Add(color); counts.Add(1);
                return added;
            }
            bool TryShare(int vertex, int candidate, Vector4 color)
            {
                int rep = representatives[candidate];
                if (owners[vertex] != owners[rep] || !data.positions[vertex].Equals(data.positions[rep]) || !data.SameAttributes(vertex, rep, false)) return false;
                var lo = Vector4.Min(minima[candidate], color); var hi = Vector4.Max(maxima[candidate], color);
                if (!ColorFits(color, minima[candidate], hi - lo)) return false;
                minima[candidate] = lo; maxima[candidate] = hi; sums[candidate] += color; counts[candidate]++;
                return true;
            }
            bool ColorFits(Vector4 color, Vector4 minimum, Vector4 range)
            {
                if (tolerance == 0) return color.Equals(minimum);
                // Preserve the original ordered comparisons, including nonfinite fields.
                return !(range.x > tolerance || range.y > tolerance || range.z > tolerance || range.w > tolerance);
            }
            internal void SetAveragedColors(Mesh mesh)
            {
                if (!hasColors || tolerance <= 0) return;
                var averaged = new List<Color>();
                for (int i = 0; i < sums.Count; i++) averaged.Add((Color)(sums[i] / counts[i]));
                if (data.source.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                    mesh.SetColors(averaged.ConvertAll(c => (Color32)c));
                else mesh.SetColors(averaged);
            }
        }

        int CornerHash(int vertex, int slot)
        {
            unchecked
            {
                int hash = positions[vertex].GetHashCode() * 397 ^ slot;
                if (normals.Length == positions.Length) hash = hash * 397 ^ normals[vertex].GetHashCode();
                if (tangents.Length == positions.Length) hash = hash * 397 ^ tangents[vertex].GetHashCode();
                for (int ch = 0; ch < 8; ch++) if (uvs[ch] != null) hash = hash * 397 ^ uvs[ch][vertex].GetHashCode();
                return hash;
            }
        }

        int[] RenderCornerOwners(Func<bool> cancelled)
        {
            var owners = new int[positions.Length];
            for (int i = 0; i < owners.Length; i++) owners[i] = i;
            int Root(int vertex)
            {
                while (owners[vertex] != vertex) { owners[vertex] = owners[owners[vertex]]; vertex = owners[vertex]; }
                return vertex;
            }
            void Join(int first, int second)
            {
                if (!SameAttributes(first, second, false)) return;
                first = Root(first); second = Root(second); owners[Math.Max(first, second)] = Math.Min(first, second);
            }
            // EdgePairs rejects ambiguous edges, opposite coincident faces and
            // corner-only contacts. Color can vary; all other fields must match.
            for (int slot = 0; slot < source.subMeshCount; slot++)
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                foreach (var pair in LodSmoothingRegions.EdgePairs(this, Triangles(source, slot)))
                {
                    if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                    Join(pair[0].a, pair[1].b); Join(pair[0].b, pair[1].a);
                }
            }
            for (int i = 0; i < owners.Length; i++) owners[i] = Root(i);
            return owners;
        }

        Mesh CreateCornerMesh(List<int> corners, int[][] indices)
        {
            var mesh = new Mesh { name = source.name, indexFormat = corners.Count > 65535 ? IndexFormat.UInt32 : source.indexFormat };
            try
            {
                SetCornerFields(mesh, corners);
                mesh.subMeshCount = indices.Length;
                for (int s = 0; s < indices.Length; s++) mesh.SetTriangles(indices[s], s);
                mesh.RecalculateBounds(); if (MeshUvState.IsDraft(source)) MeshUvState.SetDraft(mesh, true);
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }

        void SetCornerFields(Mesh mesh, List<int> corners)
        {
            mesh.SetVertices(corners.ConvertAll(v => positions[v]));
            if (normals.Length == positions.Length) mesh.SetNormals(corners.ConvertAll(v => normals[v]));
            if (tangents.Length == positions.Length) mesh.SetTangents(corners.ConvertAll(v => tangents[v]));
            SetCornerColors(mesh, corners);
            for (int ch = 0; ch < 8; ch++)
                if (uvs[ch] != null) SetCornerUv(mesh, corners, ch);
        }
        void SetCornerColors(Mesh mesh, List<int> corners)
        {
            if (colors.Length != positions.Length) return;
            if (source.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                mesh.SetColors(corners.ConvertAll(v => (Color32)colors[v]));
            else mesh.SetColors(corners.ConvertAll(v => colors[v]));
        }
        void SetCornerUv(Mesh mesh, List<int> corners, int channel)
        {
            if (uvDimensions[channel] == 2) mesh.SetUVs(channel, corners.ConvertAll(v => (Vector2)uvs[channel][v]));
            else if (uvDimensions[channel] == 3) mesh.SetUVs(channel, corners.ConvertAll(v => (Vector3)uvs[channel][v]));
            else mesh.SetUVs(channel, corners.ConvertAll(v => uvs[channel][v]));
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
