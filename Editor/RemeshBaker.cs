using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class RemeshBaker
    {
        internal const int QueryBudget = 16384;
        internal sealed class Maps
        {
            public Color32[] color, normal, metal, ao;
            public Color[] emission;
            public Color[] vertexColors; // per result vertex, null unless a transfer was requested
            public int size, misses, covered;
            public int partialMisses, gutterTexels, gutterMisses, surfaceSamples, boundarySamples;
            public int surfaceEdges, stoppedWalks, walkLimitHits, patchLimitHits, unfoldOverlapTexels;
            public float missedSampleArea;
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
            public bool sourceAO;
            public bool beauty;
            public bool gpu;            // the geometry queries ran on the GPU
            public bool gpuAO;          // source AO rays ran on the GPU
        }

        // ── Batched bake ──
        // The bake is one code path with a pluggable query backend. Prepare builds
        // everything the texel loop needs (coverage, BVH, cage, orientation probe);
        // the atlas is then processed in bands of rows: BuildRequests turns a band's
        // covered samples into ray and nearest-point queries, a resolver answers them
        // (the CPU TriangleBvh in parallel, or GpuBvh in one dispatch per batch), and
        // EvaluateBand turns the answers into texels. Finish adds the fills, the
        // diagnostics, the dilation and the vertex colour transfer.

        /// <summary>CPU bake: thread-safe, no UnityEngine.Object access. beauty (optional) folds the scene lighting into the albedo.</summary>
        public static Maps Bake(RemeshSource source, RemeshNative.Geometry target, Vector4[] tangents,
            RemeshSettings settings, CancellationToken token, RemeshBeauty beauty = null)
        {
            var ctx = Prepare(source, target, tangents, settings, token, beauty);
            var band = new Band(ctx);
            for (int pixel = 0; pixel < ctx.owners.Length; pixel = band.nextPixel) {
                BuildRequests(ctx, band, pixel, token);
                ResolveCpu(ctx, band, token);
                EvaluateBand(ctx, band, token);
            }
            return Finish(ctx, token);
        }

        /// <summary>
        /// GPU bake, driven from the main thread: the geometry queries of every band go
        /// through <paramref name="gpu"/> (a GpuBvh over this bake's source, created by
        /// <see cref="CreateGpu"/>), everything else runs on workers. Results mean the
        /// same as <see cref="Bake"/>'s.
        /// </summary>
        public static async Task<Maps> BakeAsync(RemeshSource source, RemeshNative.Geometry target, Vector4[] tangents,
            RemeshSettings settings, CancellationToken token, RemeshBeauty beauty, Func<Context, GpuBvh> createGpu, bool gpuSourceAO = false)
        {
            var ctx = await Task.Run(() => Prepare(source, target, tangents, settings, token, beauty), token);
            GpuBvh gpu = null;
            SourceAoBaker.Gpu aoGpu = null;
            try {
                token.ThrowIfCancellationRequested();
                gpu = SystemInfo.supportsAsyncGPUReadback ? createGpu(ctx) : null;
                if (gpuSourceAO) aoGpu = ctx.aoBaker?.TryCreateGpu(gpu);
                bool useAoGpu = aoGpu != null;
                var band = await Task.Run(() => {
                    var prepared = new Band(ctx);
                    if (useAoGpu) {
                        prepared.aoPoints = new SourceAoBaker.SurfacePoint[prepared.pixel.Length];
                        prepared.aoValues = new float[prepared.pixel.Length];
                    }
                    return prepared;
                }, token);
                for (int pixel = 0; pixel < ctx.owners.Length; pixel = band.nextPixel) {
                    int firstPixel = pixel;
                    await Task.Run(() => BuildRequests(ctx, band, firstPixel, token), token);
                    if (gpu != null) await ResolveGpuAsync(ctx, band, gpu, token);
                    else await Task.Run(() => ResolveCpu(ctx, band, token), token);
                    if (aoGpu != null) {
                        await Task.Run(() => PrepareAoPoints(ctx, band, token), token);
                        await aoGpu.SampleAsync(band.aoPoints, band.count, band.aoValues, token);
                    }
                    await Task.Run(() => EvaluateBand(ctx, band, token), token);
                }
                ctx.result.gpu = gpu != null;
                ctx.result.gpuAO = aoGpu != null;
                return await Task.Run(() => Finish(ctx, token), token);
            }
            finally { aoGpu?.Dispose(); gpu?.Dispose(); }
        }

        /// <summary>The GPU tree for a prepared bake: the source BVH with the oriented face normals and the either-side mask the filters use.</summary>
        public static GpuBvh CreateGpu(Context ctx) => GpuBvh.TryCreate(ctx.bvh, ctx.faceNormals, ctx.twoSided);

        /// <summary>Everything the texel loop reads; built once per bake by Prepare.</summary>
        public sealed class Context
        {
            public RemeshSource source; public RemeshNative.Geometry target; public Vector4[] tangents;
            public RemeshSettings settings; public RemeshBeauty beauty;
            internal SourceAoBaker aoBaker;
            public Maps result; public int size, bandRows; public Vector2[] offsets; public int[] owners, receivers;
            internal RemeshTexelFootprint footprint;
            public TriangleBvh bvh; public Cage cage; public bool proxy; public Vector3[] faceDirs; public float depth;
            public Vector3[] targetFaceNormals;
            public Vector3[] faceNormals;   // source faces, oriented by the winding probe
            public bool[] twoSided;         // source faces whose back counts (null = none)
            public bool facingFilter;
            public bool[] emptyTexels;
            public int misses, rayFallbacks, empties, partialMisses, gutterMisses, surfaceSamples, boundarySamples;
            public double missedSampleArea;
        }

        /// <summary>One band of rows as queries and answers; arrays are reused across bands.</summary>
        public sealed class Band
        {
            public int y0, y1, count, nextPixel;
            public int[] rowStart;          // per row of the band (+1 sentinel): first sample index
            public int[] pixel, face;        // per sample
            public Vector3[] weights;        // per sample: barycentric on the target face
            public float[] area;
            public Quaternion[] transport;
            public Vector4[] rayOrigin, rayDir, point, pointNormal;   // queries (w = reach / dotMin)
            public GpuBvh.RayHit[] rayHit; public GpuBvh.NearestHit[] nearest;
            public bool[] needNearest;
            internal SourceAoBaker.SurfacePoint[] aoPoints;
            internal float[] aoValues;
            internal readonly List<Request> requests = new List<Request>(QueryBudget);
            internal readonly List<RemeshTexelFootprint.Sample> samples = new List<RemeshTexelFootprint.Sample>(32);

            public Band(Context ctx)
            {
                int capacity = Math.Min(QueryBudget, ctx.bandRows * ctx.size * ctx.offsets.Length);
                rowStart = new int[ctx.bandRows + 1];
                pixel = new int[capacity]; face = new int[capacity]; weights = new Vector3[capacity];
                area = new float[capacity];
                transport = new Quaternion[capacity];
                rayOrigin = new Vector4[capacity]; rayDir = new Vector4[capacity]; point = new Vector4[capacity]; pointNormal = new Vector4[capacity];
                rayHit = new GpuBvh.RayHit[capacity]; nearest = new GpuBvh.NearestHit[capacity]; needNearest = new bool[capacity];
            }

            internal void EnsureCapacity(int count)
            {
                if (count <= pixel.Length) return;
                int capacity = Math.Max(count, checked(pixel.Length * 2));
                Array.Resize(ref pixel, capacity); Array.Resize(ref face, capacity); Array.Resize(ref weights, capacity);
                Array.Resize(ref area, capacity);
                Array.Resize(ref transport, capacity);
                Array.Resize(ref rayOrigin, capacity); Array.Resize(ref rayDir, capacity);
                Array.Resize(ref point, capacity); Array.Resize(ref pointNormal, capacity);
                Array.Resize(ref rayHit, capacity); Array.Resize(ref nearest, capacity); Array.Resize(ref needNearest, capacity);
                if (aoPoints != null) { Array.Resize(ref aoPoints, capacity); Array.Resize(ref aoValues, capacity); }
            }
        }

        static Context Prepare(RemeshSource source, RemeshNative.Geometry target, Vector4[] tangents,
            RemeshSettings settings, CancellationToken token, RemeshBeauty beauty)
        {
            if (target.draftUv) throw new InvalidOperationException("Bake requires an unwrapped atlas; UV0 is still draft.");
            settings.Validate();
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
            var receivers = BuildReceivers(owners, size, settings.padding, settings.dilationRadius, token);
            for (int i = 0; i < count; ++i) if (owners[i] < 0 && receivers[i] >= 0) ++result.gutterTexels;
            var neighbors = target.surfaceNeighbors ?? RemeshSurfaceTopology.Build(target.positions, target.indices, token);
            for (int i = 0; i < neighbors.Length; ++i) if (neighbors[i] > i) ++result.surfaceEdges;
            var footprint = new RemeshTexelFootprint(target, neighbors, size);
            var targetFaceNormals = TargetFaceNormals(target, token);
            var bvh = new TriangleBvh(source.positions, source.indices);
            token.ThrowIfCancellationRequested();
            result.beauty = beauty != null;
            if (beauty != null) beauty.BindShadows(bvh, source);
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
                faceDirs = targetFaceNormals;
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
            // Front-face filter for the projection rays: a plain closest-hit raycast
            // travels 2×distance THROUGH the target and can pierce a thin wall, sampling
            // the far side's texture (periodic mirrored/garbled patches). The filter only
            // accepts source triangles whose normal faces the ray origin. Source winding
            // is not guaranteed to be outward, so a probe orients the normals first;
            // without a clear majority the filter stays off and the bake behaves exactly
            // as before. Two-sided source faces (Cull Off / double-sided materials, or the
            // setting) are surface from behind too: they pass the filter from either side
            // and cast no vote in the probe.
            var twoSided = source.TwoSidedFaces(settings.sourceBackfaces);
            if (twoSided != null) foreach (bool two in twoSided) if (two) ++result.twoSidedFaces;
            var faceNormals = MeshGeometry.FaceNormals(source.positions, source.indices);
            int winding = ProbeWinding(bvh, source.positions, faceNormals, twoSided);
            bool facingFilter = winding != 0;
            if (winding < 0)
                for (int f = 0; f < faceNormals.Length; ++f) faceNormals[f] = -faceNormals[f];
            result.facingFilter = facingFilter;
            var cage = BuildCageWithFacing(target, distance, settings.cageSmoothing, settings.cageFit && !proxy ? bvh : null,
                facingFilter ? faceNormals : null, facingFilter ? twoSided : null, result, token);
            result.sourceAO = settings.bakeSourceAO;
            var aoBaker = settings.bakeSourceAO ? new SourceAoBaker(source, bvh, faceNormals, twoSided, settings.sourceAO) : null;
            // Bands sized so one band's samples fit a query batch (and a modest amount of memory).
            int bandRows = Mathf.Clamp(QueryBudget / Math.Max(1, size * offsets.Length), 1, size);
            return new Context { source = source, target = target, tangents = tangents, settings = settings, beauty = beauty,
                result = result, aoBaker = aoBaker, size = size, bandRows = bandRows, offsets = offsets, owners = owners,
                receivers = receivers, footprint = footprint, bvh = bvh, cage = cage, targetFaceNormals = targetFaceNormals,
                proxy = proxy, faceDirs = faceDirs, depth = depth, faceNormals = faceNormals, twoSided = twoSided,
                facingFilter = facingFilter, emptyTexels = proxy ? new bool[count] : null };
        }

        internal struct Request
        {
            internal int pixel;
            internal RemeshTexelFootprint.Sample sample;
        }

        // Integrate every intersecting physical face, including continuation across
        // UV cuts. Atlas proximity selects a receiving shell, never a source surface.
        static void BuildRequests(Context ctx, Band band, int firstPixel, CancellationToken token)
        {
            int size = ctx.size, grid = Mathf.RoundToInt(Mathf.Sqrt(ctx.offsets.Length));
            band.y0 = firstPixel / size;
            int limitPixel = Math.Min(ctx.owners.Length, (band.y0 + ctx.bandRows) * size);
            var requests = band.requests; requests.Clear();
            var samples = band.samples;
            int continued = 0, boundary = 0, row = band.y0;
            band.rowStart[0] = 0;
            int cursor = firstPixel;
            // A complete pixel may contain more contributors than the sample grid.
            // Stop at a query budget between pixels, even in the middle of a row.
            // One pixel is bounded by the footprint's local face/grid limits.
            for (; cursor < limitPixel; ++cursor) {
                token.ThrowIfCancellationRequested();
                int y = cursor / size, x = cursor % size;
                if (y != row) { row = y; band.rowStart[row - band.y0] = requests.Count; }
                int receiver = ctx.receivers[cursor];
                if (receiver < 0) continue;
                samples.Clear();
                ctx.footprint.Gather(receiver, x, y, grid, samples, token);
                foreach (var sample in samples) {
                    if (!(sample.area > 0)) continue;
                    requests.Add(new Request { pixel = cursor, sample = sample });
                    if (sample.boundary) ++boundary;
                    else if (sample.face != receiver) ++continued;
                }
                if (requests.Count >= QueryBudget) break;
            }
            if (requests.Count >= QueryBudget) ++cursor;
            band.nextPixel = cursor;
            band.y1 = (cursor + size - 1) / size;
            int rows = band.y1 - band.y0;
            band.rowStart[rows] = requests.Count;
            band.EnsureCapacity(requests.Count); band.count = requests.Count;
            Interlocked.Add(ref ctx.surfaceSamples, continued); Interlocked.Add(ref ctx.boundarySamples, boundary);
            var options = new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.For(0, rows, options, r => {
                int write = band.rowStart[r];
                for (int requestIndex = band.rowStart[r]; requestIndex < band.rowStart[r + 1]; ++requestIndex) {
                    var request = requests[requestIndex];
                    var sample = request.sample;
                    int face = sample.face; Vector3 w = sample.weights;
                    var target = ctx.target;
                    int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
                    Vector3 p = target.positions[a] * w.x + target.positions[b] * w.y + target.positions[c] * w.z;
                    band.pixel[write] = request.pixel; band.face[write] = face; band.weights[write] = w;
                    band.area[write] = sample.area;
                    band.transport[write] = sample.transport;
                    Vector3 direction; float reach, length;
                    if (ctx.proxy) {
                        direction = ctx.faceDirs[face]; reach = ctx.depth * 1e-4f; length = ctx.depth;
                    }
                    else {
                        direction = ctx.cage.Direction(face, w); reach = ctx.cage.Reach(face, w); length = reach * 2f;
                    }
                    Vector3 origin = p + direction * reach;
                    band.rayOrigin[write] = new Vector4(origin.x, origin.y, origin.z, length);
                    band.rayDir[write] = new Vector4(-direction.x, -direction.y, -direction.z, 0f);
                    band.point[write] = new Vector4(p.x, p.y, p.z, ctx.proxy ? ctx.depth : reach);
                    band.pointNormal[write] = new Vector4(direction.x, direction.y, direction.z, 0f);
                    ++write;
                }
            });
        }

        // CPU resolver: the same two queries per sample the GPU kernel answers, through
        // the TriangleBvh, in parallel.
        static void ResolveCpu(Context ctx, Band band, CancellationToken token)
        {
            var bvh = ctx.bvh; var facing = ctx.facingFilter ? ctx.faceNormals : null; var either = ctx.facingFilter ? ctx.twoSided : null;
            Parallel.For(0, band.count, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i => {
                Vector4 o = band.rayOrigin[i]; Vector3 d = band.rayDir[i];
                var hit = facing != null ? bvh.RaycastFacingFiltered(o, d, o.w, facing, either) : bvh.Raycast(o, d, o.w);
                band.rayHit[i] = new GpuBvh.RayHit { tri = hit.triangleIndex, t = hit.t, u = hit.barycentric.y, v = hit.barycentric.z };
                if (hit.triangleIndex >= 0) { band.nearest[i].tri = -1; return; }
                Vector4 q = band.point[i]; Vector3 qn = band.pointNormal[i];
                var near = facing != null ? bvh.FindNearestNormalFiltered(q, qn, facing, 0f, q.w, either) : bvh.FindNearest(q, q.w);
                band.nearest[i] = new GpuBvh.NearestHit { tri = near.triangleIndex, distSq = near.distSq, point = near.point, bary = near.barycentric };
            });
        }

        // GPU resolver: one ray batch, then one nearest batch for the misses only.
        static async Task ResolveGpuAsync(Context ctx, Band band, GpuBvh gpu, CancellationToken token)
        {
            await gpu.RaycastAsync(band.rayOrigin, band.rayDir, band.count, ctx.facingFilter, band.rayHit, token);
            int pending = await Task.Run(() => {
                int misses = 0;
                for (int i = 0; i < band.count; ++i) {
                    if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                    band.nearest[i].tri = -1;
                    bool miss = band.rayHit[i].tri < 0;
                    band.needNearest[i] = miss;
                    // Skipped points carry radius 0 so the kernel answers "none" without traversing.
                    if (!miss) { var q = band.point[i]; band.point[i] = new Vector4(q.x, q.y, q.z, 0f); }
                    else ++misses;
                }
                return misses;
            }, token);
            if (pending > 0) await gpu.NearestAsync(band.point, band.pointNormal, band.count, ctx.facingFilter, band.nearest, token);
        }

        static void PrepareAoPoints(Context ctx, Band band, CancellationToken token)
        {
            Parallel.For(0, band.count, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i => {
                var hit = band.rayHit[i];
                int face = hit.tri >= 0 ? hit.tri : band.nearest[i].tri;
                Vector3 weights = hit.tri >= 0 ? new Vector3(1 - hit.u - hit.v, hit.u, hit.v) : band.nearest[i].bary;
                band.aoPoints[i] = face >= 0 ? ctx.aoBaker.SamplePoint(face, weights, band.pixel[i]) : default;
            });
        }

        // Every contributing source normal is expressed in the receiving face's
        // frame before area averaging. A neighbouring chart's tangent bytes cannot
        // be copied across a seam, especially on mirrored or rotated charts.
        static void EvaluateBand(Context ctx, Band band, CancellationToken token)
        {
            int rows = band.y1 - band.y0; var result = ctx.result; var source = ctx.source;
            bool vertexTint = ctx.settings.vertexColorTint;
            Parallel.For(0, rows, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, r => {
                int i = band.rowStart[r], end = band.rowStart[r + 1];
                int misses = 0, fallbacks = 0, empties = 0, partial = 0, gutterMisses = 0;
                double missedArea = 0;
                while (i < end) {
                    token.ThrowIfCancellationRequested();
                    int pixel = band.pixel[i], receiver = ctx.receivers[pixel];
                    bool covered = ctx.owners[pixel] >= 0;
                    TargetFrame(ctx, receiver, pixel, out var n, out var tangent);
                    Color color = default, metal = default, ao = default, emission = default;
                    Vector3 normal = Vector3.zero;
                    float hitArea = 0, totalArea = 0;
                    bool sampleMiss = false;
                    for (; i < end && band.pixel[i] == pixel; ++i) {
                        float area = band.area[i]; totalArea += area;
                        int sourceFace; Vector3 sw;
                        var ray = band.rayHit[i];
                        if (ray.tri >= 0) { sourceFace = ray.tri; sw = new Vector3(1f - ray.u - ray.v, ray.u, ray.v); }
                        else {
                            var near = band.nearest[i]; sourceFace = near.tri; sw = near.bary;
                            if (covered && (sourceFace >= 0 || !ctx.proxy)) ++fallbacks;
                        }
                        if (sourceFace < 0) { sampleMiss = true; continue; }
                        int face = band.face[i]; Vector3 w = band.weights[i];
                        Vector3 donorNormal = TargetNormal(ctx, face, w);
                        Quaternion transport = Quaternion.FromToRotation(ctx.targetFaceNormals[receiver], n) *
                            band.transport[i] * Quaternion.FromToRotation(donorNormal, ctx.targetFaceNormals[face]);
                        EvaluateProjected(source, sourceFace, sw, n, tangent, transport, vertexTint,
                            out var sc, out var sn, out var sm, out var sa, out var se);
                        if (ctx.aoBaker != null) {
                            float computedAO = band.aoValues != null ? band.aoValues[i] : ctx.aoBaker.Sample(sourceFace, sw, pixel, token);
                            if (ctx.settings.multiplySourceAO) computedAO *= sa.g;
                            sa = new Color(computedAO, computedAO, computedAO, 1);
                        }
                        if (ctx.beauty != null) sc = BeautyLight(ctx.beauty, source, sourceFace, sw, sc, sm, se).gamma;
                        color += sc.linear * area; metal += sm * area; ao += sa * area; emission += se * area;
                        normal += new Vector3(sn.r * 2 - 1, sn.g * 2 - 1, sn.b * 2 - 1) * area;
                        hitArea += area;
                    }
                    if (covered && sampleMiss && totalArea > 0) missedArea += (totalArea - hitArea) / totalArea;
                    if (!(hitArea > 0)) {
                        if (!covered) { ++gutterMisses; continue; }
                        if (ctx.proxy) { ctx.emptyTexels[pixel] = true; ++empties; continue; }
                        ++misses;
                        result.color[pixel] = new Color32(255, 0, 255, 255);
                        result.normal[pixel] = new Color32(128, 128, 255, 255);
                        result.ao[pixel] = new Color32(255, 255, 255, 255);
                        continue;
                    }
                    if (covered && sampleMiss) ++partial;
                    float inv = 1f / hitArea;
                    Color averaged = (color * inv).gamma; averaged.a = 1;
                    normal = MeshGeometry.UnitDirection(normal);
                    if (normal == Vector3.zero) normal = Vector3.forward;
                    result.color[pixel] = averaged;
                    result.normal[pixel] = new Color(normal.x * .5f + .5f, normal.y * .5f + .5f, normal.z * .5f + .5f, 1);
                    result.metal[pixel] = metal * inv; result.ao[pixel] = ao * inv;
                    var e = emission * inv; e.a = 1; result.emission[pixel] = e;
                }
                Interlocked.Add(ref ctx.misses, misses); Interlocked.Add(ref ctx.rayFallbacks, fallbacks);
                Interlocked.Add(ref ctx.empties, empties); Interlocked.Add(ref ctx.partialMisses, partial);
                Interlocked.Add(ref ctx.gutterMisses, gutterMisses);
                if (missedArea > 0) lock (result) ctx.missedSampleArea += missedArea;
            });
        }

        static Maps Finish(Context ctx, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = ctx.result; var owners = ctx.owners; int count = owners.Length;
            result.misses = ctx.misses;
            result.rayFallbacks = ctx.rayFallbacks;
            result.empty = ctx.empties;
            result.partialMisses = ctx.partialMisses; result.missedSampleArea = (float)ctx.missedSampleArea;
            result.gutterMisses = ctx.gutterMisses; result.surfaceSamples = ctx.surfaceSamples; result.boundarySamples = ctx.boundarySamples;
            result.stoppedWalks = ctx.footprint.NavigationFallbacks; result.patchLimitHits = ctx.footprint.LocalFaceLimitHits;
            result.walkLimitHits = ctx.footprint.NavigationLimitHits;
            result.unfoldOverlapTexels = ctx.footprint.UnfoldOverlapTexels;
            if (ctx.proxy && ctx.empties > 0) FillEmpty(result, owners, ctx.emptyTexels, token);
            if (ctx.gutterMisses > 0) FillMissingGutters(ctx, token);
            // Normal-map tilt health: how far encoded normals lean away from the
            // tangent plane's up axis. Loud, strongly-tilted maps on smooth-ish
            // sources are the visual signature of displaced projection samples.
            double tiltSum = 0; float tiltMax = 0; int loud = 0, tiltN = 0;
            for (int i = 0; i < count; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
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
            // Gutters were baked from the physical surface together with covered texels.
            // Proxy voids retain their existing alpha-mask fill, including the margin.
            if (ctx.proxy) PreserveProxyMarginAlpha(ctx, token);
            if (ctx.settings.transferVertexColor || ctx.settings.transferVertexAlpha)
                result.vertexColors = TransferVertexColors(ctx.source, ctx.target, ctx.bvh, ctx.settings, token);
            token.ThrowIfCancellationRequested();
            return result;
        }

        // A projection failure must not leave black filtering pixels. This is an
        // explicitly counted fallback, transported between the two receiver frames.
        static void FillMissingGutters(Context ctx, CancellationToken token)
        {
            var nearest = TextureDilation.NearestFilled((int[])ctx.owners.Clone(), ctx.size, token);
            for (int i = 0; i < nearest.Length; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (ctx.owners[i] >= 0 || ctx.receivers[i] < 0 || ctx.result.color[i].a != 0 || nearest[i] < 0) continue;
                int seed = nearest[i];
                CopyTexel(ctx.result, seed, i);
                TargetFrame(ctx, ctx.owners[seed], seed, out var sourceNormal, out var sourceTangent);
                TargetFrame(ctx, ctx.receivers[i], i, out var receiverNormal, out var receiverTangent);
                Basis(sourceNormal, sourceTangent, out var st, out var sb);
                Basis(receiverNormal, receiverTangent, out var rt, out var rb);
                var encoded = ctx.result.normal[seed];
                Vector3 world = MeshGeometry.UnitDirection(st * (encoded.r / 127.5f - 1) +
                    sb * (encoded.g / 127.5f - 1) + sourceNormal * (encoded.b / 127.5f - 1));
                world = Quaternion.FromToRotation(sourceNormal, receiverNormal) * world;
                ctx.result.normal[i] = new Color(Vector3.Dot(world, rt) * .5f + .5f,
                    Vector3.Dot(world, rb) * .5f + .5f, Vector3.Dot(world, receiverNormal) * .5f + .5f, 1);
            }
        }

        static void PreserveProxyMarginAlpha(Context ctx, CancellationToken token)
        {
            var nearest = TextureDilation.NearestFilled((int[])ctx.owners.Clone(), ctx.size, token);
            for (int i = 0; i < nearest.Length; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (ctx.owners[i] >= 0 || ctx.receivers[i] < 0 || nearest[i] < 0) continue;
                var color = ctx.result.color[i]; color.a = ctx.result.color[nearest[i]].a; ctx.result.color[i] = color;
            }
        }

        static void TargetFrame(Context ctx, int receiver, int pixel, out Vector3 normal, out Vector4 tangent)
        {
            Vector3 w = ctx.footprint.ReceiverWeights(receiver,
                new Vector2((pixel % ctx.size + .5f) / ctx.size, (pixel / ctx.size + .5f) / ctx.size));
            var target = ctx.target;
            int a = target.indices[receiver * 3], b = target.indices[receiver * 3 + 1], c = target.indices[receiver * 3 + 2];
            normal = TargetNormal(ctx, receiver, w);
            tangent = ctx.tangents[a] * w.x + ctx.tangents[b] * w.y + ctx.tangents[c] * w.z;
        }

        static Vector3 TargetNormal(Context ctx, int face, Vector3 weights)
        {
            var target = ctx.target;
            int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
            var normal = MeshGeometry.UnitDirection(target.normals[a] * weights.x + target.normals[b] * weights.y + target.normals[c] * weights.z);
            return normal == Vector3.zero ? ctx.targetFaceNormals[face] : normal;
        }

        static Vector3[] TargetFaceNormals(RemeshNative.Geometry target, CancellationToken token)
        {
            var normals = new Vector3[target.indices.Length / 3];
            for (int face = 0; face < normals.Length; ++face) {
                if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                var a = target.positions[target.indices[face * 3]];
                var b = target.positions[target.indices[face * 3 + 1]];
                var c = target.positions[target.indices[face * 3 + 2]];
                normals[face] = MeshGeometry.UnitDirection(Vector3.Cross(MeshGeometry.UnitDirection(b - a), MeshGeometry.UnitDirection(c - a)));
            }
            return normals;
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

        // Prefer centre/sample ownership, then exact triangle/texel intersection.
        // Coverage cannot depend on whether a thin chart contains a sample centre.
        static int[] Rasterize(RemeshNative.Geometry target, int size, Vector2[] offsets, CancellationToken token)
        {
            var owners = new int[size * size];
            var centred = new bool[owners.Length];
            for (int i = 0; i < owners.Length; ++i) owners[i] = -1;
            for (int face = 0; face < target.indices.Length / 3; ++face) {
                token.ThrowIfCancellationRequested();
                var polygon = new Vector2[8]; var scratch = new Vector2[8];
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
                        else if (!centred[pixel] && owners[pixel] < 0) {
                            foreach (var offset in offsets)
                                if (Inside(target, face, new Vector2((x + offset.x) / size, (y + offset.y) / size), out _)) {
                                    owners[pixel] = face; break;
                                }
                            if (owners[pixel] < 0 && CoveredPoint(target, face, x, y, size, polygon, scratch, out _)) owners[pixel] = face;
                        }
                    }
            }
            return owners;
        }

        static bool Inside(RemeshNative.Geometry target, int face, Vector2 uv, out Vector3 w)
        {
            int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
            return BarycentricUv(uv, target.uv[a], target.uv[b], target.uv[c], out w) &&
                w.x >= -1e-6f && w.y >= -1e-6f && w.z >= -1e-6f;
        }

        static bool BarycentricUv(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 weights)
        {
            double bx = (double)b.x - a.x, by = (double)b.y - a.y, cx = (double)c.x - a.x, cy = (double)c.y - a.y;
            double px = (double)p.x - a.x, py = (double)p.y - a.y, determinant = bx * cy - by * cx;
            weights = default;
            if (determinant == 0 || double.IsNaN(determinant) || double.IsInfinity(determinant)) return false;
            double v = (px * cy - py * cx) / determinant, w = (bx * py - by * px) / determinant;
            weights = new Vector3((float)(1 - v - w), (float)v, (float)w);
            return !float.IsNaN(weights.x) && !float.IsNaN(weights.y) && !float.IsNaN(weights.z) &&
                !float.IsInfinity(weights.x) && !float.IsInfinity(weights.y) && !float.IsInfinity(weights.z);
        }

        // Clip the UV triangle to the texel square (Sutherland-Hodgman), retaining
        // its area centroid rather than averaging polygon vertices.
        static bool CoveredPoint(RemeshNative.Geometry target, int face, int x, int y, int size,
            Vector2[] polygon, Vector2[] scratch, out Vector3 weights)
        {
            weights = default;
            for (int i = 0; i < 3; ++i) polygon[i] = target.uv[target.indices[face * 3 + i]] * size;
            int count = 3;
            for (int edge = 0; edge < 4 && count > 0; ++edge) {
                int axis = edge / 2;
                bool minimum = (edge & 1) == 0;
                float boundary = (axis == 0 ? x : y) + (minimum ? 0 : 1);
                int written = 0;
                Vector2 previous = polygon[count - 1];
                float previousDistance = (previous[axis] - boundary) * (minimum ? 1 : -1);
                for (int i = 0; i < count; ++i) {
                    Vector2 current = polygon[i];
                    float currentDistance = (current[axis] - boundary) * (minimum ? 1 : -1);
                    if (currentDistance != 0 && previousDistance != 0 && (currentDistance >= 0) != (previousDistance >= 0))
                        scratch[written++] = previous + (current - previous) * (previousDistance / (previousDistance - currentDistance));
                    if (currentDistance >= 0) scratch[written++] = current;
                    previous = current; previousDistance = currentDistance;
                }
                count = written;
                var swap = polygon; polygon = scratch; scratch = swap;
            }
            if (count < 3) return false;
            double area = 0, sumX = 0, sumY = 0;
            for (int i = 1; i + 1 < count; ++i) {
                Vector2 a = polygon[i] - polygon[0], b = polygon[i + 1] - polygon[0];
                double cross = (double)a.x * b.y - (double)a.y * b.x;
                area += cross;
                sumX += ((double)polygon[0].x + polygon[i].x + polygon[i + 1].x) * cross / 3;
                sumY += ((double)polygon[0].y + polygon[i].y + polygon[i + 1].y) * cross / 3;
            }
            if (area == 0f) return false; // edge/point contact has no coverage
            int ia = target.indices[face * 3], ib = target.indices[face * 3 + 1], ic = target.indices[face * 3 + 2];
            if (!BarycentricUv(new Vector2((float)(sumX / (area * size)), (float)(sumY / (area * size))),
                target.uv[ia], target.uv[ib], target.uv[ic], out weights)) return false;
            // Roundoff on a very narrow chart must not extrapolate beyond its surface.
            weights = Vector3.Max(weights, Vector3.zero);
            weights /= weights.x + weights.y + weights.z;
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
                lit = albedoLinear * (beauty.SampleLightmap(source.lightmaps[lightmapId], uv2, n) + beauty.Direct(p, n, layer, lightmapped: true)) + emission;
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

        internal static Cage BuildCage(RemeshNative.Geometry target, float distance, float smoothing, TriangleBvh source,
            Maps diag = null, CancellationToken token = default)
            => BuildCageWithFacing(target, distance, smoothing, source, null, null, diag, token);

        // The fitted reach must look for the same source faces that the bake can
        // project onto. A nearer back face must not hide an eligible front face.
        internal static Cage BuildCageWithFacing(RemeshNative.Geometry target, float distance, float smoothing, TriangleBvh source,
            Vector3[] sourceNormals, bool[] eitherSide, Maps diag = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var positions = target.positions; var indices = target.indices;
            int corners = indices.Length, faces = corners / 3;
            // Bit-exact position weld: xatlas and the simplifier copy coordinates exactly.
            var slots = MeshGeometry.WeldPositions(positions, out int positionCount);
            // Sides: per position a linked list of clusters; a corner joins the cluster
            // whose running sum its face normal agrees with best (within 120°), else
            // opens a new one. A sum only ever grows (every member has a positive dot
            // with it), so it never cancels the way a plain position weld does.
            var firstSide = new int[positionCount];
            for (int i = 0; i < firstSide.Length; ++i) firstSide[i] = -1;
            var nextSide = new System.Collections.Generic.List<int>();
            var sideSum = new System.Collections.Generic.List<Vector3>();
            var sideVertex = new System.Collections.Generic.List<int>();
            var cornerSide = new int[corners];
            var faceNormal = new Vector3[faces];
            for (int f = 0; f < faces; ++f) {
                if ((f & 255) == 0) token.ThrowIfCancellationRequested();
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
                    Vector3 contribution = cross * MeshGeometry.CornerAngle(e1, e2);
                    int best = -1; float bestDot = float.NegativeInfinity;
                    for (int sd = firstSide[slot]; sd >= 0; sd = nextSide[sd]) {
                        Vector3 sum = sideSum[sd];
                        float dot = sum.sqrMagnitude > 1e-30f && fn.sqrMagnitude > 0f ? Vector3.Dot(MeshGeometry.UnitDirection(sum), fn) : 1f;
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
                sideDir[sd] = sideSum[sd].sqrMagnitude > 1e-30f ? MeshGeometry.UnitDirection(sideSum[sd]) : Vector3.zero;
            // Laplacian smoothing over the SIDE connectivity: the corners of a face all
            // sit on compatible sides, so the pass flows through chart borders and
            // creases of one surface and never across to the other side of a sheet.
            var welded = new RemeshNative.Geometry { normals = (Vector3[])sideDir.Clone(), indices = cornerSide };
            RemeshNative.SmoothNormals(welded, smoothing);
            var cage = new Cage { directions = new Vector3[corners], reach = new float[corners], side = cornerSide,
                positions = positionCount, sides = sideCount, distance = distance };
            var deviated = new bool[positions.Length];
            for (int c = 0; c < corners; ++c) {
                if ((c & 255) == 0) token.ThrowIfCancellationRequested();
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
            if (source != null && distance > 0f)
                FitReach(cage, welded.normals, sideDir, sideVertex, positions, source, sourceNormals, eitherSide, token);
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
            Vector3[] positions, TriangleBvh source, Vector3[] sourceNormals, bool[] eitherSide, CancellationToken token)
        {
            float distance = cage.distance, range = distance * Cage.FitRange;
            int sides = sideDir.Length;
            var need = new float[sides];
            // Both searches need dot(source normal, cage direction) >= 0, as
            // the bake casts inward from the outer cage. Reversing only the
            // outward search's filter normals keeps that eligibility fixed.
            Vector3[] outwardNormals = sourceNormals == null ? null : Array.ConvertAll(sourceNormals, normal => -normal);
            for (int sd = 0; sd < sides; ++sd) {
                if ((sd & 255) == 0) token.ThrowIfCancellationRequested();
                Vector3 d = smoothed[sd].sqrMagnitude > 1e-20f ? smoothed[sd] : sideDir[sd];
                Vector3 p = positions[sideVertex[sd]];
                float found = -1f;
                if (d.sqrMagnitude > 1e-20f) {
                    float eps = distance * 1e-3f;
                    var outward = sourceNormals == null ? source.Raycast(p + d * eps, d, range) :
                        source.RaycastFacingFiltered(p + d * eps, d, range, outwardNormals, eitherSide);
                    var inward = sourceNormals == null ? source.Raycast(p - d * eps, -d, range) :
                        source.RaycastFacingFiltered(p - d * eps, -d, range, sourceNormals, eitherSide);
                    if (outward.triangleIndex >= 0) found = outward.t + eps;
                    if (inward.triangleIndex >= 0 && (found < 0f || inward.t + eps < found)) found = inward.t + eps;
                }
                if (found < 0f) {
                    var nearest = sourceNormals == null ? source.FindNearest(p, range) :
                        source.FindNearestNormalFiltered(p, d, sourceNormals, 0f, range, eitherSide);
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

        internal static void Evaluate(RemeshSource source, int face, Vector3 w, Vector3 targetNormal, Vector4 targetTangent,
            out Color color, out Color normal, out Color metal, out Color ao, out Color emission)
            => Evaluate(source, face, w, targetNormal, targetTangent, false, out color, out normal, out metal, out ao, out emission);

        // vertexTint multiplies the albedo by the interpolated source vertex color (RGB,
        // taken as linear, the way vertex-tinting shaders read it).
        internal static void Evaluate(RemeshSource source, int face, Vector3 w, Vector3 targetNormal, Vector4 targetTangent, bool vertexTint,
            out Color color, out Color normal, out Color metal, out Color ao, out Color emission)
            => EvaluateProjected(source, face, w, targetNormal, targetTangent, Quaternion.identity, vertexTint,
                out color, out normal, out metal, out ao, out emission);

        static void EvaluateProjected(RemeshSource source, int face, Vector3 w, Vector3 targetNormal, Vector4 targetTangent,
            Quaternion transport, bool vertexTint, out Color color, out Color normal, out Color metal, out Color ao, out Color emission)
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
            var n = transport * SourceNormal(source, face, w, true);
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

        internal static Vector3 SourceNormal(RemeshSource source, int face, Vector3 w, bool useNormalMap)
        {
            int a = source.indices[face * 3], b = source.indices[face * 3 + 1], c = source.indices[face * 3 + 2];
            var n = MeshGeometry.UnitDirection(source.normals[a] * w.x + source.normals[b] * w.y + source.normals[c] * w.z);
            if (n.sqrMagnitude < 1e-12f)
                n = MeshGeometry.UnitDirection(Vector3.Cross(source.positions[b] - source.positions[a], source.positions[c] - source.positions[a]));
            var surface = source.materials[source.faceMaterials[face]];
            if (!useNormalMap || surface.normal.image == null) return n;
            Vector2 uv = source.uv[a] * w.x + source.uv[b] * w.y + source.uv[c] * w.z;
            var tangent = source.tangents[a] * w.x + source.tangents[b] * w.y + source.tangents[c] * w.z;
            var sample = surface.normal.Sample(uv, new Color(.5f, .5f, 1));
            float nx = (sample.r * 2 - 1) * surface.normalScale, ny = (sample.g * 2 - 1) * surface.normalScale;
            Basis(n, tangent, out var t, out var bAxis);
            return MeshGeometry.UnitDirection(t * nx + bAxis * ny + n * Mathf.Sqrt(Mathf.Max(0, 1 - nx * nx - ny * ny)));
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
            var directions = MeshGeometry.SphereDirections(probes);   // evenly spread, deterministic
            for (int i = 0; i < probes; ++i) {
                var dir = directions[i];
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

        // Keep the atlas padding + additional circular dilation mask, but select
        // the receiving shell from the nearest ORIGINAL covered texel. Values at
        // these pixels are generated through the surface, not copied from a seed.
        internal static int[] BuildReceivers(int[] owners, int size, int padding, int dilationRadius, CancellationToken token)
        {
            var padded = PaddingSeeds(owners, size, padding, token);
            var receivers = TextureDilation.NearestFilled((int[])owners.Clone(), size, token);
            var paddedNearest = dilationRadius > 0 ? TextureDilation.NearestFilled(padded, size, token) : null;
            long limit = (long)dilationRadius * dilationRadius;
            for (int y = 0; y < size; ++y) {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < size; ++x) {
                    int i = y * size + x;
                    bool filled = paddedNearest != null ? paddedNearest[i] == i : padded[i] >= 0;
                    if (!filled && paddedNearest != null && paddedNearest[i] >= 0) {
                        int seed = paddedNearest[i];
                        long dx = x - seed % size, dy = y - seed / size;
                        filled = dx * dx + dy * dy <= limit;
                    }
                    int nearest = receivers[i];
                    receivers[i] = owners[i] >= 0 ? owners[i] : filled && nearest >= 0 ? owners[nearest] : -1;
                }
            }
            return receivers;
        }

        // Geometry-free utility retained for callers that only have pixels.
        internal static void PadAndDilate(Maps maps, int[] owners, int padding, int dilationRadius, CancellationToken token)
        {
            var padded = Pad(maps, owners, padding, token);
            if (dilationRadius == 0) return;
            // Consume the padding mask as scratch. Its seeds include the padding
            // pixels, so the requested radius is additional to the atlas padding.
            var nearest = TextureDilation.NearestFilled(padded, maps.size, token);
            long limit = (long)dilationRadius * dilationRadius;
            for (int y = 0; y < maps.size; ++y) {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < maps.size; ++x) {
                    int i = y * maps.size + x, from = nearest[i];
                    if (from < 0 || from == i) continue;
                    long dx = x - from % maps.size, dy = y - from / maps.size;
                    if (dx * dx + dy * dy <= limit) CopyTexel(maps, from, i);
                }
            }
        }

        static void CopyTexel(Maps maps, int from, int to)
        {
            // Copy, never average: keep packed channels, tangent-space directions,
            // proxy alpha coverage and HDR emission intact.
            maps.color[to] = maps.color[from]; maps.normal[to] = maps.normal[from];
            maps.metal[to] = maps.metal[from]; maps.ao[to] = maps.ao[from]; maps.emission[to] = maps.emission[from];
        }

        static int[] Pad(Maps maps, int[] owners, int padding, CancellationToken token)
        {
            var nearest = PaddingSeeds(owners, maps.size, padding, token);
            for (int i = 0; i < owners.Length; ++i) {
                int from = nearest[i];
                if (owners[i] >= 0 || from < 0) continue;
                CopyTexel(maps, from, i);
            }
            return nearest;
        }

        static int[] PaddingSeeds(int[] owners, int size, int padding, CancellationToken token)
        {
            int count = owners.Length;
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
            return nearest;
        }
    }
}
