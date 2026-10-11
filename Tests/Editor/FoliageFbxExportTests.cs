using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System.Linq;
using Autodesk.Fbx;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageFbxExportTests
    {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        string folder;

        [SetUp]
        public void SetUp()
        {
            string name = "FoliageFbx_" + Guid.NewGuid().ToString("N");
            folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(folder);

        string Source(bool coincident, bool binary)
        {
            string path = folder + "/tree.fbx";
            using var manager = FbxManager.Create();
            var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
            manager.SetIOSettings(io);
            var scene = FbxScene.Create(manager, "TreeDocument");
            var data = new FbxMeshData
            {
                controlPoints = new double[] { 0,0,0, 100,0,0, 100,100,0, 0,100,0, 0,0,100 },
                polygonSizes = coincident ? new[] {3,3} : new[] {4,3},
                polygonVertices = coincident ? new[] {0,1,2,0,1,2} : new[] {0,1,2,3,0,1,4},
                polygonMaterials = new[] {0,1}
            };
            int count = data.CornerCount;
            data.normals = new double[count * 3];
            data.tangents = new double[count * 3];
            data.binormals = new double[count * 3];
            data.colors = new double[count * 4];
            for (int c = 0; c < count; c++)
            {
                int cp = data.polygonVertices[c];
                data.normals[c*3+2] = 1; data.tangents[c*3] = 1; data.binormals[c*3+1] = 1;
                data.colors[c*4] = .123456789 + cp*.01; data.colors[c*4+1] = .234567891;
                data.colors[c*4+2] = .345678912; data.colors[c*4+3] = .876543219;
            }
            double[,] uv = {{-.25,.1},{1.25,.1},{1.25,1.1},{-.25,1.1},{.2,.8}};
            for (int channel = 0; channel < 8; channel++)
            {
                var values = new double[count*2];
                for (int c = 0; c < count; c++)
                {
                    int cp = data.polygonVertices[c];
                    values[c*2] = uv[cp,0] + channel*.03123456789;
                    values[c*2+1] = uv[cp,1] + channel*.04345678912;
                }
                data.uvs.Add(values); data.uvNames.Add("map" + channel);
            }
            var group = FbxNode.Create(scene, "AuthoredRoot");
            group.LclRotation.Set(new FbxDouble3(-90, 0, 0));
            scene.GetRootNode().AddChild(group);
            var node = FbxNode.Create(scene, "Tree_LOD0");
            node.LclTranslation.Set(new FbxDouble3(1,2,3));
            node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, "Tree_LOD0", data));
            node.AddMaterial(FbxSurfacePhong.Create(scene,"M_Trunk"));
            node.AddMaterial(FbxSurfacePhong.Create(scene,"M_Leaves"));
            group.AddChild(node);
            var helper = FbxNode.Create(scene, "KeepHelper");
            helper.LclTranslation.Set(new FbxDouble3(4,5,6));
            group.AddChild(helper);
            using var exporter = FbxExporter.Create(manager, "Write");
            int format = manager.GetIOPluginRegistry().FindWriterIDByDescription(binary ? "FBX binary (*.fbx)" : "FBX ascii (*.fbx)");
            Assert.IsTrue(exporter.Initialize(path, format, io) && exporter.SetFileExportVersion("FBX201400") && exporter.Export(scene));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false; importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.Import; importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.preserveHierarchy = true; importer.generateSecondaryUV = false;
            importer.SaveAndReimport();
            return path;
        }

        [TestCase(false, true)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        public void SeparateFbxPreservesSourceDocumentAndExcludedNormalsAfterReimport(bool coincident, bool binary)
        {
            string source = Source(coincident, binary);
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source + ".meta");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            root.transform.localScale = new Vector3(.01f,.02f,-.03f);
            root.transform.localRotation = Quaternion.Euler(90,12,-4);
            var transforms = root.GetComponentsInChildren<Transform>();
            var matrices = transforms.Select(t => t.localToWorldMatrix).ToArray();
            var renderer = root.GetComponentInChildren<MeshRenderer>();
            var originalReference = renderer.GetComponent<MeshFilter>().sharedMesh;
            var materialReferences = renderer.sharedMaterials;
            FoliageTargetSnapshot target = null;
            try
            {
                var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] {1} };
                target = new FoliageTargetSnapshot(renderer, root.transform.worldToLocalMatrix, settings, CancellationToken.None);
                var normals = (Vector3[])target.localNormals.Clone();
                var desired = new Vector3(.3f,.5f,.8f).normalized;
                for (int i = 0; i < normals.Length; i++) if (target.selectedVertices[i]) normals[i] = desired;
                target.working = target.BuildResult(normals, true, CancellationToken.None, out int duplicates);
                Assert.Greater(duplicates,0,"Fixture must exercise selected/excluded vertex splitting.");
                string output = FoliageFbxExport.Save(new[] {target}, folder).Single();
                Assert.That(output, Does.EndWith("_FoliageNormals.fbx")); Assert.AreNotEqual(source, output);
                CheckDocuments(source, output);
                var sourceTag = FbxChannelWrite.ImportTagged(source, false, out _)[target.source.name];
                var resultTag = FbxChannelWrite.ImportTagged(output, false, out _)[target.source.name];
                CollectionAssert.AreEqual(sourceTag.cornerPositions, resultTag.cornerPositions);
                foreach (int corner in sourceTag.submeshCorners[0])
                    Assert.That(Vector3.Dot(sourceTag.normals[corner],resultTag.normals[corner]), Is.GreaterThan(.999999f));
                foreach (int corner in sourceTag.submeshCorners[1])
                    Assert.That(Vector3.Dot(desired,resultTag.normals[corner]), Is.GreaterThan(.999999f));
                for (int ch = 0; ch < 8; ch++) CollectionAssert.AreEqual(sourceTag.uvs[ch], resultTag.uvs[ch]);
                CollectionAssert.AreEqual(sourceTag.colors, resultTag.colors);
                var variantImporter = (ModelImporter)AssetImporter.GetAtPath(output);
                Assert.AreEqual(ModelImporterNormals.Import, variantImporter.importNormals);
                Assert.AreEqual(ModelImporterTangents.Import, variantImporter.importTangents);
                CollectionAssert.AreEqual(bytes,File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta,File.ReadAllBytes(source+".meta"));
                Assert.AreEqual(originalReference,renderer.GetComponent<MeshFilter>().sharedMesh);
                CollectionAssert.AreEqual(materialReferences,renderer.sharedMaterials);
                for (int i = 0; i < transforms.Length; i++) Assert.AreEqual(matrices[i],transforms[i].localToWorldMatrix);
                Assert.IsFalse(AssetDatabase.IsValidFolder("Assets/__MeshLabTemp"));
                Assert.IsFalse(File.Exists(output + ".tmp"));
                string second = FoliageFbxExport.Save(new[] {target},folder).Single();
                Assert.AreNotEqual(output,second); CheckDocuments(source,second);
            }
            finally { target?.Dispose(); Object.DestroyImmediate(root); }
        }

        internal static void CheckDocuments(string source, string output)
        {
            using var before = FbxSourceDocument.Load(source);
            using var after = FbxSourceDocument.Load(output);
            Assert.AreEqual(before.Binary,after.Binary); Assert.AreEqual(before.Major,after.Major); Assert.AreEqual(before.Minor,after.Minor);
            Assert.AreEqual(before.Meshes.Count,after.Meshes.Count);
            for (int i = 0; i < before.Meshes.Count; i++)
            {
                var a = before.Meshes[i]; var b = after.Meshes[i];
                var ta = new FbxLayerChannels.Topology(a); var tb = new FbxLayerChannels.Topology(b);
                Assert.AreEqual(a.GetName(),b.GetName());
                CollectionAssert.AreEqual(FbxStructureEdit.ControlPoints(a),FbxStructureEdit.ControlPoints(b));
                CollectionAssert.AreEqual(ta.polygonSizes,tb.polygonSizes); CollectionAssert.AreEqual(ta.cornerControlPoint,tb.cornerControlPoint);
                var materials = FbxStructureEdit.PolygonMaterials(a);
                CollectionAssert.AreEqual(materials,FbxStructureEdit.PolygonMaterials(b));
                var ua = FbxLayerChannels.UvElements(a); var ub = FbxLayerChannels.UvElements(b);
                Assert.AreEqual(ua.Count,ub.Count);
                CollectionAssert.AreEqual(before.UvSetNames(a.GetName()),after.UvSetNames(b.GetName()));
                for (int ch = 0; ch < ua.Count; ch++)
                {
                    Assert.AreEqual(ua[ch].GetMappingMode(),ub[ch].GetMappingMode());
                    Assert.AreEqual(ua[ch].GetReferenceMode(),ub[ch].GetReferenceMode());
                    CollectionAssert.AreEqual(FbxLayerChannels.ReadUv(ua[ch],ta),FbxLayerChannels.ReadUv(ub[ch],tb));
                }
                var ca = FbxLayerChannels.ColorElement(a); var cb = FbxLayerChannels.ColorElement(b);
                if (ca != null)
                {
                    Assert.AreEqual(ca.GetMappingMode(),cb.GetMappingMode()); Assert.AreEqual(ca.GetReferenceMode(),cb.GetReferenceMode());
                    CollectionAssert.AreEqual(FbxLayerChannels.ReadColor(ca,ta),FbxLayerChannels.ReadColor(cb,tb));
                }
                var na = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(a),ta);
                var nb = FbxLayerChannels.ReadVectors(FbxLayerChannels.NormalElement(b),tb);
                for (int c = 0; c < ta.CornerCount; c++)
                {
                    if (materials[ta.cornerPolygon[c]] != 0) continue;
                    for (int axis = 0; axis < 3; axis++) Assert.AreEqual(na[c*3+axis],nb[c*3+axis],"excluded FBX normal must stay exact");
                }
                CheckExcludedVectors(FbxLayerChannels.TangentElement(a),FbxLayerChannels.TangentElement(b),ta,tb,materials);
                CheckExcludedVectors(FbxLayerChannels.BinormalElement(a),FbxLayerChannels.BinormalElement(b),ta,tb,materials);
            }
            CheckNodes(before.Scene.GetRootNode(),after.Scene.GetRootNode());
        }

        static void CheckExcludedVectors(FbxLayerElementTemplateFbxVector4 a, FbxLayerElementTemplateFbxVector4 b,
            FbxLayerChannels.Topology ta, FbxLayerChannels.Topology tb, int[] materials)
        {
            if (a == null) return;
            Assert.IsNotNull(b);
            var before = FbxLayerChannels.ReadVectors(a,ta); var after = FbxLayerChannels.ReadVectors(b,tb);
            for (int c = 0; c < ta.CornerCount; c++)
            {
                if (materials[ta.cornerPolygon[c]] != 0) continue;
                for (int axis = 0; axis < 3; axis++) Assert.AreEqual(before[c*3+axis],after[c*3+axis],"excluded FBX tangent frame must stay exact");
            }
        }

        static void CheckNodes(FbxNode a, FbxNode b)
        {
            Assert.AreEqual(a.GetName(),b.GetName()); Assert.AreEqual(a.GetChildCount(),b.GetChildCount());
            Assert.AreEqual(a.LclTranslation.Get(),b.LclTranslation.Get());
            Assert.AreEqual(a.LclRotation.Get(),b.LclRotation.Get()); Assert.AreEqual(a.LclScaling.Get(),b.LclScaling.Get());
            Assert.AreEqual(a.GetMaterialCount(),b.GetMaterialCount());
            for (int i = 0; i < a.GetMaterialCount(); i++) Assert.AreEqual(a.GetMaterial(i).GetName(),b.GetMaterial(i).GetName());
            for (int i = 0; i < a.GetChildCount(); i++) CheckNodes(a.GetChild(i),b.GetChild(i));
        }

        [Test]
        public void CalculatedSourceNormalsAreRefusedWithoutChangingSourceOrCreatingAnOutput()
        {
            string source = Source(false,true);
            var importer = (ModelImporter)AssetImporter.GetAtPath(source);
            importer.importNormals = ModelImporterNormals.Calculate; importer.SaveAndReimport();
            var bytes = File.ReadAllBytes(source); var meta = File.ReadAllBytes(source+".meta");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(source));
            try
            {
                using var target = new FoliageTargetSnapshot(root.GetComponentInChildren<MeshRenderer>(),root.transform.worldToLocalMatrix,new FoliageNormalsSettings(),CancellationToken.None);
                var error = Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(new[] {target},folder));
                Assert.That(error.Message,Does.Contain("Normals = Import"));
                CollectionAssert.AreEqual(bytes,File.ReadAllBytes(source)); CollectionAssert.AreEqual(meta,File.ReadAllBytes(source+".meta"));
                Assert.AreEqual(1,Directory.GetFiles(folder,"*.fbx").Length);
            }
            finally { Object.DestroyImmediate(root); }
        }
#else
        [Test]
        public void MissingExporterProducesAnExplicitError()
        {
            Assert.IsFalse(FoliageFbxExport.Available);
            Assert.Throws<InvalidOperationException>(() => FoliageFbxExport.Save(Array.Empty<FoliageTargetSnapshot>(),"Assets"));
        }
#endif
    }
}
