using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Reads the saved PSD composite without importing an asset or invoking Unity APIs.</summary>
    internal static class TexturePackingPsd
    {
        private const long MaximumPixels = 64L * 1024 * 1024;

        internal static TexturePackingImage Decode(Stream stream, CancellationToken cancellation = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("PSD decoding requires a readable, seekable stream.", nameof(stream));
            cancellation.ThrowIfCancellationRequested();
            if (Read32(stream) != 0x38425053) throw Invalid("Invalid PSD signature.");
            if (Read16(stream) != 1) throw new NotSupportedException("Only PSD version 1 is supported; PSB is not supported.");
            for (int i = 0; i < 6; i++)
                if (ReadByte(stream) != 0) throw Invalid("Invalid PSD reserved header bytes.");
            int channels = Read16(stream);
            uint height = Read32(stream), width = Read32(stream);
            int depth = Read16(stream), mode = Read16(stream);
            if (width < 1 || height < 1 || width > 16384 || height > 16384 ||
                (long)width * height > MaximumPixels || channels < 1 || channels > 56)
                throw Invalid("PSD dimensions or channel count exceed the supported limits (64 million pixels).");
            if (depth != 8 && depth != 16 && depth != 32)
                throw new NotSupportedException("PSD requires 8, 16 or 32 bits per channel.");
            int colorChannels;
            if (mode == 3) colorChannels = 3;
            else if (mode == 1) colorChannels = 1;
            else throw new NotSupportedException("PSD requires RGB or grayscale color mode. Save an RGB composite in Photoshop.");
            if (channels < colorChannels) throw Invalid("PSD is missing required color channels.");
            Skip(stream, Read32(stream));
            ReadResources(stream, cancellation);
            bool mergedTransparency = ReadLayerTransparency(stream, cancellation);
            if (mergedTransparency && channels == colorChannels)
                throw Invalid("PSD merged transparency is missing its alpha channel.");
            int compression = Read16(stream);
            if (compression > 3) throw new NotSupportedException("Unsupported PSD composite compression.");
            int rowBytes = checked((int)width * (depth / 8));
            long decodedBytes = (long)rowBytes * height * channels;
            ushort[] lengths = null;
            if (compression == 0) RequireRemaining(stream, decodedBytes);
            if (compression == 1)
            {
                RequireRemaining(stream, (long)height * channels * 2);
                lengths = new ushort[checked((int)height * channels)];
                long packedBytes = 0;
                for (int i = 0; i < lengths.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    lengths[i] = (ushort)Read16(stream);
                    packedBytes += lengths[i];
                }
                RequireRemaining(stream, packedBytes);
            }
            if (compression >= 2) RequireRemaining(stream, 6);
            return DecodeRows(stream, (int)width, (int)height, channels, colorChannels,
                depth, compression, lengths, rowBytes, mergedTransparency, cancellation);
        }

        private static TexturePackingImage DecodeRows(Stream stream, int width, int height,
            int channels, int colorChannels, int depth, int compression, ushort[] lengths,
            int rowBytes, bool mergedTransparency, CancellationToken cancellation, float[] nativeAlpha = null)
        {
            long dataStart = stream.Position;
            bool captureAlpha = mergedTransparency && depth != 8 && nativeAlpha == null;
            var row = new byte[rowBytes];
            byte[] unshuffled = depth == 32 && compression == 3 ? new byte[rowBytes] : null;
            Color32[] pixels = null;
            Stream data = stream;
            DeflateStream inflater = null;
            BoundedInput bounded = null;
            uint checksumA = 1, checksumB = 0;
            uint expectedChecksum = 0;
            if (compression >= 2)
            {
                int cmf = ReadByte(stream), flg = ReadByte(stream);
                if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) + flg) % 31 != 0 || (flg & 32) != 0)
                    throw Invalid("Invalid or dictionary-based PSD ZIP header.");
                long compressedBytes = stream.Length - stream.Position - 4;
                long start = stream.Position;
                stream.Seek(compressedBytes, SeekOrigin.Current);
                expectedChecksum = Read32(stream);
                stream.Position = start;
                bounded = new BoundedInput(stream, compressedBytes, cancellation);
                inflater = new DeflateStream(bounded, CompressionMode.Decompress, true);
                data = inflater;
            }
            try
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (compression == 1) ReadPackBits(stream, row, lengths[channel * height + y], cancellation);
                        else ReadExactly(data, row, cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        if (compression >= 2) UpdateChecksum(row, ref checksumA, ref checksumB);
                        if (compression == 3) UndoPrediction(row, unshuffled, width, depth);
                        byte[] samples = unshuffled ?? row;
                        if (captureAlpha)
                        {
                            if (channel == colorChannels)
                            {
                                if (nativeAlpha == null) nativeAlpha = new float[checked(width * height)];
                                int start = (height - 1 - y) * width;
                                for (int x = 0; x < width; x++)
                                    nativeAlpha[start + x] = ClampUnit(SampleUnit(samples, x, depth));
                            }
                        }
                        else
                        {
                            // Defer the large allocation until the first scanline was successfully decoded.
                            if (pixels == null) pixels = new Color32[checked(width * height)];
                            if (channel <= colorChannels)
                                StoreRow(pixels, samples, width, height, y, channel,
                                    colorChannels, depth, mergedTransparency, nativeAlpha);
                        }
                    }
                }
                if (compression >= 2)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (inflater.ReadByte() != -1) throw Invalid("PSD ZIP composite expands beyond its declared dimensions.");
                    if ((checksumB << 16 | checksumA) != expectedChecksum)
                        throw Invalid("PSD ZIP composite checksum mismatch.");
                    stream.Position = stream.Length;
                }
                if (captureAlpha)
                {
                    // Keep only native alpha and RGBA8: at most 512 MiB for the 64 MP limit,
                    // rather than retaining three additional high-depth color planes.
                    inflater?.Dispose();
                    inflater = null;
                    bounded?.Dispose();
                    bounded = null;
                    stream.Position = dataStart;
                    cancellation.ThrowIfCancellationRequested();
                    return DecodeRows(stream, width, height, channels, colorChannels, depth,
                        compression, lengths, rowBytes, mergedTransparency, cancellation, nativeAlpha);
                }
                return new TexturePackingImage(width, height, pixels);
            }
            finally
            {
                inflater?.Dispose();
                bounded?.Dispose();
            }
        }

        private static void StoreRow(Color32[] pixels, byte[] row, int width, int height,
            int y, int channel, int colorChannels, int depth, bool mergedTransparency, float[] nativeAlpha)
        {
            int start = (height - 1 - y) * width;
            for (int x = 0; x < width; x++)
            {
                byte value;
                if (nativeAlpha != null && channel < colorChannels)
                {
                    float alpha = nativeAlpha[start + x];
                    float color = alpha > 0 ? (SampleUnit(row, x, depth) - 1 + alpha) / alpha : 0;
                    value = Quantize(color);
                }
                else value = Sample(row, x, depth);
                Color32 pixel = pixels[start + x];
                if (channel == 0) pixel.a = 255;
                if (channel == colorChannels)
                {
                    pixel.a = value;
                    // Photoshop's merged transparency preview is composited over white.
                    // User alpha planes are independent data and must not change stored RGB.
                    if (mergedTransparency && nativeAlpha == null)
                    {
                        pixel.r = RemoveWhiteMatte(pixel.r, value);
                        pixel.g = RemoveWhiteMatte(pixel.g, value);
                        pixel.b = RemoveWhiteMatte(pixel.b, value);
                    }
                }
                else if (colorChannels == 1) pixel.r = pixel.g = pixel.b = value;
                else if (channel == 0) pixel.r = value;
                else if (channel == 1) pixel.g = value;
                else if (channel == 2) pixel.b = value;
                pixels[start + x] = pixel;
            }
        }

        private static byte Sample(byte[] row, int x, int depth)
        {
            if (depth == 8) return row[x];
            if (depth == 16)
            {
                int index = x * 2;
                return (byte)(((row[index] << 8 | row[index + 1]) + 128) / 257);
            }
            return Quantize(SampleUnit(row, x, depth));
        }

        private static float SampleUnit(byte[] row, int x, int depth)
        {
            if (depth == 8) return row[x] / 255f;
            if (depth == 16)
            {
                int index = x * 2;
                return (row[index] << 8 | row[index + 1]) / 65535f;
            }
            int offset = x * 4;
            uint bits = (uint)(row[offset] << 24 | row[offset + 1] << 16 | row[offset + 2] << 8 | row[offset + 3]);
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        private static byte Quantize(float value)
        {
            if (float.IsNaN(value) || value <= 0) return 0;
            if (value >= 1) return 255;
            return (byte)(value * 255 + 0.5f);
        }

        private static float ClampUnit(float value)
        {
            if (float.IsNaN(value) || value <= 0) return 0;
            return value >= 1 ? 1 : value;
        }

        private static byte RemoveWhiteMatte(byte color, byte alpha)
        {
            if (alpha == 0) return 0;
            int value = (255 * (color + alpha - 255) + alpha / 2) / alpha;
            return (byte)Math.Min(255, Math.Max(0, value));
        }

        private static void UndoPrediction(byte[] row, byte[] unshuffled, int width, int depth)
        {
            if (depth == 16)
            {
                int previous = row[0] << 8 | row[1];
                for (int x = 1; x < width; x++)
                {
                    int i = x * 2;
                    previous = (previous + (row[i] << 8 | row[i + 1])) & 65535;
                    row[i] = (byte)(previous >> 8);
                    row[i + 1] = (byte)previous;
                }
                return;
            }
            for (int i = 1; i < row.Length; i++) row[i] = unchecked((byte)(row[i] + row[i - 1]));
            if (depth != 32) return;
            for (int x = 0; x < width; x++)
                for (int b = 0; b < 4; b++) unshuffled[x * 4 + b] = row[b * width + x];
        }

        private static void ReadPackBits(Stream stream, byte[] row, int packedLength, CancellationToken cancellation)
        {
            int remaining = packedLength, output = 0;
            while (remaining > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int command = ReadByte(stream);
                remaining--;
                if (command == 128) continue;
                int count = command < 128 ? command + 1 : 257 - command;
                if (count > row.Length - output) throw Invalid("PSD RLE row expands beyond its declared width.");
                int input = command < 128 ? count : 1;
                if (input > remaining) throw Invalid("Truncated PSD RLE packet.");
                if (command < 128)
                {
                    for (int i = 0; i < count; i++) row[output++] = (byte)ReadByte(stream);
                }
                else
                {
                    byte value = (byte)ReadByte(stream);
                    for (int i = 0; i < count; i++) row[output++] = value;
                }
                remaining -= input;
            }
            if (output != row.Length) throw Invalid("PSD RLE row does not fill its declared width.");
        }

        private static void UpdateChecksum(byte[] bytes, ref uint a, ref uint b)
        {
            // Keep sums bounded without performing modulo for every sample.
            for (int start = 0; start < bytes.Length; start += 5552)
            {
                int end = Math.Min(bytes.Length, start + 5552);
                for (int i = start; i < end; i++) { a += bytes[i]; b += a; }
                a %= 65521;
                b %= 65521;
            }
        }

        private static void ReadExactly(Stream stream, byte[] bytes, CancellationToken cancellation)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = stream.Read(bytes, offset, Math.Min(8192, bytes.Length - offset));
                if (read == 0) throw Invalid("Truncated PSD composite. Save the PSD with Maximize Compatibility enabled.");
                offset += read;
            }
        }

        private static int ReadByte(Stream stream)
        {
            int value = stream.ReadByte();
            if (value < 0) throw Invalid("Truncated PSD data.");
            return value;
        }

        private static int Read16(Stream stream) => ReadByte(stream) << 8 | ReadByte(stream);
        private static uint Read32(Stream stream) => (uint)(Read16(stream) << 16 | Read16(stream));

        private static void RequireRemaining(Stream stream, long count)
        {
            if (count < 0 || count > stream.Length - stream.Position) throw Invalid("PSD section exceeds the file length.");
        }

        private static void Skip(Stream stream, long count)
        {
            RequireRemaining(stream, count);
            stream.Seek(count, SeekOrigin.Current);
        }

        private static InvalidDataException Invalid(string message) => new InvalidDataException(message);

        private static bool ReadLayerTransparency(Stream stream, CancellationToken cancellation)
        {
            uint length = Read32(stream);
            RequireRemaining(stream, length);
            if (length == 0) return false;
            long end = stream.Position + length;
            bool transparency = false;
            if (end - stream.Position < 4) throw Invalid("Truncated PSD layer info.");
            uint layerLength = Read32(stream);
            if (layerLength > end - stream.Position) throw Invalid("PSD layer info exceeds its section.");
            if (layerLength != 0)
            {
                if (layerLength < 2) throw Invalid("Truncated PSD layer count.");
                long next = stream.Position + layerLength;
                transparency = unchecked((short)Read16(stream)) < 0;
                stream.Position = next;
            }
            // Some writers omit the global mask length before tagged blocks.
            if (end - stream.Position >= 4)
            {
                uint marker = Read32(stream);
                if (marker == 0x3842494D || marker == 0x38423634) stream.Position -= 4;
                else
                {
                    if (marker > end - stream.Position) throw Invalid("PSD global mask exceeds its section.");
                    stream.Position += marker;
                }
            }
            while (end - stream.Position >= 12)
            {
                cancellation.ThrowIfCancellationRequested();
                uint signature = Read32(stream);
                if (signature != 0x3842494D && signature != 0x38423634)
                    throw Invalid("Invalid PSD global layer tag signature.");
                uint key = Read32(stream), dataLength = Read32(stream);
                if (dataLength > end - stream.Position) throw Invalid("PSD global layer tag exceeds its section.");
                long next = stream.Position + dataLength;
                if (key == 0x4C723136 || key == 0x4C723332 || key == 0x4C617972)
                {
                    if (dataLength < 2) throw Invalid("Truncated PSD tagged layer count.");
                    transparency |= unchecked((short)Read16(stream)) < 0;
                }
                if (key == 0x4D74726E || key == 0x4D743136 || key == 0x4D743332) transparency = true;
                stream.Position = next;
                // Global tags are commonly padded to four bytes; tolerate two-byte writers too.
                for (int padding = 0; padding < 3 && stream.Position < end; padding++)
                {
                    if (ReadByte(stream) != 0) { stream.Position--; break; }
                }
            }
            if (end - stream.Position > 3) throw Invalid("Truncated PSD global layer tag.");
            stream.Position = end;
            return transparency;
        }

        private static void ReadResources(Stream stream, CancellationToken cancellation)
        {
            uint length = Read32(stream);
            RequireRemaining(stream, length);
            long end = stream.Position + length;
            while (stream.Position < end)
            {
                cancellation.ThrowIfCancellationRequested();
                if (end - stream.Position < 12 || Read32(stream) != 0x3842494D)
                    throw Invalid("Invalid PSD image resource block.");
                int id = Read16(stream);
                int nameLength = ReadByte(stream);
                int nameBytes = nameLength + ((nameLength + 1) & 1);
                if (nameBytes + 4 > end - stream.Position) throw Invalid("Invalid PSD resource name length.");
                Skip(stream, nameBytes);
                uint dataLength = Read32(stream);
                long paddedLength = (long)dataLength + (dataLength & 1);
                if (paddedLength > end - stream.Position) throw Invalid("PSD resource exceeds its enclosing section.");
                long next = stream.Position + paddedLength;
                // Version Info explicitly says whether a real saved composite is available.
                if (id == 1057)
                {
                    if (dataLength < 5) throw Invalid("Truncated PSD Version Info resource.");
                    Read32(stream);
                    if (ReadByte(stream) == 0)
                        throw new NotSupportedException("PSD has no saved composite. Save with Maximize Compatibility enabled in Photoshop.");
                }
                stream.Position = next;
            }
        }

        /// <summary>Exposes only the deflate payload, excluding the zlib header and checksum.</summary>
        private sealed class BoundedInput : Stream
        {
            private readonly Stream input;
            private readonly CancellationToken cancellation;
            private long remaining;

            internal BoundedInput(Stream input, long length, CancellationToken cancellation)
            { this.input = input; remaining = length; this.cancellation = cancellation; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = input.Read(buffer, offset, (int)Math.Min(Math.Min(count, 8192), remaining));
                remaining -= read;
                return read;
            }
            public override void Flush() { /* Read-only view has nothing to flush. */ }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
