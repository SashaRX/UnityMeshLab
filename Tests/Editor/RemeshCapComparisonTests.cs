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
        [Serializable] public sealed class Case { public string name, source; }
        [Serializable] public sealed class Candidate { public string caseName, method, path; public int resolution; }
        [Serializable] public sealed class Manifest
        {
            public string output;
            public Case[] cases;
            public Candidate[] candidates;
        }
        [Serializable] public sealed class Outcome
        {
            public string caseName, method, path, status, reason;
            public int sourceFaces, addedFaces, addedVertices, loops, refusedLoops, boundaryEdges;
            public int voxelResolution, voxelFaces, trimRemoved, simplifiedFaces, charts, overlaps, degenerateUv, outsideUv;
            public double seconds, meanStretch, maxStretch;
            public bool solve, uvValid;
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
                    var uv = RemeshNative.Unwrap(simplified, settings, default);
                    var quality = UvChartQuality.Measure(uv, default); var atlas = UvAtlasDiagnostics.Measure(uv, default);
                    row.charts = quality.charts; row.meanStretch = quality.meanStretch; row.maxStretch = quality.maxStretch;
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
    }
}
