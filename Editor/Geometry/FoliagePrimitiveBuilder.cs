using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal struct FoliagePrimitive { internal Vector3 center; internal float radius; }

    internal static class FoliagePrimitiveBuilder
    {
        internal const int MaximumPreviewPrimitives = 4096;
        internal const int MaximumTriangles = FoliageNormalsSettings.HardProxyTriangleCap;
        internal const long MaximumBytes = 128L * 1024 * 1024;

        internal static FoliagePrimitive[] FromSamples(Vector3[] samples, float radius, float variation, int seed, CancellationToken token)
        {
            if (!FoliageTargetSnapshot.Finite(radius) || radius <= 0 ||
                !FoliageTargetSnapshot.Finite(variation) || variation < 0 || variation > .95f)
                throw new ArgumentException("Sphere radius must be positive and radius variation must be 0–0.95.");
            token.ThrowIfCancellationRequested();
            var primitives = new FoliagePrimitive[samples.Length];
            for (int i = 0; i < primitives.Length; ++i) {
                token.ThrowIfCancellationRequested();
                float scale = 1 + variation * (2 * RadiusNoise(seed, i) - 1);
                primitives[i] = new FoliagePrimitive { center = samples[i], radius = radius * scale };
            }
            return primitives;
        }

        // Separate index-based noise does not consume the surface sampler's stream:
        // changing radius variation preserves sample positions and island minimums.
        static float RadiusNoise(int seed, int index)
        {
            unchecked {
                uint value = (uint)seed ^ ((uint)index + 1) * 0x9e3779b9u;
                value ^= value >> 16; value *= 0x7feb352du;
                value ^= value >> 15; value *= 0x846ca68bu;
                value ^= value >> 16;
                return (value >> 8) * (1f / 16777216f);
            }
        }

        internal static RemeshNative.IndexedMesh BuildPreview(FoliagePrimitive[] primitives, CancellationToken token)
        {
            int count = Math.Min(MaximumPreviewPrimitives, primitives.Length);
            var subset = new FoliagePrimitive[count];
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                subset[i] = primitives[(int)((long)i * primitives.Length / count)];
            }
            return Build(subset, 1, token);
        }

        internal static RemeshNative.IndexedMesh Build(FoliagePrimitive[] primitives, int subdivision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (subdivision < 0 || subdivision > 1) throw new ArgumentException("Primitive subdivision must be 0 or 1.");
            var template = Template(subdivision);
            long vertices = (long)template.positions.Length * primitives.Length, indices = (long)template.indices.Length * primitives.Length;
            long memory = vertices * 12 + indices * 4;
            if (primitives.Length == 0 || primitives.Length > FoliageNormalsSettings.HardPrimitiveCap || indices / 3 > MaximumTriangles || memory > MaximumBytes)
                throw new InvalidOperationException($"Primitive geometry budget exceeded ({primitives.Length:N0} spheres, {vertices:N0} vertices, {indices / 3:N0} triangles, {memory / 1048576f:F1} MiB). Increase spacing or reduce subdivision.");
            var mesh = new RemeshNative.IndexedMesh { positions = new Vector3[(int)vertices], indices = new int[(int)indices] };
            for (int i = 0; i < primitives.Length; i++) {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var sphere = primitives[i];
                if (!FoliageTargetSnapshot.Finite(sphere.center) || !FoliageTargetSnapshot.Finite(sphere.radius) || sphere.radius <= 0) throw new ArgumentException("Invalid sphere.");
                int first = i * template.positions.Length;
                for (int v = 0; v < template.positions.Length; v++) mesh.positions[first + v] = sphere.center + template.positions[v] * sphere.radius;
                for (int k = 0; k < template.indices.Length; k++) mesh.indices[i * template.indices.Length + k] = first + template.indices[k];
            }
            return mesh;
        }

        internal static RemeshNative.IndexedMesh Template(int subdivision)
        {
            float a = (1 + Mathf.Sqrt(5)) / 2;
            var p = new List<Vector3> { new Vector3(-1,a,0),new Vector3(1,a,0),new Vector3(-1,-a,0),new Vector3(1,-a,0),
                new Vector3(0,-1,a),new Vector3(0,1,a),new Vector3(0,-1,-a),new Vector3(0,1,-a),
                new Vector3(a,0,-1),new Vector3(a,0,1),new Vector3(-a,0,-1),new Vector3(-a,0,1) };
            for (int i = 0; i < p.Count; i++) p[i] = MeshGeometry.UnitDirection(p[i]);
            int[] t = { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
            if (subdivision == 1) {
                var midpoints = new Dictionary<(int, int), int>(); var next = new List<int>();
                int Mid(int x, int y) {
                    var key = x < y ? (x, y) : (y, x);
                    if (!midpoints.TryGetValue(key, out int v)) { v = p.Count; midpoints.Add(key, v); p.Add(MeshGeometry.UnitDirection(p[x] + p[y])); }
                    return v;
                }
                for (int i = 0; i < t.Length; i += 3) {
                    int x = t[i], y = t[i + 1], z = t[i + 2], xy = Mid(x, y), yz = Mid(y, z), zx = Mid(z, x);
                    next.AddRange(new[] { x,xy,zx, y,yz,xy, z,zx,yz, xy,yz,zx });
                }
                t = next.ToArray();
            }
            return new RemeshNative.IndexedMesh { positions = p.ToArray(), indices = t };
        }
    }
}
