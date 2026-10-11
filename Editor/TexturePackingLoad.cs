using System;
using System.Collections.Generic;
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
        private Task<SourceLease> leaseTask;
        private SourceLease lease;
        private Task validation;
        private readonly string sourcePath;
        private UnityWebRequest request;
        private Texture2D texture;
        private Color32[] pixels;
        private int offset;
        private bool disposed;

        public TexturePackingLoad(string path)
        {
            try
            {
                sourcePath = Path.GetFullPath(path);
                if (!TexturePackingImage.Supports(path)) throw new NotSupportedException("Supported inputs: PNG, JPG/JPEG, TGA.");
                if (string.Equals(Path.GetExtension(path), ".tga", StringComparison.OrdinalIgnoreCase))
                {
                    var token = cancellation.Token;
                    string fullPath = sourcePath;
                    task = Task.Run(() => ReadTga(fullPath, token), token);
                }
                else
                {
                    // DownloadHandlerTexture decodes PNG/JPEG on Unity's worker threads, including file:// URLs.
                    var token = cancellation.Token;
                    string fullPath = sourcePath;
                    leaseTask = Task.Run(() => new SourceLease(fullPath, token), token);
                }
            }
            catch { Dispose(); throw; }
        }

        private static TexturePackingImage ReadTga(string path, CancellationToken token)
        {
            using (var source = new SourceLease(path, token))
            {
                var image = TexturePackingImage.DecodeTga(source.Input, token);
                source.Validate(token);
                return image;
            }
        }

        private sealed class SourceLease : IDisposable
        {
            public FileStream Input { get; }
            private readonly string path;
            private readonly string resolved;
            private readonly long length;
            private readonly DateTime written;
            private readonly DateTime created;
            private readonly TexturePackingFiles.FileIdentity identity;

            public SourceLease(string path, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                this.path = path;
                Input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try
                {
                    var info = new FileInfo(path);
                    length = Input.Length;
                    written = info.LastWriteTimeUtc;
                    created = info.CreationTimeUtc;
                    resolved = TexturePackingFiles.ResolveExistingFile(path);
                    if (Path.DirectorySeparatorChar == '\\') identity = TexturePackingFiles.GetIdentity(Input);
                    Validate(token);
                }
                catch { Input.Dispose(); throw; }
            }

            public void Validate(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var current = new FileInfo(path);
                if (!current.Exists || current.Length != length || Input.Length != length ||
                    current.LastWriteTimeUtc != written || current.CreationTimeUtc != created ||
                    !string.Equals(resolved, TexturePackingFiles.ResolveExistingFile(path), StringComparison.Ordinal))
                    throw new IOException("Source changed while loading; load it again: " + path);
                if (Path.DirectorySeparatorChar == '\\')
                {
                    using (var currentInput = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        var currentIdentity = TexturePackingFiles.GetIdentity(currentInput);
                        if (identity.volume != currentIdentity.volume || identity.low != currentIdentity.low || identity.high != currentIdentity.high)
                            throw new IOException("Source replaced while loading; load it again: " + path);
                    }
                }
            }

            public void Dispose() => Input.Dispose();
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
            if (request == null)
            {
                if (!leaseTask.IsCompleted) return false;
                lease = leaseTask.GetAwaiter().GetResult();
                var parameters = DownloadedTextureParams.Default;
                parameters.readable = true;
                parameters.mipmapChain = false;
                parameters.linearColorSpace = true;
                request = UnityWebRequestTexture.GetTexture(new Uri(sourcePath).AbsoluteUri, parameters);
                request.timeout = 30;
                request.SendWebRequest();
                return false;
            }
            if (!request.isDone) return false;
            if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.error);
            if (texture == null)
            {
                texture = DownloadHandlerTexture.GetContent(request);
                if (texture == null || request.result != UnityWebRequest.Result.Success)
                    throw new InvalidDataException("Cannot decode image: " + sourcePath);
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
            if (validation == null)
            {
                var token = cancellation.Token;
                var source = lease;
                validation = Task.Run(() => source.Validate(token), token);
                return false;
            }
            if (!validation.IsCompleted) return false;
            validation.GetAwaiter().GetResult();
            image = new TexturePackingImage(texture.width, texture.height, pixels);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            try
            {
                if (texture == null && request != null && request.isDone && request.result == UnityWebRequest.Result.Success)
                    texture = DownloadHandlerTexture.GetContent(request);
            }
            catch (Exception exception) { UvtLog.Warn("Texture decoder cleanup: " + exception.Message); }
            finally
            {
                try { request?.Abort(); }
                finally
                {
                    try { request?.Dispose(); }
                    finally
                    {
                        request = null;
                        try { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
                        finally
                        {
                            texture = null;
                            pixels = null;
                            ReleaseWorkers();
                        }
                    }
                }
            }
        }

        private void ReleaseWorkers()
        {
            var workers = new List<Task>();
            if (task != null) workers.Add(task);
            if (leaseTask != null) workers.Add(leaseTask);
            if (pixelAllocation != null) workers.Add(pixelAllocation);
            if (validation != null) workers.Add(validation);
            var source = lease;
            var acquiring = leaseTask;
            var owner = cancellation;
            _ = Task.WhenAll(workers).ContinueWith(finished =>
            {
                try
                {
                    _ = finished.Exception;
                    foreach (var worker in workers) _ = worker.Exception;
                    if (source != null) source.Dispose();
                    else if (acquiring != null && acquiring.Status == TaskStatus.RanToCompletion) acquiring.Result.Dispose();
                }
                catch (Exception exception) { UvtLog.Warn("Texture source cleanup: " + exception.Message); }
                finally { owner.Dispose(); }
            }, TaskScheduler.Default);
            task = null;
            pixelAllocation = null;
            leaseTask = null;
            validation = null;
            lease = null;
        }
    }
}
