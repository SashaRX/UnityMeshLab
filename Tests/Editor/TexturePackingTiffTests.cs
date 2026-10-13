using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>Unit coverage for the TIFF stream decoder: compression, prediction, planar/tiled layout, orientation, cancellation and malformed input.</summary>
    public sealed class TexturePackingTiffTests
    {
        [TestCase(false)]
        [TestCase(true)]
        /// <summary>RGBA strips decode with their alpha intact and rows flipped to Unity's bottom-left origin, for both byte orders.</summary>
        public void RgbaStripsPreserveAlphaAndConvertToBottomLeft(bool bigEndian)
        {
            var fixture = new Fixture(2, 2, 4) { BigEndian = bigEndian, Rows = 1, Alpha = 2 };
            fixture.Blocks.Add(new byte[] { 1, 2, 3, 4, 10, 20, 30, 40 });
            fixture.Blocks.Add(new byte[] { 50, 60, 70, 80, 90, 100, 110, 120 });
            TexturePackingImage image = Decode(fixture);
            Assert.AreEqual(new Color32(50, 60, 70, 80), image.Pixels[0]);
            Assert.AreEqual(new Color32(90, 100, 110, 120), image.Pixels[1]);
            Assert.AreEqual(new Color32(1, 2, 3, 4), image.Pixels[2]);
            Assert.AreEqual(new Color32(10, 20, 30, 40), image.Pixels[3]);
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(8)]
        [TestCase(32946)]
        [TestCase(32773)]
        /// <summary>Every supported compression scheme decodes a strip back to the original pixels.</summary>
        public void SupportedCompressionProducesIdenticalPixels(int compression)
        {
            var fixture = new Fixture(2, 1, 3) { Compression = compression };
            fixture.Blocks.Add(new byte[] { 25, 50, 100, 200, 150, 75 });
            TexturePackingImage image = Decode(fixture);
            CollectionAssert.AreEqual(new[] { new Color32(25, 50, 100, 255), new Color32(200, 150, 75, 255) }, image.Pixels);
        }

        [TestCase(false)]
        [TestCase(true)]
        /// <summary>Horizontal differencing prediction on 16-bit samples undoes deltas correctly and resets at the start of each row, for both byte orders.</summary>
        public void HorizontalPredictionUsesSixteenBitSamplesAndResetsAtRows(bool bigEndian)
        {
            var fixture = new Fixture(2, 2, 3) { Bits = 16, BigEndian = bigEndian, Predictor = 2, Compression = 8 };
            fixture.Blocks.Add(Words(bigEndian, 0, 32768, 65535, 65535, 32767, 1,
                65535, 0, 32768, 1, 32768, 32767));
            TexturePackingImage image = Decode(fixture);
            CollectionAssert.AreEqual(new[] { new Color32(255, 0, 128, 255), new Color32(0, 128, 255, 255),
                new Color32(0, 128, 255, 255), new Color32(255, 255, 0, 255) }, image.Pixels);
        }

        [Test]
        /// <summary>8-bit horizontal prediction combined with PackBits no-op and repeat packets decodes to the expected running sums.</summary>
        public void EightBitPredictorAndPackBitsRepeatPacketsDecode()
        {
            var fixture = new Fixture(3, 1, 1) { Photo = 1, Predictor = 2, Compression = 32773, BlocksEncoded = true };
            // 128 is a no-op; literal 10 followed by two repeated differences of 20.
            fixture.Blocks.Add(new byte[] { 128, 0, 10, 255, 20 });
            TexturePackingImage image = Decode(fixture);
            CollectionAssert.AreEqual(new[] { new Color32(10, 10, 10, 255), new Color32(30, 30, 30, 255),
                new Color32(50, 50, 50, 255) }, image.Pixels);
        }

        [Test]
        /// <summary>Planar, associated 16-bit alpha unmultiplies using full native precision before quantizing to bytes.</summary>
        public void PlanarAssociatedAlphaUsesOriginalSixteenBitPrecision()
        {
            var fixture = new Fixture(2, 1, 4) { Planar = true, Bits = 16, Alpha = 1, Compression = 5 };
            fixture.Blocks.Add(Words(false, 128, 0));
            fixture.Blocks.Add(Words(false, 64, 0));
            fixture.Blocks.Add(Words(false, 32, 0));
            fixture.Blocks.Add(Words(false, 256, 0));
            TexturePackingImage image = Decode(fixture);
            Assert.AreEqual(new Color32(128, 64, 32, 1), image.Pixels[0]);
            Assert.AreEqual(new Color32(0, 0, 0, 0), image.Pixels[1]);
        }

        [Test]
        /// <summary>Tiles wider than the image are cropped to the real width without shifting pixels on the right edge.</summary>
        public void TilesCropPaddingWithoutShiftingTheRightEdge()
        {
            var fixture = new Fixture(3, 2, 1) { Photo = 1, TileWidth = 2, TileHeight = 2, Compression = 8 };
            fixture.Blocks.Add(new byte[] { 10, 20, 40, 50 });
            fixture.Blocks.Add(new byte[] { 30, 222, 60, 222 });
            TexturePackingImage image = Decode(fixture);
            CollectionAssert.AreEqual(new byte[] { 40, 50, 60, 10, 20, 30 }, image.Pixels.Select(pixel => pixel.r).ToArray());
        }

        [TestCase(1, new byte[] { 4, 5, 6, 1, 2, 3 }, 3, 2)]
        [TestCase(2, new byte[] { 6, 5, 4, 3, 2, 1 }, 3, 2)]
        [TestCase(3, new byte[] { 3, 2, 1, 6, 5, 4 }, 3, 2)]
        [TestCase(4, new byte[] { 1, 2, 3, 4, 5, 6 }, 3, 2)]
        [TestCase(5, new byte[] { 3, 6, 2, 5, 1, 4 }, 2, 3)]
        [TestCase(6, new byte[] { 6, 3, 5, 2, 4, 1 }, 2, 3)]
        [TestCase(7, new byte[] { 4, 1, 5, 2, 6, 3 }, 2, 3)]
        [TestCase(8, new byte[] { 1, 4, 2, 5, 3, 6 }, 2, 3)]
        /// <summary>Every TIFF orientation value remaps pixels to the correct upright, bottom-left-origin layout and output dimensions.</summary>
        public void AllOrientationsProduceUprightBottomLeftPixels(int orientation, byte[] expected, int width, int height)
        {
            var fixture = new Fixture(3, 2, 1) { Photo = 1, Orientation = orientation };
            fixture.Blocks.Add(new byte[] { 1, 2, 3, 4, 5, 6 });
            TexturePackingImage image = Decode(fixture);
            Assert.AreEqual(width, image.Width);
            Assert.AreEqual(height, image.Height);
            CollectionAssert.AreEqual(expected, image.Pixels.Select(pixel => pixel.r).ToArray());
        }

        [TestCase(false)]
        [TestCase(true)]
        /// <summary>32-bit float grayscale with alpha clamps out-of-range and NaN samples while preserving transparency, for both byte orders.</summary>
        public void FloatingPointGrayscaleAlphaClampsAndPreservesTransparency(bool bigEndian)
        {
            var fixture = new Fixture(2, 1, 2) { Bits = 32, Float = true, Photo = 1, Alpha = 2, BigEndian = bigEndian };
            fixture.Blocks.Add(Floats(bigEndian, 0.5f, 0.25f, 2, float.NaN));
            TexturePackingImage image = Decode(fixture);
            Assert.AreEqual(new Color32(128, 128, 128, 64), image.Pixels[0]);
            Assert.AreEqual(new Color32(255, 255, 255, 0), image.Pixels[1]);
        }

        [Test]
        /// <summary>Photometric interpretation 0 (WhiteIsZero) inverts grayscale color but leaves alpha untouched.</summary>
        public void WhiteIsZeroGrayscaleInvertsOnlyColor()
        {
            var fixture = new Fixture(1, 1, 2) { Photo = 0, Alpha = 2 };
            fixture.Blocks.Add(new byte[] { 25, 64 });
            Assert.AreEqual(new Color32(230, 230, 230, 64), Decode(fixture).Pixels[0]);
        }

        [TestCase(-1)]
        [TestCase(0)]
        /// <summary>An extra sample that is not flagged as alpha is ignored, leaving the pixel fully opaque.</summary>
        public void UnspecifiedExtraSampleDoesNotBecomeAlpha(int extra)
        {
            var fixture = new Fixture(1, 1, 4) { Alpha = extra };
            fixture.Blocks.Add(new byte[] { 10, 20, 30, 0 });
            Assert.AreEqual(new Color32(10, 20, 30, 255), Decode(fixture).Pixels[0]);
        }

        [Test]
        /// <summary>An LZW code equal to the next dictionary entry (the KwKwK case) decodes using the previous string plus its own first byte.</summary>
        public void LzwHandlesTheNextDictionaryEntryCase()
        {
            var fixture = new Fixture(3, 1, 1) { Photo = 1, Compression = 5, BlocksEncoded = true };
            fixture.Blocks.Add(PackCodes(new[] { 256, 65, 258, 257 }, new[] { 9, 9, 9, 9 }));
            CollectionAssert.AreEqual(new byte[] { 65, 65, 65 }, Decode(fixture).Pixels.Select(pixel => pixel.r).ToArray());
        }

        [Test]
        /// <summary>LZW decoding stays aligned across the code-width growth thresholds and an in-stream dictionary clear over many samples.</summary>
        public void LzwEarlyCodeWidthTransitionsAndDictionaryClearRemainAligned()
        {
            var fixture = new Fixture(2200, 1, 1) { Photo = 1, Compression = 5 };
            byte[] values = Enumerable.Range(0, 2200).Select(i => (byte)(i * 79)).ToArray();
            fixture.Blocks.Add(values);
            CollectionAssert.AreEqual(values, Decode(fixture).Pixels.Select(pixel => pixel.r).ToArray());
        }

        [Test]
        /// <summary>An LZW stream produced independently by libtiff/Pillow, spanning every code width, decodes to the expected pixel bytes.</summary>
        public void IndependentLibTiffLzwFixtureCrossesAllCodeWidths()
        {
            // Generated by Pillow 11/libtiff, not by the decoder or the literal fixture encoder.
            var fixture = new Fixture(32, 256, 1) { Photo = 1, Compression = 5, BlocksEncoded = true };
            fixture.Blocks.Add(Convert.FromBase64String(
                "gAAJKSbYkNq2f5ISLaEZsWr+I6QbIiNa0fpGR7YEJqWb8IqOa4gNKyfZERrWD5oWL6IaMaoeM6wfJCRbUDpmV74IKKaYcMque5AR" +
                "LSDZkVr2H6IaIaMaseo+Q7QDJiVb0HqGZ4YMKqeY8QrOC5gVLyHaEZoWL6oeI6QbMCpeU7wHKCZYULqmd44QLKCZcUruG6AZISLa" +
                "kdo2P7ICJaUbsGp+Y4QLKidY0PrGB5YULqGZ8YoOK6gdIyPbEBpWT7oGJ6YYMKqec4wPLCBZUTrmF54YIKKaccouO7ABJSTbkFp2" +
                "X4IKKacYsOq+A5QTLiFZ0XoGJ6YcIqOa8ApOS7gFJyXYEJqWb4oOK6AZMSreE5wXICJaUbomN64AJKmU0mycJ0nifKAoSiKMpClK" +
                "YpyoKkqirKwrSuK8sCxLIsy0LUti3LguS6LsvC9L4vzAMEwjDMQxTGMcyDJMoyzMM0zjPNA0TSNM1DVNY1zYNk2jbNw3TeN84DhO" +
                "I4zkOU5jnOg6TqOs7DtO47zwPE8jzPQ9T2Pc+D5Po+z8P0JRJm4Eo3FugSCIMhCFIYhyIIkiiLIwjSOI8kCRJIkyUJUfUKrKs60r" +
                "Wtq3riua6ruvK9r6v7AsGwrDsSxbGseyLJsqy7Ms2zrPtC0bStO1LVta17Ytm2rbty3bet+4LhuK47kuW5rnui6bquu7Ltu677wv" +
                "G8rzvS9b2ve+L5vq+78v3McyzOgaCoOhKFoah6IomiqLoyjaOo+kKRpKk6UpWlqXpimaapunKdp6n6gqGoqjqSpamqeqKpqqq6sq" +
                "3FtLxhTUZ07G1QRzUceVNH9UyFVki1fJFZSXWsnVxKNdypX0r2DLViS7Y8wWVMkzTRZ812lN1qzjbE6W3O9vT1cKWJcmD+3PAF1Q" +
                "HdsDXhBN5wZe0H3zCSvrCsc/wxQUN0LD1ERDRcSUdE9Ii1hcj1jJVaSbW8oV1KdeytYEs2HLljS/ZMxY/Zs02hNlpzfa05WzOtuT" +
                "xb893FmFyv9dEA3XAl3QPeMFXpBt7wgrau6DP0L0DDVCQ7Q8QUVEdGxNSEU0nFlLRfTMZU5GtPxxUUd1LH1USDjexzDZeQWdNVoz" +
                "bak4WvOdtTtbs83BPlx5jcz/3TAV2QLd8EXlBd6wdfEI8NCmh8TDNBw5Q0P0TEVGRLR8UUlFdKxdTEY03GlPRvUMdVJHtTyBVUh1" +
                "bI1YSTWcmVtJ9cylXkq1/LD17llvcbtmXeb0zZ4DfmdPEcEz55CE2hIWUA81o7jXotLci9Vp7lXsr7cy91f7nXwsDdC+Vg6QVVpE" +
                "Vc1N9rD2rvxYm1t+rF2vpbWKl5ZDqGzMhdY2pkrsG3J0gK8dwsCHEQLaM4x6DSnIPUac5R7ClILPcX85x8DAnQPkYM6R9DCoRvsY" +
                "c1Z+DEmtP0Ys15YULnTQxY8syGjaWSOvbayh2bcWWO3bquR/reWau/b6zkpcEokIqiU5iJjm3vsBc++Ngro3zsJhE+thrVX3sRay" +
                "/NirXX7sZbDDBjrZYzOrjQ65tjJ3ZNwZW7ZujL45O7jo75vjOHhOAZ48ZwjQHlQKaK4t57SXHjKg9FSRD6mGNUfcxBrD8mKNcfsx" +
                "hsEL2ONkdS2dkTrW1smdi29lTtW5sudy3dmbvW9s3eC39nbxXBs/eTAlojinnNIcc9JpjknrNQcs9pfjmnvMAc8+KFcYJKTIdPGV" +
                "1TaGRycmhDmNsoZqv7lK3hmkqJuQCeG4FnsPJYTkeZEGWs6YIRGcm9ePjl3tr9kBPSDcUZCvmYRCGXkJIsyNmClGHEbJQTUf1HF3" +
                "VCJtQAjvKub8O5Xzjh/LOc8DoiPTabRmd0FY/UennBqKEhHRUkhA+lqUWJGTAhRF2SMxYWsabFGSTE/ZmpsgDHiVk4IDQ9cO8uIE" +
                "tJ0QPiLUOdsFI+0dnlBmJ8g2CVNg/FWRMvYSxakdMKFUX5JzHjHJeZcZ5/zPpbJ+ab+Y4SkpnNlANQJbzrj1Rqd8S6kV0kE+Ku8u" +
                "ZD0mqjIuX8J4uSQmJCyMNWpLTKhnJqxMN412MdpY6Uc1450Jm3WGnEBKH07h9WiWdSq7QdinaGqEV7SQmi3I+Yc97Bxiq3Ya18/p" +
                "nWyk9NK2sb7bv8lNbqm0qpvW+ldOK4MspzQNiHZWCMR7MVGrkjG01zrAySmNdK1sMpM3WhtGq7LKbtyimtd6mj/47XigHQ68sB6z" +
                "3ogZEKW06r21ErhRyeMGInWdg5FKQ1JbkyKapYi69/po4AjdgKg1kX/R1lTN3BMrZw4MljOXB9Fa2VCnZBOJOFoLxNkDPXDdI68y" +
                "7tHL65lf4UxevtVmSsyUw25prgfF1DcYVlojT29OEKLVtxzHuzNR654ZyBSKvEurRXKyNX6ldVrUz4sJdO11+6vxpk7iWgdMLH5Y" +
                "xrWuoNlr3VFrjhfH1IKl2fuPh6K2IK+0qqrai6F97WZOn5MyGudKA0usbdzAdB7JYsoXWKnNv2f2azDj+kNTLQaIr3Siqd87AZKq" +
                "xavJs+6u6Uk3Yq2d2sT0FplNjFdCreXjwVjGs2M6J1qsphKjFbzF5F0XVS09z7BaQ1nVyw9sMR51oFS+22m8VSnt3TfYWVaIU8uF" +
                "lnG2fcJ7Mo3PDHtH7iWeuNh10ucL9Ve0rQCxeuqCUxshr7cF4cp1jp1ebBuNKKZ8vZsvHW7dSYY1NoXedTq9UnqlaW5ur6r2qF1t" +
                "3FPALwZSoZwTUWMqJVpp/evZVbuG5fvjoPeOQcy3I0TXylO0L6aw45m+/Ok8RX921S7gvJs9cJ2TRflmXr4aC3hXXeWHOKZEzRs/" +
                "V2SeN5uvxpLWvP9Lb7xNv2x9uLv5RxbyOy+gMeR/qT07mWqan811ZxjJGbNH5Mn1ta6uc99a56/ni7unNf7hwRlSsm5bz8I2RypE" +
                "PbuK7O5v1Xum0+7WFzjvjW92M7bc01x/KGBuy6gvJsTK+5899Hy52fCu7u1WczGHruusu73UzlvnXF/878e1753T2wcX+FuBwfY/" +
                "KcI9Iy7e/QO7+14azJ4zqWivH8ZpZ3z2/m/c9j89p+3uw8rbmwd0bxXp8/+p4fzDtny9D9v1XxfI+a9HeS9h5Te+tp/+88J0PYvJ" +
                "6fXq+H+DCmO/VLNsxNTtDN6PGupvnu5v2slv3t7OfNsOgNLraNdt/OxMCidvTMcPUP/PxumvlNUPzwCvnNWvoPIwFJ8v4QGr+Ouv" +
                "pPNMUPqwKtgNxPev7PRvugdOYvzQCPmubQRQENpQSueOtNrwUu9vbQWNeN/vdQYPBuSMFv7uivEv9wMPwwNMwL5QRwEtYwTQGOtw" +
                "HQVQitMwWwkPrPdwYv6uSwnPSPvQos/P+uHQqvyQOwBuoszwQuuQiPMwwQjwKNOwlOBtQwmwaPEPhMtwpQ2uXOmJ5iAg"));
            byte[] expected = Enumerable.Range(0, 8192).Select(i => (byte)(i * 73 + i / 251 * 29)).ToArray();
            TexturePackingImage image = Decode(fixture);
            for (int row = 0; row < 256; row++)
                for (int x = 0; x < 32; x++)
                    Assert.AreEqual(expected[row * 32 + x], image.Pixels[(255 - row) * 32 + x].r);
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(8)]
        [TestCase(32773)]
        /// <summary>A strip truncated by one byte is rejected, for every supported compression, without the caller's stream being disposed.</summary>
        public void TruncatedBlockThrowsWithoutClosingCallerStream(int compression)
        {
            var fixture = new Fixture(2, 1, 3) { Compression = compression };
            fixture.Blocks.Add(new byte[] { 1, 2, 3, 4, 5, 6 });
            byte[] data = fixture.Build();
            using (var stream = new MemoryStream(data, 0, data.Length - 1))
            {
                Assert.Throws<InvalidDataException>(() => TexturePackingTiff.Decode(stream));
                Assert.IsTrue(stream.CanRead);
            }
        }

        [Test]
        /// <summary>Deflate decoding rejects a stream that expands beyond its declared size and one with a corrupted Adler-32 checksum.</summary>
        public void DeflateRejectsOverExpansionAndChecksumCorruption()
        {
            var fixture = new Fixture(1, 1, 1) { Photo = 1, Compression = 8, BlocksEncoded = true };
            fixture.Blocks.Add(Zlib(new byte[] { 10, 20 }));
            Assert.Throws<InvalidDataException>(() => Decode(fixture));
            fixture.Blocks[0] = Zlib(new byte[] { 10 });
            fixture.Blocks[0][fixture.Blocks[0].Length - 1] ^= 1;
            Assert.Throws<InvalidDataException>(() => Decode(fixture));
        }

        [Test]
        /// <summary>The Adler-32 checksum accumulates correctly across a block large enough to require multiple modulo passes.</summary>
        public void DeflateChecksumAccumulatesAcrossLargeBlocks()
        {
            var fixture = new Fixture(8192, 1, 1) { Photo = 1, Compression = 8 };
            byte[] expected = Enumerable.Range(0, 8192).Select(i => (byte)(i * 199)).ToArray();
            fixture.Blocks.Add(expected);
            CollectionAssert.AreEqual(expected, Decode(fixture).Pixels.Select(pixel => pixel.r).ToArray());
        }

        [Test]
        /// <summary>A PackBits packet and an LZW code that would overflow their block are both rejected instead of corrupting adjacent memory.</summary>
        public void MalformedCompressionPacketsCannotWriteBeyondTheirBlock()
        {
            var fixture = new Fixture(1, 1, 1) { Photo = 1, Compression = 32773, BlocksEncoded = true };
            fixture.Blocks.Add(new byte[] { 255, 42 });
            Assert.Throws<InvalidDataException>(() => Decode(fixture));
            fixture.Compression = 5;
            fixture.Blocks[0] = PackCodes(new[] { 256, 300, 257 }, new[] { 9, 9, 9 });
            Assert.Throws<InvalidDataException>(() => Decode(fixture));
        }

        [Test]
        /// <summary>A directory entry whose out-of-line value offset is corrupted is rejected before any pixel buffer is allocated.</summary>
        public void InvalidTagRangeIsRejectedBeforePixelAllocation()
        {
            var fixture = new Fixture(1, 1, 4) { Alpha = 2 };
            fixture.Blocks.Add(new byte[] { 10, 20, 30, 40 });
            byte[] data = fixture.Build();
            // BitsPerSample has four SHORT values, stored outside its directory entry.
            int entry = fixture.TagEntryOffset(258);
            for (int i = 0; i < 4; i++) data[entry + 8 + i] = 255;
            using (var stream = new MemoryStream(data))
                Assert.Throws<InvalidDataException>(() => TexturePackingTiff.Decode(stream));
        }

        [Test]
        /// <summary>An image over the 64 megapixel limit and planar data missing one sample plane both fail before pixels are allocated.</summary>
        public void OversizedImagesAndIncompletePlanarBlocksFailBeforeAllocatingPixels()
        {
            var oversized = new Fixture(8192, 8193, 1) { Photo = 1 };
            oversized.Blocks.Add(new byte[] { 10 });
            var error = Assert.Throws<InvalidDataException>(() => Decode(oversized));
            StringAssert.Contains("64 megapixel", error.Message);
            var incomplete = new Fixture(1, 1, 4) { Planar = true, Alpha = 2 };
            incomplete.Blocks.Add(new byte[] { 10 });
            Assert.Throws<InvalidDataException>(() => Decode(incomplete));
        }

        [TestCase(7, 8, 2, 1, false)]
        [TestCase(1, 8, 5, 1, false)]
        [TestCase(1, 32, 2, 3, true)]
        [TestCase(1, 8, 2, 1, true)]
        /// <summary>Unsupported compression, photometric interpretation, predictor and float/predictor combinations each raise a clear "TIFF" error.</summary>
        public void UnsupportedFeaturesGiveActionableErrors(int compression, int bits, int photo, int predictor, bool floatingPoint)
        {
            var fixture = new Fixture(1, 1, 3)
                { Compression = compression, Bits = bits, Photo = photo, Predictor = predictor, Float = floatingPoint, BlocksEncoded = true };
            fixture.Blocks.Add(new byte[12]);
            var error = Assert.Throws<NotSupportedException>(() => Decode(fixture));
            StringAssert.Contains("TIFF", error.Message);
        }

        [Test]
        /// <summary>A BigTIFF version marker is rejected with guidance to resave as a classic TIFF.</summary>
        public void BigTiffGivesResaveGuidance()
        {
            using (var stream = new MemoryStream(new byte[] { 73, 73, 43, 0, 8, 0, 0, 0 }))
            {
                var error = Assert.Throws<NotSupportedException>(() => TexturePackingTiff.Decode(stream));
                StringAssert.Contains("Resave", error.Message);
            }
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(8)]
        [TestCase(32773)]
        /// <summary>Cancellation during a compressed read throws and leaves the caller's stream open, for every supported compression.</summary>
        public void DecodeHonorsCancellationDuringCompressedReads(int compression)
        {
            var fixture = new Fixture(4000, 1, 1) { Photo = 1, Compression = compression };
            fixture.Blocks.Add(Enumerable.Range(0, 4000).Select(i => (byte)i).ToArray());
            byte[] data = fixture.Build();
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new CancelingStream(data, fixture.FirstBlockOffset, cancellation))
            {
                Assert.Throws<OperationCanceledException>(() => TexturePackingTiff.Decode(stream, cancellation.Token));
                Assert.IsTrue(stream.CanRead);
            }
        }

        [Test]
        /// <summary>An already-cancelled token throws before any byte is read from the source stream.</summary>
        public void AlreadyCanceledDecodeDoesNotReadItsInput()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new MemoryStream(new byte[8]))
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => TexturePackingTiff.Decode(stream, cancellation.Token));
                Assert.AreEqual(0, stream.Position);
            }
        }

        /// <summary>Builds the fixture's file bytes and decodes them with <see cref="TexturePackingTiff.Decode"/>.</summary>
        private static TexturePackingImage Decode(Fixture fixture)
        {
            using (var input = new MemoryStream(fixture.Build())) return TexturePackingTiff.Decode(input);
        }

        /// <summary>Encodes 16-bit values as raw bytes in the given byte order.</summary>
        private static byte[] Words(bool bigEndian, params ushort[] values)
        {
            using (var output = new MemoryStream())
            {
                foreach (ushort value in values) WriteInteger(output, value, 2, bigEndian);
                return output.ToArray();
            }
        }

        /// <summary>Encodes 32-bit float values as raw bytes in the given byte order.</summary>
        private static byte[] Floats(bool bigEndian, params float[] values)
        {
            using (var output = new MemoryStream())
            {
                foreach (float value in values)
                {
                    byte[] bytes = BitConverter.GetBytes(value);
                    if (BitConverter.IsLittleEndian == bigEndian) Array.Reverse(bytes);
                    output.Write(bytes, 0, bytes.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>Wraps raw bytes in a valid zlib header, Deflate payload and Adler-32 trailer.</summary>
        private static byte[] Zlib(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78); output.WriteByte(0x9c);
                using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
                    deflate.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (byte value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
                WriteInteger(output, b << 16 | a, 4, true);
                return output.ToArray();
            }
        }

        /// <summary>Encodes bytes as literal (uncompressed-ratio) TIFF LZW codes, inserting clear codes at the dictionary limit.</summary>
        private static byte[] LiteralLzw(byte[] data)
        {
            var codes = new List<int> { 256 };
            var widths = new List<int> { 9 };
            int width = 9, next = 258, prior = -1;
            foreach (byte value in data)
            {
                codes.Add(value); widths.Add(width);
                if (prior >= 0)
                {
                    next++;
                    if (width < 12 && next == (1 << width) - 1) width++;
                }
                prior = value;
                if (next == 2048)
                {
                    codes.Add(256); widths.Add(width);
                    width = 9; next = 258; prior = -1;
                }
            }
            codes.Add(257); widths.Add(width);
            return PackCodes(codes, widths);
        }

        /// <summary>Packs a sequence of variable-width codes MSB-first into bytes, as TIFF LZW expects.</summary>
        private static byte[] PackCodes(IList<int> codes, IList<int> widths)
        {
            using (var output = new MemoryStream())
            {
                uint accumulator = 0;
                int available = 0;
                for (int i = 0; i < codes.Count; i++)
                {
                    accumulator = accumulator << widths[i] | (uint)codes[i];
                    available += widths[i];
                    while (available >= 8)
                    {
                        available -= 8;
                        output.WriteByte((byte)(accumulator >> available));
                    }
                }
                if (available != 0) output.WriteByte((byte)(accumulator << (8 - available)));
                return output.ToArray();
            }
        }

        /// <summary>Encodes bytes as literal (uncompressed) PackBits packets of up to 128 bytes each.</summary>
        private static byte[] LiteralPackBits(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                for (int offset = 0; offset < data.Length; offset += 128)
                {
                    int count = Math.Min(128, data.Length - offset);
                    output.WriteByte((byte)(count - 1));
                    output.Write(data, offset, count);
                }
                return output.ToArray();
            }
        }

        /// <summary>Writes an integer's low <paramref name="bytes"/> bytes to the stream in the given byte order.</summary>
        private static void WriteInteger(Stream output, uint value, int bytes, bool bigEndian)
        {
            for (int i = 0; i < bytes; i++) output.WriteByte((byte)(value >> ((bigEndian ? bytes - 1 - i : i) * 8)));
        }

        /// <summary>Builds a classic TIFF file byte-for-byte from configurable tags and raw strip/tile blocks for test fixtures.</summary>
        private sealed class Fixture
        {
            private readonly int width, height, samples;
            private List<Tag> tags;
            internal readonly List<byte[]> Blocks = new List<byte[]>();
            internal bool BigEndian, Planar, Float, BlocksEncoded;
            internal int Bits = 8, Photo = 2, Compression = 1, Predictor = 1, Orientation = 1, Alpha = -1;
            internal int Rows, TileWidth, TileHeight;
            internal int FirstBlockOffset { get; private set; }
            /// <summary>Starts a fixture with the given dimensions and sample count, defaulting to one strip spanning the full height.</summary>
            internal Fixture(int width, int height, int samples)
            { this.width = width; this.height = height; this.samples = samples; Rows = height; }

            /// <summary>Returns the byte offset of a tag's directory entry, for tests that corrupt it directly.</summary>
            internal int TagEntryOffset(int id) => 10 + tags.FindIndex(tag => tag.Id == id) * 12;

            /// <summary>Assembles the configured tags and encoded blocks into a complete classic TIFF file.</summary>
            internal byte[] Build()
            {
                byte[][] encoded = Blocks.Select(EncodeBlock).ToArray();
                tags = new List<Tag>
                {
                    new Tag(256, 4, (uint)width), new Tag(257, 4, (uint)height),
                    new Tag(258, 3, Enumerable.Repeat((uint)Bits, samples).ToArray()), new Tag(259, 3, (uint)Compression),
                    new Tag(262, 3, (uint)Photo), new Tag(274, 3, (uint)Orientation), new Tag(277, 3, (uint)samples),
                    new Tag(284, 3, Planar ? 2u : 1u), new Tag(317, 3, (uint)Predictor)
                };
                if (Alpha >= 0) tags.Add(new Tag(338, 3, (uint)Alpha));
                if (Float) tags.Add(new Tag(339, 3, Enumerable.Repeat(3u, samples).ToArray()));
                bool tiled = TileWidth != 0;
                if (tiled)
                {
                    tags.Add(new Tag(322, 4, (uint)TileWidth)); tags.Add(new Tag(323, 4, (uint)TileHeight));
                }
                else tags.Add(new Tag(278, 4, (uint)Rows));
                var offsets = new Tag(tiled ? 324 : 273, 4, new uint[encoded.Length]);
                tags.Add(offsets);
                tags.Add(new Tag(tiled ? 325 : 279, 4, encoded.Select(block => (uint)block.Length).ToArray()));
                tags.Sort((a, b) => a.Id.CompareTo(b.Id));
                int cursor = 8 + 2 + tags.Count * 12 + 4;
                foreach (Tag tag in tags)
                {
                    if (tag.Bytes <= 4) continue;
                    tag.Offset = cursor;
                    cursor += tag.Bytes;
                }
                FirstBlockOffset = cursor;
                for (int i = 0; i < encoded.Length; i++) { offsets.Values[i] = (uint)cursor; cursor += encoded[i].Length; }
                using (var output = new MemoryStream())
                {
                    output.WriteByte(BigEndian ? (byte)77 : (byte)73); output.WriteByte(BigEndian ? (byte)77 : (byte)73);
                    WriteInteger(output, 42, 2, BigEndian); WriteInteger(output, 8, 4, BigEndian);
                    WriteInteger(output, (uint)tags.Count, 2, BigEndian);
                    foreach (Tag tag in tags)
                    {
                        WriteInteger(output, (uint)tag.Id, 2, BigEndian); WriteInteger(output, (uint)tag.Type, 2, BigEndian);
                        WriteInteger(output, (uint)tag.Values.Length, 4, BigEndian);
                        if (tag.Bytes > 4) WriteInteger(output, (uint)tag.Offset, 4, BigEndian);
                        else { WriteTag(output, tag); for (int pad = tag.Bytes; pad < 4; pad++) output.WriteByte(0); }
                    }
                    WriteInteger(output, 0, 4, BigEndian);
                    foreach (Tag tag in tags) if (tag.Bytes > 4) WriteTag(output, tag);
                    foreach (byte[] block in encoded) output.Write(block, 0, block.Length);
                    return output.ToArray();
                }
            }

            /// <summary>Writes a tag's values as its declared type, in the fixture's byte order.</summary>
            private void WriteTag(Stream output, Tag tag)
            { foreach (uint value in tag.Values) WriteInteger(output, value, tag.Type == 3 ? 2 : 4, BigEndian); }

            /// <summary>Compresses one block's bytes according to the fixture's compression setting, unless the block is already pre-encoded.</summary>
            private byte[] EncodeBlock(byte[] data)
            {
                if (BlocksEncoded) return data;
                switch (Compression)
                {
                    case 1: return data;
                    case 5: return LiteralLzw(data);
                    case 8: case 32946: return Zlib(data);
                    case 32773: return LiteralPackBits(data);
                    default: throw new InvalidOperationException("Unsupported fixture compression.");
                }
            }
        }

        /// <summary>One TIFF directory entry's ID, type and values, as built by a <see cref="Fixture"/>.</summary>
        private sealed class Tag
        {
            internal readonly int Id, Type;
            internal readonly uint[] Values;
            internal int Offset;
            internal int Bytes => Values.Length * (Type == 3 ? 2 : 4);
            /// <summary>Stores the tag's ID, type and values.</summary>
            internal Tag(int id, int type, params uint[] values) { Id = id; Type = type; Values = values; }
        }

        /// <summary>A memory stream that cancels its token once reads pass a configured position, to test mid-decode cancellation.</summary>
        private sealed class CancelingStream : MemoryStream
        {
            private readonly int cancelAt;
            private readonly CancellationTokenSource cancellation;
            /// <summary>Wraps the bytes, cancelling <paramref name="cancellation"/> once the read position passes <paramref name="cancelAt"/>.</summary>
            internal CancelingStream(byte[] data, int cancelAt, CancellationTokenSource cancellation) : base(data)
            { this.cancelAt = cancelAt; this.cancellation = cancellation; }
            /// <summary>Reads as normal, then cancels the token once the position passes the configured threshold.</summary>
            public override int Read(byte[] buffer, int offset, int count)
            {
                int result = base.Read(buffer, offset, count);
                if (Position > cancelAt) cancellation.Cancel();
                return result;
            }
            /// <summary>Reads one byte as normal, then cancels the token once the position passes the configured threshold.</summary>
            public override int ReadByte()
            {
                int result = base.ReadByte();
                if (Position > cancelAt) cancellation.Cancel();
                return result;
            }
        }
    }
}
