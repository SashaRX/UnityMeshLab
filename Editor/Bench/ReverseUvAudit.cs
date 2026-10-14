using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Independent audit for progressive atlases; legacy forward-transfer
    /// quality scores do not understand intentional overlap.</summary>
    internal static class ReverseUvAudit
    {
        /// <summary>Audit reports a Verbose run keeps on disk; older ones in the same directory are deleted.</summary>
        internal const int KeptReports = 20;

        [Serializable] internal sealed class NodeKey { public int lod, node; public string key; }

        /// <summary>
        /// One node's reverse ancestry as the sidecar and the viewer keep it (schema 3): per face,
        /// the bits the overlap audit reads and the chart, packed, with the chain and the overlap
        /// relations. The node's whole report, with its face decisions and source partition, is
        /// the audit's (<see cref="Audit"/>), not the entry's.
        /// </summary>
        [Serializable] internal sealed class ProvenanceEntry
        {
            internal const int CurrentSchema = 3;
            /// <summary>Bits of a <see cref="faceFlags"/> value; the layer sits above <see cref="LayerShift"/>.</summary>
            internal const int Inherited = 1, ProjectionSupport = 2, IntentionalOverlap = 4, LayerShift = 3;

            public int schema = CurrentSchema, atlasSize, seedResolution;
            public float texelsPerUnit;
            /// <summary>The node: its LOD, its key (the mesh name) and the face count of its result mesh.</summary>
            public int lod; public string key; public int faceCount;
            /// <summary>Per face: <see cref="Inherited"/>, <see cref="ProjectionSupport"/>, <see cref="IntentionalOverlap"/> and the layer above <see cref="LayerShift"/>.</summary>
            public int[] faceFlags;
            /// <summary>Per face: the chart; an intentional overlap must share the chart of the face under it.</summary>
            public int[] faceChart;
            /// <summary>Pre-cleanup source faces the result does not contain.</summary>
            public int[] removedSourceFaces;
            public NodeKey[] chain;
            public ReverseUvTransfer.OverlapRelation[] overlaps;

            internal static int Pack(bool inherited, bool projectionSupport, bool intentionalOverlap, int layer)
                => (inherited ? Inherited : 0) | (projectionSupport ? ProjectionSupport : 0) | (intentionalOverlap ? IntentionalOverlap : 0)
                    | (Math.Max(0, layer) << LayerShift);
            internal static int Pack(ReverseUvTransfer.Face face) => Pack(face.inherited, face.projectionSupport, face.intentionalOverlap, face.layer);

            /// <summary>The entry of node <paramref name="key"/> at <paramref name="lod"/> with <paramref name="faces"/>; chain, overlaps and the atlas figures are the caller's.</summary>
            internal static ProvenanceEntry Of(int lod, string key, ReverseUvTransfer.Face[] faces, int[] removedSourceFaces = null) => new ProvenanceEntry {
                lod = lod, key = key, faceCount = faces.Length,
                faceFlags = Array.ConvertAll(faces, Pack), faceChart = Array.ConvertAll(faces, face => face.chart),
                removedSourceFaces = removedSourceFaces ?? Array.Empty<int>() };

            /// <summary>True when the entry is complete and describes a mesh of <paramref name="faces"/> faces.</summary>
            internal bool Describes(int faces) => schema == CurrentSchema && faceCount == faces && faceFlags != null && faceChart != null
                && faceFlags.Length == faces && faceChart.Length == faces;
            internal bool IsInherited(int face) => (faceFlags[face] & Inherited) != 0;
            internal bool HasProjectionSupport(int face) => (faceFlags[face] & ProjectionSupport) != 0;
            internal bool IsIntentionalOverlap(int face) => (faceFlags[face] & IntentionalOverlap) != 0;
            internal int Layer(int face) => faceFlags[face] >> LayerShift;
            internal int Chart(int face) => faceChart[face];
        }

        /// <summary>The shape of schema 2, as sidecars written before schema 3 hold it: the node's whole report. Read only.</summary>
        [Serializable] internal sealed class LegacyProvenanceEntry
        {
            public int schema = 2, atlasSize, seedResolution;
            public float texelsPerUnit;
            public ReverseUvTransfer.NodeReport surface;
            public NodeKey[] chain;
            public ReverseUvTransfer.OverlapRelation[] overlaps;
        }

        /// <summary>
        /// The entry <paramref name="json"/> holds, or null when it holds none. A schema 2 record is
        /// converted (<paramref name="upgraded"/>), so a sidecar written before schema 3 still restores.
        /// </summary>
        internal static ProvenanceEntry ParseProvenance(string json, out bool upgraded)
        {
            upgraded = false;
            if (string.IsNullOrEmpty(json)) return null;
            ProvenanceEntry entry;
            try { entry = JsonUtility.FromJson<ProvenanceEntry>(json); }
            catch (ArgumentException) { return null; }
            if (entry == null) return null;
            if (entry.schema == ProvenanceEntry.CurrentSchema) return entry;
            if (entry.schema != 2) return null;
            LegacyProvenanceEntry legacy;
            try { legacy = JsonUtility.FromJson<LegacyProvenanceEntry>(json); }
            catch (ArgumentException) { return null; }
            if (legacy?.surface?.faces == null) return null;
            var converted = ProvenanceEntry.Of(legacy.surface.lod, legacy.surface.key, legacy.surface.faces, legacy.surface.removedSourceFaces);
            converted.atlasSize = legacy.atlasSize; converted.seedResolution = legacy.seedResolution; converted.texelsPerUnit = legacy.texelsPerUnit;
            converted.chain = legacy.chain; converted.overlaps = legacy.overlaps;
            upgraded = true;
            return converted;
        }
        internal static ProvenanceEntry ParseProvenance(string json) => ParseProvenance(json, out _);
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

        /// <summary>One compact <see cref="ProvenanceEntry"/> per node of <paramref name="report"/>, as JSON, in node order.</summary>
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
            {
                var surface = report.nodes[i];
                var entry = ProvenanceEntry.Of(keys[i].lod, surface.key, surface.faces ?? Array.Empty<ReverseUvTransfer.Face>(), surface.removedSourceFaces);
                entry.atlasSize = report.atlasSize; entry.seedResolution = report.seedResolution; entry.texelsPerUnit = report.texelsPerUnit;
                entry.chain = keys;
                entry.overlaps = report.overlaps.Where(r => r.lod == keys[i].lod && (r.underMesh == keys[i].node || r.overMesh == keys[i].node)).ToArray();
                json[i] = JsonUtility.ToJson(entry);
            }
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

        /// <summary>
        /// The audit of a result, and its publish gate: every node's final metric and source
        /// partition and every level's overlap classification are checked, and a failure throws
        /// before anything is published.
        /// </summary>
        internal static Audit Measure(ReverseUvTransfer.Result result, ReverseUvTransfer.Level[] inputs)
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
            return audit;
        }

        /// <summary>
        /// Writes <paramref name="audit"/> as pretty JSON under the benchmark output override or
        /// <c>BenchmarkReports/ReverseUV</c> and returns the path. With <paramref name="keepLast"/>
        /// above zero, the directory's older <c>reverse_*.json</c> files beyond that many are deleted.
        /// </summary>
        internal static string Save(Audit audit, int keepLast = 0)
        {
            string directory = BenchmarkRecorder.OutputDirectoryOverride
                ?? Path.Combine(Path.GetDirectoryName(Application.dataPath), "BenchmarkReports", "ReverseUV");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "reverse_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(audit, true));
            if (keepLast > 0) Prune(directory, keepLast);
            return path;
        }

        /// <summary>Measure, then Save: the benchmark's and the tests' one call.</summary>
        internal static string Write(ReverseUvTransfer.Result result, ReverseUvTransfer.Level[] inputs) => Save(Measure(result, inputs));

        // The file names sort by their UTC stamp; a file another process holds is left.
        static void Prune(string directory, int keepLast)
        {
            var stale = Directory.GetFiles(directory, "reverse_*.json").OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).Skip(keepLast);
            foreach (string path in stale)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
