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
        sealed class PickData
        {
            public Vector3[] vertices;
            public int[] triangles;
            public TriangleBvh bvh;
        }
        readonly Dictionary<int, PickData> bvhs = new Dictionary<int, PickData>();
        readonly List<Vector3> pickPositions = new List<Vector3>();
        readonly List<int> pickTriangles = new List<int>();
        readonly List<int> pickSubmeshTriangles = new List<int>();

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

        // A model-level summary: only the data the mesh actually carries gets a line,
        // so a plain static mesh never shows skinning or blend-shape noise.
        public string Report(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (reports.TryGetValue(id, out var report)) return report;
            var text = new StringBuilder();
            text.AppendLine($"{mesh.vertexCount:N0} vertices · {TriangleCount(mesh):N0} triangles · {mesh.subMeshCount} submesh{(mesh.subMeshCount == 1 ? "" : "es")} · {mesh.indexFormat}");
            text.AppendLine($"Size {mesh.bounds.size:F3} · center {mesh.bounds.center:F3}");
            foreach (var attribute in mesh.GetVertexAttributes())
                text.AppendLine(ChannelLine(mesh, attribute));
            if (mesh.subMeshCount > 1 || !HasOnlyTriangles(mesh))
                for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                    text.AppendLine($"Submesh {sub}: {mesh.GetTopology(sub)} · {mesh.GetIndexCount(sub):N0} indices · base vertex {mesh.GetBaseVertex(sub)}");
            using (var weights = mesh.GetAllBoneWeights())
                if (weights.Length > 0)
                    text.AppendLine($"Skinning: {weights.Length:N0} influences · {mesh.bindposes.Length:N0} bind poses");
            if (mesh.blendShapeCount > 0) {
                text.AppendLine($"Blend shapes: {mesh.blendShapeCount}");
                for (int shape = 0; shape < mesh.blendShapeCount; ++shape)
                    text.AppendLine($"  {mesh.GetBlendShapeName(shape)}: {mesh.GetBlendShapeFrameCount(shape)} frames");
            }
            return reports[id] = text.ToString();
        }

        static int TriangleCount(Mesh mesh)
        {
            int triangles = 0;
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                if (mesh.GetTopology(sub) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(sub) / 3;
            return triangles;
        }

        static string ChannelLine(Mesh mesh, VertexAttributeDescriptor attribute)
        {
            var text = new StringBuilder($"{ChannelName(attribute.attribute)}: {attribute.format} ×{attribute.dimension}");
            int channel = (int)attribute.attribute - (int)VertexAttribute.TexCoord0;
            if (channel >= 0 && channel <= 7) {
                var uv = new List<Vector4>();
                mesh.GetUVs(channel, uv);
                if (uv.Count > 0) {
                    float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                    foreach (var value in uv) {
                        minU = Mathf.Min(minU, value.x); maxU = Mathf.Max(maxU, value.x);
                        minV = Mathf.Min(minV, value.y); maxV = Mathf.Max(maxV, value.y);
                    }
                    text.Append($" · U {minU:0.###}…{maxU:0.###}");
                    if (attribute.dimension >= 2) text.Append($" · V {minV:0.###}…{maxV:0.###}");
                }
            }
            return text.ToString();
        }

        static string ChannelName(VertexAttribute attribute)
        {
            if (attribute >= VertexAttribute.TexCoord0 && attribute <= VertexAttribute.TexCoord7)
                return "UV" + ((int)attribute - (int)VertexAttribute.TexCoord0);
            switch (attribute) {
                case VertexAttribute.Position: return "Position";
                case VertexAttribute.Normal: return "Normal";
                case VertexAttribute.Tangent: return "Tangent";
                case VertexAttribute.Color: return "Color";
                case VertexAttribute.BlendWeight: return "Skin weights";
                case VertexAttribute.BlendIndices: return "Skin indices";
                default: return attribute.ToString();
            }
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
                    float brightness = largest > 0 ? 1 : 0;
                    colors[v] = indices ? Color.HSVToRGB(Mathf.Repeat(bone * .618034f, 1), .75f, brightness)
                        : new Color(largest, largest, largest, 1);
                }
                return colors;
            }
        }

        public bool Pick(IReadOnlyList<MeshViewport3D.Item> items, Vector3 origin, Vector3 direction, out int itemIndex)
        {
            itemIndex = -1; float nearest = float.MaxValue;
            for (int i = 0; i < items.Count; ++i) {
                var mesh = items[i].mesh;
                if (!mesh || !HasOnlyTriangles(mesh)) continue;
                int id = mesh.GetInstanceID();
                ReadPickGeometry(mesh);
                if (!bvhs.TryGetValue(id, out var data) || !SameValues(data.vertices, pickPositions) || !SameValues(data.triangles, pickTriangles))
                    bvhs[id] = data = BuildPickData();
                var inverse = items[i].matrix.inverse;
                var hit = data.bvh.Raycast(inverse.MultiplyPoint3x4(origin), inverse.MultiplyVector(direction), float.MaxValue);
                // Keep the local direction unnormalised: t remains the world ray parameter.
                if (hit.triangleIndex < 0 || hit.t >= nearest) continue;
                nearest = hit.t; itemIndex = i;
            }
            return itemIndex >= 0;
        }

        void ReadPickGeometry(Mesh mesh)
        {
            mesh.GetVertices(pickPositions);
            pickTriangles.Clear();
            for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                mesh.GetTriangles(pickSubmeshTriangles, sub);
                pickTriangles.AddRange(pickSubmeshTriangles);
            }
        }

        PickData BuildPickData()
        {
            var vertices = pickPositions.ToArray();
            var triangles = pickTriangles.ToArray();
            return new PickData { vertices = vertices, triangles = triangles, bvh = new TriangleBvh(vertices, triangles) };
        }

        static bool SameValues<T>(T[] previous, List<T> current)
        {
            if (previous.Length != current.Count) return false;
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < previous.Length; ++i)
                if (!comparer.Equals(previous[i], current[i])) return false;
            return true;
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
            reports.Clear(); bvhs.Clear();
        }

        public void Prune(IReadOnlyList<MeshViewport3D.Item> items)
        {
            var active = new HashSet<int>();
            foreach (var item in items) if (item.mesh) active.Add(item.mesh.GetInstanceID());
            var dropped = new List<int>();
            PruneCopies(active, dropped);
            PruneCache(reports, active, dropped);
            PruneCache(bvhs, active, dropped);
        }

        void PruneCopies(HashSet<int> active, List<int> dropped)
        {
            foreach (var pair in copies)
                if (!pair.Value || !active.Contains(pair.Value.GetInstanceID())) dropped.Add(pair.Key);
            foreach (int id in dropped)
            {
                if (copies[id]) Object.DestroyImmediate(copies[id]);
                copies.Remove(id);
            }
        }

        static void PruneCache<T>(Dictionary<int, T> cache, HashSet<int> active, List<int> dropped)
        {
            dropped.Clear();
            foreach (int id in cache.Keys) if (!active.Contains(id)) dropped.Add(id);
            foreach (int id in dropped) cache.Remove(id);
        }

        public void Clear()
        {
            foreach (var mesh in copies.Values) if (mesh) Object.DestroyImmediate(mesh);
            copies.Clear(); InvalidateData();
        }
        public void Dispose() { VertexChannels.Changed -= Invalidate; Clear(); }
    }
}
