using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using Autodesk.Fbx;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public class FbxWriteSettingsTests
    {
        MeshLabFbxFormat previousFormat;
        bool previousEmbed;
        string folder;

        [SetUp]
        public void SetUp()
        {
            var settings = MeshLabProjectSettings.Instance;
            previousFormat = settings.fbxFormat; previousEmbed = settings.embedFbxTextures;
            settings.fbxFormat = MeshLabFbxFormat.Binary; settings.embedFbxTextures = false;
            Directory.CreateDirectory(folder = Path.Combine(Path.GetTempPath(), "meshlab-fbx-policy-" + Guid.NewGuid().ToString("N")));
        }

        [TearDown]
        public void TearDown()
        {
            var settings = MeshLabProjectSettings.Instance;
            settings.fbxFormat = previousFormat; settings.embedFbxTextures = previousEmbed;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [Test]
        public void NewAndOldProjectSettingsDefaultToBinaryWithoutEmbeddedTextures()
        {
            var settings = ScriptableObject.CreateInstance<MeshLabProjectSettings>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"savePath\":\"Assets/Existing\"}", settings);
                Assert.AreEqual(MeshLabFbxFormat.Binary, settings.fbxFormat);
                Assert.IsFalse(settings.embedFbxTextures);
                Assert.AreEqual("Assets/Existing", settings.savePath);
                Assert.IsFalse(new RemeshSettings().embedFbxTextures);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [TestCase(MeshLabFbxFormat.Binary, false, false)]
        [TestCase(MeshLabFbxFormat.Binary, true, true)]
        [TestCase(MeshLabFbxFormat.ASCII, false, false)]
        [TestCase(MeshLabFbxFormat.ASCII, true, false)]
        public void SharedPolicyDisablesEmbeddingForAscii(MeshLabFbxFormat format, bool embed, bool expected)
        {
            var settings = MeshLabProjectSettings.Instance;
            settings.fbxFormat = format; settings.embedFbxTextures = embed;
            var policy = FbxWriteSettings.FromProject();
            Assert.AreEqual(format == MeshLabFbxFormat.Binary, policy.Binary);
            Assert.AreEqual(expected, policy.EmbedTextures);
        }

        [Test]
        public void ProjectOutputOptionsSurviveTheSettingsFileRoundTrip()
        {
            var settings = ScriptableObject.CreateInstance<MeshLabProjectSettings>();
            UnityEngine.Object[] loaded = null;
            try
            {
                settings.fbxFormat = MeshLabFbxFormat.ASCII; settings.embedFbxTextures = true;
                string path = Path.Combine(folder, "settings.asset");
                UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new UnityEngine.Object[] { settings }, path, true);
                loaded = UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(path);
                var restored = (MeshLabProjectSettings)loaded[0];
                Assert.AreEqual(MeshLabFbxFormat.ASCII, restored.fbxFormat);
                Assert.IsTrue(restored.embedFbxTextures, "retains the explicit choice when switching back to Binary");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
                if (loaded != null) foreach (var item in loaded) UnityEngine.Object.DestroyImmediate(item);
            }
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        string Source(bool binary, bool embedded = false, bool external = false)
        {
            string path = Path.Combine(folder, "source.fbx");
            using var manager = FbxManager.Create();
            var io = FbxIOSettings.Create(manager, Globals.IOSROOT); manager.SetIOSettings(io);
            io.SetBoolProp(Globals.EXP_FBX_EMBEDDED, embedded);
            var scene = FbxScene.Create(manager, "Source");
            var mesh = FbxMesh.Create(scene, "Quad_LOD0");
            mesh.InitControlPoints(4);
            var points = new[] { new FbxVector4(.123456789012345, 0, 0), new FbxVector4(1, 0, 0), new FbxVector4(1, 1, 0), new FbxVector4(0, 1, 0) };
            for (int i = 0; i < points.Length; i++) mesh.SetControlPointAt(points[i], i);
            mesh.BeginPolygon(); for (int i = 0; i < 4; i++) mesh.AddPolygon(i); mesh.EndPolygon();
            var topology = new FbxLayerChannels.Topology(mesh);
            FbxLayerChannels.WriteUv(mesh, 0, topology, Enumerable.Repeat(.234567890123456, 8).ToArray(), Enumerable.Repeat(true, 4).ToArray());
            FbxLayerChannels.WriteColor(mesh, topology, Enumerable.Repeat(.345678901234567, 16).ToArray(), Enumerable.Repeat(true, 4).ToArray());
            var node = FbxNode.Create(scene, "Quad_LOD0"); node.SetNodeAttribute(mesh); scene.GetRootNode().AddChild(node);
            if (embedded || external)
            {
                var texture = new Texture2D(2, 2);
                try { File.WriteAllBytes(Path.Combine(folder, "map.png"), texture.EncodeToPNG()); }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
                node.AddMaterial(FbxStructureEdit.NewMaterial(scene, "M_Quad", 1, 1, 1, "T_Quad_Albedo",
                    Path.Combine(folder, external ? "missing/map.png" : "map.png"), "map.png", null));
            }
            using var exporter = FbxExporter.Create(manager, "Source");
            int format = new FbxWriteSettings(binary, embedded).WriterFormat(manager);
            Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.SetFileExportVersion("FBX201400") && exporter.Export(scene));
            return path;
        }

        [TestCase(false, MeshLabFbxFormat.Binary)]
        [TestCase(true, MeshLabFbxFormat.Binary)]
        [TestCase(false, MeshLabFbxFormat.ASCII)]
        [TestCase(true, MeshLabFbxFormat.ASCII)]
        public void DocumentPolicyChangesContainerWithoutRebuildingPolygons(bool inputBinary, MeshLabFbxFormat outputFormat)
        {
            MeshLabProjectSettings.Instance.fbxFormat = outputFormat;
            string output = Path.Combine(folder, "output.fbx");
            using (var document = FbxSourceDocument.Load(Source(inputBinary))) document.Save(output);
            using (var document = FbxSourceDocument.Load(output))
            {
                Assert.AreEqual(outputFormat == MeshLabFbxFormat.Binary, document.Binary);
                Assert.IsFalse(document.EmbedsMedia);
                Assert.AreEqual((7, 4), (document.Major, document.Minor));
                Assert.AreEqual(4, document.Meshes[0].GetPolygonSize(0), "an authored quad remains a quad");
                Assert.AreEqual(4, document.Meshes[0].GetControlPointsCount());
            }
        }

        [TestCase(MeshLabFbxFormat.Binary)]
        [TestCase(MeshLabFbxFormat.ASCII)]
        public void RemovingEmbeddedMediaKeepsExternalMapsAfterDocumentDisposal(MeshLabFbxFormat outputFormat)
        {
            string source = Source(true, embedded: true);
            File.Delete(Path.Combine(folder, "map.png"));
            MeshLabProjectSettings.Instance.fbxFormat = outputFormat;
            string output = Path.Combine(folder, "external.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                Assert.IsTrue(document.EmbedsMedia);
                document.SaveAtomic(output);
            }
            using (var document = FbxSourceDocument.Load(output))
            {
                Assert.IsFalse(document.EmbedsMedia);
                Assert.AreEqual(4, document.Meshes[0].GetPolygonSize(0));
                var mesh = document.Meshes[0];
                Assert.AreEqual(.123456789012345, mesh.GetControlPointAt(0).X, 1e-12);
                var topology = new FbxLayerChannels.Topology(mesh);
                foreach (double value in FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(mesh)[0], topology))
                    Assert.AreEqual(.234567890123456, value, 1e-12);
                foreach (double value in FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(mesh), topology))
                    Assert.AreEqual(.345678901234567, value, 1e-12);
                string inspection = Path.Combine(folder, "inspection.fbx");
                document.ExportScene(document.Scene, inspection, new FbxWriteSettings(false, false).WriterFormat(document.Manager), document.Manager.GetIOSettings());
                var names = Regex.Matches(File.ReadAllText(inspection), "(?m)^\\s*(?:FileName|Filename):\\s*\"([^\"]+)\"")
                    .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                Assert.IsNotEmpty(names);
                foreach (string name in names)
                {
                    Assert.IsTrue(File.Exists(name), name);
                    StringAssert.Contains("external.fbm", name, "uses the final filename, never the atomic .tmp or disposable extraction folder");
                }
            }
        }

        [Test]
        public void BinaryEmbeddingCanBeEnabledExplicitlyForDocumentSave()
        {
            string source = Source(true, embedded: true), output = Path.Combine(folder, "embedded.fbx");
            File.Delete(Path.Combine(folder, "map.png"));
            MeshLabProjectSettings.Instance.embedFbxTextures = true;
            using (var document = FbxSourceDocument.Load(source)) document.Save(output);
            using (var document = FbxSourceDocument.Load(output)) Assert.IsTrue(document.EmbedsMedia);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EmbeddingResolvesExternalMapsRelativeToTheOriginalDocument(bool binary)
        {
            string source = Source(binary, external: true);
            var original = File.ReadAllBytes(Path.Combine(folder, "map.png"));
            string relocated = Path.Combine(folder, "relocated"); Directory.CreateDirectory(Path.Combine(relocated, "missing"));
            string sourceMap = Path.Combine(relocated, "missing", "map.png"); File.WriteAllBytes(sourceMap, original);
            string relocatedSource = Path.Combine(relocated, "source.fbx"); File.Move(source, relocatedSource); source = relocatedSource;
            string destination = Path.Combine(folder, "elsewhere"); Directory.CreateDirectory(destination);
            string output = Path.Combine(destination, "embedded.fbx");
            MeshLabProjectSettings.Instance.embedFbxTextures = true;
            using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
            File.Delete(source); File.Delete(sourceMap); File.Delete(Path.Combine(folder, "map.png"));
            using var saved = FbxSourceDocument.Load(output);
            Assert.IsTrue(saved.EmbedsMedia, "relative-only source maps must be embedded despite scratch import and moved output");
            MeshLabProjectSettings.Instance.embedFbxTextures = false;
            saved.Save(Path.Combine(destination, "external.fbx"));
            string[] extracted = Directory.GetFiles(Path.Combine(destination, "external.fbm"), "*.png", SearchOption.AllDirectories);
            Assert.AreEqual(1, extracted.Length);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(extracted[0]));
        }

        [Test]
        public void MissingExternalMediaRefusesEmbeddingBeforeReplacingTheTarget()
        {
            string source = Source(false, external: true), output = Path.Combine(folder, "existing.fbx");
            byte[] original = { 5, 7, 11 }; File.WriteAllBytes(output, original);
            MeshLabProjectSettings.Instance.embedFbxTextures = true;
            using var document = FbxSourceDocument.Load(source);
            Assert.Throws<IOException>(() => document.SaveAtomic(output));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
            Assert.IsFalse(File.Exists(output + ".tmp"));
        }

        [Test]
        public void ExternalizedMapsAreImportedBeforeTheSavedModel()
        {
            string source = Source(true, embedded: true);
            string name = "MeshLabMediaImport_" + Guid.NewGuid().ToString("N"), assets = "Assets/" + name;
            UnityEditor.AssetDatabase.CreateFolder("Assets", name);
            try
            {
                string output = Path.GetFullPath(assets + "/output.fbx");
                using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
                string[] maps = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(output), "output.fbm"), "*.png", SearchOption.AllDirectories);
                Assert.IsNotEmpty(maps);
                foreach (string map in maps)
                {
                    string relative = "Assets/" + Path.GetRelativePath(Application.dataPath, map).Replace('\\', '/');
                    Assert.IsNotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(relative), "new maps must be imported without a global refresh: " + relative);
                }
                UnityEditor.AssetDatabase.ImportAsset(assets + "/output.fbx", UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                Assert.IsNotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assets + "/output.fbx"));
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(assets); }
        }

        [Test]
        public void ReusedExternalizedMapsAreImportedWithoutBecomingRollbackOwned()
        {
            string source = Source(true, embedded: true), seed = Path.Combine(folder, "seed.fbx");
            using (var document = FbxSourceDocument.Load(source)) document.Save(seed);
            string name = "MeshLabMediaReuse_" + Guid.NewGuid().ToString("N"), assets = "Assets/" + name;
            UnityEditor.AssetDatabase.CreateFolder("Assets", name);
            UnityEditor.AssetDatabase.DisallowAutoRefresh();
            try
            {
                string output = Path.GetFullPath(assets + "/output.fbx"), media = Path.GetFullPath(assets + "/output.fbm");
                string seedMedia = Path.Combine(folder, "seed.fbm");
                var existing = new Dictionary<string, byte[]>();
                foreach (string map in Directory.GetFiles(seedMedia, "*.png", SearchOption.AllDirectories))
                {
                    string path = Path.Combine(media, Path.GetRelativePath(seedMedia, map));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, File.ReadAllBytes(map)); existing[path] = File.ReadAllBytes(path);
                    Assert.IsNull(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/" + Path.GetRelativePath(Application.dataPath, path).Replace('\\', '/')));
                }
                using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
                Assert.IsNotEmpty(existing);
                foreach (var pair in existing)
                {
                    string asset = "Assets/" + Path.GetRelativePath(Application.dataPath, pair.Key).Replace('\\', '/');
                    Assert.IsNotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(asset), "reused maps also need an explicit import");
                    CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key));
                }
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    using var document = FbxSourceDocument.Load(source);
                    using var locked = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Assert.Throws<IOException>(() => document.SaveAtomic(output));
                    foreach (var pair in existing) CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key));
                }
            }
            finally { UnityEditor.AssetDatabase.AllowAutoRefresh(); UnityEditor.AssetDatabase.DeleteAsset(assets); }
        }

        [Test]
        [Platform("Win")]
        public void DeepOutputPathsUseBoundedMediaNames()
        {
            string source = Source(true, embedded: true);
            string directory = Path.Combine(folder, new string('d', 190 - folder.Length - 1));
            Directory.CreateDirectory(directory);
            string output = Path.Combine(directory, "output.fbx"), media = Path.Combine(directory, "output.fbm");
            Assert.Less(output.Length, 240, "the target FBX itself is a valid Windows path");
            Assert.Greater(Path.Combine(media, new string('h', 64), "map.png").Length, 260, "the old media layout exceeds MAX_PATH");
            using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
            string[] maps = Directory.GetFiles(media, "*.png", SearchOption.AllDirectories);
            Assert.AreEqual(1, maps.Length); Assert.Less(maps[0].Length, 250);
            using var saved = FbxSourceDocument.Load(output);
            Assert.IsFalse(saved.EmbedsMedia); Assert.AreEqual(4, saved.Meshes[0].GetPolygonSize(0));
        }

        [Test]
        public void LocalPackageRollbackDeletesImportedMediaAndItsMeta()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.sasharx.unitymeshlab");
            Assert.IsNotNull(package);
            string name = "MeshLabMediaRollback_" + Guid.NewGuid().ToString("N"), asset = package.assetPath + "/" + name;
            UnityEditor.AssetDatabase.CreateFolder(package.assetPath, name);
            var texture = new Texture2D(2, 2);
            try
            {
                string directory = Path.Combine(package.resolvedPath, name), file = Path.Combine(directory, "map.png");
                File.WriteAllBytes(file, texture.EncodeToPNG());
                UnityEditor.AssetDatabase.ImportAsset(asset + "/map.png", UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                Assert.IsTrue(File.Exists(file + ".meta"));
                FbxExternalMedia.DeleteCreated(file);
                Assert.IsFalse(File.Exists(file)); Assert.IsFalse(File.Exists(file + ".meta"), "local-package cleanup must use AssetDatabase");
                FbxExternalMedia.DeleteCreated(directory);
                Assert.IsFalse(Directory.Exists(directory)); Assert.IsFalse(File.Exists(directory + ".meta"));
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); UnityEditor.AssetDatabase.DeleteAsset(asset); }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        [Platform("Win")]
        public void FailedAtomicExportPreservesExistingFbxAndMapsAndRemovesOnlyNewMaps(bool previousExport, bool inProject)
        {
            string source = Source(true, embedded: true), output = Path.Combine(folder, "output.fbx");
            File.Delete(Path.Combine(folder, "map.png"));
            string assetFolder = null;
            try
            {
                if (inProject)
                {
                    string name = "MeshLabMediaFailure_" + Guid.NewGuid().ToString("N");
                    UnityEditor.AssetDatabase.CreateFolder("Assets", name); assetFolder = "Assets/" + name;
                    output = Path.GetFullPath(assetFolder + "/output.fbx");
                }
                using var document = FbxSourceDocument.Load(source);
                if (previousExport) document.Save(output);
                else File.WriteAllBytes(output, File.ReadAllBytes(source));
                if (inProject) UnityEditor.AssetDatabase.ImportAsset(assetFolder + "/output.fbx");
                var original = File.ReadAllBytes(output);
                string media = Path.Combine(Path.GetDirectoryName(output), "output.fbm");
                var maps = Directory.Exists(media) ? Directory.GetFiles(media, "*", SearchOption.AllDirectories) : Array.Empty<string>();
                var bytes = maps.ToDictionary(path => path, File.ReadAllBytes);
                using (var locked = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.Throws<IOException>(() => document.SaveAtomic(output));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
                foreach (var pair in bytes) CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key));
                CollectionAssert.AreEquivalent(maps, Directory.Exists(media) ? Directory.GetFiles(media, "*", SearchOption.AllDirectories) : Array.Empty<string>());
                if (!previousExport) Assert.IsFalse(Directory.Exists(media), "new empty media folders must also be rolled back");
                Assert.IsFalse(File.Exists(output + ".tmp"));
            }
            finally { if (assetFolder != null) UnityEditor.AssetDatabase.DeleteAsset(assetFolder); }
        }

        [Test]
        [Platform("Win")]
        public void FailureAfterAtomicCommitKeepsTheInstalledFbxExternalMaps()
        {
            string source = Source(true, embedded: true), output = Path.Combine(folder, "committed.fbx");
            File.Delete(Path.Combine(folder, "map.png"));
            File.Copy(source, output);
            string meta = output + ".meta", originalMeta = "fileFormatVersion: 2\nguid: 991c2ef843634137bdfc6f0e7949f4de\n";
            File.WriteAllText(meta, originalMeta);
            using (var document = FbxSourceDocument.Load(source))
            using (var locked = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<IOException>(() => document.SaveAtomic(output), "metadata restoration fails after the FBX rename committed");
            Assert.AreEqual(originalMeta, File.ReadAllText(meta));
            string media = Path.Combine(folder, "committed.fbm");
            Assert.IsTrue(Directory.Exists(media));
            Assert.IsNotEmpty(Directory.GetFiles(media, "*", SearchOption.AllDirectories), "installed FBX still owns the new external maps");
            using (var document = FbxSourceDocument.Load(output))
            {
                Assert.IsFalse(document.EmbedsMedia, "replacement really committed before the injected metadata failure");
                string inspection = Path.Combine(folder, "committed-inspection.fbx");
                document.ExportScene(document.Scene, inspection, new FbxWriteSettings(false, false).WriterFormat(document.Manager), document.Manager.GetIOSettings());
                var names = Regex.Matches(File.ReadAllText(inspection), "(?m)^\\s*(?:FileName|Filename):\\s*\"([^\"]+)\"")
                    .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                Assert.IsNotEmpty(names);
                foreach (string name in names) Assert.IsTrue(File.Exists(name), name);
            }
            string pathHash = unchecked((uint)Path.GetFullPath(output).GetHashCode()).ToString("X8");
            string backup = Path.Combine(Path.GetTempPath(), Path.GetFileName(output) + "." + pathHash + ".fbx.bak");
            Assert.IsTrue(File.Exists(backup), "retain the old FBX until metadata restoration and cleanup succeed");
            File.Delete(backup);
            File.Delete(Path.Combine(Path.GetTempPath(), Path.GetFileName(output) + "." + pathHash + ".meta.bak"));
        }

        [Test]
        public void RollbackDeletesUnimportedProjectFilesAndFoldersWithoutLeavingMeta()
        {
            string name = "MeshLabUnimportedMedia_" + Guid.NewGuid().ToString("N"), assetFolder = "Assets/" + name;
            UnityEditor.AssetDatabase.CreateFolder("Assets", name);
            string fresh = Path.GetFullPath(assetFolder + "/new.fbm");
            var texture = new Texture2D(2, 2);
            try
            {
                Directory.CreateDirectory(fresh);
                string file = Path.Combine(fresh, "map.png"); File.WriteAllBytes(file, texture.EncodeToPNG());
                FbxExternalMedia.DeleteCreated(file);
                FbxExternalMedia.DeleteCreated(fresh);
                Assert.IsFalse(Directory.Exists(fresh));
                Assert.IsFalse(File.Exists(file + ".meta"));
                Assert.IsFalse(File.Exists(fresh + ".meta"));
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); UnityEditor.AssetDatabase.DeleteAsset(assetFolder); }
        }

        [TestCase("FileName", false)]
        [TestCase("Filename", false)]
        [TestCase("RelativeFilename", true)]
        [TestCase("Path", false)]
        [TestCase("RelPath", true)]
        public void FilenameRewriteKeepsDistinctCaseSensitiveMediaAndOtherMetadata(string field, bool relative)
        {
            string target = Path.Combine(folder, "output.fbx"), scratch = Path.Combine(folder, "scratch");
            string first = Path.Combine(scratch, "input.fbm", "A.png"), second = Path.Combine(scratch, "input.fbm", "a.png");
            // Model a case-sensitive extraction filesystem even on a Windows test host.
            var paths = new Dictionary<string, string>(StringComparer.Ordinal) {
                [first] = Path.Combine(folder, "output.fbm", "first", "A.png"),
                [second] = Path.Combine(folder, "output.fbm", "second", "a.png")
            };
            string prefix = field == "Path" || field == "RelPath"
                ? "\tP: \"" + field + "\", \"KString\", \"XRefUrl\", \"\", \"" : "\t" + field + ": \"";
            foreach (var pair in paths)
            {
                string stored = relative ? Path.GetRelativePath(scratch, pair.Key) : pair.Key;
                string line = prefix + stored.Replace('\\', '/') + "\", \"keep\"";
                string expected = relative ? Path.GetRelativePath(folder, pair.Value) : pair.Value;
                Assert.AreEqual(prefix + expected.Replace('\\', '/') + "\", \"keep\"",
                    FbxExternalMedia.RewriteFilename(line, paths, scratch, target));
            }
            string unknown = prefix + "unmapped.png\"";
            Assert.AreEqual(unknown, FbxExternalMedia.RewriteFilename(unknown, paths, scratch, target));
            Assert.AreEqual("Name: \"A.png\"", FbxExternalMedia.RewriteFilename("Name: \"A.png\"", paths, scratch, target));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RebuiltFilesUseTheSelectedContainerIncludingNormalizedAtomicWrites(bool normalized, bool atomic)
        {
            MeshLabProjectSettings.Instance.fbxFormat = MeshLabFbxFormat.ASCII;
            var root = new GameObject("Quad_LOD0"); var mesh = new Mesh();
            try
            {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
                root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
                string output = Path.Combine(folder, "rebuild.fbx");
                if (atomic) FbxExport.WriteAtomic(output, root, normalized);
                else FbxExport.Write(output, root, normalizedTransforms: normalized);
                using (var document = FbxSourceDocument.Load(output))
                {
                    Assert.IsFalse(document.Binary); Assert.IsFalse(document.EmbedsMedia);
                    Assert.AreEqual(3, document.Meshes[0].GetControlPointsCount());
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
        }
#endif
    }
}
