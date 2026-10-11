using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;

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

        public sealed class PendingFile
        {
            public string Staged { get; }
            public string Destination { get; }
            private readonly bool existed;
            private readonly long length;
            private readonly DateTime modified;
            private string backup;
            private bool committed;

            public PendingFile(string staged, string destination, bool existed, long length, DateTime modified)
            {
                Staged = staged;
                Destination = destination;
                this.existed = existed;
                this.length = length;
                this.modified = modified;
            }

            public void ValidateUnchanged()
            {
                var current = new FileInfo(Destination);
                if (current.Exists != existed || (existed && (current.Length != length || current.LastWriteTimeUtc != modified)))
                    throw new IOException("Output changed during export; save again to confirm overwrite: " + Destination);
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
                foreach (var file in files) file.DiscardBackup();
                throw;
            }
            foreach (var file in files) file.DiscardBackup();
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
                    var buffer = new StringBuilder(32768);
                    uint count = GetFinalPathNameByHandle(file.SafeFileHandle, buffer,
                        (uint)buffer.Capacity, 0);
                    if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
                    string resolved = buffer.ToString();
                    if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + resolved.Substring(8);
                    return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved.Substring(4) : resolved;
                }
            }
            IntPtr result = RealPath(full, IntPtr.Zero);
            if (result == IntPtr.Zero) throw new IOException("Cannot resolve file path: " + full);
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

        [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

        [DllImport("libc", EntryPoint = "free")]
        private static extern void Free(IntPtr buffer);
    }
}
