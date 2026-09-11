using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class RemeshBaker
    {
        internal sealed class Maps
        {
            public Color32[] color, normal, metal, ao;
            public Color[] emission;
            public int size, misses, covered;
        }

        // Thread-safe: no UnityEngine.Object access in this method or the BVH.
        public static Maps Bake(RemeshSource source, RemeshNative.Geometry target, Vector4[] tangents,
            RemeshSettings settings, CancellationToken token)
        {
            int size = settings.textureResolution;
            int count = checked(size * size);
            var result = new Maps { size = size, color = new Color32[count], normal = new Color32[count],
                metal = new Color32[count], ao = new Color32[count], emission = new Color[count] };
            var owners = new int[count];
            for (int i = 0; i < count; ++i) owners[i] = -1;
            for (int face = 0; face < target.indices.Length / 3; ++face) {
                token.ThrowIfCancellationRequested();
                int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
                Vector2 ua = target.uv[a], ub = target.uv[b], uc = target.uv[c];
                int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(ua.x, Mathf.Min(ub.x, uc.x)) * size), 0, size - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(ua.x, Mathf.Max(ub.x, uc.x)) * size), 0, size - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(ua.y, Mathf.Min(ub.y, uc.y)) * size), 0, size - 1);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(ua.y, Mathf.Max(ub.y, uc.y)) * size), 0, size - 1);
                for (int y = y0; y <= y1; ++y)
                    for (int x = x0; x <= x1; ++x)
                        if (Barycentric(new Vector2((x + 0.5f) / size, (y + 0.5f) / size), ua, ub, uc, out var bary) &&
                            bary.x >= -1e-6f && bary.y >= -1e-6f && bary.z >= -1e-6f)
                            owners[y * size + x] = face;
            }
            for (int i = 0; i < count; ++i) if (owners[i] >= 0) ++result.covered;
            token.ThrowIfCancellationRequested();
            if (result.covered == 0) throw new InvalidOperationException("UV atlas covers no texels. Increase texture resolution.");
            var bvh = new TriangleBvh(source.positions, source.indices);
            token.ThrowIfCancellationRequested();
            float distance = source.diagonal * settings.projectionDistance;
            Parallel.For(0, size, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, y => {
                for (int x = 0; x < size; ++x) {
                    if ((x & 63) == 0) token.ThrowIfCancellationRequested();
                    int pixel = y * size + x, face = owners[pixel];
                    if (face < 0) continue;
                    int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
                    Barycentric(new Vector2((x + 0.5f) / size, (y + 0.5f) / size), target.uv[a], target.uv[b], target.uv[c], out var w);
                    Vector3 p = target.positions[a] * w.x + target.positions[b] * w.y + target.positions[c] * w.z;
                    Vector3 n = (target.normals[a] * w.x + target.normals[b] * w.y + target.normals[c] * w.z).normalized;
                    Vector4 tangent = tangents[a] * w.x + tangents[b] * w.y + tangents[c] * w.z;
                    var hit = bvh.Raycast(p + n * distance, -n, distance * 2);
                    int sourceFace = hit.triangleIndex;
                    Vector3 sw = hit.barycentric;
                    if (sourceFace < 0) {
                        var nearest = bvh.FindNearest(p, distance);
                        sourceFace = nearest.triangleIndex; sw = nearest.barycentric;
                    }
                    if (sourceFace < 0) {
                        Interlocked.Increment(ref result.misses);
                        result.color[pixel] = new Color32(255, 0, 255, 255);
                        result.normal[pixel] = new Color32(128, 128, 255, 255);
                        result.ao[pixel] = new Color32(255, 255, 255, 255);
                        continue;
                    }
                    Evaluate(source, sourceFace, sw, n, tangent, out var color, out var normal,
                        out var metal, out var ao, out var emission);
                    result.color[pixel] = color; result.normal[pixel] = normal;
                    result.metal[pixel] = metal; result.ao[pixel] = ao; result.emission[pixel] = emission;
                }
            });
            Dilate(result, owners, settings.padding, token);
            return result;
        }

        internal static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 weights)
        {
            Vector2 ab = b - a, ac = c - a, ap = p - a;
            float det = ab.x * ac.y - ab.y * ac.x;
            if (Mathf.Abs(det) < 1e-15f) { weights = Vector3.zero; return false; }
            float v = (ap.x * ac.y - ap.y * ac.x) / det;
            float w = (ab.x * ap.y - ab.y * ap.x) / det;
            weights = new Vector3(1 - v - w, v, w); return true;
        }

        internal static void Evaluate(RemeshSource source, int face, Vector3 w, Vector3 targetNormal, Vector4 targetTangent,
            out Color color, out Color normal, out Color metal, out Color ao, out Color emission)
        {
            int a = source.indices[face * 3], b = source.indices[face * 3 + 1], c = source.indices[face * 3 + 2];
            Vector2 uv = source.uv[a] * w.x + source.uv[b] * w.y + source.uv[c] * w.z;
            var surface = source.materials[source.faceMaterials[face]];
            Color albedo = surface.color.Sample(uv, Color.white);
            color = (albedo * surface.tint).gamma; color.a = 1;
            var n = (source.normals[a] * w.x + source.normals[b] * w.y + source.normals[c] * w.z).normalized;
            var t = source.tangents[a] * w.x + source.tangents[b] * w.y + source.tangents[c] * w.z;
            if (surface.normal.image != null) {
                var sample = surface.normal.Sample(uv, new Color(0.5f, 0.5f, 1));
                float nx = (sample.r * 2 - 1) * surface.normalScale, ny = (sample.g * 2 - 1) * surface.normalScale;
                Basis(n, t, out var st, out var sb);
                n = (st * nx + sb * ny + n * Mathf.Sqrt(Mathf.Max(0, 1 - nx * nx - ny * ny))).normalized;
            }
            Basis(targetNormal, targetTangent, out var tt, out var tb);
            normal = new Color(Vector3.Dot(n, tt) * 0.5f + 0.5f, Vector3.Dot(n, tb) * 0.5f + 0.5f,
                Vector3.Dot(n, targetNormal) * 0.5f + 0.5f, 1);
            var mr = surface.metal.Sample(uv, Color.white);
            float smooth = surface.smoothness * (surface.smoothnessFromAlbedo ? albedo.a : mr.a);
            metal = new Color(surface.metal.image != null ? mr.r : surface.metallic, 0, 0, smooth);
            float occlusion = Mathf.Lerp(1, surface.ao.Sample(uv, Color.white).g, surface.aoStrength);
            ao = new Color(occlusion, occlusion, occlusion, 1);
            emission = surface.emission.Sample(uv, Color.white) * surface.emissionTint;
            emission.a = 1;
        }

        static void Basis(Vector3 n, Vector4 tangent, out Vector3 t, out Vector3 b)
        {
            t = new Vector3(tangent.x, tangent.y, tangent.z);
            t = (t - n * Vector3.Dot(t, n)).normalized;
            if (t.sqrMagnitude < 1e-10f) t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            b = Vector3.Cross(n, t) * (tangent.w < 0 ? -1 : 1);
        }

        static void Dilate(Maps maps, int[] owners, int padding, CancellationToken token)
        {
            int size = maps.size, count = owners.Length;
            var nearest = new int[count]; var next = new int[count];
            for (int i = 0; i < count; ++i) nearest[i] = owners[i] < 0 ? -1 : i;
            for (int iteration = 0; iteration < padding; ++iteration) {
                token.ThrowIfCancellationRequested();
                Array.Copy(nearest, next, count);
                for (int y = 0; y < size; ++y) {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < size; ++x) {
                        int i = y * size + x;
                        if (nearest[i] >= 0) continue;
                        for (int dy = -1; dy <= 1 && next[i] < 0; ++dy)
                            for (int dx = -1; dx <= 1; ++dx) {
                                int xx = x + dx, yy = y + dy;
                                if (xx >= 0 && yy >= 0 && xx < size && yy < size && nearest[yy * size + xx] >= 0) {
                                    next[i] = nearest[yy * size + xx]; break;
                                }
                            }
                    }
                }
                var swap = nearest; nearest = next; next = swap;
            }
            for (int i = 0; i < count; ++i) {
                int from = nearest[i];
                if (owners[i] >= 0 || from < 0) continue;
                maps.color[i] = maps.color[from]; maps.normal[i] = maps.normal[from];
                maps.metal[i] = maps.metal[from]; maps.ao[i] = maps.ao[from]; maps.emission[i] = maps.emission[from];
            }
        }
    }
}
