using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvTransferInputTests
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        string folder, path;
        GameObject root;
        UvToolContext ctx;
        UvTransferWorkflow workflow;
        Vector3[] positions;
        Vector2[] uv;
        byte[] fileBytes;

        [SetUp]
        public void CreateImportedFbx()
        {
            folder = "Assets/MeshLabUvInput_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            path = folder + "/Input.fbx";
            File.WriteAllText(path, Fbx);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            SetImport(ModelImporterMeshCompression.Off, false, false);
            var mesh = Imported();
            positions = Corners(mesh); uv = UvCorners(mesh);
            fileBytes = File.ReadAllBytes(path);
            root = new GameObject("UV input test");
            var filter = root.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(.1f, new Renderer[] { renderer }) });
            ctx = new UvToolContext { LodGroup = group };
            ctx.MeshEntries.Add(new MeshEntry { fbxMesh = mesh, originalMesh = mesh, meshFilter = filter, renderer = renderer });
            workflow = new UvTransferWorkflow();
            typeof(UvTransferWorkflow).GetField("ctx", Private).SetValue(workflow, ctx);
        }

        [TearDown]
        public void Cleanup()
        {
            if (ctx != null)
                foreach (var entry in ctx.MeshEntries)
                {
                    if (entry.repackedMesh) Object.DestroyImmediate(entry.repackedMesh);
                    if (entry.transferredMesh) Object.DestroyImmediate(entry.transferredMesh);
                    if (entry.originalMesh && entry.originalMesh != entry.fbxMesh) Object.DestroyImmediate(entry.originalMesh);
                }
            if (root) Object.DestroyImmediate(root);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        void SetImport(ModelImporterMeshCompression compression, bool optimize, bool weld)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.meshCompression = compression;
            importer.meshOptimizationFlags = optimize ? MeshOptimizationFlags.Everything : 0;
            importer.weldVertices = weld;
            importer.isReadable = true;
            importer.generateSecondaryUV = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.SaveAndReimport();
        }

        Mesh Imported() => AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single();
        static Vector3[] Corners(Mesh mesh) => mesh.triangles.Select(i => mesh.vertices[i]).ToArray();
        static Vector2[] UvCorners(Mesh mesh) => mesh.triangles.Select(i => mesh.uv[i]).ToArray();

        void AssertFresh()
        {
            var entry = ctx.MeshEntries[0];
            var imported = Imported();
            Assert.AreSame(imported, entry.fbxMesh);
            CollectionAssert.AreEqual(positions, Corners(imported));
            CollectionAssert.AreEqual(uv, UvCorners(imported));
            CollectionAssert.AreEqual(fileBytes, File.ReadAllBytes(path), "Preparation must not rewrite the FBX.");
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            Assert.AreEqual(ModelImporterMeshCompression.Off, importer.meshCompression);
            Assert.AreEqual(0, (int)importer.meshOptimizationFlags);
            Assert.IsFalse(importer.weldVertices);
            Assert.IsTrue(importer.isReadable);
            Assert.AreEqual(ModelImporterNormals.Import, importer.importNormals);
            Assert.AreEqual(ModelImporterTangents.CalculateMikk, importer.importTangents);
        }

        [TestCase(ModelImporterMeshCompression.Off)]
        [TestCase(ModelImporterMeshCompression.Low)]
        [TestCase(ModelImporterMeshCompression.Medium)]
        [TestCase(ModelImporterMeshCompression.High)]
        public void PreparationRestoresUncompressedFbxCornersAndInvalidatesOwnedResults(ModelImporterMeshCompression compression)
        {
            SetImport(compression, true, true);
            var entry = ctx.MeshEntries[0];
            var working = entry.originalMesh = Object.Instantiate(entry.fbxMesh);
            var packed = entry.repackedMesh = Object.Instantiate(working);
            var transferred = entry.transferredMesh = Object.Instantiate(working);
            entry.meshFilter.sharedMesh = transferred;
            entry.wasEdgeWelded = entry.wasSymmetrySplit = true;
            entry.repackedAtlasWidth = 512;
            ctx.HasRepack = ctx.HasTransfer = true;
            Assert.IsTrue(workflow.PrepareTransferInputs());
            AssertFresh();
            Assert.IsFalse(working); Assert.IsFalse(packed); Assert.IsFalse(transferred);
            Assert.AreSame(entry.fbxMesh, entry.originalMesh);
            Assert.AreSame(entry.fbxMesh, entry.meshFilter.sharedMesh);
            Assert.IsFalse(entry.wasEdgeWelded || entry.wasSymmetrySplit || ctx.HasRepack || ctx.HasTransfer);
            Assert.AreEqual(0, entry.repackedAtlasWidth);
            var freshCopy = entry.originalMesh = MeshAccess.ReadableCopy(entry.fbxMesh);
            Assert.IsFalse(workflow.PrepareTransferInputs(), "Clean imports must preserve completed work.");
            Assert.AreSame(freshCopy, entry.originalMesh);
        }

        [Test]
        public void HighCompressionChangesCornersAndPreparationRestoresTheirPrecision()
        {
            SetImport(ModelImporterMeshCompression.High, false, false);
            var compressed = Imported();
            double positionError = Corners(compressed).Zip(positions, (a, b) => (double)(a - b).magnitude).Max();
            double uvError = UvCorners(compressed).Zip(uv, (a, b) => (double)(a - b).magnitude).Max();
            Assert.Greater(Math.Max(positionError, uvError), 1e-6, "This fixture must actually exercise import quantization.");
            Debug.Log($"UV input compression errors: position={positionError:R}, UV0={uvError:R}");
            Assert.IsTrue(workflow.PrepareTransferInputs());
            AssertFresh();
        }

        [Test]
        public void AnalyzeUsesAReadableFreshCopyWithoutChangingReadWriteSetting()
        {
            SetImport(ModelImporterMeshCompression.High, true, true);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false; importer.SaveAndReimport();
            typeof(UvTransferWorkflow).GetMethod("ExecAnalyzeUv0", Private).Invoke(workflow, null);
            var entry = ctx.MeshEntries[0];
            Assert.IsTrue(entry.originalMesh.isReadable);
            Assert.AreNotSame(entry.fbxMesh, entry.originalMesh);
            CollectionAssert.AreEqual(positions, Corners(entry.originalMesh));
            CollectionAssert.AreEqual(uv, UvCorners(entry.originalMesh));
            Assert.IsFalse(((ModelImporter)AssetImporter.GetAtPath(path)).isReadable);
            Assert.IsFalse(Uv2AssetPostprocessor.bypassPaths.Contains(path));
        }

        [Test]
        public void ExcludedSubassetOfPreparedFileIsReboundWithoutLosingEntrySelection()
        {
            var entry = ctx.MeshEntries[0];
            var excluded = new MeshEntry { fbxMesh = entry.fbxMesh, originalMesh = entry.fbxMesh, include = false, lodIndex = 2 };
            ctx.MeshEntries.Add(excluded);
            ctx.PreviewLod = 2;
            SetImport(ModelImporterMeshCompression.High, false, false);
            Assert.IsTrue(workflow.PrepareTransferInputs());
            Assert.AreSame(entry, ctx.MeshEntries[0]);
            Assert.AreSame(Imported(), excluded.fbxMesh);
            Assert.AreSame(excluded.fbxMesh, excluded.originalMesh);
            Assert.IsFalse(excluded.include);
            Assert.AreEqual(2, ctx.PreviewLod);
        }

        [Test]
        public void ExcludedFileDoesNotChangeImporterOrWorkingMesh()
        {
            SetImport(ModelImporterMeshCompression.High, true, true);
            var entry = ctx.MeshEntries[0]; entry.include = false;
            var working = entry.originalMesh = Object.Instantiate(entry.fbxMesh);
            Assert.IsFalse(workflow.PrepareTransferInputs());
            Assert.AreSame(working, entry.originalMesh);
            Assert.AreEqual(ModelImporterMeshCompression.High, ((ModelImporter)AssetImporter.GetAtPath(path)).meshCompression);
        }

        [TestCase("ExecAnalyzeUv0")]
        [TestCase("ExecMeshOptimize")]
        [TestCase("ExecWeldUv0")]
        [TestCase("ExecSymmetrySplit")]
        [TestCase("ExecRepackImpl")]
        public void IndependentStagePreparesInputsBeforeReadingGeometry(string method)
        {
            SetImport(ModelImporterMeshCompression.High, true, true);
            object[] args;
            switch (method)
            {
                case "ExecWeldUv0": args = new object[] { false }; break;
                case "ExecSymmetrySplit": args = new object[] { true, .1f }; break;
                case "ExecRepackImpl": args = new object[] { ctx.MeshEntries, false }; break;
                default: args = Array.Empty<object>(); break;
            }
            var result = typeof(UvTransferWorkflow).GetMethod(method, Private).Invoke(workflow, args);
            if (result is Task task) task.GetAwaiter().GetResult();
            AssertFresh();
        }

        [TestCase("ExecTransferAllImpl", false)]
        [TestCase("ExecTransferLodImpl", true)]
        public void TransferStopsAfterReimportInsteadOfReusingAStaleAtlas(string method, bool singleLod)
        {
            SetImport(ModelImporterMeshCompression.High, false, false);
            var packed = ctx.MeshEntries[0].repackedMesh = Object.Instantiate(ctx.MeshEntries[0].fbxMesh);
            ctx.HasRepack = true;
            var args = singleLod ? new object[] { 1, false } : new object[] { false };
            var task = (Task)typeof(UvTransferWorkflow).GetMethod(method, Private).Invoke(workflow, args);
            var error = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("Run Full Pipeline", error.Message);
            Assert.IsFalse(packed); Assert.IsFalse(ctx.HasTransfer || ctx.HasRepack);
            AssertFresh();
        }

        [Test]
        public void FullPipelinePreparesCompressedSourceAndCompletesRepack()
        {
            SetImport(ModelImporterMeshCompression.High, true, true);
            ctx.AtlasResolution = 128;
            var task = (Task)typeof(UvTransferWorkflow).GetMethod("ExecFullPipelineImpl", Private)
                .Invoke(workflow, new object[] { "Compressed FBX regression", false });
            task.GetAwaiter().GetResult();
            AssertFresh();
            Assert.IsTrue(ctx.HasRepack);
            Assert.IsNotNull(ctx.MeshEntries[0].repackedMesh);
        }

        // A real ASCII FBX fixture, independent of the optional FBX Exporter package.
        const string Fbx = @"; FBX 7.4.0 project file
