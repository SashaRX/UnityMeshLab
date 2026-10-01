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
            public Color[] vertexColors; // per result vertex, null unless a transfer was requested
            public int size, misses, covered;
            // Proxy shapes: covered texels whose inward ray met no source geometry (the
            // empty part of a box face). Filled from their nearest hit and written with
            // alpha 0 in the base color, not counted as misses.
            public int empty;
            // Bake health counters surfaced through the RemeshDiag log category.
            public int rayFallbacks, weldedPositions, splitCopies, oneSidedNormals, loudTexels, zeroNormals;
            public float maxOneSidedDeg, meanTiltDeg, maxTiltDeg;
            public bool facingFilter;
            public bool beauty;
        }

        // Thread-safe: no UnityEngine.Object access in this method or the BVH.
        // beauty (optional) folds the scene lighting into the transferred albedo.
        public static Maps Bake(RemeshSource source, RemeshNative.Geometry target, Vector4[] tangents,
            RemeshSettings settings, CancellationToken token, RemeshBeauty beauty = null)
        {
            int size = settings.textureResolution;
            int count = checked(size * size);
            var result = new Maps { size = size, color = new Color32[count], normal = new Color32[count],
                metal = new Color32[count], ao = new Color32[count], emission = new Color[count] };
            int grid = Mathf.RoundToInt(Mathf.Sqrt(settings.bakeSamples));
            var offsets = SampleOffsets(grid);
            var owners = Rasterize(target, size, offsets, token);
            for (int i = 0; i < count; ++i) if (owners[i] >= 0) ++result.covered;
            token.ThrowIfCancellationRequested();
            if (result.covered == 0) throw new InvalidOperationException("UV atlas covers no texels. Increase texture resolution.");
            var bvh = new TriangleBvh(source.positions, source.indices);
            token.ThrowIfCancellationRequested();
            result.beauty = beauty != null;
            if (beauty != null) beauty.BindShadows(bvh);
            float distance = source.diagonal * settings.projectionDistance;
            // Proxy shapes (boxes, hull) are far from the surface they stand for, so
            // their texels look INWARD along the face normal through the whole proxy —
            // an orthographic snapshot of the model from that side — instead of the
            // short two-sided cage ray; nothing behind a texel means "empty", not a miss.
            bool proxy = settings.sourceShape != RemeshShape.LOD0;
            Vector3[] faceDirs = null; float depth = 0;
            if (proxy) {
                faceDirs = FaceNormals(target.positions, target.indices);
                Vector3 mn = target.positions[0], mx = target.positions[0];
                foreach (var pv in target.positions) { mn = Vector3.Min(mn, pv); mx = Vector3.Max(mx, pv); }
                depth = (mx - mn).magnitude * 1.01f + source.diagonal * 1e-4f;
            }
            // Projection rays follow a smooth welded "cage" direction, not the vertex
            // normal: island hard-edge modes leave chart-border normals one-sided,
            // and casting along them samples a displaced source point, which bakes
            // artifact bands around every island. The tangent frame stays the vertex
            // normal the result mesh shades with, so encode and decode still match.
            var cage = BuildCageNormals(target, result);
            // Front-face filter for the projection rays: a plain closest-hit raycast
            // travels 2×distance THROUGH the target and can pierce a thin wall, sampling
            // the far side's texture (periodic mirrored/garbled patches). The filter only
            // accepts source triangles whose normal faces the ray origin. Source winding
            // is not guaranteed to be outward, so a probe orients the normals first;
            // without a clear majority the filter stays off and the bake behaves exactly
            // as before.
            var faceNormals = FaceNormals(source.positions, source.indices);
            int winding = ProbeWinding(bvh, source.positions, faceNormals);
            bool facingFilter = winding != 0;
            if (winding < 0)
                for (int f = 0; f < faceNormals.Length; ++f) faceNormals[f] = -faceNormals[f];
            result.facingFilter = facingFilter;
            Vector3[] facing = facingFilter ? faceNormals : null;
            int misses = 0, rayFallbacks = 0, empties = 0;
            var emptyTexels = proxy ? new bool[count] : null;
            Parallel.For(0, size, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, y => {
                var candidates = new int[9];
                for (int x = 0; x < size; ++x) {
                    if ((x & 63) == 0) token.ThrowIfCancellationRequested();
                    int pixel = y * size + x;
                    if (owners[pixel] < 0) continue;
                    // A sample belongs to whichever nearby face contains it, so texels on
                    // chart edges average only the surfaces that actually cover them.
                    int candidateCount = 0;
                    for (int dy = -1; dy <= 1; ++dy)
                        for (int dx = -1; dx <= 1; ++dx) {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                            int f = owners[yy * size + xx];
                            if (f >= 0 && Array.IndexOf(candidates, f, 0, candidateCount) < 0) candidates[candidateCount++] = f;
                        }
                    Color color = default, metal = default, ao = default, emission = default;
                    Vector3 normal = Vector3.zero;
                    int hits = 0;
                    foreach (var offset in offsets) {
                        var uv = new Vector2((x + offset.x) / size, (y + offset.y) / size);
                        int face = -1; Vector3 w = default;
                        for (int c = 0; c < candidateCount && face < 0; ++c)
                            if (Inside(target, candidates[c], uv, out w)) face = candidates[c];
                        if (face < 0) continue;
                    if (!Project(source, target, tangents, cage, bvh, facing, beauty, face, w, distance,
                            proxy, faceDirs, depth, settings.vertexColorTint,
                            out var sc, out var sn, out var sm, out var sa, out var se, out var rayFallback)) continue;
                        if (rayFallback) Interlocked.Increment(ref rayFallbacks);
                        color += sc.linear; metal += sm; ao += sa; emission += se;
                        normal += new Vector3(sn.r * 2 - 1, sn.g * 2 - 1, sn.b * 2 - 1);
                        ++hits;
                    }
                    if (hits == 0) {
                        if (proxy) { emptyTexels[pixel] = true; Interlocked.Increment(ref empties); continue; }
                        Interlocked.Increment(ref misses);
                        result.color[pixel] = new Color32(255, 0, 255, 255);
                        result.normal[pixel] = new Color32(128, 128, 255, 255);
                        result.ao[pixel] = new Color32(255, 255, 255, 255);
                        continue;
                    }
                    float inv = 1f / hits;
                    Color averaged = (color * inv).gamma; averaged.a = 1;
                    normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.forward;
                    result.color[pixel] = averaged;
                    result.normal[pixel] = new Color(normal.x * 0.5f + 0.5f, normal.y * 0.5f + 0.5f, normal.z * 0.5f + 0.5f, 1);
                    result.metal[pixel] = metal * inv; result.ao[pixel] = ao * inv;
                    var e = emission * inv; e.a = 1; result.emission[pixel] = e;
                }
            });
            result.misses = misses;
            result.rayFallbacks = rayFallbacks;
            result.empty = empties;
            if (proxy && empties > 0) FillEmpty(result, owners, emptyTexels, token);
            // Normal-map tilt health: how far encoded normals lean away from the
            // tangent plane's up axis. Loud, strongly-tilted maps on smooth-ish
            // sources are the visual signature of displaced projection samples.
            double tiltSum = 0; float tiltMax = 0; int loud = 0, tiltN = 0;
            for (int i = 0; i < count; ++i) {
                if (owners[i] < 0) continue;
                float z = result.normal[i].b * (1f / 127.5f) - 1f;
                float tilt = Mathf.Acos(Mathf.Clamp(z, -1f, 1f)) * Mathf.Rad2Deg;
                tiltSum += tilt; ++tiltN;
                if (tilt > tiltMax) tiltMax = tilt;
                if (tilt > 45f) ++loud;
            }
            result.meanTiltDeg = tiltN > 0 ? (float)(tiltSum / tiltN) : 0;
            result.maxTiltDeg = tiltMax;
            result.loudTexels = loud;
            Dilate(result, owners, settings.padding, token);
            if (settings.transferVertexColor || settings.transferVertexAlpha)
                result.vertexColors = TransferVertexColors(source, target, bvh, settings, token);
            return result;
        }

        // Stratified sample positions inside one texel, in texel units.
        internal static Vector2[] SampleOffsets(int grid)
        {
            var offsets = new Vector2[grid * grid];
            for (int y = 0; y < grid; ++y)
                for (int x = 0; x < grid; ++x)
                    offsets[y * grid + x] = new Vector2((x + 0.5f) / grid, (y + 0.5f) / grid);
            return offsets;
        }

        // A texel is owned by the face under its centre. With multisampling, texels whose
        // centre misses every face but that some sample hits are owned too (conservative
        // coverage), so thin charts and chart edges are not lost.
        static int[] Rasterize(RemeshNative.Geometry target, int size, Vector2[] offsets, CancellationToken token)
        {
            var owners = new int[size * size];
            var centred = new bool[owners.Length];
            for (int i = 0; i < owners.Length; ++i) owners[i] = -1;
            for (int face = 0; face < target.indices.Length / 3; ++face) {
                token.ThrowIfCancellationRequested();
                int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
                Vector2 ua = target.uv[a], ub = target.uv[b], uc = target.uv[c];
                int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(ua.x, Mathf.Min(ub.x, uc.x)) * size), 0, size - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(ua.x, Mathf.Max(ub.x, uc.x)) * size), 0, size - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(ua.y, Mathf.Min(ub.y, uc.y)) * size), 0, size - 1);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(ua.y, Mathf.Max(ub.y, uc.y)) * size), 0, size - 1);
                for (int y = y0; y <= y1; ++y)
                    for (int x = x0; x <= x1; ++x) {
                        int pixel = y * size + x;
                        if (Inside(target, face, new Vector2((x + 0.5f) / size, (y + 0.5f) / size), out _)) {
                            owners[pixel] = face; centred[pixel] = true;
                        }
                        else if (offsets.Length > 1 && !centred[pixel] && owners[pixel] < 0)
                            foreach (var offset in offsets)
                                if (Inside(target, face, new Vector2((x + offset.x) / size, (y + offset.y) / size), out _)) {
                                    owners[pixel] = face; break;
                                }
                    }
            }
            return owners;
        }

        static bool Inside(RemeshNative.Geometry target, int face, Vector2 uv, out Vector3 w)
        {
            int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
            return Barycentric(uv, target.uv[a], target.uv[b], target.uv[c], out w) &&
                w.x >= -1e-6f && w.y >= -1e-6f && w.z >= -1e-6f;
        }

        // Trace from the destination surface point back to the source and evaluate its
        // material there. False when neither the ray nor the bounded nearest query hits.
        // The ray direction comes from the welded cage normal; the tangent frame keeps
        // the (possibly hard) vertex normal so encode matches how the mesh shades.
        // facing (when non-null) carries the orientation-consensus source face normals
        // that keep the ray and the fallback on the side of the surface facing the texel.
        // proxy: the ray starts just outside the proxy face and travels inward along the
        // face normal through the whole proxy (depth), first hit wins, no fallback.
        static bool Project(RemeshSource source, RemeshNative.Geometry target, Vector4[] tangents, Vector3[] cage,
            TriangleBvh bvh, Vector3[] facing, RemeshBeauty beauty, int face, Vector3 w, float distance,
            bool proxy, Vector3[] faceDirs, float depth, bool vertexTint,
            out Color color, out Color normal, out Color metal, out Color ao, out Color emission, out bool rayFallback)
        {
            int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
            Vector3 p = target.positions[a] * w.x + target.positions[b] * w.y + target.positions[c] * w.z;
            Vector3 n = (target.normals[a] * w.x + target.normals[b] * w.y + target.normals[c] * w.z).normalized;
            Vector4 tangent = tangents[a] * w.x + tangents[b] * w.y + tangents[c] * w.z;
            int sourceFace; Vector3 sw;
            rayFallback = false;
            if (proxy) {
                Vector3 dir = faceDirs[face];
                float eps = depth * 1e-4f;
                var hit = facing != null
                    ? bvh.RaycastFacingFiltered(p + dir * eps, -dir, depth, facing)
                    : bvh.Raycast(p + dir * eps, -dir, depth);
                sourceFace = hit.triangleIndex; sw = hit.barycentric;
            }
            else {
                Vector3 rayN = (cage[a] * w.x + cage[b] * w.y + cage[c] * w.z).normalized;
                var hit = facing != null
                    ? bvh.RaycastFacingFiltered(p + rayN * distance, -rayN, distance * 2, facing)
                    : bvh.Raycast(p + rayN * distance, -rayN, distance * 2);
                sourceFace = hit.triangleIndex; sw = hit.barycentric;
                if (sourceFace < 0) {
                    // Both fallbacks stay bounded by the projection distance: an unbounded
                    // filtered query would smear an unrelated far part across a gap.
                    var nearest = facing != null
                        ? bvh.FindNearestNormalFiltered(p, rayN, facing, 0f, distance)
                        : bvh.FindNearest(p, distance);
                    sourceFace = nearest.triangleIndex; sw = nearest.barycentric;
                    rayFallback = true;
                }
            }
            if (sourceFace < 0) {
                color = normal = metal = ao = emission = default;
                return false;
            }
            Evaluate(source, sourceFace, sw, n, tangent, vertexTint, out color, out normal, out metal, out ao, out emission);
            if (beauty != null) color = BeautyLight(beauty, source, sourceFace, sw, color, metal, emission).gamma;
            return true;
        }

        // Folds the captured scene lighting into the transferred albedo (all linear).
        // Unity lightmaps store incoming irradiance (GI + baked lights), which the Lit
        // shader multiplies by the material's diffuse response at runtime — so a
        // lightmapped face shades albedo × (lightmap + realtime/mixed direct) + emission,
        // with ambient left out (the lightmap already carries it); an unlightmapped face
        // gets albedo × (direct + ambient) + emission. Probe specular rides on both.
        // Lighting reacts to the source's normal map, exactly as the game shades: the
        // bump-perturbed normal drives the directional-lightmap half-Lambert, the
        // realtime N·L terms, the ambient probe and the specular view dot.
        static Color BeautyLight(RemeshBeauty beauty, RemeshSource source, int face, Vector3 w,
            Color albedo, Color metal, Color emission)
        {
            int a = source.indices[face * 3], b = source.indices[face * 3 + 1], c = source.indices[face * 3 + 2];
            Vector3 p = source.positions[a] * w.x + source.positions[b] * w.y + source.positions[c] * w.z;
            Vector3 n = (source.normals[a] * w.x + source.normals[b] * w.y + source.normals[c] * w.z).normalized;
            Vector4 tangent = source.tangents[a] * w.x + source.tangents[b] * w.y + source.tangents[c] * w.z;
            Vector2 uv = source.uv[a] * w.x + source.uv[b] * w.y + source.uv[c] * w.z;
            var surface = source.materials[source.faceMaterials[face]];
            if (surface.normal.image != null) {
                var sample = surface.normal.Sample(uv, new Color(0.5f, 0.5f, 1));
                float nx = (sample.r * 2 - 1) * surface.normalScale, ny = (sample.g * 2 - 1) * surface.normalScale;
                Basis(n, tangent, out var st, out var sb);
                n = (st * nx + sb * ny + n * Mathf.Sqrt(Mathf.Max(0, 1 - nx * nx - ny * ny))).normalized;
            }
            Color albedoLinear = albedo.linear;
            int lightmapId = source.faceLightmaps != null && face < source.faceLightmaps.Length ? source.faceLightmaps[face] : -1;
            // The face's renderer layer gates lights by their culling mask.
            int layer = source.vertexRenderer != null && source.rendererLayer != null ? source.rendererLayer[source.vertexRenderer[a]] : 0;
            Color lit;
            if (lightmapId >= 0 && source.lightmaps != null && lightmapId < source.lightmaps.Length) {
                Vector2 uv2 = source.uv2[a] * w.x + source.uv2[b] * w.y + source.uv2[c] * w.z;
                lit = albedoLinear * (beauty.SampleLightmap(source.lightmaps[lightmapId], uv2, n) + beauty.Direct(p, n, layer)) + emission;
            }
            else {
                lit = albedoLinear * (beauty.Direct(p, n, layer) + beauty.Ambient(n)) + emission;
            }
            lit += beauty.Specular(p, n, albedoLinear, metal.r, metal.a);
            return lit;
        }

        // Per-vertex raycast directions from area-weighted face normals welded across
        // coincident vertices: UV chart borders (and crease edges) split vertices, and
        // each copy's own normal is one-sided there, so rays cast along it land on a
        // displaced source point. Welding by exact position — xatlas copies bit-identical
        // coordinates — averages both sides back into a smooth cage direction, and a
        // Laplacian pass over the WELDED connectivity smooths the sliver noise the
        // adaptive decimation leaves in those directions (the "fully smooth cage" every
        // baker prescribes). The pass runs on the welded mesh precisely so it flows
        // through chart borders and hard edges instead of re-splitting them.
        internal static Vector3[] BuildCageNormals(RemeshNative.Geometry target, Maps diag)
        {
            const float cageSmoothing = 2f;
            var slots = new int[target.positions.Length];
            var map = new System.Collections.Generic.Dictionary<(int, int, int), int>(target.positions.Length);
            for (int i = 0; i < target.positions.Length; ++i) {
                var p = target.positions[i];
                var key = (BitConverter.SingleToInt32Bits(p.x), BitConverter.SingleToInt32Bits(p.y), BitConverter.SingleToInt32Bits(p.z));
                if (!map.TryGetValue(key, out int slot)) { slot = map.Count; map[key] = slot; }
                slots[i] = slot;
            }
            var welded = new Vector3[map.Count];
            var weldedIndices = new int[target.indices.Length];
            for (int i = 0; i < target.indices.Length; i += 3) {
                int a = target.indices[i], b = target.indices[i + 1], c = target.indices[i + 2];
                Vector3 n = Vector3.Cross(target.positions[b] - target.positions[a], target.positions[c] - target.positions[a]);
                welded[slots[a]] += n; welded[slots[b]] += n; welded[slots[c]] += n;
                weldedIndices[i] = slots[a]; weldedIndices[i + 1] = slots[b]; weldedIndices[i + 2] = slots[c];
            }
            for (int i = 0; i < welded.Length; ++i)
                if (welded[i].sqrMagnitude > 1e-30f) welded[i] = welded[i].normalized;
            var weldedMesh = new RemeshNative.Geometry { normals = welded, indices = weldedIndices };
            RemeshNative.SmoothNormals(weldedMesh, cageSmoothing);
            var cage = new Vector3[target.positions.Length];
            float maxDev = 0; int oneSided = 0, zeroNormalVerts = 0;
            for (int i = 0; i < cage.Length; ++i) {
                bool zeroVertexNormal = target.normals[i].sqrMagnitude < 1e-12f;
                if (zeroVertexNormal) ++zeroNormalVerts;
                // Fall back to the (possibly zero) vertex normal only when the welded
                // sum is degenerate; zero cage entries zero the projection ray.
                cage[i] = welded[slots[i]].sqrMagnitude > 1e-30f ? welded[slots[i]].normalized : target.normals[i];
                float dev = Mathf.Acos(Mathf.Clamp(Vector3.Dot(cage[i], target.normals[i]), -1f, 1f)) * Mathf.Rad2Deg;
                if (dev > maxDev) maxDev = dev;
                if (dev > 30f) ++oneSided;
            }
            if (diag != null) {
                diag.weldedPositions = map.Count;
                diag.splitCopies = target.positions.Length - map.Count;
                diag.oneSidedNormals = oneSided;
                diag.maxOneSidedDeg = maxDev;
                diag.zeroNormals = zeroNormalVerts;
            }
            return cage;
        }

        // Nearest source surface point per result vertex, interpolating its vertex colours.
        internal static Color[] TransferVertexColors(RemeshSource source, RemeshNative.Geometry target, TriangleBvh bvh,
            RemeshSettings settings, CancellationToken token)
        {
            var colors = new Color[target.positions.Length];
            Parallel.For(0, colors.Length, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i => {
                var hit = bvh.FindNearest(target.positions[i]);
                Color c = Color.white;
                if (hit.triangleIndex >= 0) {
                    int f = hit.triangleIndex, a = source.indices[f * 3], b = source.indices[f * 3 + 1], d = source.indices[f * 3 + 2];
                    Vector3 w = hit.barycentric;
                    c = source.colors[a] * w.x + source.colors[b] * w.y + source.colors[d] * w.z;
                }
                colors[i] = new Color(settings.transferVertexColor ? c.r : 1, settings.transferVertexColor ? c.g : 1,
                    settings.transferVertexColor ? c.b : 1, settings.transferVertexAlpha ? c.a : 1);
            });
            return colors;
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
            => Evaluate(source, face, w, targetNormal, targetTangent, false, out color, out normal, out metal, out ao, out emission);

        // vertexTint multiplies the albedo by the interpolated source vertex color (RGB,
        // taken as linear, the way vertex-tinting shaders read it).
        internal static void Evaluate(RemeshSource source, int face, Vector3 w, Vector3 targetNormal, Vector4 targetTangent, bool vertexTint,
            out Color color, out Color normal, out Color metal, out Color ao, out Color emission)
        {
            int a = source.indices[face * 3], b = source.indices[face * 3 + 1], c = source.indices[face * 3 + 2];
            Vector2 uv = source.uv[a] * w.x + source.uv[b] * w.y + source.uv[c] * w.z;
            var surface = source.materials[source.faceMaterials[face]];
            Color albedo = surface.color.Sample(uv, Color.white);
            Color linear = albedo * surface.tint;
            if (vertexTint && source.colors != null) {
                Color vc = source.colors[a] * w.x + source.colors[b] * w.y + source.colors[c] * w.z;
                linear = new Color(linear.r * vc.r, linear.g * vc.g, linear.b * vc.b, linear.a);
            }
            color = linear.gamma; color.a = 1;
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

        /// <summary>
        /// Which way the source winds, judged from OUTSIDE: rays from a sphere around the
        /// source toward its centre always meet an outer surface first, so the sign of
        /// that triangle's winding normal against the ray is the answer — whatever the
        /// target looks like (the old probe compared source normals with the target's
        /// cage, which falls apart once the target is a 100-triangle proxy of a 2000-
        /// triangle block). +1 outward, -1 inverted, 0 when fewer than 8 rays hit or
        /// under 70% agree (open sheets, mixed winding): the caller leaves the filter off.
        /// </summary>
        internal static int ProbeWinding(TriangleBvh bvh, Vector3[] positions, Vector3[] faceNormals)
        {
            if (positions.Length == 0) return 0;
            Vector3 mn = positions[0], mx = positions[0];
            foreach (var p in positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            Vector3 centre = (mn + mx) * 0.5f;
            float radius = Mathf.Max((mx - mn).magnitude * 0.5f, 1e-6f);
            const int probes = 256;
            int outward = 0, inward = 0;
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < probes; ++i) {
                // Fibonacci sphere: evenly spread directions, deterministic.
                float y = 1f - 2f * (i + 0.5f) / probes, r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y)), a = golden * i;
                var dir = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                var hit = bvh.Raycast(centre + dir * (radius * 2f), -dir, radius * 4f);
                if (hit.triangleIndex < 0) continue;
                // The ray travels along -dir; a triangle facing the ray origin has its
                // winding normal pointing back along +dir.
                if (Vector3.Dot(faceNormals[hit.triangleIndex], dir) > 0f) ++outward; else ++inward;
            }
            int total = outward + inward;
            if (total < 8 || Math.Max(outward, inward) * 10 < total * 7) return 0;
            return outward >= inward ? 1 : -1;
        }

        static Vector3[] FaceNormals(Vector3[] positions, int[] indices)
        {
            var normals = new Vector3[indices.Length / 3];
            for (int f = 0; f < normals.Length; ++f) {
                int a = indices[f * 3], b = indices[f * 3 + 1], c = indices[f * 3 + 2];
                normals[f] = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).normalized;
            }
            return normals;
        }

        // Proxy "empty" texels take the maps of their nearest hit texel (jump flooding
        // over the whole atlas, O(n log n), so holes hundreds of texels wide fill
        // without seams) and keep alpha 0 in the base color as the coverage mask.
        static void FillEmpty(Maps maps, int[] owners, bool[] empty, CancellationToken token)
        {
            int size = maps.size, count = owners.Length;
            var seed = new bool[count];
            for (int i = 0; i < count; ++i) seed[i] = owners[i] >= 0 && !empty[i];
            var nearest = NearestSeeds(seed, size, token);
            for (int i = 0; i < count; ++i) {
                if (!empty[i]) continue;
                int from = nearest[i];
                if (from >= 0) {
                    maps.color[i] = maps.color[from]; maps.normal[i] = maps.normal[from];
                    maps.metal[i] = maps.metal[from]; maps.ao[i] = maps.ao[from]; maps.emission[i] = maps.emission[from];
                }
                else { maps.normal[i] = new Color32(128, 128, 255, 255); maps.ao[i] = new Color32(255, 255, 255, 255); }
                var c = maps.color[i]; c.a = 0; maps.color[i] = c;
            }
        }

        // Jump flooding: for every texel, the index of the nearest seed texel (-1 when
        // there is none).
        internal static int[] NearestSeeds(bool[] seed, int size, CancellationToken token)
        {
            int count = size * size;
            var nearest = new int[count]; var next = new int[count];
            for (int i = 0; i < count; ++i) nearest[i] = seed[i] ? i : -1;
            long DistSq(int from, int to) { long dx = from % size - to % size, dy = from / size - to / size; return dx * dx + dy * dy; }
            for (int step = Math.Max(1, size / 2); step >= 1; step /= 2) {
                token.ThrowIfCancellationRequested();
                Array.Copy(nearest, next, count);
                for (int y = 0; y < size; ++y)
                    for (int x = 0; x < size; ++x) {
                        int i = y * size + x;
                        int best = next[i];
                        long bestD = best >= 0 ? DistSq(i, best) : long.MaxValue;
                        for (int dy = -step; dy <= step; dy += step)
                            for (int dx = -step; dx <= step; dx += step) {
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                                int candidate = nearest[yy * size + xx];
                                if (candidate < 0) continue;
                                long d = DistSq(i, candidate);
                                if (d < bestD) { bestD = d; best = candidate; }
                            }
                        next[i] = best;
                    }
                var swap = nearest; nearest = next; next = swap;
            }
            return nearest;
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
