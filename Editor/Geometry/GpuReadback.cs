using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The one way this package reads texture data back from the GPU: blit the
    /// source (through an optional material, optionally a sub-rectangle) into a
    /// temporary linear render texture and ReadPixels it into a CPU copy. Every
    /// capture — source maps, lightmap regions, probe cubemaps — goes through here
    /// so the format, colour-space and cleanup rules live in one place. Main thread
    /// only (GPU access); the pixel arrays it returns are free to use on workers.
    /// </summary>
    internal static class GpuReadback
    {
        /// <summary>
        /// A readable copy of <paramref name="source"/> at width × height. hdr selects
        /// RGBAFloat (linear floats) over RGBA32. material (optional) is the blit
        /// shader; scale/offset (optional) sample the source at uv × scale + offset, so
        /// a sub-rectangle can be read without reading the whole texture. The caller
        /// owns the returned texture (destroy it when done); null when the blit failed.
        /// </summary>
        public static Texture2D Read(Texture source, int width, int height, bool hdr, Material material = null,
            Vector2? scale = null, Vector2? offset = null)
        {
            if (!source || width < 1 || height < 1) return null;
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 0, hdr ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try {
                if (material) Graphics.Blit(source, rt, material);
                else if (scale.HasValue || offset.HasValue) Graphics.Blit(source, rt, scale ?? Vector2.one, offset ?? Vector2.zero);
                else Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(width, height, hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return copy;
            }
            catch (System.Exception) {
                if (copy) Object.DestroyImmediate(copy);
                return null;
            }
            finally {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Linear float pixels of the source (or of its sub-rectangle) at width × height; null when the blit failed.</summary>
        public static Color[] ReadColors(Texture source, int width, int height, Material material = null,
            Vector2? scale = null, Vector2? offset = null)
        {
            var copy = Read(source, width, height, true, material, scale, offset);
            if (!copy) return null;
            try { return copy.GetPixels(); }
            finally { Object.DestroyImmediate(copy); }
        }
    }
}