FBXHeaderExtension: {
    FBXHeaderVersion: 1003
    FBXVersion: 7400
}
GlobalSettings: {
    Version: 1000
    Properties70: {
        P: ""UpAxis"", ""int"", ""Integer"", """", 1
        P: ""UpAxisSign"", ""int"", ""Integer"", """", 1
        P: ""FrontAxis"", ""int"", ""Integer"", """", 2
        P: ""FrontAxisSign"", ""int"", ""Integer"", """", -1
        P: ""CoordAxis"", ""int"", ""Integer"", """", 0
        P: ""CoordAxisSign"", ""int"", ""Integer"", """", 1
        P: ""UnitScaleFactor"", ""double"", ""Number"", """", 100
    }
}
Definitions: {
    Version: 100
    Count: 2
    ObjectType: ""Model"" {
        Count: 1
    }
    ObjectType: ""Geometry"" {
        Count: 1
    }
}
Objects: {
    Geometry: 100, ""Geometry::Part_LOD0"", ""Mesh"" {
        Vertices: *9 {
            a: 0.123456789,0.234567891,0,1.135791113,0.345678912,0,0.246813579,1.987654321,0
        }
        PolygonVertexIndex: *3 {
            a: 0,1,-3
        }
        GeometryVersion: 124
        LayerElementNormal: 0 {
            Version: 101
            Name: """"
            MappingInformationType: ""ByPolygonVertex""
            ReferenceInformationType: ""Direct""
            Normals: *9 {
                a: 0,0,1,0,0,1,0,0,1
            }
        }
        LayerElementUV: 0 {
            Version: 101
            Name: ""map1""
            MappingInformationType: ""ByPolygonVertex""
            ReferenceInformationType: ""Direct""
            UV: *6 {
                a: 0.123456789,0.234567891,0.987654321,0.345678912,0.246813579,0.876543219
            }
        }
        Layer: 0 {
            Version: 100
            LayerElement: {
                Type: ""LayerElementNormal""
                TypedIndex: 0
            }
            LayerElement: {
                Type: ""LayerElementUV""
                TypedIndex: 0
            }
        }
    }
    Model: 200, ""Model::Part_LOD0"", ""Mesh"" {
        Version: 232
        Properties70: {
            P: ""Lcl Translation"", ""Lcl Translation"", """", ""A"",0,0,0
            P: ""Lcl Rotation"", ""Lcl Rotation"", """", ""A"",0,0,0
            P: ""Lcl Scaling"", ""Lcl Scaling"", """", ""A"",1,1,1
        }
        Shading: T
        Culling: ""CullingOff""
    }
}
Connections: {
    C: ""OO"",100,200
    C: ""OO"",200,0
}
";
    }
}
