using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Threading;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Texture output staging and filesystem path checks; never changes asset metadata.</summary>
    public static class TexturePackingFiles
    {
        public static StringComparison ComparisonForDirectory(string directory)
        {
            directory = Path.GetFullPath(directory);
            while (!Directory.Exists(directory)) directory = Path.GetDirectoryName(directory) ??
                throw new DirectoryNotFoundException("Cannot inspect filesystem case rules.");
            string name = ".meshlab-case-" + Guid.NewGuid().ToString("N");
            string probe = Path.Combine(directory, name);
            try
            {
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.WriteByte(0);
                return File.Exists(Path.Combine(directory, name.ToUpperInvariant())) ?
                    StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            }
            finally { Discard(probe); }
        }

        public sealed class PendingFile : IDisposable
        {
            public string Staged { get; }
            public string Destination { get; }
            private readonly bool existed;
            private readonly long length;
            private readonly DateTime modified;
            private string backup;
            private bool committed;
            private readonly byte[] contentHash;
            private FileStream verifiedSource;
            private DateTime verifiedCreation;
            private FileIdentity verifiedIdentity;

            public PendingFile(string staged, string destination, bool existed, long length, DateTime modified)
                : this(staged, destination, existed, length, modified, null) { }

            public PendingFile(string staged, string destination, bool existed, long length, DateTime modified,
                byte[] contentHash)
            {
                Staged = staged;
                Destination = destination;
                this.existed = existed;
                this.length = length;
                this.modified = modified;
                this.contentHash = existed ? contentHash ?? ContentHash(destination) : null;
            }

            public void ValidateUnchanged()
            {
                var current = new FileInfo(Destination);
                if (current.Exists != existed || (existed && (current.Length != length || current.LastWriteTimeUtc != modified)))
                    throw new IOException("Output changed during export; save again to confirm overwrite: " + Destination);
                if (!existed) return;
                if (verifiedSource == null) VerifyContent(CancellationToken.None);
                else if (current.CreationTimeUtc != verifiedCreation)
                    throw new IOException("Output replaced during export; save again to confirm overwrite: " + Destination);
                if (Path.DirectorySeparatorChar == '\\')
                {
                    using (var currentInput = new FileStream(Destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        var identity = GetIdentity(currentInput);
                        if (identity.volume != verifiedIdentity.volume || identity.low != verifiedIdentity.low || identity.high != verifiedIdentity.high)
                            throw new IOException("Output replaced during export; save again to confirm overwrite: " + Destination);
                    }
                }
            }

            public void VerifyContent(CancellationToken cancellation)
            {
                if (!existed) return;
                if (verifiedSource != null) { ValidateUnchanged(); return; }
                var stream = new FileStream(Destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try
                {
                    var before = new FileInfo(Destination);
                    if (before.Length != length || before.LastWriteTimeUtc != modified ||
                        !HashesEqual(contentHash, ContentHash(stream, cancellation)))
                        throw new IOException("Output changed during export; save again to confirm overwrite: " + Destination);
                    var after = new FileInfo(Destination);
                    if (after.Length != length || after.LastWriteTimeUtc != modified || after.CreationTimeUtc != before.CreationTimeUtc)
                        throw new IOException("Output changed while checking overwrite: " + Destination);
                    verifiedCreation = after.CreationTimeUtc;
                    if (Path.DirectorySeparatorChar == '\\') verifiedIdentity = GetIdentity(stream);
                    verifiedSource = stream; // Retain the read lease until installation; Windows denies in-place writers.
                }
                catch { stream.Dispose(); throw; }
            }

            public void Dispose()
            {
                verifiedSource?.Dispose();
                verifiedSource = null;
            }

            public void Install()
            {
                ValidateUnchanged();
                if (existed)
                {
                    backup = TemporaryPath(Destination);
                    File.Replace(Staged, Destination, backup);
                }
                else File.Move(Staged, Destination); // Fails rather than replacing a late-arriving file.
                committed = true;
                Dispose(); // The original is now the backup; release its lease before any rollback.
            }

            public void Rollback()
            {
                if (!committed) return;
                if (existed) File.Replace(backup, Destination, null);
                else File.Delete(Destination); // Newly installed file, before any AssetDatabase import.
                committed = false;
            }

            public void DiscardBackup() => Discard(backup);
        }

        public static void InstallBatch(IList<PendingFile> files)
        {
            try { InstallBatchCore(files); }
            finally { foreach (var file in files) file.Dispose(); }
        }

        private static void InstallBatchCore(IList<PendingFile> files)
        {
            foreach (var file in files) file.ValidateUnchanged();
            try
            {
                foreach (var file in files) file.Install();
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                for (int i = files.Count - 1; i >= 0; i--)
                {
                    try { files[i].Rollback(); }
                    catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
                }
                // Preserve backups if rollback itself fails, so the originals remain recoverable.
                if (failures.Count > 1) throw new AggregateException("Texture batch rollback failed; backups were retained.", failures);
                DiscardBackups(files);
                throw;
            }
            DiscardBackups(files);
        }

        public static byte[] ContentHash(string path, CancellationToken cancellation = default)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                return ContentHash(stream, cancellation);
        }

        private static byte[] ContentHash(Stream stream, CancellationToken cancellation)
        {
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                }
                cancellation.ThrowIfCancellationRequested();
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return hash.Hash;
            }
        }

        private static bool HashesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdentity
        {
            public ulong volume;
            public ulong low;
            public ulong high;
        }

        internal static FileIdentity GetIdentity(FileStream input)
        {
            if (!GetFileInformationByHandleEx(input.SafeFileHandle, 18, out var identity, 24))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return identity;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
            out FileIdentity information, uint size);

        public static string ResolveDirectory(string path)
        {
            path = Path.GetFullPath(path);
            var remaining = new List<string>();
            while (!Directory.Exists(path))
            {
                remaining.Add(Path.GetFileName(path));
                path = Path.GetDirectoryName(path) ?? throw new DirectoryNotFoundException("Cannot resolve output directory.");
            }
            string resolved;
            if (Path.DirectorySeparatorChar == '\\')
            {
                using (var directory = OpenDirectory(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
                {
                    if (directory.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    resolved = FinalHandlePath(directory);
                }
            }
            else resolved = ResolveUnixPath(path);
            for (int i = remaining.Count - 1; i >= 0; i--) resolved = Path.Combine(resolved, remaining[i]);
            return resolved;
        }

        private static void DiscardBackups(IList<PendingFile> files)
        {
            foreach (var file in files)
            {
                try { file.DiscardBackup(); }
                catch (Exception exception)
                {
                    // Installation/rollback already succeeded. Continue cleanup and preserve import finalization.
                    UvtLog.Warn("Texture backup cleanup: " + exception.Message);
                }
            }
        }

        public static string ResolveExistingFile(string path)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) return full;
            if (Path.DirectorySeparatorChar == '\\')
            {
                using (var file = new FileStream(full, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    return FinalHandlePath(file.SafeFileHandle);
                }
            }
            return ResolveUnixPath(full);
        }

        private static string FinalHandlePath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(32768);
            uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
            string resolved = buffer.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + resolved.Substring(8);
            return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved.Substring(4) : resolved;
        }

        private static string ResolveUnixPath(string path)
        {
            IntPtr result = RealPath(path, IntPtr.Zero);
            if (result == IntPtr.Zero) throw new IOException("Cannot resolve file path: " + path);
            try { return Marshal.PtrToStringAnsi(result); }
            finally { Free(result); }
        }

        public static bool SameSource(string source, string destination)
        {
            return string.Equals(ResolveExistingFile(source), ResolveExistingFile(destination),
                ComparisonForDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))));
        }

        public static string Stage(string destination, Action<Stream> write)
        {
            // Dot-prefixed temporary files are ignored by Unity. They are not imported assets.
            string temporary = TemporaryPath(destination);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }
                return temporary;
            }
            catch
            {
                Discard(temporary);
                throw;
            }
        }

        private static string TemporaryPath(string destination)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination)),
                ".meshlab-texture-" + Guid.NewGuid().ToString("N") + ".tmp");
        }

        public static void Discard(string temporary)
        {
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security,
            uint disposition, uint attributes, IntPtr template);

        [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

        [DllImport("libc", EntryPoint = "free")]
        private static extern void Free(IntPtr buffer);
    }
}
