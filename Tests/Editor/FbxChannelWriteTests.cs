// FbxChannelWriteTests.cs — the strict FBX channel re-save: pairing FBX corners with
// Unity vertices (FbxCornerMatch), and, with the FBX SDK, editing one layer element
// of a real file while its polygons and other channels come through unchanged.
using System;
using System.Linq;
using NUnit.Framework;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System.IO;
using Autodesk.Fbx;
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
