using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FurnitureSourceQualityTests
    {
        /// <summary>Checks the measured furniture improvement on an imported copy in each source-preparation mode.</summary>
        [TestCase("off")] [TestCase("legacy")] [TestCase("adaptive")]
        public void PointJunctionRepairReducesFurnitureContacts(string symmetry)
        {
            string fixture = Environment.GetEnvironmentVariable("MESHLAB_FBX_SEAM_FIXTURE");
            if (string.IsNullOrEmpty(fixture)) Assert.Ignore("Optional furniture fixture is not configured.");
            string model = Path.GetFileNameWithoutExtension(fixture);
            bool shelf = model == "Modern_ShelvingUnit_A";
            if (!shelf && model != "Modern_Kitchen_A") Assert.Ignore("This regression uses the shelving or kitchen fixture.");
            byte[] original = File.ReadAllBytes(fixture);
            string folder = "Assets/FurnitureQuality_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            try
            {
                string path = folder + "/source.fbx";
                File.WriteAllBytes(path, original);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true; importer.generateSecondaryUV = false;
                importer.weldVertices = false; importer.SaveAndReimport();
                string sourceName = shelf ? "Modern_ShelvingUnit_Shelf_LOD0" : "Modern_Kitchen_A_Countertop_LOD0";
                var pair = TransferBenchmarkAssets.Pairs(new TransferBenchmarkAssets.Case {
                    asset = path, symmetry = symmetry, weldUv0 = true, preOptimize = true }).First(p => p.source.name == sourceName);
                using var prepared = TransferBenchmarkAssets.Prepare(pair);
                var sourceQuality = TransferUvQuality.Measure(prepared.source, prepared.source.uv2, Vector2.one, Matrix4x4.identity);
                var targetQuality = TransferUvQuality.Measure(prepared.target, prepared.reference, Vector2.one, Matrix4x4.identity);
                TestContext.WriteLine($"{sourceName}/{symmetry}: source overlaps={sourceQuality.overlapPairs}, worst={sourceQuality.worstAnisotropy}; " +
                    $"target overlaps={targetQuality.overlapPairs}, worst={targetQuality.worstAnisotropy}, degenerate={targetQuality.degenerateFaces}, invalid={targetQuality.invalidFaces}");
                Assert.AreEqual(0, sourceQuality.invalidFaces + sourceQuality.degenerateFaces);
                Assert.LessOrEqual(sourceQuality.overlapPairs, shelf ? 25 : 8);
                Assert.LessOrEqual(sourceQuality.worstAnisotropy, shelf ? 153 : 56497);
                Assert.LessOrEqual(targetQuality.overlapPairs, shelf ? 0 : 4);
                Assert.LessOrEqual(targetQuality.degenerateFaces, shelf ? 4 : 0);
                Assert.AreEqual(0, targetQuality.invalidFaces);
                Assert.LessOrEqual(targetQuality.worstAnisotropy, shelf ? 51561202 : 5.943e15);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
