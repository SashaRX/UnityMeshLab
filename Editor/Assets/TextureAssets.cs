// TextureAssets.cs — pixels in and out of textures and texture files: a Texture2D from
// a pixel buffer (previews, encodes), PNG/EXR encoding through a transient texture, a
// file written with its directory, a PNG read back to pixels, and the importer
// configuration of a written map (colour, linear data or normal map). The remesh
// exporter, the remesh previews, the hierarchical repack diagnostics and the UV PNG
// writer each created their transient textures and wrote their files by hand.
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TextureAssets
    {
        /// <summary>What a written map holds, for its importer.</summary>
        internal enum Kind
        {
            /// <summary>sRGB colour (albedo).</summary>
            Color,
            /// <summary>Linear data (metallic/smoothness, occlusion, emission).</summary>
            Linear,
            /// <summary>A tangent-space normal map.</summary>
            NormalMap,
        }

        // ── textures from pixels ──

        /// <summary>
        /// An RGBA32 texture holding <paramref name="pixels"/> (row-major, bottom row
        /// first, as Unity stores them), applied and flagged HideAndDontSave: for a
        /// preview or an encode, never an asset. <paramref name="linear"/> marks the data
        /// as not sRGB. The caller destroys it.
        /// </summary>
        internal static Texture2D FromPixels(Color32[] pixels, int width, int height, bool linear, bool mipmaps = false)
        {
            if (pixels == null || pixels.Length != width * height) return null;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipmaps, linear) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        // ── encoding ──

        /// <summary>The pixels as a PNG, through a transient texture that is destroyed again.</summary>
        internal static byte[] EncodePng(Color32[] pixels, int width, int height)
        {
            var texture = FromPixels(pixels, width, height, linear: true);
            if (texture == null) return null;
            try { return texture.EncodeToPNG(); }
            finally { Object.DestroyImmediate(texture); }
        }

        /// <summary>The HDR pixels as a float EXR, through a transient texture that is destroyed again.</summary>
        internal static byte[] EncodeExr(Color[] pixels, int width, int height)
        {
            if (pixels == null || pixels.Length != width * height) return null;
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                texture.SetPixels(pixels);
                texture.Apply();
                return texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        // ── files ──

        /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>, creating the directory.</summary>
        internal static void WriteFile(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path) || bytes == null) return;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }

        /// <summary>Encodes the pixels to PNG and writes the file (<see cref="EncodePng"/> + <see cref="WriteFile"/>).</summary>
        internal static void WritePng(string path, Color32[] pixels, int width, int height)
            => WriteFile(path, EncodePng(pixels, width, height));

        /// <summary>
        /// The pixels of a PNG or JPG file (bottom row first), with its size; null when
        /// the file is missing or does not decode.
        /// </summary>
        internal static Color32[] ReadImage(string path, out int width, out int height)
        {
            width = height = 0;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path))) return null;
                width = texture.width;
                height = texture.height;
                return texture.GetPixels32();
            }
            finally { Object.DestroyImmediate(texture); }
        }

        // ── importers ──

        /// <summary>
        /// Sets up the importer of a map the tool wrote: texture type and sRGB from
        /// <paramref name="kind"/>, clamp wrapping, the given maximum size, no
        /// compression, mipmaps, alpha from the file; then reimports. Throws when the
        /// path has no texture importer (the write or import failed).
        /// </summary>
        internal static void Configure(string assetPath, Kind kind, int maxSize, bool mipmaps = true)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) throw new IOException("Texture import failed: " + assetPath);
            importer.textureType = kind == Kind.NormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = kind == Kind.Color;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = maxSize;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = mipmaps;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.SaveAndReimport();
        }
    }
}
