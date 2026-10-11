using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    // Opt-in fixture for a user-provided model. No third-party model is distributed
    // with the package; ImportPackage keeps import work inside the test project.
    public class FoliageReferenceAssetTests
    {
        const string Input = "Assets/FoliageReferenceInput";
        const string Output = "Assets/FoliageReferenceOutput";

        [UnityTest]
        public IEnumerator PineLeavesOnlyPreserveTrunkChannelsMaterialsAndTransforms()
        {
            string package = Environment.GetEnvironmentVariable("MESHLAB_FOLIAGE_REFERENCE_PACKAGE");
            string output = Environment.GetEnvironmentVariable("MESHLAB_FOLIAGE_OUTPUT");
            if (string.IsNullOrEmpty(package)) Assert.Ignore("Set MESHLAB_FOLIAGE_REFERENCE_PACKAGE to the opt-in pine fixture Unity package.");
            Assert.IsFalse(string.IsNullOrEmpty(output)); Assert.IsTrue(File.Exists(package));
            Assert.IsFalse(AssetDatabase.IsValidFolder(Output), "The opt-in fixture requires an unused output asset folder.");
            GameObject root = null; Mesh applied = null;
            using var pipeline = new FoliageNormalsPipeline();
            try {
                Assert.IsTrue(MeshAssetOperations.EnsureOutputFolder(Input));
                // Match the verified live asset's material names and slot order. Test
                // materials use a portable shader; the source project's HDRP shader
                // and textures are unnecessary for explicit normal/channel checks.
                Material wood, leaves; AssetDatabase.StartAssetEditing();
                try {
                    wood = MaterialAsset(Input + "/M_Pine_trunk_05_Optimized.mat", "M_Pine_trunk_05_Optimized");
                    leaves = MaterialAsset(Input + "/M_Pine_leaves_Optimized.mat", "M_Pine_leaves_Optimized");
                }
                finally { AssetDatabase.StopAssetEditing(); }
                string modelPath = Input + "/Forest_pine_03_Optimized.fbx";
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                Assert.IsTrue(model, "Import the reference package with Unity's -importPackage argument before running this opt-in test.");
                var source = model.GetComponent<MeshFilter>().sharedMesh;
                int sourceId = source.GetInstanceID();
                Assert.AreEqual(2, source.subMeshCount); Assert.IsFalse(source.isReadable);
                root = (GameObject)PrefabUtility.InstantiatePrefab(model); Undo.RegisterCreatedObjectUndo(root, "Pine foliage reference");
                var renderer = root.GetComponent<MeshRenderer>(); Assert.IsTrue(renderer);
                Undo.RecordObject(renderer, "Reference material slots"); renderer.sharedMaterials = new[] {wood, leaves};
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                Assert.AreEqual(sourceId, root.GetComponent<MeshFilter>().sharedMesh.GetInstanceID());
                byte[] sourceBytes = File.ReadAllBytes(modelPath); byte[] sourceMeta = File.ReadAllBytes(modelPath + ".meta");
                var transforms = root.GetComponentsInChildren<Transform>();
                var positions = Array.ConvertAll(transforms, t => t.localPosition); var rotations = Array.ConvertAll(transforms, t => t.localRotation);
                var scales = Array.ConvertAll(transforms, t => t.localScale); var worlds = Array.ConvertAll(transforms, t => t.localToWorldMatrix);
                var defaults = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedMaterials, materials = new[] { leaves } };
                var task = pipeline.Run(root, defaults, FoliageNormalsPipeline.Stage.Scatter);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                int defaultIslands = pipeline.Samples.islands, defaultPrimitives = pipeline.Primitives.Length;
                int defaultRetained = pipeline.Samples.retainedForIslandMinimum;
                Assert.AreEqual(defaultIslands, new HashSet<int>(pipeline.Samples.sampleIslands).Count);
                Assert.LessOrEqual(defaultPrimitives, defaults.maximumPrimitiveCount);
                var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedMaterials, materials = new[] {leaves},
                    sampleSpacing = .04f, radiusMultiplier = 1.6f, dedupSpacingFraction = .01f, voxelResolution = 64, smoothingIterations = 3, smoothingStrength = .5f,
                    gpuProjection = GpuBvh.Supported };
                task = pipeline.Run(root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status); Assert.IsTrue(pipeline.CanApply);
                Assert.AreEqual(1, pipeline.Targets.Count); var target = pipeline.Targets[0]; var result = target.working;
                Assert.IsFalse(target.selectedSubmeshes[0]); Assert.IsTrue(target.selectedSubmeshes[1]);
                Assert.AreEqual(target.indices[1].Length/3, pipeline.Samples.faces);
                Assert.AreEqual(0, pipeline.TransferDiagnostics.misses); Assert.AreEqual(pipeline.TransferDiagnostics.selected, pipeline.TransferDiagnostics.transferred);
                int excluded = 0, changed = 0; var normals = result.normals; var resultPositions = result.vertices;
                int next = target.localPositions.Length; var sourceMap = new List<int>();
                for (int i = 0; i < target.localPositions.Length; i++) sourceMap.Add(i);
                for (int i = 0; i < target.localPositions.Length; i++) {
                    int v = target.selectedVertices[i] && target.excludedVertices[i] ? next++ : i;
                    if (v != i) sourceMap.Add(i);
                    if (target.excludedVertices[i]) { Assert.AreEqual(target.localNormals[i], normals[i]); excluded++; }
                    if (target.selectedVertices[i] && Vector3.Dot(target.localNormals[i], normals[v]) < .999f) changed++;
                }
                Assert.Greater(excluded, 0); Assert.Greater(changed, 0);
                CollectionAssert.AreEqual(target.indices[0], result.GetIndices(0));
                Assert.AreEqual(target.original.subMeshCount, result.subMeshCount); Assert.AreEqual(target.original.bounds, result.bounds);
                for (int i = 0; i < sourceMap.Count; i++) {
                    Assert.AreEqual(target.localPositions[sourceMap[i]], resultPositions[i]);
                    Assert.IsTrue(FoliageTargetSnapshot.Finite(normals[i])); Assert.That(normals[i].magnitude, Is.EqualTo(1).Within(.001));
                }
                for (int channel = 0; channel < 8; channel++) {
                    var before = new List<Vector4>(); var after = new List<Vector4>(); target.original.GetUVs(channel, before); result.GetUVs(channel, after);
                    if (before.Count == 0) { Assert.AreEqual(0, after.Count); continue; }
                    Assert.AreEqual(sourceMap.Count, after.Count); for (int i = 0; i < sourceMap.Count; i++) Assert.AreEqual(before[sourceMap[i]], after[i]);
                }
                var colorsBefore = target.original.colors32; var colorsAfter = result.colors32;
                if (colorsBefore.Length > 0) for (int i = 0; i < sourceMap.Count; i++) Assert.AreEqual(colorsBefore[sourceMap[i]], colorsAfter[i]);
                var tangentsBefore = target.original.tangents; var tangentsAfter = result.tangents;
                if (tangentsBefore.Length > 0) for (int i = 0; i < target.localPositions.Length; i++) if (target.excludedVertices[i]) Assert.AreEqual(tangentsBefore[i], tangentsAfter[i]);
                CollectionAssert.AreEqual(new[] {wood, leaves}, renderer.sharedMaterials);
                Assert.AreEqual(sourceId, root.GetComponent<MeshFilter>().sharedMesh.GetInstanceID());
                Assert.AreEqual(source, root.GetComponent<MeshFilter>().sharedMesh);
                CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(modelPath));
                CollectionAssert.AreEqual(sourceMeta, File.ReadAllBytes(modelPath + ".meta"));
                for (int i = 0; i < transforms.Length; i++) {
                    Assert.AreEqual(positions[i], transforms[i].localPosition); Assert.AreEqual(rotations[i], transforms[i].localRotation);
                    Assert.AreEqual(scales[i], transforms[i].localScale); Assert.AreEqual(worlds[i], transforms[i].localToWorldMatrix);
                }
                FoliageProxyBuilder.Validate(pipeline.Remeshed, CancellationToken.None, false);
                Directory.CreateDirectory(output);
                yield return FoliageNormalsPreviewTests.CapturePinePreview(root, settings, pipeline, output);
                FoliageNormalsToolTests.RenderPreview(pipeline.RemeshedMesh, Matrix4x4.identity, Path.Combine(output, "pine-proxy-wire.png"), false, true);
                FoliageNormalsToolTests.RenderPreview(target.original, target.localToCapture, Path.Combine(output, "pine-before-normals.png"), true);
                FoliageNormalsToolTests.RenderPreview(result, target.localToCapture, Path.Combine(output, "pine-after-normals.png"), true);
                var paths = pipeline.Save(Output);
                AssetDatabase.ExportPackage(paths, Path.Combine(output, "Forest_pine_03_Optimized_FoliageNormals.unitypackage"), ExportPackageOptions.Default);
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                string exportedFbx = pipeline.SaveFbx(Output)[0];
                FoliageFbxExportTests.CheckDocuments(modelPath, exportedFbx);
                var sourceTags = FbxChannelWrite.ImportTagged(modelPath, false, out _)[source.name];
                var resultTags = FbxChannelWrite.ImportTagged(exportedFbx, false, out _)[source.name];
                CollectionAssert.AreEqual(sourceTags.cornerPositions, resultTags.cornerPositions);
                // The real asset uses Mesh Compression Low. Validate file shading via
                // uncompressed throwaway imports, while positions retain the exact
                // source compression settings used by the transfer correspondence.
                var sourceShading = FbxChannelWrite.ImportTagged(modelPath, false, out _, uncompressed: true)[source.name];
                var resultShading = FbxChannelWrite.ImportTagged(exportedFbx, false, out _, uncompressed: true)[source.name];
                foreach (int corner in sourceTags.submeshCorners[0])
                    Assert.AreEqual(sourceShading.normals[corner],resultShading.normals[corner],"FBX export changed trunk/branch normals.");
                var leafIndices = result.GetIndices(1);
                var leafNormals = new Dictionary<Vector3,Vector3>();
                foreach (int vertex in leafIndices) leafNormals[resultPositions[vertex]] = normals[vertex];
                foreach (int corner in sourceTags.submeshCorners[1])
                {
                    int at = corner*3;
                    var position = new Vector3(resultTags.cornerPositions[at],resultTags.cornerPositions[at+1],resultTags.cornerPositions[at+2]);
                    Assert.Greater(Vector3.Dot(leafNormals[position].normalized,resultShading.normals[corner].normalized),.999999f,"Transferred leaf normal was not preserved in FBX.");
                }
                File.WriteAllBytes(Path.Combine(output,"Forest_pine_03_Optimized_FoliageNormals.fbx"),File.ReadAllBytes(exportedFbx));
                CollectionAssert.AreEqual(sourceBytes,File.ReadAllBytes(modelPath));
                CollectionAssert.AreEqual(sourceMeta,File.ReadAllBytes(modelPath+".meta"));
