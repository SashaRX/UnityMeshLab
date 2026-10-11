#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Fbx;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Turns extracted embedded media into durable external references before a save.</summary>
    internal static class FbxExternalMedia
    {
        static readonly Regex FilenameLine = new Regex(
            "^(\\s*(?:(?:FileName|Filename|RelativeFilename)\\s*:\\s*|P:\\s*\"(?:Path|RelPath)\"(?:,\\s*\"[^\"]*\"){3},\\s*)\")([^\"]*)(\".*)$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // Existing path components are canonicalized from directory entries, so
        // exact keys preserve distinct files on any case-sensitive directory.
        static readonly StringComparer PathComparer = StringComparer.Ordinal;
        static readonly Regex FilenamePrefix = new Regex(
            "^\\s*(?:(?:FileName|Filename|RelativeFilename)\\s*:|P:\\s*\"(?:Path|RelPath)\")",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        internal static FbxScene Prepare(FbxSourceDocument document, string scratch, string targetPath, List<string> created, List<string> createdFolders, List<string> referenced)
        {
            string media = Path.Combine(scratch, "input.fbm");
            string target = Path.GetFullPath(targetPath);
            string destination = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + ".fbm");
            var paths = new Dictionary<string, string>(PathComparer);
            foreach (string file in Directory.GetFiles(media, "*", SearchOption.AllDirectories))
            {
                // Content-addressed subfolders avoid overwriting authored external maps,
                // including an earlier export of a different map with the same filename.
                // A 128-bit prefix bounds path overhead; reuse still verifies the full hash.
                string hash = Hash(file);
                string output = MediaOutputPath(destination, hash, Path.GetFileName(file));
                if (File.Exists(output))
                {
                    if (Hash(output) != hash) throw new IOException("An external FBX media file has changed: " + output);
                }
                else
                {
                    CreateFolder(destination, createdFolders);
                    CreateFolder(Path.GetDirectoryName(output), createdFolders);
                    using (var input = File.OpenRead(file))
                    using (var saved = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                    {
                        created.Add(output);
                        input.CopyTo(saved);
                    }
                }
                output = CanonicalPath(output);
                referenced.Add(output);
                paths[CanonicalPath(file)] = output;
            }

            string staged = Path.Combine(scratch, "external.fbx");
            return RewriteAndImport(document, staged, scratch, target, paths, false);
        }

        internal static FbxScene ResolveExternal(FbxSourceDocument document, string scratch, string target, bool requireMedia)
        {
            // Documents without media need no filename staging. Preserve their
            // untouched layer values exactly, including binary double precision.
            if (document.MediaReferences.Count == 0) return document.Scene;
            string staged = Path.Combine(scratch, "resolved.fbx");
            return RewriteAndImport(document, staged, scratch, Path.GetFullPath(target), new Dictionary<string, string>(PathComparer), requireMedia);
        }

        static string MediaOutputPath(string destination, string hash, string filename)
        {
            string output = Path.Combine(destination, hash.Substring(0, 32), filename);
            if (Path.DirectorySeparatorChar != '\\' || output.Length < 250) return output;
            // Legacy Windows SDK/filesystem paths must fit as a whole, not just
            // the hash component. Flatten long names while keeping their extension.
            string extension = Path.GetExtension(filename);
            int budget = 249 - destination.Length - 1 - extension.Length;
            if (budget < 16) throw new IOException("The FBX output directory is too long to store external media: " + destination);
            return Path.Combine(destination, hash.Substring(0, Math.Min(32, budget)) + extension);
        }

        static void Stage(FbxSourceDocument document, string staged)
        {
            var io = document.Manager.GetIOSettings();
            io.SetBoolProp(Globals.EXP_FBX_EMBEDDED, false);
            document.ExportScene(document.Scene, staged, new FbxWriteSettings(true, false).WriterFormat(document.Manager), io);
        }

        static FbxScene RewriteAndImport(FbxSourceDocument document, string staged, string scratch, string target, IReadOnlyDictionary<string, string> extractedPaths, bool requireMedia)
        {
            Stage(document, staged);
            string rewritten = Path.Combine(scratch, "media-paths.fbx");
            var originalPaths = SourcePaths(document.MediaReferences, document.SourceDirectory);
            var mediaPaths = FbxUvSetNames.ReadMediaReferences(staged)
                .Select(reference => ResolveBinaryPaths(reference, scratch, document.SourceDirectory, extractedPaths, originalPaths, requireMedia)).ToArray();
            using (var reader = File.OpenRead(staged))
            using (var writer = File.Create(rewritten))
                RewriteBinary(reader, writer, mediaPaths, scratch, target);
            var scene = FbxScene.Create(document.Manager, "MeshLabExternalMedia");
            using var importer = FbxImporter.Create(document.Manager, "MeshLabExternalMediaImport");
            if (!importer.Initialize(rewritten, -1, document.Manager.GetIOSettings()) || !importer.Import(scene))
            {
                scene.Destroy();
                throw new IOException("FBX SDK cannot read the external-media document.");
            }
            return scene;
        }

        static IReadOnlyDictionary<string, string> ResolveBinaryPaths((string filename, string relative) reference, string scratch, string source,
            IReadOnlyDictionary<string, string> extracted, IReadOnlyDictionary<string, string> original, bool requireMedia)
        {
            var names = new[] { reference.filename, reference.relative }.Where(name => !string.IsNullOrEmpty(name)).ToArray();
            var mapped = names.Select(name => extracted.TryGetValue(MediaPath(name, scratch), out string path) ? path : name).ToList();
            var resolved = ResolveBlockPaths(mapped, scratch, source, original, requireMedia);
            string output = resolved.Values.FirstOrDefault();
            var paths = new Dictionary<string, string>(PathComparer);
            if (output != null) foreach (string name in names) paths[MediaPath(name, scratch)] = output;
            return paths;
        }

        // Only media string properties change. Copy all other property payloads,
        // including compressed geometry arrays, with a bounded buffer. Recompute
        // absolute node offsets because filename strings can change length.
        internal static void RewriteBinary(Stream input, Stream output, IReadOnlyList<IReadOnlyDictionary<string, string>> mediaPaths, string scratch, string target)
            => new BinaryMediaRewrite(input, output, mediaPaths, scratch, target).Run();

        sealed class BinaryMediaRewrite
        {
            readonly BinaryReader reader;
            readonly BinaryWriter writer;
            readonly IReadOnlyList<IReadOnlyDictionary<string, string>> mediaPaths;
            readonly string scratch, target;
            readonly byte[] buffer = new byte[4096];
            bool wide;
            int mediaIndex;

            internal BinaryMediaRewrite(Stream input, Stream output, IReadOnlyList<IReadOnlyDictionary<string, string>> mediaPaths, string scratch, string target)
            {
                reader = new BinaryReader(input, Encoding.UTF8, true);
                writer = new BinaryWriter(output, Encoding.UTF8, true);
                this.mediaPaths = mediaPaths; this.scratch = scratch; this.target = target;
            }

            internal void Run()
            {
                byte[] header = reader.ReadBytes(27);
                if (header.Length != 27 || Encoding.ASCII.GetString(header, 0, 18) != "Kaydara FBX Binary")
                    throw new IOException("The SDK media stage is not a Binary FBX.");
                wide = BitConverter.ToUInt32(header, 23) >= 7500;
                writer.Write(header);
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                    if (!Node(reader.BaseStream.Length, null, null, 0)) break;
                Copy(reader.BaseStream.Length - reader.BaseStream.Position);
                if (mediaIndex != mediaPaths.Count) throw new IOException("FBX media record count changed during filename rewriting.");
                writer.Flush();
            }

            bool Node(long parentEnd, string parent, IReadOnlyDictionary<string, string> paths, int depth)
            {
                if (depth > 256) throw new IOException("FBX record nesting is too deep.");
                int headerSize = wide ? 25 : 13;
                long start = writer.BaseStream.Position;
                if (parentEnd - reader.BaseStream.Position < headerSize) { Copy(parentEnd - reader.BaseStream.Position); return false; }
                long end = Offset(), count = Offset(), length = Offset();
                byte nameLength = reader.ReadByte();
                if (end == 0) { writer.Write(new byte[headerSize]); return false; }
                long properties = reader.BaseStream.Position + nameLength;
                if (end > parentEnd || count < 0 || length < 0 || properties > end || length > end - properties)
                    throw new IOException("Invalid Binary FBX node bounds.");
                byte[] nameBytes = reader.ReadBytes(nameLength);
                string name = Encoding.ASCII.GetString(nameBytes);
                if (parent == "Objects" && (name == "Texture" || name == "Video"))
                {
                    if (mediaIndex >= mediaPaths.Count) throw new IOException("Unexpected FBX media record.");
                    paths = mediaPaths[mediaIndex++];
                }
                writer.Write(new byte[headerSize - 1]); writer.Write(nameLength); writer.Write(nameBytes);
                long outputProperties = writer.BaseStream.Position;
                Properties(properties + length, count, name, parent, paths);
                long propertyLength = writer.BaseStream.Position - outputProperties;
                while (reader.BaseStream.Position < end)
                    if (!Node(end, name, paths, depth + 1)) break;
                Copy(end - reader.BaseStream.Position);
                long outputEnd = writer.BaseStream.Position;
                writer.BaseStream.Position = start;
                WriteOffset(outputEnd); WriteOffset(count); WriteOffset(propertyLength);
                writer.BaseStream.Position = outputEnd;
                return true;
            }

            void Properties(long end, long count, string name, string parent, IReadOnlyDictionary<string, string> paths)
            {
                long start = reader.BaseStream.Position;
                int index = -1;
                bool relative = false;
                if (paths != null && (parent == "Texture" || parent == "Video"))
                {
                    if (name == "FileName" || name == "Filename") index = 0;
                    if (name == "RelativeFilename") { index = 0; relative = true; }
                }
                if (paths != null && parent == "Properties70" && name == "P")
                {
                    string property = String(end);
                    if (property == "Path" || property == "RelPath") { index = 4; relative = property == "RelPath"; }
                    reader.BaseStream.Position = start;
                }
                if (index < 0 || count <= index) { Copy(end - start); return; }
                long valueStart = start;
                string value = null;
                for (int i = 0; i <= index; i++)
                {
                    valueStart = reader.BaseStream.Position;
                    value = String(end);
                    if (value == null) { reader.BaseStream.Position = start; Copy(end - start); return; }
                }
                long tail = reader.BaseStream.Position;
                string replaced = value;
                if (paths.TryGetValue(MediaPath(value, scratch), out string output))
                    replaced = (relative ? Path.GetRelativePath(Path.GetDirectoryName(target), output) : output).Replace('\\', '/');
                reader.BaseStream.Position = start; Copy(valueStart - start);
                byte[] encoded = Encoding.UTF8.GetBytes(replaced);
                writer.Write((byte)'S'); writer.Write(encoded.Length); writer.Write(encoded);
                reader.BaseStream.Position = tail; Copy(end - tail);
            }

            string String(long end)
            {
                if (end - reader.BaseStream.Position < 5 || reader.ReadByte() != 'S') return null;
                uint length = reader.ReadUInt32();
                if (length > end - reader.BaseStream.Position || length > 65536) throw new IOException("Invalid FBX media string length.");
                return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
            }

            long Offset() => wide ? reader.ReadInt64() : reader.ReadUInt32();
            void WriteOffset(long value)
            {
                if (wide) writer.Write(value);
                else if (value <= uint.MaxValue) writer.Write((uint)value);
                else throw new IOException("Rewritten FBX exceeds its 32-bit record offset limit.");
            }

            void Copy(long count)
            {
                if (count < 0) throw new IOException("Invalid FBX copy bounds.");
                while (count > 0)
                {
                    int read = reader.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                    if (read == 0) throw new EndOfStreamException("Incomplete Binary FBX record.");
                    writer.Write(buffer, 0, read); count -= read;
                }
            }
        }

        // SDK ASCII texture/video objects contain paired absolute and relative
        // filename records. Resolve each object independently to avoid mixing maps
        // with identical basenames in different source folders.
        static Dictionary<string, string> SourcePaths(IReadOnlyList<(string filename, string relative)> references, string source)
        {
            var paths = new Dictionary<string, string>(PathComparer);
            var ambiguous = new HashSet<string>(PathComparer);
            foreach (var reference in references)
            {
                string absolute = string.IsNullOrEmpty(reference.filename) ? null : MediaPath(reference.filename, source);
                string relative = string.IsNullOrEmpty(reference.relative) ? null : MediaPath(reference.relative, source);
                string resolved = absolute != null && File.Exists(absolute) ? absolute : relative;
                if (resolved == null || !File.Exists(resolved)) continue;
                if (absolute == null) continue;
                if (paths.TryGetValue(absolute, out string previous) && !PathComparer.Equals(previous, resolved)) ambiguous.Add(absolute);
                else paths[absolute] = resolved;
            }
            foreach (string key in ambiguous) paths.Remove(key);
            return paths;
        }

        // Geometry and opaque properties are copied with fixed-size buffers, even
        // when an SDK array occupies one enormous line. Media blocks are spooled
        // to owned scratch storage; only their bounded filename records are read.
        internal static void RewriteAscii(TextReader reader, TextWriter writer, string scratch, string source, string target,
            IReadOnlyDictionary<string, string> extractedPaths, IReadOnlyDictionary<string, string> originalPaths, bool requireMedia)
        {
            string spool = Path.Combine(scratch, "media-block.tmp");
            var buffer = new char[4096];
            try
            {
                string prefix;
                while ((prefix = ReadPrefix(reader)) != null)
                {
                    string header = prefix.TrimStart();
                    if (!header.StartsWith("Texture:", StringComparison.Ordinal) && !header.StartsWith("Video:", StringComparison.Ordinal))
                    {
                        CopyRecord(reader, writer, prefix, buffer, extractedPaths, scratch, target, null);
                        continue;
                    }
                    string closing = prefix.Substring(0, prefix.Length - header.Length) + "}";
                    var names = new List<string>();
                    using (var block = new StreamWriter(spool))
                    {
                        CopyRecord(reader, block, prefix, buffer, extractedPaths, scratch, target, null);
                        while ((prefix = ReadPrefix(reader)) != null)
                        {
                            bool closed = prefix.EndsWith("\n", StringComparison.Ordinal) && prefix.TrimEnd() == closing;
                            CopyRecord(reader, block, prefix, buffer, extractedPaths, scratch, target, names);
                            if (closed) break;
                        }
                    }
                    var paths = ResolveBlockPaths(names, scratch, source, originalPaths, requireMedia);
                    using (var block = new StreamReader(spool))
                        while ((prefix = ReadPrefix(block)) != null)
                            CopyRecord(block, writer, prefix, buffer, paths, scratch, target, null);
                }
            }
            finally { if (File.Exists(spool)) File.Delete(spool); }
        }

        static string ReadPrefix(TextReader reader)
        {
            var prefix = new StringBuilder(256);
            int value;
            while (prefix.Length < 256 && (value = reader.Read()) >= 0)
            {
                prefix.Append((char)value);
                if (value == '\n') break;
            }
            return prefix.Length == 0 ? null : prefix.ToString();
        }

        static void CopyRecord(TextReader reader, TextWriter writer, string prefix, char[] buffer,
            IReadOnlyDictionary<string, string> paths, string scratch, string target, List<string> names)
        {
            if (FilenamePrefix.IsMatch(prefix))
            {
                var record = new StringBuilder(prefix);
                if (!prefix.EndsWith("\n", StringComparison.Ordinal))
                {
                    int value;
                    while ((value = reader.Read()) >= 0)
                    {
                        if (record.Length >= 65536) throw new IOException("An FBX media filename record is too long.");
                        record.Append((char)value);
                        if (value == '\n') break;
                    }
                }
                string full = record.ToString(), line = full.TrimEnd('\r', '\n');
                string rewritten = RewriteFilename(line, paths, scratch, target);
                var match = FilenameLine.Match(rewritten);
                if (names != null && match.Success && !string.IsNullOrEmpty(match.Groups[2].Value)) names.Add(match.Groups[2].Value);
                writer.Write(rewritten);
                writer.Write(full.Substring(line.Length));
                return;
            }
            writer.Write(prefix);
            if (prefix.EndsWith("\n", StringComparison.Ordinal)) return;
            int count = 0, next;
            while ((next = reader.Read()) >= 0)
            {
                buffer[count++] = (char)next;
                if (count == buffer.Length || next == '\n') { writer.Write(buffer, 0, count); count = 0; }
                if (next == '\n') break;
            }
            if (count > 0) writer.Write(buffer, 0, count);
        }

        static Dictionary<string, string> ResolveBlockPaths(List<string> names, string scratch, string source,
            IReadOnlyDictionary<string, string> originalPaths, bool requireMedia)
        {
            var paths = new Dictionary<string, string>(PathComparer);
            string resolved = names.Select(name => MediaPath(name, scratch)).FirstOrDefault(File.Exists)
                ?? names.Select(name => originalPaths.TryGetValue(MediaPath(name, source), out string mapped) ? mapped : null).FirstOrDefault(path => path != null)
                ?? names.Where(name => !Path.IsPathRooted(name.Replace('\\', Path.DirectorySeparatorChar)))
                    .Select(name => MediaPath(name, source)).FirstOrDefault(File.Exists);
            if (resolved == null)
            {
                if (requireMedia && names.Count > 0) throw new IOException("Cannot embed missing FBX media: " + names[0]);
                return paths;
            }
            foreach (string name in names) paths[MediaPath(name, scratch)] = resolved;
            return paths;
        }

        static string MediaPath(string name, string directory)
        {
            string stored = name.Replace('\\', Path.DirectorySeparatorChar);
            return CanonicalPath(Path.IsPathRooted(stored) ? stored : Path.Combine(directory, stored));
        }

        internal static string RewriteFilename(string line, IReadOnlyDictionary<string, string> paths, string scratch, string target)
        {
            var match = FilenameLine.Match(line);
            if (!match.Success || string.IsNullOrEmpty(match.Groups[2].Value)) return line;
            string stored = match.Groups[2].Value.Replace('\\', Path.DirectorySeparatorChar);
            string absolute = CanonicalPath(Path.IsPathRooted(stored) ? stored : Path.Combine(scratch, stored));
            if (!paths.TryGetValue(absolute, out string output)) return line;
            bool relative = match.Groups[1].Value.Contains("RelativeFilename") || match.Groups[1].Value.Contains("\"RelPath\"");
            string filename = relative ? Path.GetRelativePath(Path.GetDirectoryName(target), output) : output;
            return match.Groups[1].Value + filename.Replace('\\', '/') + match.Groups[3].Value;
        }

        internal static void DeleteCreated(string file)
        {
            string relative = AssetPath(file);
            if (relative != null)
            {
                if (UnityEditor.AssetDatabase.DeleteAsset(relative)) return;
                if (!File.Exists(file) && !Directory.Exists(file)) return;
                // SDK-created files/folders may not be registered yet. Import the
                // owned resource before retrying Unity's GUID-aware deletion.
                UnityEditor.AssetDatabase.ImportAsset(relative, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                if (!UnityEditor.AssetDatabase.DeleteAsset(relative) && (File.Exists(file) || Directory.Exists(file)))
                    throw new IOException("Cannot remove the failed export's new media resource: " + file);
            }
            else if (Directory.Exists(file)) Directory.Delete(file);
            else File.Delete(file);
        }

        internal static void ImportMedia(IEnumerable<string> files)
        {
            var assets = files.Select(AssetPath).Where(path => path != null).Distinct().ToArray();
            if (assets.Length == 0) return;
            UnityEditor.AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string asset in assets)
                    UnityEditor.AssetDatabase.ImportAsset(asset, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
            }
            finally { UnityEditor.AssetDatabase.StopAssetEditing(); }
        }

        internal static string AssetPath(string file)
        {
            string full = CanonicalPath(file);
            string assets = CanonicalPath(UnityEngine.Application.dataPath) + Path.DirectorySeparatorChar;
            if (full.StartsWith(assets, StringComparison.Ordinal)) return "Assets/" + full.Substring(assets.Length).Replace('\\', '/');
            string packages = CanonicalPath(Path.Combine(UnityEngine.Application.dataPath, "../Packages")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(packages, StringComparison.Ordinal)) return "Packages/" + full.Substring(packages.Length).Replace('\\', '/');
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                string root = CanonicalPath(package.resolvedPath) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.Ordinal)) return package.assetPath + "/" + full.Substring(root.Length).Replace('\\', '/');
            }
            return null;
        }

        static string CanonicalPath(string path)
        {
            string full = Path.GetFullPath(path), root = Path.GetPathRoot(full);
            string current = root.Length == 3 && root[1] == ':' ? root.ToUpperInvariant() : root;
            foreach (string component in full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(current, component);
                if (Directory.Exists(current) && (File.Exists(candidate) || Directory.Exists(candidate)))
                {
                    string[] entries = Directory.GetFileSystemEntries(current);
                    // Prefer the exact entry even if another entry differs only in case.
                    string exact = entries.FirstOrDefault(entry => Path.GetFileName(entry) == component);
                    string[] aliases = exact == null ? entries.Where(entry => string.Equals(Path.GetFileName(entry), component, StringComparison.OrdinalIgnoreCase)).ToArray() : Array.Empty<string>();
                    if (exact != null) candidate = exact;
                    else if (aliases.Length == 1) candidate = aliases[0];
                    else if (aliases.Length > 1) throw new IOException("Ambiguous FBX media path: " + candidate);
                }
                current = candidate;
            }
            return current;
        }

        static void CreateFolder(string path, List<string> created)
        {
            if (Directory.Exists(path)) return;
            Directory.CreateDirectory(path);
            created.Add(path);
        }

        static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }
    }
}
#endif
