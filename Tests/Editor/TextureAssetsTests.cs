// TextureAssetsTests.cs — pixels through textures and files.
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class TextureAssetsTests
    {
        static Color32[] Gradient(int w, int h)
        {
            var p = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    p[y * w + x] = new Color32((byte)(x * 255 / (w - 1)), (byte)(y * 255 / (h - 1)), 128, 255);
            return p;
        }

        [Test]
        public void FromPixelsBuildsAnAppliedHiddenTextureAndRejectsSizeMismatches()
        {
            var tex = TextureAssets.FromPixels(Gradient(4, 2), 4, 2, linear: true);
            try
            {
                Assert.AreEqual(4, tex.width);
                Assert.AreEqual(2, tex.height);
                Assert.AreEqual(HideFlags.HideAndDontSave, tex.hideFlags);
                Assert.AreEqual(new Color32(255, 0, 128, 255), tex.GetPixels32()[3]);
            }
            finally { Object.DestroyImmediate(tex); }
            Assert.IsNull(TextureAssets.FromPixels(Gradient(4, 2), 2, 2, linear: false));
            Assert.IsNull(TextureAssets.EncodePng(null, 1, 1));
            Assert.IsNull(TextureAssets.EncodeExr(new Color[3], 2, 2));
        }

        [Test]
        public void PngRoundTripsThroughTheFileAndCreatesTheDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "MeshLabTextureAssets_" + System.Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "nested", "grad.png");
            try
            {
                var pixels = Gradient(8, 4);
                TextureAssets.WritePng(path, pixels, 8, 4);
                Assert.IsTrue(File.Exists(path));

                var back = TextureAssets.ReadImage(path, out int w, out int h);
                Assert.AreEqual(8, w);
                Assert.AreEqual(4, h);
                Assert.AreEqual(pixels, back, "PNG is lossless");

                Assert.IsNull(TextureAssets.ReadImage(Path.Combine(dir, "missing.png"), out _, out _));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Test]
        public void ExrEncodesHdrValues()
        {
            var hdr = new[] { new Color(4f, 0.5f, 0f, 1f), Color.black, Color.white, new Color(0f, 0f, 16f, 1f) };
            var bytes = TextureAssets.EncodeExr(hdr, 2, 2);
            Assert.IsNotNull(bytes);
            Assert.Greater(bytes.Length, 16);
            // OpenEXR magic number
            Assert.AreEqual(new byte[] { 0x76, 0x2f, 0x31, 0x01 }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });
        }
    }
}
