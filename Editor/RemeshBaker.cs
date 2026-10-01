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
            // Cage: welded (position, side) clusters and the positions carrying more than
            // one side (double-sided sheets, collapsed slabs); the longest fitted reach as
            // a multiple of the projection distance.
            public int cageSides, foldedPositions;
            public float maxOneSidedDeg, meanTiltDeg, maxTiltDeg, maxReachRatio;
            public bool facingFilter;
            public int twoSidedFaces;   // source faces whose back counts as surface (two-sided materials)
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
            // The look is bounded (proxyDepth × capture diagonal) and falls back to the
            // nearest surface within the same reach: a hull's rounded corner still picks
            // the wall beside it, and no texel traverses the whole model to find nothing.
            bool proxy = settings.sourceShape != RemeshShape.LOD0;
            Vector3[] faceDirs = null; float depth = 0;
            if (proxy) {
                faceDirs = FaceNormals(target.positions, target.indices);
                depth = Mathf.Max(source.diagonal * settings.proxyDepth, source.diagonal * 1e-4f);
            }
            // Projection rays follow the smooth welded "cage" direction, not the vertex
            // normal: island hard-edge modes leave chart-border normals one-sided,
            // and casting along them samples a displaced source point, which bakes
            // artifact bands around every island. The tangent frame stays the vertex
            // normal the result mesh shades with, so encode and decode still match.
            // The cage is per corner and per side (see Cage), so a double-sided sheet
            // projects each face from its own side, and its reach is fitted to where
            // the source actually is when the settings ask for it.
            var cage = BuildCage(target, distance, settings.cageSmoothing, settings.cageFit && !proxy ? bvh : null, result);
            // Front-face filter for the projection rays: a plain closest-hit raycast
            // travels 2×distance THROUGH the target and can pierce a thin wall, sampling
            // the far side's texture (periodic mirrored/garbled patches). The filter only
            // accepts source triangles whose normal faces the ray origin. Source winding
            // is not guaranteed to be outward, so a probe orients the normals first;
            // without a clear majority the filter stays off and the bake behaves exactly
            // as before.
            // Two-sided source faces (Cull Off / double-sided materials, or the setting)
            // are surface from behind too: they pass the filter from either side and cast
            // no vote in the winding probe.
            var twoSided = source.TwoSidedFaces(settings.sourceBackfaces);
            if (twoSided != null) foreach (bool two in twoSided) if (two) ++result.twoSidedFaces;
            var faceNormals = FaceNormals(source.positions, source.indices);
            int winding = ProbeWinding(bvh, source.positions, faceNormals, twoSided);
            bool facingFilter = winding != 0;
            if (winding < 0)
                for (int f = 0; f < faceNormals.Length; ++f) faceNormals[f] = -faceNormals[f];
            result.facingFilter = facingFilter;
            Vector3[] facing = facingFilter ? faceNormals : null;
            bool[] eitherSide = facingFilter ? twoSided : null;
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
                    if (!Project(source, target, tangents, cage, bvh, facing, eitherSide, beauty, face, w,
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
        // The ray direction and reach come from the cage at the texel's corners; the
        // tangent frame keeps the (possibly hard) vertex normal so encode matches how
        // the mesh shades.
        // facing (when non-null) carries the orientation-consensus source face normals
        // that keep the ray and the fallback on the side of the surface facing the texel.
        // proxy: the ray starts just outside the proxy face and travels inward along the
        // face normal through the whole proxy (depth), first hit wins, no fallback.
        static bool Project(RemeshSource source, RemeshNative.Geometry target, Vector4[] tangents, Cage cage,
            TriangleBvh bvh, Vector3[] facing, bool[] eitherSide, RemeshBeauty beauty, int face, Vector3 w,
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
                    ? bvh.RaycastFacingFiltered(p + dir * eps, -dir, depth, facing, eitherSide)
                    : bvh.Raycast(p + dir * eps, -dir, depth);
                sourceFace = hit.triangleIndex; sw = hit.barycentric;
                if (sourceFace < 0) {
                    var nearest = facing != null
                        ? bvh.FindNearestNormalFiltered(p, dir, facing, 0f, depth, eitherSide)
                        : bvh.FindNearest(p, depth);
                    sourceFace = nearest.triangleIndex; sw = nearest.barycentric;
                    rayFallback = sourceFace >= 0;
                }
            }
            else {
                Vector3 rayN = cage.Direction(face, w);
                float reach = cage.Reach(face, w);
                var hit = facing != null
                    ? bvh.RaycastFacingFiltered(p + rayN * reach, -rayN, reach * 2, facing, eitherSide)
                    : bvh.Raycast(p + rayN * reach, -rayN, reach * 2);
                sourceFace = hit.triangleIndex; sw = hit.barycentric;
                if (sourceFace < 0) {
                    // Both fallbacks stay bounded by the cage reach: an unbounded
                    // filtered query would smear an unrelated far part across a gap.
                    var nearest = facing != null
                        ? bvh.FindNearestNormalFiltered(p, rayN, facing, 0f, reach, eitherSide)
                        : bvh.FindNearest(p, reach);
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

        /// <summary>
        /// The projection cage: a ray direction and a ray reach for every face CORNER of
        /// the result mesh. Rays leave the surface point along the interpolated corner
        /// directions, start <c>reach</c> away on the outside and travel 2 × reach.
        ///
        /// Directions are built per <b>side</b>, not per position: the corners meeting
        /// at one position are clustered by the hemisphere their face normals share,
        /// and only corners of one cluster are averaged (area × corner angle) and
        /// smoothed together. UV-chart and crease splits (copies with agreeing normals)
        /// weld back into one smooth direction as a plain welded cage does, but a
        /// double-sided sheet — the walls of a non-closed source after the voxel
        /// remesh, thinner than a cell and collapsed to zero thickness by the
        /// simplifier — keeps a front side and a back side, where a position weld
        /// would sum two opposite normals to nothing and normalize the noise (rays
        /// leaving at 180° from their face, shells crossing the whole model).
        /// Every corner direction is finally checked against its own face normal and
        /// falls back to the unsmoothed side, then to the face normal, so no ray ever
        /// starts behind the surface it belongs to.
        ///
        /// The reach is the projection distance everywhere, or, fitted, the distance the
        /// source actually sits at along each side's ray (times a margin for oblique
        /// surfaces, clamped between the projection distance and 8 × it) smoothed over
        /// the side connectivity — a cage that hugs the source where the decimation
        /// stayed close and opens where it drifted, instead of one global distance that
        /// misses here and bleeds through there.
        /// </summary>
        internal sealed class Cage
        {
            public const float MinFacing = 0.05f;   // cos ≈ 87°: a direction must leave its face's front
            public const float SideCos = -0.5f;     // corners within 120° of a side's sum join it
            public const float FitMargin = 2f;      // reach = source distance × margin
            public const float FitRange = 8f;       // the fit looks this many projection distances out

            public Vector3[] directions;   // per corner (indices.Length)
            public float[] reach;          // per corner
            public int[] side;             // per corner: welded (position, side) cluster
            public int positions, sides, folded, oneSided, zeroNormals;
            public float maxDeviationDeg, maxReach, distance;

            public Vector3 Direction(int face, Vector3 w)
            {
                int c = face * 3;
                Vector3 d = directions[c] * w.x + directions[c + 1] * w.y + directions[c + 2] * w.z;
                return d.sqrMagnitude > 1e-20f ? d.normalized : directions[c];
            }

            public float Reach(int face, Vector3 w)
            {
                int c = face * 3;
                return reach[c] * w.x + reach[c + 1] * w.y + reach[c + 2] * w.z;
            }
        }

        internal static Cage BuildCage(RemeshNative.Geometry target, float distance, float smoothing, TriangleBvh source, Maps diag = null)
        {
            var positions = target.positions; var indices = target.indices;
            int corners = indices.Length, faces = corners / 3;
            // Bit-exact position weld: xatlas and the simplifier copy coordinates exactly.
            var slots = new int[positions.Length];
            var map = new System.Collections.Generic.Dictionary<(int, int, int), int>(positions.Length);
            for (int i = 0; i < positions.Length; ++i) {
                var p = positions[i];
                var key = (BitConverter.SingleToInt32Bits(p.x), BitConverter.SingleToInt32Bits(p.y), BitConverter.SingleToInt32Bits(p.z));
                if (!map.TryGetValue(key, out int slot)) { slot = map.Count; map[key] = slot; }
                slots[i] = slot;
            }
            // Sides: per position a linked list of clusters; a corner joins the cluster
            // whose running sum its face normal agrees with best (within 120°), else
            // opens a new one. A sum only ever grows (every member has a positive dot
            // with it), so it never cancels the way a plain position weld does.
            var firstSide = new int[map.Count];
            for (int i = 0; i < firstSide.Length; ++i) firstSide[i] = -1;
            var nextSide = new System.Collections.Generic.List<int>();
            var sideSum = new System.Collections.Generic.List<Vector3>();
            var sideVertex = new System.Collections.Generic.List<int>();
            var cornerSide = new int[corners];
            var faceNormal = new Vector3[faces];
            for (int f = 0; f < faces; ++f) {
                int a = indices[f * 3], b = indices[f * 3 + 1], c = indices[f * 3 + 2];
                Vector3 cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                float area2 = cross.magnitude;
                Vector3 fn = area2 > 1e-30f ? cross / area2 : Vector3.zero;
                faceNormal[f] = fn;
                for (int k = 0; k < 3; ++k) {
                    int v = indices[f * 3 + k], slot = slots[v];
                    Vector3 e1 = positions[indices[f * 3 + (k + 1) % 3]] - positions[v];
                    Vector3 e2 = positions[indices[f * 3 + (k + 2) % 3]] - positions[v];
                    // Area × corner angle: meshopt's accumulation and Blender's weighted
                    // normal in one, so decimation slivers barely steer the direction.
                    Vector3 contribution = cross * (Vector3.Angle(e1, e2) * Mathf.Deg2Rad);
                    int best = -1; float bestDot = float.NegativeInfinity;
                    for (int sd = firstSide[slot]; sd >= 0; sd = nextSide[sd]) {
                        Vector3 sum = sideSum[sd];
                        float dot = sum.sqrMagnitude > 1e-30f && fn.sqrMagnitude > 0f ? Vector3.Dot(sum.normalized, fn) : 1f;
                        if (dot > Cage.SideCos && dot > bestDot) { best = sd; bestDot = dot; }
                    }
                    if (best < 0) {
                        best = sideSum.Count;
                        sideSum.Add(Vector3.zero); sideVertex.Add(v);
                        nextSide.Add(firstSide[slot]); firstSide[slot] = best;
                    }
                    sideSum[best] += contribution;
                    cornerSide[f * 3 + k] = best;
                }
            }
            int sideCount = sideSum.Count;
            var sideDir = new Vector3[sideCount];
            for (int sd = 0; sd < sideCount; ++sd)
                sideDir[sd] = sideSum[sd].sqrMagnitude > 1e-30f ? sideSum[sd].normalized : Vector3.zero;
            // Laplacian smoothing over the SIDE connectivity: the corners of a face all
            // sit on compatible sides, so the pass flows through chart borders and
            // creases of one surface and never across to the other side of a sheet.
            var welded = new RemeshNative.Geometry { normals = (Vector3[])sideDir.Clone(), indices = cornerSide };
            RemeshNative.SmoothNormals(welded, smoothing);
            var cage = new Cage { directions = new Vector3[corners], reach = new float[corners], side = cornerSide,
                positions = map.Count, sides = sideCount, distance = distance };
            var deviated = new bool[positions.Length];
            for (int c = 0; c < corners; ++c) {
                int sd = cornerSide[c], v = indices[c];
                Vector3 fn = faceNormal[c / 3];
                Vector3 d = welded.normals[sd];
                if (!Facing(d, fn)) d = sideDir[sd];
                if (!Facing(d, fn)) d = fn;
                if (d.sqrMagnitude < 1e-20f) d = target.normals[v];   // degenerate face at a degenerate vertex
                cage.directions[c] = d;
                cage.reach[c] = distance;
                if (target.normals[v].sqrMagnitude > 1e-12f) {
                    float dev = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, target.normals[v]), -1f, 1f)) * Mathf.Rad2Deg;
                    if (dev > cage.maxDeviationDeg) cage.maxDeviationDeg = dev;
                    if (dev > 30f) deviated[v] = true;
                }
            }
            for (int i = 0; i < positions.Length; ++i) {
                if (deviated[i]) ++cage.oneSided;
                if (target.normals[i].sqrMagnitude < 1e-12f) ++cage.zeroNormals;
            }
            for (int slot = 0; slot < firstSide.Length; ++slot)
                if (firstSide[slot] >= 0 && nextSide[firstSide[slot]] >= 0) ++cage.folded;
            cage.maxReach = distance;
            if (source != null && distance > 0f) FitReach(cage, welded.normals, sideDir, sideVertex, positions, source);
            if (diag != null) {
                diag.weldedPositions = cage.positions;
                diag.splitCopies = positions.Length - cage.positions;
                diag.cageSides = cage.sides;
                diag.foldedPositions = cage.folded;
                diag.oneSidedNormals = cage.oneSided;
                diag.maxOneSidedDeg = cage.maxDeviationDeg;
                diag.zeroNormals = cage.zeroNormals;
                diag.maxReachRatio = distance > 0f ? cage.maxReach / distance : 1f;
            }
            return cage;
        }

        static bool Facing(Vector3 d, Vector3 faceNormal)
            => d.sqrMagnitude > 1e-20f && (faceNormal.sqrMagnitude < 1e-20f || Vector3.Dot(d, faceNormal) >= Cage.MinFacing);

        // Per side: the distance the source sits at along the side's ray (either way),
        // or the nearest source point when the ray meets nothing — the bake's nearest
        // fallback is bounded by the same reach, so a fitted reach lets it catch what
        // the ray cannot. Smoothed over the side connectivity, never below a side's own
        // measured need, so the shells stay shells instead of spiking per vertex.
        static void FitReach(Cage cage, Vector3[] smoothed, Vector3[] sideDir, System.Collections.Generic.List<int> sideVertex,
            Vector3[] positions, TriangleBvh source)
        {
            float distance = cage.distance, range = distance * Cage.FitRange;
            int sides = sideDir.Length;
            var need = new float[sides];
            for (int sd = 0; sd < sides; ++sd) {
                Vector3 d = smoothed[sd].sqrMagnitude > 1e-20f ? smoothed[sd] : sideDir[sd];
                Vector3 p = positions[sideVertex[sd]];
                float found = -1f;
                if (d.sqrMagnitude > 1e-20f) {
                    float eps = distance * 1e-3f;
                    var outward = source.Raycast(p + d * eps, d, range);
                    var inward = source.Raycast(p - d * eps, -d, range);
                    if (outward.triangleIndex >= 0) found = outward.t + eps;
                    if (inward.triangleIndex >= 0 && (found < 0f || inward.t + eps < found)) found = inward.t + eps;
                }
                if (found < 0f) {
                    var nearest = source.FindNearest(p, range);
                    if (nearest.triangleIndex >= 0) found = Mathf.Sqrt(nearest.distSq);
                }
                need[sd] = found < 0f ? distance : Mathf.Clamp(found * Cage.FitMargin, distance, range);
            }
            var reach = (float[])need.Clone();
            var sum = new float[sides]; var count = new int[sides];
            var side = cage.side;
            for (int pass = 0; pass < 2; ++pass) {
                Array.Clear(sum, 0, sides); Array.Clear(count, 0, sides);
                for (int c = 0; c < side.Length; c += 3) {
                    Neighbour(reach, sum, count, side[c], side[c + 1]);
                    Neighbour(reach, sum, count, side[c + 1], side[c + 2]);
                    Neighbour(reach, sum, count, side[c + 2], side[c]);
                }
                for (int sd = 0; sd < sides; ++sd)
                    if (count[sd] > 0) reach[sd] = Mathf.Max(need[sd], 0.5f * (reach[sd] + sum[sd] / count[sd]));
            }
            float max = distance;
            for (int c = 0; c < side.Length; ++c) { cage.reach[c] = reach[side[c]]; if (reach[side[c]] > max) max = reach[side[c]]; }
            cage.maxReach = max;
        }

        static void Neighbour(float[] reach, float[] sum, int[] count, int a, int b)
        {
            if (a == b) return;
            sum[a] += reach[b]; ++count[a];
            sum[b] += reach[a]; ++count[b];
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
        /// Two-sided faces (twoSided, optional) are oriented either way and cast no vote.
        /// </summary>
        internal static int ProbeWinding(TriangleBvh bvh, Vector3[] positions, Vector3[] faceNormals, bool[] twoSided = null)
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
                if (twoSided != null && hit.triangleIndex < twoSided.Length && twoSided[hit.triangleIndex]) continue;
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
