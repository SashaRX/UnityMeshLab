// RendererSettings.cs — copying a renderer's rendering settings onto another renderer,
// the one implementation behind the FBX export hierarchy, LOD generation and
// split/merge (each used to carry its own subset).
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class RendererSettings
    {
        /// <summary>
        /// Copies shadows, probes, motion vectors, occlusion, layers, priority and (for
        /// MeshRenderers) the lightmap settings from <paramref name="src"/> to
        /// <paramref name="dst"/>. With <paramref name="includeMaterials"/> the material
        /// array comes along too, unless the source currently wears a preview shader (a
        /// checker or shell-colour preview must never end up in a saved asset).
        /// </summary>
        internal static void Copy(Renderer src, Renderer dst, bool includeMaterials = true)
        {
            if (src == null || dst == null) return;

            if (includeMaterials)
            {
                var srcMats = src.sharedMaterials;
                bool hasPreviewMat = false;
                for (int i = 0; i < srcMats.Length; i++)
                {
                    var m = srcMats[i];
                    string shaderName = m != null && m.shader != null ? m.shader.name : null;
                    if (CheckerTexturePreview.IsPreviewShader(shaderName)) { hasPreviewMat = true; break; }
                }
                if (!hasPreviewMat) dst.sharedMaterials = srcMats;
            }

            dst.shadowCastingMode = src.shadowCastingMode;
            dst.receiveShadows = src.receiveShadows;
            dst.lightProbeUsage = src.lightProbeUsage;
            dst.reflectionProbeUsage = src.reflectionProbeUsage;
            dst.probeAnchor = src.probeAnchor;
            dst.motionVectorGenerationMode = src.motionVectorGenerationMode;
            dst.allowOcclusionWhenDynamic = src.allowOcclusionWhenDynamic;
            dst.renderingLayerMask = src.renderingLayerMask;
            dst.rendererPriority = src.rendererPriority;
            dst.lightmapIndex = src.lightmapIndex;
            dst.realtimeLightmapIndex = src.realtimeLightmapIndex;

            if (src is MeshRenderer srcMr && dst is MeshRenderer dstMr)
            {
                dstMr.receiveGI = srcMr.receiveGI;
                dstMr.scaleInLightmap = srcMr.scaleInLightmap;
                dstMr.stitchLightmapSeams = srcMr.stitchLightmapSeams;
                dstMr.lightmapScaleOffset = srcMr.lightmapScaleOffset;
                dstMr.realtimeLightmapScaleOffset = srcMr.realtimeLightmapScaleOffset;
            }
        }
    }
}
