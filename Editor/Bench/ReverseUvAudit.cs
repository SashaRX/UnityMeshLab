using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Independent audit for progressive atlases; legacy forward-transfer
    /// quality scores do not understand intentional overlap.</summary>
    internal static class ReverseUvAudit
    {
        [Serializable] internal sealed class NodeKey { public int lod, node; public string key; }
        [Serializable] internal sealed class ProvenanceEntry
        {
            public int schema = 2, atlasSize, seedResolution;
            public float texelsPerUnit;
            public ReverseUvTransfer.NodeReport surface;
            public NodeKey[] chain;
            public ReverseUvTransfer.OverlapRelation[] overlaps;
        }
        [Serializable] internal sealed class Measurement
        {
            public int lod; public string key;
            public int triangles, invalidFaces, degenerateFaces, stretchedFaces, outOfBounds;
            public double worstAnisotropy;
            public long overlapPairs;
            public bool complete;
        }
        [Serializable] internal sealed class Audit
        {
            public ReverseUvTransfer.Report report;
            public List<Measurement> measurements = new List<Measurement>();
            public List<LevelMeasurement> levels = new List<LevelMeasurement>();
        }
        [Serializable] internal sealed class LevelMeasurement
        {
            public int lod, triangles, allowedOverlapPairs, unexpectedOverlapPairs;
            public bool complete;
            public double texelsPerUnitMin, texelsPerUnitMax;
            public double uvAreaFraction, inheritedAreaFraction;
            public int splitSourceFaces, refusedCutFaces, groups, charts;
        }

        internal static string[] Provenance(ReverseUvTransfer.Report report)
        {
            var keys = new NodeKey[report.nodes.Count];
            int node = 0, lod = report.nodes[0].lod;
            for (int i = 0; i < keys.Length; ++i)
            {
                var entry = report.nodes[i];
                if (entry.lod != lod) { lod = entry.lod; node = 0; }
                keys[i] = new NodeKey { lod = lod, node = node, key = entry.key };
                ++node;
            }
            var json = new string[keys.Length];
            for (int i = 0; i < json.Length; ++i)
                json[i] = JsonUtility.ToJson(new ProvenanceEntry { atlasSize = report.atlasSize,
                    seedResolution = report.seedResolution, texelsPerUnit = report.texelsPerUnit,
                    surface = report.nodes[i], chain = keys, overlaps = report.overlaps.Where(r => r.lod == keys[i].lod
                        && (r.underMesh == keys[i].node || r.overMesh == keys[i].node)).ToArray() });
            return json;
        }

        internal static string Write(ReverseUvTransfer.Result result, ReverseUvTransfer.Level[] inputs)
        {
            var audit = new Audit { report = result.report };
            int reportNode = 0;
            for (int level = 0; level < inputs.Length; ++level)
            {
                var pixels = new List<Vector2>(); var faces = new List<ReverseUvTransfer.Face>();
                var measurement = new LevelMeasurement { lod = inputs[level].lod, texelsPerUnitMin = double.MaxValue };
                double surfaceArea = 0, inheritedArea = 0;
                for (int node = 0; node < inputs[level].inputs.Length; ++node)
                {
                    var mesh = result.meshes[level][node];
                    var nodeReport = result.report.nodes[reportNode];
                    ValidateSourcePartition(nodeReport);
                    measurement.splitSourceFaces += nodeReport.splitSourceFaces;
                    measurement.refusedCutFaces += nodeReport.faces.Select((f, i) => (f, i)).Where(p => p.f.cutRefusal != null)
                        .Select(p => nodeReport.sourceFaces[p.i]).Distinct().Count();
                    var quality = TransferUvQuality.Measure(mesh, mesh.uv2, Vector2.one, inputs[level].inputs[node].toWorld);
                    if (!quality.overlapScanComplete || quality.invalidFaces > 0 || quality.degenerateFaces > 0
                        || quality.outOfBoundsVertices > 0 || double.IsNaN(quality.worstAnisotropy) || quality.worstAnisotropy > 4.001)
                        throw new InvalidOperationException($"Final reverse UV metric failed for LOD{inputs[level].lod} '{inputs[level].inputs[node].key}'; no result was published.");
                    audit.measurements.Add(new Measurement { lod = inputs[level].lod, key = inputs[level].inputs[node].key,
                        triangles = mesh.triangles.Length / 3, invalidFaces = quality.invalidFaces,
                        degenerateFaces = quality.degenerateFaces, stretchedFaces = quality.stretchedFaces,
                        outOfBounds = quality.outOfBoundsVertices, worstAnisotropy = quality.worstAnisotropy,
                        overlapPairs = quality.overlapPairs, complete = quality.overlapScanComplete });
                    var positions = mesh.vertices; var uv = mesh.uv2; var triangles = mesh.triangles;
                    var transform = inputs[level].inputs[node].toWorld;
                    for (int t = 0; t < triangles.Length; t += 3)
                    {
                        int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                        pixels.Add(uv[a]); pixels.Add(uv[b]); pixels.Add(uv[c]);
                        var u = uv[b] - uv[a]; var v = uv[c] - uv[a];
                        double uvArea = Math.Abs((double)u.x * v.y - (double)u.y * v.x);
                        double worldArea = Vector3.Cross(transform.MultiplyVector(positions[b] - positions[a]), transform.MultiplyVector(positions[c] - positions[a])).magnitude;
                        double density = result.report.atlasSize * Math.Sqrt(uvArea / worldArea);
                        surfaceArea += worldArea;
                        if (nodeReport.faces[t / 3].inherited) inheritedArea += worldArea;
                        measurement.uvAreaFraction += uvArea * .5;
                        measurement.texelsPerUnitMin = Math.Min(measurement.texelsPerUnitMin, density);
                        measurement.texelsPerUnitMax = Math.Max(measurement.texelsPerUnitMax, density);
                    }
                    faces.AddRange(result.report.nodes[reportNode++].faces);
                }
                var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = pixels.ToArray(),
                    indices = System.Linq.Enumerable.Range(0, pixels.Count).ToArray(), charts = new int[pixels.Count] }, default,
                    comparisonBudget: 2000000, collectConflicts: true);
                measurement.triangles = faces.Count; measurement.complete = scan.complete;
                measurement.inheritedAreaFraction = surfaceArea > 0 ? inheritedArea / surfaceArea : 0;
                measurement.charts = faces.Select(f => f.chart).Distinct().Count();
                measurement.groups = faces.Select(f => f.group).Distinct().Count();
                foreach (var (a, b) in scan.conflicts)
                {
                    if (faces[a].inherited && faces[b].inherited && (faces[a].intentionalOverlap || faces[b].intentionalOverlap)) ++measurement.allowedOverlapPairs;
                    else ++measurement.unexpectedOverlapPairs;
                }
                if (!scan.complete || measurement.unexpectedOverlapPairs > 0 || scan.degenerateFaces > 0 || scan.invalidFaces > 0 || scan.outOfBoundsVertices > 0)
                    throw new InvalidOperationException("Final reverse UV audit failed; no result was published.");
                audit.levels.Add(measurement);
            }
            string directory = BenchmarkRecorder.OutputDirectoryOverride
                ?? Path.Combine(Path.GetDirectoryName(Application.dataPath), "BenchmarkReports", "ReverseUV");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "reverse_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(audit, true));
            return path;
        }

        static void ValidateSourcePartition(ReverseUvTransfer.NodeReport node)
        {
            if (node.sourceBarycentrics == null) return;
            if (node.sourceBarycentrics.Length != node.faces.Length * 3 || node.sourceFaces.Length != node.faces.Length)
                throw new InvalidOperationException("Reverse UV source partition has inconsistent corner ancestry.");
            var areas = new Dictionary<int, double>();
            for (int f = 0; f < node.faces.Length; ++f)
            {
                var a = node.sourceBarycentrics[f * 3]; var b = node.sourceBarycentrics[f * 3 + 1]; var c = node.sourceBarycentrics[f * 3 + 2];
                double area = ((double)b.y - a.y) * ((double)c.z - a.z) - ((double)b.z - a.z) * ((double)c.y - a.y);
                if (area <= 0 || new[] { a, b, c }.Any(p => p.x < -1e-6f || p.y < -1e-6f || p.z < -1e-6f || Math.Abs(p.x + p.y + p.z - 1) > 1e-6))
                    throw new InvalidOperationException("Reverse UV subdivision leaves or inverts a source triangle.");
                areas.TryGetValue(node.sourceFaces[f], out double sum); areas[node.sourceFaces[f]] = sum + area;
            }
            if (areas.Values.Any(area => Math.Abs(area - 1) > 1e-5))
                throw new InvalidOperationException("Reverse UV subdivision does not retain the complete source triangle area.");
        }
    }
}
