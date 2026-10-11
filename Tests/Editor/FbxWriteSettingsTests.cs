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

        [TestCase(false)]
        [TestCase(true)]
        public void BinaryMediaDiscoverySkipsLargeGeometryPayloads(bool wide)
        {
            string path = Path.Combine(folder, "large-binary.fbx");
            using (var file = File.Create(path))
                WriteLargeBinaryMediaFixture(file, wide, 20 * 1024 * 1024);
            using var stream = new BoundedReadStream(File.OpenRead(path));
            AssertBinaryMediaDiscovery(stream);
        }

        [Test]
        public void BinaryMediaDiscoveryUses64BitOffsetsBeyondTwoGiB()
        {
            using var fixture = new VirtualFixtureStream();
            WriteLargeBinaryMediaFixture(fixture, true, 3L * 1024 * 1024 * 1024);
            using var stream = new BoundedReadStream(fixture);
            AssertBinaryMediaDiscovery(stream);
            Assert.Greater(stream.Length, int.MaxValue);
        }

        static void WriteLargeBinaryMediaFixture(Stream file, bool wide, long payload)
        {
            using (var writer = new System.IO.BinaryWriter(file, System.Text.Encoding.UTF8, true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0"));
                writer.Write(wide ? 7500 : 7400);
                long objects = BeginMediaFixtureNode(writer, "Objects", wide);
                long geometry = BeginMediaFixtureNode(writer, "Geometry", wide, payload);
                file.Position += payload;
                FinishMediaFixtureNode(writer, geometry, wide);
                long texture = BeginMediaFixtureNode(writer, "Texture", wide);
                WriteMediaFixtureProperty(writer, "FileName", "authoring/map.png", wide);
                WriteMediaFixtureProperty(writer, "RelativeFilename", "maps/map.png", wide);
                FinishMediaFixtureNode(writer, texture, wide);
                long video = BeginMediaFixtureNode(writer, "Video", wide);
                WriteMediaFixtureProperty(writer, "Filename", "authoring/video.png", wide);
                WriteMediaFixtureProperty(writer, "RelativeFilename", "maps/video.png", wide);
                FinishMediaFixtureNode(writer, video, wide);
                FinishMediaFixtureNode(writer, objects, wide);
            }
        }

        static long BeginMediaFixtureNode(System.IO.BinaryWriter writer, string name, bool wide, long propertiesLength = 0)
        {
            long start = writer.BaseStream.Position;
            if (wide) { writer.Write(0L); writer.Write(propertiesLength == 0 ? 0L : 1L); writer.Write(propertiesLength); }
            else { writer.Write(0U); writer.Write(propertiesLength == 0 ? 0U : 1U); writer.Write((uint)propertiesLength); }
            writer.Write((byte)name.Length); writer.Write(System.Text.Encoding.ASCII.GetBytes(name));
            return start;
        }

        static void FinishMediaFixtureNode(System.IO.BinaryWriter writer, long start, bool wide)
        {
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = start;
            if (wide) writer.Write(end); else writer.Write((uint)end);
            writer.BaseStream.Position = end;
        }

        static void WriteMediaFixtureProperty(System.IO.BinaryWriter writer, string name, string value, bool wide)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            long start = BeginMediaFixtureNode(writer, name, wide, 5 + bytes.Length);
            writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes);
            FinishMediaFixtureNode(writer, start, wide);
        }

        [Test]
        public void AsciiMediaDiscoveryStreamsLongGeometryAndQuotedBraces()
        {
            string path = Path.Combine(folder, "large-ascii.fbx");
            using (var writer = new StreamWriter(path))
            {
                writer.Write("Objects: {\nGeometry: 1, \"Geometry::mesh {quoted}\", \"Mesh\" {\nVertices: *10000000 {\na: ");
                string chunk = new string('7', 4096);
                for (int i = 0; i < 5120; i++) writer.Write(chunk);
                writer.Write("\n}\n}\n; Comment with ignored braces } {\nTexture: 2, \"Texture::map\", \"\" {\nFileName: \"authoring/map.png\"\nRelativeFilename: \"maps/map.png\"\n}\n");
                writer.Write("Video: 3, \"Video::map\", \"Clip\" {\nFilename: \"authoring/video.png\"\nRelativeFilename: \"maps/video.png\"\n}\n}\n");
            }
            AssertBoundedMediaDiscovery(path);
            using var reader = new StreamingOnlyFileReader(path);
            var references = new List<(string filename, string relative)>();
            FbxUvSetNames.ReadAsciiMedia(reader, references);
            Assert.AreEqual(2, references.Count);
        }

        static void AssertBoundedMediaDiscovery(string path)
        {
            using var stream = new BoundedReadStream(File.OpenRead(path));
            var references = FbxUvSetNames.ReadMediaReferences(stream);
            CollectionAssert.AreEqual(new[] { ("authoring/map.png", "maps/map.png"), ("authoring/video.png", "maps/video.png") }, references);
        }

        static void AssertBinaryMediaDiscovery(BoundedReadStream stream)
        {
            var references = FbxUvSetNames.ReadMediaReferences(stream);
            CollectionAssert.AreEqual(new[] { ("authoring/map.png", "maps/map.png"), ("authoring/video.png", "maps/video.png") }, references);
            Assert.Less(stream.BytesRead, 4096, "Binary geometry payloads must be skipped by their record offsets.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BinaryMediaDiscoveryReadsPathProperties(bool wide)
        {
            using var file = new MemoryStream();
            using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0"));
                writer.Write(wide ? 7500 : 7400);
                long objects = BeginMediaFixtureNode(writer, "Objects", wide);
                long video = BeginMediaFixtureNode(writer, "Video", wide);
                long properties = BeginMediaFixtureNode(writer, "Properties70", wide);
                WriteMediaFixturePath(writer, "Path", "authoring/map.png", wide);
                WriteMediaFixturePath(writer, "RelPath", "maps/map.png", wide);
                FinishMediaFixtureNode(writer, properties, wide);
                FinishMediaFixtureNode(writer, video, wide);
                FinishMediaFixtureNode(writer, objects, wide);
            }
            CollectionAssert.AreEqual(new[] { ("authoring/map.png", "maps/map.png") }, FbxUvSetNames.ReadMediaReferences(file));
        }

        static void WriteMediaFixturePath(BinaryWriter writer, string name, string value, bool wide)
        {
            string[] values = { name, "KString", "XRefUrl", "", value };
            long size = values.Sum(s => 5L + System.Text.Encoding.UTF8.GetByteCount(s));
            long start = BeginMediaFixtureNode(writer, "P", wide, size);
            foreach (string item in values)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(item);
                writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes);
            }
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = start + (wide ? 8 : 4);
            if (wide) writer.Write(5L); else writer.Write(5U);
            writer.BaseStream.Position = end;
            FinishMediaFixtureNode(writer, start, wide);
        }

        [Test]
        public void AsciiMediaDiscoveryReadsLongPathProperties()
        {
            string path = "authoring/" + new string('x', 600) + "/map.png";
            string text = "Objects: {\nVideo: 1, \"Video::map\", \"Clip\" {\nProperties70: {\n"
                + "P: \"Path\", \"KString\", \"XRefUrl\", \"\", \"" + path + "\"\n"
                + "P: \"RelPath\", \"KString\", \"XRefUrl\", \"\", \"maps/map.png\"\n}\n}\n}\n";
            using var file = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            CollectionAssert.AreEqual(new[] { (path, "maps/map.png") }, FbxUvSetNames.ReadMediaReferences(file));
        }

        sealed class VirtualFixtureStream : Stream
        {
            readonly Dictionary<long, byte[]> records = new Dictionary<long, byte[]>();
            long length;
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => true;
            public override long Length => length;
            public override long Position { get; set; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = (int)Math.Min(count, Math.Max(0, length - Position));
                Array.Clear(buffer, offset, read);
                foreach (var record in records)
                {
                    long start = Math.Max(Position, record.Key), end = Math.Min(Position + read, record.Key + record.Value.Length);
                    if (end > start) Array.Copy(record.Value, (int)(start - record.Key), buffer, offset + (int)(start - Position), (int)(end - start));
                }
                Position += read; return read;
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                var copy = new byte[count]; Array.Copy(buffer, offset, copy, 0, count);
                records[Position] = copy; Position += count; length = Math.Max(length, Position);
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : length) + offset;
                return Position;
            }
            public override void Flush() { }
            public override void SetLength(long value) { length = value; }
        }

        sealed class StreamingOnlyFileReader : StreamReader
        {
            internal StreamingOnlyFileReader(string path) : base(path) { }
            public override string ReadLine() => throw new InvalidOperationException("Giant geometry lines must not be read as strings.");
            public override string ReadToEnd() => throw new InvalidOperationException("The entire source must not be read as a string.");
        }

        sealed class BoundedReadStream : Stream
        {
            readonly Stream source;
            internal long BytesRead;
            internal BoundedReadStream(Stream source) { this.source = source; }
            public override bool CanRead => true;
            public override bool CanWrite => false;
            public override bool CanSeek => true;
            public override long Length => source.Length;
            public override long Position { get => source.Position; set => source.Position = value; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                Assert.LessOrEqual(count, 4096, "Read requests must stay bounded regardless of source length.");
                int read = source.Read(buffer, offset, count); BytesRead += read; return read;
            }
            public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [Test]
        public void MissingForeignAbsoluteMediaReferencesRemainAuthored()
        {
            string source = Path.Combine(folder, "source"), scratch = Path.Combine(folder, "scratch");
            string target = Path.Combine(folder, "variant", "output.fbx");
            Directory.CreateDirectory(source); Directory.CreateDirectory(scratch);
            string[] foreign = Path.DirectorySeparatorChar == '/' ? new[] { @"C:\maps\albedo.png", "D:/maps/albedo.png", @"\\server\share\albedo.png", "//server/share/albedo.png" }
                : new[] { "/Volumes/maps/albedo.png", "/home/author/maps/albedo.png" };
            var originalMethod = typeof(FbxExternalMedia).GetMethod("SourcePaths", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var resolveMethod = typeof(FbxExternalMedia).GetMethod("ResolveBinaryPaths", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            foreach (string authored in foreign)
            foreach (var pair in new[] { (authored, (string)null), ((string)null, authored) })
            {
                var references = new List<(string, string)> { pair };
                var original = (Dictionary<string, string>)originalMethod.Invoke(null, new object[] { references, source });
                var resolved = (IReadOnlyDictionary<string, string>)resolveMethod.Invoke(null,
                    new object[] { pair, scratch, source, new Dictionary<string, string>(), original, false });
                foreach (string field in new[] { "FileName", "RelativeFilename", "Path", "RelPath" })
                {
                    string prefix = field == "Path" || field == "RelPath" ? "P: \"" + field + "\", \"KString\", \"XRefUrl\", \"\", \"" : field + ": \"";
                    string line = prefix + authored + "\"";
                    Assert.AreEqual(line, FbxExternalMedia.RewriteFilename(line, resolved, scratch, target));
                }
                var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => resolveMethod.Invoke(null,
                    new object[] { pair, scratch, source, new Dictionary<string, string>(), original, true }));
                Assert.IsInstanceOf<IOException>(exception.InnerException);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BinaryForeignMediaRewriteKeepsTheAuthoredRoot(bool wide)
        {
            string input = Path.Combine(folder, "foreign-stage.fbx"), output = Path.Combine(folder, "foreign-rewrite.fbx");
            using (var file = File.Create(input)) WriteLargeBinaryMediaFixture(file, wide, 0);
            string foreign = Path.DirectorySeparatorChar == '/' ? @"C:\maps\albedo.png" : "/Volumes/maps/albedo.png";
            var paths = new List<IReadOnlyDictionary<string, string>>();
            foreach (string name in new[] { "map", "video" })
                paths.Add(new Dictionary<string, string> {
                    [Path.Combine(folder, "authoring", name + ".png")] = foreign,
                    [Path.Combine(folder, "maps", name + ".png")] = foreign });
            using (var source = File.OpenRead(input))
            using (var saved = File.Create(output)) FbxExternalMedia.RewriteBinary(source, saved, paths, folder, output);
            foreach (var reference in FbxUvSetNames.ReadMediaReferences(output))
            {
                Assert.AreEqual(foreign, reference.filename);
                Assert.AreEqual(foreign, reference.relative);
            }
        }

        [Test]
        public void SdkSaveKeepsAnUnavailableForeignAbsoluteTextureReference()
        {
            string source = Source(false, external: true);
            string foreign = Path.DirectorySeparatorChar == '/' ? @"C:\maps\albedo.png" : "/Volumes/maps/albedo.png";
            string quoted = "\"" + foreign + "\"";
            string text = Regex.Replace(File.ReadAllText(source), "\"[^\"]*map\\.png\"", _ => quoted);
            File.WriteAllText(source, text);
            var before = FbxUvSetNames.ReadMediaReferences(source);
            Assert.IsNotEmpty(before);
            foreach (var reference in before) Assert.AreEqual(foreign, reference.filename);
            string destination = Path.Combine(folder, "variant"); Directory.CreateDirectory(destination);
            string output = Path.Combine(destination, "foreign-output.fbx");
            using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
            var after = FbxUvSetNames.ReadMediaReferences(output);
            Assert.IsNotEmpty(after);
            foreach (var reference in after) Assert.AreEqual(foreign, reference.filename);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BinaryFilenameRewriteStreamsOpaqueGeometryAndUpdatesOffsets(bool wide)
        {
            const int payload = 20 * 1024 * 1024;
            string input = Path.Combine(folder, "stage.fbx"), output = Path.Combine(folder, "rewrite.fbx");
            long geometryStart = 27 + 2 * (wide ? 25 : 13) + "Objects".Length + "Geometry".Length;
            using (var file = File.Create(input))
            {
                WriteLargeBinaryMediaFixture(file, wide, payload);
                file.Position = geometryStart + payload - 20;
                file.Write(new byte[] { 3, 5, 7, 11 }, 0, 4);
            }
            var paths = new List<IReadOnlyDictionary<string, string>>();
            var expected = new List<(string, string)>();
            foreach (string name in new[] { "map", "video" })
            {
                string destination = Path.Combine(folder, "durable", name + "-a-longer-filename.png");
                paths.Add(new Dictionary<string, string> {
                    [Path.Combine(folder, "authoring", name + ".png")] = destination,
                    [Path.Combine(folder, "maps", name + ".png")] = destination });
                expected.Add((destination.Replace('\\', '/'), Path.GetRelativePath(folder, destination).Replace('\\', '/')));
            }
            using (var source = new BoundedReadStream(File.OpenRead(input)))
            using (var saved = File.Create(output)) FbxExternalMedia.RewriteBinary(source, saved, paths, folder, output);
            CollectionAssert.AreEqual(expected, FbxUvSetNames.ReadMediaReferences(output));
            CollectionAssert.AreEqual(PayloadHash(input, geometryStart, payload), PayloadHash(output, geometryStart, payload));
        }

        static byte[] PayloadHash(string path, long offset, int length)
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path); stream.Position = offset;
            var buffer = new byte[4096];
            while (length > 0)
            {
                int read = stream.Read(buffer, 0, Math.Min(length, buffer.Length));
                Assert.Greater(read, 0); hash.TransformBlock(buffer, 0, read, buffer, 0); length -= read;
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return hash.Hash;
        }

        sealed class StreamingOnlyReader : StringReader
        {
            internal StreamingOnlyReader(string value) : base(value) { }
            public override string ReadLine() => throw new InvalidOperationException("Whole geometry lines must not be allocated.");
            public override string ReadToEnd() => throw new InvalidOperationException("The staged document must not be loaded as a whole.");
        }

        [Test]
        public void StagingStreamsOpaqueGeometryAndRewritesPairedMediaRecords()
        {
            string map = Path.Combine(folder, "map.png"), target = Path.Combine(folder, "variant.fbx");
            File.WriteAllText(map, "fixture");
            string stored = map.Replace('\\', '/');
            string opaque = "\t\ta: " + new string('7', 1024 * 1024) + " café\r\n";
            string block = "\tVideo: 1, \"Video::map\", \"Clip\" {\r\n" +
                "\t\tFileName: \"missing.png\"\r\n\t\tRelativeFilename: \"map.png\"\r\n" +
                "\t\tProperties70: {\r\n\t\t\tP: \"Path\", \"KString\", \"XRefUrl\", \"\", \"missing.png\"\r\n\t\t}\r\n" +
                opaque + "\t}\r\n";
            string expected = block.Replace("missing.png", stored);
            using var reader = new StreamingOnlyReader(opaque + block + opaque);
            using var writer = new StringWriter();
            var empty = new Dictionary<string, string>(StringComparer.Ordinal);
            FbxExternalMedia.RewriteAscii(reader, writer, folder, folder, target, empty, empty, true);
            Assert.AreEqual(opaque + expected + opaque, writer.ToString());
            Assert.IsFalse(File.Exists(Path.Combine(folder, "media-block.tmp")));
        }

        [Test]
        public void MissingMediaRefusalRemovesTheStagingSpool()
        {
            using var reader = new StreamingOnlyReader("\tVideo: 1, \"Video::map\", \"Clip\" {\n\t\tFileName: \"missing.png\"\n\t}\n");
            using var writer = new StringWriter();
            var empty = new Dictionary<string, string>(StringComparer.Ordinal);
            Assert.Throws<IOException>(() => FbxExternalMedia.RewriteAscii(reader, writer, folder, folder,
                Path.Combine(folder, "output.fbx"), empty, empty, true));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "media-block.tmp")));
        }

        string Source(bool binary, bool embedded = false, bool external = false, string mediaName = "map.png")
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
                try { File.WriteAllBytes(Path.Combine(folder, mediaName), texture.EncodeToPNG()); }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
                node.AddMaterial(FbxStructureEdit.NewMaterial(scene, "M_Quad", 1, 1, 1, "T_Quad_Albedo",
                    Path.Combine(folder, external ? "missing/" + mediaName : mediaName), mediaName, null));
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

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void MediaSaveKeepsGeometryDoublesBitForBit(bool embedded, bool probe)
        {
            string source = Source(true, embedded: embedded, external: !embedded);
            string output = Path.Combine(folder, "exact.fbx");
            using var document = FbxSourceDocument.Load(source);
            var mesh = document.Meshes[0];
            double precise = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(.234567890123456) + 1);
            mesh.SetControlPointAt(new FbxVector4(precise, precise, precise), 0);
            var topology = new FbxLayerChannels.Topology(mesh);
            FbxLayerChannels.WriteUv(mesh, 0, topology, Enumerable.Repeat(precise, 8).ToArray(), Enumerable.Repeat(true, 4).ToArray());
            FbxLayerChannels.WriteColor(mesh, topology, Enumerable.Repeat(precise, 16).ToArray(), Enumerable.Repeat(true, 4).ToArray());
            long[] before = GeometryBits(mesh);
            if (probe) document.SaveForImport(output); else document.SaveAtomic(output);
            using var saved = FbxSourceDocument.Load(output);
            CollectionAssert.AreEqual(before, GeometryBits(saved.Meshes[0]));
            Assert.AreEqual(embedded && probe, saved.EmbedsMedia);
        }

        static long[] GeometryBits(FbxMesh mesh)
        {
            var values = new List<double>();
            for (int i = 0; i < mesh.GetControlPointsCount(); i++)
            {
                var point = mesh.GetControlPointAt(i); values.Add(point.X); values.Add(point.Y); values.Add(point.Z);
            }
            var topology = new FbxLayerChannels.Topology(mesh);
            foreach (var element in FbxLayerChannels.UvElements(mesh)) values.AddRange(FbxLayerChannels.ReadUv(element, topology));
            values.AddRange(FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(mesh), topology));
            return values.Select(BitConverter.DoubleToInt64Bits).ToArray();
        }

        [Test]
        public void TemporaryProbeKeepsAvailableEmbeddedMediaDespiteStaleExternalMap()
        {
            string source = Source(true, embedded: true), mixed = Path.Combine(folder, "mixed.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                document.Scene.GetRootNode().GetChild(0).AddMaterial(FbxStructureEdit.NewMaterial(document.Scene,
                    "M_Missing", 1, 1, 1, "T_Missing", Path.Combine(folder, "missing.png"), "missing.png", null));
                document.Manager.GetIOSettings().SetBoolProp(Globals.EXP_FBX_EMBEDDED, true);
                document.ExportScene(document.Scene, mixed, new FbxWriteSettings(true, true).WriterFormat(document.Manager), document.Manager.GetIOSettings());
            }
            File.Delete(Path.Combine(folder, "map.png"));
            using var loaded = FbxSourceDocument.Load(mixed);
            Assert.IsTrue(loaded.EmbedsMedia);
            string probe = Path.Combine(folder, "probe.fbx");
            Assert.DoesNotThrow(() => loaded.SaveForImport(probe));
            using (var saved = FbxSourceDocument.Load(probe)) Assert.IsTrue(saved.EmbedsMedia);
            byte[] original = { 2, 3, 5 }; string output = Path.Combine(folder, "unchanged.fbx"); File.WriteAllBytes(output, original);
            MeshLabProjectSettings.Instance.embedFbxTextures = true;
            Assert.Throws<IOException>(() => loaded.SaveAtomic(output), "explicit final embedding still refuses a missing map");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExternalSourceVariantKeepsOriginalMediaAfterScratchDisposal(bool binary)
        {
            string source = Source(binary, external: true), originalMap = Path.Combine(folder, "map.png");
            byte[] bytes = File.ReadAllBytes(originalMap);
            string relocated = Path.Combine(folder, "relocated"); Directory.CreateDirectory(Path.Combine(relocated, "missing"));
            string map = Path.Combine(relocated, "missing", "map.png"); File.WriteAllBytes(map, bytes);
            string relocatedSource = Path.Combine(relocated, "source.fbx"); File.Move(source, relocatedSource); File.Delete(originalMap);
            string destination = Path.Combine(folder, "variant"); Directory.CreateDirectory(destination);
            string output = Path.Combine(destination, "output.fbx");
            using (var document = FbxSourceDocument.Load(relocatedSource)) document.SaveAtomic(output);
            var references = FbxUvSetNames.ReadMediaReferences(output);
            Assert.IsNotEmpty(references);
            foreach (var reference in references)
            {
                if (!string.IsNullOrEmpty(reference.filename))
                    Assert.AreEqual(Path.GetFullPath(map), Path.GetFullPath(reference.filename.Replace('/', Path.DirectorySeparatorChar)));
                if (!string.IsNullOrEmpty(reference.relative))
                    Assert.AreEqual(Path.GetFullPath(map), Path.GetFullPath(Path.Combine(destination, reference.relative.Replace('/', Path.DirectorySeparatorChar))));
            }
            using var saved = FbxSourceDocument.Load(output);
            Assert.IsFalse(saved.EmbedsMedia); Assert.AreEqual(4, saved.Meshes[0].GetPolygonSize(0));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(map));
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

        [TestCase(false)]
        [TestCase(true)]
        public void MissingExternalVariantKeepsReferenceToOriginalDirectory(bool binary)
        {
            string source = Source(binary, external: true), originalMap = Path.Combine(folder, "map.png");
            byte[] bytes = File.ReadAllBytes(originalMap);
            string relocated = Path.Combine(folder, "relocated"); Directory.CreateDirectory(relocated);
            string moved = Path.Combine(relocated, "source.fbx"); File.Move(source, moved); File.Delete(originalMap);
            string map = Path.Combine(relocated, "missing", "map.png");
            string destination = Path.Combine(folder, "variant"); Directory.CreateDirectory(destination);
            string output = Path.Combine(destination, "output.fbx");
            using (var document = FbxSourceDocument.Load(moved)) document.SaveAtomic(output);
            Assert.IsFalse(File.Exists(map), "the texture is absent during save-as");
            var references = FbxUvSetNames.ReadMediaReferences(output);
            Assert.IsNotEmpty(references);
            foreach (var reference in references)
            {
                if (!string.IsNullOrEmpty(reference.filename))
                    Assert.AreEqual(Path.GetFullPath(map), Path.GetFullPath(reference.filename.Replace('/', Path.DirectorySeparatorChar)));
                if (!string.IsNullOrEmpty(reference.relative))
                    Assert.AreEqual(Path.GetFullPath(map), Path.GetFullPath(Path.Combine(destination, reference.relative.Replace('/', Path.DirectorySeparatorChar))));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(map)); File.WriteAllBytes(map, bytes);
            foreach (var reference in references)
                if (!string.IsNullOrEmpty(reference.filename)) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(reference.filename));
        }

        [Test]
        public void EmbeddedActiveFileIsRefusedBeforeExternalization()
        {
            string payload = Path.Combine(folder, "map.cs"), embedded = Path.Combine(folder, "active.fbx");
            File.WriteAllText(payload, "// inert test fixture; no executable declarations\n");
            using (var document = FbxSourceDocument.Load(Source(true)))
            {
                document.Scene.GetRootNode().GetChild(0).AddMaterial(FbxStructureEdit.NewMaterial(document.Scene,
                    "M_Active", 1, 1, 1, "T_Active", payload, "map.cs", null));
                document.Manager.GetIOSettings().SetBoolProp(Globals.EXP_FBX_EMBEDDED, true);
                document.ExportScene(document.Scene, embedded, new FbxWriteSettings(true, true).WriterFormat(document.Manager), document.Manager.GetIOSettings());
            }
            File.Delete(payload);
            using var loaded = FbxSourceDocument.Load(embedded);
            Assert.IsTrue(loaded.EmbedsMedia, "SDK accepts an inert .cs file as embedded texture content");
            string output = Path.Combine(folder, "output.fbx"); byte[] before = { 2, 3, 5 }; File.WriteAllBytes(output, before);
            Assert.Throws<IOException>(() => loaded.SaveAtomic(output));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(output));
            Assert.IsFalse(Directory.Exists(Path.Combine(folder, "output.fbm")), "no importable media is created");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PathOnlyExternalMediaSurvivesVariantSave(bool embed)
        {
            string source = Source(false, external: true);
            string text = File.ReadAllText(source);
            StringAssert.Contains("P: \"Path\"", text);
            text = Regex.Replace(text, "(?m)^\\s*(?:FileName|Filename|RelativeFilename):[^\\r\\n]*\\r?\\n", "");
            File.WriteAllText(source, text);
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, "map.png"));
            string relocated = Path.Combine(folder, "relocated"); Directory.CreateDirectory(Path.Combine(relocated, "missing"));
            string map = Path.Combine(relocated, "missing/map.png"); File.WriteAllBytes(map, bytes);
            string moved = Path.Combine(relocated, "source.fbx"); File.Move(source, moved); File.Delete(Path.Combine(folder, "map.png"));
            string destination = Path.Combine(folder, "variant"); Directory.CreateDirectory(destination);
            string output = Path.Combine(destination, "output.fbx");
            MeshLabProjectSettings.Instance.embedFbxTextures = embed;
            using (var document = FbxSourceDocument.Load(moved)) document.SaveAtomic(output);
            if (embed)
            {
                File.Delete(map);
                using var saved = FbxSourceDocument.Load(output);
                Assert.IsTrue(saved.EmbedsMedia);
                MeshLabProjectSettings.Instance.embedFbxTextures = false;
                saved.Save(Path.Combine(destination, "extracted.fbx"));
                string[] maps = Directory.GetFiles(Path.Combine(destination, "extracted.fbm"), "*.png", SearchOption.AllDirectories);
                Assert.AreEqual(1, maps.Length);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(maps[0]));
            }
            else
            {
                var references = FbxUvSetNames.ReadMediaReferences(output);
                Assert.IsTrue(references.Any(r => !string.IsNullOrEmpty(r.filename)), "saved source must still reference its map");
                foreach (var reference in references.Where(r => !string.IsNullOrEmpty(r.filename)))
                    Assert.AreEqual(Path.GetFullPath(map), Path.GetFullPath(reference.filename.Replace('/', Path.DirectorySeparatorChar)));
                using var saved = FbxSourceDocument.Load(output);
                Assert.AreEqual(4, saved.Meshes[0].GetPolygonSize(0));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MediaImportFailureLeavesTheExistingFbxAndMetadataUntouched(bool atomic)
        {
            string source = Source(true, embedded: true);
            string name = "MeshLabMediaFailure_" + Guid.NewGuid().ToString("N"), assets = "Assets/" + name;
            UnityEditor.AssetDatabase.CreateFolder("Assets", name);
            try
            {
                string output = Path.GetFullPath(assets + "/output.fbx");
                byte[] original = File.ReadAllBytes(source); File.WriteAllBytes(output, original);
                UnityEditor.AssetDatabase.ImportAsset(assets + "/output.fbx", UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                byte[] metadata = File.ReadAllBytes(output + ".meta");
                using var document = FbxSourceDocument.Load(source);
                var method = typeof(FbxSourceDocument).GetMethod("SaveWithSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                bool imported = false;
                Action<IEnumerable<string>> failingImport = files =>
                {
                    FbxExternalMedia.ImportMedia(files); imported = true;
                    throw new IOException("Injected texture import failure after the import batch.");
                };
                var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(document,
                    new object[] { output, FbxWriteSettings.FromProject(), atomic, failingImport, null }));
                Assert.IsInstanceOf<IOException>(exception.InnerException);
                Assert.IsTrue(imported);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
                CollectionAssert.AreEqual(metadata, File.ReadAllBytes(output + ".meta"));
                Assert.IsFalse(Directory.Exists(Path.GetFullPath(assets + "/output.fbm")), "new maps and their metadata must roll back");
                Assert.IsFalse(File.Exists(output + ".tmp"));
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(assets); }
        }

        [Test]
        [Platform("Win")]
        public void MediaRollbackContinuesAfterLockedFileAndRetainsTheSaveError()
        {
            string source = Source(true, embedded: true), mixed = Path.Combine(folder, "two-maps.fbx");
            string second = Path.Combine(folder, "second.png");
            var texture = new Texture2D(2, 2);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.red, 4).ToArray()); texture.Apply();
                File.WriteAllBytes(second, texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            using (var document = FbxSourceDocument.Load(source))
            {
                document.Scene.GetRootNode().GetChild(0).AddMaterial(FbxStructureEdit.NewMaterial(document.Scene,
                    "M_Second", 1, 1, 1, "T_Second", second, "second.png", null));
                document.Manager.GetIOSettings().SetBoolProp(Globals.EXP_FBX_EMBEDDED, true);
                document.ExportScene(document.Scene, mixed, new FbxWriteSettings(true, true).WriterFormat(document.Manager), document.Manager.GetIOSettings());
            }
            string output = Path.Combine(folder, "rollback.fbx"), metadata = output + ".meta";
            byte[] original = File.ReadAllBytes(source); File.WriteAllBytes(output, original);
            File.WriteAllText(metadata, "original metadata");
            string[] maps = null;
            FileStream locked = null;
            var originalError = new IOException("Injected save failure before the FBX commit.");
            try
            {
                using var document = FbxSourceDocument.Load(mixed);
                Action<IEnumerable<string>> failingImport = files =>
                {
                    maps = files.ToArray(); Assert.AreEqual(2, maps.Length);
                    Assert.AreNotEqual(Path.GetDirectoryName(maps[0]), Path.GetDirectoryName(maps[1]));
                    locked = new FileStream(maps[0], FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw originalError;
                };
                var method = typeof(FbxSourceDocument).GetMethod("SaveWithSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(document,
                    new object[] { output, FbxWriteSettings.FromProject(), true, failingImport, null }));
                Assert.IsInstanceOf<AggregateException>(exception.InnerException);
                var errors = ((AggregateException)exception.InnerException).InnerExceptions;
                Assert.AreSame(originalError, errors[0]);
                Assert.AreEqual(2, errors.Count, "retain the original save error and the actual locked-file deletion error");
                Assert.IsInstanceOf<IOException>(errors[1]);
                Assert.IsTrue(File.Exists(maps[0]), "the failed resource remains available for manual recovery");
                Assert.IsFalse(File.Exists(maps[1]), "a deletion error must not abort cleanup of subsequent resources");
                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(maps[1])), "later empty folders must also be removed");
                CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
                Assert.AreEqual("original metadata", File.ReadAllText(metadata));
            }
            finally { locked?.Dispose(); }
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

        [TestCase(false)]
        [TestCase(true)]
        public void ExistingExternalMapsAreImportedBeforeTheSavedModel(bool embed)
        {
            string source = Source(true);
            string name = "MeshLabExternalImport_" + Guid.NewGuid().ToString("N"), assets = "Assets/" + name;
            UnityEditor.AssetDatabase.CreateFolder("Assets", name);
            UnityEditor.AssetDatabase.DisallowAutoRefresh();
            try
            {
                string map = assets + "/external.png", authored = Path.Combine(folder, "authored.fbx");
                byte[] bytes;
                var texture = new Texture2D(2, 2);
                try { bytes = texture.EncodeToPNG(); }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
                File.WriteAllBytes(map, bytes);
                using (var document = FbxSourceDocument.Load(source))
                {
                    document.Scene.GetRootNode().GetChild(0).AddMaterial(FbxStructureEdit.NewMaterial(document.Scene,
                        "M_External", 1, 1, 1, "T_External", Path.GetFullPath(map), "external.png", null));
                    var io = document.Manager.GetIOSettings(); io.SetBoolProp(Globals.EXP_FBX_EMBEDDED, false);
                    document.ExportScene(document.Scene, authored, new FbxWriteSettings(true, false).WriterFormat(document.Manager), io);
                }
                Assert.IsNull(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(map));
                MeshLabProjectSettings.Instance.embedFbxTextures = embed;
                using (var document = FbxSourceDocument.Load(authored)) document.SaveAtomic(Path.GetFullPath(assets + "/output.fbx"));
                Assert.IsNotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(map), "existing project-local external maps need pre-model import too");
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(map), "authored maps remain outside rollback ownership");
                UnityEditor.AssetDatabase.ImportAsset(assets + "/output.fbx", UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                Assert.IsNotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assets + "/output.fbx"));
            }
            finally { UnityEditor.AssetDatabase.AllowAutoRefresh(); UnityEditor.AssetDatabase.DeleteAsset(assets); }
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
        [Platform("Win")]
        public void DeepOutputWithLongMediaNameBoundsTheCompletePath()
        {
            string source = Source(true, embedded: true, mediaName: new string('m', 40) + ".png");
            string directory = Path.Combine(folder, new string('d', 210 - folder.Length - 1));
            Directory.CreateDirectory(directory);
            string output = Path.Combine(directory, "output.fbx");
            using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
            string[] maps = Directory.GetFiles(Path.Combine(directory, "output.fbm"), "*.png", SearchOption.AllDirectories);
            Assert.AreEqual(1, maps.Length); Assert.Less(maps[0].Length, 250);
            foreach (var reference in FbxUvSetNames.ReadMediaReferences(output))
                if (!string.IsNullOrEmpty(reference.filename)) Assert.IsTrue(File.Exists(reference.filename));
        }

        [Test]
        public void ReusedMediaReferencesUseTheActualDirectoryEntryCase()
        {
            string source = Source(true, embedded: true), output = Path.Combine(folder, "output.fbx");
            using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
            string map = Directory.GetFiles(Path.Combine(folder, "output.fbm"), "*.png", SearchOption.AllDirectories).Single();
            string actual = Path.Combine(Path.GetDirectoryName(map), "MAP.PNG"), temporary = map + ".rename";
            File.Move(map, temporary); File.Move(temporary, actual);
            if (!File.Exists(map)) Assert.Ignore("Fixture requires an insensitive filesystem.");
            using (var document = FbxSourceDocument.Load(source)) document.SaveAtomic(output);
            foreach (var reference in FbxUvSetNames.ReadMediaReferences(output))
            {
                if (!string.IsNullOrEmpty(reference.filename)) StringAssert.EndsWith("MAP.PNG", reference.filename);
                if (!string.IsNullOrEmpty(reference.relative)) StringAssert.EndsWith("MAP.PNG", reference.relative);
            }
        }

        [Test]
        [Platform("Win")]
        public void UnfitMediaPathRefusesBeforeChangingTheOutput()
        {
            string source = Source(true, embedded: true);
            string directory = Path.Combine(folder, new string('d', 230 - folder.Length - 1));
            Directory.CreateDirectory(directory);
            string output = Path.Combine(directory, "output.fbx");
            byte[] original = File.ReadAllBytes(source);
            File.WriteAllBytes(output, original);
            using (var document = FbxSourceDocument.Load(source))
                StringAssert.Contains("too long", Assert.Throws<IOException>(() => document.SaveAtomic(output)).Message);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "output.fbm")));
        }

        [Test]
        public void LocalPackageRollbackDeletesImportedMediaAndItsMeta()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.sasharx.unitymeshlab");
            Assert.IsNotNull(package);
            if (package.source != UnityEditor.PackageManager.PackageSource.Local
                && package.source != UnityEditor.PackageManager.PackageSource.Embedded)
                Assert.Ignore("Rollback fixture needs a writable local or embedded package.");
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

        [Test]
        [Platform("Win")]
        public void OriginalMediaMappingKeepsDistinctCaseSensitiveWindowsFiles()
        {
            var info = new System.Diagnostics.ProcessStartInfo("fsutil.exe", "file setCaseSensitiveInfo \"" + folder + "\" enable") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = System.Diagnostics.Process.Start(info))
            {
                process.WaitForExit();
                if (process.ExitCode != 0) Assert.Ignore("Host cannot enable per-directory case sensitivity.");
            }
            string first = Path.Combine(folder, "A.png"), second = Path.Combine(folder, "a.png");
            File.WriteAllBytes(first, new byte[] { 1 }); File.WriteAllBytes(second, new byte[] { 2 });
            Assert.AreEqual(2, Directory.GetFiles(folder, "*.png").Length, "real case-sensitive Windows directory");
            using var document = FbxSourceDocument.Load(Source(false));
            string scratch = Path.Combine(folder, "mapping"); Directory.CreateDirectory(scratch);
            string staleFirst = Path.Combine(folder, "missing", "A.png"), staleSecond = Path.Combine(folder, "missing", "a.png");
            File.WriteAllText(Path.Combine(scratch, "input.fbx"), "Objects: {\n" +
                "\tTexture: 1, \"Texture::A\", \"\" {\n\t\tFileName: \"" + staleFirst.Replace('\\', '/') + "\"\n\t\tRelativeFilename: \"A.png\"\n\t}\n" +
                "\tTexture: 2, \"Texture::a\", \"\" {\n\t\tFileName: \"" + staleSecond.Replace('\\', '/') + "\"\n\t\tRelativeFilename: \"a.png\"\n\t}\n}\n");
            var method = typeof(FbxExternalMedia).GetMethod("SourcePaths", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var references = FbxUvSetNames.ReadMediaReferences(Path.Combine(scratch, "input.fbx"));
            var paths = (Dictionary<string, string>)method.Invoke(null, new object[] { references, document.SourceDirectory });
            Assert.AreEqual(2, paths.Count);
            Assert.AreEqual(first, paths[staleFirst]); Assert.AreEqual(second, paths[staleSecond]);
        }

        [Test]
        public void FilenameRewriteMatchesExistingFilesystemCaseAliases()
        {
            string map = Path.Combine(folder, "Map.png"), alias = Path.Combine(folder.ToUpperInvariant(), "MAP.PNG");
            File.WriteAllBytes(map, new byte[] { 1 });
            if (!File.Exists(alias)) Assert.Ignore("This filesystem has no case alias for the fixture.");
            string output = Path.Combine(folder, "out.png"), target = Path.Combine(folder, "output.fbx");
            var paths = new Dictionary<string, string>(StringComparer.Ordinal) { [map] = output };
            Assert.AreEqual("FileName: \"" + output.Replace('\\', '/') + "\"",
                FbxExternalMedia.RewriteFilename("FileName: \"" + alias.Replace('\\', '/') + "\"", paths, folder, target));
        }

        [Test]
        public void FilenameCaseAliasesWorkAcrossDeepDirectoryHierarchy()
        {
            string directory = folder;
            for (int i = 0; i < 50; i++) directory = Path.Combine(directory, "d");
            Directory.CreateDirectory(directory);
            string map = Path.Combine(directory, "Map.png"), alias = Path.Combine(directory.ToUpperInvariant(), "MAP.PNG");
            File.WriteAllBytes(map, new byte[] { 1 });
            if (!File.Exists(alias)) Assert.Ignore("This filesystem has no case alias for the fixture.");
            string output = Path.Combine(folder, "out.png"), target = Path.Combine(folder, "output.fbx");
            var paths = new Dictionary<string, string>(StringComparer.Ordinal) { [map] = output };
            Assert.AreEqual("FileName: \"" + output.Replace('\\', '/') + "\"",
                FbxExternalMedia.RewriteFilename("FileName: \"" + alias.Replace('\\', '/') + "\"", paths, folder, target));
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
