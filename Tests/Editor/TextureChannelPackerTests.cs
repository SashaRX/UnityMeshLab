using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class TextureChannelPackerTests
    {
        [TestCase(".png")]
        [TestCase(".jpg")]
        [TestCase(".tga")]
        [TestCase(".psd")]
        [TestCase(".PSD")]
        [TestCase(".tif")]
        [TestCase(".tiff")]
        /// <summary>Verifies that dropping a path onto a slot binds it and queues background loading without the drag handler touching the filesystem.</summary>
        public void DropAssignsImmediatelyWithoutOpeningTheSource(string extension)
        {
            // A missing source must still bind immediately; only the worker checks readability.
            string path = Path.Combine(Path.GetTempPath(), "MeshLabDeferred-" + Guid.NewGuid().ToString("N") + extension);
            var panel = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            var host = ScriptableObject.CreateInstance<TexturePackingDropTestWindow>();
            string[] oldPaths = DragAndDrop.paths;
            var oldVisualMode = DragAndDrop.visualMode;
            try
            {
                int repaints = 0;
                panel.RequestRepaint = () => repaints++;
                object output = GetPrivate(panel, "albedo");
                object slot = output.GetType().GetField("rgb").GetValue(output);
                DragAndDrop.paths = new[] { path };
                var rect = new Rect(0, 0, 48, 48);
                int events = 0;
                host.HandleEvent = () =>
                {
                    if (Event.current.type != EventType.DragUpdated && Event.current.type != EventType.DragPerform) return;
                    events++;
                    InvokePrivate(panel, "HandleDrop", rect, output, slot);
                };
                host.ShowUtility();
                host.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = new Vector2(24, 24) });
                Assert.IsNull(host.Failure);
                Assert.AreEqual(1, events);
                Assert.AreEqual(DragAndDropVisualMode.Copy, DragAndDrop.visualMode);
                host.SendEvent(new Event { type = EventType.DragPerform, mousePosition = new Vector2(24, 24) });
                Assert.IsNull(host.Failure);
                Assert.AreEqual(2, events);
                Assert.AreEqual(path, slot.GetType().GetField("path").GetValue(slot));
                Assert.IsTrue((bool)output.GetType().GetField("enabled").GetValue(output));
                Assert.IsTrue(((System.Collections.Generic.HashSet<string>)GetPrivate(panel, "pendingSources")).Contains(path));
                Assert.AreEqual(0, ((IDictionary)GetPrivate(panel, "activeLoads")).Count);
                Assert.AreEqual(0, ((IDictionary)GetPrivate(panel, "loaded")).Count);
                Assert.Greater(repaints, 0, "The filename/loading state must be visible immediately.");
                Assert.IsFalse(File.Exists(path));
                Assert.IsFalse(File.Exists(path + ".meta"));
            }
            finally
            {
                host.HandleEvent = null;
                host.Close();
                DragAndDrop.paths = oldPaths;
                DragAndDrop.visualMode = oldVisualMode;
                UnityEngine.Object.DestroyImmediate(panel);
            }
        }

        [Test]
        public void RleRoundTripPreservesChannelsAlphaAndRows()
        {
            var pixels = new Color32[260 * 2];
            for (int i = 0; i < 260; i++) pixels[i] = new Color32(12, 34, 56, 78);
            for (int i = 260; i < pixels.Length; i++)
                pixels[i] = new Color32((byte)i, (byte)(i * 3), (byte)(i * 7), (byte)(i * 11));
            var image = new TexturePackingImage(260, 2, pixels);
            using (var stream = new MemoryStream())
            {
                image.WriteTgaRle(stream);
                byte[] encoded = stream.ToArray();
                Assert.AreEqual(10, encoded[2]);
                Assert.AreEqual(32, encoded[16]);
                Assert.AreEqual(8, encoded[17]);
                Assert.AreEqual(255, encoded[18], "First packet repeats 128 pixels.");
                Assert.Less(encoded.Length, 18 + pixels.Length * 4);
                CollectionAssert.AreEqual(pixels, TexturePackingImage.DecodeTga(encoded).Pixels);
            }
        }

        [TestCase(0)]
        [TestCase(16)]
        [TestCase(32)]
        [TestCase(48)]
        public void RawTgaRespectsBothOriginBits(int origin)
        {
            byte[] bytes = new byte[18 + 4 * 3];
            bytes[2] = 2;
            bytes[12] = bytes[14] = 2;
            bytes[16] = 24;
            bytes[17] = (byte)origin;
            for (int i = 0; i < 4; i++) bytes[18 + i * 3 + 2] = (byte)(i + 1);
            var image = TexturePackingImage.DecodeTga(bytes);
            for (int i = 0; i < 4; i++)
            {
                int x = i % 2, y = i / 2;
                if ((origin & 16) != 0) x = 1 - x;
                if ((origin & 32) != 0) y = 1 - y;
                Assert.AreEqual(i + 1, image.Pixels[y * 2 + x].r);
                Assert.AreEqual(255, image.Pixels[y * 2 + x].a);
            }
        }

        [Test]
        public void TgaRejectsPacketsBeyondTheImageAndTruncatedData()
        {
            byte[] bytes = new byte[23];
            bytes[2] = 10;
            bytes[12] = bytes[14] = 1;
            bytes[16] = 32;
            bytes[18] = 129;
            Assert.Throws<InvalidDataException>(() => TexturePackingImage.DecodeTga(bytes));
            Array.Resize(ref bytes, 19);
            bytes[18] = 128;
            Assert.Throws<EndOfStreamException>(() => TexturePackingImage.DecodeTga(bytes));
        }

        [Test]
        public void WholeRgbCanTakeAlphaFromAnotherImagesRedChannel()
        {
            var rgb = new TexturePackingImage(1, 1, new[] { new Color32(10, 20, 30, 40) });
            var alpha = new TexturePackingImage(1, 1, new[] { new Color32(99, 88, 77, 66) });
            var sources = new TexturePackingImage[4];
            sources[3] = alpha;
            var result = TexturePackingImage.Compose(2, 3, rgb, sources,
                new[] { 0, 1, 2, 0 }, new byte[] { 255, 255, 255, 255 }, true);
            foreach (var pixel in result.Pixels) Assert.AreEqual(new Color32(10, 20, 30, 99), pixel);
            sources[3] = null;
            Assert.AreEqual(40, TexturePackingImage.Compose(1, 1, rgb, sources,
                new[] { 0, 1, 2, 3 }, new byte[] { 0, 0, 0, 255 }, true).Pixels[0].a);
        }

        [Test]
        public void SeparateChannelsUseAlphaAndDefaultsWithoutGammaConversion()
        {
            var image = new TexturePackingImage(1, 1, new[] { new Color32(10, 20, 30, 128) });
            var result = TexturePackingImage.Compose(1, 1, null, new[] { image, null, image, image },
                new[] { 3, 0, 0, 1 }, new byte[] { 0, 200, 0, 255 }, false);
            Assert.AreEqual(new Color32(128, 200, 10, 20), result.Pixels[0]);
        }

        [Test]
        public void BilinearResizeUsesTheWholeImageAndClampsEdges()
        {
            var image = new TexturePackingImage(2, 1,
                new[] { new Color32(0, 0, 0, 0), new Color32(200, 100, 50, 240) });
            Assert.AreEqual(new Color32(100, 50, 25, 120), image.Sample(0, 0, 1, 1));
            Assert.AreEqual(image.Pixels[0], image.Sample(0, 0, 4, 3));
            Assert.AreEqual(image.Pixels[1], image.Sample(3, 2, 4, 3));
            Assert.AreEqual(50, image.Sample(1, 1, 4, 3).r);
        }

        [TestCase("Assets/Props/SM_Chair.fbx", "Chair")]
        [TestCase("Assets/Props/Chair_SM_Back.fbx", "Chair_SM_Back")]
        public void ModelNamingStripsOnlyTheLeadingPrefix(string path, string expected)
        {
            Assert.AreEqual(expected, TextureChannelPackerWindow.ModelBaseName(path));
        }

        [Test]
        public void ExternalPngIsReadWithoutCreatingAProjectAsset()
        {
            string folder = Path.Combine(Path.GetTempPath(), "MeshLabTextureTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "source.png");
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                texture.SetPixels32(new[] { new Color32(10, 20, 30, 40) });
                texture.Apply();
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(texture));
                Assert.AreEqual(new Color32(10, 20, 30, 40), TexturePackingImage.Load(path).Pixels[0]);
                Assert.AreEqual(1, Directory.GetFiles(folder).Length);
                Assert.IsFalse(File.Exists(path + ".meta"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                File.Delete(path);
                Directory.Delete(folder);
            }
        }

        [Test]
        public void UnityImporterReadsRleOutputWithCorrectOrientationAndAlpha()
        {
            string assetPath = "Assets/MeshLabTexturePackingTest-" + Guid.NewGuid().ToString("N") + ".tga";
            var pixels = new[] { new Color32(10, 20, 30, 40), new Color32(10, 20, 30, 40),
                new Color32(50, 60, 70, 80), new Color32(90, 100, 110, 120) };
            try
            {
                using (var stream = File.Create(Path.GetFullPath(assetPath)))
                    new TexturePackingImage(2, 2, pixels).WriteTgaRle(stream);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                importer.isReadable = true;
                importer.sRGBTexture = false;
                importer.alphaIsTransparency = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
                var result = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                CollectionAssert.AreEqual(pixels, result.GetPixels32());
            }
            finally { AssetDatabase.DeleteAsset(assetPath); }
        }

        [UnityTest]
        public IEnumerator WindowSavesThreeNamedOutputsAndImportsOnlyResults()
        {
            string id = Guid.NewGuid().ToString("N");
            string sourcePath = Path.Combine(Path.GetTempPath(), "MeshLabSource-" + id + ".tga");
            string assetFolder = "Assets/MeshLabPacking-" + id;
            var window = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            var pixel = new Color32(12, 34, 56, 78);
            var paintedPhases = new System.Collections.Generic.HashSet<string>();
            window.RequestRepaint = () =>
            {
                if (GetPrivate(window, "saveTask") == null) return;
                if ((bool)GetPrivate(window, "snapshotPending")) paintedPhases.Add("snapshot");
                else if (GetPrivate(window, "importTask") != null) paintedPhases.Add("import");
                else paintedPhases.Add("stage");
            };
            try
            {
                using (var stream = File.Create(sourcePath))
                    new TexturePackingImage(1, 1, new[] { pixel }).WriteTgaRle(stream);
                byte[] original = File.ReadAllBytes(sourcePath);
                AssetDatabase.CreateFolder("Assets", "MeshLabPacking-" + id);
                SetPrivate(window, "followFbx", false);
                SetPrivate(window, "textureName", "Chair");
                SetPrivate(window, "outputFolder", Path.GetFullPath(assetFolder));
                SetPrivate(window, "widthIndex", 0);
                SetPrivate(window, "heightIndex", 0);
                foreach (string field in new[] { "albedo", "mrca", "normal" })
                {
                    object output = GetPrivate(window, field);
                    output.GetType().GetField("split").SetValue(output, false);
                    object rgb = output.GetType().GetField("rgb").GetValue(output);
                    InvokePrivate(window, "Assign", output, rgb, sourcePath);
                }
                yield return WaitForSources(window);
                InvokePrivate(window, "SaveEnabled");
                SetPrivate(window, "followFbx", true);
                InvokePrivate(window, "OnSelectionChanged");
                Assert.IsTrue((bool)GetPrivate(window, "selectionPending"));
                yield return WaitForExport(window);
                Assert.Contains("stage", new System.Collections.Generic.List<string>(paintedPhases), "Encoding phase must repaint without mouse input");
                Assert.Contains("import", new System.Collections.Generic.List<string>(paintedPhases), "Import phase must repaint to disable cancellation");
                Assert.IsFalse((bool)GetPrivate(window, "selectionPending"));
                SetPrivate(window, "followFbx", false);
                Assert.IsEmpty((string)GetPrivate(window, "error"));
                foreach (string suffix in new[] { "Albedo", "MRCA", "Normal" })
                {
                    string path = assetFolder + "/T_Chair_" + suffix + ".tga";
                    Assert.IsTrue(File.Exists(path), path);
                    var decoded = TexturePackingImage.DecodeTga(File.ReadAllBytes(path));
                    Assert.AreEqual(64, decoded.Width);
                    Assert.AreEqual(64, decoded.Height);
                    Assert.AreEqual(pixel, decoded.Pixels[0]);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    Assert.IsNotNull(importer);
                    Assert.AreEqual(suffix == "Normal" ? TextureImporterType.NormalMap : TextureImporterType.Default,
                        importer.textureType);
                    if (suffix != "Normal") Assert.AreEqual(suffix == "Albedo", importer.sRGBTexture);
                }
                Assert.IsFalse(File.Exists(sourcePath + ".meta"));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(sourcePath));
                Assert.AreEqual(3, Directory.GetFiles(assetFolder, "*.tga").Length);

                // Change naming suffixes without changing the semantic output blocks.
                SetPrivate(window, "textureName", "Renamed");
                string[] fields = { "albedo", "mrca", "normal" };
                int[] suffixes = { 2, 1, 0 };
                string[] names = { "MRCA", "Normal", "Albedo" };
                for (int i = 0; i < fields.Length; i++)
                {
                    object output = GetPrivate(window, fields[i]);
                    output.GetType().GetField("suffix").SetValue(output, suffixes[i]);
                }
                InvokePrivate(window, "SaveEnabled");
                yield return WaitForExport(window);
                Assert.IsEmpty((string)GetPrivate(window, "error"));
                for (int i = 0; i < fields.Length; i++)
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(assetFolder + "/T_Renamed_" + names[i] + ".tga");
                    Assert.IsNotNull(importer);
                    Assert.AreEqual(i == 2 ? TextureImporterType.NormalMap : TextureImporterType.Default, importer.textureType);
                    if (i != 2) Assert.AreEqual(i == 0, importer.sRGBTexture);
                    Assert.AreEqual(i == 0, importer.alphaIsTransparency);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                AssetDatabase.DeleteAsset(assetFolder);
                File.Delete(sourcePath);
            }
        }

        [Test]
        public void FailedEncodingLeavesTheExistingFileUntouched()
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshLabAtomic-" + Guid.NewGuid().ToString("N") + ".tga");
            byte[] original = { 1, 2, 3, 4 };
            File.WriteAllBytes(path, original);
            try
            {
                Assert.Throws<IOException>(() => TexturePackingFiles.Stage(path, stream =>
                {
                    stream.WriteByte(99);
                    throw new IOException("Simulated encoding failure.");
                }));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void SuccessfulAtomicReplacementPreservesAssetMetadata()
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshLabAtomic-" + Guid.NewGuid().ToString("N") + ".tga");
            string staged = null;
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2 });
                File.WriteAllText(path + ".meta", "existing-guid");
                staged = TexturePackingFiles.Stage(path, stream => stream.WriteByte(42));
                var original = new FileInfo(path);
                TexturePackingFiles.InstallBatch(new[] { new TexturePackingFiles.PendingFile(staged, path,
                    true, original.Length, original.LastWriteTimeUtc) });
                CollectionAssert.AreEqual(new byte[] { 42 }, File.ReadAllBytes(path));
                Assert.AreEqual("existing-guid", File.ReadAllText(path + ".meta"));
                Assert.IsFalse(File.Exists(staged));
            }
            finally
            {
                TexturePackingFiles.Discard(staged);
                File.Delete(path);
                File.Delete(path + ".meta");
            }
        }

        [Test]
        public void PackingAndEncodingSupportCancellation()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new MemoryStream())
            {
                cancellation.Cancel();
                var image = new TexturePackingImage(1, 1, new[] { new Color32(1, 2, 3, 4) });
                Assert.Throws<OperationCanceledException>(() => image.WriteTgaRle(stream, cancellation.Token));
                Assert.Throws<OperationCanceledException>(() => TexturePackingImage.Compose(64, 64, image,
                    new TexturePackingImage[4], new int[4], new byte[4], true, cancellation.Token));
            }
        }

        [Test]
        public void AssetContainmentUsesFilesystemPathCaseRules()
        {
            string original = Path.Combine(Application.dataPath, "Texture.tga");
            Assert.AreEqual("Assets/Texture.tga", InvokeAssetPath(original));
            string differentCase = Path.Combine(Path.GetDirectoryName(Application.dataPath), "assets", "Texture.tga");
            Assert.AreEqual(TexturePackingFiles.ComparisonForDirectory(Path.GetDirectoryName(Application.dataPath)) ==
                StringComparison.OrdinalIgnoreCase ? "Assets/Texture.tga" : null, InvokeAssetPath(differentCase));
        }

        [TestCase(0, 255)]
        [TestCase(8, 0)]
        public void TgaPaddingIsOpaqueUnlessAlphaIsDeclared(int attributeBits, int expected)
        {
            byte[] bytes = new byte[22];
            bytes[2] = 2;
            bytes[12] = bytes[14] = 1;
            bytes[16] = 32;
            bytes[17] = (byte)attributeBits;
            Assert.AreEqual(expected, TexturePackingImage.DecodeTga(bytes).Pixels[0].a);
        }

        [Test]
        public void LateArrivingDestinationIsNotOverwritten()
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshLabLate-" + Guid.NewGuid().ToString("N") + ".tga");
            string staged = TexturePackingFiles.Stage(path, stream => stream.WriteByte(99));
            try
            {
                var pending = new TexturePackingFiles.PendingFile(staged, path, false, 0, default);
                File.WriteAllBytes(path, new byte[] { 42 });
                Assert.Throws<IOException>(() => TexturePackingFiles.InstallBatch(new[] { pending }));
                CollectionAssert.AreEqual(new byte[] { 42 }, File.ReadAllBytes(path));
            }
            finally { TexturePackingFiles.Discard(staged); File.Delete(path); }
        }

        [Test]
        public void FailedSecondCommitRestoresTheFirstOutput()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MeshLabBatch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string first = Path.Combine(directory, "first.tga"), second = Path.Combine(directory, "second.tga");
            string staged = TexturePackingFiles.Stage(first, stream => stream.WriteByte(99));
            try
            {
                File.WriteAllBytes(first, new byte[] { 1 });
                File.WriteAllBytes(second, new byte[] { 2 });
                var firstInfo = new FileInfo(first);
                var secondInfo = new FileInfo(second);
                var files = new[]
                {
                    new TexturePackingFiles.PendingFile(staged, first, true, firstInfo.Length, firstInfo.LastWriteTimeUtc),
                    new TexturePackingFiles.PendingFile(Path.Combine(directory, "missing-stage"), second,
                        true, secondInfo.Length, secondInfo.LastWriteTimeUtc)
                };
                Assert.Throws<FileNotFoundException>(() => TexturePackingFiles.InstallBatch(files));
                CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(first));
                CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(second));
                Assert.AreEqual(2, Directory.GetFiles(directory).Length);
            }
            finally { TexturePackingFiles.Discard(staged); Directory.Delete(directory, true); }
        }

        [UnityTest]
        public IEnumerator ReloadRetainsEarlyFailuresWhenLaterSourcesAreValid()
        {
            string valid = Path.Combine(Path.GetTempPath(), "MeshLabReload-" + Guid.NewGuid().ToString("N") + ".tga");
            var window = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            try
            {
                using (var stream = File.Create(valid))
                    new TexturePackingImage(1, 1, new[] { new Color32(1, 2, 3, 4) }).WriteTgaRle(stream);
                object albedoOutput = GetPrivate(window, "albedo"), normalOutput = GetPrivate(window, "normal");
                object missingSlot = albedoOutput.GetType().GetField("rgb").GetValue(albedoOutput);
                object validSlot = normalOutput.GetType().GetField("rgb").GetValue(normalOutput);
                missingSlot.GetType().GetField("path").SetValue(missingSlot, valid + ".missing.tga");
                validSlot.GetType().GetField("path").SetValue(validSlot, valid);
                InvokePrivate(window, "ReloadSources");
                yield return WaitForSources(window);
                StringAssert.Contains("missing.tga", (string)GetPrivate(window, "error"));
                InvokePrivate(window, "BuildPreview", normalOutput);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); File.Delete(valid); }
        }

        [Test]
        public void IgnoredAssetFoldersFailBeforeExport()
        {
            string ignored = Path.Combine(Application.dataPath, "Textures~", "Texture.tga");
            var failure = Assert.Throws<TargetInvocationException>(() => typeof(TextureChannelPackerPanel)
                .GetMethod("ValidateAssetDestination", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { ignored }));
            Assert.IsInstanceOf<InvalidOperationException>(failure.InnerException);
        }

        private static object InvokeAssetPath(string path)
        {
            return typeof(TextureChannelPackerPanel).GetMethod("AssetPath", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { path });
        }

        private static void UnsubscribeUpdate(EditorApplication.CallbackFunction callback) => EditorApplication.update -= callback;

        private static IEnumerator WaitForSources(TextureChannelPackerPanel window)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            var loading = typeof(TextureChannelPackerPanel).GetMethod("SourcesLoading", BindingFlags.NonPublic | BindingFlags.Instance);
            while ((bool)loading.Invoke(window, null))
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline, "Source loading did not finish.");
                yield return null;
            }
        }

        private static IEnumerator WaitForExport(TextureChannelPackerPanel window)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (GetPrivate(window, "saveTask") != null)
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline, "Export did not finish.");
                yield return null;
            }
        }

        [UnityTest]
        /// <summary>Verifies that loading a large PNG copies its pixels in several bounded slices across frames while preserving every channel byte.</summary>
        public IEnumerator LargePngCopiesInBoundedBulkSlicesAndKeepsChannelBytes()
        {
            const int size = 2048;
            string path = Path.Combine(Path.GetTempPath(), "MeshLabBulk-" + Guid.NewGuid().ToString("N") + ".png");
            var source = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var expected = new Color32[size * size];
            for (int i = 0; i < expected.Length; i++) expected[i] = new Color32((byte)i, (byte)(i >> 8), 117, (byte)(i % 251));
            try
            {
                source.SetPixels32(expected);
                source.Apply();
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(source));
                using (var load = new TexturePackingLoad(path))
                {
                    double started = EditorApplication.timeSinceStartup, deadline = started + 30;
                    double maximumTick = 0;
                    int slices = 0;
                    TexturePackingImage image;
                    while (true)
                    {
                        int before = (int)GetPrivate(load, "offset");
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        bool finished = load.TryComplete(out image);
                        maximumTick = Math.Max(maximumTick, timer.Elapsed.TotalMilliseconds);
                        int copied = (int)GetPrivate(load, "offset") - before;
                        if (copied > 0)
                        {
                            slices++;
                            var native = (Texture2D)GetPrivate(load, "texture");
                            if (native.format == TextureFormat.RGBA32) Assert.LessOrEqual(copied, 1048576);
                        }
                        if (finished) break;
                        Assert.Less(EditorApplication.timeSinceStartup, deadline);
                        yield return null;
                    }
                    Assert.Greater(slices, 1, "A large source must yield between copy slices.");
                    CollectionAssert.AreEqual(expected, image.Pixels);
                    TestContext.WriteLine("2048x2048 PNG: " + slices + " pixel-copy updates, max poll " +
                        maximumTick.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms, total " +
                        (EditorApplication.timeSinceStartup - started).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s.");
                }
                Assert.IsFalse(File.Exists(path + ".meta"));
            }
            finally { UnityEngine.Object.DestroyImmediate(source); File.Delete(path); }
        }

        [UnityTest]
        public IEnumerator AsyncPngLoadPreservesBytesAndReleasesItsTexture()
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshLabAsync-" + Guid.NewGuid().ToString("N") + ".png");
            var source = new Texture2D(257, 257, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[257 * 257];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32((byte)i, 20, 128, (byte)(i % 251));
            try
            {
                source.SetPixels32(pixels);
                source.Apply();
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(source));
                using (var load = new TexturePackingLoad(path))
                {
                    double deadline = EditorApplication.timeSinceStartup + 30;
                    TexturePackingImage image;
                    int updates = 0;
                    while (!load.TryComplete(out image))
                    {
                        updates++;
                        Assert.Less(EditorApplication.timeSinceStartup, deadline);
                        yield return null;
                    }
                    Assert.GreaterOrEqual(updates, 2, "Large pixel copies must yield to the editor.");
                    CollectionAssert.AreEqual(pixels, image.Pixels);
                    var decodedTexture = (Texture2D)GetPrivate(load, "texture");
                    Assert.IsNotNull(decodedTexture);
                    load.Dispose();
                    Assert.IsTrue(decodedTexture == null, "Native decoder texture must be destroyed.");
                    Assert.Throws<ObjectDisposedException>(() => load.TryComplete(out _));
                }
                Assert.IsFalse(File.Exists(path + ".meta"));
            }
            finally { UnityEngine.Object.DestroyImmediate(source); File.Delete(path); }
        }

        [UnityTest]
        public IEnumerator ClearingSlotCancelsPendingLoadWithoutRecreatingThumbnail()
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshLabClear-" + Guid.NewGuid().ToString("N") + ".tga");
            var window = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            try
            {
                using (var stream = File.Create(path))
                    new TexturePackingImage(1, 1, new[] { new Color32(1, 2, 3, 4) }).WriteTgaRle(stream);
                object output = GetPrivate(window, "albedo");
                object slot = output.GetType().GetField("rgb").GetValue(output);
                InvokePrivate(window, "Assign", output, slot, path);
                InvokePrivate(window, "TickLoads");
                slot.GetType().GetField("path").SetValue(slot, "");
                InvokePrivate(window, "PruneSources");
                yield return null;
                InvokePrivate(window, "TickLoads");
                Assert.AreEqual(0, ((IDictionary)GetPrivate(window, "loaded")).Count);
                Assert.AreEqual(0, ((IDictionary)GetPrivate(window, "activeLoads")).Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); File.Delete(path); }
        }

        [Test]
        public void TgaRejectsImpossibleHugePayloadBeforeAllocationAndSupportsCancellation()
        {
            byte[] header = new byte[18];
            header[2] = 10;
            header[13] = header[15] = 64;
            header[16] = 32;
            Assert.Throws<EndOfStreamException>(() => TexturePackingImage.DecodeTga(header));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => TexturePackingImage.DecodeTga(header, cancellation.Token));
            }
        }

        [Test]
        public void ParallelCompositionMatchesSerialSamplingAtEveryPixel()
        {
            var source = new TexturePackingImage(2, 2, new[] { new Color32(0, 30, 50, 70),
                new Color32(20, 60, 80, 90), new Color32(110, 130, 150, 170), new Color32(220, 230, 240, 250) });
            var packed = TexturePackingImage.Compose(512, 512, source, new TexturePackingImage[4],
                new int[4], new byte[4], true);
            for (int y = 0; y < 512; y++)
                for (int x = 0; x < 512; x++) Assert.AreEqual(source.Sample(x, y, 512, 512), packed.Pixels[y * 512 + x]);
        }

        [UnityTest]
        public IEnumerator ClosingWindowDuringExportCancelsAndRemovesStagedFiles()
        {
            string folder = Path.Combine(Path.GetTempPath(), "MeshLabClose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "source.tga");
            var window = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            try
            {
                using (var stream = File.Create(path))
                    new TexturePackingImage(1, 1, new[] { new Color32(1, 2, 3, 4) }).WriteTgaRle(stream);
                SetPrivate(window, "followFbx", false);
                SetPrivate(window, "outputFolder", folder);
                SetPrivate(window, "widthIndex", 6);
                SetPrivate(window, "heightIndex", 6);
                object output = GetPrivate(window, "albedo");
                InvokePrivate(window, "Assign", output, output.GetType().GetField("rgb").GetValue(output), path);
                yield return WaitForSources(window);
                InvokePrivate(window, "SaveEnabled");
                double snapshotDeadline = EditorApplication.timeSinceStartup + 30;
                while ((bool)GetPrivate(window, "snapshotPending"))
                {
                    Assert.Less(EditorApplication.timeSinceStartup, snapshotDeadline);
                    yield return null;
                }
                var task = (System.Threading.Tasks.Task)GetPrivate(window, "saveTask");
                Assert.IsNotNull(task);
                UnityEngine.Object.DestroyImmediate(window);
                double deadline = EditorApplication.timeSinceStartup + 30;
                while (!task.IsCompleted)
                {
                    Assert.Less(EditorApplication.timeSinceStartup, deadline);
                    yield return null;
                }
                _ = task.Exception;
                CollectionAssert.AreEquivalent(new[] { path }, Directory.GetFiles(folder));
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                Directory.Delete(folder, true);
            }
        }

        [UnityTest]
        public IEnumerator CancelAfterEncodingRemovesStagedOutputsBeforeCommit()
        {
            string folder = Path.Combine(Path.GetTempPath(), "MeshLabCancelStage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "source.tga");
            var window = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            try
            {
                using (var stream = File.Create(path))
                    new TexturePackingImage(1, 1, new[] { new Color32(1, 2, 3, 4) }).WriteTgaRle(stream);
                SetPrivate(window, "followFbx", false);
                SetPrivate(window, "outputFolder", folder);
                SetPrivate(window, "widthIndex", 0);
                SetPrivate(window, "heightIndex", 0);
                object output = GetPrivate(window, "albedo");
                InvokePrivate(window, "Assign", output, output.GetType().GetField("rgb").GetValue(output), path);
                yield return WaitForSources(window);
                InvokePrivate(window, "SaveEnabled");
                // Hold the main-thread commit boundary while the real encoder finishes asynchronously.
                var callback = (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction),
                    window, typeof(TextureChannelPackerPanel).GetMethod("TickSave", BindingFlags.NonPublic | BindingFlags.Instance));
                UnsubscribeUpdate(callback);
                var task = (System.Threading.Tasks.Task)GetPrivate(window, "saveTask");
                double deadline = EditorApplication.timeSinceStartup + 30;
                while (!task.IsCompleted)
                {
                    Assert.Less(EditorApplication.timeSinceStartup, deadline);
                    yield return null;
                }
                InvokePrivate(window, "TickSave");
                UnsubscribeUpdate(callback);
                task = (System.Threading.Tasks.Task)GetPrivate(window, "saveTask");
                while (!task.IsCompleted)
                {
                    Assert.Less(EditorApplication.timeSinceStartup, deadline);
                    yield return null;
                }
                Assert.AreEqual(1, Directory.GetFiles(folder, ".meshlab-texture-*").Length);
                ((CancellationTokenSource)GetPrivate(window, "saveCancellation")).Cancel();
                InvokePrivate(window, "TickSave");
                Assert.IsNull(GetPrivate(window, "saveTask"));
                CollectionAssert.AreEquivalent(new[] { path }, Directory.GetFiles(folder));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); Directory.Delete(folder, true); }
        }

        [UnityTest]
        public IEnumerator ClosingAfterCommitStillFinishesAllImporterSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            string path = Path.Combine(Path.GetTempPath(), "MeshLabImportClose-" + id + ".tga");
            string folder = "Assets/MeshLabImportClose-" + id;
            var window = ScriptableObject.CreateInstance<TextureChannelPackerPanel>();
            try
            {
                using (var stream = File.Create(path))
                    new TexturePackingImage(1, 1, new[] { new Color32(1, 2, 3, 4) }).WriteTgaRle(stream);
                AssetDatabase.CreateFolder("Assets", "MeshLabImportClose-" + id);
                SetPrivate(window, "followFbx", false);
                SetPrivate(window, "outputFolder", Path.GetFullPath(folder));
                SetPrivate(window, "widthIndex", 0);
                SetPrivate(window, "heightIndex", 0);
                foreach (string field in new[] { "albedo", "mrca", "normal" })
                {
                    object output = GetPrivate(window, field);
                    output.GetType().GetField("split").SetValue(output, false);
                    InvokePrivate(window, "Assign", output, output.GetType().GetField("rgb").GetValue(output), path);
                }
                yield return WaitForSources(window);
                InvokePrivate(window, "SaveEnabled");
                double deadline = EditorApplication.timeSinceStartup + 30;
                while (GetPrivate(window, "importTask") == null)
                {
                    Assert.IsNotNull(GetPrivate(window, "saveTask"), (string)GetPrivate(window, "error"));
                    Assert.Less(EditorApplication.timeSinceStartup, deadline);
                    yield return null;
                }
                var importing = (System.Threading.Tasks.Task)GetPrivate(window, "importTask");
                UnityEngine.Object.DestroyImmediate(window);
                while (!importing.IsCompleted)
                {
                    Assert.Less(EditorApplication.timeSinceStartup, deadline);
                    yield return null;
                }
                importing.GetAwaiter().GetResult();
                var normalImporter = (TextureImporter)AssetImporter.GetAtPath(folder + "/T_Texture_Normal.tga");
                Assert.AreEqual(TextureImporterType.NormalMap, normalImporter.textureType);
                var maskImporter = (TextureImporter)AssetImporter.GetAtPath(folder + "/T_Texture_MRCA.tga");
                Assert.IsFalse(maskImporter.sRGBTexture);
                var albedoImporter = (TextureImporter)AssetImporter.GetAtPath(folder + "/T_Texture_Albedo.tga");
                Assert.IsTrue(albedoImporter.sRGBTexture);
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                AssetDatabase.DeleteAsset(folder);
                File.Delete(path);
            }
        }

        private static object GetPrivate(object instance, string name)
        {
            return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }

        private static void SetPrivate(object instance, string name, object value)
        {
            instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
        }

        private static void InvokePrivate(object instance, string name, params object[] arguments)
        {
            instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, arguments);
        }
    }
}

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>Minimal EditorWindow used to deliver real IMGUI drag-and-drop events to a handler under test.</summary>
    internal sealed class TexturePackingDropTestWindow : EditorWindow
    {
        internal Action HandleEvent;
        internal Exception Failure;

        /// <summary>Invokes the test's event handler and captures any exception it throws so the test can assert on it.</summary>
        private void OnGUI()
        {
            try { HandleEvent?.Invoke(); }
            catch (Exception exception) { Failure = exception; }
        }
    }
}
