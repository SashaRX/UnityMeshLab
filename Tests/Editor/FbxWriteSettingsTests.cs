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
        string Source(bool binary, bool embedded = false)
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
            if (embedded)
            {
                var texture = new Texture2D(2, 2);
                try { File.WriteAllBytes(Path.Combine(folder, "map.png"), texture.EncodeToPNG()); }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
                node.AddMaterial(FbxStructureEdit.NewMaterial(scene, "M_Quad", 1, 1, 1, "T_Quad_Albedo",
                    Path.Combine(folder, "map.png"), "map.png", null));
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
                Assert.IsFalse(File.Exists(output + ".tmp"));
            }
            finally { if (assetFolder != null) UnityEditor.AssetDatabase.DeleteAsset(assetFolder); }
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
