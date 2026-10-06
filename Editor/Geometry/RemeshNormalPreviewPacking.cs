using System;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Packs canonical RGB normals for the active Lit shader's texture decoder.
    /// Exported PNGs remain canonical; their NormalMap importer performs that packing.</summary>
    internal static class RemeshNormalPreviewPacking
    {
        internal static NormalMapEncoding ActiveEncoding()
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            return PlayerSettings.GetNormalMapEncoding(UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group));
        }

        /// <summary>The caller owns the returned GPU texture and must release/destroy it.</summary>
        internal static RenderTexture Create(Texture canonical, bool urp)
        {
            if (!canonical) throw new ArgumentNullException(nameof(canonical));
            var shader = Shader.Find("Hidden/MeshLab/RemeshNormalPreviewPack");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("Remesh normal-preview packing shader is unavailable.");
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture packed = null;
            var previous = RenderTexture.active;
            try {
                packed = new RenderTexture(canonical.width, canonical.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) {
                    name = "Remesh Lit normal preview", hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                if (!packed.Create()) throw new InvalidOperationException("Remesh normal-preview texture allocation failed.");
                material.SetFloat("_NormalConvention", urp ? 1 : 0);
                Graphics.Blit(canonical, packed, material);
                return packed;
            }
            catch {
                RenderTexture.active = previous;
                if (packed) { packed.Release(); UnityEngine.Object.DestroyImmediate(packed); }
                throw;
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(material); }
        }
    }
}
