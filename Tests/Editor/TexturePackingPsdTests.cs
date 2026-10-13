using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class TexturePackingPsdTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void CompositePreservesRgbAlphaAndBottomLeftOrigin(int compression)
        {
            // Two rows, each two pixels. PSD planes are in top-to-bottom order.
            byte[] planes = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 0, 64, 128, 255 };
            byte[] file = Fixture(2, 2, 4, 8, 3, compression, planes);
            using (var stream = new MemoryStream(file))
            {
                TexturePackingImage image = TexturePackingPsd.Decode(stream);
                Assert.AreEqual(2, image.Width);
                Assert.AreEqual(2, image.Height);
                Assert.AreEqual(new Color32(30, 70, 110, 128), image.Pixels[0]);
                Assert.AreEqual(new Color32(40, 80, 120, 255), image.Pixels[1]);
                Assert.AreEqual(new Color32(10, 50, 90, 0), image.Pixels[2]);
                Assert.AreEqual(new Color32(20, 60, 100, 64), image.Pixels[3]);
                Assert.IsTrue(stream.CanRead, "The caller retains ownership of the source stream.");
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SixteenBitGrayscaleScalesTheWholeUnsignedRange(int compression)
        {
            byte[] planes = { 0, 0, 0x80, 0, 255, 255, 255, 255, 0x40, 0x40, 0, 0 };
            using (var stream = new MemoryStream(Fixture(3, 1, 2, 16, 1, compression, planes)))
            {
                TexturePackingImage image = TexturePackingPsd.Decode(stream);
                Assert.AreEqual(new Color32(0, 0, 0, 255), image.Pixels[0]);
                Assert.AreEqual(new Color32(128, 128, 128, 64), image.Pixels[1]);
                Assert.AreEqual(new Color32(255, 255, 255, 0), image.Pixels[2]);
            }
        }

        [Test]
        public void SixteenBitZipPredictionUsesWordDeltasWithWraparound()
        {
            // Samples: 65535, 0, 32768. Deltas: 65535, 1, 32768.
            byte[] encoded = { 255, 255, 0, 1, 128, 0 };
            using (var stream = new MemoryStream(Fixture(3, 1, 1, 16, 1, 3, encoded, true)))
            {
                TexturePackingImage image = TexturePackingPsd.Decode(stream);
                Assert.AreEqual(new Color32(255, 255, 255, 255), image.Pixels[0]);
                Assert.AreEqual(new Color32(0, 0, 0, 255), image.Pixels[1]);
                Assert.AreEqual(new Color32(128, 128, 128, 255), image.Pixels[2]);
            }
        }

        [TestCase(0)]
        [TestCase(2)]
        public void FloatCompositeClampsHdrAndNonFiniteSamples(int compression)
        {
            byte[] planes = { 0xBF, 0x80, 0, 0, 0x3F, 0, 0, 0, 0x40, 0, 0, 0, 0x7F, 0xC0, 0, 0 };
            using (var stream = new MemoryStream(Fixture(4, 1, 1, 32, 1, compression, planes)))
            {
                TexturePackingImage image = TexturePackingPsd.Decode(stream);
                Assert.AreEqual(0, image.Pixels[0].r);
                Assert.AreEqual(128, image.Pixels[1].r);
                Assert.AreEqual(255, image.Pixels[2].r);
                Assert.AreEqual(0, image.Pixels[3].r);
                Assert.AreEqual(255, image.Pixels[1].a);
            }
        }

        [Test]
        public void FloatZipPredictionRestoresShuffledBytePlanes()
        {
            // Floats 0, 0.5, 1: shuffle byte planes, then delta across the entire shuffled row.
            byte[] encoded = { 0, 0x3F, 0, 0xC1, 0, 0x80, 0x80, 0, 0, 0, 0, 0 };
            using (var stream = new MemoryStream(Fixture(3, 1, 1, 32, 1, 3, encoded, true)))
            {
                TexturePackingImage image = TexturePackingPsd.Decode(stream);
                Assert.AreEqual(new Color32(0, 0, 0, 255), image.Pixels[0]);
                Assert.AreEqual(new Color32(128, 128, 128, 255), image.Pixels[1]);
                Assert.AreEqual(new Color32(255, 255, 255, 255), image.Pixels[2]);
            }
        }

        [Test]
        public void ExtraAlphaChannelsDoNotOverwriteFirstAlpha()
        {
            using (var stream = new MemoryStream(Fixture(1, 1, 5, 8, 3, 0, new byte[] { 10, 20, 30, 40, 50 })))
                Assert.AreEqual(new Color32(10, 20, 30, 40), TexturePackingPsd.Decode(stream).Pixels[0]);
        }

        [Test]
        public void PhotoshopMergedTransparencyRemovesWhiteMatte()
        {
            byte[] planes = { 191, 255, 127, 255, 159, 255, 128, 0 };
            byte[] file = WithLayerCount(Fixture(2, 1, 4, 8, 3, 0, planes), -1);
            using (var stream = new MemoryStream(file))
            {
                TexturePackingImage image = TexturePackingPsd.Decode(stream);
                Assert.AreEqual(new Color32(128, 0, 64, 128), image.Pixels[0]);
                Assert.AreEqual(new Color32(0, 0, 0, 0), image.Pixels[1]);
            }
        }

        [Test]
        public void LayeredUserAlphaPreservesStoredRgbWithoutUnmatting()
        {
            byte[] file = WithLayerCount(Fixture(1, 1, 4, 8, 3, 0, new byte[] { 191, 127, 159, 128 }), 1);
            using (var stream = new MemoryStream(file))
                Assert.AreEqual(new Color32(191, 127, 159, 128), TexturePackingPsd.Decode(stream).Pixels[0]);
        }

        [TestCase(16, 0x4C723136u)]
        [TestCase(32, 0x4C723332u)]
        public void TaggedHighDepthLayerCountMarksMergedTransparency(int depth, uint tag)
        {
            byte[] planes = depth == 16 ? new byte[] { 0xBF, 0xBF, 0x80, 0x80 } :
                new byte[] { 0x3F, 0x40, 0, 0, 0x3F, 0, 0, 0 };
            byte[] file = WithLayerCount(Fixture(1, 1, 2, depth, 1, 2, planes), -1, tag);
            using (var stream = new MemoryStream(file))
                Assert.AreEqual(new Color32(128, 128, 128, 128), TexturePackingPsd.Decode(stream).Pixels[0]);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void LowAlphaSixteenBitCompositeUnmattesBeforeQuantizing(int compression)
        {
            // Exact unmatted native samples are327/655 and328/655 (gray127 and128).
            byte[] samples = { 0xFE, 0xB7, 0xFE, 0xB8, 2, 0x8F, 2, 0x8F };
            byte[] predicted = { 0xFE, 0xB7, 0, 1, 2, 0x8F, 0, 0 };
            byte[] file = WithLayerCount(Fixture(2, 1, 2, 16, 1, compression,
                compression == 3 ? predicted : samples, compression == 3), -1, 0x4C723136);
            using (var stream = new MemoryStream(file))
            {
                TexturePackingImage image = TexturePackingPsd.Decode(stream);
                Assert.AreEqual(new Color32(127, 127, 127, 3), image.Pixels[0]);
                Assert.AreEqual(new Color32(128, 128, 128, 3), image.Pixels[1]);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void LowAlphaFloatCompositeUnmattesBeforeQuantizing(int compression)
        {
            // Native float gray0.995 and alpha0.01 represent gray0.5 over white.
            byte[] samples = { 0x3F, 0x7E, 0xB8, 0x52, 0x3C, 0x23, 0xD7, 0x0A };
            byte[] predicted = { 0x3F, 0x3F, 0x3A, 0x9A, 0x3C, 0xE7, 0xB4, 0x33 };
            byte[] file = WithLayerCount(Fixture(1, 1, 2, 32, 1, compression,
                compression == 3 ? predicted : samples, compression == 3), -1, 0x4C723332);
            using (var stream = new MemoryStream(file))
                Assert.AreEqual(new Color32(128, 128, 128, 3), TexturePackingPsd.Decode(stream).Pixels[0]);
        }

        [Test]
        public void CancellationInterruptsSecondNativeTransparencyPass()
        {
            byte[] file = WithLayerCount(Fixture(1, 1, 2, 16, 1, 0,
                new byte[] { 0xFE, 0xB7, 2, 0x8F }), -1, 0x4C723136);
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new CancellingStream(file, cancellation, 3))
            {
                Assert.Throws<OperationCanceledException>(() => TexturePackingPsd.Decode(stream, cancellation.Token));
                Assert.IsTrue(stream.CanRead);
            }
        }

        [Test]
        public void RleAcceptsRepeatAndNoOpPackets()
        {
            byte[] file = Fixture(3, 1, 1, 8, 1, 0, new byte[] { 0, 0, 0 });
            Array.Resize(ref file, 43);
            file[39] = 1; // Composite compression.
            file[40] = 0; file[41] = 3; // Packed scanline size.
            file[42] = 128;
            Array.Resize(ref file, 45);
            file[43] = 254; file[44] = 77; // Repeat three samples.
            using (var stream = new MemoryStream(file))
                foreach (Color32 pixel in TexturePackingPsd.Decode(stream).Pixels)
                    Assert.AreEqual(new Color32(77, 77, 77, 255), pixel);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void TruncatedCompositeIsRejected(int compression)
        {
            byte[] file = Fixture(2, 1, 3, 8, 3, compression, new byte[] { 1, 2, 3, 4, 5, 6 });
            Array.Resize(ref file, file.Length - 1);
            using (var stream = new MemoryStream(file))
                Assert.Throws<InvalidDataException>(() => TexturePackingPsd.Decode(stream));
        }

        [Test]
        public void ZipRejectsChecksumMismatchAndOversizedExpansion()
        {
            byte[] file = Fixture(1, 1, 1, 8, 1, 2, new byte[] { 17 });
            file[file.Length - 1] ^= 1;
            using (var stream = new MemoryStream(file))
                Assert.Throws<InvalidDataException>(() => TexturePackingPsd.Decode(stream));
            using (var stream = new MemoryStream(Fixture(1, 1, 1, 8, 1, 2, new byte[] { 17, 18 })))
                Assert.Throws<InvalidDataException>(() => TexturePackingPsd.Decode(stream));
        }

        [Test]
        public void RleRejectsScanlineOverflowAndUnderflow()
        {
            byte[] file = Fixture(1, 1, 1, 8, 1, 1, new byte[] { 17 });
            file[42] = 255; // Repeat two samples into a one-sample row.
            using (var stream = new MemoryStream(file))
                Assert.Throws<InvalidDataException>(() => TexturePackingPsd.Decode(stream));
            file[42] = 128; // No-op leaves row empty.
            file[43] = 128;
            using (var stream = new MemoryStream(file))
                Assert.Throws<InvalidDataException>(() => TexturePackingPsd.Decode(stream));
        }

        [Test]
        public void HostileDimensionsAndSectionLengthsAreRejectedBeforeAllocation()
        {
            byte[] file = Fixture(1, 1, 1, 8, 1, 0, new byte[] { 17 });
            file[18] = 0x7F; // Width in header.
            using (var stream = new MemoryStream(file))
                Assert.Throws<InvalidDataException>(() => TexturePackingPsd.Decode(stream));
            file[18] = 0;
            file[26] = 0xFF; // Color-mode section length.
            using (var stream = new MemoryStream(file))
                Assert.Throws<InvalidDataException>(() => TexturePackingPsd.Decode(stream));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void CancellationInterruptsDecodeAndLeavesCallerStreamOpen(int compression)
        {
            using (var cancel = new CancellationTokenSource())
            using (var stream = new CancellingStream(Fixture(2, 2, 1, 8, 1, compression,
                       new byte[] { 17, 18, 19, 20 }), cancel))
            {
                Assert.Throws<OperationCanceledException>(() => TexturePackingPsd.Decode(stream, cancel.Token));
                Assert.IsTrue(stream.CanRead);
            }
        }

        [Test]
        public void UnsupportedPsbColorModeAndDepthAreReportedClearly()
        {
            byte[] file = Fixture(1, 1, 1, 8, 1, 0, new byte[] { 17 });
            file[5] = 2;
            using (var stream = new MemoryStream(file))
                StringAssert.Contains("PSB", Assert.Throws<NotSupportedException>(() => TexturePackingPsd.Decode(stream)).Message);
            file[5] = 1; file[25] = 4;
            using (var stream = new MemoryStream(file))
                StringAssert.Contains("RGB", Assert.Throws<NotSupportedException>(() => TexturePackingPsd.Decode(stream)).Message);
            file[25] = 1; file[23] = 1;
            using (var stream = new MemoryStream(file))
                StringAssert.Contains("bits", Assert.Throws<NotSupportedException>(() => TexturePackingPsd.Decode(stream)).Message);
        }

        [Test]
        public void PsdWithoutSavedCompositeRequestsMaximizeCompatibility()
        {
            byte[] file = Fixture(1, 1, 1, 8, 1, 0, new byte[] { 17 });
            using (var fixture = new MemoryStream())
            {
                fixture.Write(file, 0, 30);
                Write32(fixture, 18);
                Write32(fixture, 0x3842494D); Write16(fixture, 1057);
                Write16(fixture, 0); // Empty padded Pascal name.
                Write32(fixture, 5); Write32(fixture, 1);
                Write16(fixture, 0); // hasRealMergedData=false and even padding.
                fixture.Write(file, 34, file.Length - 34);
                fixture.Position = 0;
                StringAssert.Contains("Maximize Compatibility",
                    Assert.Throws<NotSupportedException>(() => TexturePackingPsd.Decode(fixture)).Message);
            }
        }

        [Test]
        public void PreCancelledReadDoesNotAdvanceSourceStream()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new MemoryStream(Fixture(1, 1, 1, 8, 1, 0, new byte[] { 17 })))
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => TexturePackingPsd.Decode(stream, cancellation.Token));
                Assert.AreEqual(0, stream.Position);
            }
        }

        private static byte[] Fixture(int width, int height, int channels, int depth,
            int mode, int compression, byte[] planes, bool alreadyPredicted = false)
        {
            using (var stream = new MemoryStream())
            {
                Write32(stream, 0x38425053); Write16(stream, 1);
                stream.Write(new byte[6], 0, 6);
                Write16(stream, channels); Write32(stream, (uint)height); Write32(stream, (uint)width);
                Write16(stream, depth); Write16(stream, mode);
                Write32(stream, 0); Write32(stream, 0); Write32(stream, 0);
                Write16(stream, compression);
                int rowBytes = width * depth / 8;
                if (compression == 1)
                {
                    for (int i = 0; i < channels * height; i++) Write16(stream, rowBytes + 1);
                    for (int i = 0; i < channels * height; i++)
                    { stream.WriteByte((byte)(rowBytes - 1)); stream.Write(planes, i * rowBytes, rowBytes); }
                }
                else if (compression >= 2)
                {
                    byte[] encoded = (byte[])planes.Clone();
                    if (compression == 3 && !alreadyPredicted)
                    {
                        Assert.AreEqual(8, depth, "Other prediction fixtures supply independently encoded bytes.");
                        for (int row = 0; row < channels * height; row++)
                            for (int x = rowBytes - 1; x > 0; x--)
                                encoded[row * rowBytes + x] = unchecked((byte)(planes[row * rowBytes + x] - planes[row * rowBytes + x - 1]));
                    }
                    stream.WriteByte(0x78); stream.WriteByte(0x9C);
                    using (var deflater = new DeflateStream(stream, System.IO.Compression.CompressionLevel.Optimal, true))
                        deflater.Write(encoded, 0, encoded.Length);
                    uint a = 1, b = 0;
                    foreach (byte value in encoded) { a = (a + value) % 65521; b = (b + a) % 65521; }
                    Write32(stream, b << 16 | a);
                }
                else stream.Write(planes, 0, planes.Length);
                return stream.ToArray();
            }
        }

        private static void Write16(Stream stream, int value)
        { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void Write32(Stream stream, uint value)
        { Write16(stream, (int)(value >> 16)); Write16(stream, (int)value); }

        private static byte[] WithLayerCount(byte[] file, int layerCount, uint tag = 0)
        {
            using (var layers = new MemoryStream())
            using (var result = new MemoryStream())
            {
                Write32(layers, tag == 0 ? 2u : 0u);
                if (tag == 0) Write16(layers, layerCount);
                Write32(layers, 0); // Empty global mask.
                if (tag != 0)
                {
                    Write32(layers, 0x3842494D); Write32(layers, tag); Write32(layers, 2);
                    Write16(layers, layerCount); Write16(layers, 0);
                }
                result.Write(file, 0, 34);
                Write32(result, (uint)layers.Length);
                layers.Position = 0;
                layers.CopyTo(result);
                result.Write(file, 38, file.Length - 38);
                return result.ToArray();
            }
        }

        private sealed class CancellingStream : MemoryStream
        {
            private readonly CancellationTokenSource cancellation;
            private int readsRemaining;
            internal CancellingStream(byte[] bytes, CancellationTokenSource cancellation, int readsBeforeCancellation = 1) : base(bytes)
            { this.cancellation = cancellation; readsRemaining = readsBeforeCancellation; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = base.Read(buffer, offset, count);
                if (--readsRemaining == 0) cancellation.Cancel();
                return read;
            }
        }
    }
}
