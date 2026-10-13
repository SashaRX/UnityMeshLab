using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class TexturePackingFormatIntegrationTests
    {
        // TIFF payloads generated independently by Pillow/libtiff 11.0.0;
        // PSD payload follows the Adobe composite layout and Python zlib encoder.
        private sealed class Fixture
        {
            internal readonly string Name, Extension, Data;
            internal Fixture(string name, string extension, string data)
            { Name = name; Extension = extension; Data = data; }
        }
        private static readonly Fixture[] Fixtures = {
            new Fixture("raw", ".tiff", "SUkqAAgAAAALAAABBAABAAAAAgAAAAEBBAABAAAAAgAAAAIBAwAEAAAAkgAAAAMBAwABAAAAAQAAAAYBAwABAAAAAgAAABEBBAABAAAAmgAAABUBAwABAAAABAAAABYBBAABAAAAAgAAABcBBAABAAAAEAAAABwBAwABAAAAAQAAAFIBAwABAAAAAgAAAAAAAAAIAAgACAAIAAoyWgAUPGRAHkZugChQeP8="),
            new Fixture("packbits", ".tiff", "SUkqABoAAAAHCjJaABQ8ZEAHHkZugChQeP8LAAABAwABAAAAAgAAAAEBAwABAAAAAgAAAAIBAwAEAAAApAAAAAMBAwABAAAABYAAAAYBAwABAAAAAgAAABEBBAABAAAACAAAABUBAwABAAAABAAAABYBAwABAAAAAgAAABcBBAABAAAAEgAAABwBAwABAAAAAQAAAFIBAwABAAAAAgAAAAAAAAAIAAgACAAIAA=="),
            new Fixture("tiff_lzw", ".tiff", "SUkqAB4AAACAAoZFoABQeGQgB4jG5ACgoHh/wEAACwAAAQMAAQAAAAIAAAABAQMAAQAAAAIAAAACAQMABAAAAKgAAAADAQMAAQAAAAUAAAAGAQMAAQAAAAIAAAARAQQAAQAAAAgAAAAVAQMAAQAAAAQAAAAWAQMAAQAAAAIAAAAXAQQAAQAAABUAAAAcAQMAAQAAAAEAAABSAQMAAQAAAAIAAAAAAAAACAAIAAgACAA="),
            new Fixture("tiff_adobe_deflate", ".tiff", "SUkqACAAAAB4nOMyimIQsUlxkHPLa9AIqPgPAB2jBMwLAAABAwABAAAAAgAAAAEBAwABAAAAAgAAAAIBAwAEAAAAqgAAAAMBAwABAAAACAAAAAYBAwABAAAAAgAAABEBBAABAAAACAAAABUBAwABAAAABAAAABYBAwABAAAAAgAAABcBBAABAAAAGAAAABwBAwABAAAAAQAAAFIBAwABAAAAAgAAAAAAAAAIAAgACAAIAA=="),
            new Fixture("psd_zip", ".psd", "OEJQUwABAAAAAAAAAAQAAAACAAAAAgAIAAMAAAAAAAAAAAAAAAAAAnic4xKR0zCycQuISsmrYHBo+A8AHTcEzA=="),
        };
        private static readonly Color32[] Expected = {
            new Color32(30, 70, 110, 128), new Color32(40, 80, 120, 255),
            new Color32(10, 50, 90, 0), new Color32(20, 60, 100, 64)
        };

        [UnityTest]
        public IEnumerator ExternalPsdAndTiffLoadWithoutImportAndPreserveEveryChannel()
        {
            foreach (var fixture in Fixtures)
            {
                string path = Path.Combine(Path.GetTempPath(), "MeshLabFormat-" + Guid.NewGuid().ToString("N") + fixture.Extension);
                byte[] bytes = Convert.FromBase64String(fixture.Data);
                File.WriteAllBytes(path, bytes);
                try
                {
                    using (var load = new TexturePackingLoad(path))
                    {
                        double deadline = EditorApplication.timeSinceStartup + 15;
                        TexturePackingImage image;
                        while (!load.TryComplete(out image))
                        {
                            Assert.Less(EditorApplication.timeSinceStartup, deadline, fixture.Name);
                            yield return null;
                        }
                        CollectionAssert.AreEqual(Expected, image.Pixels, fixture.Name);
                        var packed = TexturePackingImage.Compose(2, 2, image, new TexturePackingImage[4],
                            new int[4], new byte[4], true);
                        using (var output = new MemoryStream())
                        {
                            packed.WriteTgaRle(output);
                            CollectionAssert.AreEqual(Expected, TexturePackingImage.DecodeTga(output.ToArray()).Pixels, fixture.Name);
                        }
                    }
                    CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
                    Assert.IsFalse(File.Exists(path + ".meta"), fixture.Name + " must not be imported.");
                }
                finally { File.Delete(path); }
            }
        }
    }
}