#endif
                string firstTransfer = pipeline.TransferDiagnostics.ToString(); int firstMisses = pipeline.TransferDiagnostics.misses;
                var remeshed = pipeline.Remeshed; settings.gpuProjection = false;
                task = pipeline.Run(root, settings, FoliageNormalsPipeline.Stage.Transfer);
                yield return FoliageNormalsToolTests.Await(task); Assert.IsTrue(task.Result, pipeline.Status);
                Assert.AreSame(remeshed, pipeline.Remeshed, "Backend switch must only rebuild Transfer.");
                Assert.AreEqual(firstMisses, pipeline.TransferDiagnostics.misses);
                var cpuNormals = target.working.normals; float minimumDot = 1;
                for (int i = 0; i < sourceMap.Count; i++) {
                    if (target.selectedVertices[sourceMap[i]]) {
                        float dot = Vector3.Dot(normals[i], cpuNormals[i]); minimumDot = Mathf.Min(minimumDot, dot);
                        Assert.Greater(dot, .999f, "CPU/GPU transferred normals differ.");
                    }
                    else Assert.AreEqual(normals[i], cpuNormals[i]);
                }
                File.WriteAllText(Path.Combine(output, "pine-metrics.txt"), $"Mesh {source.name}: {source.vertexCount} vertices, 2 submeshes; unreadable imported source.\n" +
                    $"Default scatter: {defaultIslands} islands covered, {defaultPrimitives} primitives, {defaultRetained} close samples retained for island minima.\n" +
                    $"Slot 0: {wood.name}, {target.indices[0].Length/3} faces, {excluded} vertices with exact original normals.\n" +
                    $"Slot 1: {leaves.name}, {target.indices[1].Length/3} faces, {changed} changed vertex directions.\n" +
                    $"{pipeline.Samples.islands} foliage islands; {pipeline.Primitives.Length} primitives; {pipeline.ProxyComponents} proxy components.\n" +
                    $"Positions, UV0-UV7, colors, excluded indices/normals/tangents, material references and transforms preserved.\n" +
                    $"Spacing {pipeline.Samples.spacing:G5} (4% diagonal), radius multiplier 1.6, dedup fraction 0.01, voxel resolution 64, Taubin 3 iterations at strength 0.5.\n" +
                    $"{pipeline.RemeshDiagnostics}\n{firstTransfer}\n{pipeline.TransferDiagnostics}\nCPU/GPU minimum normal dot {minimumDot:G9}.\nSaved {string.Join(", ", paths)}\n");
                pipeline.Apply(); Undo.FlushUndoRecordObjects(); applied = root.GetComponent<MeshFilter>().sharedMesh;
                Assert.AreNotEqual(source, applied); Undo.PerformUndo(); Assert.AreEqual(source, root.GetComponent<MeshFilter>().sharedMesh);
                Undo.PerformRedo(); applied = root.GetComponent<MeshFilter>().sharedMesh; Assert.IsTrue(applied); Assert.AreNotEqual(source, applied);
                pipeline.ClearFrom(FoliageNormalsPipeline.Stage.Capture); Assert.IsTrue(applied); Assert.IsFalse(source.isReadable);
            }
            finally {
                pipeline.Dispose(); Undo.ClearAll(); if (root) Object.DestroyImmediate(root); if (applied) Object.DestroyImmediate(applied);
                // The exported output is outside Assets and survives test cleanup.
                AssetDatabase.StartAssetEditing();
                try {
                    if (AssetDatabase.IsValidFolder(Input)) Assert.IsTrue(AssetDatabase.DeleteAsset(Input));
                    if (AssetDatabase.IsValidFolder(Output)) Assert.IsTrue(AssetDatabase.DeleteAsset(Output));
                }
                finally { AssetDatabase.StopAssetEditing(); }
            }
        }

        static Material MaterialAsset(string path, string name)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path); if (material) return material;
            material = new Material(Shader.Find("Standard")) { name = name };
            try { AssetDatabase.CreateAsset(material, path); return material; }
            catch { Object.DestroyImmediate(material); throw; }
        }
    }
}
