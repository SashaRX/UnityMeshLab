using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class UvJunctionCutsTests
    {
        [Serializable] sealed class FrozenReport
        {
            public string mesh;
            public int cuts, chartsBefore, chartsAfter;
            public uint atlasBefore, atlasAfter;
            public TransferUvQuality qualityBefore, qualityAfter;
            public Vector2[] uvBefore, uvAfter;
            public int[] trianglesBefore, trianglesAfter;
            public int[] faceCharts;
        }

        [Serializable] sealed class StretchTrial
        {
            public string mesh;
            public int shell, faces;
            public float l2;
            public bool arap;
            public TransferCandidateQuality before, after;
        }
        [Serializable] sealed class StretchTrials { public List<StretchTrial> trials = new List<StretchTrial>(); }

        [Test]
        public void FrozenForwardShellStretchDiagnostics()
        {
            string paths = Environment.GetEnvironmentVariable("MESHLAB_STRETCH_ASSETS");
            if (string.IsNullOrEmpty(paths)) Assert.Ignore("Optional private stretch fixtures are not configured.");
            var report = new StretchTrials();
            foreach (string path in paths.Split(';'))
                foreach (var original in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>())
                {
                    using var prepared = ReverseUvInputs.Prepare(new[] {new ReverseUvTransfer.Level {lod=0,
                        inputs=new[] {new ReverseUvTransfer.Input {mesh=original,key=original.name}}}});
                    var mesh=prepared.levels[0].inputs[0].mesh;
                    var snapshot = TransferMeshSnapshot.Capture(mesh);
                    var positions = mesh.vertices; var indices = mesh.triangles; var uv = mesh.uv;
                    if (uv.Length != positions.Length) continue;
                    var flat = uv.SelectMany(p => new[] {p.x,p.y}).ToArray();
                    foreach (var shell in UvShellExtractor.Extract(uv,indices))
                    {
                        var candidate = (float[])flat.Clone();
                        Dictionary<int,Vector2> Coordinates(float[] data) => shell.vertexIndices.ToDictionary(v=>v,v=>new Vector2(data[v*2],data[v*2+1]));
                        var trial = new StretchTrial {mesh=mesh.name,shell=shell.shellId,faces=shell.faceIndices.Count,
                            l2=ShellQuality.ComputeL2Stretch(shell,indices,positions,flat),
                            before=TransferCandidateQuality.Measure(shell,indices,positions,Coordinates(flat))};
                        trial.arap=ArapParameterization.Reparameterize(positions,indices,shell.faceIndices.ToArray(),shell.vertexIndices,candidate,50,out _);
                        trial.after=TransferCandidateQuality.Measure(shell,indices,positions,Coordinates(candidate));
                        if (trial.before.worstAnisotropy>4) report.trials.Add(trial);
                    }
                    CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(mesh));
                }
            string output = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_OUTPUT");
            Assert.IsNotEmpty(output); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,"shell-stretch.json"),JsonUtility.ToJson(report,true));
        }

        static Mesh Grid(string pattern, int subdivisions = 1)
        {
            var rows = pattern.Split('/'); var vertices = new List<Vector3>();
            var indices = new List<int>(); var unique = new Dictionary<Vector3, int>();
            for (int y = 0; y < rows.Length; ++y)
                for (int x = 0; x < rows[y].Length; ++x)
                {
                    if (rows[y][x] != '#') continue;
                    for (int sy = 0; sy < subdivisions; ++sy)
                    for (int sx = 0; sx < subdivisions; ++sx)
                    {
                        float left = x + sx / (float)subdivisions, bottom = y + sy / (float)subdivisions;
                        float right = x + (sx + 1) / (float)subdivisions, top = y + (sy + 1) / (float)subdivisions;
                        var cell = new[] { new Vector3(left, bottom), new Vector3(right, bottom), new Vector3(right, top), new Vector3(left, top) };
                        var ids = new int[4];
                        for (int k = 0; k < 4; ++k)
                        {
                            if (!unique.TryGetValue(cell[k], out int v)) { v = vertices.Count; unique.Add(cell[k], v); vertices.Add(cell[k]); }
                            ids[k] = v;
                        }
                        foreach (int k in new[] { 0, 1, 2, 0, 2, 3 }) indices.Add(ids[k]);
                    }
                }
            var mesh = new Mesh { name = "Junction fixture" };
            mesh.SetVertices(vertices); mesh.triangles = indices.ToArray();
            mesh.uv = vertices.Select(p => new Vector2(p.x, p.y)).ToArray();
            mesh.uv2 = mesh.uv; mesh.RecalculateNormals();
            return mesh;
        }

        [TestCase("#...#/#...#/#...#/#...#/#####", 0f)] // U
        [TestCase("#####/..#../..#../..#../..#..", 17f)] // T
        [TestCase("#####/#..../#..../####./#..../#..../#....", 43f)] // F
        [TestCase("#####/#..../#..../####./#..../#..../#####", 73f)] // E
        [TestCase("#...#/#...#/#...#/#####/#...#/#...#/#...#", 91f)] // H
        [TestCase("#######/#.....#/#.....#/#.....#/#.....#/#.....#/#######", 137f)] // O
        [TestCase("...#.../...#.../...#.../#######/...#.../...#.../...#...", 45f)] // X
        [TestCase("#####/#..../#..../#..../#####", 211f)] // C
        public void JunctionPathsCrossTessellationAndPreserveMetric(string pattern, float angle)
        {
            var mesh = Grid(pattern, 3);
            try
            {
                float a = angle * Mathf.Deg2Rad;
                mesh.uv = mesh.uv.Select(p => new Vector2(Mathf.Cos(a) * p.x - Mathf.Sin(a) * p.y,
                    Mathf.Sin(a) * p.x + Mathf.Cos(a) * p.y)).ToArray();
                var plan = UvJunctionCuts.Find(mesh.uv, mesh.triangles);
                Assert.AreEqual(1, plan.originalCharts); Assert.Greater(plan.charts, 1); Assert.Greater(plan.cuts, 1);
                var corners = mesh.triangles.Select(i => mesh.vertices[i]).ToArray();
                var pixels = mesh.triangles.Select(i => mesh.uv[i] * 8).ToArray();
                var packed = UvJunctionCuts.Pack(corners, pixels, 2, default);
                var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = packed,
                    indices = Enumerable.Range(0, packed.Length).ToArray(), charts = new int[packed.Length] }, default);
                Assert.IsTrue(scan.complete); Assert.AreEqual(0, scan.pairs); Assert.AreEqual(0, scan.degenerateFaces);
                for (int t = 0; t < pixels.Length; t += 3)
                    Assert.Less(ReverseUvTransfer.TriangleAnisotropy(corners[t], corners[t + 1], corners[t + 2],
                        packed[t], packed[t + 1], packed[t + 2]), 1.001);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase("#####/..#../..#../..#../..#..", 2)]
        [TestCase("#...#/#...#/#...#/#####/#...#/#...#/#...#", 3)]
        [TestCase("#...#/#...#/#...#/#...#/#####", 3)]
        [TestCase("#..../#..../#..../#..../#####", 2)]
        [TestCase("#######/#.....#/#.....#/#.....#/#.....#/#.....#/#######", 4)]
        public void ReflexJunctionsBecomeCompactStrips(string shape, int charts)
        {
            var mesh = Grid(shape);
            try
            {
                var plan = UvJunctionCuts.Find(mesh.uv, mesh.triangles);
                Assert.AreEqual(1, plan.originalCharts); Assert.AreEqual(charts, plan.charts);
                Assert.Greater(plan.cuts, 0);
                var positions = mesh.vertices; var uv = mesh.uv; var triangles = mesh.triangles;
                var corners = triangles.Select(i => positions[i]).ToArray();
                var pixels = triangles.Select(i => uv[i] * 8).ToArray();
                var packed = UvJunctionCuts.Pack(corners, pixels, 2, default);
                var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = packed,
                    indices = Enumerable.Range(0, packed.Length).ToArray(), charts = new int[packed.Length] }, default);
                Assert.IsTrue(scan.complete); Assert.AreEqual(0, scan.pairs); Assert.AreEqual(0, scan.degenerateFaces);
                for (int f = 0; f < triangles.Length / 3; ++f)
                {
                    int t = f * 3;
                    Assert.AreEqual(1, ReverseUvTransfer.TriangleAnisotropy(corners[t], corners[t + 1], corners[t + 2], packed[t], packed[t + 1], packed[t + 2]), 1e-4);
                    Assert.AreEqual((pixels[t + 1] - pixels[t]).magnitude, (packed[t + 1] - packed[t]).magnitude, 1e-4);
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase("#######")]
        [TestCase("####/####/####/####")]
        [TestCase("######/######")]
        public void ConvexChartsRetainTheirExistingVertices(string shape)
        {
            var mesh = Grid(shape);
            try { Assert.IsNull(UvJunctionCuts.CopyForRepack(mesh, Vector2.one)); }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase(.001f, 17f, false)] [TestCase(1f, 43f, true)] [TestCase(1000f, 131f, false)]
        public void CutsRespectRotationScaleReflectionAndKeepEveryCornerAttribute(float scale, float angle, bool mirror)
        {
            var mesh = Grid("#######/#.....#/#.....#/#.....#/#.....#/#.....#/#######"); Mesh copy = null;
            try
            {
                float a = angle * Mathf.Deg2Rad;
                mesh.uv = mesh.uv.Select(p => scale * new Vector2(Mathf.Cos(a) * p.x - Mathf.Sin(a) * p.y,
                    (mirror ? -1 : 1) * (Mathf.Sin(a) * p.x + Mathf.Cos(a) * p.y))).ToArray();
                mesh.tangents = mesh.vertices.Select((p, i) => new Vector4(1, 0, 0, i % 2 == 0 ? 1 : -1)).ToArray();
                var positions = mesh.vertices; var uv = mesh.uv; var tangents = mesh.tangents; var indices = mesh.triangles;
                var plan = UvJunctionCuts.Find(uv, indices); Assert.AreEqual(4, plan.charts);
                copy = UvJunctionCuts.CopyForRepack(mesh, Vector2.one);
                Assert.IsNotNull(copy); Assert.Greater(copy.vertexCount, mesh.vertexCount);
                var actual = copy.triangles;
                for (int i = 0; i < indices.Length; ++i)
                {
                    Assert.AreEqual(positions[indices[i]], copy.vertices[actual[i]]);
                    Assert.AreEqual(uv[indices[i]], copy.uv[actual[i]]);
                    Assert.AreEqual(tangents[indices[i]], copy.tangents[actual[i]]);
                }
                CollectionAssert.AreEqual(indices, mesh.triangles); CollectionAssert.AreEqual(uv, mesh.uv);
            }
            finally { if (copy) Object.DestroyImmediate(copy); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void CancelledSearchDoesNotCreateAnOutputMesh()
        {
            var mesh = Grid("#######/#.....#/#.....#/#.....#/#.....#/#.....#/#######");
            try { Assert.Throws<OperationCanceledException>(() => UvJunctionCuts.CopyForRepack(mesh, Vector2.one, new CancellationToken(true))); }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void RealXatlasRepackKeepsTheCutChartsAndSourceUv()
        {
            var mesh = Grid("#######/#.....#/#.....#/#.....#/#.....#/#.....#/#######"); Mesh copy = null;
            try
            {
                copy = UvJunctionCuts.CopyForRepack(mesh, Vector2.one);
                var before = copy.uv; var options = RepackOptions.Default;
                options.resolution = 128; options.bruteForce = false; options.blockAlign = false;
                var result = XatlasRepack.RepackSingle(copy, options);
                Assert.IsTrue(result.ok, result.error); Assert.AreEqual(4, result.chartCount);
                CollectionAssert.AreEqual(before, copy.uv);
                var q = TransferUvQuality.Measure(copy, copy.uv2, Vector2.one, Matrix4x4.identity);
                Assert.AreEqual(0, q.overlapPairs); Assert.AreEqual(0, q.degenerateFaces);
            }
            finally { if (copy) Object.DestroyImmediate(copy); Object.DestroyImmediate(mesh); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ReverseTrialRetainsEveryInheritedFaceAndOriginalMesh(bool overlap)
        {
            var mesh = Grid("#######/#.....#/#.....#/#.....#/#.....#/#.....#/#######");
            try
            {
                var snapshot = TransferMeshSnapshot.Capture(mesh);
                var sources = new[] { new ReverseUvTransfer.Level { lod = 1, inputs = new[] {
                    new ReverseUvTransfer.Input { mesh = mesh, key = "frame" } } },
                    new ReverseUvTransfer.Level { lod = 0, inputs = new[] {
                    new ReverseUvTransfer.Input { mesh = mesh, key = "frame" } } } };
                using var baseline = ReverseUvJunctionTrial.Build(sources, new ReverseUvTransfer.Options {
                    seedResolution = 128, padding = 2, preserveProjectedOverlap = overlap }, true, 8).GetAwaiter().GetResult();
                using var trial = ReverseUvJunctionTrial.Build(sources, new ReverseUvTransfer.Options {
                    seedResolution = 128, padding = 2, cutNarrowJunctions = true, preserveProjectedOverlap = overlap }, true, 8).GetAwaiter().GetResult();
                Assert.IsNull(ReverseUvJunctionTrial.Compare(baseline, trial));
                Assert.AreEqual(mesh.triangles.Length / 3, trial.result.report.inheritedFaces);
                CollectionAssert.AreEqual(snapshot, TransferMeshSnapshot.Capture(mesh));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ReverseGuardRejectsLostFacesEvenWhenTotalInheritanceIsUnchanged()
        {
            using var baseline = new ReverseUvJunctionTrial.PreparedResult { result = new ReverseUvTransfer.Result {
                report = new ReverseUvTransfer.Report { atlasSize = 128, texelsPerUnit = 8 } } };
            using var candidate = new ReverseUvJunctionTrial.PreparedResult { result = new ReverseUvTransfer.Result {
                report = new ReverseUvTransfer.Report { atlasSize = 128, texelsPerUnit = 8 } } };
            baseline.result.report.nodes.Add(new ReverseUvTransfer.NodeReport { lod = 0, key = "frame",
                faces = new[] { new ReverseUvTransfer.Face { inherited = true }, new ReverseUvTransfer.Face() } });
            candidate.result.report.nodes.Add(new ReverseUvTransfer.NodeReport { lod = 0, key = "frame",
                faces = new[] { new ReverseUvTransfer.Face(), new ReverseUvTransfer.Face { inherited = true } } });
            StringAssert.Contains("face 0 loses inheritance", ReverseUvJunctionTrial.Compare(baseline, candidate));
            candidate.result.report.atlasSize = 256;
            StringAssert.Contains("face 0 loses inheritance", ReverseUvJunctionTrial.Compare(baseline, candidate));
            candidate.result.report.nodes[0].faces[0].inherited = true;
            Assert.IsNull(ReverseUvJunctionTrial.Compare(baseline,candidate),"An expanded canvas must preserve inherited regions without an arbitrary size-equality gate.");
        }

        [TestCase(false)] [TestCase(true)]
        public void FinalPackingMovesInheritedChartsAndTheirOverlaysTogether(bool fill)
        {
            ReverseUvTransfer.Surface Surface(int lod,int chart,Vector2 shift)
            {
                var uv = new[] {shift,shift+Vector2.right*10,shift+new Vector2(10,1),shift,shift+new Vector2(10,1),shift+Vector2.up};
                return new ReverseUvTransfer.Surface {lod=lod,node=0,pixels=uv,
                    positions=uv.Select(p=>new Vector3(p.x,p.y)).ToArray(),indices=Enumerable.Range(0,6).ToArray(),
                    faces=new[] {new ReverseUvTransfer.Face {chart=chart,inherited=lod==0},new ReverseUvTransfer.Face {chart=chart,inherited=lod==0}}};
            }
            var seed=Surface(1,0,new Vector2(2,30)); var fine=Surface(0,0,new Vector2(2,30));
            var overlay=Surface(0,1,new Vector2(2,30)); overlay.node=1;
            foreach (var face in overlay.faces) {face.intentionalOverlap=true;face.overlayMesh=0;face.overlayFace=0;}
            var source=seed.pixels.ToArray(); var geometry=seed.positions.ToArray();
            var report=new ReverseUvTransfer.Report {texelsPerUnit=1};
            report.overlaps.Add(new ReverseUvTransfer.OverlapRelation {lod=0,underMesh=0,underFace=0,overMesh=1,overFace=0});
            report.overlaps.Add(new ReverseUvTransfer.OverlapRelation {lod=0,underMesh=0,underFace=1,overMesh=1,overFace=1});
            int side=ReverseUvFinalPacking.Pack(new List<ReverseUvTransfer.Surface[]> {new[] {seed},new[] {fine,overlay}},
                new ReverseUvTransfer.Options {padding=2,fillFinalAtlas=fill},report,64,default);
            Assert.AreEqual("shared-chart-families",report.finalPacking,report.finalPackingRefusal);
            CollectionAssert.AreEqual(seed.pixels,fine.pixels); CollectionAssert.AreEqual(fine.pixels,overlay.pixels);
            CollectionAssert.AreEqual(geometry,seed.positions);
            Assert.LessOrEqual(side,64);
            if(fill) Assert.Greater(report.texelsPerUnit,1); else Assert.AreEqual(1,report.texelsPerUnit);
            Assert.AreEqual(report.texelsPerUnit,(seed.pixels[1]-seed.pixels[0]).magnitude/(source[1]-source[0]).magnitude,1e-5);
            foreach(var p in seed.pixels) Assert.IsTrue(p.x>=0 && p.y>=0 && p.x<=side && p.y<=side);
        }

        [Test] public void FinalPackingHonoursTheAtlasLimitBeforeItsFirstPlacement()
        {
            var chart = new[] {Vector2.zero,Vector2.right*20,Vector2.one*20};
            Assert.Throws<InvalidOperationException>(() => ReverseUvSeedPacking.PackRigid(new[] {chart},
                new ReverseUvTransfer.Options {maxAtlasSize=16,padding=2},default,out _,out _));
        }

        [TestCase("Cafe_Table_A_01")] [TestCase("WindowsNA_R_1.5")]
        public void FrozenModelRepackPreservesSourceAndAuditsEveryCut(string model)
        {
            string directory = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_FIXTURES");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Optional private FBX fixtures are not configured.");
            string output = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_OUTPUT");
            var meshes = AssetDatabase.LoadAllAssetsAtPath(directory + "/" + model + ".fbx").OfType<Mesh>()
                .Where(m => !m.name.Contains("_COL")).ToArray();
            Assert.IsNotEmpty(meshes);
            foreach (var mesh in meshes)
            {
                var levels = new[] { new ReverseUvTransfer.Level { lod = 0,
                    inputs = new[] { new ReverseUvTransfer.Input { mesh = mesh, key = mesh.name } } } };
                using var prepared = ReverseUvInputs.Prepare(levels);
                var source = prepared.levels[0].inputs[0].mesh;
                var bytes = TransferMeshSnapshot.Capture(source);
                var baseline = Object.Instantiate(source); Mesh cut = null;
                try
                {
                    var plan = UvJunctionCuts.Find(source.uv, source.triangles);
                    cut = UvJunctionCuts.CopyForRepack(source, Vector2.one) ?? Object.Instantiate(source);
                    var options = RepackOptions.Default; options.resolution = 128;
                    options.padding = 2; options.bruteForce = false; options.blockAlign = false;
                    var before = XatlasRepack.RepackSingle(baseline, options);
                    var after = XatlasRepack.RepackSingle(cut, options);
                    Assert.IsTrue(before.ok, before.error); Assert.IsTrue(after.ok, after.error);
                    var qb = TransferUvQuality.Measure(baseline, baseline.uv2, Vector2.one, Matrix4x4.identity);
                    var qa = TransferUvQuality.Measure(cut, cut.uv2, Vector2.one, Matrix4x4.identity);
                    if (!string.IsNullOrEmpty(output))
                    {
                        Directory.CreateDirectory(output);
                        File.WriteAllText(Path.Combine(output, mesh.name + "-repack.json"), JsonUtility.ToJson(new FrozenReport {
                            mesh = mesh.name, cuts = plan.cuts, chartsBefore = (int)before.chartCount, chartsAfter = (int)after.chartCount,
                            atlasBefore = Math.Max(before.atlasWidth, before.atlasHeight), atlasAfter = Math.Max(after.atlasWidth, after.atlasHeight),
                            qualityBefore = qb, qualityAfter = qa, uvBefore = baseline.uv2, uvAfter = cut.uv2,
                            trianglesBefore = baseline.triangles, trianglesAfter = cut.triangles }, true));
                    }
                    Assert.IsTrue(qa.overlapScanComplete);
                    Assert.LessOrEqual(qa.overlapPairs, qb.overlapPairs);
                    Assert.LessOrEqual(qa.degenerateFaces, qb.degenerateFaces);
                    CollectionAssert.AreEqual(bytes, TransferMeshSnapshot.Capture(source));
                }
                finally { Object.DestroyImmediate(baseline); if (cut) Object.DestroyImmediate(cut); }
            }
        }

        [TestCase(false, 10)] [TestCase(true, 10)]
        [TestCase(false, 32)] [TestCase(true, 32)]
        public void FrozenTableReverseAuditsBothJunctionPolicies(bool overlap, int density)
        {
            string directory = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_FIXTURES");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Optional private FBX fixtures are not configured.");
            float tolerance=float.TryParse(Environment.GetEnvironmentVariable("MESHLAB_TRANSFER_BOUNDARY_TEXELS"),
                System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var configured)
                ?configured:UvTransferTolerance.BoundaryTexels;
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(directory + "/Cafe_Table_A_01.fbx");
            Assert.IsNotNull(root);
            var filters = root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh).ToArray();
            var sceneFrame = Matrix4x4.TRS(new Vector3(33.65f, .002f, 51.18f), Quaternion.Euler(0, 89.99995f, 0), Vector3.one * .84974f);
            var levels = Enumerable.Range(0, 3).Reverse().Select(lod => new ReverseUvTransfer.Level { lod = lod,
                inputs = filters.Where(f => f.sharedMesh.name.Contains("LOD" + lod)).Select(f => new ReverseUvTransfer.Input {
                    mesh = f.sharedMesh, key = f.sharedMesh.name, toWorld = sceneFrame * f.transform.localToWorldMatrix }).ToArray() }).ToArray();
            Assert.IsTrue(levels.All(l => l.inputs.Length > 0));
            var previous = BenchmarkRecorder.OutputDirectoryOverride;
            try
            {
                string output = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_OUTPUT");
                int seedCharts = 0;
                foreach (bool cuts in new[] { false, true })
                {
                    using var prepared = ReverseUvJunctionTrial.Build(levels, new ReverseUvTransfer.Options {boundaryToleranceTexels=tolerance,
                        seedResolution = density == 10 ? 64 : 128, padding = 2, projectionReach = .05f, cutNarrowJunctions = cuts,
                        preserveProjectedOverlap = overlap, repackFinalAtlas = true, fillFinalAtlas = true }, true, density).GetAwaiter().GetResult();
                    var result = prepared.result;
                    BenchmarkRecorder.OutputDirectoryOverride = string.IsNullOrEmpty(output) ? null : Path.Combine(output,
                        (cuts ? "reverse-cuts" : "reverse-baseline") + (overlap ? "-overlap" : "-exclusive") + "-" + density);
                    ReverseUvAudit.Write(result, prepared.inputs.levels);
                    if (!string.IsNullOrEmpty(output))
                    {
                        int node = 0;
                        foreach (var level in result.meshes)
                            foreach (var mesh in level)
                            {
                                var report = result.report.nodes[node++];
                                File.WriteAllText(Path.Combine(BenchmarkRecorder.OutputDirectoryOverride,$"lod{report.lod}.layout.json"),
                                    JsonUtility.ToJson(new FrozenReport { mesh=mesh.name, uvAfter=mesh.uv2, trianglesAfter=mesh.triangles,
                                        faceCharts=report.faces.Select(f=>f.chart).ToArray() },true));
                            }
                    }
                    UvtLog.Info($"[JunctionFrozen] Table cuts={cuts}, overlap={overlap}: atlas={result.report.atlasSize}, inherited={result.report.inheritedFaces}, new={result.report.newFaces}, ambiguous={result.report.ambiguousFaces}.");
                    Assert.IsNull(prepared.refusal, "Enabled cuts must actually run, rather than silently retaining the baseline.");
                    Assert.AreEqual("shared-chart-families", result.report.finalPacking, result.report.finalPackingRefusal);
                    Assert.GreaterOrEqual(result.report.texelsPerUnit,density*.999f);
                    int charts = result.report.nodes[0].faces.Select(f => f.chart).Distinct().Count();
                    if (!cuts) seedCharts = charts;
                    else Assert.Greater(charts,seedCharts,"The table's T/H chart must be split.");
                    var allUv = result.meshes.SelectMany(l => l).SelectMany(m => m.uv2).ToArray();
                    var min = allUv.Aggregate(Vector2.Min); var max = allUv.Aggregate(Vector2.Max);
                    Assert.Greater(Math.Max(max.x-min.x,max.y-min.y),.93f,"The final chain must use the atlas extent.");
                }
            }
            finally { BenchmarkRecorder.OutputDirectoryOverride = previous; }
        }

        [TestCase(false, 0)] [TestCase(true, 0)]
        [TestCase(false, 2)] [TestCase(true, 2)]
        public void FrozenTableForwardAuditsJunctionPolicies(bool cuts, int sourceLod)
        {
            string directory = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_FIXTURES");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Optional private FBX fixtures are not configured.");
            var meshes = AssetDatabase.LoadAllAssetsAtPath(directory + "/Cafe_Table_A_01.fbx").OfType<Mesh>()
                .Where(m => m.name.Contains("_LOD")).ToArray();
            var source = meshes.Single(m => m.name.Contains("LOD" + sourceLod));
            var baseline = Object.Instantiate(source);
            Mesh donor = null;
            try
            {
                donor = cuts ? UvJunctionCuts.CopyForRepack(baseline, Vector2.one) : null;
                if (!donor) donor = Object.Instantiate(baseline);
                var options = RepackOptions.Default; options.resolution = 64; options.padding = 2;
                options.bruteForce = false; options.blockAlign = false;
                var packed = XatlasRepack.RepackSingle(donor, options);
                Assert.IsTrue(packed.ok, packed.error);
                foreach (var target in meshes.Where(m => m != source))
                {
                    var transfer = GroupedShellTransfer.Transfer(target, donor, sourceAtlasWidth: (int)packed.atlasWidth,
                        sourceAtlasHeight: (int)packed.atlasHeight);
                    var quality = TransferUvQuality.Measure(target, transfer.uv2, Vector2.one, Matrix4x4.identity);
                    UvtLog.Info($"[JunctionForward] source={sourceLod}, cuts={cuts}, target={target.name}, collapsed={quality.degenerateFaces}, overlap={quality.overlapPairs}, worst={quality.worstAnisotropy:G6}, stretched={quality.stretchedFaces}.");
                    string output = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_OUTPUT");
                    if (!string.IsNullOrEmpty(output))
                    {
                        Directory.CreateDirectory(output);
                        File.WriteAllText(Path.Combine(output, $"forward-{sourceLod}-{cuts}-{target.name}.json"), JsonUtility.ToJson(quality, true));
                    }
                }
            }
            finally { Object.DestroyImmediate(baseline); if (donor) Object.DestroyImmediate(donor); }
        }
    }
}
