using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Texture output staging and filesystem path checks; never changes asset metadata.</summary>
    public static class TexturePackingFiles
    {
        public static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\' ?
            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
                    uint count = GetFinalPathNameByHandle(file.SafeFileHandle.DangerousGetHandle(), buffer,
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
            return string.Equals(ResolveExistingFile(source), ResolveExistingFile(destination), PathComparison);
        }

        public static string Stage(string destination, Action<Stream> write)
        {
            // Dot-prefixed temporary files are ignored by Unity. They are not imported assets.
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination)),
                ".meshlab-texture-" + Guid.NewGuid().ToString("N") + ".tmp");
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

        public static void Commit(string temporary, string destination)
        {
            // Replacement also detaches hard-linked destinations without changing other links.
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }

        public static void Discard(string temporary)
        {
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint length, uint flags);

        [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

        [DllImport("libc", EntryPoint = "free")]
        private static extern void Free(IntPtr buffer);
    }
}
