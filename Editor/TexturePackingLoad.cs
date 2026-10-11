using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Poll on the editor thread. File reads and image decompression do not block IMGUI.</summary>
    public sealed class TexturePackingLoad : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task<TexturePackingImage> task;
        private Task<Color32[]> pixelAllocation;
        private UnityWebRequest request;
        private Texture2D texture;
        private Color32[] pixels;
        private int offset;
        private bool disposed;

        public TexturePackingLoad(string path)
        {
            try
            {
                if (!TexturePackingImage.Supports(path)) throw new NotSupportedException("Supported inputs: PNG, JPG/JPEG, TGA.");
                if (string.Equals(Path.GetExtension(path), ".tga", StringComparison.OrdinalIgnoreCase))
                {
                    var token = cancellation.Token;
                    task = Task.Run(() => ReadTga(path, token), token);
                }
                else
                {
                    // DownloadHandlerTexture decodes PNG/JPEG on Unity's worker threads, including file:// URLs.
                    request = UnityWebRequestTexture.GetTexture(new Uri(Path.GetFullPath(path)).AbsoluteUri, false);
                    request.timeout = 30;
                    request.SendWebRequest();
                }
            }
            catch { Dispose(); throw; }
        }

        private static TexturePackingImage ReadTga(string path, CancellationToken token)
        {
            using (var input = File.OpenRead(path))
            using (var bytes = new MemoryStream())
            {
                var buffer = new byte[65536];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    bytes.Write(buffer, 0, read);
                }
                return TexturePackingImage.DecodeTga(bytes.ToArray(), token);
            }
        }

        public bool TryComplete(out TexturePackingImage image)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TexturePackingLoad));
            image = null;
            if (task != null)
            {
                if (!task.IsCompleted) return false;
                image = task.GetAwaiter().GetResult();
                return true;
            }
            if (!request.isDone) return false;
            if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.error);
            if (texture == null)
            {
                texture = DownloadHandlerTexture.GetContent(request);
                texture.hideFlags = HideFlags.HideAndDontSave;
                if (texture.width > 16384 || texture.height > 16384) throw new InvalidDataException("Image dimensions exceed 16384.");
                if (texture.format != TextureFormat.RGB24 && texture.format != TextureFormat.RGBA32 &&
                    texture.format != TextureFormat.ARGB32 && texture.format != TextureFormat.BGRA32)
                    throw new InvalidDataException("Unsupported decoded pixel format: " + texture.format);
                int count = checked(texture.width * texture.height);
                var token = cancellation.Token;
                pixelAllocation = Task.Run(() => new Color32[count], token);
                return false;
            }
            if (!pixelAllocation.IsCompleted) return false;
            if (pixels == null) pixels = pixelAllocation.GetAwaiter().GetResult();
            // A large GetPixels32 copy can also stall the editor. Copy a bounded slice per update.
            var raw = texture.GetPixelData<byte>(0);
            TextureFormat format = texture.format;
            int stride = format == TextureFormat.RGB24 ? 3 : 4;
            int end = Math.Min(offset + 32768, pixels.Length);
            for (; offset < end; offset++)
            {
                int i = offset * stride;
                if (format == TextureFormat.ARGB32)
                    pixels[offset] = new Color32(raw[i + 1], raw[i + 2], raw[i + 3], raw[i]);
                else if (format == TextureFormat.BGRA32)
                    pixels[offset] = new Color32(raw[i + 2], raw[i + 1], raw[i], raw[i + 3]);
                else
                    pixels[offset] = new Color32(raw[i], raw[i + 1], raw[i + 2], stride == 4 ? raw[i + 3] : (byte)255);
            }
            if (offset != pixels.Length) return false;
            image = new TexturePackingImage(texture.width, texture.height, pixels);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            if (request != null)
            {
                // Closing after download but before the first poll must release the decoder's texture too.
                if (texture == null && request.isDone && request.result == UnityWebRequest.Result.Success)
                    texture = DownloadHandlerTexture.GetContent(request);
                request.Abort();
                request.Dispose();
                request = null;
            }
            if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
            pixels = null;
            Task pending = (Task)task ?? pixelAllocation;
            if (pending == null) cancellation.Dispose();
            else
            {
                var owner = cancellation;
                _ = pending.ContinueWith(finished =>
                {
                    _ = finished.Exception;
                    owner.Dispose();
                }, TaskScheduler.Default);
                task = null;
                pixelAllocation = null;
            }
        }
    }
}
