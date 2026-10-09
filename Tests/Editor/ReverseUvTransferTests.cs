using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvTransferTests
    {
        readonly List<Mesh> owned = new List<Mesh>();
        [TearDown] public void Cleanup() { foreach (var m in owned) if (m) Object.DestroyImmediate(m); owned.Clear(); }

        Mesh Quad(float x = 0, float z = 0, float size = 1)
        {
            var mesh = new Mesh { name = "Quad" };
            mesh.vertices = new[] { new Vector3(x, 0, z), new Vector3(x + size, 0, z), new Vector3(x + size, size, z), new Vector3(x, size, z) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.uv2 = mesh.uv.Select(p => p * .5f + Vector2.one * .25f).ToArray();
            mesh.RecalculateNormals(); owned.Add(mesh); return mesh;
        }

        Mesh Combine(params Mesh[] meshes)
        {
            var mesh = new Mesh { name = "Combined" };
            mesh.CombineMeshes(meshes.Select(m => new CombineInstance { mesh = m, transform = Matrix4x4.identity }).ToArray(), true);
            owned.Add(mesh); return mesh;
        }
        static ReverseUvTransfer.Level Level(int lod, params Mesh[] meshes) => new ReverseUvTransfer.Level {
            lod = lod, inputs = meshes.Select((m, i) => new ReverseUvTransfer.Input { mesh = m, key = m.name + i }).ToArray() };
        static ReverseUvTransfer.Options Options(bool overlap = false) => new ReverseUvTransfer.Options {
            seedResolution = 128, projectionReach = .1f, preserveProjectedOverlap = overlap };
        static ReverseUvTransfer.Result Build(ReverseUvTransfer.Options options, params ReverseUvTransfer.Level[] levels)
            => ReverseUvTransfer.Build(levels, options).GetAwaiter().GetResult();

        [Test]
        public void CleanupPreservesSourceAttributesAndMaterialSlotsAndTracksFaceIdentity()
        {
            var source = Quad();
            source.subMeshCount = 3;
            source.SetTriangles(new[] { 0, 0, 1, 0, 1, 2 }, 0);
            source.SetTriangles(new[] { 1, 1, 2 }, 1);
            source.SetTriangles(new[] { 0, 2, 3 }, 2);
            var delta = Enumerable.Repeat(Vector3.up, 4).ToArray();
            source.AddBlendShapeFrame("move", 100, delta, delta, delta);
            source.bindposes = new[] { Matrix4x4.identity };
            source.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            var before = source.triangles;
            var prepared = ReverseUvInputs.Prepare(new[] { Level(1, source), Level(0, source) });
            var input = prepared.levels[0].inputs[0]; var copy = input.mesh;
            Assert.AreNotSame(source, copy);
            CollectionAssert.AreEqual(before, source.triangles);
            CollectionAssert.AreEqual(new[] { 1, 3 }, input.sourceFaces);
            CollectionAssert.AreEqual(new[] { 0, 2 }, input.removedSourceFaces);
            Assert.AreEqual(3, copy.subMeshCount); Assert.IsEmpty(copy.GetTriangles(1));
            CollectionAssert.AreEqual(source.vertices, copy.vertices);
            CollectionAssert.AreEqual(source.uv, copy.uv); CollectionAssert.AreEqual(source.uv2, copy.uv2);
            CollectionAssert.AreEqual(source.normals, copy.normals);
            CollectionAssert.AreEqual(source.boneWeights, copy.boneWeights);
            CollectionAssert.AreEqual(source.bindposes, copy.bindposes);
            Assert.AreEqual(1, copy.blendShapeCount);
            var actual = new Vector3[4]; copy.GetBlendShapeFrameVertices(0, 0, actual, null, null);
            CollectionAssert.AreEqual(delta, actual);
            using (prepared)
            using (var result = Build(Options(), prepared.levels))
            {
                CollectionAssert.AreEqual(input.sourceFaces, result.report.nodes[0].sourceFaces);
                CollectionAssert.AreEqual(input.removedSourceFaces, result.report.nodes[0].removedSourceFaces);
                Assert.AreEqual(2, result.meshes[0][0].triangles.Length / 3);
            }
            Assert.IsTrue(copy == null); Assert.IsTrue(source);
        }

        [Test]
        public void CleanupKeepsKamazThinFaceAndRemovesOnlyItsCollinearControl()
        {
            var mesh = Quad();
            mesh.vertices = new[] {
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.8186256885528564f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.8186254501342773f),
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.8186254501342773f),
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.723873257637024f) };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            using var prepared = ReverseUvInputs.Prepare(new[] { Level(1, mesh) });
            CollectionAssert.AreEqual(new[] { 0 }, prepared.levels[0].inputs[0].sourceFaces);
            CollectionAssert.AreEqual(new[] { 1 }, prepared.levels[0].inputs[0].removedSourceFaces);
            Assert.AreEqual(6, mesh.triangles.Length);
        }

        [Test]
        public void CleanupRejectsNonfiniteAndEmptyGeometryAndHonoursCancellation()
        {
            var mesh = Quad(); var before = mesh.triangles;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => ReverseUvInputs.Prepare(new[] { Level(1, mesh) }, canceled.Token));
            CollectionAssert.AreEqual(before, mesh.triangles);
            mesh.triangles = new[] { 0, 0, 0 };
            Assert.Throws<InvalidOperationException>(() => ReverseUvInputs.Prepare(new[] { Level(1, mesh) }));
            mesh.vertices = new[] { Vector3.zero, new Vector3(float.NaN, 0, 0), Vector3.up };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, false);
            Assert.Throws<InvalidOperationException>(() => ReverseUvInputs.Prepare(new[] { Level(1, mesh) }));
        }

        [Serializable] sealed class KamazCapture
        {
            public string kind, key;
            public Vector3[] positions;
            public int[] indices;
            public Matrix4x4 toWorld;
        }
        [Serializable] sealed class KamazTrial
        {
            public int frame, removedFaces;
            public bool overlap, accepted;
            public string stage, error, audit;
            public int[] removedByLod;
        }
        [Serializable] sealed class KamazTrials { public List<KamazTrial> trials = new List<KamazTrial>(); }

        [UnityTest]
        public IEnumerator FrozenKamazFullLodChainAfterCleanup()
        {
            string directory = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_KAMAZ");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Set MESHLAB_REVERSE_KAMAZ to the readonly Kamaz captures.");
            string output = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_OUTPUT");
            Assert.IsNotEmpty(output); System.IO.Directory.CreateDirectory(output);
            var trials = new KamazTrials();
            var previousOutput = BenchmarkRecorder.OutputDirectoryOverride;
            BenchmarkRecorder.OutputDirectoryOverride = output;
            try
            {
                for (int frame = 0; frame < 4; ++frame)
                {
                    var captures = new List<KamazCapture>();
                    for (int file = frame * 15; file < (frame == 3 ? 61 : (frame + 1) * 15); ++file)
                    {
                        var capture = JsonUtility.FromJson<KamazCapture>(System.IO.File.ReadAllText(System.IO.Path.Combine(directory, file + ".json")));
                        if (capture.key.Contains("_LOD")) captures.Add(capture);
                    }
                    var levels = captures.GroupBy(c => int.Parse(c.key.Substring(c.key.LastIndexOf("_LOD", StringComparison.Ordinal) + 4)))
                        .OrderByDescending(g => g.Key).Select(g => new ReverseUvTransfer.Level { lod = g.Key,
                            inputs = g.OrderBy(c => c.key).Select(c => {
                                var mesh = new Mesh { name = c.key, indexFormat = IndexFormat.UInt32 };
                                mesh.vertices = c.positions; mesh.triangles = c.indices; owned.Add(mesh);
                                return new ReverseUvTransfer.Input { mesh = mesh, key = c.key, toWorld = c.toWorld };
                            }).ToArray() }).ToArray();
                    Assert.AreEqual(3, levels.Length); Assert.IsTrue(levels.All(l => l.inputs.Length == 5));
                    foreach (bool overlap in new[] { false, true })
                    {
                        var trial = new KamazTrial { frame = frame, overlap = overlap, stage = "cleanup" };
                        trials.trials.Add(trial);
                        using var prepared = ReverseUvInputs.Prepare(levels);
                        trial.removedByLod = prepared.levels.Select(l => l.inputs.Sum(i => i.removedSourceFaces.Length)).ToArray();
                        trial.removedFaces = trial.removedByLod.Sum();
                        Assert.Greater(trial.removedFaces, 0);
                        for (int l = 0; l < levels.Length; ++l)
                            for (int n = 0; n < levels[l].inputs.Length; ++n)
                            {
                                var input = prepared.levels[l].inputs[n];
                                Assert.AreEqual(levels[l].inputs[n].mesh.triangles.Length / 3,
                                    input.sourceFaces.Length + input.removedSourceFaces.Length);
                            }
                        trial.stage = "seed";
                        int size = 0;
                        try { size = prepared.PrepareSeed(256, 2, 0, default); }
                        catch (Exception ex) { trial.error = ex.Message; }
                        if (trial.error != null) continue;
                        trial.stage = "projection";
                        var task = ReverseUvTransfer.Build(prepared.levels, new ReverseUvTransfer.Options {
                            seedResolution = size, preserveProjectedOverlap = overlap, projectionReach = .05f }, true);
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { trial.error = task.Exception.GetBaseException().Message; continue; }
                        using var result = task.Result;
                        trial.stage = "audit";
                        try { trial.audit = ReverseUvAudit.Write(result, prepared.levels); trial.accepted = true; }
                        catch (Exception ex) { trial.error = ex.Message; }
                    }
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(output, "kamaz-trials.json"), JsonUtility.ToJson(trials, true));
            }
            finally { BenchmarkRecorder.OutputDirectoryOverride = previousOutput; }
            Assert.AreEqual(8, trials.trials.Count);
        }

        static void Clean(Mesh mesh, bool overlaps = false)
        {
            var q = TransferUvQuality.Measure(mesh, mesh.uv2, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, q.invalidFaces); Assert.AreEqual(0, q.degenerateFaces); Assert.AreEqual(0, q.outOfBoundsVertices);
            Assert.LessOrEqual(q.worstAnisotropy, 4.001); Assert.IsTrue(q.overlapScanComplete);
            if (!overlaps) Assert.AreEqual(0, q.overlapPairs);
        }

        [Test] public void FragmentedUv0InheritsOneContinuousParentChart()
        {
            var seed = Quad(); var a = Quad(0, 0, .5f); var b = Quad(.5f, 0, .5f);
            // Disconnected UV0 layouts and separate renderer inputs cannot influence surface projection.
            a.uv = a.uv.Select(p => p * 7 + Vector2.one * 12).ToArray();
            b.uv = b.uv.Select(p => -p * 3).ToArray();
            var beforeA = a.uv; var beforeSeed = seed.uv2;
            using var result = Build(Options(), Level(1, seed), Level(0, a, b));
            Assert.AreEqual(4, result.report.inheritedFaces); Assert.AreEqual(0, result.report.newFaces);
            Assert.AreEqual(128, result.report.atlasSize);
            foreach (var output in result.meshes[1])
            {
                Clean(output);
                for (int i = 0; i < output.vertexCount; ++i)
                    Assert.Less(Vector2.Distance(output.uv2[i], new Vector2(output.vertices[i].x, output.vertices[i].y) * .5f + Vector2.one * .25f), 1e-6f);
            }
            CollectionAssert.AreEqual(beforeA, a.uv); CollectionAssert.AreEqual(beforeSeed, seed.uv2);
            Assert.AreEqual(result.report.nodes[1].faces[0].chart, result.report.nodes[2].faces[0].chart);
        }

        [Test] public void ChainAppendsNewSurfacesAndNormalizesAllLodsTogether()
        {
            var seed = Quad(); var middle = Combine(Quad(), Quad(3)); var fine = Combine(Quad(), Quad(3), Quad(6));
            using var result = Build(Options(), Level(2, seed), Level(1, middle), Level(0, fine));
            Assert.AreEqual(6, result.report.inheritedFaces); Assert.AreEqual(4, result.report.newFaces);
            Assert.Greater(result.report.atlasSize, 128); Assert.AreEqual(64f, result.report.texelsPerUnit, .001f);
            var m = result.meshes[1][0]; var f = result.meshes[2][0];
            for (int i = 0; i < 8; ++i)
            {
                var position = middle.vertices[i];
                var mu = m.uv2[Array.FindIndex(m.vertices, p => p == position)];
                var fu = f.uv2[Array.FindIndex(f.vertices, p => p == position)];
                Assert.Less(Vector2.Distance(mu, fu), 1e-6f, "Inherited atlas coordinates moved.");
            }
            Assert.AreEqual(1, result.report.nodes[2].faces[2].parentLod);
            foreach (var level in result.meshes) foreach (var mesh in level) Clean(mesh);
        }

        [TestCase(false)] [TestCase(true)]
        public void RaisedDetailOverlapIsExplicitAndOptional(bool allow)
        {
            var seed = Quad(); var detail = Quad(.25f, .02f, .25f);
            using var result = Build(Options(allow), Level(1, seed), Level(0, Quad(), detail));
            var node = result.report.nodes[2];
            Assert.AreEqual(allow ? 2 : 0, node.inheritedFaces);
            Assert.AreEqual(allow ? 0 : 2, node.newFaces);
            Assert.AreEqual(allow ? 2 : 0, node.overlapFaces);
            foreach (var face in node.faces)
            {
                Assert.AreEqual(allow, face.intentionalOverlap);
                Assert.AreEqual(allow ? 1 : 0, face.layer);
                if (allow) { Assert.AreEqual(0, face.overlayMesh); Assert.AreEqual(1, face.parentLod); }
                else { Assert.AreEqual(-1, face.parentLod); Assert.AreEqual(-1, face.overlayFace); }
            }
            foreach (var mesh in result.meshes[1]) Clean(mesh);
            var json = ReverseUvAudit.Provenance(result.report);
            var saved = JsonUtility.FromJson<ReverseUvAudit.ProvenanceEntry>(json[2]);
            Assert.AreEqual(3, saved.chain.Length); Assert.AreEqual(allow, saved.surface.faces[0].intentionalOverlap);
            Assert.AreEqual(allow, saved.overlaps.Length > 0);
        }

        [Test] public void OverlapLayerSurvivesTheNextProjection()
        {
            var seed = Quad(); var middle = new[] { Quad(), Quad(.25f, .02f, .25f) };
            var fine = new[] { Quad(0,.005f), Quad(.25f, .02f, .25f) };
            using var result = Build(Options(true), Level(2, seed), Level(1, middle), Level(0, fine));
            Assert.AreEqual(1, result.report.nodes[4].faces[0].layer, "Repeated LOD projection must not increment an existing layer.");
            Assert.AreEqual(1, result.report.nodes[4].faces[0].parentMesh);
            Assert.AreEqual(0, result.report.nodes[3].faces[0].layer,"A closer detail must not reorder the parent layer.");
        }

        [TestCase(true)] [TestCase(false)]
        public void OppositeNormalOrDistantSurfaceGetsNewUv(bool opposite)
        {
            var target = Quad(opposite ? 0 : 4);
            if (opposite) target.triangles = target.triangles.Reverse().ToArray();
            using var result = Build(Options(), Level(1, Quad()), Level(0, target));
            Assert.AreEqual(0, result.report.inheritedFaces); Assert.AreEqual(2, result.report.newFaces); Clean(result.meshes[1][0]);
        }

        [Test] public void TriangleAcrossAParentUvSeamDoesNotCreateNeedles()
        {
            var seed = Quad(); var uv = seed.uv2;
            // Separate the two donor faces in UV by duplicating their shared corners.
            var source = ReverseUvMesh.Copy(seed, seed.triangles, new[] { uv[0], uv[1], uv[2], uv[0] + Vector2.right * .5f, uv[2] + Vector2.right * .5f, uv[3] + Vector2.right * .5f });
            owned.Add(source);
            // Fit both charts into one atlas; seam remains discontinuous.
            source.uv2 = source.uv2.Select(p => p * .6f).ToArray();
            var target = Quad(); target.Clear(); target.vertices = new[] { new Vector3(.1f,.1f,0), new Vector3(.9f,.1f,0), new Vector3(.1f,.9f,0) };
            target.triangles = new[] { 0, 1, 2 }; target.uv = new[] { Vector2.zero,Vector2.right,Vector2.up }; target.uv2 = target.uv;
            using var result = Build(Options(), Level(1, source), Level(0, target));
            Assert.AreEqual(0, result.report.inheritedFaces); Assert.AreEqual(1, result.report.newFaces);
            Assert.Greater(result.report.ambiguousFaces, 0); Clean(result.meshes[1][0]);
        }

        [Test] public void CoincidentParentChartsAreAmbiguous()
        {
            var a = Quad(); var b = Quad();
            a.uv2 = a.uv2.Select(p => p * .4f).ToArray(); b.uv2 = b.uv2.Select(p => p * .4f + Vector2.right * .5f).ToArray();
            using var result = Build(Options(), Level(1, a, b), Level(0, Quad()));
            Assert.AreEqual(0, result.report.inheritedFaces); Assert.AreEqual(2, result.report.ambiguousFaces);
        }

        [Test] public void ParentHoleBetweenInteriorProbesCannotBeBorrowed()
        {
            var seed = new Mesh { name = "Perforated" }; owned.Add(seed);
            var positions = new List<Vector3>(); var indices = new List<int>();
            for (int y=0;y<=10;++y) for(int x=0;x<=10;++x) positions.Add(new Vector3(x*.1f,y*.1f,0));
            for (int y=0;y<10;++y) for(int x=0;x<10;++x)
            {
                if(x==1 && y==1) continue;
                int a=y*11+x; indices.AddRange(new[] {a,a+1,a+12,a,a+12,a+11});
            }
            seed.SetVertices(positions); seed.triangles=indices.ToArray();
            seed.uv2=positions.Select(p=>new Vector2(p.x,p.y)*.5f+Vector2.one*.25f).ToArray();
            var target = new Mesh { name="AcrossHole" }; owned.Add(target);
            target.vertices=new[] {Vector3.zero,Vector3.right,Vector3.up}; target.triangles=new[] {0,1,2};
            using var result = Build(Options(),Level(1,seed),Level(0,target));
            Assert.AreEqual(0,result.report.inheritedFaces); Assert.AreEqual(1,result.report.newFaces);
            Assert.AreEqual(1,result.report.ambiguousFaces); Clean(result.meshes[1][0]);
        }

        [TestCase("budget")] [TestCase("atlas")] [TestCase("seed-overlap")] [TestCase("seed-stretch")]
        [TestCase("order")] [TestCase("degenerate")] [TestCase("reach")]
        public void InvalidOrUnverifiedInputsFailWithoutMutatingSources(string issue)
        {
            var a = Quad(); var b = Quad(4); var options = Options(); var levels = new[] { Level(1,a), Level(0,b) };
            switch(issue)
            {
                case "budget": options.comparisonBudget = 1; b.vertices = a.vertices; break;
                case "atlas": options.maxAtlasSize = 128; break;
                case "seed-overlap": levels[0] = Level(1, a, Quad()); break;
                case "seed-stretch": a.uv2 = a.uv2.Select(p => new Vector2(p.x, p.y * .01f)).ToArray(); break;
                case "order": levels[0].lod = 0; break;
                case "degenerate": b.vertices = new Vector3[4]; break;
                case "reach": options.projectionReach = float.NaN; break;
            }
            var av = a.vertices; var au = a.uv2; var bv = b.vertices;
            Assert.Catch(() => { using var result = Build(options, levels); });
            CollectionAssert.AreEqual(av, a.vertices); CollectionAssert.AreEqual(au, a.uv2); CollectionAssert.AreEqual(bv,b.vertices);
        }

        [Test] public void CancelledBuildProducesNoOutput()
        {
            using var token = new CancellationTokenSource(); token.Cancel();
            Assert.Throws<OperationCanceledException>(() => ReverseUvTransfer.Build(new[] { Level(1,Quad()),Level(0,Quad()) }, Options(), token:token.Token).GetAwaiter().GetResult());
        }

        [Test] public void WorldTransformsAndMirroringAreRespected()
        {
            var seed = Quad(); var target = Quad(); var levels = new[] { Level(1,seed), Level(0,target) };
            var transform = Matrix4x4.TRS(new Vector3(30,2,-7), Quaternion.Euler(15,40,0), new Vector3(-2,2,2));
            levels[0].inputs[0].toWorld = levels[1].inputs[0].toWorld = transform;
            using var result = Build(Options(), levels);
            Assert.AreEqual(2,result.report.inheritedFaces); Assert.AreEqual(32f,result.report.texelsPerUnit,.001f);
            CollectionAssert.AreEqual(seed.uv2, result.meshes[1][0].uv2);
        }

        [Test] public void RepeatedBuildsAreDeterministic()
        {
            var levels = new[] { Level(1,Quad()),Level(0,Combine(Quad(),Quad(4))) };
            using var a = Build(Options(),levels); using var b = Build(Options(),levels);
            Assert.AreEqual(JsonUtility.ToJson(a.report),JsonUtility.ToJson(b.report));
            CollectionAssert.AreEqual(a.meshes[1][0].uv2,b.meshes[1][0].uv2);
        }

        [UnityTest] public IEnumerator AsyncProjectionCompletesOnTheEditorThread()
        {
            var task = ReverseUvTransfer.Build(new[] { Level(1,Quad()),Level(0,Quad()) }, Options(), true);
            while (!task.IsCompleted) yield return null;
            using var result = task.GetAwaiter().GetResult(); Assert.AreEqual(2,result.report.inheritedFaces); Clean(result.meshes[1][0]);
        }

        [UnityTest] public IEnumerator FrozenCapturesProduceIndependentReverseBenchReports()
        {
            string manifests=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_MANIFESTS");
            if(string.IsNullOrEmpty(manifests)) Assert.Ignore("Set MESHLAB_REVERSE_MANIFESTS to replay frozen model captures.");
            string directory=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_OUTPUT") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(),"MeshLabReverseBench");
            string[] baselines=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_BASELINES")?.Split(';');
            int index=0;
            foreach(string manifest in manifests.Split(';'))
            {
                int capture=index++;
                var task=ReverseUvBenchmark.Run(manifest,System.IO.Path.Combine(directory,capture.ToString()));
                while(!task.IsCompleted) yield return null;
                var summary=task.GetAwaiter().GetResult();
                Assert.Greater(summary.accepted+summary.refused,0);
                // Refusal is a recorded prototype limitation, not a successful transfer.
                foreach(var trial in summary.trials.Where(t=>t.accepted))
                {
                    var audit=JsonUtility.FromJson<ReverseUvAudit.Audit>(System.IO.File.ReadAllText(trial.audit));
                    foreach(var node in audit.measurements)
                    {
                        Assert.AreEqual(0,node.invalidFaces); Assert.AreEqual(0,node.degenerateFaces);
                        Assert.AreEqual(0,node.outOfBounds); Assert.IsTrue(node.complete);
                        Assert.LessOrEqual(node.worstAnisotropy,4.001);
                    }
                    foreach(var level in audit.levels)
                    {
                        Assert.IsTrue(level.complete); Assert.AreEqual(0,level.unexpectedOverlapPairs);
                        Assert.Greater(level.texelsPerUnitMin,0); Assert.IsFalse(double.IsInfinity(level.texelsPerUnitMax));
                    }
                }
                foreach(var trial in summary.trials.Where(t=>!t.accepted)) Assert.IsNotEmpty(trial.failureStage);
                if(baselines!=null)
                {
                    Assert.Less(capture,baselines.Length);
                    var baseline=JsonUtility.FromJson<ReverseUvBenchmark.Summary>(System.IO.File.ReadAllText(baselines[capture]));
                    Assert.AreEqual(baseline.manifest,summary.manifest);
                    Assert.AreEqual(baseline.trials.Count,summary.trials.Count);
                    foreach(var previous in baseline.trials.Where(t=>t.accepted))
                    {
                        var current=summary.trials.Single(t=>t.pair==previous.pair && t.allowOverlap==previous.allowOverlap);
                        Assert.AreEqual(previous.target,current.target);
                        Assert.IsTrue(current.accepted,$"Previously accepted pair {current.pair}, overlap {current.allowOverlap}: {current.error}");
                    }
                }
            }
        }

        [TestCase(0,1,2)] [TestCase(1,2,0)] [TestCase(2,0,1)]
        [TestCase(0,2,1)] [TestCase(2,1,0)] [TestCase(1,0,2)]
        public void CapturedCountertopMetricDoesNotDependOnCornerOrder(int a,int b,int c)
        {
            // Exact raw coordinates of Kitchen Countertop LOD1 face 38.
            var positions=new[] {
                new Vector3(.3567301630973816f,.06804550439119339f,-.18836303055286407f),
                new Vector3(.3572298288345337f,.06804550439119339f,-.18836303055286407f),
                new Vector3(.632880687713623f,.06803926080465317f,-.18835678696632385f) };
            // Independent double reference intrinsic coordinates from the capture.
            var uv=new[] {Vector2.zero,new Vector2(.0004996657371520996f,0),new Vector2(.27615052461624146f,8.8297647630323e-6f)};
            Assert.Less(ReverseUvTransfer.TriangleAnisotropy(positions[a],positions[b],positions[c],uv[a],uv[b],uv[c]),1.001);
        }

        [TestCase(1e-12f)] [TestCase(1e-8f)] [TestCase(1)] [TestCase(1e12f)]
        public void TriangleMetricIsScaleIndependentAndDetectsStretch(float size)
        {
            var a=Vector3.zero; var b=Vector3.right*size; var c=Vector3.up*size;
            Assert.AreEqual(1,ReverseUvTransfer.TriangleAnisotropy(a,b,c,Vector2.zero,Vector2.right,Vector2.up),1e-6);
            Assert.AreEqual(8,ReverseUvTransfer.TriangleAnisotropy(a,b,c,Vector2.zero,Vector2.right,new Vector2(0,.125f)),1e-6);
        }

        [Test] public void FloatPlacementCollapseIsNotMistakenForGoodIntrinsicUv()
        {
            var positions=new[] {Vector3.zero,new Vector3(.0004996657371520996f,0,0),
                new Vector3(.27615052461624146f,8.8297647630323e-6f,0)};
            var uv=positions.Select(p=>new Vector2(p.x,p.y)).ToArray();
            Assert.AreEqual(1,ReverseUvTransfer.TriangleAnisotropy(positions[0],positions[1],positions[2],uv[0],uv[1],uv[2]),1e-6);
            uv=uv.Select(p=>p+Vector2.one*4096).ToArray();
            Assert.IsTrue(double.IsPositiveInfinity(ReverseUvTransfer.TriangleAnisotropy(positions[0],positions[1],positions[2],uv[0],uv[1],uv[2])));
        }

        [TestCase(false)] [TestCase(true)]
        public void FarTranslatedChainKeepsUvsAndProjectionRelationships(bool overlap)
        {
            var coarse=Quad(size:.001f); var fine=Quad(size:.001f);
            var near=new[] {Level(1,coarse),Level(0,fine)};
            using var expected=Build(Options(overlap),near);
            var translation=Matrix4x4.Translate(new Vector3(1000000,2000000,-1000000));
            foreach(var level in near) foreach(var input in level.inputs) input.toWorld=translation;
            var points=coarse.vertices.Select(translation.MultiplyPoint3x4).ToArray();
            Assert.IsFalse(MeshGeometry.HasArea(points[0],points[1],points[2]),"Control must reproduce world-coordinate collapse.");
            using var actual=Build(Options(overlap),near);
            for(int i=0;i<2;++i) {
                CollectionAssert.AreEqual(expected.meshes[i][0].uv2,actual.meshes[i][0].uv2);
                var source=i==0?coarse:fine;
                var output=actual.meshes[i][0];
                CollectionAssert.AreEqual(source.triangles.Select(v=>source.vertices[v]),output.triangles.Select(v=>output.vertices[v]));
            }
            Assert.AreEqual(expected.report.inheritedFaces,actual.report.inheritedFaces);
        }

        [Test] public void SeedPreparationUsesTheSameRelativeWorldMetric()
        {
            var mesh=Quad(size:.001f); var input=new ReverseUvTransfer.Input {mesh=mesh,key="SmallSeed"};
            var first=ReverseUvSeed.Prepare(new[] {input},128,2,0,default,out int firstSize);
            owned.AddRange(first);
            input.toWorld=Matrix4x4.Translate(new Vector3(1000000,2000000,-1000000));
            var second=ReverseUvSeed.Prepare(new[] {input},128,2,0,default,out int secondSize);
            owned.AddRange(second);
            Assert.AreEqual(firstSize,secondSize);CollectionAssert.AreEqual(first[0].uv2,second[0].uv2);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void CapturedKamazThinFaceSurvivesWorldTranslation(int transform)
        {
            var mesh=new Mesh {name="Kamaz_Typhoon_LOD1"};owned.Add(mesh);
            mesh.vertices=new[] {
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.8186256885528564f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.8186254501342773f),
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.8186254501342773f) };
            mesh.triangles=new[] {0,1,2};
            var input=new ReverseUvTransfer.Input {mesh=mesh,toWorld=Matrix4x4.Translate(new Vector3(52.7f,-.07f,85.75f))};
            if(transform==1) input.toWorld=new Matrix4x4(
                new Vector4(-.04123282432556152f,-8.881784197001252e-15f,-.9991496801376343f,0),
                new Vector4(4.36742055853756e-8f,1,-1.8023467163175155e-9f,0),
                new Vector4(.9991496801376343f,-4.3711374075883214e-8f,-.04123282432556152f,0),
                new Vector4(-47.790000915527344f,-.05875444412231445f,-104.54000091552734f,1));
            if(transform==2) input.toWorld=new Matrix4x4(
                new Vector4(-.8483419418334961f,-1.5260752661561128e-9f,-.5294487476348877f,0),
                new Vector4(2.1834194896541703e-8f,1,-3.786757574175681e-8f,0),
                new Vector4(.5294487476348877f,-4.3684739381433246e-8f,-.8483419418334961f,0),
                new Vector4(52.70000076293945f,-.06999999284744263f,85.75f,1));
            var rounded=mesh.vertices.Select(input.toWorld.MultiplyPoint3x4).ToArray();
            Assert.IsFalse(MeshGeometry.HasArea(rounded[0],rounded[1],rounded[2]));
            var relative=ReverseUvTransfer.RelativePositions(input,input.toWorld.GetColumn(3),1);
            if(transform==0) CollectionAssert.AreEqual(mesh.vertices,relative);
            Assert.IsTrue(MeshGeometry.HasArea(relative[0],relative[1],relative[2]));
        }

        [Test] public void CapturedKamazCollinearFaceRefusesBeforeRoundingCanInventArea()
        {
            var mesh=new Mesh {name="Kamaz_Typhoon_LOD1"};owned.Add(mesh);
            mesh.vertices=new[] {
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.723873257637024f) };
            mesh.triangles=new[] {0,1,2};
            var input=new ReverseUvTransfer.Input {mesh=mesh,toWorld=Matrix4x4.TRS(new Vector3(48.349f,-.113f,-95.336f),Quaternion.Euler(1,224,0),Vector3.one)};
            var before=TransferMeshSnapshot.Capture(mesh);
            var error=Assert.Throws<InvalidOperationException>(()=>ReverseUvTransfer.RelativePositions(input,input.toWorld.GetColumn(3),1));
            StringAssert.Contains("source-local coordinates",error.Message);
            StringAssert.Contains("vertices 0, 1, 2",error.Message);
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(mesh));
        }

        [Test] public void CapturedNanometreBacksplashRefusesPublicationWithoutChangingGeometry()
        {
            var fine=new Mesh {name="Backsplash nanometre face"}; owned.Add(fine);
            fine.vertices=new[] {
                new Vector3(-.013632268644869328f,.06390988081693649f,-.1929730921983719f),
                new Vector3(-.7002735733985901f,.06392237544059753f,-.19298714399337769f),
                new Vector3(-.7007233500480652f,.06392237544059753f,-.19298714399337769f) };
            fine.triangles=new[] {0,1,2}; fine.uv=new[] {Vector2.zero,Vector2.right,Vector2.up};
            // A normal neighbouring surface keeps Unity's connected unwrap alive;
            // the nanometre triangle must then fail the final placement gate.
            fine=Combine(fine,Quad(8));
            var before=TransferMeshSnapshot.Capture(fine);
            Assert.IsTrue(MeshGeometry.HasArea(fine.vertices[0],fine.vertices[1],fine.vertices[2]));
            Assert.Throws<InvalidOperationException>(()=> {using var result=Build(Options(),Level(1,Quad(4)),Level(0,fine));});
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(fine));
        }

        [Test] public void AuditRefusesStretchedFinalUvEvenWhenOverlapScanIsClean()
        {
            var levels=new[] {Level(1,Quad()),Level(0,Quad())};
            using var result=Build(Options(),levels);
            var output=result.meshes[1][0];
            output.uv2=output.uv2.Select(p=>new Vector2(p.x*.01f,p.y)).ToArray();
            var quality=TransferUvQuality.Measure(output,output.uv2,Vector2.one,Matrix4x4.identity);
            Assert.IsTrue(quality.overlapScanComplete); Assert.AreEqual(0,quality.overlapPairs);
            Assert.Greater(quality.worstAnisotropy,4);
            Assert.Throws<InvalidOperationException>(()=>ReverseUvAudit.Write(result,levels));
        }

        [Test] public void SplitsPreserveCornerAttributesSubmeshesBlendShapesAndSkinWeights()
        {
            var mesh = Quad(); mesh.subMeshCount = 2; mesh.SetTriangles(new[] {0,1,2},0); mesh.SetTriangles(new[] {0,2,3},1);
            mesh.tangents = new[] { Vector4.one,new Vector4(1,0,0,-1),Vector4.one,Vector4.one };
            mesh.colors32 = new[] {new Color32(1,2,3,4),new Color32(5,6,7,8),new Color32(9,10,11,12),new Color32(13,14,15,16)};
            mesh.SetUVs(3,new List<Vector4> {Vector4.one,Vector4.one*2,Vector4.one*3,Vector4.one*4});
            var delta = mesh.vertices.Select(p => p + Vector3.forward).ToArray(); mesh.AddBlendShapeFrame("Detail",50,delta,delta,delta);
            mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity,5).ToArray();
            using (var counts = new NativeArray<byte>(new byte[] {5,1,1,1},Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(new[] {
                new BoneWeight1 {boneIndex=0,weight=.4f},new BoneWeight1 {boneIndex=1,weight=.3f},new BoneWeight1 {boneIndex=2,weight=.15f},
                new BoneWeight1 {boneIndex=3,weight=.1f},new BoneWeight1 {boneIndex=4,weight=.05f},new BoneWeight1 {boneIndex=0,weight=1},
                new BoneWeight1 {boneIndex=0,weight=1},new BoneWeight1 {boneIndex=0,weight=1}},Allocator.Temp)) mesh.SetBoneWeights(counts,weights);
            var corners = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.one*.1f,Vector2.one*.2f,Vector2.up };
            var output = ReverseUvMesh.Copy(mesh,mesh.triangles,corners); owned.Add(output);
            Assert.AreEqual(6,output.vertexCount); Assert.AreEqual(2,output.subMeshCount);
            var original = mesh.triangles; var mapped = output.triangles;
            var uv3 = new List<Vector4>(); mesh.GetUVs(3,uv3); var newUv3=new List<Vector4>(); output.GetUVs(3,newUv3);
            var newDelta = new Vector3[6]; output.GetBlendShapeFrameVertices(0,0,newDelta,null,null);
            using var newCounts = output.GetBonesPerVertex(); using var newWeights = output.GetAllBoneWeights();
            for(int i=0;i<6;++i)
            {
                int old = original[i], v = mapped[i];
                Assert.AreEqual(mesh.vertices[old],output.vertices[v]); Assert.AreEqual(mesh.normals[old],output.normals[v]);
                Assert.AreEqual(mesh.tangents[old],output.tangents[v]); Assert.AreEqual(mesh.colors32[old],output.colors32[v]);
                Assert.AreEqual(mesh.uv[old],output.uv[v]); Assert.AreEqual(uv3[old],newUv3[v]); Assert.AreEqual(delta[old],newDelta[v]);
                Assert.AreEqual(old==0?5:1,newCounts[v]); Assert.AreEqual(corners[i],output.uv2[v]);
            }
            Assert.AreEqual(14,newWeights.Length); Assert.AreEqual(4,mesh.vertexCount);
        }

        [TestCase(VertexAttributeFormat.Float16,2)] [TestCase(VertexAttributeFormat.Float16,4)]
        [TestCase(VertexAttributeFormat.Float32,1)]
        public void RawUvAndColorBytesSurviveUv2Splits(VertexAttributeFormat format,int dimension)
        {
            var source=new Mesh {name="Raw channels"}; owned.Add(source);
            source.SetVertexBufferParams(4,new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3,0),
                new VertexAttributeDescriptor(VertexAttribute.Color,VertexAttributeFormat.UNorm8,4,1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0,format,dimension,1));
            var geometry=new[] {0f,0,0,1,0,0,1,1,0,0,1,0}; source.SetVertexBufferData(geometry,0,0,geometry.Length,0);
            int stride=source.GetVertexBufferStride(1),offset=source.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
            var bytes=new byte[stride*4]; int componentSize=format==VertexAttributeFormat.Float16?2:4;
            for(int v=0;v<4;++v)
            {
                bytes[v*stride]=(byte)(v*37); bytes[v*stride+3]=255;
                for(int c=0;c<dimension;++c)
                {
                    var value=format==VertexAttributeFormat.Float16?BitConverter.GetBytes((ushort)(v==0?0x8000:0x3800+v*32+c)):
                        BitConverter.GetBytes(v==0?BitConverter.Int32BitsToSingle(int.MinValue):v*.123456789f);
                    Buffer.BlockCopy(value,0,bytes,v*stride+offset+c*componentSize,componentSize);
                }
            }
            source.SetVertexBufferData(bytes,0,0,bytes.Length,1); source.triangles=new[] {0,1,2,0,2,3,0,0,0};
            using var prepared=ReverseUvInputs.Prepare(new[] {Level(1,source)});
            var cleaned=prepared.levels[0].inputs[0].mesh;
            using (var cleanData=Mesh.AcquireReadOnlyMeshData(cleaned))
                CollectionAssert.AreEqual(bytes,cleanData[0].GetVertexData<byte>(1).ToArray());
            var output=ReverseUvMesh.Copy(cleaned,cleaned.triangles,new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.one*.1f,Vector2.one*.2f,Vector2.up}); owned.Add(output);
            Assert.AreEqual(format,output.GetVertexAttributeFormat(VertexAttribute.TexCoord0)); Assert.AreEqual(dimension,output.GetVertexAttributeDimension(VertexAttribute.TexCoord0));
            using var data=Mesh.AcquireReadOnlyMeshData(output);
            int stream=output.GetVertexAttributeStream(VertexAttribute.TexCoord0),newStride=output.GetVertexBufferStride(stream),newOffset=output.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
            var raw=data[0].GetVertexData<byte>(stream);
            for(int i=0;i<6;++i)
                for(int c=0;c<dimension*componentSize;++c)
                    Assert.AreEqual(bytes[source.triangles[i]*stride+offset+c],raw[output.triangles[i]*newStride+newOffset+c]);
            Assert.AreEqual(VertexAttributeFormat.UNorm8,output.GetVertexAttributeFormat(VertexAttribute.Color));
        }

        [Test] public void SidecarRetainsResultUvAndAncestryAndClearsItOnLegacyReplacement()
        {
            var original=Quad(); var working=Object.Instantiate(original); owned.Add(working);
            var result=Object.Instantiate(original); owned.Add(result); result.uv2=result.uv2.Select(p=>p*.3f).ToArray();
            var entry=new MeshEntry { fbxMesh=original, originalMesh=working, transferredMesh=result, reverseTransferJson="ancestry" };
            Assert.IsTrue(SidecarStore.TryBuildEntry(entry,result,false,SidecarStore.AoUvTarget.None,out var saved));
            CollectionAssert.AreEqual(result.uv2,saved.uv2);
            var asset=ScriptableObject.CreateInstance<Uv2DataAsset>();
            try
            {
                asset.Set(saved); Assert.AreEqual("ancestry",asset.Find(original.name).reverseTransferJson);
                saved.reverseTransferJson="replacement"; asset.Set(saved); Assert.AreEqual("replacement",asset.Find(original.name).reverseTransferJson);
                asset.Set(original.name,original.uv2); Assert.IsNull(asset.Find(original.name).reverseTransferJson);
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test] public void LocalRescueKeepsValidChartsAndUnwrapsNeedlesWithExactMetric()
        {
            var corners=new[] {Vector3.zero,Vector3.right,Vector3.up,new Vector3(3,0,0),new Vector3(4,0,0),new Vector3(3,1,0)};
            var uv=new[] {Vector2.zero,Vector2.right,Vector2.up,new Vector2(2,0),new Vector2(3,0),new Vector2(2,.00001f)};
            var output=ReverseUvNewCharts.Prepare(corners,uv,64,Options(),default,out var fallback);
            CollectionAssert.AreEqual(new[] {false,true},fallback);
            Assert.AreEqual(64f,(output[1]-output[0]).magnitude,.001f);
            Assert.AreEqual(64f,(output[4]-output[3]).magnitude,.001f);
            Assert.AreEqual(64f,(output[5]-output[3]).magnitude,.001f);
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=output,indices=new[] {0,1,2,3,4,5},charts=new int[6]},default);
            Assert.AreEqual(0,scan.pairs); Assert.AreEqual(0,scan.degenerateFaces);
        }

        [TestCase(0)] [TestCase(32)]
        public void SeedPreparationIgnoresStretchedTextureUvAndUsesOneWorldMetric(int density)
        {
            var a=Quad(); var b=Quad(3); a.uv=a.uv.Select(p=>new Vector2(p.x,p.y*.5f)).ToArray();
            var transform=Matrix4x4.Scale(new Vector3(1,2,1)); var before=a.uv;
            var inputs=new[] {new ReverseUvTransfer.Input {mesh=a,toWorld=transform},new ReverseUvTransfer.Input {mesh=b,toWorld=transform}};
            var outputs=ReverseUvSeed.Prepare(inputs,128,2,density,default,out int side);
            owned.AddRange(outputs);
            var levels=new[] {new ReverseUvTransfer.Level {lod=1,inputs=inputs.Select((input,i)=>new ReverseUvTransfer.Input {mesh=outputs[i],toWorld=transform}).ToArray()},
                new ReverseUvTransfer.Level {lod=0,inputs=inputs}};
            using var result=Build(new ReverseUvTransfer.Options {seedResolution=side,projectionReach=.1f},levels);
            Assert.AreEqual(4,result.report.inheritedFaces); Assert.AreEqual(0,result.report.newFaces);
            foreach(var mesh in outputs)
            {
                var quality=TransferUvQuality.Measure(mesh,mesh.uv2,Vector2.one,transform);
                Assert.Less(quality.worstAnisotropy,1.05);
            }
            if(density>0) Assert.AreEqual(density,result.report.texelsPerUnit,.1f);
            CollectionAssert.AreEqual(before,a.uv);
        }
    }
}
