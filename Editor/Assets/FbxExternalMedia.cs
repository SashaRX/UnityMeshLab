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
            RegexOptions.CultureInvariant);

        internal static FbxScene Prepare(FbxSourceDocument document, string scratch, string targetPath, List<string> created, List<string> createdFolders)
        {
            string media = Path.Combine(scratch, "input.fbm");
            string target = Path.GetFullPath(targetPath);
            string destination = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + ".fbm");
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(media, "*", SearchOption.AllDirectories))
            {
                // Content-addressed subfolders avoid overwriting authored external maps,
                // including an earlier export of a different map with the same filename.
                string hash = Hash(file);
                string output = Path.Combine(destination, hash, Path.GetFileName(file));
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
                paths[Path.GetFullPath(file)] = output;
            }

            string staged = Path.Combine(scratch, "external.fbx");
            var io = document.Manager.GetIOSettings();
            document.ExportScene(document.Scene, staged, new FbxWriteSettings(false, false).WriterFormat(document.Manager), io);
            string[] lines = File.ReadAllLines(staged);
            for (int i = 0; i < lines.Length; i++)
            {
                var match = FilenameLine.Match(lines[i]);
                if (!match.Success || string.IsNullOrEmpty(match.Groups[2].Value)) continue;
                string stored = match.Groups[2].Value.Replace('\\', Path.DirectorySeparatorChar);
                string absolute = Path.GetFullPath(Path.IsPathRooted(stored) ? stored : Path.Combine(scratch, stored));
                if (paths.TryGetValue(absolute, out string output))
                {
                    bool relative = match.Groups[1].Value.Contains("RelativeFilename") || match.Groups[1].Value.Contains("\"RelPath\"");
                    string filename = relative ? Path.GetRelativePath(Path.GetDirectoryName(target), output) : output;
                    lines[i] = match.Groups[1].Value + filename.Replace('\\', '/') + match.Groups[3].Value;
                }
            }
            File.WriteAllLines(staged, lines);
            var scene = FbxScene.Create(document.Manager, "MeshLabExternalMedia");
            using var importer = FbxImporter.Create(document.Manager, "MeshLabExternalMediaImport");
            if (!importer.Initialize(staged, -1, io) || !importer.Import(scene))
                throw new IOException("FBX SDK cannot read the external-media document.");
            return scene;
        }

        internal static void DeleteCreated(string file)
        {
            string assets = Path.GetFullPath(UnityEngine.Application.dataPath) + Path.DirectorySeparatorChar;
            if (file.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            {
                string relative = "Assets/" + file.Substring(assets.Length).Replace('\\', '/');
                if (!UnityEditor.AssetDatabase.DeleteAsset(relative) && File.Exists(file))
                    throw new IOException("Cannot remove the failed export's new media file: " + file);
            }
            else if (Directory.Exists(file)) Directory.Delete(file);
            else File.Delete(file);
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
