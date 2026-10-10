using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvSeamTests
    {
        readonly List<Mesh> owned = new List<Mesh>();
        [TearDown] public void Cleanup() { foreach (var mesh in owned) if (mesh) Object.DestroyImmediate(mesh); owned.Clear(); }

        Mesh Quad(float x = 0, float z = 0, float size = 1)
        {
            var mesh = new Mesh { name = "SeamQuad" }; owned.Add(mesh);
            mesh.vertices = new[] { new Vector3(x, 0, z), new Vector3(x + size, 0, z), new Vector3(x + size, size, z), new Vector3(x, size, z) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.uv2 = mesh.uv.Select(p => p * .5f + Vector2.one * .25f).ToArray();
            mesh.normals = Enumerable.Repeat(Vector3.forward, 4).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 4).ToArray();
            return mesh;
        }

        Mesh Donor()
        {
            var source = Quad(); var uv = source.uv2;
            var split = ReverseUvMesh.Copy(source, source.triangles, new[] { uv[0], uv[1], uv[2],
                uv[0] + Vector2.right * .5f, uv[2] + Vector2.right * .5f, uv[3] + Vector2.right * .5f });
            owned.Add(split); split.uv2 = split.uv2.Select(p => p * .6f).ToArray(); return split;
        }

        static ReverseUvTransfer.Level Level(int lod, params Mesh[] meshes) => new ReverseUvTransfer.Level {
            lod = lod, inputs = meshes.Select(m => new ReverseUvTransfer.Input { mesh = m, key = m.name }).ToArray() };

        static double Area(Mesh mesh, Func<int, bool> include = null)
        {
            var p = mesh.vertices; var indices = mesh.triangles; double area = 0;
            for (int t = 0; t < indices.Length; t += 3)
                if (include == null || include(t / 3)) area += Vector3.Cross(p[indices[t + 1]] - p[indices[t]], p[indices[t + 2]] - p[indices[t]]).magnitude * .5;
            return area;
        }

        static byte[] Snapshot(Mesh source)
        {
            var readable=MeshAccess.Readable(source,out bool owns);
            try { return TransferMeshSnapshot.Capture(readable); }
            finally { if(owns) Object.DestroyImmediate(readable); }
        }

        [TestCase(false)] [TestCase(true)]
        public void CrossingFacesInheritBothChartsAndKeepSurfaceAndAttributes(bool overlap)
        {
            var donor = Donor(); var target = Quad(); target.triangles = new[] { 0, 1, 3, 1, 2, 3 };
            var before = TransferMeshSnapshot.Capture(target);
            using var result = ReverseUvTransfer.Build(new[] { Level(1, donor), Level(0, target) },
                new ReverseUvTransfer.Options { seedResolution = 128, preserveProjectedOverlap = overlap }).GetAwaiter().GetResult();
            var mesh = result.meshes[1][0]; var report = result.report.nodes[1];
            Assert.Greater(report.faces.Length, 2); Assert.AreEqual(0, report.newFaces);
            Assert.AreEqual(Area(target), Area(mesh), 1e-6); Assert.AreEqual(2, report.splitSourceFaces);
            Assert.AreEqual(2, report.faces.Select(f => f.chart).Distinct().Count());
            Assert.AreEqual(2, report.faces.Select(f => f.group).Distinct().Count());
            Assert.IsTrue(result.report.edges.Any(e => e.lod == 0 && e.reason == "chart-seam"));
            for (int v = 0; v < mesh.vertexCount; ++v)
            {
                Assert.Less(Vector2.Distance(new Vector2(mesh.vertices[v].x, mesh.vertices[v].y), mesh.uv[v]), 1e-6);
                Assert.AreEqual(-1, mesh.tangents[v].w); Assert.AreEqual(Vector3.forward, mesh.normals[v]);
            }
            var quality = TransferUvQuality.Measure(mesh, mesh.uv2, Vector2.one, Matrix4x4.identity);
            Assert.IsTrue(quality.overlapScanComplete); Assert.AreEqual(0, quality.overlapPairs); Assert.AreEqual(0, quality.degenerateFaces);
            Assert.Less(quality.worstAnisotropy, 1.001);
            CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(target));
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, report.sourceFaces.Distinct());
        }

        [TestCase(false)] [TestCase(true)]
        public void NewSmallIslandUsesSeedVacancyWithoutGrowingOrMovingInheritedUv(bool overlap)
        {
            var seed = Quad(); var matching = Quad(); var detail = Quad(3, size: .1f);
            using var result = ReverseUvTransfer.Build(new[] { Level(1, seed), Level(0, matching, detail) },
                new ReverseUvTransfer.Options { seedResolution = 128, preserveProjectedOverlap = overlap }).GetAwaiter().GetResult();
            Assert.AreEqual(128, result.report.atlasSize);
            CollectionAssert.AreEqual(seed.uv2, result.meshes[0][0].uv2);
            CollectionAssert.AreEqual(matching.uv2, result.meshes[1][0].uv2);
            var uv = result.meshes[1][1].uv2;
            Assert.IsTrue(uv.All(p => p.x < .25f || p.y < .25f || p.x > .75f || p.y > .75f));
            Assert.AreEqual(64, result.report.texelsPerUnit, .001);
        }

        [TestCase(false)] [TestCase(true)]
        public void CrossingRaisedDetailPreservesOnlyExplicitOverlapLayers(bool overlap)
        {
            var donor = Donor(); var parent = Quad(); parent.triangles = new[] { 0, 1, 3, 1, 2, 3 };
            var detail = Quad(.25f, .02f, .5f); detail.triangles = parent.triangles;
            using var result = ReverseUvTransfer.Build(new[] { Level(1, donor), Level(0, parent, detail) },
                new ReverseUvTransfer.Options { seedResolution = 128, preserveProjectedOverlap = overlap }).GetAwaiter().GetResult();
            var node = result.report.nodes[2];
            Assert.AreEqual(overlap, node.faces.All(f => f.inherited && f.intentionalOverlap && f.layer == 1));
            Assert.AreEqual(overlap, result.report.overlaps.Count > 0);
            Assert.AreEqual(Area(detail), Area(result.meshes[1][1]), 1e-6);
        }

        [Test]
        public void RefinedCopyInterpolatesSkinBlendShapesAndKeepsEmptyMaterialSlots()
        {
            var source = Quad(); source.subMeshCount = 3;
            source.SetTriangles(new[] { 0, 1, 2 }, 0); source.SetTriangles(Array.Empty<int>(), 1); source.SetTriangles(new[] { 0, 2, 3 }, 2);
            source.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            using (var counts = new NativeArray<byte>(new byte[] { 1, 1, 1, 1 }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(new[] { new BoneWeight1 { boneIndex = 0, weight = 1 },
                new BoneWeight1 { boneIndex = 1, weight = 1 }, new BoneWeight1 { boneIndex = 0, weight = 1 },
                new BoneWeight1 { boneIndex = 0, weight = 1 } }, Allocator.Temp)) source.SetBoneWeights(counts, weights);
            source.AddBlendShapeFrame("Offset", 100, source.vertices, new Vector3[4], new Vector3[4]);
            var before = TransferMeshSnapshot.Capture(source);
            var bary = new[] { Vector3.right, new Vector3(.5f,.5f,0), Vector3.forward,
                new Vector3(.5f,.5f,0), Vector3.up, Vector3.forward, Vector3.right, Vector3.up, Vector3.forward };
            var output = ReverseUvRefinedMesh.Copy(source, new[] { 0, 0, 1 }, bary,
                bary.Select(p => new Vector2(p.y,p.z)).ToArray()); owned.Add(output);
            Assert.AreEqual(3, output.subMeshCount); Assert.AreEqual(0, output.GetTriangles(1).Length);
            Assert.AreEqual(Area(source), Area(output), 1e-6);
            int midpoint = Array.FindIndex(output.vertices, p => p == new Vector3(.5f,0,0)); Assert.GreaterOrEqual(midpoint,0);
            using var outputCounts = output.GetBonesPerVertex(); using var outputWeights = output.GetAllBoneWeights();
            int offset = 0; for (int i = 0; i < midpoint; ++i) offset += outputCounts[i];
            Assert.AreEqual(2, outputCounts[midpoint]); Assert.AreEqual(.5f, outputWeights[offset].weight);
            Assert.AreEqual(.5f, outputWeights[offset+1].weight);
            var deltas = new Vector3[output.vertexCount];
            output.GetBlendShapeFrameVertices(0, 0, deltas, new Vector3[deltas.Length], new Vector3[deltas.Length]);
            Assert.AreEqual(new Vector3(.5f,0,0), deltas[midpoint]);
            CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(source));
        }

        [TestCase(2)] [TestCase(4)]
        public void RefinedCopyKeepsPackedUvAndColourFormatsAndInterpolatesExtraComponents(int dimensions)
        {
            var source = new Mesh(); owned.Add(source);
            source.SetVertexBufferParams(3, new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, dimensions),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 1));
            int stride = source.GetVertexBufferStride(0); var bytes = new byte[stride * 3];
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up };
            for (int v = 0; v < 3; ++v)
            {
                for (int c = 0; c < 3; ++c) Buffer.BlockCopy(BitConverter.GetBytes(positions[v][c]), 0, bytes, v * stride + c * 4, 4);
                int colour = source.GetVertexAttributeOffset(VertexAttribute.Color);
                for (int c = 0; c < 4; ++c) bytes[v * stride + colour + c] = (byte)(v * 50 + c);
                int uv = source.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                for (int c = 0; c < dimensions; ++c) Buffer.BlockCopy(BitConverter.GetBytes(Mathf.FloatToHalf(v + c * .125f)), 0, bytes, v * stride + uv + c * 2, 2);
                int extra = source.GetVertexAttributeOffset(VertexAttribute.TexCoord3);
                Buffer.BlockCopy(BitConverter.GetBytes(v * 2f), 0, bytes, v * stride + extra, 4);
            }
            source.SetVertexBufferData(bytes,0,0,bytes.Length); source.triangles = new[] {0,1,2};
            var bary = new[] {Vector3.right,new Vector3(.5f,.5f,0),Vector3.forward,new Vector3(.5f,.5f,0),Vector3.up,Vector3.forward};
            var output = ReverseUvRefinedMesh.Copy(source,new[] {0,0},bary,bary.Select(p=>new Vector2(p.y,p.z)).ToArray()); owned.Add(output);
            Assert.AreEqual(VertexAttributeFormat.Float16, output.GetVertexAttributeFormat(VertexAttribute.TexCoord0));
            Assert.AreEqual(dimensions, output.GetVertexAttributeDimension(VertexAttribute.TexCoord0));
            var uvs = new List<Vector4>(); output.GetUVs(0,uvs); var extraUvs = new List<Vector4>(); output.GetUVs(3,extraUvs);
            int midpoint = Array.FindIndex(output.vertices,p=>p==new Vector3(.5f,0,0));
            for (int c = 0; c < dimensions; ++c) Assert.AreEqual(.5f+c*.125f,uvs[midpoint][c],.001);
            Assert.AreEqual(1,extraUvs[midpoint].x); Assert.AreEqual(new Color32(25,26,27,28),output.colors32[midpoint]);
        }

        [Serializable] sealed class Layout
        {
            public int lod, atlas; public string key;
            public Vector3[] positions; public Vector2[] uv; public int[] indices;
            public ReverseUvTransfer.NodeReport report;
        }

        [TestCase(false)] [TestCase(true)]
        public void FrozenTableSeamAndVacancyRegression(bool overlap)
        {
            string directory = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_FIXTURES");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Optional private Table FBX fixture is not configured.");
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(directory + "/Cafe_Table_A_01.fbx"); Assert.IsNotNull(root);
            var filters = root.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.sharedMesh).ToArray();
            var frame = Matrix4x4.TRS(new Vector3(33.65f,.002f,51.18f),Quaternion.Euler(0,89.99995f,0),Vector3.one*.84974f);
            var levels = Enumerable.Range(0,3).Reverse().Select(lod => new ReverseUvTransfer.Level { lod=lod,
                inputs=filters.Where(f=>f.sharedMesh.name.Contains("LOD"+lod)).Select(f=>new ReverseUvTransfer.Input {
                    mesh=f.sharedMesh,key=f.sharedMesh.name,toWorld=frame*f.transform.localToWorldMatrix }).ToArray() }).ToArray();
            var snapshots = levels.SelectMany(l=>l.inputs).Select(i=>Snapshot(i.mesh)).ToArray();
            var previous = BenchmarkRecorder.OutputDirectoryOverride; double oldInheritedArea = 0; int oldSide=0;
            try
            {
                foreach (bool improved in new[] {false,true})
                {
                    using var prepared = ReverseUvJunctionTrial.Build(levels,new ReverseUvTransfer.Options {seedResolution=128,
                        preserveProjectedOverlap=overlap,splitDonorSeams=improved,fillAtlasVacancies=improved},true,32).GetAwaiter().GetResult();
                    string output = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_OUTPUT");
                    BenchmarkRecorder.OutputDirectoryOverride = string.IsNullOrEmpty(output)?null:Path.Combine(output,
                        "table-"+(improved?"improved":"previous")+(overlap?"-overlap":"-exclusive"));
                    string auditPath = ReverseUvAudit.Write(prepared.result,prepared.inputs.levels);
                    var audit = JsonUtility.FromJson<ReverseUvAudit.Audit>(File.ReadAllText(auditPath));
                    double inheritedArea=0; int index=0;
                    for (int l=0;l<levels.Length;++l) for (int n=0;n<levels[l].inputs.Length;++n)
                    {
                        var node=prepared.result.report.nodes[index++]; var mesh=prepared.result.meshes[l][n];
                        if(!node.seed) inheritedArea+=Area(mesh,f=>node.faces[f].inherited);
                        if(!string.IsNullOrEmpty(output)) File.WriteAllText(Path.Combine(BenchmarkRecorder.OutputDirectoryOverride,"lod"+node.lod+"-"+n+".json"),
                            JsonUtility.ToJson(new Layout {lod=node.lod,atlas=prepared.result.report.atlasSize,key=node.key,
                                positions=mesh.vertices,uv=mesh.uv2,indices=mesh.triangles,report=node},true));
                    }
                    if(!improved) {oldInheritedArea=inheritedArea;oldSide=prepared.result.report.atlasSize;}
                    else {Assert.GreaterOrEqual(inheritedArea,oldInheritedArea-1e-5);Assert.Less(prepared.result.report.atlasSize,oldSide);}
                    Assert.IsTrue(audit.levels.All(m=>m.complete && m.unexpectedOverlapPairs==0));
                }
            }
            finally {BenchmarkRecorder.OutputDirectoryOverride=previous;}
            int source=0;foreach(var input in levels.SelectMany(l=>l.inputs)) CollectionAssert.AreEqual(snapshots[source++],Snapshot(input.mesh));
        }

        [Serializable] sealed class AssetTrial
        {
            public string asset, stage, error, audit;
            public bool overlap, improved, accepted;
            public int atlas;
            public double inheritedAreaFraction;
        }
        [Serializable] sealed class AssetTrials { public List<AssetTrial> trials = new List<AssetTrial>(); }

        [Serializable] sealed class WoodenInputDump
        {
            public string key;
            public int lod;
            public Vector3[] positions;
            public int[] indices;
            public Vector2[] uv;
        }

        [UnityTest]
        public IEnumerator FrozenWoodenBoxReverseStandaloneCompletes()
        {
            string path=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_WOODEN_BOX");
            if(string.IsNullOrEmpty(path)) Assert.Ignore("Optional Wooden_Box_Long FBX is not configured.");
            var root=AssetDatabase.LoadAssetAtPath<GameObject>(path); Assert.IsNotNull(root,path);
            var levels=root.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.sharedMesh && !f.sharedMesh.name.Contains("_COL"))
                .Select(f=>(filter:f,match:System.Text.RegularExpressions.Regex.Match(f.sharedMesh.name,@"_LOD(\d+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                .Where(p=>p.match.Success).GroupBy(p=>int.Parse(p.match.Groups[1].Value)).OrderByDescending(g=>g.Key)
                .Select(g=>new ReverseUvTransfer.Level {lod=g.Key,inputs=g.Select(p=>new ReverseUvTransfer.Input {
                    mesh=p.filter.sharedMesh,key=p.filter.sharedMesh.name,toWorld=p.filter.transform.localToWorldMatrix}).ToArray()}).ToArray();
            Assert.GreaterOrEqual(levels.Length,2,path);
            var snapshots=levels.SelectMany(l=>l.inputs).Select(i=>Snapshot(i.mesh)).ToArray();
            string directory=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_OUTPUT"); Assert.IsNotEmpty(directory);
            Directory.CreateDirectory(directory); var trials=new AssetTrials(); var previous=BenchmarkRecorder.OutputDirectoryOverride;
            try
            {
                foreach(bool overlap in new[] {false,true})
                {
                    using var prepared=ReverseUvInputs.Prepare(levels);
                    var origin=prepared.levels[0].inputs[0].toWorld.GetColumn(3);
                    foreach(var level in prepared.levels)
                        foreach(var input in level.inputs)
                            File.WriteAllText(Path.Combine(directory,input.key+"-geometry.json"),JsonUtility.ToJson(new WoodenInputDump {
                                key=input.key,lod=level.lod,positions=ReverseUvTransfer.RelativePositions(input,origin),indices=input.mesh.triangles},true));
                    int side=prepared.PrepareSeed(256,2,32,default);
                    var trial=new AssetTrial {asset=path,overlap=overlap,improved=true,stage="projection"}; trials.trials.Add(trial);
                    var task=ReverseUvTransfer.Build(prepared.levels,new ReverseUvTransfer.Options {seedResolution=side,preserveProjectedOverlap=overlap},true);
                    while(!task.IsCompleted) yield return null;
                    if(task.IsFaulted) trial.error=task.Exception.GetBaseException().Message;
                    else
                    {
                        using var result=task.Result;
                        BenchmarkRecorder.OutputDirectoryOverride=Path.Combine(directory,overlap?"overlap":"exclusive");
                        trial.stage="audit"; trial.audit=ReverseUvAudit.Write(result,prepared.levels);
                        trial.inheritedAreaFraction=JsonUtility.FromJson<ReverseUvAudit.Audit>(File.ReadAllText(trial.audit)).levels.OrderBy(l=>l.lod).First().inheritedAreaFraction;
                        trial.accepted=true; trial.atlas=result.report.atlasSize;
                        for(int level=0;level<prepared.levels.Length;++level)
                            for(int node=0;node<prepared.levels[level].inputs.Length;++node)
                            {
                                var input=prepared.levels[level].inputs[node]; var mesh=result.meshes[level][node];
                                File.WriteAllText(Path.Combine(BenchmarkRecorder.OutputDirectoryOverride,input.key+"-layout.json"),JsonUtility.ToJson(new WoodenInputDump {
                                    key=input.key,lod=prepared.levels[level].lod,positions=mesh.vertices,indices=mesh.triangles,uv=mesh.uv2},true));
                            }
                    }
                    File.WriteAllText(Path.Combine(directory,"wooden-trials.json"),JsonUtility.ToJson(trials,true));
                }
                int i=0;foreach(var input in levels.SelectMany(l=>l.inputs)) CollectionAssert.AreEqual(snapshots[i++],Snapshot(input.mesh));
            }
            finally {BenchmarkRecorder.OutputDirectoryOverride=previous;}
            Assert.IsTrue(trials.trials.All(t=>t.accepted),string.Join("\n",trials.trials.Select(t=>t.error)));
        }

        [UnityTest]
        public IEnumerator FrozenAssetChainsRetainBaselineAcceptance()
        {
            string paths=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_ASSET_PATHS");
            if(string.IsNullOrEmpty(paths)) Assert.Ignore("Optional private full FBX chains are not configured.");
            string directory=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_OUTPUT"); Assert.IsNotEmpty(directory);
            Directory.CreateDirectory(directory); var trials=new AssetTrials(); var previous=BenchmarkRecorder.OutputDirectoryOverride;
            var regressions=new List<string>();
            try
            {
                foreach(string path in paths.Split(';'))
                {
                    var root=AssetDatabase.LoadAssetAtPath<GameObject>(path); Assert.IsNotNull(root,path);
                    var levels=root.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.sharedMesh && !f.sharedMesh.name.Contains("_COL"))
                        .Select(f=>(filter:f,match:System.Text.RegularExpressions.Regex.Match(f.sharedMesh.name,@"_LOD(\d+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                        .Where(p=>p.match.Success).GroupBy(p=>int.Parse(p.match.Groups[1].Value)).OrderByDescending(g=>g.Key)
                        .Select(g=>new ReverseUvTransfer.Level {lod=g.Key,inputs=g.Select(p=>new ReverseUvTransfer.Input {
                            mesh=p.filter.sharedMesh,key=p.filter.sharedMesh.name,toWorld=p.filter.transform.localToWorldMatrix}).ToArray()}).ToArray();
                    Assert.GreaterOrEqual(levels.Length,2,path);
                    var snapshots=levels.SelectMany(l=>l.inputs).Select(i=>Snapshot(i.mesh)).ToArray();
                    foreach(bool overlap in new[] {false,true})
                    {
                        AssetTrial baseline=null;
                        foreach(bool improved in new[] {false,true})
                        {
                            var trial=new AssetTrial {asset=path,overlap=overlap,improved=improved,stage="cleanup"}; trials.trials.Add(trial);
                            using var prepared=ReverseUvInputs.Prepare(levels);
                            int side=0; trial.stage="seed";
                            try {side=prepared.PrepareSeed(256,2,0,default);} catch(Exception e) {trial.error=e.Message;}
                            if(trial.error==null)
                            {
                                trial.stage="projection";
                                var task=ReverseUvTransfer.Build(prepared.levels,new ReverseUvTransfer.Options {seedResolution=side,
                                    splitDonorSeams=improved,fillAtlasVacancies=improved,preserveProjectedOverlap=overlap},true);
                                while(!task.IsCompleted) yield return null;
                                if(task.IsFaulted) trial.error=task.Exception.GetBaseException().Message;
                                else
                                {
                                    using var result=task.Result; trial.stage="audit";
                                    BenchmarkRecorder.OutputDirectoryOverride=Path.Combine(directory,"assets",Path.GetFileNameWithoutExtension(path),
                                        (improved?"improved":"previous")+(overlap?"-overlap":"-exclusive"));
                                    try
                                    {
                                        trial.audit=ReverseUvAudit.Write(result,prepared.levels);
                                        var audit=JsonUtility.FromJson<ReverseUvAudit.Audit>(File.ReadAllText(trial.audit));
                                        trial.inheritedAreaFraction=audit.levels.Last().inheritedAreaFraction;
                                        trial.atlas=result.report.atlasSize; trial.accepted=true;
                                    }
                                    catch(Exception e) {trial.error=e.Message;}
                                }
                            }
                            if(!improved) baseline=trial;
                            else if(baseline.accepted)
                            {
                                if(!trial.accepted) regressions.Add(path+", overlap="+overlap+": "+trial.error);
                                else if(trial.inheritedAreaFraction<baseline.inheritedAreaFraction-1e-5 || trial.atlas>baseline.atlas)
                                    regressions.Add(path+", overlap="+overlap+": inherited area or atlas regressed");
                            }
                            File.WriteAllText(Path.Combine(directory,"asset-trials.json"),JsonUtility.ToJson(trials,true));
                        }
                    }
                    int i=0;foreach(var input in levels.SelectMany(l=>l.inputs)) CollectionAssert.AreEqual(snapshots[i++],Snapshot(input.mesh));
                }
            }
            finally {BenchmarkRecorder.OutputDirectoryOverride=previous;File.WriteAllText(Path.Combine(directory,"asset-trials.json"),JsonUtility.ToJson(trials,true));}
            Assert.IsEmpty(regressions,string.Join("\n",regressions));
        }
    }
}
