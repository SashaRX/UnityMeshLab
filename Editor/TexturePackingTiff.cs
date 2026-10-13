using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Managed, worker-safe decoder for the first image in a classic TIFF file.</summary>
    internal static class TexturePackingTiff
    {
        private const long MaximumImageBytes = 256L * 1024 * 1024;
        private const int MaximumBlockBytes = 256 * 1024 * 1024;
        private const int MaximumTagValues = 1048576;

        internal static TexturePackingImage Decode(Stream stream, CancellationToken cancellation = default)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("TIFF input must be readable and seekable.", nameof(stream));
            cancellation.ThrowIfCancellationRequested();
            var input = new Input(stream, cancellation);
            var image = new ImageInfo(input.ReadDirectory());
            image.ValidateBlocks(input.Length, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var pixels = new Color32[checked(image.Width * image.Height)];
            for (int block = 0; block < image.BlocksPerPlane; block++)
            {
                cancellation.ThrowIfCancellationRequested();
                int blockRows = image.BlockRows(block);
                int components = image.Planar ? 1 : image.Samples;
                int bytes = checked(image.BlockWidth * blockRows * components * image.SampleBytes);
                var planes = new byte[image.Planar ? image.Samples : 1][];
                for (int plane = 0; plane < planes.Length; plane++)
                {
                    int index = plane * image.BlocksPerPlane + block;
                    planes[plane] = DecodeBlock(input, image, index, bytes, cancellation);
                    if (image.Predictor == 2)
                        UndoPredictor(planes[plane], image, blockRows, components, input.LittleEndian, cancellation);
                }
                CopyBlock(pixels, planes, image, block, blockRows, input.LittleEndian, cancellation);
            }
            return new TexturePackingImage(image.OutputWidth, image.OutputHeight, pixels);
        }

        private static byte[] DecodeBlock(Input input, ImageInfo image, int index, int bytes,
            CancellationToken cancellation)
        {
            var decoded = new byte[bytes];
            using (var encoded = input.OpenRange(image.Offsets[index], image.ByteCounts[index]))
            {
                switch (image.Compression)
                {
                    case 1: ReadExactly(encoded, decoded, cancellation); break;
                    case 32773: DecodePackBits(encoded, decoded, cancellation); break;
                    case 5: DecodeLzw(encoded, decoded, cancellation); break;
                    case 8:
                    case 32946: DecodeDeflate(encoded, decoded, cancellation); break;
                    default: throw new NotSupportedException("Unsupported TIFF compression.");
                }
            }
            return decoded;
        }

        private static void ReadExactly(Stream input, byte[] output, CancellationToken cancellation)
        {
            int offset = 0;
            while (offset < output.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = input.Read(output, offset, Math.Min(output.Length - offset, 65536));
                if (count == 0) throw new InvalidDataException("Truncated TIFF pixel data.");
                offset += count;
            }
        }

        private static void DecodePackBits(Stream input, byte[] output, CancellationToken cancellation)
        {
            int written = 0;
            while (written < output.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int control = RequiredByte(input);
                if (control == 128) continue;
                int count = control < 128 ? control + 1 : 257 - control;
                if (count > output.Length - written)
                    throw new InvalidDataException("TIFF PackBits packet exceeds its strip or tile.");
                if (control < 128)
                {
                    for (int i = 0; i < count; i++) output[written++] = (byte)RequiredByte(input);
                }
                else
                {
                    byte value = (byte)RequiredByte(input);
                    for (int i = 0; i < count; i++) output[written++] = value;
                }
            }
        }

        private static void DecodeLzw(Stream input, byte[] output, CancellationToken cancellation)
        {
            var prefix = new ushort[4096];
            var suffix = new byte[4096];
            var stack = new byte[4096];
            var bits = new MsbBits(input);
            int width = 9, next = 258, previous = -1, written = 0;
            byte first = 0;
            bool cleared = false;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int code = bits.Read(width);
                if (code == 256)
                {
                    width = 9; next = 258; previous = -1; cleared = true;
                    continue;
                }
                if (!cleared) throw new InvalidDataException("TIFF LZW data must begin with a clear code.");
                if (code == 257)
                {
                    if (written != output.Length) throw new InvalidDataException("Truncated TIFF LZW pixels.");
                    return;
                }
                int current = code, count = 0;
                if (current >= next)
                {
                    if (current != next || previous < 0 || next >= 4096)
                        throw new InvalidDataException("Invalid TIFF LZW dictionary code.");
                    stack[count++] = first;
                    current = previous;
                }
                while (current >= 256)
                {
                    if (current >= next || count >= stack.Length - 1)
                        throw new InvalidDataException("Invalid TIFF LZW dictionary chain.");
                    stack[count++] = suffix[current];
                    current = prefix[current];
                }
                first = (byte)current;
                stack[count++] = first;
                if (count > output.Length - written)
                    throw new InvalidDataException("TIFF LZW data exceeds its strip or tile.");
                for (int i = count - 1; i >= 0; i--) output[written++] = stack[i];
                if (previous >= 0 && next < 4096)
                {
                    prefix[next] = (ushort)previous;
                    suffix[next++] = first;
                    // TIFF uses MSB-first codes and the early-change width transition.
                    if (width < 12 && next == (1 << width) - 1) width++;
                }
                previous = code;
            }
        }

        private static void DecodeDeflate(RangeStream input, byte[] output, CancellationToken cancellation)
        {
            if (input.Length < 6) throw new InvalidDataException("Truncated TIFF zlib stream.");
            int cmf = RequiredByte(input), flags = RequiredByte(input);
            if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) + flags) % 31 != 0 || (flags & 32) != 0)
                throw new InvalidDataException("TIFF Deflate requires a valid zlib stream without a preset dictionary.");
            long compressedLength = input.Length - 6;
            using (var payload = new RangeStream(input, compressedLength, cancellation))
            using (var inflater = new DeflateStream(payload, CompressionMode.Decompress, true))
            {
                ReadExactly(inflater, output, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (inflater.ReadByte() != -1)
                    throw new InvalidDataException("TIFF Deflate data exceeds its strip or tile.");
            }
            // DeflateStream can finish before reading all padding in the bounded payload.
            input.SkipTo(input.Length - 4);
            uint expected = (uint)RequiredByte(input) << 24 | (uint)RequiredByte(input) << 16 |
                (uint)RequiredByte(input) << 8 | (uint)RequiredByte(input);
            uint a = 1, b = 0;
            for (int start = 0; start < output.Length; start += 5552)
            {
                cancellation.ThrowIfCancellationRequested();
                int end = Math.Min(start + 5552, output.Length);
                // Adler-32's standard NMAX keeps both accumulators within uint.
                for (int i = start; i < end; i++) { a += output[i]; b += a; }
                a %= 65521;
                b %= 65521;
            }
            if ((b << 16 | a) != expected) throw new InvalidDataException("TIFF zlib checksum mismatch.");
        }

        private static int RequiredByte(Stream stream)
        {
            int value = stream.ReadByte();
            if (value < 0) throw new InvalidDataException("Truncated TIFF compressed data.");
            return value;
        }

        private static void UndoPredictor(byte[] data, ImageInfo image, int rows, int components,
            bool littleEndian, CancellationToken cancellation)
        {
            int rowSamples = image.BlockWidth * components;
            int stride = rowSamples * image.SampleBytes;
            for (int row = 0; row < rows; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                int start = row * stride;
                for (int sample = components; sample < rowSamples; sample++)
                {
                    if ((sample & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    int offset = start + sample * image.SampleBytes;
                    int prior = offset - components * image.SampleBytes;
                    if (image.SampleBytes == 1) data[offset] = unchecked((byte)(data[offset] + data[prior]));
                    else Write16(data, offset, unchecked((ushort)(Read16(data, offset, littleEndian) +
                        Read16(data, prior, littleEndian))), littleEndian);
                }
            }
        }

        private static void CopyBlock(Color32[] pixels, byte[][] planes, ImageInfo image, int block, int rows,
            bool littleEndian, CancellationToken cancellation)
        {
            int left = image.Tiled ? block % image.BlocksAcross * image.BlockWidth : 0;
            int top = image.Tiled ? block / image.BlocksAcross * image.BlockHeight : block * image.BlockHeight;
            int visibleWidth = Math.Min(image.BlockWidth, image.Width - left);
            int visibleRows = Math.Min(rows, image.Height - top);
            for (int y = 0; y < visibleRows; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < visibleWidth; x++)
                {
                    if ((x & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    int sample = y * image.BlockWidth + x;
                    double r = ReadSample(planes, image, sample, 0, littleEndian);
                    double g = image.Gray ? r : ReadSample(planes, image, sample, 1, littleEndian);
                    double b = image.Gray ? r : ReadSample(planes, image, sample, 2, littleEndian);
                    if (image.Photo == 0) { r = 1 - r; g = 1 - g; b = 1 - b; }
                    double alpha = image.Alpha == 0 ? 1 : ReadSample(planes, image, sample, image.Samples - 1, littleEndian);
                    if (image.Alpha == 1)
                    {
                        if (alpha > 0) { r /= alpha; g /= alpha; b /= alpha; }
                        else { r = 0; g = 0; b = 0; }
                    }
                    int target = OutputIndex(left + x, top + y, image);
                    pixels[target] = new Color32(ToByte(r), ToByte(g), ToByte(b), ToByte(alpha));
                }
            }
        }

        private static double ReadSample(byte[][] planes, ImageInfo image, int pixel, int component, bool littleEndian)
        {
            byte[] data = planes[image.Planar ? component : 0];
            int offset = (image.Planar ? pixel : pixel * image.Samples + component) * image.SampleBytes;
            if (image.Bits == 8) return data[offset] / 255.0;
            if (image.Bits == 16) return Read16(data, offset, littleEndian) / 65535.0;
            uint value = littleEndian ? (uint)data[offset] | (uint)data[offset + 1] << 8 |
                (uint)data[offset + 2] << 16 | (uint)data[offset + 3] << 24 :
                (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3];
            return new FloatBits { Bits = value }.Value;
        }

        private static byte ToByte(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 0;
            if (value >= 1) return 255;
            return (byte)(value * 255 + 0.5);
        }

        private static int OutputIndex(int x, int y, ImageInfo image)
        {
            int targetX, targetY;
            switch (image.Orientation)
            {
                case 2: targetX = image.Width - 1 - x; targetY = y; break;
                case 3: targetX = image.Width - 1 - x; targetY = image.Height - 1 - y; break;
                case 4: targetX = x; targetY = image.Height - 1 - y; break;
                case 5: targetX = y; targetY = x; break;
                case 6: targetX = image.Height - 1 - y; targetY = x; break;
                case 7: targetX = image.Height - 1 - y; targetY = image.Width - 1 - x; break;
                case 8: targetX = y; targetY = image.Width - 1 - x; break;
                default: targetX = x; targetY = y; break;
            }
            return (image.OutputHeight - 1 - targetY) * image.OutputWidth + targetX;
        }

        private static ushort Read16(byte[] data, int offset, bool littleEndian)
            => (ushort)(littleEndian ? data[offset] | data[offset + 1] << 8 : data[offset] << 8 | data[offset + 1]);

        private static void Write16(byte[] data, int offset, ushort value, bool littleEndian)
        {
            data[offset] = littleEndian ? (byte)value : (byte)(value >> 8);
            data[offset + 1] = littleEndian ? (byte)(value >> 8) : (byte)value;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public uint Bits;
            [FieldOffset(0)] public float Value;
        }

        private sealed class ImageInfo
        {
            internal readonly int Width, Height, Samples, Bits, Photo, Alpha, Compression, Predictor, Orientation;
            internal readonly int BlockWidth, BlockHeight, BlocksAcross, BlocksPerPlane;
            internal readonly bool Planar, Tiled, Gray;
            internal readonly uint[] Offsets, ByteCounts;
            internal int SampleBytes => Bits / 8;
            internal int OutputWidth => Orientation >= 5 ? Height : Width;
            internal int OutputHeight => Orientation >= 5 ? Width : Height;

            internal ImageInfo(Dictionary<int, uint[]> tags)
            {
                Width = Dimension(tags, 256);
                Height = Dimension(tags, 257);
                if ((long)Width * Height * 4 > MaximumImageBytes)
                    throw new InvalidDataException("TIFF image exceeds the 64 megapixel decoded pixel limit.");
                Photo = Scalar(tags, 262, -1);
                Gray = Photo == 0 || Photo == 1;
                if (!Gray && Photo != 2)
                    throw new NotSupportedException("TIFF must contain RGB or grayscale pixels. Convert CMYK/palette images to RGB.");
                Samples = Scalar(tags, 277, 1);
                int colorSamples = Gray ? 1 : 3;
                if (Samples < colorSamples || Samples > colorSamples + 1)
                    throw new NotSupportedException("TIFF supports grayscale/RGB with at most one alpha sample.");
                Bits = Uniform(tags, 258, Samples, 1);
                int format = Uniform(tags, 339, Samples, 1);
                if (!(format == 1 && (Bits == 8 || Bits == 16) || format == 3 && Bits == 32))
                    throw new NotSupportedException("TIFF samples must be 8/16-bit unsigned integers or 32-bit float.");
                Compression = Scalar(tags, 259, 1);
                if (Compression != 1 && Compression != 5 && Compression != 8 && Compression != 32946 && Compression != 32773)
                    throw new NotSupportedException("TIFF compression must be raw, LZW, Deflate or PackBits. Resave JPEG-compressed TIFF as LZW.");
                Predictor = Scalar(tags, 317, 1);
                if (Predictor != 1 && Predictor != 2 || Predictor == 2 && Bits == 32)
                    throw new NotSupportedException("TIFF predictor must be none or integer horizontal differencing. Resave float TIFF without prediction.");
                if (Scalar(tags, 266, 1) != 1)
                    throw new NotSupportedException("TIFF reverse bit fill order is unsupported. Resave with standard fill order.");
                int planar = Scalar(tags, 284, 1);
                if (planar != 1 && planar != 2) throw new InvalidDataException("Invalid TIFF planar configuration.");
                Planar = planar == 2;
                Orientation = Scalar(tags, 274, 1);
                if (Orientation < 1 || Orientation > 8) throw new InvalidDataException("Invalid TIFF orientation.");
                if (tags.TryGetValue(338, out var extras))
                {
                    if (Samples != colorSamples + extras.Length || extras.Length != 1 || extras[0] > 2)
                        throw new NotSupportedException("TIFF ExtraSamples must specify one associated or straight alpha sample.");
                    Alpha = (int)extras[0];
                }
                Tiled = tags.ContainsKey(324);
                if (Tiled)
                {
                    if (tags.ContainsKey(273)) throw new InvalidDataException("TIFF cannot mix strips and tiles.");
                    BlockWidth = Dimension(tags, 322);
                    BlockHeight = Dimension(tags, 323);
                    BlocksAcross = (Width + BlockWidth - 1) / BlockWidth;
                    BlocksPerPlane = checked(BlocksAcross * ((Height + BlockHeight - 1) / BlockHeight));
                    Offsets = Required(tags, 324);
                    ByteCounts = Required(tags, 325);
                }
                else
                {
                    BlockWidth = Width;
                    uint rows = tags.TryGetValue(278, out var rowValues) ? Single(rowValues) : uint.MaxValue;
                    if (rows == 0) throw new InvalidDataException("Invalid TIFF rows per strip.");
                    BlockHeight = (int)Math.Min(rows, (uint)Height);
                    BlocksAcross = 1;
                    BlocksPerPlane = (Height + BlockHeight - 1) / BlockHeight;
                    Offsets = Required(tags, 273);
                    ByteCounts = Required(tags, 279);
                }
                int expectedBlocks = checked(BlocksPerPlane * (Planar ? Samples : 1));
                if (Offsets.Length != expectedBlocks || ByteCounts.Length != expectedBlocks)
                    throw new InvalidDataException("TIFF strip/tile counts do not match its dimensions and sample planes.");
            }

            internal int BlockRows(int block) => Tiled ? BlockHeight : Math.Min(BlockHeight, Height - block * BlockHeight);

            internal void ValidateBlocks(long length, CancellationToken cancellation)
            {
                for (int i = 0; i < Offsets.Length; i++)
                {
                    if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                    if (Offsets[i] < 8 || ByteCounts[i] == 0 || (long)Offsets[i] + ByteCounts[i] > length)
                        throw new InvalidDataException("TIFF strip/tile points outside the file.");
                    long expected = (long)BlockWidth * BlockRows(i % BlocksPerPlane) * (Planar ? 1 : Samples) * SampleBytes;
                    if (expected > MaximumBlockBytes || expected * (Planar ? Samples : 1) > MaximumImageBytes)
                        throw new InvalidDataException("TIFF strip/tile exceeds the decoded memory limit. Resave with smaller strips or tiles.");
                    if (Compression == 1 && ByteCounts[i] < expected)
                        throw new InvalidDataException("Truncated TIFF strip/tile.");
                }
            }

            private static int Dimension(Dictionary<int, uint[]> tags, int id)
            {
                uint value = Single(Required(tags, id));
                if (value < 1 || value > 16384) throw new InvalidDataException("Invalid TIFF dimensions (maximum 16384).");
                return (int)value;
            }

            private static uint[] Required(Dictionary<int, uint[]> tags, int id)
            {
                if (!tags.TryGetValue(id, out var value)) throw new InvalidDataException("Missing required TIFF tag " + id + ".");
                return value;
            }

            private static uint Single(uint[] values)
            {
                if (values.Length != 1) throw new InvalidDataException("TIFF scalar tag contains multiple values.");
                return values[0];
            }

            private static int Scalar(Dictionary<int, uint[]> tags, int id, int fallback)
            {
                if (!tags.TryGetValue(id, out var value)) return fallback;
                uint result = Single(value);
                if (result > int.MaxValue) throw new InvalidDataException("TIFF tag value exceeds its supported range.");
                return (int)result;
            }

            private static int Uniform(Dictionary<int, uint[]> tags, int id, int samples, int fallback)
            {
                if (!tags.TryGetValue(id, out var values)) return fallback;
                if (values.Length != 1 && values.Length != samples)
                    throw new InvalidDataException("TIFF sample tag count does not match SamplesPerPixel.");
                uint first = values[0];
                foreach (uint value in values)
                    if (value != first) throw new NotSupportedException("TIFF samples must share their bit depth and sample format.");
                if (first > int.MaxValue) throw new InvalidDataException("Invalid TIFF sample tag.");
                return (int)first;
            }
        }

        private sealed class Input
        {
            private readonly Stream stream;
            private readonly CancellationToken cancellation;
            private readonly byte[] scratch = new byte[4];
            internal bool LittleEndian { get; private set; }
            internal long Length => stream.Length;

            internal Input(Stream stream, CancellationToken cancellation)
            { this.stream = stream; this.cancellation = cancellation; }

            internal Dictionary<int, uint[]> ReadDirectory()
            {
                stream.Position = 0;
                int first = RequiredByte(stream), second = RequiredByte(stream);
                if (first == 73 && second == 73) LittleEndian = true;
                else if (first != 77 || second != 77) throw new InvalidDataException("Invalid TIFF byte order signature.");
                int version = Word();
                if (version == 43) throw new NotSupportedException("BigTIFF is unsupported. Resave as a classic TIFF smaller than 4 GiB.");
                if (version != 42) throw new InvalidDataException("Invalid TIFF version.");
                uint directory = Dword();
                CheckRange(directory, 2);
                stream.Position = directory;
                int entries = Word();
                CheckRange(directory + 2L, (long)entries * 12 + 4);
                var tags = new Dictionary<int, uint[]>();
                for (int i = 0; i < entries; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int tag = Word(), type = Word();
                    uint count = Dword(), value = Dword();
                    if (!Relevant(tag)) continue;
                    if (tags.ContainsKey(tag)) throw new InvalidDataException("Duplicate TIFF image tag.");
                    int size = type == 1 ? 1 : type == 3 ? 2 : type == 4 ? 4 : 0;
                    if (size == 0) throw new InvalidDataException("Invalid TIFF image tag type.");
                    if (count == 0 || count > MaximumTagValues) throw new InvalidDataException("Invalid TIFF tag value count.");
                    long resume = stream.Position;
                    long bytes = (long)count * size;
                    if (bytes <= 4) stream.Position = resume - 4;
                    else { CheckRange(value, bytes); stream.Position = value; }
                    var values = new uint[(int)count];
                    for (int j = 0; j < values.Length; j++)
                    {
                        if ((j & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                        values[j] = size == 1 ? (uint)RequiredByte(stream) : size == 2 ? Word() : Dword();
                    }
                    tags.Add(tag, values);
                    stream.Position = resume;
                }
                return tags;
            }

            private static bool Relevant(int tag)
            {
                switch (tag)
                {
                    case 256: case 257: case 258: case 259: case 262: case 266: case 273: case 274:
                    case 277: case 278: case 279: case 284: case 317: case 322: case 323: case 324:
                    case 325: case 338: case 339: return true;
                    default: return false;
                }
            }

            private ushort Word()
            {
                int a = RequiredByte(stream), b = RequiredByte(stream);
                return (ushort)(LittleEndian ? a | b << 8 : a << 8 | b);
            }

            private uint Dword()
            {
                for (int i = 0; i < 4; i++) scratch[i] = (byte)RequiredByte(stream);
                return LittleEndian ? (uint)scratch[0] | (uint)scratch[1] << 8 | (uint)scratch[2] << 16 | (uint)scratch[3] << 24 :
                    (uint)scratch[0] << 24 | (uint)scratch[1] << 16 | (uint)scratch[2] << 8 | scratch[3];
            }

            private void CheckRange(long offset, long count)
            {
                if (offset < 8 || offset > Length || count < 0 || count > Length - offset)
                    throw new InvalidDataException("TIFF directory/tag points outside the file.");
            }

            internal RangeStream OpenRange(uint offset, uint count)
            {
                CheckRange(offset, count);
                stream.Position = offset;
                return new RangeStream(stream, count, cancellation);
            }
        }

        private sealed class MsbBits
        {
            private readonly Stream stream;
            private uint buffer;
            private int available;
            internal MsbBits(Stream stream) { this.stream = stream; }
            internal int Read(int width)
            {
                while (available < width) { buffer = buffer << 8 | (uint)RequiredByte(stream); available += 8; }
                available -= width;
                return (int)(buffer >> available & ((1u << width) - 1));
            }
        }

        /// <summary>Limits compressed reads without taking ownership of the source stream.</summary>
        private sealed class RangeStream : Stream
        {
            private readonly Stream source;
            private readonly long length;
            private readonly CancellationToken cancellation;
            private long position;
            internal RangeStream(Stream source, long length, CancellationToken cancellation)
            { this.source = source; this.length = length; this.cancellation = cancellation; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = source.Read(buffer, offset, (int)Math.Min(Math.Min(count, 65536), length - position));
                position += read;
                return read;
            }
            public override int ReadByte()
            {
                cancellation.ThrowIfCancellationRequested();
                if (position >= length) return -1;
                int value = source.ReadByte();
                if (value >= 0) position++;
                return value;
            }
            internal void SkipTo(long target)
            {
                cancellation.ThrowIfCancellationRequested();
                if (target < position || target > length) throw new InvalidDataException("Invalid TIFF compressed range.");
                if (source.CanSeek) { source.Seek(target - position, SeekOrigin.Current); position = target; return; }
                while (position < target) RequiredByte(this);
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
