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
            private byte[] stagedHash;
            private long stagedLength;
            private FileIdentity stagedIdentity;
            private DateTime installedModified;
            private DateTime installedCreated;
            private FileStream rollbackSource;
            private DateTime rollbackModified;
            private DateTime rollbackCreated;
            private FileIdentity rollbackIdentity;
            private bool rollbackPrepared;
            private Exception rollbackConflict;

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
                if (stagedHash == null)
                {
                    using (var staged = new FileStream(Staged, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        stagedLength = staged.Length;
                        stagedHash = ContentHash(staged, cancellation);
                        if (Path.DirectorySeparatorChar == '\\') stagedIdentity = GetIdentity(staged);
                    }
                }
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

            private static bool HashesEqual(byte[] left, byte[] right)
            {
                if (left == null || right == null || left.Length != right.Length) return false;
                for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
                return true;
            }

            public void Dispose()
            {
                verifiedSource?.Dispose();
                verifiedSource = null;
                rollbackSource?.Dispose();
                rollbackSource = null;
            }

            public void Install()
            {
                try
                {
                    VerifyContent(CancellationToken.None);
                    InstallUnverified();
                    VerifyInstalled();
                }
                finally { Dispose(); }
            }

            internal void InstallUnverified()
            {
                if (stagedHash == null) throw new InvalidOperationException("Verify staged content before installation.");
                ValidateUnchanged();
                if (existed)
                {
                    // The backup joins the batch (and its cleanup) only once the replace
                    // succeeded: a replace that failed halfway may have left the original
                    // under the backup name, and that file must go back, not away.
                    string candidate = TemporaryPath(Destination);
                    try { File.Replace(Staged, Destination, candidate); }
                    catch { RestoreReplaced(Destination, candidate); throw; }
                    backup = candidate;
                }
                else File.Move(Staged, Destination); // Fails rather than replacing a late-arriving file.
                committed = true;
                // Retain the original's lease while the worker checks its backup; no hash runs here.
                var installed = new FileInfo(Destination);
                if (installed.Length != stagedLength) throw new IOException("Installed output changed: " + Destination);
                installedModified = installed.LastWriteTimeUtc;
                installedCreated = installed.CreationTimeUtc;
                if (Path.DirectorySeparatorChar == '\\')
                {
                    using (var stream = new FileStream(Destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        if (!SameIdentity(stagedIdentity, GetIdentity(stream)))
                            throw new IOException("Installed output replaced: " + Destination);
                    }
                }
            }

            internal void VerifyInstalled()
            {
                if (!committed) return;
                if (existed && !BackupMatchesVerifiedSource())
                    throw new IOException("Output changed during installation; batch will be restored: " + Destination);
            }

            private bool BackupMatchesVerifiedSource()
            {
                using (var original = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (Path.DirectorySeparatorChar == '\\')
                    {
                        var identity = GetIdentity(original);
                        if (!SameIdentity(identity, verifiedIdentity))
                            return false;
                    }
                    return original.Length == length && HashesEqual(contentHash, ContentHash(original, CancellationToken.None));
                }
            }

            public void Rollback()
            {
                if (!committed) return;
                try
                {
                    if (!rollbackPrepared) PrepareRollback();
                    RollbackPrepared();
                }
                finally { Dispose(); }
            }

            internal void PrepareRollback()
            {
                if (!committed) return;
                // The verified original is now the rollback source; ReplaceFile must be able to open it for writing.
                verifiedSource?.Dispose();
                verifiedSource = null;
                rollbackPrepared = true;
                rollbackConflict = null;
                rollbackSource?.Dispose();
                rollbackSource = null;
                FileStream source = null;
                try
                {
                    source = new FileStream(Destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    var before = new FileInfo(Destination);
                    if (before.LastWriteTimeUtc != installedModified || before.CreationTimeUtc != installedCreated)
                        throw new IOException("Installed output metadata was edited; rollback will preserve it: " + Destination);
                    if (source.Length != stagedLength || !HashesEqual(stagedHash, ContentHash(source, CancellationToken.None)))
                        throw new IOException("Installed output was edited; rollback will preserve it: " + Destination);
                    var after = new FileInfo(Destination);
                    if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc ||
                        before.CreationTimeUtc != after.CreationTimeUtc)
                        throw new IOException("Installed output changed while preparing rollback: " + Destination);
                    rollbackModified = after.LastWriteTimeUtc;
                    rollbackCreated = after.CreationTimeUtc;
                    if (Path.DirectorySeparatorChar == '\\')
                    {
                        rollbackIdentity = GetIdentity(source);
                        if (!SameIdentity(rollbackIdentity, stagedIdentity))
                            throw new IOException("Installed output was replaced; rollback will preserve it: " + Destination);
                    }
                    rollbackSource = source;
                    source = null;
                }
                catch (Exception exception) { rollbackConflict = exception; }
                finally { source?.Dispose(); }
            }

            internal void RollbackPrepared()
            {
                if (!committed) return;
                if (!rollbackPrepared) throw new InvalidOperationException("Prepare rollback content on a worker first.");
                if (rollbackConflict != null)
                    throw new IOException("Rollback conflict; current output preserved and recovery backup retained: " +
                        Destination + "; backup: " + backup, rollbackConflict);
                var current = new FileInfo(Destination);
                if (!current.Exists || current.Length != stagedLength || current.LastWriteTimeUtc != rollbackModified ||
                    current.CreationTimeUtc != rollbackCreated)
                    throw new IOException("Output changed after rollback preparation; recovery backup retained: " + backup);
                if (Path.DirectorySeparatorChar == '\\')
                {
                    using (var input = new FileStream(Destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        if (!SameIdentity(GetIdentity(input), rollbackIdentity))
                            throw new IOException("Output replaced after rollback preparation; recovery backup retained: " + backup);
                    }
                }
                if (existed) File.Replace(backup, Destination, null);
                else File.Delete(Destination); // Newly installed file, before any AssetDatabase import.
                committed = false;
            }

            private static bool SameIdentity(FileIdentity left, FileIdentity right)
                => left.volume == right.volume && left.low == right.low && left.high == right.high;

            public void DiscardBackup() => Discard(backup);
        }

        public static void InstallBatch(IList<PendingFile> files)
        {
            try
            {
                foreach (var file in files) file.VerifyContent(CancellationToken.None);
                InstallBatchUnverified(files);
                VerifyInstalledBatch(files);
                CompleteBatch(files);
            }
            catch (Exception failure)
            {
                PrepareRollbackBatch(files);
                try { RollbackBatch(files); }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("Texture batch rollback failed; recovery backups were retained.", failure, rollbackFailure);
                }
                throw;
            }
            finally { foreach (var file in files) file.Dispose(); }
        }

        internal static void InstallBatchUnverified(IList<PendingFile> files)
        {
            foreach (var file in files) file.ValidateUnchanged();
            foreach (var file in files) file.InstallUnverified();
        }

        internal static void VerifyInstalledBatch(IList<PendingFile> files)
        {
            foreach (var file in files) file.VerifyInstalled();
        }

        internal static void PrepareRollbackBatch(IList<PendingFile> files)
        {
            foreach (var file in files) file.PrepareRollback();
        }

        internal static void RollbackBatch(IList<PendingFile> files)
        {
            var failures = new List<Exception>();
            for (int i = files.Count - 1; i >= 0; i--)
            {
                try { files[i].RollbackPrepared(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count > 0)
                throw new AggregateException("Texture rollback conflicts; current outputs and recovery backups were retained.", failures);
            CompleteBatch(files);
        }

        internal static void CompleteBatch(IList<PendingFile> files)
        {
            DiscardBackups(files);
            foreach (var file in files)
            {
                file.Dispose();
                try { Discard(file.Staged); }
                catch (Exception exception) { UvtLog.Warn("Texture stage cleanup: " + exception.Message); }
            }
        }

        public static byte[] ContentHash(string path, CancellationToken cancellation = default)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                return ContentHash(stream, cancellation);
        }

        internal static byte[] ContentHash(Stream stream, CancellationToken cancellation)
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

        // ReplaceFile can fail after it moved the original to the backup name
        // (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): put the original back so the batch cleanup,
        // which deletes backups, never deletes the only copy. A backup next to an intact
        // destination is a copy (the Unix implementation links first) and is dropped.
        internal static void RestoreReplaced(string destination, string backup)
        {
            try
            {
                if (!File.Exists(backup)) return;
                if (!File.Exists(destination)) { File.Move(backup, destination); return; }
                // The destination survived. The backup is the copy the Unix implementations
                // make first when its content matches; otherwise it may be the only copy of
                // the original and stays, named in the log.
                if (SameContent(ContentHash(backup), ContentHash(destination))) Discard(backup);
                else UvtLog.Warn("Texture export: '" + destination + "' and its backup '" + backup + "' differ after a failed replace; the backup is kept.");
            }
            catch (Exception exception)
            {
                // The replace's own exception is what the caller rethrows; this one must not replace it.
                UvtLog.Error("Texture export: the original of '" + destination + "' may be at '" + backup +
                    "' and could not be restored: " + exception.Message);
            }
        }

        private static bool SameContent(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
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
