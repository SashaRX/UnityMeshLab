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
using UnityEngine.Rendering;
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

        [Test]
        public void PolygonSplit_UsesAuditedImportTrianglesAndKeepsNeighbour()
        {
            var plan = FbxPolygonSplit.Plan(new[] { 4, 5 }, new[] { true, false, true, false, false, false, false, false, false },
                new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6, 4, 8, 7 });
            CollectionAssert.AreEqual(new[] { 3, 3, 5 }, plan.sizes);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 7, 8 }, plan.corners);
            CollectionAssert.AreEqual(new[] { 0, 0, 1 }, plan.polygons);
            Assert.AreEqual(1, plan.splitCount);
        }

        [TestCase(new[] { 0, 2, 1 })]
        [TestCase(new[] { 0, 2, 1, 0, 2, 1 })]
        public void PolygonSplit_RefusesIncompleteOrDuplicateTriangulation(int[] triangles)
            => Assert.Throws<InvalidOperationException>(() => FbxPolygonSplit.Plan(new[] { 4 }, new[] { true, false, true, false }, triangles));

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [TestCase(false)] [TestCase(true)]
        public void SplitMaterialSelectionIncludesEveryExpandedCorner(bool keepQuads)
        {
            var tag=new FbxChannelWrite.Tagged {cornerPositions=new float[7*3],
                submeshCorners=new[] {keepQuads?new[] {0,1,2,3}:new[] {0,1,2,0,2,3},new[] {4,5,6}},
                submeshFaceSizes=new[] {keepQuads?4:3,3}};
            var plan=FbxPolygonSplit.Plan(new[] {4,3},new[] {true,false,true,false,false,false,false},tag.TriangleCorners());
            FbxChannelWrite.ExpandTag(tag,plan,7);
            CollectionAssert.AreEquivalent(Enumerable.Range(0,6).ToArray(),tag.submeshCorners[0]);
            CollectionAssert.AreEqual(new[] {6,7,8},tag.submeshCorners[1]);
            Assert.IsTrue(tag.submeshFaceSizes.All(s=>s==3));
            for(int s=0;s<tag.submeshCorners.Length;++s)
                for(int i=0;i<tag.submeshCorners[s].Length;++i)
                    Assert.That(plan.corners[tag.submeshCorners[s][i]],s==0?Is.LessThan(4):Is.GreaterThanOrEqualTo(4));
        }

        string folder;
        MeshLabFbxFormat previousFormat;
        bool previousEmbed;

        [SetUp]
        public void SetUp()
        {
            var settings = MeshLabProjectSettings.Instance;
            previousFormat = settings.fbxFormat; previousEmbed = settings.embedFbxTextures;
            settings.fbxFormat = MeshLabFbxFormat.Binary; settings.embedFbxTextures = false;
            Directory.CreateDirectory(folder = Path.Combine(Path.GetTempPath(), "meshlab-channel-" + Guid.NewGuid().ToString("N")));
        }

        [TearDown]
        public void TearDown()
        {
            var settings = MeshLabProjectSettings.Instance;
            settings.fbxFormat = previousFormat; settings.embedFbxTextures = previousEmbed;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        // A quad and a pentagon sharing an edge, UV0 per corner with a seam, colours per corner at full precision.
        string WriteSource(bool binary, string version = "FBX201400", bool withCollision = false)
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
            if (withCollision)
            {
                var data = new FbxMeshData {
                    controlPoints = new double[] { 0, 0, 0, 10, 0, 0, 0, 10, 0 },
                    polygonSizes = new[] { 3 }, polygonVertices = new[] { 0, 1, 2 },
                    colors = new double[] { .1, .2, .3, 1, .4, .5, .6, 1, .7, .8, .9, 1 }
                };
                data.uvNames.Add("map1"); data.uvs.Add(new double[] { 0, 0, 1, 0, 0, 1 });
                var collision = FbxNode.Create(scene, "Part_COL");
                collision.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, "Part_COL", data));
                scene.GetRootNode().AddChild(collision);
            }
            using var exporter = FbxExporter.Create(manager, "Write");
            int format = manager.GetIOPluginRegistry().FindWriterIDByDescription(binary ? "FBX binary (*.fbx)" : "FBX ascii (*.fbx)");
            Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.SetFileExportVersion(version) && exporter.Export(scene));
            return path;
        }

        [TestCase(true, FbxExportIntent.UV1, true)]
        [TestCase(false, FbxExportIntent.UV1, false)]
        [TestCase(true, FbxExportIntent.UV1, false)]
        [TestCase(false, FbxExportIntent.UV1, true)]
        [TestCase(true, FbxExportIntent.VertexColors, true)]
        [TestCase(true, FbxExportIntent.Normals, true)]
        [TestCase(true, FbxExportIntent.UV1 | FbxExportIntent.VertexColors | FbxExportIntent.Normals, true)]
        public void ChannelWrite_SplitsOnlyThePolygonWithANewInternalSeam(bool binary, FbxExportIntent intent, bool outputBinary)
        {
            MeshLabProjectSettings.Instance.fbxFormat=outputBinary?MeshLabFbxFormat.Binary:MeshLabFbxFormat.ASCII;
            string assetFolder = "Assets/MeshLabPolygonSeam_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring("Assets/".Length));
            string sourcePath = assetFolder + "/source.fbx";
            Mesh working = null;
            try
            {
                File.WriteAllBytes(sourcePath, File.ReadAllBytes(WriteSource(binary)));
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(sourcePath);
                importer.isReadable = true;
                importer.keepQuads = false;
                importer.generateSecondaryUV = false;
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<Mesh>().Single();
                working = MakeTriangleSeams(imported, intent, onlyQuad: true);
                byte[] before = File.ReadAllBytes(sourcePath), metadata = File.ReadAllBytes(sourcePath + ".meta");
                string output = Path.Combine(folder, "seam.fbx");
                Assert.IsTrue(FbxChannelWrite.Write(sourcePath,
                    new[] { new MeshEntry { fbxMesh = imported, originalMesh = working } }, intent, output, null));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(sourcePath));
                CollectionAssert.AreEqual(metadata, File.ReadAllBytes(sourcePath + ".meta"));
                using var source = FbxSourceDocument.Load(Path.GetFullPath(sourcePath));
                using var result = FbxSourceDocument.Load(output);
                Assert.AreEqual((outputBinary, source.Major, source.Minor), (result.Binary, result.Major, result.Minor));
                var a = source.Meshes.Single(); var b = result.Meshes.Single();
                CollectionAssert.AreEqual(FbxStructureEdit.ControlPoints(a), FbxStructureEdit.ControlPoints(b));
                var original = new FbxLayerChannels.Topology(a); var split = new FbxLayerChannels.Topology(b);
                CollectionAssert.AreEqual(new[] { 3, 3, 5 }, split.polygonSizes);
                CollectionAssert.AreEqual(original.cornerControlPoint.Skip(4), split.cornerControlPoint.Skip(6));
                var oldUv = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(a)[0], original);
                var newUv = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(b)[0], split);
                var oldColor = FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(a), original);
                var newColor = FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(b), split);
                void Preserved(double expected,double actual)
                {
                    // SDK Binary-to-ASCII serialization can round the last
                    // double digit once. All same-format channels stay exact.
                    double tolerance=binary && !outputBinary
                        ?Math.Abs(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(expected)+1)-expected):0;
                    Assert.AreEqual(expected,actual,tolerance);
                }
                for (int c = 0; c < split.CornerCount; c++)
                {
                    int oldCorner = c < 6 ? Array.IndexOf(original.cornerControlPoint, split.cornerControlPoint[c], 0, 4) : c - 2;
                    Preserved(oldUv[oldCorner * 2], newUv[c * 2]);
                    Preserved(oldUv[oldCorner * 2 + 1], newUv[c * 2 + 1]);
                    if ((intent & FbxExportIntent.VertexColors) == 0)
                        for (int k = 0; k < 4; k++) Preserved(oldColor[oldCorner * 4 + k], newColor[c * 4 + k]);
                }
                AssertWrittenSeams(b, split, working, intent);
            }
            finally
            {
                if (working != null) UnityEngine.Object.DestroyImmediate(working);
                AssetDatabase.DeleteAsset(assetFolder);
            }
        }

        static Mesh MakeTriangleSeams(Mesh imported, FbxExportIntent intent, bool onlyQuad)
        {
            int[] vertices = Enumerable.Range(0, imported.subMeshCount).SelectMany(imported.GetTriangles).ToArray();
            var originalPositions = imported.vertices; var originalNormals = imported.normals;
            var originalTangents = imported.tangents; var originalColors = imported.colors; var originalUvs = imported.uv;
            var working = new Mesh { name = imported.name, indexFormat = IndexFormat.UInt32 };
            working.vertices = vertices.Select(v => originalPositions[v]).ToArray();
            for (int channel = 0; channel < 8; channel++)
            {
                var uv = new List<Vector4>(); imported.GetUVs(channel, uv);
                if (uv.Count > 0) working.SetUVs(channel, vertices.Select(v => uv[v]).ToList());
            }
            if (originalNormals.Length > 0) working.normals = vertices.Select(v => originalNormals[v]).ToArray();
            if (originalTangents.Length > 0) working.tangents = vertices.Select(v => originalTangents[v]).ToArray();
            if (originalColors.Length > 0) working.colors = vertices.Select(v => originalColors[v]).ToArray();
            var uvs = vertices.Select(v => originalUvs[v] * .4f).ToArray();
            var colors = working.colors;
            var normals = working.normals;
            int quad = 0;
            for (int t = 0; t < vertices.Length; t += 3)
            {
                bool selected = !onlyQuad || Enumerable.Range(t, 3).All(c => originalUvs[vertices[c]].x < .35f);
                if (!selected) continue;
                float delta = .13f * ++quad;
                for (int k = 0; k < 3; k++)
                {
                    uvs[t + k] += new Vector2(delta, delta);
                    if (colors.Length > 0 && (intent & FbxExportIntent.VertexColors) != 0) colors[t + k] = new Color(delta, .2f, .3f, 1f);
                    if (normals.Length > 0 && (intent & FbxExportIntent.Normals) != 0) normals[t + k] = new Vector3(delta, .2f, -1f).normalized;
                }
            }
            if (intent.IncludesUv(1)) working.uv2 = uvs;
            if ((intent & FbxExportIntent.VertexColors) != 0) working.colors = colors;
            if ((intent & FbxExportIntent.Normals) != 0) working.normals = normals;
            working.subMeshCount = imported.subMeshCount;
            for (int s = 0, offset = 0; s < imported.subMeshCount; s++)
            {
                int count = imported.GetTriangles(s).Length;
                working.SetTriangles(Enumerable.Range(offset, count).ToArray(), s); offset += count;
            }
            return working;
        }

        [TestCase(1f)] [TestCase(.001f)] [TestCase(.00001f)]
        public void ChannelWrite_ReimportKeepsSmallLightmapSeams(float scale)
        {
            string assetFolder = "Assets/MeshLabSmallUvSeams_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring(7));
            string path = assetFolder + "/source.fbx";
            Mesh working = null;
            try {
                File.Copy(WriteSource(true), path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true; importer.keepQuads = false; importer.generateSecondaryUV = false;
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.optimizeMeshPolygons = importer.optimizeMeshVertices = importer.weldVertices = false;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single();
                working = MakeTriangleSeams(imported, FbxExportIntent.UV1, false);
                working.uv2 = working.uv2.Select(p => p * scale).ToArray();
                var expected = TriangleUvRecords(working);
                Assert.IsTrue(FbxChannelWrite.Write(path, new[] { new MeshEntry { fbxMesh = imported, originalMesh = working } }, FbxExportIntent.UV1, null, null));
                var restored = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single();
                string audit = Environment.GetEnvironmentVariable("MESHLAB_FBX_SAVE_AUDIT");
                if (!string.IsNullOrEmpty(audit)) {
                    Directory.CreateDirectory(audit);
                    string stem = Path.Combine(audit, "small-seam-" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    File.Copy(path, stem + ".fbx", true);
                    File.WriteAllBytes(stem + ".mesh.bin", TransferMeshSnapshot.Capture(working));
                }
                CollectionAssert.AreEqual(expected, TriangleUvRecords(restored));
            }
            finally { if (working) UnityEngine.Object.DestroyImmediate(working); AssetDatabase.DeleteAsset(assetFolder); }
        }

        [Test]
        public void SmallUvPointSplitsPreserveSkinAndBlendShapeBindings()
        {
            string source = WriteSource(true);
            using (var document = FbxSourceDocument.Load(source)) {
                var mesh = document.Meshes.Single();
                var bone = FbxNode.Create(document.Scene, "Bone");
                var skeleton = FbxSkeleton.Create(document.Scene, "Skeleton");
                skeleton.SetSkeletonType(FbxSkeleton.EType.eRoot); bone.SetNodeAttribute(skeleton);
                document.Scene.GetRootNode().AddChild(bone);
                var skin = FbxSkin.Create(document.Scene, "Skin");
                var cluster = FbxCluster.Create(document.Scene, "Cluster");
                cluster.SetLink(bone); cluster.SetLinkMode(FbxCluster.ELinkMode.eNormalize);
                cluster.SetTransformMatrix(new FbxAMatrix()); cluster.SetTransformLinkMatrix(new FbxAMatrix());
                for (int i = 0; i < mesh.GetControlPointsCount(); ++i) cluster.AddControlPointIndex(i, 1);
                skin.AddCluster(cluster); mesh.AddDeformer(skin);
                var shape = FbxShape.Create(document.Scene, "Raised"); shape.InitControlPoints(mesh.GetControlPointsCount());
                for (int i = 0; i < mesh.GetControlPointsCount(); ++i) {
                    var p = mesh.GetControlPointAt(i);
                    shape.SetControlPointAt(new FbxVector4(p.X, p.Y, p.Z + 2), i);
                }
                var blend = FbxBlendShape.Create(document.Scene, "Blend");
                var channel = FbxBlendShapeChannel.Create(document.Scene, "Raised");
                channel.AddTargetShape(shape); blend.AddBlendShapeChannel(channel); mesh.AddDeformer(blend);
                document.Save(source);
            }
            string assetFolder = "Assets/MeshLabUvBindings_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring(7));
            string path = assetFolder + "/source.fbx";
            Mesh working = null;
            try {
                File.Copy(source, path); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true; importer.generateSecondaryUV = false; importer.keepQuads = false;
                importer.weldVertices = false; importer.meshCompression = ModelImporterMeshCompression.Off; importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single();
                Assert.AreEqual(1, imported.blendShapeCount); Assert.AreEqual(imported.vertexCount, imported.boneWeights.Length);
                var beforeDelta = new Vector3[imported.vertexCount]; imported.GetBlendShapeFrameVertices(0, 0, beforeDelta, null, null);
                working = MakeTriangleSeams(imported, FbxExportIntent.UV1, false);
                working.uv2 = working.uv2.Select(uv => uv * .001f).ToArray();
                Assert.IsTrue(FbxChannelWrite.Write(path, new[] { new MeshEntry { fbxMesh = imported, originalMesh = working } }, FbxExportIntent.UV1, null, null));
                var restored = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single();
                CollectionAssert.AreEqual(TriangleUvRecords(working), TriangleUvRecords(restored));
                Assert.AreEqual(1, restored.blendShapeCount); Assert.AreEqual(restored.vertexCount, restored.boneWeights.Length);
                var delta = new Vector3[restored.vertexCount]; restored.GetBlendShapeFrameVertices(0, 0, delta, null, null);
                for (int v = 0; v < restored.vertexCount; ++v) {
                    Assert.AreEqual(1, restored.boneWeights[v].weight0); Assert.AreEqual(0, restored.boneWeights[v].boneIndex0);
                    Assert.AreEqual(beforeDelta[0], delta[v]);
                }
            }
            finally { if (working) UnityEngine.Object.DestroyImmediate(working); AssetDatabase.DeleteAsset(assetFolder); }
        }

        [Test]
        public void ChannelWrite_RealKamazRoundTripPreservesTriangleUvSeamsAndSource()
        {
            string fixture = Environment.GetEnvironmentVariable("MESHLAB_FBX_KAMAZ");
            if (string.IsNullOrEmpty(fixture)) Assert.Ignore("Set MESHLAB_FBX_KAMAZ to the original FBX for the real-model regression.");
            byte[] original = File.ReadAllBytes(fixture);
            byte[] metadata = File.Exists(fixture + ".meta") ? File.ReadAllBytes(fixture + ".meta") : null;
            string assetFolder = "Assets/MeshLabKamazSeams_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring("Assets/".Length));
            string sourcePath = assetFolder + "/source.fbx", outputPath = assetFolder + "/result.fbx";
            Mesh working = null;
            try
            {
                File.WriteAllBytes(sourcePath, original);
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(sourcePath);
                importer.isReadable = true; importer.keepQuads = false;
                importer.generateSecondaryUV = false; importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<Mesh>().Single(m => m.name == "Kamaz_Typhoon_LOD0");
                working = MakeTriangleSeams(imported, FbxExportIntent.UV1, onlyQuad: false);
                var tags = FbxChannelWrite.ImportTagged(sourcePath, false, out _);
                Assert.IsTrue(FbxChannelWrite.Write(sourcePath,
                    new[] { new MeshEntry { fbxMesh = imported, originalMesh = working } }, FbxExportIntent.UV1, outputPath, null));
                using var source = FbxSourceDocument.Load(Path.GetFullPath(sourcePath));
                using var result = FbxSourceDocument.Load(Path.GetFullPath(outputPath));
                Assert.AreEqual(source.Meshes.Count, result.Meshes.Count);
                for (int m = 0; m < source.Meshes.Count; m++)
                {
                    var a = source.Meshes[m]; var b = result.Meshes[m];
                    Assert.AreEqual(a.GetName(), b.GetName());
                    CollectionAssert.AreEqual(FbxStructureEdit.ControlPoints(a), FbxStructureEdit.ControlPoints(b));
                    var ta = new FbxLayerChannels.Topology(a); var tb = new FbxLayerChannels.Topology(b);
                    FbxPolygonSplit plan = a.GetName() == "Kamaz_Typhoon_LOD0"
                        ? FbxPolygonSplit.Plan(ta.polygonSizes, Enumerable.Repeat(true, ta.CornerCount).ToArray(), tags[imported.name].TriangleCorners()) : null;
                    AssertUntouchedLayers(a, ta, b, tb, plan);
                    if (a.GetName() != "Kamaz_Typhoon_LOD0")
                    {
                        CollectionAssert.AreEqual(ta.polygonSizes, tb.polygonSizes);
                        CollectionAssert.AreEqual(ta.cornerControlPoint, tb.cornerControlPoint);
                    }
                }
                var restored = AssetDatabase.LoadAllAssetsAtPath(outputPath).OfType<Mesh>().Single(m => m.name == imported.name);
                CollectionAssert.AreEqual(TriangleUvRecords(working), TriangleUvRecords(restored));
                TestContext.WriteLine($"Kamaz LOD0: {working.vertexCount / 3} triangles round-tripped with independent UV1 per triangle.");
                CollectionAssert.AreEqual(original, File.ReadAllBytes(sourcePath));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture));
                if (metadata != null) CollectionAssert.AreEqual(metadata, File.ReadAllBytes(fixture + ".meta"));
            }
            finally
            {
                if (working != null) UnityEngine.Object.DestroyImmediate(working);
                AssetDatabase.DeleteAsset(assetFolder);
            }
        }

        [Test]
        public void DocumentWrite_UvSavePreservesUntouchedCollisionSurfaceData()
        {
            string assetFolder = "Assets/MeshLabCollisionChannels_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring(7));
            string path = assetFolder + "/source.fbx", outputPath = assetFolder + "/saved.fbx";
            Mesh working = null;
            try
            {
                File.WriteAllBytes(path, File.ReadAllBytes(WriteSource(true, withCollision: true)));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true; importer.generateSecondaryUV = false; importer.SaveAndReimport();
                var meshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray();
                var collision = meshes.Single(m => MeshNaming.IsCollision(m.name));
                Assert.IsTrue(collision.HasVertexAttribute(VertexAttribute.TexCoord0));
                Assert.IsTrue(collision.HasVertexAttribute(VertexAttribute.Color));
                var imported = meshes.Single(m => !MeshNaming.IsCollision(m.name));
                working = UnityEngine.Object.Instantiate(imported);
                working.uv2 = working.uv.Select(uv => uv * .5f).ToArray();
                var entry = new MeshEntry { fbxMesh = imported, originalMesh = working };
                using var plan = FbxStructureWrite.Plan(path, new List<(MeshEntry, Mesh)> { (entry, working) }, 0);
                Assert.IsTrue(FbxChannelWrite.Write(path, new[] { entry }, FbxExportIntent.UV1, outputPath, null, plan));
                AssertCollisionUnchanged(path, outputPath);
                var restored = AssetDatabase.LoadAllAssetsAtPath(outputPath).OfType<Mesh>().Single(m => m.name == imported.name);
                CollectionAssert.AreEqual(TriangleUvRecords(working), TriangleUvRecords(restored));
            }
            finally
            {
                if (working) UnityEngine.Object.DestroyImmediate(working);
                AssetDatabase.DeleteAsset(assetFolder);
            }
        }

        static void AssertCollisionUnchanged(string sourcePath, string outputPath)
        {
            using var source = FbxSourceDocument.Load(Path.GetFullPath(sourcePath));
            using var output = FbxSourceDocument.Load(Path.GetFullPath(outputPath));
            var before = source.Meshes.Single(m => MeshNaming.IsCollision(m.GetName()));
            var after = output.Meshes.Single(m => m.GetName() == before.GetName());
            var original = new FbxLayerChannels.Topology(before); var written = new FbxLayerChannels.Topology(after);
            CollectionAssert.AreEqual(FbxStructureEdit.ControlPoints(before), FbxStructureEdit.ControlPoints(after));
            CollectionAssert.AreEqual(original.polygonSizes, written.polygonSizes);
            CollectionAssert.AreEqual(original.cornerControlPoint, written.cornerControlPoint);
            AssertUntouchedLayers(before, original, after, written, null);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator DocumentWrite_RealKamazAfterReverseExclusive() => RealKamazReverseDocument(false);

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator DocumentWrite_RealKamazAfterReverseOverlap() => RealKamazReverseDocument(true);

        [TestCase(false)] [TestCase(true)]
        public void ChannelWrite_CapturedReverseKamazPreservesEveryUvCorner(bool completeDocument)
        {
            string fixture = Environment.GetEnvironmentVariable("MESHLAB_FBX_REPLAY_DIR");
            if (string.IsNullOrEmpty(fixture)) Assert.Ignore("Set MESHLAB_FBX_REPLAY_DIR to a captured reverse-save case.");
            string assetFolder = "Assets/MeshLabReverseSaveReplay_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring(7));
            string path = assetFolder + "/Kamaz.fbx";
            Mesh working = null;
            var owned = new List<Mesh>();
            try
            {
                File.Copy(Path.Combine(fixture, "before.fbx"), path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true; importer.keepQuads = false; importer.generateSecondaryUV = false;
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.optimizeMeshPolygons = importer.optimizeMeshVertices = importer.weldVertices = false;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single(m => m.name == "Kamaz_Typhoon_LOD0");
                working = TransferMeshSnapshot.Restore(File.ReadAllBytes(Path.Combine(fixture, imported.name + ".mesh.bin")));
                owned.Add(working);
                var expected = TriangleUvRecords(working).Distinct().ToArray();
                var entries = new List<MeshEntry> { new MeshEntry { fbxMesh = imported, originalMesh = working, transferredMesh = working } };
                if (completeDocument)
                    foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Where(m => m != imported)) {
                        string snapshot = Path.Combine(fixture, mesh.name + ".mesh.bin");
                        if (!File.Exists(snapshot)) continue;
                        var copy = TransferMeshSnapshot.Restore(File.ReadAllBytes(snapshot)); owned.Add(copy);
                        entries.Add(new MeshEntry { fbxMesh = mesh, originalMesh = copy, transferredMesh = copy, lodIndex = MeshNaming.LodIndex(mesh.name) });
                    }
                var allExpected = entries.ToDictionary(e => e.fbxMesh.name, e => TriangleUvRecords(e.originalMesh).Distinct().ToArray());
                using var plan = completeDocument ? FbxStructureWrite.Plan(path, entries.Select(e => (e, e.originalMesh)).ToList(), 0) : null;
                Assert.IsTrue(FbxChannelWrite.Write(path, entries, FbxChannelWrite.Supported, null, null, plan));
                var restored = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single(m => m.name == imported.name);
                string audit = Environment.GetEnvironmentVariable("MESHLAB_FBX_SAVE_AUDIT");
                if (!string.IsNullOrEmpty(audit)) {
                    Directory.CreateDirectory(audit);
                    File.WriteAllBytes(Path.Combine(audit, "restored.mesh.bin"), TransferMeshSnapshot.Capture(restored));
                }
                var actual = TriangleUvRecords(restored).Distinct().ToArray();
                foreach (string missing in expected.Except(actual)) TestContext.WriteLine("Missing corner: " + missing);
                CollectionAssert.AreEqual(expected, actual, "Captured Reverse UV must survive FBX write and reimport.");
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Where(m => allExpected.ContainsKey(m.name))) {
                    var records = TriangleUvRecords(mesh).Distinct().ToArray();
                    foreach (string missing in allExpected[mesh.name].Except(records).Take(10)) TestContext.WriteLine(mesh.name + " missing: " + missing);
                    foreach (string extra in records.Except(allExpected[mesh.name]).Take(10)) TestContext.WriteLine(mesh.name + " extra: " + extra);
                    if (!string.IsNullOrEmpty(audit)) File.WriteAllBytes(Path.Combine(audit, mesh.name + ".mesh.bin"), TransferMeshSnapshot.Capture(mesh));
                }
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Where(m => allExpected.ContainsKey(m.name)))
                    CollectionAssert.AreEqual(allExpected[mesh.name], TriangleUvRecords(mesh).Distinct().ToArray(), mesh.name);
            }
            finally { foreach (var mesh in owned) if (mesh) UnityEngine.Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(assetFolder); }
        }

        static System.Collections.IEnumerator RealKamazReverseDocument(bool overlap)
        {
            string fixture = Environment.GetEnvironmentVariable("MESHLAB_FBX_KAMAZ");
            if (string.IsNullOrEmpty(fixture)) Assert.Ignore("Set MESHLAB_FBX_KAMAZ to the original FBX for the real-model regression.");
            byte[] original = File.ReadAllBytes(fixture);
            byte[] metadata = File.Exists(fixture + ".meta") ? File.ReadAllBytes(fixture + ".meta") : null;
            string folder = "Assets/MeshLabKamazReverseSave_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            string path = folder + "/Kamaz.fbx";
            GameObject root = null;
            var canvas = new UvCanvasView();
            var workflow = new UvTransferWorkflow();
            var context = new UvToolContext();
            try
            {
                File.WriteAllBytes(path, original);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true; importer.keepQuads = false; importer.generateSecondaryUV = false;
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.optimizeMeshPolygons = importer.optimizeMeshVertices = importer.weldVertices = false;
                importer.SaveAndReimport();
                root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                root.transform.SetPositionAndRotation(new Vector3(52.7f, -.07f, 85.75f), Quaternion.Euler(0, 148.031784f, 0));
                var lod = root.GetComponentInChildren<LODGroup>();
                if (!lod)
                {
                    lod = root.AddComponent<LODGroup>();
                    var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                    var levels = renderers.GroupBy(r => {
                        var match = System.Text.RegularExpressions.Regex.Match(r.name, "_LOD(\\d+)$");
                        Assert.IsTrue(match.Success, r.name);
                        return int.Parse(match.Groups[1].Value);
                    }).OrderBy(g => g.Key).Select(g => new LOD(.5f / (g.Key + 1), g.Cast<Renderer>().ToArray())).ToArray();
                    lod.SetLODs(levels);
                }
                context.Refresh(lod); workflow.OnActivate(context, canvas);
                context.LightmapDensity = 32; context.ShellPaddingPx = 2; context.RepackPerMesh = false;
                context.RepackResolutionMode = ResolutionMode.AutoFromTexelDensity;
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(UvTransferWorkflow).GetField("reversePreserveOverlap", flags).SetValue(workflow, overlap);
                var task = workflow.RunReverseTransfer(false);
                while (!task.IsCompleted) yield return null;
                task.GetAwaiter().GetResult();
                Assert.IsTrue(context.HasTransfer);
                var group = context.MeshEntries.Where(e => e.include)
                    .Select(e => (entry: e, resultMesh: context.Assets.GetResultMesh(e))).ToList();
                Assert.IsTrue(group.All(p => !string.IsNullOrEmpty(p.entry.reverseTransferJson)));
                var expected = group.ToDictionary(p => p.entry.fbxMesh.name, p => TriangleUvRecords(p.resultMesh));
                string auditFolder = Environment.GetEnvironmentVariable("MESHLAB_FBX_SAVE_AUDIT");
                if (!string.IsNullOrEmpty(auditFolder))
                {
                    auditFolder = Path.Combine(auditFolder, overlap ? "overlap" : "exclusive");
                    Directory.CreateDirectory(auditFolder);
                    File.Copy(path, Path.Combine(auditFolder, "before.fbx"), true);
                    foreach (var p in group)
                        File.WriteAllBytes(Path.Combine(auditFolder, p.entry.fbxMesh.name + ".mesh.bin"), TransferMeshSnapshot.Capture(p.resultMesh));
                }
                string collisionBefore = Path.Combine(Path.GetTempPath(), "meshlab-kamaz-collision-" + Guid.NewGuid().ToString("N") + ".fbx");
                File.WriteAllBytes(collisionBefore, File.ReadAllBytes(path));
                string guid = AssetDatabase.AssetPathToGUID(path);
                try
                {
                    using var plan = FbxStructureWrite.Plan(path, group, context.SourceLodIndex);
                    Assert.IsTrue(FbxChannelWrite.Write(path, group.Select(p => p.entry.PreviewCopy(p.resultMesh)),
                        FbxChannelWrite.Supported, null, null, plan));
                    AssertCollisionUnchanged(collisionBefore, path);
                    TestContext.WriteLine($"Kamaz Reverse overlap={overlap}: {group.Count} meshes saved; {plan.reshaped.Count} geometry replacements.");
                }
                finally { File.Delete(collisionBefore); }
                Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
                if (!string.IsNullOrEmpty(auditFolder)) File.Copy(path, Path.Combine(auditFolder, "after.fbx"), true);
                var restored = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToDictionary(m => m.name);
                foreach (var result in expected)
                {
                    var actual = TriangleUvRecords(restored[result.Key]);
                    // Unsplit FBX polygons may import with a different diagonal after UV edits.
                    // Compare every distinct position/UV corner, independently of how often
                    // triangulation repeats it, and keep the total triangle corner count.
                    var wantedCorners = result.Value.Distinct().ToArray(); var savedCorners = actual.Distinct().ToArray();
                    TestContext.WriteLine($"{result.Key}: {result.Value.Length} triangle corners, " +
                        $"{wantedCorners.Length}/{savedCorners.Length} distinct corners, " +
                        $"{wantedCorners.Except(savedCorners).Count()} missing, {savedCorners.Except(wantedCorners).Count()} extra.");
                    CollectionAssert.AreEqual(wantedCorners, savedCorners, result.Key + " UV corners round trip");
                    Assert.AreEqual(result.Value.Length, actual.Length, result.Key + " triangle corner count");
                }
                CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture), "The user's original FBX must remain untouched.");
                if (metadata != null) CollectionAssert.AreEqual(metadata, File.ReadAllBytes(fixture + ".meta"));
            }
            finally
            {
                if (root)
                {
                    workflow.OnDeactivate();
                    typeof(UvTransferWorkflow).GetMethod("ResetWorkingMeshesToFbx",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(workflow, null);
                }
                canvas.Cleanup();
                if (root) UnityEngine.Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        static string[] TriangleUvRecords(Mesh mesh)
        {
            var positions = mesh.vertices; var uv0 = mesh.uv; var uv1 = mesh.uv2;
            return Enumerable.Range(0, mesh.subMeshCount).SelectMany(mesh.GetTriangles).Select(v =>
                string.Join("/", new[] { positions[v].x, positions[v].y, positions[v].z, uv0[v].x, uv0[v].y, uv1[v].x, uv1[v].y }
                    .Select(value => BitConverter.SingleToInt32Bits(value).ToString("X8")))).OrderBy(record => record, StringComparer.Ordinal).ToArray();
        }

        static void AssertUntouchedLayers(FbxMesh source, FbxLayerChannels.Topology original, FbxMesh written,
            FbxLayerChannels.Topology result, FbxPolygonSplit plan)
        {
            void Same(double[] before, double[] after, int stride)
                => CollectionAssert.AreEqual(plan == null ? before : plan.Expand(before, stride), after);
            double[] Vectors(FbxLayerElementTemplateFbxVector4 element, FbxLayerChannels.Topology topology)
                => element == null ? null : FbxLayerChannels.ReadVectors(element, topology);
            var oldUv = FbxLayerChannels.UvElements(source); var newUv = FbxLayerChannels.UvElements(written);
            for (int ch = 0; ch < oldUv.Count; ch++)
                if (plan == null || ch != 1) Same(FbxLayerChannels.ReadUv(oldUv[ch], original), FbxLayerChannels.ReadUv(newUv[ch], result), 2);
            Same(FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(source), original), FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(written), result), 4);
            Same(Vectors(FbxLayerChannels.NormalElement(source), original), Vectors(FbxLayerChannels.NormalElement(written), result), 3);
            Same(Vectors(FbxLayerChannels.TangentElement(source), original), Vectors(FbxLayerChannels.TangentElement(written), result), 3);
            Same(Vectors(FbxLayerChannels.BinormalElement(source), original), Vectors(FbxLayerChannels.BinormalElement(written), result), 3);
            var materials = FbxStructureEdit.PolygonMaterials(source);
            CollectionAssert.AreEqual(plan == null ? materials : plan.polygons.Select(p => materials[p]), FbxStructureEdit.PolygonMaterials(written));
        }

        [Test]
        public void PolygonSplit_Binary75KeepsUnchangedChannelsAndControlPoints()
        {
            using var source = FbxSourceDocument.Load(WriteSource(true, "FBX201800"));
            using var result = FbxSourceDocument.Load(WriteSource(true, "FBX201800"));
            var original = new FbxLayerChannels.Topology(source.Meshes.Single());
            var plan = FbxPolygonSplit.Plan(original.polygonSizes,
                new[] { true, false, true, false, false, false, false, false, false }, new[] { 0, 2, 1, 0, 3, 2 });
            result.SplitPolygons(new Dictionary<string, FbxPolygonSplit> { { result.Meshes.Single().GetName(), plan } });
            string path = Path.Combine(folder, "split75.fbx"); result.Save(path);
            using var reloaded = FbxSourceDocument.Load(path);
            Assert.AreEqual((7, 5), (reloaded.Major, reloaded.Minor));
            var mesh = reloaded.Meshes.Single(); var topology = new FbxLayerChannels.Topology(mesh);
            CollectionAssert.AreEqual(new[] { 3, 3, 5 }, topology.polygonSizes);
            CollectionAssert.AreEqual(FbxStructureEdit.ControlPoints(source.Meshes.Single()), FbxStructureEdit.ControlPoints(mesh));
            AssertUntouchedLayers(source.Meshes.Single(), original, mesh, topology, plan);
        }

        static void AssertWrittenSeams(FbxMesh mesh, FbxLayerChannels.Topology topology, Mesh working, FbxExportIntent intent)
        {
            double[] values = intent.IncludesUv(1)
                ? FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(mesh)[1], topology)
                : intent == FbxExportIntent.VertexColors
                    ? FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(mesh), topology)
                    : FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(mesh), topology);
            int stride = intent.IncludesUv(1) ? 2 : intent == FbxExportIntent.VertexColors ? 4 : 3;
            // Both triangles must retain distinct values at the two shared control points.
            foreach (int point in topology.cornerControlPoint.Take(3).Intersect(topology.cornerControlPoint.Skip(3).Take(3)))
            {
                int a = Array.IndexOf(topology.cornerControlPoint, point, 0, 3);
                int b = Array.IndexOf(topology.cornerControlPoint, point, 3, 3);
                Assert.AreNotEqual(values[a * stride], values[b * stride], $"seam at control point {point}");
            }
            Assert.Greater(working.vertexCount, mesh.GetControlPointsCount());
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
        [TestCase(-.125f, -1f)]
        [TestCase(.125f, -1f)]
        [TestCase(1f, -1.125f)]
        [TestCase(1f, -4097f)]
        [TestCase(4096f, -1f)]
        [TestCase(1f, 0f)]
        [TestCase(1f, -16777218f)]
        [TestCase(float.NaN, -1f)]
        [TestCase(float.PositiveInfinity, -1f)]
        public void CornerTags_RefuseInvalidNumbersAndMixedOrdinals(float corner, float ordinal)
        {
            Assert.AreEqual(-1, FbxChannelWrite.TagOrdinal(new List<Vector2>
                { new Vector2(0, -1), new Vector2(corner, ordinal) }));
        }

        [TestCase(0, 0)]
        [TestCase(4095, 0)]
        [TestCase(4096, 7)]
        [TestCase(33743, 7)]
        [TestCase(16777215, 4095)]
        public void CornerTags_DecodeBothDigitsAndMeshOrdinal(int expectedCorner, int expectedOrdinal)
        {
            int radix = FbxLayerChannels.CornerTagRadix;
            var tag = new Vector2(expectedCorner % radix, -(expectedOrdinal * radix + expectedCorner / radix + 1));
            Assert.IsTrue(FbxChannelWrite.DecodeTag(tag, out int corner, out int ordinal));
            Assert.AreEqual(expectedCorner, corner);
            Assert.AreEqual(expectedOrdinal, ordinal);
        }

        [Test]
        public void CornerTags_EmptyChannelIsNotATag()
        {
            Assert.AreEqual(-1, FbxChannelWrite.TagOrdinal(new List<Vector2>()));
        }

        [TestCase(ModelImporterMeshCompression.Off, true)]
        [TestCase(ModelImporterMeshCompression.Low, true)]
        [TestCase(ModelImporterMeshCompression.Medium, true)]
        [TestCase(ModelImporterMeshCompression.High, true)]
        [TestCase(ModelImporterMeshCompression.High, false)]
        public void ChannelWrite_LargeCompressedGridWritesEveryCorner(ModelImporterMeshCompression compression, bool keepQuads)
        {
            const int size = 96; // 36,864 FBX corners: exercises both tag digits.
            string assetFolder = "Assets/MeshLabLargeTags_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", assetFolder.Substring("Assets/".Length));
            string sourcePath = assetFolder + "/grid.fbx";
            string outputPath = Path.Combine(folder, "grid_written.fbx");
            Mesh working = null;
            try
            {
                WriteGridSource(sourcePath, size);
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(sourcePath);
                importer.isReadable = true;
                importer.keepQuads = keepQuads;
                importer.meshCompression = compression;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<Mesh>().Single();
                working = UnityEngine.Object.Instantiate(imported);
                Vector2 Edited(Vector2 uv) => new Vector2(Mathf.Round(uv.x * size) / 127f + .07f,
                    Mathf.Round(uv.y * size) / 131f + .11f);
                working.uv2 = imported.uv.Select(Edited).ToArray();
                var sourceBytes = File.ReadAllBytes(sourcePath);
                var metaBytes = File.ReadAllBytes(sourcePath + ".meta");
                Assert.IsTrue(FbxChannelWrite.Write(sourcePath,
                    new[] { new MeshEntry { fbxMesh = imported, originalMesh = working } },
                    FbxExportIntent.UV1, outputPath, null));
                CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourcePath));
                CollectionAssert.AreEqual(metaBytes, File.ReadAllBytes(sourcePath + ".meta"));
                using var source = FbxSourceDocument.Load(Path.GetFullPath(sourcePath));
                using var written = FbxSourceDocument.Load(outputPath);
                var a = source.Meshes.Single(); var b = written.Meshes.Single();
                var at = new FbxLayerChannels.Topology(a); var bt = new FbxLayerChannels.Topology(b);
                CollectionAssert.AreEqual(at.polygonSizes, bt.polygonSizes);
                CollectionAssert.AreEqual(at.cornerControlPoint, bt.cornerControlPoint);
                Assert.AreEqual(a.GetControlPointsCount(), b.GetControlPointsCount());
                for (int p = 0; p < a.GetControlPointsCount(); p++)
                    for (int axis = 0; axis < 3; axis++) Assert.AreEqual(a.GetControlPointAt(p)[axis], b.GetControlPointAt(p)[axis]);
                var original = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(a)[0], at);
                CollectionAssert.AreEqual(original, FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(b)[0], bt));
                var saved = FbxLayerChannels.ReadUv(FbxLayerChannels.UvElements(b)[1], bt);
                Assert.AreEqual(at.CornerCount * 2, saved.Length);
                for (int c = 0; c < at.CornerCount; c++)
                {
                    var expected = Edited(new Vector2((float)original[c * 2], (float)original[c * 2 + 1]));
                    Assert.AreEqual((double)expected.x, saved[c * 2], $"Corner {c} U");
                    Assert.AreEqual((double)expected.y, saved[c * 2 + 1], $"Corner {c} V");
                }
            }
            finally
            {
                if (working) UnityEngine.Object.DestroyImmediate(working);
                AssetDatabase.DeleteAsset(assetFolder);
            }
        }

        static void WriteGridSource(string path, int size)
        {
            using var manager = FbxManager.Create();
            var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
            manager.SetIOSettings(io);
            var scene = FbxScene.Create(manager, "Grid");
            var mesh = FbxMesh.Create(scene, "GridMesh");
            int side = size + 1;
            mesh.InitControlPoints(side * side);
            mesh.CreateLayer();
            var uv = FbxLayerElementUV.Create(mesh, "map1");
            uv.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
            uv.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    mesh.SetControlPointAt(new FbxVector4(x, y, 0), y * side + x);
                    uv.GetDirectArray().Add(new FbxVector2((double)x / size, (double)y / size));
                }
            mesh.GetLayer(0).SetUVs(uv, FbxLayerElement.EType.eTextureDiffuse);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int p = y * side + x;
                    mesh.BeginPolygon();
                    mesh.AddPolygon(p); mesh.AddPolygon(p + 1); mesh.AddPolygon(p + side + 1); mesh.AddPolygon(p + side);
                    mesh.EndPolygon();
                }
            var node = FbxNode.Create(scene, "Grid"); node.SetNodeAttribute(mesh); scene.GetRootNode().AddChild(node);
            using var exporter = FbxExporter.Create(manager, "Write");
            Assert.IsTrue(exporter.Initialize(Path.GetFullPath(path), -1, io) && exporter.Export(scene));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Edit_WritesOnlyTheChangedChannelAndKeepsPolygonsInBinaryOutput(bool binary)
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
                Assert.IsTrue(document.Binary, "default output is binary even for an ASCII source");
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
                Assert.AreEqual(c % FbxLayerChannels.CornerTagRadix, tags[c * 2]);
                Assert.AreEqual(-(4 * FbxLayerChannels.CornerTagRadix + c / FbxLayerChannels.CornerTagRadix + 1), tags[c * 2 + 1]);
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
