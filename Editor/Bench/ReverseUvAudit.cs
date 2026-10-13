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

        sealed class LevelAudit
        {
            internal readonly List<Vector2> pixels = new List<Vector2>();
            internal readonly List<ReverseUvTransfer.Face> faces = new List<ReverseUvTransfer.Face>();
            internal readonly List<(int mesh,int face)> faceKeys = new List<(int,int)>();
            internal readonly LevelMeasurement measurement;
            internal double surfaceArea, inheritedArea;
            internal LevelAudit(int lod) => measurement = new LevelMeasurement { lod = lod, texelsPerUnitMin = double.MaxValue };
        }

        internal static string Write(ReverseUvTransfer.Result result, ReverseUvTransfer.Level[] inputs)
        {
            var audit = new Audit { report = result.report };
            int reportNode = 0;
            for (int level = 0; level < inputs.Length; ++level)
            {
                var state = new LevelAudit(inputs[level].lod);
                for (int node = 0; node < inputs[level].inputs.Length; ++node)
                    AuditNode(result, inputs, level, node, audit, state, reportNode++);
                AuditLevel(result, state);
                audit.levels.Add(state.measurement);
            }
            string directory = BenchmarkRecorder.OutputDirectoryOverride
                ?? Path.Combine(Path.GetDirectoryName(Application.dataPath), "BenchmarkReports", "ReverseUV");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "reverse_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(audit, true));
            return path;
        }

        static void AuditNode(ReverseUvTransfer.Result result, ReverseUvTransfer.Level[] inputs, int level, int node,
            Audit audit, LevelAudit state, int reportNode)
        {
            var mesh = result.meshes[level][node];
            var nodeReport = result.report.nodes[reportNode];
            ValidateSourcePartition(nodeReport);
            state.measurement.splitSourceFaces += nodeReport.splitSourceFaces;
            state.measurement.refusedCutFaces += nodeReport.faces.Select((f, i) => (f, i)).Where(p => p.f.cutRefusal != null)
                .Select(p => nodeReport.sourceFaces[p.i]).Distinct().Count();
            var quality = TransferUvQuality.Measure(mesh, mesh.uv2, Vector2.one, inputs[level].inputs[node].toWorld);
            if (!quality.overlapScanComplete || quality.invalidFaces > 0 || quality.degenerateFaces > 0
                || quality.outOfBoundsVertices > 0 || double.IsNaN(quality.worstAnisotropy) )
                throw new InvalidOperationException($"Final reverse UV metric failed for LOD{inputs[level].lod} '{inputs[level].inputs[node].key}'; no result was published.");
            audit.measurements.Add(new Measurement { lod = inputs[level].lod, key = inputs[level].inputs[node].key,
                triangles = mesh.triangles.Length / 3, invalidFaces = quality.invalidFaces,
                degenerateFaces = quality.degenerateFaces, stretchedFaces = quality.stretchedFaces,
                outOfBounds = quality.outOfBoundsVertices, worstAnisotropy = quality.worstAnisotropy,
                overlapPairs = quality.overlapPairs, complete = quality.overlapScanComplete });
            var meshPositions = mesh.vertices; var meshUv = mesh.uv2; var meshTriangles = mesh.triangles;
            var transform = inputs[level].inputs[node].toWorld;
            AuditPrecision(meshPositions, meshUv, meshTriangles, transform, result.report.atlasSize, inputs[level].lod, inputs[level].inputs[node].key);
            AccumulateFaces(meshPositions, meshUv, meshTriangles, transform, result.report.atlasSize, nodeReport, state);
            state.faces.AddRange(nodeReport.faces);
            for (int f = 0; f < nodeReport.faces.Length; ++f) state.faceKeys.Add((node,f));
        }

        static void AuditPrecision(Vector3[] positions, Vector2[] uv, int[] triangles, Matrix4x4 transform, int atlasSize, int lod, string key)
        {
            for(int t=0;t<triangles.Length;t+=3)
            {
                var frame=TransferUvQuality.WorldFrame(positions[triangles[t]],positions[triangles[t+1]],positions[triangles[t+2]],transform);
                if(!ReverseUvShellPrecision.Fits(frame,uv[triangles[t]]*atlasSize,
                    uv[triangles[t+1]]*atlasSize,uv[triangles[t+2]]*atlasSize,4.001))
                    throw new InvalidOperationException($"Final reverse UV metric failed for LOD{lod} '{key}' face {t/3}; no result was published.");
            }
        }

        static void AccumulateFaces(Vector3[] positions, Vector2[] uv, int[] triangles, Matrix4x4 transform,
            int atlasSize, ReverseUvTransfer.NodeReport nodeReport, LevelAudit state)
        {
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                state.pixels.Add(uv[a]); state.pixels.Add(uv[b]); state.pixels.Add(uv[c]);
                var u = uv[b] - uv[a]; var v = uv[c] - uv[a];
                double uvArea = Math.Abs((double)u.x * v.y - (double)u.y * v.x);
                double worldArea = Vector3.Cross(transform.MultiplyVector(positions[b] - positions[a]), transform.MultiplyVector(positions[c] - positions[a])).magnitude;
                double density = atlasSize * Math.Sqrt(uvArea / worldArea);
                state.surfaceArea += worldArea;
                if (nodeReport.faces[t / 3].inherited) state.inheritedArea += worldArea;
                state.measurement.uvAreaFraction += uvArea * .5;
                state.measurement.texelsPerUnitMin = Math.Min(state.measurement.texelsPerUnitMin, density);
                state.measurement.texelsPerUnitMax = Math.Max(state.measurement.texelsPerUnitMax, density);
            }
        }

        static void AuditLevel(ReverseUvTransfer.Result result, LevelAudit state)
        {
            var pixels = state.pixels; var faces = state.faces; var faceKeys = state.faceKeys; var measurement = state.measurement;
            double surfaceArea = state.surfaceArea, inheritedArea = state.inheritedArea;
            var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = pixels.ToArray(),
                indices = System.Linq.Enumerable.Range(0, pixels.Count).ToArray(), charts = new int[pixels.Count] }, default,
                comparisonBudget: 2000000, collectConflicts: true);
            measurement.triangles = faces.Count; measurement.complete = scan.complete;
            measurement.inheritedAreaFraction = surfaceArea > 0 ? inheritedArea / surfaceArea : 0;
            measurement.charts = faces.Select(f => f.chart).Distinct().Count();
            measurement.groups = faces.Select(f => f.group).Distinct().Count();
            var allowed = new HashSet<((int mesh,int face) under,(int mesh,int face) over)>(result.report.overlaps
                .Where(r => r.lod == measurement.lod)
                .Select(r => ((r.underMesh,r.underFace),(r.overMesh,r.overFace))));
            foreach (var (a, b) in scan.conflicts)
            {
                int under = faces[a].layer < faces[b].layer ? a : b;
                int over = faces[a].layer < faces[b].layer ? b : a;
                if (AllowsOverlap(faces[under], faces[over]) && allowed.Contains((faceKeys[under],faceKeys[over]))) ++measurement.allowedOverlapPairs;
                else ++measurement.unexpectedOverlapPairs;
            }
            if (!scan.complete || measurement.unexpectedOverlapPairs > 0 || scan.degenerateFaces > 0 || scan.invalidFaces > 0 || scan.outOfBoundsVertices > 0)
                throw new InvalidOperationException($"Final reverse UV audit failed for LOD{measurement.lod}: complete={scan.complete}, unexpected overlaps={measurement.unexpectedOverlapPairs}, "
                    + $"collapsed={scan.degenerateFaces}, invalid={scan.invalidFaces}, outside={scan.outOfBoundsVertices}; {string.Join("; ",scan.samples)}. No result was published.");
        }

        static bool AllowsOverlap(ReverseUvTransfer.Face under, ReverseUvTransfer.Face over)
            => (under.inherited || under.projectionSupport) && over.inherited && under.chart == over.chart
                && under.layer < over.layer && over.intentionalOverlap;

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
