using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class TexturePackingLoadReviewTests
    {
        [Test]
        public void OverwriteRejectsSameSizeRewriteWithPreservedTimestamp()
        {
            string folder = Path.Combine(Path.GetTempPath(), "MeshLabHash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "output.tga");
            string staged = Path.Combine(folder, "staged.tga");
            try
            {
                File.WriteAllBytes(destination, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(staged, new byte[] { 7, 8, 9 });
                var info = new FileInfo(destination);
                using (var pending = new TexturePackingFiles.PendingFile(staged, destination, true, info.Length, info.LastWriteTimeUtc))
                {
                    File.WriteAllBytes(destination, new byte[] { 4, 5, 6 });
                    File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
                    Assert.Throws<IOException>(() => TexturePackingFiles.InstallBatch(new[] { pending }));
                    CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(destination));
                }
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void OverwriteRejectsAtomicReplacementAfterVerification()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows file identity guard.");
            string folder = Path.Combine(Path.GetTempPath(), "MeshLabIdentity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "output.tga");
            string replacement = Path.Combine(folder, "replacement.tga");
            string staged = Path.Combine(folder, "staged.tga");
            try
            {
                File.WriteAllBytes(destination, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(staged, new byte[] { 7, 8, 9 });
                var info = new FileInfo(destination);
                using (var pending = new TexturePackingFiles.PendingFile(staged, destination, true, info.Length, info.LastWriteTimeUtc))
                {
                    pending.VerifyContent(default);
                    File.WriteAllBytes(replacement, new byte[] { 4, 5, 6 });
                    File.SetLastWriteTimeUtc(replacement, info.LastWriteTimeUtc);
                    File.Replace(replacement, destination, null);
                    Assert.Throws<IOException>(() => TexturePackingFiles.InstallBatch(new[] { pending }));
                    CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(destination));
                }
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void ExternalJunctionIntoAssetsIsClassifiedByItsTarget()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows junction fixture.");
            string name = "MeshLabAlias-" + Guid.NewGuid().ToString("N");
            string assetFolder = "Assets/" + name;
            string alias = Path.Combine(Path.GetTempPath(), name);
            AssetDatabase.CreateFolder("Assets", name);
            try
            {
                string target = Path.GetFullPath(assetFolder);
                string command = "New-Item -ItemType Junction -Path '" + alias.Replace("'", "''") +
                    "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
                var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
                {
                    Arguments = "-NoProfile -NonInteractive -Command \"" + command + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    Assert.IsTrue(process.WaitForExit(10000));
                    Assert.AreEqual(0, process.ExitCode);
                }
                string destination = Path.Combine(alias, "output.tga");
                var method = typeof(TextureChannelPackerWindow).GetMethod("AssetPathWithComparison", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.AreEqual(assetFolder + "/output.tga", method.Invoke(null, new object[] { destination, StringComparison.OrdinalIgnoreCase }));
                Directory.CreateDirectory(Path.Combine(target, "Ignored~"));
                var validate = typeof(TextureChannelPackerWindow).GetMethod("ValidateAssetDestination", BindingFlags.NonPublic | BindingFlags.Static);
                var exception = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null,
                    new object[] { Path.Combine(alias, "Ignored~", "output.tga") }));
                Assert.IsInstanceOf<InvalidOperationException>(exception.InnerException);
            }
            finally
            {
                if (Directory.Exists(alias)) Directory.Delete(alias); // Delete the junction itself, without recursion.
                AssetDatabase.DeleteAsset(assetFolder);
            }
        }

        [UnityTest]
        public IEnumerator FailedBackgroundInstallationDoesNotStrandImportQueue()
        {
            var queue = typeof(TextureChannelPackerWindow).Assembly.GetType("SashaRX.UnityMeshLab.TexturePackingImports");
            var input = queue.GetNestedType("Input", BindingFlags.NonPublic);
            var empty = Array.CreateInstance(input, 0);
            var schedule = queue.GetMethod("Schedule", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { empty.GetType(), typeof(Action) }, null);
            LogAssert.Expect(LogType.Error, "[MeshLab][General] Texture packer installation: Expected installation failure");
            var task = (Task)schedule.Invoke(null, new object[] { empty, (Action)(() => throw new IOException("Expected installation failure")) });
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!task.IsCompleted)
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline);
                yield return null;
            }
            Assert.IsTrue(task.IsFaulted);
            Assert.IsInstanceOf<IOException>(task.Exception.InnerException);
            bool ran = false;
            var next = (Task)schedule.Invoke(null, new object[] { empty, (Action)(() => ran = true) });
            while (!next.IsCompleted)
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline);
                yield return null;
            }
            Assert.IsTrue(ran);
            Assert.IsFalse(next.IsFaulted);
        }

        private sealed class CountingStream : MemoryStream
        {
            public int BytesRead { get; private set; }
            public CountingStream(byte[] bytes) : base(bytes, false) { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = base.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }
            public override int ReadByte()
            {
                int value = base.ReadByte();
                if (value >= 0) BytesRead++;
                return value;
            }
        }

        [Test]
        public void StreamDecodeReadsOnlyPixelsAndLeavesCallerStreamOpen()
        {
            byte[] bytes;
            using (var encoded = new MemoryStream())
            {
                new TexturePackingImage(1, 1, new[] { new Color32(10, 20, 30, 40) }).WriteTgaRle(encoded);
                encoded.Write(new byte[65536], 0, 65536);
                bytes = encoded.ToArray();
            }
            using (var input = new CountingStream(bytes))
            {
                var image = TexturePackingImage.DecodeTga(input);
                Assert.AreEqual(new Color32(10, 20, 30, 40), image.Pixels[0]);
                Assert.AreEqual(23, input.BytesRead);
                Assert.IsTrue(input.CanRead);
            }
        }

        [UnityTest]
        public IEnumerator PngDecodeDisablesMipmapsAndDetectsAtomicSourceReplacement()
        {
            string path = Path.Combine(Path.GetTempPath(), "MeshLabSnapshot-" + Guid.NewGuid().ToString("N") + ".png");
            string replacement = path + ".replacement";
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                texture.SetPixels32(new[] { new Color32(1, 2, 3, 4), new Color32(5, 6, 7, 8),
                    new Color32(9, 10, 11, 12), new Color32(13, 14, 15, 16) });
                texture.Apply();
                byte[] encoded = ImageConversion.EncodeToPNG(texture);
                File.WriteAllBytes(path, encoded);
                using (var load = new TexturePackingLoad(path))
                {
                    double deadline = EditorApplication.timeSinceStartup + 30;
                    TexturePackingImage image;
                    while (!load.TryComplete(out image))
                    {
                        Assert.Less(EditorApplication.timeSinceStartup, deadline);
                        yield return null;
                    }
                    var decoded = (Texture2D)typeof(TexturePackingLoad).GetField("texture", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(load);
                    Assert.AreEqual(1, decoded.mipmapCount);
                }
                using (var load = new TexturePackingLoad(path))
                {
                    var acquiring = (Task)typeof(TexturePackingLoad).GetField("leaseTask", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(load);
                    double deadline = EditorApplication.timeSinceStartup + 30;
                    while (!acquiring.IsCompleted)
                    {
                        Assert.Less(EditorApplication.timeSinceStartup, deadline);
                        yield return null;
                    }
                    acquiring.GetAwaiter().GetResult();
                    if (Path.DirectorySeparatorChar == '\\')
                        Assert.Throws<IOException>(() => File.WriteAllBytes(path, encoded));
                    File.WriteAllBytes(replacement, encoded);
                    File.Replace(replacement, path, null);
                    Exception failure = null;
                    while (failure == null)
                    {
                        try { if (load.TryComplete(out _)) Assert.Fail("A replaced source must be rejected."); }
                        catch (IOException exception) { failure = exception; }
                        Assert.Less(EditorApplication.timeSinceStartup, deadline);
                        if (failure == null) yield return null;
                    }
                    StringAssert.Contains("Source", failure.Message);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                File.Delete(path);
                File.Delete(replacement);
            }
        }
    }
}
