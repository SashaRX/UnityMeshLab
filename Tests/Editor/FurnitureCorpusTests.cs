using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>Optional private corpus audit. Successful execution does not certify clean UVs.</summary>
    public class FurnitureCorpusTests
    {
        [Serializable] sealed class Row
        {
            public string model, source, target, profile, method, status, error, inputHash;
            public int schema = 1, resolution = 512, atlasSide, rawOutOfBounds, misses, fallbackVertices;
            public int fileCount, pairCount, errorCount;
            public double milliseconds, uvTriangleAreaSum;
            public int geometryDegenerateFaces, uvDegenerateFaces;
            // Traced grouped rows (oversample analysis only): the source-atlas recovery gate
            // and the target shells clean-chart recovery ran on. The gate also lets merged and
            // overlap-group candidate selection recover, which records no projection and is not counted.
            public bool traced, sourceAtlasRecoveryAllowed;
            public int cleanChartRecoveries;
            public TransferUvQuality quality;
        }

        /// <summary>Audits every matched LOD pair on owned imports, retaining failures and exclusions in JSONL.</summary>
        [UnityTest, Timeout(3600000)]
        public IEnumerator ComparePrivateFurnitureCorpus()
        {
            string root = Environment.GetEnvironmentVariable("MESHLAB_FURNITURE_CORPUS");
            if (string.IsNullOrEmpty(root)) Assert.Ignore("Optional furniture corpus is not configured.");
            Assert.IsTrue(Directory.Exists(root), "Corpus directory does not exist.");
            string output = Environment.GetEnvironmentVariable("MESHLAB_CORPUS_OUTPUT");
            Assert.IsFalse(string.IsNullOrEmpty(output), "Set MESHLAB_CORPUS_OUTPUT to a fresh JSONL file outside Assets/Packages.");
            output = Path.GetFullPath(output);
            string project = Path.GetDirectoryName(Application.dataPath);
            foreach (string forbidden in new[] { Application.dataPath, Path.Combine(project, "Packages") })
                Assert.IsFalse(output.StartsWith(Path.GetFullPath(forbidden) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => string.Equals(Path.GetExtension(p), ".fbx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.That(files.Length, Is.InRange(1, 256));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            using var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            writer.AutoFlush = true;
            string folder = "Assets/FurnitureCorpus_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            int errors = 0, pairs = 0;
            int iterations = GroupedShellTransfer.LastTopologyIterations, fixes = GroupedShellTransfer.LastTopologyFixed;
            bool cap = GroupedShellTransfer.LastTopologyCapHit;
            var logTrace = Application.GetStackTraceLogType(LogType.Log);
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            try
            {
                foreach (string file in files)
                {
                    string model = Path.GetRelativePath(root, file).Replace('\\', '/');
                    byte[] bytes = File.ReadAllBytes(file);
                    string hash = TransferMeshSnapshot.Hash(bytes), path = folder + "/source.fbx";
                    var selected = new List<TransferBenchmarkAssets.Pair>();
                    try
                    {
                        File.WriteAllBytes(path, bytes);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                        importer.isReadable = true; importer.generateSecondaryUV = false;
                        importer.weldVertices = false; importer.SaveAndReimport();
                        var meshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray();
                        var sources = meshes.Where(m => MeshNaming.LodIndex(m.name) == 0)
                            .Select(m => UvToolContext.ExtractGroupKey(m.name)).ToHashSet();
                        var groups = meshes.Where(m => MeshNaming.LodIndex(m.name) > 0)
                            .Select(m => UvToolContext.ExtractGroupKey(m.name)).Distinct().ToArray();
                        foreach (string group in groups.Where(g => !sources.Contains(g)))
                            Write(writer, new Row { model = model, target = group, status = "excluded", error = "No LOD0 in this FBX", inputHash = hash });
                        var matched = groups.Where(sources.Contains).ToArray();
                        if (matched.Length == 0)
                            Write(writer, new Row { model = model, status = "excluded", error = "No matched LOD0-to-LOD pairs", inputHash = hash });
                        else selected = TransferBenchmarkAssets.Pairs(new TransferBenchmarkAssets.Case {
                            asset = path, groups = matched, weldUv0 = true, preOptimize = true });
                    }
                    catch (Exception error)
                    {
                        ++errors; Write(writer, new Row { model = model, status = "error", error = error.ToString(), inputHash = hash });
                    }
                    foreach (var pair in selected)
                    {
                        ++pairs;
                        try { AuditPair(writer, pair, model, hash); }
                        catch (Exception error)
                        {
                            ++errors; Write(writer, new Row { model = model, source = pair.source.name, target = pair.target.name,
                                status = "error", error = error.ToString(), inputHash = hash });
                        }
                        yield return null;
                    }
                    Assert.AreEqual(hash, TransferMeshSnapshot.Hash(File.ReadAllBytes(file)), "Original FBX changed: " + model);
                    Assert.AreEqual(hash, TransferMeshSnapshot.Hash(File.ReadAllBytes(path)), "Imported copy bytes changed: " + model);
                    Assert.IsTrue(AssetDatabase.DeleteAsset(path));
                    TestContext.WriteLine($"Corpus: {model}; pairs={selected.Count}; total={pairs}; errors={errors}");
                    yield return null;
                }
                Write(writer, new Row { status = "complete", fileCount = files.Length, pairCount = pairs, errorCount = errors });
                Assert.Greater(pairs, 0); Assert.AreEqual(0, errors, "See explicit error rows in " + output);
            }
            finally
            {
                GroupedShellTransfer.LastTopologyIterations = iterations; GroupedShellTransfer.LastTopologyFixed = fixes;
                GroupedShellTransfer.LastTopologyCapHit = cap;
                Application.SetStackTraceLogType(LogType.Log, logTrace);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        /// <summary>Compares pack profiles and transfer baselines without using a baseline as correspondence truth.</summary>
        static void AuditPair(StreamWriter writer, TransferBenchmarkAssets.Pair pair, string model, string hash)
        {
            // A pack reports its internal (oversampled) atlas, which grouped transfer takes as
            // the side of its texel tolerances. The opt-in control transfers the default and
            // oversample-8 sources again against the requested resolution (oversample.py).
            bool atlasSideControl = Environment.GetEnvironmentVariable("MESHLAB_CORPUS_OVERSAMPLE_ANALYSIS") == "1";
            using var input = TransferBenchmarkAssets.Prepare(pair);
            byte[] originalSource = TransferMeshSnapshot.Capture(input.source), originalTarget = TransferMeshSnapshot.Capture(input.target);
            foreach (string profile in new[] { "default", "no-reparameterization", "oversample-8" })
            {
                var source = MeshAccess.ReadableCopy(input.source);
                try
                {
                    var watch = Stopwatch.StartNew();
                    int side = input.atlasWidth;
                    if (profile != "default")
                    {
                        var options = RepackOptions.Default; options.resolution = 512;
                        options.reparameterizeStretchedShells = profile != "no-reparameterization";
                        if (profile == "oversample-8") options.internalOversample = 8;
                        var packed = XatlasRepack.RepackSingle(source, options);
                        if (!packed.ok) throw new InvalidDataException(packed.error);
                        side = (int)SourceTextureUvMetric.NormalizePackedLightmap(source, packed.atlasWidth, packed.atlasHeight);
                    }
                    double packMilliseconds = watch.Elapsed.TotalMilliseconds;
                    var sourceRow = Measure(model, pair, profile, "source", source, source.uv2, side, hash);
                    // Default preparation was already performed by Prepare; its time is not comparable.
                    sourceRow.milliseconds = profile == "default" ? -1 : packMilliseconds;
                    Write(writer, sourceRow);
                    var methods = profile == "default"
                        ? new List<string> { "grouped", "uv0-nearest", "surface-nearest", "surface-normal" } : new List<string> { "grouped" };
                    if (atlasSideControl && profile != "no-reparameterization") methods.Add("grouped-at-resolution");
                    foreach (string method in methods)
                    {
                        watch.Restart();
                        Vector2[] uv; int misses = 0, fallback = 0;
                        int transferSide = method == "grouped-at-resolution" ? pair.settings.resolution : side;
                        TransferMatchTrace trace = null;
                        if (method == "grouped" || method == "grouped-at-resolution")
                        {
                            // The analysis traces grouped transfer; its times then include the trace.
                            if (atlasSideControl) trace = new TransferMatchTrace();
                            var result = GroupedShellTransfer.TransferWithDiagnostics(input.target, source, null, null, transferSide, transferSide, trace);
                            uv = result.uv2; misses = result.verticesTotal - result.verticesTransferred;
                        }
                        else
                        {
                            // These comparison implementations execute synchronously on detached input.
                            var borrowed = new TransferBenchmark.Input { source = source, target = input.target, atlasWidth = side, atlasHeight = side };
                            var result = TransferBenchmarkMethods.Run(method, borrowed).GetAwaiter().GetResult();
                            uv = result.uv; misses = result.misses; fallback = result.fallbackVertices;
                        }
                        double elapsed = watch.Elapsed.TotalMilliseconds;
                        if (uv == null) throw new InvalidDataException("Transfer returned no UVs.");
                        int outside = uv.Count(p => p.x < 0 || p.y < 0 || p.x > 1 || p.y > 1);
                        XatlasRepack.ClampUvsToUnit(uv);
                        var row = Measure(model, pair, profile, method, input.target, uv, transferSide, hash);
                        row.rawOutOfBounds = outside; row.misses = misses; row.fallbackVertices = fallback;
                        if (trace != null)
                        {
                            // Clean-chart recovery records the legacy candidate first on every shell it runs on.
                            row.traced = true; row.sourceAtlasRecoveryAllowed = trace.sourceAtlasRecoveryAllowed;
                            row.cleanChartRecoveries = trace.shells.Count(s => s.projections.Any(p => p.method == "legacy-selected"));
                        }
                        row.milliseconds = elapsed; Write(writer, row);
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(source); }
            }
            CollectionAssert.AreEqual(originalSource, TransferMeshSnapshot.Capture(input.source));
            CollectionAssert.AreEqual(originalTarget, TransferMeshSnapshot.Capture(input.target));
        }

        /// <summary>Separates exact geometric degeneracy from collapsed UVs and reports summed area, not occupancy.</summary>
        static Row Measure(string model, TransferBenchmarkAssets.Pair pair, string profile, string method,
            Mesh mesh, Vector2[] uv, int side, string hash)
        {
            var row = new Row { model = model, source = pair.source.name, target = pair.target.name,
                profile = profile, method = method, status = "measured", atlasSide = side, inputHash = hash,
                quality = TransferUvQuality.Measure(mesh, uv, Vector2.one, Matrix4x4.identity) };
            var positions = mesh.vertices; var indices = mesh.triangles;
            for (int f = 0; f < indices.Length; f += 3)
            {
                int a = indices[f], b = indices[f + 1], c = indices[f + 2];
                var frame = TransferUvQuality.WorldFrame(positions[a], positions[b], positions[c], Matrix4x4.identity);
                if (frame.length == 0 || frame.y == 0) ++row.geometryDegenerateFaces;
                double area = Math.Abs(((double)uv[b].x - uv[a].x) * ((double)uv[c].y - uv[a].y)
                    - ((double)uv[b].y - uv[a].y) * ((double)uv[c].x - uv[a].x)) * .5;
                if (area == 0) ++row.uvDegenerateFaces;
                row.uvTriangleAreaSum += area;
            }
            // Per-face arrays are unnecessary for the corpus scoreboard.
            row.quality.faceAnisotropy = Array.Empty<float>();
            return row;
        }

        /// <summary>Flushes each result so interrupted runs cannot masquerade as complete reports.</summary>
        static void Write(StreamWriter writer, Row row) => writer.WriteLine(JsonUtility.ToJson(row));
    }
}
