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
        }

        static Mesh Grid(string pattern)
        {
            var rows = pattern.Split('/'); var vertices = new List<Vector3>();
            var indices = new List<int>(); var unique = new Dictionary<Vector3, int>();
            for (int y = 0; y < rows.Length; ++y)
                for (int x = 0; x < rows[y].Length; ++x)
                {
                    if (rows[y][x] != '#') continue;
                    var cell = new[] { new Vector3(x, y), new Vector3(x + 1, y), new Vector3(x + 1, y + 1), new Vector3(x, y + 1) };
                    var ids = new int[4];
                    for (int k = 0; k < 4; ++k)
                    {
                        if (!unique.TryGetValue(cell[k], out int v)) { v = vertices.Count; unique.Add(cell[k], v); vertices.Add(cell[k]); }
                        ids[k] = v;
                    }
                    foreach (int k in new[] { 0, 1, 2, 0, 2, 3 }) indices.Add(ids[k]);
                }
            var mesh = new Mesh { name = "Junction fixture" };
            mesh.SetVertices(vertices); mesh.triangles = indices.ToArray();
            mesh.uv = vertices.Select(p => new Vector2(p.x, p.y)).ToArray();
            mesh.uv2 = mesh.uv; mesh.RecalculateNormals();
            return mesh;
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
            StringAssert.Contains("larger atlas", ReverseUvJunctionTrial.Compare(baseline, candidate));
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

        [TestCase(false)] [TestCase(true)]
        public void FrozenTableReverseAuditsBothJunctionPolicies(bool overlap)
        {
            string directory = Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_FIXTURES");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Optional private FBX fixtures are not configured.");
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
                int inherited = 0, newFaces = 0, atlas = 0;
                foreach (bool cuts in new[] { false, true })
                {
                    using var prepared = ReverseUvJunctionTrial.Build(levels, new ReverseUvTransfer.Options {
                        seedResolution = 128, padding = 2, projectionReach = .05f, cutNarrowJunctions = cuts,
                        preserveProjectedOverlap = overlap }, true, 32).GetAwaiter().GetResult();
                    var result = prepared.result;
                    BenchmarkRecorder.OutputDirectoryOverride = string.IsNullOrEmpty(output) ? null : Path.Combine(output,
                        (cuts ? "reverse-cuts" : "reverse-baseline") + (overlap ? "-overlap" : "-exclusive"));
                    ReverseUvAudit.Write(result, prepared.inputs.levels);
                    UvtLog.Info($"[JunctionFrozen] Table cuts={cuts}, overlap={overlap}: atlas={result.report.atlasSize}, inherited={result.report.inheritedFaces}, new={result.report.newFaces}, ambiguous={result.report.ambiguousFaces}.");
                    if (!cuts) { inherited = result.report.inheritedFaces; newFaces = result.report.newFaces; atlas = result.report.atlasSize; }
                    else { Assert.GreaterOrEqual(result.report.inheritedFaces, inherited); Assert.LessOrEqual(result.report.newFaces, newFaces); Assert.LessOrEqual(result.report.atlasSize, atlas); }
                }
            }
            finally { BenchmarkRecorder.OutputDirectoryOverride = previous; }
        }
    }
}
