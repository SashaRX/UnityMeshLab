using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Read-only material inspection. Per-draw values prevent submeshes from sharing
    // the last material's maps; nothing is assigned to the scene or authored assets.
    internal static class MaterialChannelPreview
    {
        internal struct Data
        {
            internal Texture texture;
            internal Vector4 transform;
            internal Color tint;
            internal float mode, channel, scalar, bias, normalScale, normalConvention, vertexColor;

            internal void Apply(MaterialPropertyBlock block)
            {
                block.SetTexture("_MainTex", texture ? texture : Texture2D.whiteTexture);
                block.SetVector("_UvScaleOffset", transform);
                block.SetColor("_Color", tint);
                block.SetFloat("_ViewerMode", mode);
                block.SetFloat("_UseTexture", texture ? 1 : 0);
                block.SetFloat("_Channel", channel);
                block.SetFloat("_Scalar", scalar);
                block.SetFloat("_Bias", bias);
                block.SetFloat("_NormalScale", normalScale);
                block.SetFloat("_NormalConvention", normalConvention);
                block.SetFloat("_UseVertexColor", vertexColor);
            }

            internal void Apply(Material material)
            {
                material.SetTexture("_MainTex", texture ? texture : Texture2D.whiteTexture);
                material.SetVector("_UvScaleOffset", transform);
                material.SetColor("_Color", tint);
                material.SetFloat("_ViewerMode", mode);
                material.SetFloat("_UseTexture", texture ? 1 : 0);
                material.SetFloat("_Channel", channel);
                material.SetFloat("_Scalar", scalar);
                material.SetFloat("_Bias", bias);
                material.SetFloat("_NormalScale", normalScale);
                material.SetFloat("_NormalConvention", normalConvention);
                material.SetFloat("_UseVertexColor", vertexColor);
            }
        }

        internal static Data Read(Material material, MeshViewport3D.Shading mode)
        {
            var data = new Data {
                mode = mode - MeshViewport3D.Shading.Albedo, tint = Color.white,
                scalar = 1, normalScale = 1, transform = new Vector4(1, 1, 0, 0)
            };
            string shader = material && material.shader ? material.shader.name : "";
            bool urp = shader == "Universal Render Pipeline/Lit";
            bool standard = shader == "Standard" || shader == "Standard (Specular setup)";
            bool specular = shader == "Standard (Specular setup)" || (urp && Value(material, 1, "_WorkflowMode") == 0);
            string albedo = Property(material, "_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Albedo");
            string property = null;
            switch (mode) {
                case MeshViewport3D.Shading.Albedo:
                    property = albedo;
                    data.tint = Tint(material);
                    data.vertexColor = Value(material, 0, "_UseVertexColor");
                    break;
                case MeshViewport3D.Shading.NormalMap:
                    property = Property(material, "_BumpMap", "_NormalMap");
                    if ((standard || urp) && !material.IsKeywordEnabled("_NORMALMAP")) property = null;
                    data.normalScale = Value(material, 1, "_BumpScale", "_NormalScale");
                    data.normalConvention = urp ? 1 : 0;
                    break;
                case MeshViewport3D.Shading.Gloss:
                    bool fromAlbedo = material && material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
                    property = fromAlbedo ? albedo : GlossProperty(material, standard, urp, specular);
                    data.channel = fromAlbedo || property == "_MetallicGlossMap" || property == "_SpecGlossMap" || property == "_MaskMap" ? 3 : 0;
                    data.scalar = standard ? Value(material, .5f, property != null || fromAlbedo ? "_GlossMapScale" : "_Glossiness") : Value(material, .5f, "_Smoothness", "_Glossiness", "_GlossMapScale");
                    break;
                case MeshViewport3D.Shading.Metalness:
                    property = specular ? null : MetalProperty(material, standard, urp);
                    data.scalar = specular ? 0 : property != null ? 1 : Value(material, 0, "_Metallic", "_Metalness");
                    break;
                case MeshViewport3D.Shading.AO:
                    property = Property(material, "_OcclusionMap", "_AOMap", "_AmbientOcclusionMap", "_MaskMap");
                    data.channel = property == "_OcclusionMap" || property == "_MaskMap" ? 1 : 0;
                    data.scalar = Mathf.Clamp01(Value(material, 1, "_OcclusionStrength"));
                    data.bias = 1 - data.scalar;
                    break;
            }
            if (property != null) {
                data.texture = material.GetTexture(property);
                // Lit shaders share the base transform; custom shaders keep each map's ST.
                string st = (standard || urp) ? DeclaredProperty(material, urp ? "_BaseMap" : "_MainTex") ?? property : property;
                var scale = material.GetTextureScale(st); var offset = material.GetTextureOffset(st);
                data.transform = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }
            if (mode == MeshViewport3D.Shading.NormalMap && data.texture) {
                string path = AssetDatabase.GetAssetPath(data.texture);
                bool raw = shader == "Hidden/MeshLab/RemeshPreview" ||
                    (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType != TextureImporterType.NormalMap);
                if (raw) data.normalConvention = 2;
            }
            return data;
        }

        static string GlossProperty(Material material, bool standard, bool urp, bool specular)
        {
            if (standard || urp) {
                string keyword = urp ? "_METALLICSPECGLOSSMAP" : specular ? "_SPECGLOSSMAP" : "_METALLICGLOSSMAP";
                return material.IsKeywordEnabled(keyword) ? Property(material, specular ? "_SpecGlossMap" : "_MetallicGlossMap") : null;
            }
            return Property(material, "_SmoothnessMap", "_GlossMap", "_MetallicGlossMap", "_SpecGlossMap", "_MaskMap");
        }

        static string MetalProperty(Material material, bool standard, bool urp)
        {
            if ((standard || urp) && !material.IsKeywordEnabled(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP")) return null;
            return Property(material, "_MetallicGlossMap", "_MetallicMap", "_MetalnessMap", "_MaskMap");
        }

        static string Property(Material material, params string[] names)
        {
            if (!material) return null;
            foreach (string name in names) if (IsTexture(material, name) && material.GetTexture(name)) return name;
            return null;
        }

        static string DeclaredProperty(Material material, string name) => material && material.HasProperty(name) ? name : null;

        static bool IsTexture(Material material, string name)
        {
            int index = material.shader ? material.shader.FindPropertyIndex(name) : -1;
            return index >= 0 && material.shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Texture;
        }

        static float Value(Material material, float fallback, params string[] names)
        {
            if (material) foreach (string name in names) if (material.HasProperty(name)) return material.GetFloat(name);
            return fallback;
        }

        static Color Tint(Material material)
        {
            if (!material) return new Color(.72f, .72f, .72f, 1);
            if (material.HasProperty("_BaseColor")) return material.GetColor("_BaseColor");
            return material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
        }
    }
}
