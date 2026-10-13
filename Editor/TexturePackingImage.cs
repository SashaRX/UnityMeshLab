using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Byte-domain channel packing. Pixels use Unity's bottom-left origin.</summary>
    public sealed class TexturePackingImage
    {
        internal const string SupportedInputs = "PNG, JPG/JPEG, TGA, PSD, TIF/TIFF";
        public int Width { get; }
        public int Height { get; }
        public Color32[] Pixels { get; }

        public TexturePackingImage(int width, int height, Color32[] pixels)
        {
            if (width < 1 || height < 1 || width > 16384 || height > 16384 ||
                pixels == null || (long)width * height != pixels.Length)
                throw new ArgumentException("Invalid image dimensions or pixel count.");
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public static bool Supports(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".tga" ||
                extension == ".psd" || extension == ".tif" || extension == ".tiff";
        }

        internal static bool UsesStreamDecoder(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".tga" || extension == ".psd" || extension == ".tif" || extension == ".tiff";
        }

        internal static TexturePackingImage DecodeStream(string path, Stream stream, CancellationToken cancellation)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".psd") return TexturePackingPsd.Decode(stream, cancellation);
            if (extension == ".tif" || extension == ".tiff") return TexturePackingTiff.Decode(stream, cancellation);
            return DecodeTga(stream, cancellation);
        }

        public static TexturePackingImage Load(string path)
        {
            if (!Supports(path)) throw new NotSupportedException("Supported inputs: " + SupportedInputs + ".");
            if (UsesStreamDecoder(path))
                using (var stream = File.OpenRead(path)) return DecodeStream(path, stream, default);
            byte[] bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, false))
                    throw new InvalidDataException("Cannot decode image: " + path);
                return new TexturePackingImage(texture.width, texture.height, texture.GetPixels32());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        public Color32 Sample(int x, int y, int targetWidth, int targetHeight)
        {
            float sx = Mathf.Clamp((x + 0.5f) * Width / targetWidth - 0.5f, 0, Width - 1);
            float sy = Mathf.Clamp((y + 0.5f) * Height / targetHeight - 0.5f, 0, Height - 1);
            int x0 = (int)sx, y0 = (int)sy;
            int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
            Color32 a = Pixels[y0 * Width + x0], b = Pixels[y0 * Width + x1];
            Color32 c = Pixels[y1 * Width + x0], d = Pixels[y1 * Width + x1];
            float tx = sx - x0, ty = sy - y0;
            return new Color32(Interpolate(a.r, b.r, c.r, d.r, tx, ty),
                Interpolate(a.g, b.g, c.g, d.g, tx, ty),
                Interpolate(a.b, b.b, c.b, d.b, tx, ty),
                Interpolate(a.a, b.a, c.a, d.a, tx, ty));
        }

        private static byte Interpolate(byte a, byte b, byte c, byte d, float x, float y)
        {
            return (byte)Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(a, b, x), Mathf.Lerp(c, d, x), y));
        }

        public static byte Channel(Color32 pixel, int channel)
        {
            switch (channel)
            {
                case 0: return pixel.r;
                case 1: return pixel.g;
                case 2: return pixel.b;
                case 3: return pixel.a;
                default: throw new ArgumentOutOfRangeException(nameof(channel));
            }
        }

        public static TexturePackingImage Compose(int width, int height, TexturePackingImage rgb,
            TexturePackingImage[] sources, int[] channels, byte[] defaults, bool sourceAlpha,
            CancellationToken cancellation = default)
        {
            if (width < 1 || height < 1 || width > 8192 || height > 8192)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (sources == null || sources.Length != 4 || channels == null || channels.Length != 4 ||
                defaults == null || defaults.Length != 4)
                throw new ArgumentException("Four output channel bindings are required.");
            cancellation.ThrowIfCancellationRequested();
            var pixels = new Color32[checked(width * height)];
            Action<int> composeRow = y =>
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    Color32 pixel = rgb != null ? rgb.Sample(x, y, width, height) :
                        new Color32(defaults[0], defaults[1], defaults[2], defaults[3]);
                    if (!sourceAlpha) pixel.a = defaults[3];
                    for (int channel = 0; channel < 4; channel++)
                    {
                        if (sources[channel] == null) continue;
                        byte value = Channel(sources[channel].Sample(x, y, width, height), channels[channel]);
                        switch (channel)
                        {
                            case 0: pixel.r = value; break;
                            case 1: pixel.g = value; break;
                            case 2: pixel.b = value; break;
                            case 3: pixel.a = value; break;
                        }
                    }
                    pixels[y * width + x] = pixel;
                }
            };
            // Parallelize disjoint rows of one output, rather than holding several full outputs in memory.
            if ((long)width * height >= 262144)
                Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellation,
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)) }, composeRow);
            else
                for (int y = 0; y < height; y++) composeRow(y);
            return new TexturePackingImage(width, height, pixels);
        }

        /// <summary>32-bit BGRA, type 10, bottom-left origin, eight alpha bits.</summary>
        public void WriteTgaRle(Stream stream, CancellationToken cancellation = default)
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                var header = new byte[18];
                header[2] = 10;
                header[12] = (byte)Width;
                header[13] = (byte)(Width >> 8);
                header[14] = (byte)Height;
                header[15] = (byte)(Height >> 8);
                header[16] = 32;
                header[17] = 8;
                writer.Write(header);
                // Keep packets within scanlines for compatibility with strict TGA readers.
                for (int row = 0; row < Height; row++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    WriteRow(writer, row * Width, (row + 1) * Width);
                }
            }
        }

        private void WriteRow(BinaryWriter writer, int start, int end)
        {
            while (start < end)
            {
                int run = RunLength(start, end);
                if (run > 1)
                {
                    writer.Write((byte)(0x80 | (run - 1)));
                    WritePixel(writer, Pixels[start]);
                    start += run;
                    continue;
                }
                int rawStart = start++;
                while (start < end && start - rawStart < 128 && RunLength(start, end) == 1) start++;
                writer.Write((byte)(start - rawStart - 1));
                for (int i = rawStart; i < start; i++) WritePixel(writer, Pixels[i]);
            }
        }

        private int RunLength(int start, int end)
        {
            int count = 1;
            while (count < 128 && start + count < end && Pixels[start].Equals(Pixels[start + count])) count++;
            return count;
        }

        private static void WritePixel(BinaryWriter writer, Color32 pixel)
        {
            writer.Write(pixel.b);
            writer.Write(pixel.g);
            writer.Write(pixel.r);
            writer.Write(pixel.a);
        }

        public static TexturePackingImage DecodeTga(byte[] bytes, CancellationToken cancellation = default)
        {
            using (var stream = new MemoryStream(bytes, false)) return DecodeTga(stream, cancellation);
        }

        /// <summary>Decode directly from a seekable source; leave its ownership with the caller.</summary>
        public static TexturePackingImage DecodeTga(Stream stream, CancellationToken cancellation = default)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("TGA input must be readable and seekable.", nameof(stream));
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
            {
                byte[] header = reader.ReadBytes(18);
                if (header.Length != 18) throw new InvalidDataException("Truncated TGA header.");
                int type = header[2], depth = header[16];
                bool gray = type == 3 || type == 11;
                bool rle = type == 10 || type == 11;
                bool hasAlpha = (header[17] & 15) != 0;
                if (header[1] != 0 || (header[17] & 0xC0) != 0 ||
                    !(type == 2 || type == 3 || type == 10 || type == 11) ||
                    (gray ? depth != 8 : depth != 24 && depth != 32))
                    throw new NotSupportedException("TGA must be non-paletted 24/32-bit RGB(A) or 8-bit grayscale, raw or RLE.");
                int width = header[12] | header[13] << 8;
                int height = header[14] | header[15] << 8;
                if (width < 1 || height < 1 || width > 16384 || height > 16384)
                    throw new InvalidDataException("Invalid TGA dimensions.");
                if (reader.ReadBytes(header[0]).Length != header[0]) throw new EndOfStreamException();
                cancellation.ThrowIfCancellationRequested();
                long countPixels = (long)width * height;
                long minimum = rle ? ((countPixels + 127) / 128) * (1 + depth / 8) : countPixels * (depth / 8);
                // Reject impossible payloads before allocating a potentially huge pixel buffer.
                if (reader.BaseStream.Length - reader.BaseStream.Position < minimum) throw new EndOfStreamException();
                var pixels = new Color32[checked(width * height)];
                int index = 0;
                while (index < pixels.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int packet = rle ? reader.ReadByte() : 0;
                    int count = rle ? (packet & 127) + 1 : 1;
                    if (count > pixels.Length - index) throw new InvalidDataException("TGA packet exceeds image size.");
                    bool repeat = rle && (packet & 128) != 0;
                    Color32 pixel = repeat ? ReadPixel(reader, depth, gray, hasAlpha) : default;
                    for (int i = 0; i < count; i++, index++)
                    {
                        if (!repeat) pixel = ReadPixel(reader, depth, gray, hasAlpha);
                        int x = index % width, y = index / width;
                        if ((header[17] & 0x10) != 0) x = width - 1 - x;
                        if ((header[17] & 0x20) != 0) y = height - 1 - y;
                        pixels[y * width + x] = pixel;
                    }
                }
                return new TexturePackingImage(width, height, pixels);
            }
        }

        private static Color32 ReadPixel(BinaryReader reader, int depth, bool gray, bool hasAlpha)
        {
            byte b = reader.ReadByte();
            if (gray) return new Color32(b, b, b, 255);
            byte g = reader.ReadByte(), r = reader.ReadByte();
            byte alpha = depth == 32 ? reader.ReadByte() : (byte)255;
            return new Color32(r, g, b, hasAlpha ? alpha : (byte)255);
        }
    }
}
