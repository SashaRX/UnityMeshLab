using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    // Shared, read-only inspection of the actual displayed mesh. UVs are optional.
    internal sealed class MeshInspection : IDisposable
    {
        readonly Dictionary<int, Mesh> copies = new Dictionary<int, Mesh>();
        readonly Dictionary<int, string> reports = new Dictionary<int, string>();
        readonly Dictionary<int, TriangleBvh> bvhs = new Dictionary<int, TriangleBvh>();
        int sampledMesh, sampledVertex = -1;
        string sampledValues;
        Vector3 sampledPosition;

        public MeshInspection() { VertexChannels.Changed += Invalidate; }

        public Mesh Readable(Mesh mesh)
        {
            if (!mesh || mesh.isReadable) return mesh;
            int id = mesh.GetInstanceID();
            if (!copies.TryGetValue(id, out var copy) || !copy) copies[id] = copy = MeshAccess.ReadableCopy(mesh);
            copy.name = mesh.name; copy.hideFlags = HideFlags.HideAndDontSave;
            return copy;
        }

        internal static bool Supports(Mesh mesh, MeshViewport3D.Shading mode)
        {
            if (!mesh) return false;
            switch (mode) {
                case MeshViewport3D.Shading.Shaded: return true;
                case MeshViewport3D.Shading.Positions: return mesh.HasVertexAttribute(VertexAttribute.Position);
                case MeshViewport3D.Shading.VertexColors:
                case MeshViewport3D.Shading.ColorAlpha: return mesh.HasVertexAttribute(VertexAttribute.Color);
                case MeshViewport3D.Shading.Normals: return mesh.HasVertexAttribute(VertexAttribute.Normal);
                case MeshViewport3D.Shading.Tangents:
                case MeshViewport3D.Shading.TangentSign: return mesh.HasVertexAttribute(VertexAttribute.Tangent);
                case MeshViewport3D.Shading.BoneWeights:
                case MeshViewport3D.Shading.BoneIndices:
                    using (var weights = mesh.GetAllBoneWeights()) return weights.Length > 0;
                default:
                    int channel = mode - MeshViewport3D.Shading.UV0;
                    return channel >= 0 && channel < 8 && mesh.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel));
            }
        }

        public string Report(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (reports.TryGetValue(id, out var report)) return report;
            var text = new StringBuilder();
            text.AppendLine($"{mesh.vertexCount:N0} vertices · {mesh.subMeshCount} submeshes · indices {mesh.indexFormat}");
            text.AppendLine($"Bounds center {mesh.bounds.center:G6} · size {mesh.bounds.size:G6}");
            foreach (var attribute in mesh.GetVertexAttributes())
                text.AppendLine($"{attribute.attribute}: {attribute.format} ×{attribute.dimension} · stream {attribute.stream}");
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                text.AppendLine($"Submesh {sub}: {mesh.GetTopology(sub)} · {mesh.GetIndexCount(sub):N0} indices · base vertex {mesh.GetBaseVertex(sub)}");
            text.AppendLine($"Bind poses: {mesh.bindposes.Length} · blend shapes: {mesh.blendShapeCount}");
            using (var weights = mesh.GetAllBoneWeights()) text.AppendLine($"Skin influences: {weights.Length:N0}");
            for (int shape = 0; shape < mesh.blendShapeCount; ++shape)
                text.AppendLine($"Blend shape {shape}: {mesh.GetBlendShapeName(shape)} · {mesh.GetBlendShapeFrameCount(shape)} frames");
            return reports[id] = text.ToString();
        }

        public string VertexValues(Mesh mesh, int vertex)
        {
            if (!mesh || vertex < 0 || vertex >= mesh.vertexCount) return "No vertex selected.";
            int id = mesh.GetInstanceID();
            if (sampledMesh == id && sampledVertex == vertex) return sampledValues;
            var text = new StringBuilder();
            sampledPosition = mesh.vertices[vertex];
            text.AppendLine($"Position: {sampledPosition:G9}");
            if (mesh.HasVertexAttribute(VertexAttribute.Normal)) text.AppendLine($"Normal: {mesh.normals[vertex]:G9}");
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) text.AppendLine($"Tangent xyzw: {mesh.tangents[vertex]:G9}");
            if (mesh.HasVertexAttribute(VertexAttribute.Color)) text.AppendLine($"Color rgba: {mesh.colors[vertex]:G9}");
            var uv = new List<Vector4>();
            for (int channel = 0; channel < 8; ++channel) {
                if (!mesh.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel))) continue;
                mesh.GetUVs(channel, uv);
                text.AppendLine($"UV{channel} xyzw: {uv[vertex]:G9}");
            }
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights()) {
                if (counts.Length == mesh.vertexCount) {
                    int start = 0;
                    for (int i = 0; i < vertex; ++i) start += counts[i];
                    for (int i = 0; i < counts[vertex]; ++i) {
                        var weight = weights[start + i];
                        text.AppendLine($"Bone {weight.boneIndex}: {weight.weight:G9}");
                    }
                }
            }
            sampledMesh = id; sampledVertex = vertex;
            return sampledValues = text.ToString();
        }

        public Vector3 VertexPosition(Mesh mesh, int vertex)
        {
            VertexValues(mesh, vertex);
            return sampledPosition;
        }

        internal static Color32[] SkinColors(Mesh mesh, bool indices)
        {
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights()) {
                if (counts.Length != mesh.vertexCount || weights.Length == 0) return null;
                var colors = new Color32[mesh.vertexCount];
                int start = 0;
                for (int v = 0; v < colors.Length; ++v) {
                    float largest = 0; int bone = 0;
                    for (int i = 0; i < counts[v]; ++i) {
                        var weight = weights[start++];
                        if (weight.weight > largest) { largest = weight.weight; bone = weight.boneIndex; }
                    }
                    colors[v] = indices ? Color.HSVToRGB(Mathf.Repeat(bone * .618034f, 1), .75f, largest > 0 ? 1 : 0)
                        : new Color(largest, largest, largest, 1);
                }
                return colors;
            }
        }

        public bool Pick(IReadOnlyList<MeshViewport3D.Item> items, Vector3 origin, Vector3 direction, out int itemIndex, out int vertex)
        {
            itemIndex = vertex = -1; float nearest = float.MaxValue;
            for (int i = 0; i < items.Count; ++i) {
                var mesh = items[i].mesh;
                if (!mesh || !HasOnlyTriangles(mesh)) continue;
                int id = mesh.GetInstanceID();
                if (!bvhs.TryGetValue(id, out var bvh)) bvhs[id] = bvh = new TriangleBvh(mesh.vertices, mesh.triangles);
                var inverse = items[i].matrix.inverse;
                var hit = bvh.Raycast(inverse.MultiplyPoint3x4(origin), inverse.MultiplyVector(direction), float.MaxValue);
                // Keep the local direction unnormalised: t remains the world ray parameter.
                if (hit.triangleIndex < 0 || hit.t >= nearest) continue;
                var tri = mesh.triangles; var b = hit.barycentric;
                vertex = tri[hit.triangleIndex * 3 + (b.x >= b.y && b.x >= b.z ? 0 : b.y >= b.z ? 1 : 2)];
                nearest = hit.t; itemIndex = i;
            }
            return itemIndex >= 0;
        }

        internal static bool HasOnlyTriangles(Mesh mesh)
        {
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) return false;
            return true;
        }

        void Invalidate(Mesh mesh) { Clear(); }
        public void InvalidateData()
        {
            reports.Clear(); bvhs.Clear(); sampledMesh = 0; sampledVertex = -1; sampledValues = null;
        }

        public void Prune(IReadOnlyList<MeshViewport3D.Item> items)
        {
            var active = new HashSet<int>();
            foreach (var item in items) if (item.mesh) active.Add(item.mesh.GetInstanceID());
            var dropped = new List<int>();
            foreach (var pair in copies)
                if (!pair.Value || !active.Contains(pair.Value.GetInstanceID())) dropped.Add(pair.Key);
            foreach (int id in dropped) { if (copies[id]) Object.DestroyImmediate(copies[id]); copies.Remove(id); }
            dropped.Clear();
            foreach (int id in reports.Keys) if (!active.Contains(id)) dropped.Add(id);
            foreach (int id in dropped) reports.Remove(id);
            dropped.Clear();
            foreach (int id in bvhs.Keys) if (!active.Contains(id)) dropped.Add(id);
            foreach (int id in dropped) bvhs.Remove(id);
        }

        public void Clear()
        {
            foreach (var mesh in copies.Values) if (mesh) Object.DestroyImmediate(mesh);
            copies.Clear(); InvalidateData();
        }
        public void Dispose() { VertexChannels.Changed -= Invalidate; Clear(); }
    }
}
