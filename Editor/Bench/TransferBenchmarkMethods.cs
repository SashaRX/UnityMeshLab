using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Comparison baselines only; these do not alter the production transfer strategy.</summary>
    internal static class TransferBenchmarkMethods
    {
        internal static readonly string[] Names = {
            "grouped", "grouped-no-hints", "uv0-nearest", "surface-nearest", "surface-normal", "shell-similarity"
        };

        internal sealed class Output
        {
            internal Vector2[] uv;
            internal int misses, fallbackVertices;
            internal GroupedShellTransfer.TransferResult result;
        }

        internal static async Task<Output> Run(string method, TransferBenchmark.Input input, TransferMatchTrace trace = null)
        {
            if (method == "grouped" || method == "grouped-no-hints") {
                bool hints = method == "grouped";
                var result = await GroupedShellTransfer.TransferAsyncWithDiagnostics(input.target, input.source,
                    hints ? input.overlapHints : null, hints ? input.matchHints : null, input.atlasWidth, input.atlasHeight, trace);
                return new Output { uv = result.uv2, misses = result.verticesTotal - result.verticesTransferred, result = result };
            }
            if (method == "uv0-nearest") return UvNearest(input);
            if (method == "surface-nearest" || method == "surface-normal") return SurfaceNearest(input, method == "surface-normal");
            if (method == "shell-similarity") return ShellSimilarity(input);
            throw new InvalidDataException("Unknown transfer benchmark method: " + method);
        }

        static Output UvNearest(TransferBenchmark.Input input)
        {
            var triangles = input.source.triangles;
            var uv0 = input.source.uv; var uv2 = input.source.uv2; var queries = input.target.uv;
            int count = triangles.Length / 3;
            var a = new Vector2[count]; var b = new Vector2[count]; var c = new Vector2[count];
            for (int face = 0; face < count; ++face) {
                a[face] = uv0[triangles[face * 3]]; b[face] = uv0[triangles[face * 3 + 1]]; c[face] = uv0[triangles[face * 3 + 2]];
            }
            var bvh = new TriangleBvh2D(a, b, c);
            var output = new Output { uv = new Vector2[queries.Length] };
            for (int vertex = 0; vertex < queries.Length; ++vertex) {
                CheckCancel(vertex);
                var hit = bvh.FindNearest(queries[vertex]);
                if (hit.faceIndex < 0) { ++output.misses; continue; }
                output.uv[vertex] = Interpolate(uv2, triangles, hit.faceIndex, new Vector3(hit.u, hit.v, hit.w));
            }
            return output;
        }

        static Output SurfaceNearest(TransferBenchmark.Input input, bool filterNormals)
        {
            // Captures contain the exact local-coordinate inputs seen by production transfer.
            var positions = input.source.vertices; var triangles = input.source.triangles; var uv = input.source.uv2;
            var queries = input.target.vertices; var normals = input.target.normals;
            var faceNormals = new Vector3[triangles.Length / 3];
            for (int face = 0; face < faceNormals.Length; ++face) {
                var a = positions[triangles[face * 3]]; var b = positions[triangles[face * 3 + 1]]; var c = positions[triangles[face * 3 + 2]];
                faceNormals[face] = Vector3.Cross(b - a, c - a).normalized;
            }
            var bvh = new TriangleBvh(positions, triangles);
            var output = new Output { uv = new Vector2[queries.Length] };
            for (int vertex = 0; vertex < queries.Length; ++vertex) {
                CheckCancel(vertex);
                bool hasNormal = normals.Length == queries.Length && normals[vertex].sqrMagnitude > 1e-12f;
                var hit = filterNormals && hasNormal
                    ? bvh.FindNearestNormalFiltered(queries[vertex], normals[vertex].normalized, faceNormals, .25f)
                    : bvh.FindNearest(queries[vertex]);
                if (filterNormals && (!hasNormal || hit.triangleIndex < 0)) {
                    hit = bvh.FindNearest(queries[vertex]); ++output.fallbackVertices;
                }
                if (hit.triangleIndex < 0) { ++output.misses; continue; }
                output.uv[vertex] = Interpolate(uv, triangles, hit.triangleIndex, hit.barycentric);
            }
            return output;
        }

        static Output ShellSimilarity(TransferBenchmark.Input input)
        {
            var source = GroupedShellTransfer.AnalyzeSource(input.source);
            var uv0 = input.source.uv; var uv2 = input.source.uv2;
            var positions = input.target.vertices; var targetUv = input.target.uv;
            var shells = UvShellExtractor.Extract(targetUv, input.target.triangles);
            var output = new Output { uv = new Vector2[positions.Length] };
            var centers = new Vector3[shells.Count];
            for (int s = 0; s < shells.Count; ++s) {
                foreach (int vertex in shells[s].vertexIndices) centers[s] += positions[vertex];
                centers[s] /= Math.Max(1, shells[s].vertexIndices.Count);
            }
            foreach (var shell in shells) {
                CheckCancel(0);
                int nearest = -1; float distance = float.MaxValue;
                for (int s = 0; s < source.Length; ++s) {
                    float candidate = (source[s].worldCentroid - centers[shell.shellId]).sqrMagnitude;
                    if (candidate < distance) { distance = candidate; nearest = s; }
                }
                if (nearest < 0) { output.misses += shell.vertexIndices.Count; continue; }
                var indices = source[nearest].vertexIndices;
                var direct = GroupedShellTransfer.ComputeSimilarityTransform(uv0, uv2, indices, false);
                var mirrored = GroupedShellTransfer.ComputeSimilarityTransform(uv0, uv2, indices, true);
                var transform = mirrored.valid && (!direct.valid || mirrored.residual < direct.residual) ? mirrored : direct;
                if (!transform.valid) { output.misses += shell.vertexIndices.Count; continue; }
                foreach (int vertex in shell.vertexIndices) output.uv[vertex] = transform.Apply(targetUv[vertex]);
            }
            return output;
        }

        static Vector2 Interpolate(Vector2[] uv, int[] triangles, int face, Vector3 weights)
            => uv[triangles[face * 3]] * weights.x + uv[triangles[face * 3 + 1]] * weights.y + uv[triangles[face * 3 + 2]] * weights.z;

        static void CheckCancel(int index)
        {
            if ((index & 255) == 0 && UvProgress.CancelRequested) throw new OperationCanceledException();
        }
    }
}
