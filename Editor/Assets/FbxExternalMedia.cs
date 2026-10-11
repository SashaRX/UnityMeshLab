#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
                string output = Path.Combine(destination, hash.Substring(0, 32), Path.GetFileName(file));
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
                referenced.Add(output);
                paths[CanonicalPath(file)] = output;
            }

            string staged = Path.Combine(scratch, "external.fbx");
            string[] lines = Stage(document, staged);
            for (int i = 0; i < lines.Length; i++) lines[i] = RewriteFilename(lines[i], paths, scratch, target);
            RebaseTextureBlocks(lines, scratch, document.SourceDirectory, target, SourcePaths(document, scratch), false);
            return ImportStaged(document, staged, lines);
        }

        internal static FbxScene ResolveExternal(FbxSourceDocument document, string scratch, string target)
        {
            string staged = Path.Combine(scratch, "resolved.fbx");
            string[] lines = Stage(document, staged);
            RebaseTextureBlocks(lines, scratch, document.SourceDirectory, Path.GetFullPath(target), SourcePaths(document, scratch), true);
            return ImportStaged(document, staged, lines);
        }

        static string[] Stage(FbxSourceDocument document, string staged)
        {
            var io = document.Manager.GetIOSettings();
            io.SetBoolProp(Globals.EXP_FBX_EMBEDDED, false);
            document.ExportScene(document.Scene, staged, new FbxWriteSettings(false, false).WriterFormat(document.Manager), io);
            return File.ReadAllLines(staged);
        }

        static FbxScene ImportStaged(FbxSourceDocument document, string staged, string[] lines)
        {
            File.WriteAllLines(staged, lines);
            var scene = FbxScene.Create(document.Manager, "MeshLabExternalMedia");
            using var importer = FbxImporter.Create(document.Manager, "MeshLabExternalMediaImport");
            if (!importer.Initialize(staged, -1, document.Manager.GetIOSettings()) || !importer.Import(scene))
                throw new IOException("FBX SDK cannot read the external-media document.");
            return scene;
        }

        // SDK ASCII texture/video objects contain paired absolute and relative
        // filename records. Resolve each object independently to avoid mixing maps
        // with identical basenames in different source folders.
        static Dictionary<string, string> SourcePaths(FbxSourceDocument document, string scratch)
        {
            var paths = new Dictionary<string, string>(PathComparer);
            var ambiguous = new HashSet<string>(PathComparer);
            foreach (var reference in FbxUvSetNames.ReadMediaReferences(Path.Combine(scratch, "input.fbx")))
            {
                string absolute = string.IsNullOrEmpty(reference.filename) ? null : MediaPath(reference.filename, document.SourceDirectory);
                string relative = string.IsNullOrEmpty(reference.relative) ? null : MediaPath(reference.relative, document.SourceDirectory);
                string resolved = absolute != null && File.Exists(absolute) ? absolute : relative;
                if (resolved == null || !File.Exists(resolved)) continue;
                if (absolute == null) continue;
                if (paths.TryGetValue(absolute, out string previous) && !PathComparer.Equals(previous, resolved)) ambiguous.Add(absolute);
                else paths[absolute] = resolved;
            }
            foreach (string key in ambiguous) paths.Remove(key);
            return paths;
        }

        static void RebaseTextureBlocks(string[] lines, string scratch, string source, string target, IReadOnlyDictionary<string, string> originalPaths, bool requireMedia)
        {
            int start = 0;
            while (start < lines.Length)
            {
                string header = lines[start].TrimStart();
                if (!header.StartsWith("Texture:", StringComparison.Ordinal) && !header.StartsWith("Video:", StringComparison.Ordinal)) { start++; continue; }
                string closing = lines[start].Substring(0, lines[start].Length - header.Length) + "}";
                int end = start + 1;
                while (end < lines.Length && lines[end].TrimEnd() != closing) end++;
                RebaseTextureBlock(lines, start + 1, end, scratch, source, target, originalPaths, requireMedia);
                start = end + 1;
            }
        }

        static void RebaseTextureBlock(string[] lines, int start, int end, string scratch, string source, string target, IReadOnlyDictionary<string, string> originalPaths, bool requireMedia)
        {
            var names = new List<string>();
            for (int i = start; i < end; i++)
            {
                var match = FilenameLine.Match(lines[i]);
                if (match.Success && !string.IsNullOrEmpty(match.Groups[2].Value)) names.Add(match.Groups[2].Value);
            }
            string resolved = names.Select(name => MediaPath(name, scratch)).FirstOrDefault(File.Exists)
                ?? names.Select(name => originalPaths.TryGetValue(MediaPath(name, source), out string mapped) ? mapped : null).FirstOrDefault(path => path != null)
                ?? names.Where(name => !Path.IsPathRooted(name.Replace('\\', Path.DirectorySeparatorChar)))
                    .Select(name => MediaPath(name, source)).FirstOrDefault(File.Exists);
            if (resolved == null)
            {
                if (requireMedia && names.Count > 0) throw new IOException("Cannot embed missing FBX media: " + names[0]);
                return;
            }
            var paths = new Dictionary<string, string>(PathComparer);
            foreach (string name in names) paths[MediaPath(name, scratch)] = resolved;
            for (int i = start; i < end; i++) lines[i] = RewriteFilename(lines[i], paths, scratch, target);
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
            foreach (string component in full.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (component.Length == 0) continue;
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
