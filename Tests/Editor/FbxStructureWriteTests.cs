// FbxStructureWriteTests.cs — geometry MeshLab adds to an FBX (generated LODs, collision,
// edited faces): the fit from Unity space back to control-point space (FbxSpaceFit), the
// FBX mesh built from Unity triangles (FbxMeshData), and, with the FBX SDK, new nodes
// placed next to their source node while the rest of the document stays as it was.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System.IO;
using Autodesk.Fbx;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public class FbxStructureWriteTests
    {
        // Unity's view of FBX point p: X and Z swapped (a mirror), centimetres to metres, offset.
        static (float x, float y, float z) Import(double x, double y, double z)
            => ((float)(z * 0.01 + 2.005), (float)(y * 0.01 - 1), (float)(x * 0.01 + 3));

        static double[] CubePoints() => new double[]
        {
            0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0,
            0, 0, 50, 100, 0, 50, 100, 100, 50, 0, 100, 50, 33, 71, 12,
        };

        static float[] Imported(double[] points)
        {
            var result = new float[points.Length];
            for (int i = 0; i < points.Length / 3; i++)
            {
                var (x, y, z) = Import(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
                result[i * 3] = x; result[i * 3 + 1] = y; result[i * 3 + 2] = z;
            }
            return result;
        }

        [Test]
        public void Fit_RecoversTheImportMapAndItsMirror()
        {
            var points = CubePoints();
            var fit = FbxSpaceFit.Fit(points, Imported(points), 0, out string error);
            Assert.IsNotNull(fit, error);
            Assert.Less(fit.Determinant, 0, "the import mirrors");
            // A point the reference does not have goes back through the inverse map.
            var (ux, uy, uz) = Import(12.5, -40, 77);
            fit.ToFbx(ux, uy, uz, out double x, out double y, out double z);
            Assert.AreEqual(12.5, x, 1e-3);
            Assert.AreEqual(-40, y, 1e-3);
            Assert.AreEqual(77, z, 1e-3);
        }

        [Test]
        public void Fit_ReturnsTheReferencesOwnControlPointForAnImportedPosition()
        {
            var points = CubePoints();
            var unity = Imported(points);
            var fit = FbxSpaceFit.Fit(points, unity, 0, out _);
            fit.ToFbx(unity[24], unity[25], unity[26], out double x, out double y, out double z);
            Assert.AreEqual((33.0, 71.0, 12.0), (x, y, z), "bit for bit, not through the inverse");
        }

        [TestCase(-1)]
        [TestCase(1)]
        public void Fit_TakesAFlatMeshsMirrorFromTheWinding(int sign)
        {
            var points = new double[] { 0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0, 40, 30, 0 };
            var fit = FbxSpaceFit.Fit(points, Imported(points), sign, out string error);
            Assert.IsNotNull(fit, error);
            Assert.AreEqual(sign, Math.Sign(fit.Determinant));
            if (sign < 0)
            {
                // The true map mirrors: a point off the plane comes back where it was.
                var (ux, uy, uz) = Import(10, 20, 30);
                fit.ToFbx(ux, uy, uz, out double x, out double y, out double z);
                Assert.AreEqual(10, x, 1e-3);
                Assert.AreEqual(20, y, 1e-3);
                Assert.AreEqual(30, z, 1e-3);
            }
        }

        [Test]
        public void Fit_RefusesAnImportThatIsNotAnAffineImage()
        {
            var points = CubePoints();
            var unity = Imported(points);
            unity[13] += 0.2f; // a deformed vertex (skinning, blend shape)
            Assert.IsNull(FbxSpaceFit.Fit(points, unity, 0, out string error));
            StringAssert.Contains("not an affine image", error);
        }

        [Test]
        public void WindingRelation_ReadsTheImportsTriangleOrder()
        {
            int[] quad = { 4 };
            Assert.AreEqual(1, FbxSpaceFit.WindingRelation(quad, new[] { 0, 1, 2, 0, 2, 3 }));
            Assert.AreEqual(-1, FbxSpaceFit.WindingRelation(quad, new[] { 0, 2, 1, 0, 3, 2 }));
            Assert.AreEqual(0, FbxSpaceFit.WindingRelation(quad, new[] { 0, 0, 1 }));
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [Test]
        public void AStrayPreviewMaterialKeepsTheModelsSlot()
        {
            // A preview material copied onto a renderer its preview never captured has no backup
            // to unwind it; the save must not take it for an authored assignment.
            var wood = new UnityEngine.Material(UnityEngine.Shader.Find("Standard")) { name = "Wood" };
            var stone = new UnityEngine.Material(UnityEngine.Shader.Find("Standard")) { name = "Stone" };
            var preview = new UnityEngine.Material(UnityEngine.Shader.Find("Hidden/Internal-Colored")) { name = "Preview" };
            try
            {
                var model = new[] { wood, stone };
                CollectionAssert.AreEqual(model, FbxStructureWrite.WithoutStrayPreviews(new[] { preview, stone }, model, "Seat"));
                Assert.IsNull(FbxStructureWrite.WithoutStrayPreviews(new[] { stone, preview }, new[] { stone }, "Seat")[1],
                    "A slot the model does not have gets no material.");
                var edited = new[] { stone, stone };
                Assert.AreSame(edited, FbxStructureWrite.WithoutStrayPreviews(edited, model, "Seat"), "An authored change is left as it is.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wood); UnityEngine.Object.DestroyImmediate(stone);
                UnityEngine.Object.DestroyImmediate(preview);
            }
        }
#endif

        [Test]
        public void GeometryWriteKeepsThinNonzeroFaces()
        {
            var source = new FbxMeshData.Source {
                positions = new[] { -.754247129f, 2.82715511f, -1.81862569f,
                    -.754247129f, 2.85412812f, -1.81862545f,
                    -.754247129f, 2.82715535f, -1.81862545f },
                submeshTriangles = new[] { new[] { 0, 1, 2 } } };
            var data = FbxMeshData.FromTriangles(source, CubeFit(), false, null);
            CollectionAssert.AreEqual(new[] { 3 }, data.polygonSizes);
            Assert.AreEqual(3, data.CornerCount);
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        /// <summary>
        /// A Keep Quads import whose result stores each quad as Unity's own two triangles has
        /// the same faces, so the save writes channels instead of rewriting the mesh as
        /// triangles. The other diagonal is a re-triangulated quad and stays geometry.
        /// </summary>
        [Test]
        public void GeometryDiffersIgnoresQuadsStoredAsTheirOwnTriangles()
        {
            var imported = new UnityEngine.Mesh { vertices = new[] { UnityEngine.Vector3.zero, UnityEngine.Vector3.right,
                new UnityEngine.Vector3(1, 1, 0), UnityEngine.Vector3.up, new UnityEngine.Vector3(2, 0, 0), new UnityEngine.Vector3(2, 1, 0) } };
            UnityEngine.Mesh triangulated = null, otherDiagonal = null, moved = null;
            try
            {
                imported.SetIndices(new[] { 0, 1, 2, 3, 1, 4, 5, 2 }, UnityEngine.MeshTopology.Quads, 0);
                var split = imported.GetTriangles(0);
                triangulated = UnityEngine.Object.Instantiate(imported);
                triangulated.SetTriangles(split, 0);
                Assert.IsFalse(FbxStructureWrite.GeometryDiffers(imported, triangulated));

                // Split the first quad (corners 0..3) along the diagonal Unity did not use.
                var diagonal = split.Take(3).Intersect(split.Skip(3).Take(3)).ToArray();
                var other = Enumerable.Range(0, 4).Except(diagonal).OrderBy(c => c).ToArray();
                int a = other[0], b = other[1];
                var resplit = new[] { a, (a + 1) % 4, b, b, (b + 1) % 4, a }.Concat(split.Skip(6)).ToArray();
                otherDiagonal = UnityEngine.Object.Instantiate(imported);
                otherDiagonal.SetTriangles(resplit, 0);
                Assert.IsTrue(FbxStructureWrite.GeometryDiffers(imported, otherDiagonal), "A re-triangulated quad is geometry.");

                moved = UnityEngine.Object.Instantiate(triangulated);
                var vertices = moved.vertices; vertices[5].z = .25f; moved.vertices = vertices;
                Assert.IsTrue(FbxStructureWrite.GeometryDiffers(imported, moved), "A moved corner is geometry.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(imported);
                if (triangulated) UnityEngine.Object.DestroyImmediate(triangulated);
                if (otherDiagonal) UnityEngine.Object.DestroyImmediate(otherDiagonal);
                if (moved) UnityEngine.Object.DestroyImmediate(moved);
            }
        }
#endif

        static FbxSpaceFit CubeFit()
        {
            var points = CubePoints();
            return FbxSpaceFit.Fit(points, Imported(points), 0, out _);
        }

        // Two triangles over a square, split along a UV seam: six vertices on four positions.
        static FbxMeshData.Source SeamedSquare()
        {
            var a = Import(0, 0, 0); var b = Import(100, 0, 0); var c = Import(100, 100, 0); var d = Import(0, 100, 0);
            var source = new FbxMeshData.Source
            {
                positions = new[] { a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z, a.x, a.y, a.z, c.x, c.y, c.z, d.x, d.y, d.z },
                submeshTriangles = new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
                normals = Enumerable.Range(0, 6).SelectMany(_ => new[] { 1f, 0f, 0f }).ToArray(),
            };
            source.uvs.Add(new float[] { 0, 0, 1, 0, 1, 1, 0.5f, 0, 0.5f, 1, 0, 1 });
            return source;
        }

        [Test]
        public void MeshData_SharesControlPointsByPositionAndKeepsSeamsPerCorner()
        {
            var data = FbxMeshData.FromTriangles(SeamedSquare(), CubeFit(), false, new[] { 2, 0 });
            Assert.AreEqual(4 * 3, data.controlPoints.Length, "four positions, four control points");
            CollectionAssert.AreEqual(new[] { 3, 3 }, data.polygonSizes);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3 }, data.polygonVertices);
            CollectionAssert.AreEqual(new[] { 2, 0 }, data.polygonMaterials, "submesh → node material");
            Assert.AreEqual(100, data.controlPoints[3], 0.0, "the reference's exact control point");
            Assert.AreEqual(0.5, data.uvs[0][6]);
            Assert.AreEqual("UVChannel_1", data.uvNames[0]);
        }

        [Test]
        public void MeshData_ReversesTheWindingTheImportReversed()
        {
            var data = FbxMeshData.FromTriangles(SeamedSquare(), CubeFit(), true, null);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 3, 1 }, data.polygonVertices);
            Assert.IsNull(data.polygonMaterials);
        }

        [Test]
        public void MeshData_KeepsQuadsAsQuadsAndReversesThemWhole()
        {
            var a = Import(0, 0, 0); var b = Import(100, 0, 0); var c = Import(100, 100, 0); var d = Import(0, 100, 0);
            var source = new FbxMeshData.Source
            {
                positions = new[] { a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z, d.x, d.y, d.z },
                submeshTriangles = new[] { new[] { 0, 1, 2, 3 } },
                submeshFaceSizes = new[] { 4 },
            };
            var data = FbxMeshData.FromTriangles(source, CubeFit(), true, null);
            CollectionAssert.AreEqual(new[] { 4 }, data.polygonSizes, "a quad stays a quad");
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, data.polygonVertices, "points numbered in first use: a, d, c, b");
            Assert.AreEqual(0.0, data.controlPoints[0], 1e-9, "the first corner stays first");
            Assert.AreEqual(100.0, data.controlPoints[4], 1e-9, "then the last one: (0, 100, 0)");
        }

        [Test]
        public void MeshData_WritesTheTangentFrameBackThroughTheMap()
        {
            var source = SeamedSquare();
            var t = Import(1, 0, 0);
            var o = Import(0, 0, 0);
            // Unity tangent = the image of FBX +X, normal = the image of FBX +Z (the map is linear here).
            var n = Import(0, 0, 1);
            var tangent = new[] { t.x - o.x, t.y - o.y, t.z - o.z };
            var normal = new[] { n.x - o.x, n.y - o.y, n.z - o.z };
            source.normals = Enumerable.Range(0, 6).SelectMany(_ => normal).ToArray();
            source.tangents = Enumerable.Range(0, 6).SelectMany(_ => new[] { tangent[0], tangent[1], tangent[2], 1f }).ToArray();
            var data = FbxMeshData.FromTriangles(source, CubeFit(), false, null);
            Assert.IsNotNull(data.tangents);
            Assert.AreEqual(1, data.tangents[0], 1e-4, "tangent back on FBX +X");
            Assert.AreEqual(0, data.tangents[1], 1e-4);
            Assert.AreEqual(0, data.tangents[2], 1e-4);
            Assert.AreEqual(1, Math.Abs(data.binormals[1]), 1e-4, "binormal along FBX Y");
        }

        [Test]
        public void MeshData_LeavesOutAFaceWithoutArea()
        {
            var a = Import(0, 0, 0); var b = Import(50, 0, 0); var c = Import(100, 0, 0); var d = Import(0, 100, 0);
            var source = new FbxMeshData.Source
            {
                positions = new[] { a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z, d.x, d.y, d.z },
                submeshTriangles = new[] { new[] { 0, 1, 2, 0, 2, 3 } },
            };
            var data = FbxMeshData.FromTriangles(source, CubeFit(), false, null);
            CollectionAssert.AreEqual(new[] { 3 }, data.polygonSizes, "three distinct points on one line make no polygon");
        }

        [Test]
        public void Fit_KnowsAFlatMeshsPlaneAndWhetherItMapsWithoutStretch()
        {
            var points = new double[] { 0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0, 40, 30, 0 };
            var fit = FbxSpaceFit.Fit(points, Imported(points), -1, out string error);
            Assert.IsNotNull(fit, error);
            Assert.IsTrue(fit.Flat);
            Assert.IsTrue(fit.Conformal, "the import scales uniformly");
            var on = Import(70, 20, 0);
            var off = Import(70, 20, 5);
            Assert.IsTrue(fit.OnPlane(on.x, on.y, on.z));
            Assert.IsFalse(fit.OnPlane(off.x, off.y, off.z));

            // Stretched within the plane: off it, the scale is anyone's guess.
            var stretched = Imported(points);
            for (int i = 0; i < stretched.Length; i += 3) stretched[i + 2] = stretched[i + 2] * 3;
            var skewed = FbxSpaceFit.Fit(points, stretched, -1, out error);
            Assert.IsNotNull(skewed, error);
            Assert.IsTrue(skewed.Flat);
            Assert.IsFalse(skewed.Conformal);

            var solid = CubeFit();
            Assert.IsFalse(solid.Flat);
            Assert.IsTrue(solid.OnPlane(off.x, off.y, off.z), "a solid fit has no plane to leave");
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        string folder;

        [SetUp]
        public void SetUp() => Directory.CreateDirectory(folder = Path.Combine(Path.GetTempPath(), "meshlab-structure-" + Guid.NewGuid().ToString("N")));

        [TearDown]
        public void TearDown() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        // Root/Group/Rock_LOD0 (a quad with two materials' worth of slots, pivots and a geometric
        // offset) plus Rock_LOD0_COL with a hull child.
        string WriteSource()
        {
            string path = Path.Combine(folder, "source.fbx");
            using var manager = FbxManager.Create();
            var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
            manager.SetIOSettings(io);
            var scene = FbxScene.Create(manager, "Source");
            var group = FbxNode.Create(scene, "Group");
            group.LclTranslation.Set(new FbxDouble3(5, 0, 0));
            scene.GetRootNode().AddChild(group);

            var mesh = FbxMesh.Create(scene, "Rock_LOD0");
            mesh.InitControlPoints(4);
            double[,] points = { { 0, 0, 0 }, { 100, 0, 0 }, { 100, 100, 0 }, { 0, 100, 0 } };
            for (int i = 0; i < 4; i++) mesh.SetControlPointAt(new FbxVector4(points[i, 0], points[i, 1], points[i, 2]), i);
            mesh.BeginPolygon();
            for (int i = 0; i < 4; i++) mesh.AddPolygon(i);
            mesh.EndPolygon();
            var node = FbxNode.Create(scene, "Rock_LOD0");
            node.SetNodeAttribute(mesh);
            node.LclTranslation.Set(new FbxDouble3(1, 2, 3));
            node.LclRotation.Set(new FbxDouble3(10, 20, 30));
            node.SetRotationActive(true);
            node.SetPreRotation(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(-90, 0, 0));
            node.SetRotationPivot(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(4, 5, 6));
            node.SetGeometricTranslation(FbxNode.EPivotSet.eSourcePivot, new FbxVector4(7, 8, 9));
            node.AddMaterial(FbxSurfacePhong.Create(scene, "Stone"));
            node.AddMaterial(FbxSurfacePhong.Create(scene, "Moss"));
            group.AddChild(node);

            var col = FbxNode.Create(scene, "Rock_LOD0_COL");
            group.AddChild(col);
            var hullMesh = FbxMesh.Create(scene, "Rock_LOD0_COL_Hull0");
            hullMesh.InitControlPoints(3);
            for (int i = 0; i < 3; i++) hullMesh.SetControlPointAt(new FbxVector4(points[i, 0], points[i, 1], 1), i);
            hullMesh.BeginPolygon();
            for (int i = 0; i < 3; i++) hullMesh.AddPolygon(i);
            hullMesh.EndPolygon();
            var hull = FbxNode.Create(scene, "Rock_LOD0_COL_Hull0");
            hull.SetNodeAttribute(hullMesh);
            col.AddChild(hull);

            using var exporter = FbxExporter.Create(manager, "Write");
            int format = manager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
            Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.SetFileExportVersion("FBX201400") && exporter.Export(scene));
            return path;
        }

        // Three quads in a row. The first two meet at a smooth edge (one normal at each shared
        // point), the last meets the middle one at a hard edge (a second normal there).
        [Test]
        public void SmoothFromNormals_MarksAnEdgeHardExactlyWhereTheNormalsSplit()
        {
            string source = Path.Combine(folder, "strip.fbx"), output = Path.Combine(folder, "strip_smoothed.fbx");
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(io);
                var scene = FbxScene.Create(manager, "Strip");
                double s = Math.Sqrt(.5);
                var up = new double[] { 0, 0, 1 }; var tilted = new double[] { s, 0, s };
                var data = new FbxMeshData
                {
                    controlPoints = new double[] { 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 0, 1, 0, 1, 1, 0, 2, 1, 0, 3, 1, 0 },
                    polygonSizes = new[] { 4, 4, 4 },
                    polygonVertices = new[] { 0, 1, 5, 4, 1, 2, 6, 5, 2, 3, 7, 6 },
                    normals = Enumerable.Repeat(up, 8).Concat(Enumerable.Repeat(tilted, 4)).SelectMany(n => n).ToArray(),
                };
                var node = FbxNode.Create(scene, "Strip");
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, "Strip", data));
                scene.GetRootNode().AddChild(node);
                using var exporter = FbxExporter.Create(manager, "Write");
                int format = manager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
                Assert.IsTrue(exporter.Initialize(source, format, io) && exporter.SetFileExportVersion("FBX201400") && exporter.Export(scene));
            }
            Assert.IsNull(FbxPolygonSplitFile.ReadEdgeSmoothing(source, "Strip"), "a mesh written whole has no smoothing");
            using (var document = FbxSourceDocument.Load(source))
            {
                Assert.AreEqual(1, document.SmoothFromNormals(document.Meshes));
                document.Save(output, new FbxWriteSettings(true, false));
            }
            var smoothing = FbxPolygonSplitFile.ReadEdgeSmoothing(output, "Strip");
            Assert.IsNotNull(smoothing, "the smoothing layer survives the SDK round trip");
            Assert.AreEqual(10, smoothing.Count);
            Assert.AreEqual(1, smoothing[(1, 5)], "one normal on both sides");
            Assert.AreEqual(0, smoothing[(2, 6)], "the normals split here");
            Assert.AreEqual(1, smoothing[(0, 1)], "an open edge has nothing to split from");
            Assert.AreEqual(1, smoothing[(6, 7)]);
        }

        static FbxMeshData Triangle(int material) => new FbxMeshData
        {
            controlPoints = new double[] { 0, 0, 0, 100, 0, 0, 0, 100, 0 },
            polygonSizes = new[] { 3 },
            polygonVertices = new[] { 0, 1, 2 },
            polygonMaterials = new[] { material },
            normals = new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
            colors = new double[] { 1, 0, 0, 1, 1, 0, 0, 1, 0.5, 0.25, 0.125, 1 },
        };

        [Test]
        public void AddSibling_LandsWhereItsSourceIsWithItsMaterials()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "edited.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                var lod0 = FbxStructureEdit.NodeOf(document.Meshes[0], "Rock_LOD0");
                Assert.IsNotNull(lod0);
                Assert.IsFalse(FbxStructureEdit.IsSoleTopNode(document.Scene, lod0));
                var data = Triangle(1);
                data.uvs.Add(new double[] { 0, 0, 1, 0, 0, 1 });
                data.uvNames.Add("map1");
                var node = FbxStructureEdit.AddSibling(lod0, "Rock_LOD1", true);
                FbxStructureEdit.AddMaterials(node, lod0, Enumerable.Range(0, lod0.GetMaterialCount()));
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(document.Scene, "Rock_LOD1", data));
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var lod0 = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0").Single();
                var lod1 = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD1").Single();
                Assert.AreEqual("Group", lod1.GetParent().GetName(), "next to LOD0");
                Assert.IsTrue(FbxStructureEdit.SamePlacement(lod0, lod1, true), "same transform, pivots and geometric offset");
                Assert.AreEqual(2, lod1.GetMaterialCount());
                Assert.AreEqual("Moss", lod1.GetMaterial(1).GetName());
                var mesh = lod1.GetMesh();
                CollectionAssert.AreEqual(new[] { 1 }, FbxStructureEdit.PolygonMaterials(mesh));
                Assert.AreEqual(1, FbxLayerChannels.UvElements(mesh).Count);
                Assert.IsNotNull(mesh.GetLayer(0).GetNormals());
                Assert.IsNotNull(FbxLayerChannels.ColorElement(mesh));
                var topology = new FbxLayerChannels.Topology(mesh);
                Assert.AreEqual(0.125, FbxLayerChannels.ReadColor(FbxLayerChannels.ColorElement(mesh), topology)[10], 0.0, "colours at full precision");
                Assert.AreEqual(4, lod0.GetMesh().GetPolygonSize(0), "LOD0 stays a quad");
            }
        }

        // What Unity's import makes of an FBX mesh, as Import() maps points: one vertex per
        // corner, polygons fanned into triangles with the winding reversed (the map mirrors).
        static (float[] positions, int[] triangles) SimulatedImport(FbxMesh mesh)
        {
            var topology = new FbxLayerChannels.Topology(mesh);
            var points = FbxStructureEdit.ControlPoints(mesh);
            var positions = new float[topology.CornerCount * 3];
            for (int c = 0; c < topology.CornerCount; c++)
            {
                int cp = topology.cornerControlPoint[c];
                var (x, y, z) = Import(points[cp * 3], points[cp * 3 + 1], points[cp * 3 + 2]);
                positions[c * 3] = x; positions[c * 3 + 1] = y; positions[c * 3 + 2] = z;
            }
            var triangles = new System.Collections.Generic.List<int>();
            for (int p = 0, start = 0; p < topology.polygonSizes.Length; start += topology.polygonSizes[p], p++)
                for (int k = 1; k + 1 < topology.polygonSizes[p]; k++)
                    triangles.AddRange(new[] { start, start + k + 1, start + k });
            return (positions, triangles.ToArray());
        }

        static string[] TriangleKeys(float[] positions, int[] triangles)
        {
            string P(int v) => $"{positions[v * 3]:R},{positions[v * 3 + 1]:R},{positions[v * 3 + 2]:R}";
            // Rotated so each triangle starts at its smallest key: same orientation, any starting corner.
            return Enumerable.Range(0, triangles.Length / 3).Select(t =>
            {
                var keys = new[] { P(triangles[t * 3]), P(triangles[t * 3 + 1]), P(triangles[t * 3 + 2]) };
                int first = Array.IndexOf(keys, keys.OrderBy(k => k, StringComparer.Ordinal).First());
                return string.Join("|", keys[first], keys[(first + 1) % 3], keys[(first + 2) % 3]);
            }).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }

        [Test]
        public void Structure_CollisionUsesSourceLocalGeometryAndKeepsSourceNodePlacement()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "collision.fbx");
            string[] expected;
            using (var document = FbxSourceDocument.Load(source))
            {
                var (positions, triangles) = SimulatedImport(document.Meshes[0]);
                var mesh = new UnityEngine.Mesh { name = "Rock_LOD0_COL" };
                using var plan = new FbxStructurePlan();
                plan.collisions.Add(("Rock_LOD0", new List<UnityEngine.Mesh> { mesh }, false));
                var vertices = new UnityEngine.Vector3[positions.Length / 3];
                for (int i = 0; i < vertices.Length; ++i)
                    vertices[i] = new UnityEngine.Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
                mesh.vertices = vertices;
                mesh.triangles = triangles.Take(3).ToArray();
                expected = TriangleKeys(positions, mesh.triangles);
                var tagged = new Dictionary<string, FbxChannelWrite.Tagged> {
                    ["Rock_LOD0"] = new FbxChannelWrite.Tagged {
                        ordinal = 0, cornerPositions = positions,
                        submeshCorners = new[] { triangles }, submeshFaceSizes = new[] { 3 }
                    }
                };
                Assert.AreEqual(1, FbxStructureWrite.Apply(document, tagged, plan,
                    new FbxStructureWrite.Options { preserveHierarchy = true }, new List<string>()));
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var lod0 = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0").Single();
                var collision = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0_COL").Single();
                Assert.IsTrue(FbxStructureEdit.SamePlacement(lod0, collision, true));
                var (positions, triangles) = SimulatedImport(collision.GetMesh());
                CollectionAssert.AreEqual(expected, TriangleKeys(positions, triangles));
                Assert.AreEqual(4, lod0.GetMesh().GetPolygonSize(0));
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void Structure_CollisionInstancesFollowTheirOwnNodePaths(bool sameParent, bool migrate)
        {
            using var document = FbxSourceDocument.Load(WriteSource());
            var scene = document.Scene;
            var first = FbxStructureEdit.FindNodes(scene, "Rock_LOD0").Single();
            var second = FbxNode.Create(scene, "Rock_LOD0");
            var parent = first.GetParent();
            if (!sameParent)
            {
                parent = FbxNode.Create(scene, "OtherGroup");
                scene.GetRootNode().AddChild(parent);
            }
            parent.AddChild(second);
            second.SetNodeAttribute(first.GetMesh());
            second.LclTranslation.Set(new FbxDouble3(8, 4, -2));
            var (positions, triangles) = SimulatedImport(document.Meshes[0]);
            var tagged = new Dictionary<string, FbxChannelWrite.Tagged> {
                ["Rock_LOD0"] = new FbxChannelWrite.Tagged {
                    ordinal = 0, cornerPositions = positions, submeshCorners = new[] { triangles }, submeshFaceSizes = new[] { 3 }
                }
            };
            using var plan = new FbxStructurePlan();
            var paths = new[] { "Group[0]/Rock_LOD0[0]", sameParent ? "Group[0]/Rock_LOD0[1]" : "OtherGroup[0]/Rock_LOD0[0]" };
            foreach (string path in paths)
            {
                string key = CollisionHierarchyAnalyzer.InstanceKey("Rock_LOD0", path);
                var mesh = new UnityEngine.Mesh();
                var vertices = new UnityEngine.Vector3[positions.Length / 3];
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = new UnityEngine.Vector3(positions[i*3], positions[i*3+1], positions[i*3+2]);
                mesh.vertices = vertices; mesh.triangles = triangles.Take(3).ToArray();
                plan.collisions.Add((key, new List<UnityEngine.Mesh> { mesh }, false));
                plan.collisionSources[key] = new CollisionMeshEntry { meshGroupKey = key, sourceMeshName = "Rock_LOD0", sourceHierarchyPath = path,
                    replacedMeshGroupKeys = migrate ? new[] { "Rock_LOD0", "PreviousInstance", key } : null };
            }
            if (migrate)
            {
                var oldScoped = FbxNode.Create(scene, "PreviousInstance_COL");
                second.GetParent().AddChild(oldScoped);
                var oldHull = FbxNode.Create(scene, "PreviousInstance_COL_Hull0");
                oldScoped.AddChild(oldHull);
            }
            Assert.AreEqual(migrate ? 4 : 2, FbxStructureWrite.Apply(document, tagged, plan,
                new FbxStructureWrite.Options { preserveHierarchy = true }, new List<string>()));
            if (migrate)
            {
                Assert.IsEmpty(FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL"));
                Assert.IsEmpty(FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL_Hull0"));
                Assert.IsEmpty(FbxStructureEdit.FindNodes(scene, "PreviousInstance_COL"));
                Assert.IsEmpty(FbxStructureEdit.FindNodes(scene, "PreviousInstance_COL_Hull0"));
            }
            var sourceNodes = new[] { first, second };
            for (int i = 0; i < 2; i++)
            {
                var collision = FbxStructureEdit.FindNodes(scene, plan.collisions[i].key + "_COL").Single();
                Assert.IsTrue(FbxStructureEdit.SamePlacement(sourceNodes[i], collision, true));
                Assert.AreEqual(sourceNodes[i].GetParent(), collision.GetParent());
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void Structure_UnchangedCollisionStillChecksItsSourcePath(int sourceEdit)
        {
            using var document = FbxSourceDocument.Load(WriteSource());
            var scene = document.Scene;
            var source = FbxStructureEdit.FindNodes(scene, "Rock_LOD0").Single();
            var collision = FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL").Single();
            var (positions, triangles) = SimulatedImport(document.Meshes[0]);
            var tagged = new Dictionary<string, FbxChannelWrite.Tagged> {
                ["Rock_LOD0"] = new FbxChannelWrite.Tagged {
                    ordinal = 0, cornerPositions = positions, submeshCorners = new[] { triangles }, submeshFaceSizes = new[] { 3 }
                },
                ["Rock_LOD0_COL"] = new FbxChannelWrite.Tagged {
                    cornerPositions = positions, submeshCorners = new[] { triangles.Take(3).ToArray() }, submeshFaceSizes = new[] { 3 }
                }
            };
            using var plan = new FbxStructurePlan();
            var mesh = new UnityEngine.Mesh();
            var vertices = new UnityEngine.Vector3[positions.Length / 3];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new UnityEngine.Vector3(positions[i*3], positions[i*3+1], positions[i*3+2]);
            mesh.vertices = vertices; mesh.triangles = triangles.Take(3).ToArray();
            plan.collisions.Add(("Rock_LOD0", new List<UnityEngine.Mesh> { mesh }, false));
            plan.collisionSources["Rock_LOD0"] = new CollisionMeshEntry {
                sourceMeshName = "Rock_LOD0", sourceHierarchyPath = "Group[0]/Rock_LOD0[0]"
            };
            if (sourceEdit == 4 || sourceEdit == 5)
            {
                var wrapper = FbxNode.Create(scene, "ImportedWrapper");
                var group = source.GetParent();
                group.GetParent().RemoveChild(group);
                wrapper.AddChild(group); scene.GetRootNode().AddChild(wrapper);
                plan.collisionSources["Rock_LOD0"].sourceDocumentHierarchyPath =
                    FbxStructureWrite.ImportedCollisionDocumentPath(document, "Group[0]/Rock_LOD0[0]");
                Assert.AreEqual("ImportedWrapper[0]/Group[0]/Rock_LOD0[0]", plan.collisionSources["Rock_LOD0"].sourceDocumentHierarchyPath);
            }
            if (sourceEdit == 1) source.SetName("RenamedRock");
            if (sourceEdit == 2)
            {
                var parent = FbxNode.Create(scene, "OtherGroup");
                scene.GetRootNode().AddChild(parent);
                source.GetParent().RemoveChild(source);
                parent.AddChild(source);
            }
            if (sourceEdit == 3 || sourceEdit == 5)
            {
                var parent = FbxNode.Create(scene, "NewAncestor");
                scene.GetRootNode().AddChild(parent);
                var group = source.GetParent();
                group.GetParent().RemoveChild(group);
                parent.AddChild(group);
            }
            var log = new List<string>();
            if (sourceEdit == 0 || sourceEdit == 4)
                Assert.AreEqual(0, FbxStructureWrite.Apply(document, tagged, plan,
                    new FbxStructureWrite.Options { preserveHierarchy = true }, log));
            else
            {
                var error = Assert.Throws<FbxStructureRefusalException>(() => FbxStructureWrite.Apply(document, tagged, plan,
                    new FbxStructureWrite.Options { preserveHierarchy = true }, log));
                StringAssert.Contains("has no source node", error.Message);
            }
            Assert.IsEmpty(log, "source validation happens before document changes");
            Assert.AreEqual(collision, FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL").Single());
        }

        [Test]
        public void Structure_CollisionReplaceRefusesUnrelatedChildrenBeforeRemovingTheOldNode()
        {
            // The `{key}_COL` node a collision write replaces is audited like a superseded one: a
            // child that is not this tool's collision refuses the save before the document changes.
            using var document = FbxSourceDocument.Load(WriteSource());
            var scene = document.Scene;
            var old = FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL").Single();
            old.AddChild(FbxNode.Create(scene, "AuthoredMarker"));
            var (positions, triangles) = SimulatedImport(document.Meshes[0]);
            var tagged = new Dictionary<string, FbxChannelWrite.Tagged> {
                ["Rock_LOD0"] = new FbxChannelWrite.Tagged {
                    ordinal = 0, cornerPositions = positions, submeshCorners = new[] { triangles }, submeshFaceSizes = new[] { 3 }
                }
            };
            using var plan = new FbxStructurePlan();
            var mesh = new UnityEngine.Mesh();
            var vertices = new UnityEngine.Vector3[positions.Length / 3];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new UnityEngine.Vector3(positions[i*3], positions[i*3+1], positions[i*3+2]);
            mesh.vertices = vertices; mesh.triangles = triangles.Take(3).ToArray();
            plan.collisions.Add(("Rock_LOD0", new List<UnityEngine.Mesh> { mesh }, false));
            plan.collisionSources["Rock_LOD0"] = new CollisionMeshEntry { sourceMeshName = "Rock_LOD0", sourceHierarchyPath = "Group[0]/Rock_LOD0[0]" };
            var error = Assert.Throws<FbxStructureRefusalException>(() => FbxStructureWrite.Apply(document, tagged, plan,
                new FbxStructureWrite.Options { preserveHierarchy = true }, new List<string>()));
            StringAssert.Contains("AuthoredMarker", error.Message);
            Assert.AreEqual(1, FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL").Count);
            Assert.AreEqual(1, FbxStructureEdit.FindNodes(scene, "AuthoredMarker").Count);
            Assert.AreEqual(1, FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL_Hull0").Count, "the old hull stays with its container");
        }

        [Test]
        public void Structure_MigrationRefusesUnrelatedChildrenBeforeRemovingOldCollision()
        {
            using var document = FbxSourceDocument.Load(WriteSource());
            var scene = document.Scene;
            var old = FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL").Single();
            old.AddChild(FbxNode.Create(scene, "AuthoredMarker"));
            var (positions, triangles) = SimulatedImport(document.Meshes[0]);
            var tagged = new Dictionary<string, FbxChannelWrite.Tagged> {
                ["Rock_LOD0"] = new FbxChannelWrite.Tagged {
                    ordinal = 0, cornerPositions = positions, submeshCorners = new[] { triangles }, submeshFaceSizes = new[] { 3 }
                }
            };
            string key = CollisionHierarchyAnalyzer.InstanceKey("Rock_LOD0", "Group[0]/Rock_LOD0[0]");
            using var plan = new FbxStructurePlan();
            var mesh = new UnityEngine.Mesh();
            var vertices = new UnityEngine.Vector3[positions.Length / 3];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new UnityEngine.Vector3(positions[i*3], positions[i*3+1], positions[i*3+2]);
            mesh.vertices = vertices; mesh.triangles = triangles.Take(3).ToArray();
            plan.collisions.Add((key, new List<UnityEngine.Mesh> { mesh }, false));
            plan.collisionSources[key] = new CollisionMeshEntry { sourceMeshName = "Rock_LOD0", sourceHierarchyPath = "Group[0]/Rock_LOD0[0]",
                replacedMeshGroupKeys = new[] { "Rock_LOD0" } };
            var error = Assert.Throws<FbxStructureRefusalException>(() => FbxStructureWrite.Apply(document, tagged, plan,
                new FbxStructureWrite.Options { preserveHierarchy = true }, new List<string>()));
            StringAssert.Contains("AuthoredMarker", error.Message);
            Assert.AreEqual(1, FbxStructureEdit.FindNodes(scene, "Rock_LOD0_COL").Count);
            Assert.AreEqual(1, FbxStructureEdit.FindNodes(scene, "AuthoredMarker").Count);
            Assert.IsEmpty(FbxStructureEdit.FindNodes(scene, key + "_COL"));
        }

        [Test]
        public void Structure_ANewLodReimportsAsTheUnityMeshItWasMadeFrom()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "edited.fbx");
            string[] expected;
            using (var document = FbxSourceDocument.Load(source))
            {
                var lod0 = document.Meshes[0];
                var topology = new FbxLayerChannels.Topology(lod0);
                var (positions, triangles) = SimulatedImport(lod0);
                // The tag pairs every imported vertex with its corner; here vertex = corner.
                int relation = FbxSpaceFit.WindingRelation(topology.polygonSizes, triangles);
                Assert.AreEqual(-1, relation);
                var fit = FbxSpaceFit.FitCorners(FbxStructureEdit.ControlPoints(lod0), topology.cornerControlPoint, positions, relation, out string error);
                Assert.IsNotNull(fit, error);

                // The "generated LOD": the import's own triangles, one dropped, one point moved.
                var lodTriangles = triangles.Take(3).ToArray();
                var lodPositions = (float[])positions.Clone();
                lodPositions[lodTriangles[2] * 3 + 1] += 0.25f;
                var data = FbxMeshData.FromTriangles(new FbxMeshData.Source { positions = lodPositions, submeshTriangles = new[] { lodTriangles } },
                    fit, relation < 0, new[] { 0 });
                var node = FbxStructureEdit.AddSibling(FbxStructureEdit.NodeOf(lod0, "Rock_LOD0"), "Rock_LOD1", true);
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(document.Scene, "Rock_LOD1", data));
                document.Save(edited);
                expected = TriangleKeys(lodPositions, lodTriangles);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var lod1 = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD1").Single().GetMesh();
                var (positions, triangles) = SimulatedImport(lod1);
                var actual = TriangleKeys(positions, triangles);
                Assert.AreEqual(expected.Length, actual.Length);
                // Kept points come back bit for bit; the moved one through the inverse map.
                for (int i = 0; i < expected.Length; i++)
                {
                    var e = expected[i].Split('|', ',').Select(float.Parse).ToArray();
                    var a = actual[i].Split('|', ',').Select(float.Parse).ToArray();
                    for (int k = 0; k < e.Length; k++) Assert.AreEqual(e[k], a[k], 1e-6f, "same triangle, same orientation");
                }
            }
        }

        [Test]
        public void CreateMesh_PutsTheTangentFrameOnLayerZero()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "edited.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                var data = Triangle(0);
                data.tangents = new double[] { 1, 0, 0, 1, 0, 0, 1, 0, 0 };
                data.binormals = new double[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 };
                var node = FbxStructureEdit.AddSibling(FbxStructureEdit.NodeOf(document.Meshes[0], "Rock_LOD0"), "Rock_LOD1", true);
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(document.Scene, "Rock_LOD1", data));
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var layer = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD1").Single().GetMesh().GetLayer(0);
                Assert.IsNotNull(layer.GetTangents());
                Assert.IsNotNull(layer.GetBinormals());
            }
        }

        [TestCase("FBX binary (*.fbx)", "FBX201400")]
        [TestCase("FBX binary (*.fbx)", "FBX202000")]
        [TestCase("FBX ascii (*.fbx)", "FBX201400")]
        public void UvSetNames_AreReadFromTheFile(string writer, string version)
        {
            string path = Path.Combine(folder, "names.fbx");
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(io);
                var scene = FbxScene.Create(manager, "Names");
                var data = Triangle(0);
                data.polygonMaterials = null;
                data.uvs.Add(new double[] { 0, 0, 1, 0, 0, 1 });
                data.uvs.Add(new double[] { 0, 0, 0.5, 0, 0, 0.5 });
                data.uvNames.Add("map1");
                data.uvNames.Add("lightmap");
                foreach (string name in new[] { "Plane", "Twin", "Twin" })
                {
                    var node = FbxNode.Create(scene, name);
                    node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, name, data));
                    scene.GetRootNode().AddChild(node);
                }
                using var exporter = FbxExporter.Create(manager, "Write");
                int format = manager.GetIOPluginRegistry().FindWriterIDByDescription(writer);
                Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.SetFileExportVersion(version) && exporter.Export(scene));
            }
            var names = FbxUvSetNames.Read(path);
            CollectionAssert.AreEqual(new[] { "map1", "lightmap" }, names["Plane"]);
            Assert.IsFalse(names.ContainsKey("Twin"), "two meshes of one name cannot be told apart");
            using var document = FbxSourceDocument.Load(path);
            CollectionAssert.AreEqual(new[] { "map1", "lightmap" }, document.UvSetNames("Plane"));
            Assert.That(document.HasSmoothing("Plane"), Is.False, "the mesh has no smoothing element");
            Assert.IsNull(document.HasSmoothing("Twin"), "a shared name is unknown");
        }

        [Test]
        public void HasSmoothing_SeesTheSmoothingElement()
        {
            string path = Path.Combine(folder, "smoothing.fbx");
            File.WriteAllText(path, string.Join("\n",
                "; FBX 7.4.0 project file",
                "Objects:  {",
                "\tGeometry: 1, \"Geometry::Smooth\", \"Mesh\" {",
                "\t\tLayerElementSmoothing: 0 {",
                "\t\t\tMappingInformationType: \"ByPolygon\"",
                "\t\t}",
                "\t\tLayerElementUV: 0 {",
                "\t\t\tName: \"map1\"",
                "\t\t}",
                "\t}",
                "\tGeometry: 2, \"Geometry::Flat\", \"Mesh\" {",
                "\t\tLayerElementUV: 0 {",
                "\t\t\tName: \"UVMap\"",
                "\t\t}",
                "\t}",
                "}"));
            var (names, smoothing) = FbxUvSetNames.ReadLayers(path);
            Assert.IsTrue(smoothing["Smooth"]);
            Assert.IsFalse(smoothing["Flat"]);
            CollectionAssert.AreEqual(new[] { "map1" }, names["Smooth"], "the smoothing element does not hide the UV set after it");
        }

        [Test]
        public void HasTransformAnimation_SeesCurvesThatMoveTheTransform()
        {
            string path = Path.Combine(folder, "animated.fbx");
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(io);
                var scene = FbxScene.Create(manager, "Animated");
                var stack = FbxAnimStack.Create(scene, "Take");
                var layer = FbxAnimLayer.Create(scene, "Base");
                stack.AddMember(layer);
                scene.SetCurrentAnimationStack(stack);
                foreach (var (name, second) in new[] { ("Still", 0f), ("Keyed", 0f), ("Moving", 5f) })
                {
                    var node = FbxNode.Create(scene, name);
                    scene.GetRootNode().AddChild(node);
                    if (name == "Still") continue;
                    // Every exporter keys X at its value; only Moving leaves it.
                    node.LclTranslation.GetCurveNode(layer, true);
                    var curve = node.LclTranslation.GetCurve(layer, "X", true);
                    curve.KeyModifyBegin();
                    curve.KeySet(curve.KeyAdd(FbxTime.FromSecondDouble(0)), FbxTime.FromSecondDouble(0), 0f);
                    curve.KeySet(curve.KeyAdd(FbxTime.FromSecondDouble(1)), FbxTime.FromSecondDouble(1), second);
                    curve.KeyModifyEnd();
                }
                using var exporter = FbxExporter.Create(manager, "Write");
                int format = manager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
                Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.Export(scene));
            }
            using var document = FbxSourceDocument.Load(path);
            Assert.IsFalse(FbxStructureEdit.HasTransformAnimation(FbxStructureEdit.FindNodes(document.Scene, "Still").Single()));
            Assert.IsFalse(FbxStructureEdit.HasTransformAnimation(FbxStructureEdit.FindNodes(document.Scene, "Keyed").Single()), "keys at the value");
            Assert.IsTrue(FbxStructureEdit.HasTransformAnimation(FbxStructureEdit.FindNodes(document.Scene, "Moving").Single()));
        }

        [Test]
        public void SetMaterial_ReplacesOneSlotAndLeavesTheOthersAndOtherNodes()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "edited.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                var scene = document.Scene;
                var rock = FbxStructureEdit.FindNodes(scene, "Rock_LOD0").Single();
                // Another node showing the same "Moss" material.
                var twin = FbxStructureEdit.AddSibling(rock, "Rock_Twin", true);
                FbxStructureEdit.AddMaterials(twin, rock, new[] { 1 });
                int materials = scene.GetMaterialCount();
                Assert.AreEqual("Stone", FbxStructureEdit.SceneMaterial(scene, "Stone").GetName());
                Assert.AreEqual(materials, scene.GetMaterialCount(), "a material of that name is reused, not duplicated");

                FbxStructureEdit.SetMaterial(rock, 1, FbxStructureEdit.SceneMaterial(scene, "Lichen"));
                Assert.Throws<FbxStructureRefusalException>(() => FbxStructureEdit.SetMaterial(rock, 0, FbxStructureEdit.SceneMaterial(scene, "Lichen")),
                    "one node cannot hold a material in two slots");
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var rock = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0").Single();
                Assert.AreEqual(2, rock.GetMaterialCount());
                Assert.AreEqual("Stone", rock.GetMaterial(0).GetName(), "slot 0 untouched");
                Assert.AreEqual("Lichen", rock.GetMaterial(1).GetName(), "slot 1 replaced in place");
                var twin = FbxStructureEdit.FindNodes(document.Scene, "Rock_Twin").Single();
                Assert.AreEqual("Moss", twin.GetMaterial(0).GetName(), "another node keeps the material it shared");
            }
        }

        [Test]
        public void NewMaterial_IsPhongWithItsDiffuseTextureAndBothPaths()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "textured.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                var scene = document.Scene;
                var rock = FbxStructureEdit.FindNodes(scene, "Rock_LOD0").Single();
                var bark = FbxStructureEdit.NewMaterial(scene, "Bark", 0.5, 0.25, 0.125, "T_Bark_Albedo",
                    "C:/Project/Assets/Textures/T_Bark_Albedo.png", "../Textures/T_Bark_Albedo.png", "map1");
                FbxStructureEdit.SetMaterial(rock, 1, bark);
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var rock = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0").Single();
                var bark = rock.GetMaterial(1);
                Assert.AreEqual("Bark", bark.GetName());
                Assert.IsTrue(bark.FindProperty(FbxSurfaceMaterial.sShininess).IsValid(), "a Phong material");
                var diffuse = bark.FindProperty(FbxSurfaceMaterial.sDiffuse);
                Assert.AreEqual(0.5, diffuse.GetFbxDouble3().X, 1e-9);
                Assert.AreEqual(0.25, diffuse.GetFbxDouble3().Y, 1e-9);
                Assert.AreEqual(1, diffuse.GetSrcObjectCount(), "a texture on the diffuse");
                Assert.AreEqual("T_Bark_Albedo", diffuse.GetSrcObject(0).GetName());
                Assert.AreEqual("map1", diffuse.GetSrcObject(0).FindProperty("UVSet").GetString(), "bound to the mesh's UV set");
            }
            // Texture paths are UTF-8 string records in binary FBX too. The wrapper does not
            // downcast imported texture objects, so inspect the exact stored filename bytes.
            string written = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(edited));
            StringAssert.Contains("C:/Project/Assets/Textures/T_Bark_Albedo.png", written, "absolute path");
        }

        [Test]
        public void SetMaterials_SwapsTwoSlotsInOneStep()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "swapped.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                var scene = document.Scene;
                var rock = FbxStructureEdit.FindNodes(scene, "Rock_LOD0").Single();
                FbxStructureEdit.SetMaterials(rock, new Dictionary<int, FbxSurfaceMaterial>
                    { [0] = FbxStructureEdit.SceneMaterial(scene, "Moss"), [1] = FbxStructureEdit.SceneMaterial(scene, "Stone") });
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var rock = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0").Single();
                Assert.AreEqual(2, rock.GetMaterialCount());
                Assert.AreEqual("Moss", rock.GetMaterial(0).GetName());
                Assert.AreEqual("Stone", rock.GetMaterial(1).GetName());
            }
        }

        [Test]
        public void ChildrenNamed_LooksOnlyUnderTheGivenParent()
        {
            using var document = FbxSourceDocument.Load(WriteSource());
            var group = FbxStructureEdit.FindNodes(document.Scene, "Group").Single();
            Assert.AreEqual(1, FbxStructureEdit.ChildrenNamed(group, "Rock_LOD0").Count);
            Assert.IsEmpty(FbxStructureEdit.ChildrenNamed(document.Scene.GetRootNode(), "Rock_LOD0"), "a same-named node in another branch is not this parent's");
        }

        [Test]
        public void RemoveSubtree_TakesTheReplacedNodeAndItsMeshesOnly()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "edited.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                Assert.AreEqual(2, document.Meshes.Count);
                FbxStructureEdit.RemoveSubtree(FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0_COL").Single());
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                Assert.AreEqual(1, document.Meshes.Count);
                Assert.IsEmpty(FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0_COL"));
                Assert.IsEmpty(FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0_COL_Hull0"));
                Assert.AreEqual(1, FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0").Count);
            }
        }

        [Test]
        public void ReplaceMesh_KeepsTheNodeAndItsMaterials()
        {
            string source = WriteSource(), edited = Path.Combine(folder, "edited.fbx");
            using (var document = FbxSourceDocument.Load(source))
            {
                var old = document.Meshes[0];
                Assert.AreEqual(1, FbxStructureEdit.ReplaceMesh(old, FbxStructureEdit.CreateMesh(document.Scene, "Rock_LOD0", Triangle(0))));
                document.Save(edited);
            }
            using (var document = FbxSourceDocument.Load(edited))
            {
                var node = FbxStructureEdit.FindNodes(document.Scene, "Rock_LOD0").Single();
                Assert.AreEqual(3, node.GetMesh().GetPolygonSize(0));
                Assert.AreEqual(2, node.GetMaterialCount());
                Assert.AreEqual(2, document.Meshes.Count, "the old mesh is gone, the hull stays");
            }
        }
#endif
    }
}
