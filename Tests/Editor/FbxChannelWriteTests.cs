// FbxChannelWriteTests.cs — the strict FBX channel re-save: pairing FBX corners with
// Unity vertices (FbxCornerMatch), and, with the FBX SDK, editing one layer element
// of a real file while its polygons and other channels come through unchanged.
using System;
using System.Linq;
using NUnit.Framework;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System.IO;
using System.Collections.Generic;
using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public class FbxChannelWriteTests
    {
        static float[] Positions(params (float x, float y)[] points) => points.SelectMany(p => new[] { p.x, p.y, 0f }).ToArray();

        [Test]
        public void Match_PairsEveryCornerOfAWeldedTriangulatedQuad()
        {
            // Corners of one quad (X mirrored as Unity imports it); the result mesh has the
            // four welded vertices in another order and two triangles against the corner order.
            var corners = Positions((0, 0), (-1, 0), (-1, 1), (0, 1));
            var vertices = Positions((-1, 1), (0, 0), (0, 1), (-1, 0));
            int[] triangles = { 1, 0, 3, 1, 2, 0 };
            var result = FbxCornerMatch.Match(new[] { 4 }, corners, vertices, triangles, new[] { 3, 3 }, null, (a, b) => a == b);
            Assert.AreEqual(0, result.unresolved + result.conflicts + result.missing);
            CollectionAssert.AreEqual(new[] { 1, 3, 0, 2 }, result.cornerToVertex);
        }

        [Test]
        public void Match_RefusesAValueSeamInsideAPolygon()
        {
            // The quad's diagonal is a seam: corners 0 and 2 get different values from its two triangles.
            var corners = Positions((0, 0), (-1, 0), (-1, 1), (0, 1));
            var vertices = Positions((0, 0), (-1, 0), (-1, 1), (0, 1), (0, 0), (-1, 1));
            int[] triangles = { 0, 2, 1, 4, 3, 5 };
            float[] value = { 0, 1, 2, 3, 9, 8 };
            var result = FbxCornerMatch.Match(new[] { 4 }, corners, vertices, triangles, new[] { 3, 3 }, null, (a, b) => value[a] == value[b]);
            Assert.AreEqual(2, result.conflicts);
            Assert.AreEqual(-1, result.cornerToVertex[0]);
            Assert.AreEqual(-1, result.cornerToVertex[2]);
            Assert.AreEqual(1, result.cornerToVertex[1]);
            Assert.AreEqual(3, result.cornerToVertex[3]);
        }

        [Test]
        public void Match_SeparatesDoubleSidedFacesByWinding()
        {
            var corners = Positions((0, 0), (-1, 0), (-1, 1), (0, 0), (-1, 1), (-1, 0));
            var vertices = Positions((0, 0), (-1, 0), (-1, 1), (0, 0), (-1, 1), (-1, 0));
            int[] triangles = { 0, 2, 1, 3, 5, 4 };
            float[] value = { 1, 2, 3, 7, 8, 9 };
            var result = FbxCornerMatch.Match(new[] { 3, 3 }, corners, vertices, triangles, new[] { 3, 3 }, null, (a, b) => value[a] == value[b]);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, result.cornerToVertex);
        }

        [Test]
        public void Match_PairsAFanAroundAPole()
        {
            // 400 triangles sharing one centre vertex; the import welds the ring and reverses
            // the winding. Each polygon finds only its own face, not the 400 around the pole.
            const int n = 400;
            var ring = Enumerable.Range(0, n + 1).Select(i => ((float)Math.Cos(i * 0.01), (float)Math.Sin(i * 0.01))).ToArray();
            var corners = Enumerable.Range(0, n).SelectMany(i => Positions((0, 0), ring[i], ring[i + 1])).ToArray();
            var vertices = Positions(new[] { (0f, 0f) }.Concat(ring).ToArray());
            var triangles = Enumerable.Range(0, n).SelectMany(i => new[] { 0, i + 2, i + 1 }).ToArray();
            var result = FbxCornerMatch.Match(Enumerable.Repeat(3, n).ToArray(), corners, vertices, triangles,
                Enumerable.Repeat(3, n).ToArray(), null, (a, b) => a == b);
            Assert.AreEqual(0, result.unresolved + result.conflicts + result.missing);
            for (int i = 0; i < n; i++)
                CollectionAssert.AreEqual(new[] { 0, i + 1, i + 2 }, result.cornerToVertex.Skip(i * 3).Take(3).ToArray());
        }

        [Test]
        public void Match_CountsCornersTheImportDropped()
        {
            var corners = Positions((0, 0), (-1, 0), (-1, 1));
            corners[6] = float.NaN;
            var result = FbxCornerMatch.Match(new[] { 3 }, corners, Positions((0, 0), (-1, 0)), new int[0], new int[0], null, (a, b) => true);
            Assert.AreEqual(1, result.missing);
            Assert.AreEqual(2, result.unresolved);
            Assert.IsTrue(result.cornerToVertex.All(v => v == -1));
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        string folder;

        [SetUp]
        public void SetUp() => Directory.CreateDirectory(folder = Path.Combine(Path.GetTempPath(), "meshlab-channel-" + Guid.NewGuid().ToString("N")));

        [TearDown]
        public void TearDown() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        // A quad and a pentagon sharing an edge, UV0 per corner with a seam, colours per corner at full precision.
        string WriteSource(bool binary)
        {
            string path = Path.Combine(folder, "source.fbx");
            using var manager = FbxManager.Create();
            var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
            manager.SetIOSettings(io);
            var scene = FbxScene.Create(manager, "Source");
            var mesh = FbxMesh.Create(scene, "PartMesh");
            mesh.InitControlPoints(7);
            double[,] points = { { 0, 0 }, { 10, 0 }, { 10, 10 }, { 0, 10 }, { 20, 0 }, { 25, 8 }, { 18, 14 } };
            for (int i = 0; i < 7; i++) mesh.SetControlPointAt(new FbxVector4(points[i, 0], points[i, 1], 0), i);
            int[][] polygons = { new[] { 0, 1, 2, 3 }, new[] { 1, 4, 5, 6, 2 } };
            foreach (var polygon in polygons)
            {
                mesh.BeginPolygon();
                foreach (int i in polygon) mesh.AddPolygon(i);
                mesh.EndPolygon();
            }
            var node = FbxNode.Create(scene, "Part");
            node.SetNodeAttribute(mesh);
            scene.GetRootNode().AddChild(node);
            mesh.CreateLayer();
            var uv = FbxLayerElementUV.Create(mesh, "map1");
            uv.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
            uv.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            for (int i = 0; i < 7; i++) uv.GetDirectArray().Add(new FbxVector2(i * 0.1 + 1e-9, i * 0.05));
            uv.GetDirectArray().Add(new FbxVector2(0.987654321, 0.123456789));
            foreach (var (polygon, p) in polygons.Select((polygon, p) => (polygon, p)))
                foreach (int i in polygon) uv.GetIndexArray().Add(p == 1 && i == 2 ? 7 : i);
            mesh.GetLayer(0).SetUVs(uv, FbxLayerElement.EType.eTextureDiffuse);
            var colors = FbxLayerElementVertexColor.Create(mesh, "colorSet1");
            colors.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
            colors.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
            for (int c = 0; c < 9; c++) colors.GetDirectArray().Add(new FbxColor(0.123456789 + c * 0.01, 0.5, 0.25, 1));
            mesh.GetLayer(0).SetVertexColors(colors);
            using var exporter = FbxExporter.Create(manager, "Write");
            int format = manager.GetIOPluginRegistry().FindWriterIDByDescription(binary ? "FBX binary (*.fbx)" : "FBX ascii (*.fbx)");
            Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.SetFileExportVersion("FBX201400") && exporter.Export(scene));
            return path;
        }

        [TestCase(ModelImporterMeshCompression.Off)]
        [TestCase(ModelImporterMeshCompression.Low)]
        [TestCase(ModelImporterMeshCompression.Medium)]
        [TestCase(ModelImporterMeshCompression.High)]
        public void ChannelWrite_CompressedImportKeepsSourceAndWritesUv1(ModelImporterMeshCompression compression)
        {
            string assetFolder = "Assets/MeshLabCornerTags_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring("Assets/".Length));
            string sourcePath = assetFolder + "/source.fbx";
            string outputPath = Path.Combine(folder, "written.fbx");
            Mesh working = null;
            try
            {
                File.WriteAllBytes(sourcePath, File.ReadAllBytes(WriteSource(true)));
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(sourcePath);
                importer.isReadable = true;
                importer.keepQuads = true;
                importer.generateSecondaryUV = false;
                importer.meshCompression = compression;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<Mesh>().Single();
                working = UnityEngine.Object.Instantiate(imported);
                working.uv2 = imported.uv.Select(uv => new Vector2(uv.x * .37f + .13f, uv.y * .41f + .17f)).ToArray();
                var sourceBytes = File.ReadAllBytes(sourcePath);
                var metaBytes = File.ReadAllBytes(sourcePath + ".meta");
                Assert.IsTrue(FbxChannelWrite.Write(sourcePath,
                    new[] { new MeshEntry { fbxMesh = imported, originalMesh = working } },
                    FbxExportIntent.UV1, outputPath, null));
                CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourcePath));
                CollectionAssert.AreEqual(metaBytes, File.ReadAllBytes(sourcePath + ".meta"));
                Assert.AreEqual(compression, ((ModelImporter)AssetImporter.GetAtPath(sourcePath)).meshCompression);
                using var source = FbxSourceDocument.Load(Path.GetFullPath(sourcePath));
                using var written = FbxSourceDocument.Load(outputPath);
                var sourceMesh = source.Meshes.Single();
                var writtenMesh = written.Meshes.Single();
                Assert.AreEqual((source.Binary, source.Major, source.Minor), (written.Binary, written.Major, written.Minor));
                Assert.AreEqual(sourceMesh.GetControlPointsCount(), writtenMesh.GetControlPointsCount());
                for (int p = 0; p < sourceMesh.GetControlPointsCount(); p++)
                {
                    var before = sourceMesh.GetControlPointAt(p);
                    var after = writtenMesh.GetControlPointAt(p);
                    for (int axis = 0; axis < 3; axis++) Assert.AreEqual(before[axis], after[axis]);
                }
                var sourceTopology = new FbxLayerChannels.Topology(sourceMesh);
                var writtenTopology = new FbxLayerChannels.Topology(writtenMesh);
                CollectionAssert.AreEqual(sourceTopology.polygonSizes, writtenTopology.polygonSizes);
                CollectionAssert.AreEqual(sourceTopology.cornerControlPoint, writtenTopology.cornerControlPoint);
                CollectionAssert.AreEqual(FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(sourceMesh)[0], sourceTopology),
                    FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(writtenMesh)[0], writtenTopology));
                CollectionAssert.AreEqual(FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(sourceMesh), sourceTopology),
                    FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(writtenMesh), writtenTopology));
                var uv1 = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(writtenMesh)[1], writtenTopology);
                Assert.AreEqual(sourceTopology.CornerCount * 2, uv1.Length);
                var uv0 = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(sourceMesh)[0], sourceTopology);
                var importedUvs = imported.uv;
                var editedUvs = working.uv2;
                for (int c = 0; c < sourceTopology.CornerCount; c++)
                {
                    // The original values are distinct (apart from corners sharing an edit).
                    // Find their imported, compressed counterparts independently of the tags.
                    var originalUv = new Vector2((float)uv0[c * 2], (float)uv0[c * 2 + 1]);
                    int vertex = Enumerable.Range(0, importedUvs.Length)
                        .OrderBy(i => (importedUvs[i] - originalUv).sqrMagnitude).First();
                    Assert.That((importedUvs[vertex] - originalUv).magnitude, Is.LessThan(.005f));
                    Assert.AreEqual((double)editedUvs[vertex].x, uv1[c * 2], $"corner {c} U");
                    Assert.AreEqual((double)editedUvs[vertex].y, uv1[c * 2 + 1], $"corner {c} V");
                }
            }
            finally
            {
                if (working != null) UnityEngine.Object.DestroyImmediate(working);
                AssetDatabase.DeleteAsset(assetFolder);
            }
        }

        [Test]
        public void CornerTags_DecodeSmallCompressionNoiseIncludingNegativeZero()
        {
            Assert.AreEqual(0, FbxChannelWrite.TagOrdinal(new List<Vector2>
                { new Vector2(-.000000894f, -1), new Vector2(131.00101f, -1) }));
        }

        [TestCase(-1f, -1f)]
        [TestCase(-.01f, -1f)]
        [TestCase(.01f, -1f)]
        [TestCase(1f, -1.01f)]
        [TestCase(1f, -2f)]
        [TestCase(float.NaN, -1f)]
        [TestCase(float.PositiveInfinity, -1f)]
        public void CornerTags_RefuseInvalidNumbersAndMixedOrdinals(float corner, float ordinal)
        {
            Assert.AreEqual(-1, FbxChannelWrite.TagOrdinal(new List<Vector2>
                { new Vector2(0, -1), new Vector2(corner, ordinal) }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Edit_WritesOnlyTheChangedChannelAndKeepsPolygonsAndFormat(bool binary)
        {
            string source = WriteSource(binary), edited = Path.Combine(folder, "edited.fbx");
            double[] uv0, colorsBefore;
            int[] sizes, controlPoints;
            using (var document = FbxSourceDocument.Load(source))
            {
                Assert.AreEqual(binary, document.Binary);
                Assert.AreEqual((7, 4), (document.Major, document.Minor));
                var mesh = document.Meshes[0];
                var topology = new FbxLayerChannels.Topology(mesh);
                sizes = topology.polygonSizes;
                controlPoints = topology.cornerControlPoint;
                uv0 = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(mesh)[0], topology);
                colorsBefore = FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(mesh), topology);

                // A new UV1 at every corner, and one corner's colour changed.
                var uv1 = new double[topology.CornerCount * 2];
                for (int c = 0; c < uv1.Length; c++) uv1[c] = c * 0.01;
                Assert.AreEqual(9, FbxLayerChannels.WriteUv(mesh, 1, topology, uv1, Enumerable.Repeat(true, 9).ToArray()));
                var color = new double[topology.CornerCount * 4];
                var changed = new bool[topology.CornerCount];
                changed[4] = true;
                color[16] = 0.75; color[17] = 0.5; color[18] = 0.25; color[19] = 1;
                Assert.AreEqual(1, FbxLayerChannels.WriteColor(mesh, topology, color, changed));
                document.Save(edited);
            }

            using (var document = FbxSourceDocument.Load(edited))
            {
                Assert.AreEqual(binary, document.Binary, "container format");
                Assert.AreEqual((7, 4), (document.Major, document.Minor), "file version");
                var mesh = document.Meshes[0];
                var topology = new FbxLayerChannels.Topology(mesh);
                CollectionAssert.AreEqual(sizes, topology.polygonSizes, "a quad and a pentagon, not triangles");
                CollectionAssert.AreEqual(controlPoints, topology.cornerControlPoint, "control points and corner order");
                var sets = FbxLayerChannels.UvElements(mesh);
                Assert.AreEqual(2, sets.Count);
                CollectionAssert.AreEqual(uv0, FbxLayerChannels.ReadUv(sets[0], topology), "UV0 untouched, seam included");
                var colors = FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(mesh), topology);
                for (int c = 0; c < topology.CornerCount; c++)
                    if (c == 4) Assert.AreEqual(0.75, colors[c * 4]);
                    else Assert.AreEqual(colorsBefore[c * 4], colors[c * 4], 0.0, $"corner {c} keeps its stored colour exactly");
            }
        }

        [Test]
        public void Edit_SharesValuesOnlyWithinAControlPoint()
        {
            using var document = FbxSourceDocument.Load(WriteSource(true));
            var mesh = document.Meshes[0];
            var topology = new FbxLayerChannels.Topology(mesh);
            // Every corner gets (0.5, 0.5): corners on one control point may share an entry,
            // corners on different points must not (separate islands stay separate).
            var values = Enumerable.Repeat(0.5, topology.CornerCount * 2).ToArray();
            FbxLayerChannels.WriteUv(mesh, 1, topology, values, Enumerable.Repeat(true, topology.CornerCount).ToArray());
            var index = FbxLayerChannels.UvElements(mesh)[1].GetIndexArray();
            for (int a = 0; a < topology.CornerCount; a++)
                for (int b = a + 1; b < topology.CornerCount; b++)
                {
                    bool samePoint = topology.cornerControlPoint[a] == topology.cornerControlPoint[b];
                    Assert.AreEqual(samePoint, index.GetAt(a) == index.GetAt(b), $"corners {a} and {b}");
                }
        }

        [Test]
        public void Edit_RemovesTheLastUvSetAndTheColours()
        {
            string source = WriteSource(true), edited = Path.Combine(folder, "edited.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                var mesh = document.Meshes[0];
                var topology = new FbxLayerChannels.Topology(mesh);
                FbxLayerChannels.WriteUv(mesh, 1, topology, new double[topology.CornerCount * 2], Enumerable.Repeat(true, topology.CornerCount).ToArray());
                Assert.Throws<InvalidOperationException>(() => FbxLayerChannels.RemoveUv(mesh, 0), "UV1 would become UV0");
                FbxLayerChannels.RemoveUv(mesh, 1);
                Assert.IsTrue(FbxLayerChannels.RemoveColor(mesh));
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var mesh = document.Meshes[0];
                Assert.AreEqual(1, FbxLayerChannels.UvElements(mesh).Count);
                Assert.IsNull(FbxLayerChannels.ColorElement(mesh));
                Assert.AreEqual(4, mesh.GetPolygonSize(0), "polygons untouched");
            }
        }

        [Test]
        public void WriteNormals_NewElementOnAnUnsmoothedMeshIsPerControlPoint()
        {
            string source = WriteSource(true);
            using var document = FbxSourceDocument.Load(source);
            var mesh = document.Meshes[0];
            var topology = new FbxLayerChannels.Topology(mesh);
            int corners = topology.CornerCount;
            var all = Enumerable.Repeat(true, corners).ToArray();
            var smooth = Enumerable.Range(0, corners).SelectMany(_ => new double[] { 0, 0, 1 }).ToArray();
            Assert.AreEqual(corners, FbxLayerChannels.WriteNormals(mesh, topology, smooth, all, true));
            var element = FbxLayerChannels.NormalElement(mesh);
            Assert.AreEqual(FbxLayerElement.EMappingMode.eByControlPoint, element.GetMappingMode());
            Assert.AreEqual(FbxLayerElement.EReferenceMode.eDirect, element.GetReferenceMode());
            Assert.AreEqual(topology.controlPointCount, element.GetDirectArray().GetCount());
            CollectionAssert.AreEqual(smooth, FbxLayerChannels.ReadVectors(element, topology));

            // A hard edge (two normals on one point: corner 1 shares point 1 with corner 4)
            // cannot be held per point.
            Assert.IsTrue(FbxLayerChannels.RemoveNormals(mesh));
            var hard = (double[])smooth.Clone();
            hard[3] = 1; hard[5] = 0;
            Assert.AreEqual(corners, FbxLayerChannels.WriteNormals(mesh, topology, hard, all, true));
            element = FbxLayerChannels.NormalElement(mesh);
            Assert.AreEqual(FbxLayerElement.EMappingMode.eByPolygonVertex, element.GetMappingMode());
            CollectionAssert.AreEqual(hard, FbxLayerChannels.ReadVectors(element, topology));
        }

        [Test]
        public void Edit_WritesColoursOnLayerZero()
        {
            using var document = FbxSourceDocument.Load(WriteSource(true));
            var mesh = document.Meshes[0];
            var topology = new FbxLayerChannels.Topology(mesh);
            Assert.IsNotNull(FbxLayerChannels.ColorElement(mesh));
            Assert.AreEqual(1, FbxLayerChannels.WriteColor(mesh, topology, new double[topology.CornerCount * 4], Enumerable.Range(0, topology.CornerCount).Select(c => c == 0).ToArray()));
            Assert.IsNotNull(mesh.GetLayer(0).GetVertexColors(), "Unity reads colours from layer 0 only");
        }

        [Test]
        public void Edit_WritesNormalsAndTheTangentFrameOnLayerZeroKeepingUnchangedCorners()
        {
            string source = WriteSource(true), first = Path.Combine(folder, "first.fbx"), second = Path.Combine(folder, "second.fbx");
            int corners;
            using (var document = FbxSourceDocument.Load(source))
            {
                var mesh = document.Meshes[0];
                var topology = new FbxLayerChannels.Topology(mesh);
                corners = topology.CornerCount;
                Assert.IsNull(FbxLayerChannels.NormalElement(mesh), "the source has no normals");
                var all = Enumerable.Repeat(true, corners).ToArray();
                double[] Repeat(params double[] v) => Enumerable.Range(0, corners).SelectMany(_ => v).ToArray();
                Assert.AreEqual(corners, FbxLayerChannels.WriteNormals(mesh, topology, Repeat(0, 0, 1), all));
                Assert.AreEqual(corners, FbxLayerChannels.WriteTangentFrame(mesh, topology, Repeat(1, 0, 0), Repeat(0, 1, 0), all));
                document.Save(first);
            }
            using (var document = FbxSourceDocument.Load(first))
            {
                var mesh = document.Meshes[0];
                var topology = new FbxLayerChannels.Topology(mesh);
                var normals = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(mesh), topology);
                normals[3 * 3] = 0.6; normals[3 * 3 + 2] = 0.8;
                var changed = Enumerable.Range(0, corners).Select(c => c == 3).ToArray();
                Assert.AreEqual(1, FbxLayerChannels.WriteNormals(mesh, topology, normals, changed));
                document.Save(second);
            }
            using (var document = FbxSourceDocument.Load(second))
            {
                var mesh = document.Meshes[0];
                var topology = new FbxLayerChannels.Topology(mesh);
                var normals = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(mesh), topology);
                CollectionAssert.AreEqual(new[] { 0.6, 0, 0.8 }, normals.Skip(9).Take(3).ToArray(), "the changed corner");
                CollectionAssert.AreEqual(new[] { 0.0, 0, 1 }, normals.Take(3).ToArray(), "an unchanged corner keeps its value");
                CollectionAssert.AreEqual(new[] { 1.0, 0, 0 }, FbxLayerChannels.ReadVectors(FbxLayerChannels.TangentElement(mesh), topology).Take(3).ToArray());
                CollectionAssert.AreEqual(new[] { 0.0, 1, 0 }, FbxLayerChannels.ReadVectors(FbxLayerChannels.BinormalElement(mesh), topology).Take(3).ToArray());
                Assert.AreEqual(4, mesh.GetPolygonSize(0), "polygons untouched");
                Assert.IsTrue(FbxLayerChannels.RemoveTangentFrame(mesh));
                Assert.IsTrue(FbxLayerChannels.RemoveNormals(mesh));
                Assert.IsNull(FbxLayerChannels.TangentElement(mesh));
                Assert.IsNull(FbxLayerChannels.NormalElement(mesh));
            }
        }

        [Test]
        public void Edit_RefusesAUvSetThatWouldSkipAChannel()
        {
            using var document = FbxSourceDocument.Load(WriteSource(true));
            var mesh = document.Meshes[0];
            var topology = new FbxLayerChannels.Topology(mesh);
            Assert.Throws<InvalidOperationException>(() =>
                FbxLayerChannels.WriteUv(mesh, 2, topology, new double[topology.CornerCount * 2], Enumerable.Repeat(true, topology.CornerCount).ToArray()));
        }

        [Test]
        public void CornerTag_TakesTheNextChannel()
        {
            using var document = FbxSourceDocument.Load(WriteSource(true));
            var mesh = document.Meshes[0];
            Assert.AreEqual(1, FbxLayerChannels.AddCornerTag(mesh, 4));
            var topology = new FbxLayerChannels.Topology(mesh);
            var tags = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(mesh)[1], topology);
            for (int c = 0; c < topology.CornerCount; c++)
            {
                Assert.AreEqual(c, tags[c * 2]);
                Assert.AreEqual(-5, tags[c * 2 + 1]);
            }
        }

        [Test]
        public void WriterVersion_PicksTheSourceVersionOrTheOldestNewer()
        {
            Assert.AreEqual("FBX201400", FbxSourceDocument.WriterVersion(7, 4));
            Assert.AreEqual("FBX202000", FbxSourceDocument.WriterVersion(7, 7));
            Assert.AreEqual("FBX201800", FbxSourceDocument.WriterVersion(7, 5));
            Assert.AreEqual("FBX201100", FbxSourceDocument.WriterVersion(6, 1));
        }
#endif
    }
}
