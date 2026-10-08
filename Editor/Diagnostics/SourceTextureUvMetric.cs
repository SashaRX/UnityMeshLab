using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [Serializable]
    internal sealed class SourceTextureUvMetric
    {
        [Serializable]
        internal sealed class TextureInfo
        {
            public string material, texture, assetPath;
            public int width, height;
            public Vector2 tiling;
            public float aspect;
        }

        public List<TextureInfo> textures = new List<TextureInfo>();
        public List<string> unresolvedMaterials = new List<string>();
        public Vector2 uvScale = Vector2.one;
        public bool conflictingAspects;
        public string reason = "No source texture; normalized UV coordinates are used.";

        internal sealed class OriginalUv0
        {
            readonly MeshAccess.RawVertexAttribute channel;
            readonly int dimension;
            internal bool modified;
            internal int Dimension => dimension;
            internal OriginalUv0(Mesh mesh) {
                channel = MeshAccess.RawVertexAttribute.Capture(mesh, UnityEngine.Rendering.VertexAttribute.TexCoord0);
                dimension = channel == null ? 0 : channel.Dimension;
            }
            internal void Restore(Mesh mesh) {
                if (!modified) return;
                channel.Restore(mesh);
            }
        }

        internal static SourceTextureUvMetric Resolve(Renderer renderer, Texture previewTexture = null, Mesh mesh = null)
        {
            var metric = new SourceTextureUvMetric();
            if (renderer != null) {
                if (mesh == null) {
                    if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
                    else {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter) mesh = filter.sharedMesh;
                    }
                }
                var materials = renderer.sharedMaterials;
                int slots = mesh != null ? Math.Max(mesh.subMeshCount, materials.Length) : materials.Length;
                for (int slot = 0; slot < slots; ++slot) {
                    // Extra renderer materials render the last submesh. Empty submeshes contribute no UVs.
                    if (mesh != null && (mesh.subMeshCount == 0 || mesh.GetIndexCount(Math.Min(slot, mesh.subMeshCount - 1)) == 0)) continue;
                    var material = slot < materials.Length ? materials[slot] : null;
                    if (material == null || !metric.Add(material.mainTexture, material.mainTextureScale, material.name))
                        metric.unresolvedMaterials.Add(material ? material.name : "Missing material at slot " + slot);
                }
            }
            if (metric.textures.Count == 0 && metric.unresolvedMaterials.Count == 0 && previewTexture != null)
                metric.Add(previewTexture, Vector2.one, "Preview texture");
            if (metric.textures.Count == 0) return metric;

            float aspect = metric.textures[0].aspect;
            metric.conflictingAspects = metric.unresolvedMaterials.Count > 0;
            foreach (var texture in metric.textures)
                if (Math.Abs(Math.Log(texture.aspect / aspect)) > 1e-5) metric.conflictingAspects = true;
            if (metric.conflictingAspects) {
                metric.reason = "Used materials have different or unresolved texture proportions; no single mesh-wide correction is valid.";
                return metric;
            }
            float u = Mathf.Sqrt(aspect);
            metric.uvScale = new Vector2(u, 1f / u);
            metric.reason = "Texture width/height and material tiling define the UV metric before ARAP and packing.";
            return metric;
        }

        bool Add(Texture texture, Vector2 tiling, string material)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0) return false;
            double width = texture.width * Math.Abs((double)tiling.x);
            double height = texture.height * Math.Abs((double)tiling.y);
            double aspect = width / height;
            if (width <= 0 || height <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect < 1e-6 || aspect > 1e6) return false;
            textures.Add(new TextureInfo { material = material, texture = texture.name,
                assetPath = AssetDatabase.GetAssetPath(texture), width = texture.width, height = texture.height,
                tiling = tiling, aspect = (float)aspect });
            return true;
        }

        /// <summary>Only the temporary repack mesh is modified. Restore the returned UV0 in finally.</summary>
        internal OriginalUv0 PrepareTemporaryMesh(Mesh mesh, bool enabled)
        {
            var saved = new OriginalUv0(mesh);
            if (!enabled || conflictingAspects || (uvScale - Vector2.one).sqrMagnitude < 1e-12f) return saved;
            // A one-component channel cannot define a 2D texture metric; do not change its layout.
            if (saved.Dimension < 2) return saved;
            var original = mesh.uv;
            var corrected = new Vector2[original.Length];
            for (int i = 0; i < corrected.Length; ++i) corrected[i] = Vector2.Scale(original[i], uvScale);
            saved.modified = true;
            mesh.uv = corrected;
            UvtLog.Info(UvtLog.Category.Repack,
                $"[TextureAspect] '{mesh.name}': UV metric scale ({uvScale.x:G6}, {uvScale.y:G6}) before packing; source UV0 is preserved.");
            return saved;
        }

        /// <summary>The native bridge normalizes each axis independently. Unity's lightmap domain is square.</summary>
        internal static uint NormalizePackedLightmap(Mesh mesh, uint packedWidth, uint packedHeight)
        {
            uint side = Math.Max(packedWidth, packedHeight);
            if (side == 0 || packedWidth == 0 || packedHeight == 0 || packedWidth == packedHeight) return side;
            var scale = new Vector2((float)packedWidth / side, (float)packedHeight / side);
            var uv2 = mesh.uv2;
            for (int i = 0; i < uv2.Length; ++i) uv2[i] = Vector2.Scale(uv2[i], scale);
            mesh.uv2 = uv2;
            UvtLog.Info(UvtLog.Category.Repack, $"[TextureAspect] '{mesh.name}': packed atlas {packedWidth}x{packedHeight} uses a {side}x{side} lightmap metric.");
            return side;
        }
    }
}
