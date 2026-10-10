using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>Opt-in replay of private captures; outcomes are data, not acceptance assertions.</summary>
    public class RemeshCapComparisonTests
    {
        [Serializable] public sealed class Case { public string name, source, selection; }
        [Serializable] public sealed class Candidate { public string caseName, method, path; public int resolution; }
        [Serializable] public sealed class Manifest
        {
            public string output;
            public bool measureSurface;
            public int unwrapRepeats;
            public Case[] cases;
            public Candidate[] candidates;
        }
        [Serializable] public sealed class Outcome
        {
            public string caseName, method, path, status, reason, rejectedPath, rejectedReason;
            public int sourceFaces, addedFaces, addedVertices, loops, refusedLoops, boundaryEdges;
            public int voxelResolution, voxelFaces, trimRemoved, simplifiedFaces, charts, overlaps, degenerateUv, outsideUv;
            public double seconds, meanStretch, maxStretch;
            public double targetRms, targetMax, sourceRms, sourceMax;
            public int originalCharts, originalSmallCharts;
            public bool solve, uvValid, mutualCollar;
        }
        [Serializable] public sealed class Report { public List<Outcome> results = new List<Outcome>(); }

        static Manifest ReadManifest()
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_COMPARISON_MANIFEST");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_COMPARISON_MANIFEST to opt into private capture replay.");
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
            if (manifest == null || string.IsNullOrEmpty(manifest.output)) throw new InvalidDataException("Missing benchmark output path.");
            Directory.CreateDirectory(manifest.output);
            return manifest;
        }

        static void ReadMesh(string path, out Vector3[] positions, out int[] indices)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            if (vertices < 3 || vertices > 200000 || count < 3 || count > 1200000 || count % 3 != 0 ||
                reader.BaseStream.Length != 8L + vertices * 12L + count * 4L) throw new InvalidDataException("Invalid capture size.");
            positions = new Vector3[vertices]; indices = new int[count];
            for (int i = 0; i < vertices; ++i) positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < count; ++i) indices[i] = reader.ReadInt32();
            if (positions.Any(p => !float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) ||
                indices.Any(v => v < 0 || v >= vertices)) throw new InvalidDataException("Invalid capture geometry.");
        }

        static void WriteMesh(string path, Vector3[] positions, int[] indices)
        {
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(positions.Length); writer.Write(indices.Length);
            foreach (var p in positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
            foreach (int v in indices) writer.Write(v);
        }

        [Test]
        public void GenerateProductionCandidates()
        {
            var manifest = ReadManifest(); var report = new Report();
            foreach (var source in manifest.cases) {
                ReadMesh(source.source, out var p, out var ix);
                var originalP = (Vector3[])p.Clone(); var originalI = (int[])ix.Clone();
                MeshGeometry.WeldPositions(p, out int weldedCount);
                foreach (string method in new[] { "ours_planar_001", "ours_planar_01", "ours_surface", "ours_auto", "ours_planes_then_surface" }) {
                    var row = new Outcome { caseName = source.name, method = method, sourceFaces = ix.Length / 3 };
                    var timer = Stopwatch.StartNew(); RemeshPlanarCap.Support cap = null;
                    try {
                        var mode = method == "ours_surface" ? RemeshClosureMode.SurfaceCaps :
                            method == "ours_auto" ? RemeshClosureMode.Automatic : RemeshClosureMode.Caps;
                        cap = RemeshPlanarCap.Prepare(p, ix, "all", default, true, mode,
                            method == "ours_planar_01" ? .1 : .01, continueOnRefusal: true, elementScopedContacts: true);
                        row.loops = cap.loops;
                        if (method == "ours_planes_then_surface" && cap.remainingBoundaryEdges > 0)
                            cap = RemeshPlanarCap.Prepare(cap.positions, cap.indices, "all", default, true,
                                RemeshClosureMode.SurfaceCaps, .01, continueOnRefusal: true, elementScopedContacts: true);
                        row.addedFaces = cap.indices.Length / 3 - row.sourceFaces; row.addedVertices = cap.positions.Length - weldedCount;
                        row.refusedLoops = cap.loopFailures.Count; row.boundaryEdges = cap.remainingBoundaryEdges;
                        row.reason = string.Join(" | ", cap.loopFailures.Select(pair => $"{pair.Key}: {pair.Value}"));
                        row.path = Path.Combine(manifest.output, source.name + "__" + method + ".bin");
                        WriteMesh(row.path, cap.positions, cap.indices); row.status = "generated";
                    }
                    catch (InvalidOperationException ex) { row.status = "refused"; row.reason = ex.Message; }
                    row.seconds = timer.Elapsed.TotalSeconds; report.results.Add(row);
                    // Preservation failures fail the test; algorithm refusals remain report rows.
                    CollectionAssert.AreEqual(originalP, p); CollectionAssert.AreEqual(originalI, ix);
                    if (cap != null) for (int corner = 0; corner < ix.Length; ++corner)
                        Assert.AreEqual(p[ix[corner]], cap.positions[cap.indices[corner]], "Donor corner changed.");
                    TestContext.WriteLine($"{source.name}/{method}: {row.status}, +{row.addedFaces}, open={row.boundaryEdges}, refused={row.refusedLoops}");
                }
            }
            File.WriteAllText(Path.Combine(manifest.output, "production.json"), JsonUtility.ToJson(report, true));
        }

        [Test]
        public void GenerateBridgeCandidates()
        {
            var manifest=ReadManifest(); var report=new Report();
            foreach (var source in manifest.cases) {
                ReadMesh(source.source,out var p,out var ix);
                var originalP=(Vector3[])p.Clone(); var originalI=(int[])ix.Clone();
                var row=new Outcome {caseName=source.name,method="ours_bridge",sourceFaces=ix.Length/3};
                var timer=Stopwatch.StartNew();
                var support=RemeshPlanarCap.Prepare(p,ix,source.selection ?? "0,1",default,
                    mode:RemeshClosureMode.Bridge,continueOnRefusal:true,elementScopedContacts:true);
                row.loops=support.loops; row.addedFaces=support.addedFaces; row.boundaryEdges=support.remainingBoundaryEdges;
                row.refusedLoops=support.loopFailures.Count;
                row.reason=string.Join(" | ",support.loopFailures.Select(pair=>$"{pair.Key}: {pair.Value}"));
                row.path=Path.Combine(manifest.output,source.name+"__ours_bridge.bin");
                WriteMesh(row.path,support.positions,support.indices);
                row.status=support.addedFaces>0?"generated":"refused";
                if (row.status=="refused") ExportRejectedBridge(source,support,row,manifest.output);
                row.seconds=timer.Elapsed.TotalSeconds; report.results.Add(row);
                CollectionAssert.AreEqual(originalP,p); CollectionAssert.AreEqual(originalI,ix);
                for (int corner=0;corner<ix.Length;++corner)
                    Assert.AreEqual(p[ix[corner]],support.positions[support.indices[corner]],"Bridge moved a donor corner.");
                TestContext.WriteLine($"{source.name}: {row.status}, +{row.addedFaces}, open={row.boundaryEdges}, {row.reason}");
            }
            File.WriteAllText(Path.Combine(manifest.output,"production-bridge.json"),JsonUtility.ToJson(report,true));
        }

        static void ExportRejectedBridge(Case source,RemeshPlanarCap.Support support,Outcome row,string output)
        {
            var chosen=(source.selection ?? "0,1").Split(',');
            if (chosen.Length!=2 || !int.TryParse(chosen[0],out int a) || !int.TryParse(chosen[1],out int b) ||
                a<0 || b<0 || a>=support.boundaryLoops.Length || b>=support.boundaryLoops.Length || a==b) return;
            var first=support.boundaryLoops[a].ToList(); var second=support.boundaryLoops[b].ToList();
            var topology=RemeshTopology.Inspect(support.positions,support.indices);
            var normals=MeshGeometry.FaceNormals(support.positions,support.indices);
            row.mutualCollar=RemeshBridge.ContinuesToward(support.positions,topology,normals,first,second) &&
                RemeshBridge.ContinuesToward(support.positions,topology,normals,second,first);
            int Owner(List<int> rim) {
                int x=topology.slots[rim[0]],y=topology.slots[rim[1]];
                return support.faceElements[topology.edges[x<y?(x,y):(y,x)].firstFace];
            }
            var external=new RemeshPlanarCap.ExternalContacts {faceElements=support.faceElements,
                closingElements=new HashSet<int> {Owner(first),Owner(second)}};
            var report=new RemeshBridge.SearchReport(captureRejected:true); int trials=0;
            try { RemeshBridge.Generate(support.positions,support.indices,first,second,default,ref trials,out _,external,report); }
            catch (InvalidOperationException ex) { row.rejectedReason=ex.Message; }
            if (report.firstRejectedIndices==null) return;
            row.rejectedPath=Path.Combine(output,source.name+"__rejected_bridge.bin");
            row.rejectedReason=report.firstContact;
            WriteMesh(row.rejectedPath,support.positions,report.firstRejectedIndices);
        }

        [Test]
        public void ReplayAuditedNativeCandidates()
        {
            var manifest = ReadManifest(); var report = new Report(); RemeshNative.CheckAvailable();
            foreach (var candidate in manifest.candidates) foreach (bool solve in new[] { false, true }) {
                var row = new Outcome { caseName = candidate.caseName, method = candidate.method, solve = solve,
                    voxelResolution = candidate.resolution > 0 ? candidate.resolution : 64 };
                var timer = Stopwatch.StartNew();
                try {
                    ReadMesh(candidate.path, out var p, out var ix);
                    var topology = RemeshTopology.Inspect(p, ix);
                    if (!topology.Valid || topology.boundary.Count != 0 || !RemeshTopology.ClosedVolumeFaces(p, ix, default).All(v => v))
                        throw new InvalidOperationException("Input is not a closed manifold with nonzero component volumes: " + topology.Description);
                    var settings = new RemeshSettings { voxelResolution = row.voxelResolution, solve = solve, maximumError = .02f,
                        pruneSmallParts = true, normalCrease = 133, normalSmoothing = 3,
                        normalWeighting = RemeshNormalWeighting.FaceAreaAndCornerAngle,
                        textureResolution = 512, padding = 3, mergeCharts = true };
                    var voxel = RemeshNative.Voxelize(p, ix, settings, default); row.voxelFaces = voxel.TriangleCount;
                    float span = (p.Aggregate(Vector3.Min) - p.Aggregate(Vector3.Max)).magnitude;
                    var trimmed = RemeshTrim.Trim(voxel, p, ix, span / row.voxelResolution * 2, default); row.trimRemoved = trimmed.removed;
                    var simplified = solve ? RemeshSurfaceRefine.Simplify(trimmed.mesh, p, ix, settings, default, out _) :
                        RemeshNative.Simplify(trimmed.mesh, settings, default, out _);
                    row.simplifiedFaces = simplified.TriangleCount;
                    if (manifest.measureSurface) {
                        string stem = candidate.caseName + "__" + candidate.method + "__solve_" + solve;
                        row.path = Path.Combine(manifest.output, stem + "__simplified.bin");
                        WriteMesh(row.path, simplified.positions, simplified.indices);
                        WriteMesh(Path.Combine(manifest.output, stem + "__voxel.bin"), voxel.positions, voxel.indices);
                        var forward = RemeshSurfaceRefine.SampleErrorCore(new TriangleBvh(p, ix), simplified.positions, simplified.indices, default);
                        var reverse = RemeshSurfaceRefine.SampleErrorCore(new TriangleBvh(simplified.positions, simplified.indices), p, ix, default, true);
                        row.targetRms = forward.rms; row.targetMax = forward.max;
                        row.sourceRms = reverse.rms; row.sourceMax = reverse.max;
                    }
                    var uv = RemeshNative.Unwrap(simplified, settings, default);
                    var quality = UvChartQuality.Measure(uv, default); var atlas = UvAtlasDiagnostics.Measure(uv, default);
                    row.charts = quality.charts; row.meanStretch = quality.meanStretch; row.maxStretch = quality.maxStretch;
                    row.originalCharts = uv.originalChartCount; row.originalSmallCharts = uv.originalSmallChartCount;
                    row.uvValid = quality.valid && atlas.complete && atlas.invalidFaces == 0; row.overlaps = atlas.pairs;
                    row.degenerateUv = atlas.degenerateFaces; row.outsideUv = atlas.outOfBoundsVertices;
                    row.status = row.uvValid && row.overlaps == 0 && row.degenerateUv == 0 && row.outsideUv == 0 ? "passed" : "invalid_uv";
                }
                catch (Exception ex) { row.status = "failed"; row.reason = ex.GetType().Name + ": " + ex.Message; }
                row.seconds = timer.Elapsed.TotalSeconds; report.results.Add(row);
                File.WriteAllText(Path.Combine(manifest.output, "native.json"), JsonUtility.ToJson(report, true));
                TestContext.WriteLine($"{row.caseName}/{row.method}/solve={solve}: {row.status} {row.reason}");
            }
        }

        [Test]
        public void VerifyAuditedNativeCandidates()
        {
            // Separate opt-in acceptance gate: the comparison exporter intentionally
            // records failed rows without turning every research run into a failure.
            var manifest = ReadManifest();
            ReplayAuditedNativeCandidates();
            var report = JsonUtility.FromJson<Report>(File.ReadAllText(Path.Combine(manifest.output,"native.json")));
            Assert.AreEqual(manifest.candidates.Length*2,report.results.Count);
            foreach (var row in report.results)
                Assert.AreEqual("passed",row.status,$"{row.caseName}/solve={row.solve}: {row.reason}");
        }

        [Test]
        public void VerifyRepeatedUnwraps()
        {
            var manifest = ReadManifest();
            if (manifest.unwrapRepeats == 0) Assert.Ignore("Set unwrapRepeats to opt into repeated private UV checks.");
            Assert.That(manifest.unwrapRepeats, Is.InRange(2, 10));
            RemeshNative.CheckAvailable();
            var hashes = new List<string>();
            foreach (var candidate in manifest.candidates) {
                ReadMesh(candidate.path, out var p, out var ix);
                string expected = null;
                var timer = Stopwatch.StartNew();
                for (int repeat = 0; repeat < manifest.unwrapRepeats; repeat++) {
                    var input = new RemeshNative.IndexedMesh { positions = p, indices = ix }.PrepareChannels(default);
                    var settings = new RemeshSettings { normalCrease = 133, normalSmoothing = 3,
                        normalWeighting = RemeshNormalWeighting.FaceAreaAndCornerAngle,
                        textureResolution = 512, padding = 3, mergeCharts = true };
                    var uv = RemeshNative.Unwrap(input, settings, default);
                    var atlas = UvAtlasDiagnostics.Measure(uv, default);
                    Assert.IsTrue(UvChartQuality.Measure(uv, default).valid);
                    Assert.IsTrue(atlas.complete); Assert.AreEqual(0, atlas.pairs);
                    Assert.AreEqual(0, atlas.invalidFaces); Assert.AreEqual(0, atlas.degenerateFaces); Assert.AreEqual(0, atlas.outOfBoundsVertices);
                    Assert.AreEqual(ix.Length, uv.indices.Length);
                    for (int corner = 0; corner < ix.Length; corner++) Assert.AreEqual(p[ix[corner]], uv.positions[uv.indices[corner]]);
                    string hash = GeometryHash(uv);
                    expected ??= hash;
                    Assert.AreEqual(expected, hash, $"{candidate.caseName}: repeat {repeat + 1} changed geometry/UV/normals/tangents/chart ids");
                }
                string summary = $"{candidate.caseName}: {manifest.unwrapRepeats} identical valid atlases, SHA256 {expected}, {timer.Elapsed.TotalSeconds:F3}s";
                hashes.Add(summary); TestContext.WriteLine(summary);
                File.WriteAllLines(Path.Combine(manifest.output, "unwrap-repeats.txt"), hashes);
            }
        }

        static string GeometryHash(RemeshNative.Geometry geometry)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) {
                writer.Write(geometry.positions.Length); writer.Write(geometry.indices.Length); writer.Write(geometry.chartCount);
                for (int v = 0; v < geometry.positions.Length; v++) {
                    var p = geometry.positions[v]; var n = geometry.normals[v]; var uv = geometry.uv[v]; var tangent = geometry.tangents[v];
                    writer.Write(p.x); writer.Write(p.y); writer.Write(p.z);
                    writer.Write(n.x); writer.Write(n.y); writer.Write(n.z);
                    writer.Write(uv.x); writer.Write(uv.y);
                    writer.Write(tangent.x); writer.Write(tangent.y); writer.Write(tangent.z); writer.Write(tangent.w);
                    writer.Write(geometry.charts[v]);
                }
                foreach (int index in geometry.indices) writer.Write(index);
            }
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }
    }
}
