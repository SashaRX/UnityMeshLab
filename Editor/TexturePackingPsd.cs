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
            ValidateInput(stream, cancellation);
            Header header = ReadHeader(stream);
            Skip(stream, Read32(stream));
            ReadResources(stream, cancellation);
            bool mergedTransparency = ReadLayerTransparency(stream, cancellation);
            if (mergedTransparency && header.Channels == header.ColorChannels)
                throw Invalid("PSD merged transparency is missing its alpha channel.");
            int compression = Read16(stream);
            if (compression > 3) throw new NotSupportedException("Unsupported PSD composite compression.");
            ushort[] lengths = ReadRowLengths(stream, header, compression, cancellation);
            return DecodeRows(stream, new RowPass(header, compression, lengths, mergedTransparency, cancellation));
        }

        private static void ValidateInput(Stream stream, CancellationToken cancellation)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("PSD decoding requires a readable, seekable stream.", nameof(stream));
            cancellation.ThrowIfCancellationRequested();
        }

        private static Header ReadHeader(Stream stream)
        {
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
            int colorChannels = ColorChannelCount(mode);
            if (channels < colorChannels) throw Invalid("PSD is missing required color channels.");
            return new Header((int)width, (int)height, channels, colorChannels, depth);
        }

        private static int ColorChannelCount(int mode)
        {
            if (mode == 3) return 3;
            if (mode == 1) return 1;
            throw new NotSupportedException("PSD requires RGB or grayscale color mode. Save an RGB composite in Photoshop.");
        }

        private static ushort[] ReadRowLengths(Stream stream, Header header, int compression, CancellationToken cancellation)
        {
            if (compression == 0) RequireRemaining(stream, (long)header.RowBytes * header.Height * header.Channels);
            if (compression >= 2) RequireRemaining(stream, 6);
            if (compression != 1) return null;
            int count = checked(header.Height * header.Channels);
            RequireRemaining(stream, (long)count * 2);
            var lengths = new ushort[count];
            long packedBytes = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                lengths[i] = (ushort)Read16(stream);
                packedBytes += lengths[i];
            }
            RequireRemaining(stream, packedBytes);
            return lengths;
        }

        private static TexturePackingImage DecodeRows(Stream stream, RowPass pass)
        {
            long dataStart = stream.Position;
            using (var input = new CompositeInput(stream, pass.Compression, pass.Cancellation))
            {
                ReadAllRows(stream, input, pass);
                input.Validate();
            }
            if (pass.CaptureAlpha)
            {
                // Keep only native alpha and RGBA8: at most 512 MiB for the 64 MP limit,
                // rather than retaining three additional high-depth color planes.
                stream.Position = dataStart;
                pass.Cancellation.ThrowIfCancellationRequested();
                return DecodeRows(stream, pass.ColorPass());
            }
            return new TexturePackingImage(pass.Header.Width, pass.Header.Height, pass.Pixels);
        }

        private static void ReadAllRows(Stream stream, CompositeInput input, RowPass pass)
        {
            for (int channel = 0; channel < pass.Header.Channels; channel++)
                for (int y = 0; y < pass.Header.Height; y++) ReadRow(stream, input, pass, channel, y);
        }

        private static void ReadRow(Stream stream, CompositeInput input, RowPass pass, int channel, int y)
        {
            pass.Cancellation.ThrowIfCancellationRequested();
            if (pass.Compression == 1)
                ReadPackBits(stream, pass.Row, pass.Lengths[channel * pass.Header.Height + y], pass.Cancellation);
            else input.Read(pass.Row);
            pass.Cancellation.ThrowIfCancellationRequested();
            input.UpdateChecksum(pass.Row);
            if (pass.Compression == 3) UndoPrediction(pass.Row, pass.Unshuffled, pass.Header.Width, pass.Header.Depth);
            byte[] samples = pass.Unshuffled ?? pass.Row;
            if (pass.CaptureAlpha)
            {
                if (channel == pass.Header.ColorChannels) CaptureAlphaRow(pass, samples, y);
                return;
            }
            // Defer the large allocation until the first scanline was successfully decoded.
            if (pass.Pixels == null) pass.Pixels = new Color32[pass.Header.PixelCount];
            if (channel <= pass.Header.ColorChannels) StoreRow(pass, samples, y, channel);
        }

        private static void CaptureAlphaRow(RowPass pass, byte[] samples, int y)
        {
            if (pass.NativeAlpha == null) pass.NativeAlpha = new float[pass.Header.PixelCount];
            int start = (pass.Header.Height - 1 - y) * pass.Header.Width;
            for (int x = 0; x < pass.Header.Width; x++)
                pass.NativeAlpha[start + x] = ClampUnit(SampleUnit(samples, x, pass.Header.Depth));
        }

        private static void StoreRow(RowPass pass, byte[] row, int y, int channel)
        {
            int start = (pass.Header.Height - 1 - y) * pass.Header.Width;
            for (int x = 0; x < pass.Header.Width; x++)
            {
                byte value = RowSample(pass, row, start, x, channel);
                Color32 pixel = pass.Pixels[start + x];
                ApplyChannel(ref pixel, value, channel, pass);
                pass.Pixels[start + x] = pixel;
            }
        }

        private static byte RowSample(RowPass pass, byte[] row, int start, int x, int channel)
        {
            if (pass.NativeAlpha == null || channel >= pass.Header.ColorChannels)
                return Sample(row, x, pass.Header.Depth);
            float alpha = pass.NativeAlpha[start + x];
            float color = alpha > 0 ? (SampleUnit(row, x, pass.Header.Depth) - 1 + alpha) / alpha : 0;
            return Quantize(color);
        }

        private static void ApplyChannel(ref Color32 pixel, byte value, int channel, RowPass pass)
        {
            if (channel == 0) pixel.a = 255;
            if (channel == pass.Header.ColorChannels) ApplyAlpha(ref pixel, value, pass);
            else if (pass.Header.ColorChannels == 1) pixel.r = pixel.g = pixel.b = value;
            else if (channel == 0) pixel.r = value;
            else if (channel == 1) pixel.g = value;
            else if (channel == 2) pixel.b = value;
        }

        private static void ApplyAlpha(ref Color32 pixel, byte value, RowPass pass)
        {
            pixel.a = value;
            // Photoshop's merged transparency preview is composited over white.
            // User alpha planes are independent data and must not change stored RGB.
            if (!pass.MergedTransparency || pass.NativeAlpha != null) return;
            pixel.r = RemoveWhiteMatte(pixel.r, value);
            pixel.g = RemoveWhiteMatte(pixel.g, value);
            pixel.b = RemoveWhiteMatte(pixel.b, value);
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
                CopyPackBitsPacket(stream, row, command, ref output, ref remaining);
            }
            if (output != row.Length) throw Invalid("PSD RLE row does not fill its declared width.");
        }

        private static void CopyPackBitsPacket(Stream stream, byte[] row, int command, ref int output, ref int remaining)
        {
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
            bool transparency = ReadPrimaryLayerCount(stream, end);
            SkipGlobalMask(stream, end);
            while (end - stream.Position >= 12)
            {
                cancellation.ThrowIfCancellationRequested();
                // Read every tag even after transparency was found, to validate its bounds.
                bool taggedTransparency = ReadGlobalLayerTag(stream, end);
                transparency |= taggedTransparency;
            }
            if (end - stream.Position > 3) throw Invalid("Truncated PSD global layer tag.");
            stream.Position = end;
            return transparency;
        }

        private static bool ReadPrimaryLayerCount(Stream stream, long end)
        {
            if (end - stream.Position < 4) throw Invalid("Truncated PSD layer info.");
            uint layerLength = Read32(stream);
            if (layerLength > end - stream.Position) throw Invalid("PSD layer info exceeds its section.");
            if (layerLength == 0) return false;
            if (layerLength < 2) throw Invalid("Truncated PSD layer count.");
            long next = stream.Position + layerLength;
            bool transparency = unchecked((short)Read16(stream)) < 0;
            stream.Position = next;
            return transparency;
        }

        private static void SkipGlobalMask(Stream stream, long end)
        {
            // Some writers omit the global mask length before tagged blocks.
            if (end - stream.Position < 4) return;
            uint marker = Read32(stream);
            if (IsTagSignature(marker)) { stream.Position -= 4; return; }
            if (marker > end - stream.Position) throw Invalid("PSD global mask exceeds its section.");
            stream.Position += marker;
        }

        private static bool ReadGlobalLayerTag(Stream stream, long end)
        {
            if (!IsTagSignature(Read32(stream))) throw Invalid("Invalid PSD global layer tag signature.");
            uint key = Read32(stream), dataLength = Read32(stream);
            if (dataLength > end - stream.Position) throw Invalid("PSD global layer tag exceeds its section.");
            long next = stream.Position + dataLength;
            bool transparency = false;
            if (IsLayerInfoTag(key))
            {
                if (dataLength < 2) throw Invalid("Truncated PSD tagged layer count.");
                transparency = unchecked((short)Read16(stream)) < 0;
            }
            if (IsMergedTransparencyTag(key)) transparency = true;
            stream.Position = next;
            SkipTagPadding(stream, end);
            return transparency;
        }

        private static bool IsTagSignature(uint value) => value == 0x3842494D || value == 0x38423634;
        private static bool IsLayerInfoTag(uint value) => value == 0x4C723136 || value == 0x4C723332 || value == 0x4C617972;
        private static bool IsMergedTransparencyTag(uint value) => value == 0x4D74726E || value == 0x4D743136 || value == 0x4D743332;

        private static void SkipTagPadding(Stream stream, long end)
        {
            // Global tags are commonly padded to four bytes; tolerate two-byte writers too.
            for (int padding = 0; padding < 3 && stream.Position < end; padding++)
            {
                if (ReadByte(stream) != 0) { stream.Position--; break; }
            }
        }

        private static void ReadResources(Stream stream, CancellationToken cancellation)
        {
            uint length = Read32(stream);
            RequireRemaining(stream, length);
            long end = stream.Position + length;
            while (stream.Position < end)
            {
                cancellation.ThrowIfCancellationRequested();
                ReadResource(stream, end);
            }
        }

        private static void ReadResource(Stream stream, long end)
        {
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
            if (id == 1057) ValidateCompositeFlag(stream, dataLength);
            stream.Position = next;
        }

        private static void ValidateCompositeFlag(Stream stream, uint length)
        {
            // Version Info explicitly says whether a real saved composite is available.
            if (length < 5) throw Invalid("Truncated PSD Version Info resource.");
            Read32(stream);
            if (ReadByte(stream) == 0)
                throw new NotSupportedException("PSD has no saved composite. Save with Maximize Compatibility enabled in Photoshop.");
        }

        private readonly struct Header
        {
            internal int Width { get; }
            internal int Height { get; }
            internal int Channels { get; }
            internal int ColorChannels { get; }
            internal int Depth { get; }
            internal int RowBytes => checked(Width * (Depth / 8));
            internal int PixelCount => checked(Width * Height);

            internal Header(int width, int height, int channels, int colorChannels, int depth)
            { Width = width; Height = height; Channels = channels; ColorChannels = colorChannels; Depth = depth; }
        }

        /// <summary>A scanline pass retains either native alpha or the final RGBA8 pixels.</summary>
        private sealed class RowPass
        {
            internal Header Header { get; }
            internal int Compression { get; }
            internal ushort[] Lengths { get; }
            internal bool MergedTransparency { get; }
            internal CancellationToken Cancellation { get; }
            internal bool CaptureAlpha { get; }
            internal byte[] Row { get; }
            internal byte[] Unshuffled { get; }
            internal float[] NativeAlpha { get; set; }
            internal Color32[] Pixels { get; set; }

            internal RowPass(Header header, int compression, ushort[] lengths, bool mergedTransparency,
                CancellationToken cancellation, float[] nativeAlpha = null)
            {
                Header = header;
                Compression = compression;
                Lengths = lengths;
                MergedTransparency = mergedTransparency;
                Cancellation = cancellation;
                NativeAlpha = nativeAlpha;
                CaptureAlpha = mergedTransparency && header.Depth != 8 && nativeAlpha == null;
                Row = new byte[header.RowBytes];
                Unshuffled = header.Depth == 32 && compression == 3 ? new byte[header.RowBytes] : null;
            }

            internal RowPass ColorPass() => new RowPass(Header, Compression, Lengths, MergedTransparency, Cancellation, NativeAlpha);
        }

        /// <summary>Owns only the optional decompressor; the caller always owns the source stream.</summary>
        private sealed class CompositeInput : IDisposable
        {
            private readonly Stream source;
            private readonly Stream data;
            private readonly CancellationToken cancellation;
            private readonly DeflateStream inflater;
            private readonly BoundedInput bounded;
            private readonly uint expectedChecksum;
            private uint checksumA = 1;
            private uint checksumB;

            internal CompositeInput(Stream source, int compression, CancellationToken cancellation)
            {
                this.source = source;
                this.cancellation = cancellation;
                if (compression < 2) { data = source; return; }
                expectedChecksum = ReadZipChecksum(source);
                bounded = new BoundedInput(source, source.Length - source.Position - 4, cancellation);
                inflater = new DeflateStream(bounded, CompressionMode.Decompress, true);
                data = inflater;
            }

            private static uint ReadZipChecksum(Stream source)
            {
                int cmf = ReadByte(source), flg = ReadByte(source);
                if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) + flg) % 31 != 0 || (flg & 32) != 0)
                    throw Invalid("Invalid or dictionary-based PSD ZIP header.");
                long start = source.Position;
                source.Seek(-4, SeekOrigin.End);
                uint checksum = Read32(source);
                source.Position = start;
                return checksum;
            }

            internal void Read(byte[] row) => ReadExactly(data, row, cancellation);

            internal void UpdateChecksum(byte[] row)
            {
                if (inflater != null) TexturePackingPsd.UpdateChecksum(row, ref checksumA, ref checksumB);
            }

            internal void Validate()
            {
                if (inflater == null) return;
                cancellation.ThrowIfCancellationRequested();
                if (inflater.ReadByte() != -1) throw Invalid("PSD ZIP composite expands beyond its declared dimensions.");
                if ((checksumB << 16 | checksumA) != expectedChecksum)
                    throw Invalid("PSD ZIP composite checksum mismatch.");
                source.Position = source.Length;
            }

            public void Dispose()
            {
                try { inflater?.Dispose(); }
                finally { bounded?.Dispose(); }
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
