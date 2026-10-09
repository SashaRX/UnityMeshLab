using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Progressive coarse-to-fine atlas. Intermediate coordinates are texels;
    /// one final square normalization is shared by every LOD.</summary>
    internal static class ReverseUvTransfer
    {
        [Serializable] internal sealed class Options
        {
            public int seedResolution = 256, padding = 2, maxAtlasSize = 8192;
            public float projectionReach = .05f, normalDot = .5f, maxAnisotropy = 4;
            public bool preserveProjectedOverlap;
            public long comparisonBudget = 2000000;
        }

        internal sealed class Input
        {
            internal Mesh mesh;
            internal Matrix4x4 toWorld = Matrix4x4.identity;
            internal string key;
        }

        internal sealed class Level
        {
            internal int lod;
            internal Input[] inputs;
        }

        [Serializable] internal sealed class Face
        {
            public int chart, parentLod = -1, parentMesh = -1, parentFace = -1;
            public int overlayMesh = -1, overlayFace = -1, layer;
            public bool inherited, intentionalOverlap, ambiguous, localFallback;
            public float distance;
        }

        [Serializable] internal sealed class NodeReport
        {
            public int lod;
            public string key;
            public bool seed;
            public int inheritedFaces, newFaces, overlapFaces, ambiguousFaces, localFallbackFaces;
            public Face[] faces;
        }

        [Serializable] internal sealed class Report
        {
            public int schema = 1, seedResolution, atlasSize, inheritedFaces, newFaces, overlapFaces, ambiguousFaces, localFallbackFaces;
            public float texelsPerUnit;
            public bool preservesOverlap;
            public List<NodeReport> nodes = new List<NodeReport>();
            public List<OverlapRelation> overlaps = new List<OverlapRelation>();
        }

        [Serializable] internal sealed class OverlapRelation
        {
            public int lod, underMesh, underFace, overMesh, overFace;
        }

        internal sealed class Result : IDisposable
        {
            internal Report report;
            internal readonly List<Mesh[]> meshes = new List<Mesh[]>();
            public void Dispose()
            {
                foreach (var level in meshes)
                    foreach (var mesh in level)
                        if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                meshes.Clear();
            }
        }

        sealed class Surface
        {
            internal Input input;
            internal Vector3[] positions;
            internal int[] indices;
            internal Vector2[] pixels;
            internal Face[] faces;
            internal int lod, node;
            internal int orientation;
        }

        sealed class Chart
        {
            internal int id;
            internal Vector3[] positions, normals;
            internal Vector2[] pixels;
            internal int[] indices, nodes, faces, layers, orientations;
            internal TriangleBvh bvh;
            internal Bounds bounds;
        }

        internal static async Task<Result> Build(Level[] levels, Options options, bool useAsync = false,
            CancellationToken token = default)
        {
            Validate(levels, options);
            var result = new Result { report = new Report { seedResolution = options.seedResolution,
                preservesOverlap = options.preserveProjectedOverlap } };
            var all = new List<Surface[]>();
            int nextChart = 0;
            float width = options.seedResolution, height = width;
            try
            {
                token.ThrowIfCancellationRequested();
                var seed = Capture(levels[0]);
                InitializeSeed(seed, options, ref nextChart, token);
                float density = Density(seed);
                result.report.texelsPerUnit = density;
                all.Add(seed);
                for (int level = 1; level < levels.Length; ++level)
                {
                    token.ThrowIfCancellationRequested();
                    var targets = Capture(levels[level]);
                    var donor = all[level - 1];
                    if (useAsync)
                        await Task.Run(() => Project(donor, targets, options, token), token);
                    else Project(donor, targets, options, token);
                    AppendNew(targets, density, options, ref width, ref height, ref nextChart, token);
                    RecordOverlapRelations(targets, result.report, options, token);
                    if (Math.Max(width, height) > options.maxAtlasSize)
                        throw new InvalidOperationException("Reverse UV atlas exceeds the configured size limit; no result was published.");
                    all.Add(targets);
                }
                int side = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Math.Max(width, height)));
                if (side > options.maxAtlasSize) throw new InvalidOperationException("Reverse UV square atlas exceeds the size limit.");
                result.report.atlasSize = side;
                foreach (var level in all)
                {
                    token.ThrowIfCancellationRequested();
                    var outputs = new Mesh[level.Length];
                    result.meshes.Add(outputs);
                    foreach (var surface in level)
                    {
                        var normalized = surface.pixels.Select(p => p / side).ToArray();
                        outputs[surface.node] = ReverseUvMesh.Copy(surface.input.mesh, surface.indices, normalized);
                        var node = new NodeReport { lod = surface.lod, key = surface.input.key, faces = surface.faces,
                            seed = surface.lod == levels[0].lod };
                        foreach (var face in surface.faces)
                        {
                            if (face.inherited) ++node.inheritedFaces;
                            else if (!node.seed) ++node.newFaces;
                            if (face.intentionalOverlap) ++node.overlapFaces;
                            if (face.ambiguous) ++node.ambiguousFaces;
                            if (face.localFallback) ++node.localFallbackFaces;
                        }
                        result.report.nodes.Add(node);
                        result.report.inheritedFaces += node.inheritedFaces;
                        result.report.newFaces += node.newFaces;
                        result.report.overlapFaces += node.overlapFaces;
                        result.report.ambiguousFaces += node.ambiguousFaces;
                        result.report.localFallbackFaces += node.localFallbackFaces;
                    }
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        static void Validate(Level[] levels, Options options)
        {
            if (levels == null || levels.Length < 2 || options == null)
                throw new ArgumentException("Reverse UV requires a coarse-to-fine chain with at least two levels.");
            if (options.seedResolution < 16 || options.seedResolution > options.maxAtlasSize || options.padding < 0
                || options.maxAtlasSize > 16384 || options.projectionReach <= 0 || !Finite(options.projectionReach)
                || options.normalDot <= 0 || options.normalDot > 1 || !Finite(options.normalDot) || options.maxAnisotropy < 1
                || !Finite(options.maxAnisotropy) || options.comparisonBudget <= 0)
                throw new ArgumentException("Invalid reverse UV settings.");
            for (int i = 0; i < levels.Length; ++i)
            {
                if (levels[i] == null || levels[i].inputs == null || levels[i].inputs.Length == 0
                    || (i > 0 && levels[i].lod >= levels[i - 1].lod))
                    throw new ArgumentException("Levels must be nonempty and ordered from coarsest to finest.");
                foreach (var input in levels[i].inputs)
                    if (input == null || !input.mesh || !input.mesh.isReadable
                        || !Finite(input.toWorld.determinant) || Math.Abs(input.toWorld.determinant) < 1e-12f)
                        throw new ArgumentException("Reverse UV inputs require readable meshes and nonsingular transforms.");
            }
        }

        static Surface[] Capture(Level level)
        {
            var surfaces = new Surface[level.inputs.Length];
            for (int node = 0; node < surfaces.Length; ++node)
            {
                var input = level.inputs[node];
                var indices = input.mesh.triangles;
                if (indices.Length == 0 || indices.Length > 750000)
                    throw new InvalidOperationException("Reverse UV prototype supports 1–250000 triangles per input.");
                var positions = input.mesh.vertices.Select(input.toWorld.MultiplyPoint3x4).ToArray();
                for (int f = 0; f < indices.Length; f += 3)
                    if (!MeshGeometry.HasArea(positions[indices[f]], positions[indices[f + 1]], positions[indices[f + 2]]))
                        throw new InvalidOperationException($"Reverse UV input '{input.key}' LOD{level.lod} face {f / 3} has a degenerate geometric triangle.");
                surfaces[node] = new Surface { input = input, indices = indices, positions = positions,
                    pixels = new Vector2[indices.Length], faces = Enumerable.Range(0, indices.Length / 3).Select(_ => new Face()).ToArray(),
                    lod = level.lod, node = node, orientation = Math.Sign(input.toWorld.determinant) };
            }
            return surfaces;
        }

        static void InitializeSeed(Surface[] seed, Options options, ref int nextChart, CancellationToken token)
        {
            foreach (var surface in seed)
            {
                token.ThrowIfCancellationRequested();
                var uv = surface.input.mesh.uv2;
                if (uv.Length != surface.positions.Length) throw new InvalidOperationException("Coarsest LOD requires a prepared UV2 atlas.");
                for (int i = 0; i < surface.indices.Length; ++i)
                {
                    var p = uv[surface.indices[i]];
                    if (!Finite(p.x) || !Finite(p.y) || p.x < 0 || p.y < 0 || p.x > 1 || p.y > 1)
                        throw new InvalidOperationException("Coarsest UV2 is invalid or outside its atlas.");
                    surface.pixels[i] = p * options.seedResolution;
                }
                AssignCharts(surface, Enumerable.Range(0, surface.faces.Length).ToArray(), ref nextChart);
                for (int f = 0; f < surface.faces.Length; ++f)
                    if (Anisotropy(surface, f) > options.maxAnisotropy)
                        throw new InvalidOperationException($"Coarsest UV2 '{surface.input.key}' face {f} is collapsed or stretched (anisotropy {Anisotropy(surface,f):G5}); prepare its atlas first.");
            }
            var geometry = Atlas(seed);
            var quality = UvAtlasDiagnostics.Measure(geometry, token, comparisonBudget: options.comparisonBudget);
            if (!quality.complete || quality.pairs > 0 || quality.invalidFaces > 0 || quality.degenerateFaces > 0)
                throw new InvalidOperationException("Coarsest UV2 must have a fully checked nonoverlapping atlas.");
        }

        static float Density(Surface[] seed)
        {
            var densities = new List<double>();
            foreach (var s in seed)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    int t = f * 3;
                    double area = Vector3.Cross(s.positions[s.indices[t + 1]] - s.positions[s.indices[t]],
                        s.positions[s.indices[t + 2]] - s.positions[s.indices[t]]).magnitude;
                    densities.Add(Math.Sqrt(Math.Abs(Cross(s.pixels[t + 1] - s.pixels[t], s.pixels[t + 2] - s.pixels[t])) / area));
                }
            densities.Sort();
            return (float)densities[densities.Count / 2];
        }

        static List<Chart> Charts(Surface[] donor)
        {
            var grouped = new SortedDictionary<int, List<(Surface s, int f)>>();
            foreach (var s in donor)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    int id = s.faces[f].chart;
                    if (!grouped.TryGetValue(id, out var faces)) { faces = new List<(Surface, int)>(); grouped.Add(id, faces); }
                    faces.Add((s, f));
                }
            var charts = new List<Chart>();
            foreach (var pair in grouped)
            {
                int n = pair.Value.Count;
                var chart = new Chart { id = pair.Key, positions = new Vector3[n * 3], pixels = new Vector2[n * 3],
                    indices = Enumerable.Range(0, n * 3).ToArray(), normals = new Vector3[n], nodes = new int[n], faces = new int[n], layers = new int[n], orientations = new int[n] };
                for (int f = 0; f < n; ++f)
                {
                    var (s, face) = pair.Value[f];
                    for (int k = 0; k < 3; ++k)
                    {
                        chart.positions[f * 3 + k] = s.positions[s.indices[face * 3 + k]];
                        chart.pixels[f * 3 + k] = s.pixels[face * 3 + k];
                    }
                    chart.normals[f] = Normal(s, face);
                    chart.nodes[f] = s.node; chart.faces[f] = face; chart.layers[f] = s.faces[face].layer;
                    chart.orientations[f] = s.orientation;
                }
                chart.bounds = new Bounds(chart.positions[0], Vector3.zero);
                foreach (var p in chart.positions) chart.bounds.Encapsulate(p);
                chart.bvh = new TriangleBvh(chart.positions, chart.indices);
                charts.Add(chart);
            }
            return charts;
        }

        static readonly Vector3[] Interior = { new Vector3(.5f, .5f, 0), new Vector3(0, .5f, .5f),
            new Vector3(.5f, 0, .5f), Vector3.one / 3 };

        static void Project(Surface[] donor, Surface[] targets, Options options, CancellationToken token)
        {
            var charts = Charts(donor);
            long queries = 0;
            foreach (var target in targets)
                for (int f = 0; f < target.faces.Length; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    int t = f * 3;
                    var a = target.positions[target.indices[t]]; var b = target.positions[target.indices[t + 1]];
                    var c = target.positions[target.indices[t + 2]]; var center = (a + b + c) / 3;
                    var normal = Normal(target, f);
                    Chart winner = null;
                    var best = new TriangleBvh.HitResult { triangleIndex = -1, distSq = float.MaxValue };
                    bool ambiguous = false;
                    foreach (var chart in charts)
                    {
                        if (chart.bounds.SqrDistance(center) > options.projectionReach * options.projectionReach) continue;
                        if (++queries > options.comparisonBudget) throw new InvalidOperationException("Reverse UV projection query budget exceeded.");
                        var hit = chart.bvh.FindNearestNormalFiltered(center, normal, chart.normals, options.normalDot, options.projectionReach);
                        if (hit.triangleIndex < 0) continue;
                        float tolerance = Math.Max(1e-12f, options.projectionReach * options.projectionReach * 1e-6f);
                        if (hit.distSq < best.distSq - tolerance) { winner = chart; best = hit; ambiguous = false; }
                        else if (Math.Abs(hit.distSq - best.distSq) <= tolerance) ambiguous = true;
                    }
                    var record = target.faces[f];
                    record.ambiguous = ambiguous;
                    if (winner == null || ambiguous) continue;
                    bool valid = true;
                    float maxDistance = best.distSq;
                    for (int k = 0; k < 3; ++k)
                    {
                        if (++queries > options.comparisonBudget) throw new InvalidOperationException("Reverse UV projection query budget exceeded.");
                        var hit = winner.bvh.FindNearestNormalFiltered(target.positions[target.indices[t + k]], normal,
                            winner.normals, options.normalDot, options.projectionReach);
                        if (hit.triangleIndex < 0) { valid = false; break; }
                        target.pixels[t + k] = Pixel(winner, hit);
                        maxDistance = Math.Max(maxDistance, hit.distSq);
                    }
                    int donorCorner = best.triangleIndex * 3;
                    int expectedSign = Math.Sign(Cross(winner.pixels[donorCorner + 1] - winner.pixels[donorCorner],
                        winner.pixels[donorCorner + 2] - winner.pixels[donorCorner])) * winner.orientations[best.triangleIndex];
                    int actualSign = Math.Sign(Cross(target.pixels[t + 1] - target.pixels[t], target.pixels[t + 2] - target.pixels[t])) * target.orientation;
                    if (!valid || actualSign != expectedSign || Anisotropy(target, f) > options.maxAnisotropy)
                    { record.ambiguous = true; continue; }
                    float span = Math.Max((target.pixels[t + 1] - target.pixels[t]).magnitude, (target.pixels[t + 2] - target.pixels[t]).magnitude);
                    foreach (var bary in Interior)
                    {
                        if (++queries > options.comparisonBudget) throw new InvalidOperationException("Reverse UV projection query budget exceeded.");
                        var hit = winner.bvh.FindNearestNormalFiltered(a * bary.x + b * bary.y + c * bary.z, normal,
                            winner.normals, options.normalDot, options.projectionReach);
                        var interpolated = target.pixels[t] * bary.x + target.pixels[t + 1] * bary.y + target.pixels[t + 2] * bary.z;
                        if (hit.triangleIndex < 0 || (Pixel(winner, hit) - interpolated).magnitude > Math.Max(.25f, span * .02f))
                        { valid = false; break; }
                    }
                    if (!valid) { record.ambiguous = true; continue; }
                    if (!CoveredByParentLayer(winner, winner.layers[best.triangleIndex], target.pixels, t,
                        options, ref queries, token)) { record.ambiguous = true; continue; }
                    record.inherited = true; record.chart = winner.id; record.distance = Mathf.Sqrt(maxDistance);
                    record.parentLod = donor[0].lod; record.parentMesh = winner.nodes[best.triangleIndex];
                    record.parentFace = winner.faces[best.triangleIndex]; record.layer = winner.layers[best.triangleIndex];
                }
            SnapContinuousCorners(targets, options);
            ResolveOverlaps(targets, options, token);
        }

        // Interior probes alone miss holes between probes. Parent faces in one
        // layer have disjoint UV interiors, so their clipped areas measure exact
        // coverage without counting overlapping detail layers twice.
        static bool CoveredByParentLayer(Chart parent, int layer, Vector2[] pixels, int start,
            Options options, ref long comparisons, CancellationToken token)
        {
            var triangle = new[] { pixels[start], pixels[start + 1], pixels[start + 2] };
            double area = Math.Abs(Cross(triangle[1] - triangle[0], triangle[2] - triangle[0]));
            double covered = 0;
            var min = Vector2.Min(triangle[0], Vector2.Min(triangle[1], triangle[2]));
            var max = Vector2.Max(triangle[0], Vector2.Max(triangle[1], triangle[2]));
            for (int f = 0; f < parent.layers.Length; ++f)
            {
                token.ThrowIfCancellationRequested();
                if (++comparisons > options.comparisonBudget) throw new InvalidOperationException("Reverse UV footprint budget exceeded.");
                if (parent.layers[f] != layer) continue;
                int t = f * 3;
                var a = parent.pixels[t]; var b = parent.pixels[t + 1]; var c = parent.pixels[t + 2];
                var lo = Vector2.Min(a, Vector2.Min(b, c)); var hi = Vector2.Max(a, Vector2.Max(b, c));
                if (hi.x <= min.x || hi.y <= min.y || lo.x >= max.x || lo.y >= max.y) continue;
                var polygon = new List<Vector2>(triangle);
                double orientation = Math.Sign(Cross(b - a, c - a));
                for (int edge = 0; edge < 3 && polygon.Count > 0; ++edge)
                {
                    var x = parent.pixels[t + edge]; var y = parent.pixels[t + (edge + 1) % 3];
                    var clipped = new List<Vector2>(); var previous = polygon[polygon.Count - 1];
                    double previousSide = orientation * Cross(y - x, previous - x);
                    foreach (var point in polygon)
                    {
                        double side = orientation * Cross(y - x, point - x);
                        if ((side >= 0) != (previousSide >= 0))
                            clipped.Add(Vector2.LerpUnclamped(previous, point, (float)(previousSide / (previousSide - side))));
                        if (side >= 0) clipped.Add(point);
                        previous = point; previousSide = side;
                    }
                    polygon = clipped;
                }
                double clippedArea = 0;
                for (int k = 1; k + 1 < polygon.Count; ++k)
                    clippedArea += Cross(polygon[k] - polygon[0], polygon[k + 1] - polygon[0]);
                covered += Math.Abs(clippedArea);
            }
            return area > 0 && covered >= area * (1 - 1e-5);
        }

        static void SnapContinuousCorners(Surface[] targets, Options options)
        {
            var canonical = new Dictionary<(Vector3, int), Vector2>();
            foreach (var s in targets)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    var face = s.faces[f];
                    if (!face.inherited) continue;
                    for (int k = 0; k < 3; ++k)
                    {
                        int t = f * 3 + k;
                        var key = (s.positions[s.indices[t]], face.chart);
                        if (canonical.TryGetValue(key, out var pixel))
                        {
                            if ((pixel - s.pixels[t]).sqrMagnitude <= 1e-6f) s.pixels[t] = pixel;
                        }
                        else canonical.Add(key, s.pixels[t]);
                    }
                    if (Anisotropy(s, f) > options.maxAnisotropy)
                    { face.inherited = false; face.ambiguous = true; }
                }
        }

        static Vector2 Pixel(Chart chart, TriangleBvh.HitResult hit)
        {
            int t = hit.triangleIndex * 3;
            return chart.pixels[t] * hit.barycentric.x + chart.pixels[t + 1] * hit.barycentric.y + chart.pixels[t + 2] * hit.barycentric.z;
        }

        static void RecordOverlapRelations(Surface[] surfaces, Report report, Options options, CancellationToken token)
        {
            var scan = UvAtlasDiagnostics.Measure(Atlas(surfaces), token, comparisonBudget: options.comparisonBudget, collectConflicts:true);
            if(!scan.complete || scan.invalidFaces>0 || scan.degenerateFaces>0)
                throw new InvalidOperationException("Reverse UV level failed its complete final validation.");
            var refs = new List<(Surface s,int f)>();
            foreach(var s in surfaces) for(int f=0;f<s.faces.Length;++f) refs.Add((s,f));
            foreach(var (a,b) in scan.conflicts)
            {
                var under=refs[a]; var over=refs[b];
                if(under.s.faces[under.f].layer>over.s.faces[over.f].layer) { var swap=under; under=over; over=swap; }
                var parent=under.s.faces[under.f]; var detail=over.s.faces[over.f];
                if(!options.preserveProjectedOverlap || !parent.inherited || !detail.inherited
                    || !detail.intentionalOverlap || detail.layer<=parent.layer)
                    throw new InvalidOperationException("Reverse UV overlap has no ordered intentional ancestry.");
                report.overlaps.Add(new OverlapRelation {lod=under.s.lod,underMesh=under.s.node,underFace=under.f,overMesh=over.s.node,overFace=over.f});
            }
        }

        static void ResolveOverlaps(Surface[] surfaces, Options options, CancellationToken token)
        {
            var geometry = Atlas(surfaces, inheritedOnly: true);
            var scan = UvAtlasDiagnostics.Measure(geometry, token, comparisonBudget: options.comparisonBudget, collectConflicts: true);
            if (!scan.complete) throw new InvalidOperationException("Reverse UV overlap audit exceeded its budget; no atlas was published.");
            var refs = new List<(Surface s, int f)>();
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f) refs.Add((s, f));
            var conflicts = new List<int>[refs.Count];
            foreach (var (a, b) in scan.conflicts)
            {
                if (conflicts[a] == null) conflicts[a] = new List<int>();
                if (conflicts[b] == null) conflicts[b] = new List<int>();
                conflicts[a].Add(b); conflicts[b].Add(a);
            }
            var accepted = new bool[refs.Count];
            foreach (int i in Enumerable.Range(0, refs.Count).OrderBy(i => refs[i].s.faces[refs[i].f].layer)
                .ThenBy(i => refs[i].s.faces[refs[i].f].distance).ThenBy(i => i))
            {
                var (s, f) = refs[i]; var face = s.faces[f];
                if (!face.inherited) continue;
                if (conflicts[i] != null)
                    foreach (int other in conflicts[i])
                    {
                        if (!accepted[other]) continue;
                        var (parent, pf) = refs[other];
                        if (!options.preserveProjectedOverlap || SharedEdge(s, f, parent, pf))
                        { face.inherited = false; face.ambiguous = true; break; }
                        face.intentionalOverlap = true; face.overlayMesh = parent.node; face.overlayFace = pf;
                        face.layer = Math.Max(face.layer, parent.faces[pf].layer + 1);
                    }
                accepted[i] = face.inherited;
            }
        }

        static bool SharedEdge(Surface a, int fa, Surface b, int fb)
        {
            int shared = 0;
            for (int i = 0; i < 3; ++i)
                for (int j = 0; j < 3; ++j)
                    if (a.positions[a.indices[fa * 3 + i]].Equals(b.positions[b.indices[fb * 3 + j]])) { ++shared; break; }
            return shared >= 2;
        }

        static RemeshNative.Geometry Atlas(Surface[] surfaces, bool inheritedOnly = false)
        {
            var pixels = new List<Vector2>(); var labels = new List<int>();
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f)
                    for (int k = 0; k < 3; ++k)
                    {
                        pixels.Add(inheritedOnly && !s.faces[f].inherited ? Vector2.zero : s.pixels[f * 3 + k]);
                        labels.Add(s.faces[f].chart);
                    }
            return new RemeshNative.Geometry { uv = pixels.ToArray(), charts = labels.ToArray(),
                indices = Enumerable.Range(0, pixels.Count).ToArray() };
        }

        static void AppendNew(Surface[] surfaces, float density, Options options, ref float width,
            ref float height, ref int nextChart, CancellationToken token)
        {
            var refs = new List<(Surface s, int f)>(); var positions = new List<Vector3>();
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f)
                    if (!s.faces[f].inherited)
                    {
                        refs.Add((s, f));
                        for (int k = 0; k < 3; ++k) positions.Add(s.positions[s.indices[f * 3 + k]]);
                    }
            if (refs.Count == 0) return;
            token.ThrowIfCancellationRequested();
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            try
            {
                // Weld geometry-only positions for the unwrap. Donor UV0 seams must
                // not fragment this new surface parameterization.
                var unique = new Dictionary<Vector3, int>(); var vertices = new List<Vector3>();
                var indices = new int[positions.Count];
                for (int i = 0; i < indices.Length; ++i)
                {
                    if (!unique.TryGetValue(positions[i], out int v)) { v = vertices.Count; unique.Add(positions[i], v); vertices.Add(positions[i]); }
                    indices[i] = v;
                }
                mesh.SetVertices(vertices); mesh.triangles = indices;
                UnwrapParam.SetDefaults(out var settings);
                settings.packMargin = .02f;
                var uv = Unwrapping.GeneratePerTriangleUV(mesh, settings);
                if (uv.Length != positions.Count) throw new InvalidOperationException("New reverse UV surfaces could not be unwrapped.");
                var pixels = ReverseUvNewCharts.Prepare(positions.ToArray(), uv, density, options, token, out var fallback);
                var min = pixels[0]; var max = min;
                foreach (var p in pixels) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
                var offset = new Vector2(width + options.padding, options.padding);
                for (int i = 0; i < refs.Count; ++i)
                {
                    var (s, f) = refs[i];
                    var face = s.faces[f];
                    face.parentLod = face.parentMesh = face.parentFace = -1;
                    face.overlayMesh = face.overlayFace = -1; face.layer = 0; face.intentionalOverlap = false;
                    face.localFallback = fallback[i];
                    for (int k = 0; k < 3; ++k) s.pixels[f * 3 + k] = pixels[i * 3 + k] - min + offset;
                    if (Anisotropy(s, f) > options.maxAnisotropy)
                        throw new InvalidOperationException($"New reverse UV '{s.input.key}' face {f} exceeds the stretch limit (anisotropy {Anisotropy(s,f):G5}, local fallback {face.localFallback}); atlas was not published.");
                }
                foreach (var s in surfaces)
                    AssignCharts(s, Enumerable.Range(0, s.faces.Length).Where(f => !s.faces[f].inherited).ToArray(), ref nextChart);
                var quality = UvAtlasDiagnostics.Measure(Atlas(surfaces), token, comparisonBudget: options.comparisonBudget, collectConflicts: true);
                if (!quality.complete || quality.degenerateFaces > 0 || quality.invalidFaces > 0
                    || (!options.preserveProjectedOverlap && quality.pairs > 0))
                    throw new InvalidOperationException("Expanded reverse UV atlas failed validation.");
                var faceRefs = new List<Face>();
                foreach (var surface in surfaces) faceRefs.AddRange(surface.faces);
                foreach (var (a, b) in quality.conflicts)
                    if (!faceRefs[a].inherited || !faceRefs[b].inherited
                        || (!faceRefs[a].intentionalOverlap && !faceRefs[b].intentionalOverlap))
                        throw new InvalidOperationException("Expanded reverse UV atlas contains an unclassified overlap.");
                width = offset.x + max.x - min.x + options.padding;
                height = Math.Max(height, offset.y + max.y - min.y + options.padding);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        static void AssignCharts(Surface surface, int[] faces, ref int nextChart)
        {
            var union = new DisjointSet(faces.Length);
            var edges = new Dictionary<((Vector3, Vector2), (Vector3, Vector2)), int>();
            for (int i = 0; i < faces.Length; ++i)
                for (int k = 0; k < 3; ++k)
                {
                    int a = faces[i] * 3 + k, b = faces[i] * 3 + (k + 1) % 3;
                    var x = (surface.positions[surface.indices[a]], surface.pixels[a]);
                    var y = (surface.positions[surface.indices[b]], surface.pixels[b]);
                    if (edges.TryGetValue((x, y), out int other) || edges.TryGetValue((y, x), out other)) union.Union(i, other);
                    else edges.Add((x, y), i);
                }
            var ids = new Dictionary<int, int>();
            for (int i = 0; i < faces.Length; ++i)
            {
                int root = union.Find(i);
                if (!ids.TryGetValue(root, out int id)) { id = nextChart++; ids.Add(root, id); }
                surface.faces[faces[i]].chart = id;
            }
        }

        static Vector3 Normal(Surface s, int face)
        {
            int t = face * 3;
            return MeshGeometry.UnitDirection(Vector3.Cross(s.positions[s.indices[t + 1]] - s.positions[s.indices[t]],
                s.positions[s.indices[t + 2]] - s.positions[s.indices[t]]) * s.orientation);
        }

        static double Anisotropy(Surface s, int f)
        {
            int t = f * 3;
            return TriangleAnisotropy(s.positions[s.indices[t]], s.positions[s.indices[t+1]], s.positions[s.indices[t+2]],
                s.pixels[t], s.pixels[t+1], s.pixels[t+2]);
        }

        internal static double TriangleAnisotropy(Vector3 p, Vector3 q, Vector3 r, Vector2 aUv, Vector2 bUv, Vector2 cUv)
        {
            var (length, x, y) = TriangleFrame(p, q, r);
            // Promote before subtraction: float edge/dot arithmetic introduces
            // order-dependent shear on nearly collinear captured triangles.
            double ux = (double)bUv.x - aUv.x, uy = (double)bUv.y - aUv.y;
            double vx = (double)cUv.x - aUv.x, vy = (double)cUv.y - aUv.y;
            double j00 = ux / length, j10 = uy / length, j01 = (vx - ux * x / length) / y, j11 = (vy - uy * x / length) / y;
            double a = j00 * j00 + j10 * j10, b = j01 * j01 + j11 * j11, c = j00 * j01 + j10 * j11;
            double max = (a + b + Math.Sqrt(Math.Max(0, (a - b) * (a - b) + 4 * c * c))) * .5;
            double determinant = j00 * j11 - j01 * j10;
            double min = max > 0 ? determinant * determinant / max : 0;
            double ratio = min > 0 ? Math.Sqrt(max / min) : double.PositiveInfinity;
            return double.IsNaN(ratio) ? double.PositiveInfinity : ratio;
        }

        internal static (double length, double x, double y) TriangleFrame(Vector3 p, Vector3 q, Vector3 r)
        {
            double ex = (double)q.x - p.x, ey = (double)q.y - p.y, ez = (double)q.z - p.z;
            double dx = (double)r.x - p.x, dy = (double)r.y - p.y, dz = (double)r.z - p.z;
            double length = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            double cx = ey * dz - ez * dy, cy = ez * dx - ex * dz, cz = ex * dy - ey * dx;
            return (length, (ex * dx + ey * dy + ez * dz) / length,
                Math.Sqrt(cx * cx + cy * cy + cz * cz) / length);
        }

        static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }
}
