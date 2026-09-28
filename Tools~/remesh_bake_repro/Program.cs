// Offline reproduction of RemeshBaker.Bake for the UvIslands normal-map artifacts.
// Exact 1:1 port of Rasterize/Inside/Project/Evaluate/Basis + Lengyel tangents
// (Unity RecalculateTangents equivalent). Synthetic source = displaced sphere with
// analytic normals; target = coarse version, chart-split UVs like xatlas output.
// Metrics: angular error of the baked map decoded through the same interpolated
// TBN a renderer would use, vs the analytic source normal at the sample point.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

static class Repro
{
    const int AtlasSize = 256;
    const int SampleGrid = 2;      // "bakeSamples = 4"
    const float ProjectionDistance = 0.8f;
    const int ChartCount = 6;      // longitude bands -> separate atlas rects (3x2)

    static void Main(string[] args)
    {
        var source = BuildSphere(40, 72);    // ~5.5k tris
        var targetBase = BuildSphere(20, 36); // ~1.4k tris
        var diag = Diagonal(source);
        int rowsT = 20, colsT = 36;

        var modes = new[] { "hardBorders(uvIslands)", "smoothBorders(nativePi)", "flatFaces(crease0)" };
        for (int modeIndex = 0; modeIndex < modes.Length; modeIndex++)
        {
            var mode = modes[modeIndex];
            var target = SplitIntoCharts(targetBase, rowsT, colsT, modeIndex);
            // Kernel check: apply the SmoothNormals port at several strengths and
            // measure how far the smoothed normals drift from the geometric truth
            // (welded cage) and from their pre-smoothing values.
            foreach (float s in new[] { 0f, 1f, 4f, 10f })
            {
                var test = new Mesh { positions = (Vector3[])target.positions.Clone(), normals = (Vector3[])target.normals.Clone(),
                    uv = (Vector2[])target.uv.Clone(), indices = (int[])target.indices.Clone() };
                test.cage = (Vector3[])target.cage.Clone();
                var pre = (Vector3[])test.normals.Clone();
                SmoothNormalsPort(test, s);
                double dotCage = 0, dotPre = 0; int nan = 0, deg = 0; int n = Math.Min(test.normals.Length, Math.Min(test.cage.Length, pre.Length));
                for (int i = 0; i < n; ++i)
                {
                    if (float.IsNaN(test.normals[i].X) || test.normals[i].LengthSquared() < 0.5f) { nan++; continue; }
                    dotCage += Vector3.Dot(test.normals[i], test.cage[i]);
                    dotPre += Vector3.Dot(test.normals[i], pre[i]);
                }
                Console.WriteLine($"[smooth] mode={mode} s={s}: mean dot(normals, cage)={dotCage / Math.Max(1, n - nan):F3}  mean dot(normals, pre)={dotPre / Math.Max(1, n - nan):F3}  nan/zero={nan}");
                if (s == 0) continue;
            }
            var tangents = LengyelTangents(target);
            var offsets = SampleOffsets(SampleGrid);
            var owners = Rasterize(target, AtlasSize, offsets);
            int covered = 0;
            for (int i = 0; i < owners.Length; i++) if (owners[i] >= 0) covered++;
            float distance = diag * ProjectionDistance;
            int misses = 0, fallbacks = 0, farHits = 0;
            double errSum = 0, errMax = 0; int errN = 0;
            var errBuckets = new int[4]; // <5deg, <20, <45, worse
            var map = new Vector3[AtlasSize * AtlasSize];
            bool[] hitMap = new bool[AtlasSize * AtlasSize];
            for (int y = 0; y < AtlasSize; y++)
            {
                var candidates = new int[9];
                for (int x = 0; x < AtlasSize; x++)
                {
                    int pixel = y * AtlasSize + x;
                    if (owners[pixel] < 0) continue;
                    int cc = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= AtlasSize || yy >= AtlasSize) continue;
                            int f = owners[yy * AtlasSize + xx];
                            if (f >= 0 && Array.IndexOf(candidates, f, 0, cc) < 0) candidates[cc++] = f;
                        }
                    Vector3 normalSum = Vector3.Zero; int hits = 0;
                    Vector3 worldSum = Vector3.Zero;
                    foreach (var off in offsets)
                    {
                        var uv = new Vector2((x + off.X) / AtlasSize, (y + off.Y) / AtlasSize);
                        int face = -1; Vector3 w = default;
                        for (int c = 0; c < cc && face < 0; c++)
                            if (Inside(target, candidates[c], uv, out w)) face = candidates[c];
                        if (face < 0) continue;
                        if (!Project(source, target, tangents, face, w, distance,
                                out var nTangent, out var usedFallback, ref farHits)) continue;
                        if (usedFallback) fallbacks++;
                        normalSum += nTangent;
                        // decode through the same frame a renderer uses at this texel
                        var n = Vector3.Normalize(Interp(target.normals, target.indices, face, w));
                        var t4 = Interp4(tangents, target.indices, face, w);
                        Basis(n, t4, out var tt, out var tb);
                        worldSum += tt * nTangent.X + tb * nTangent.Y + n * nTangent.Z;
                        hits++;
                    }
                    if (hits == 0) { misses++; continue; }
                    var nT = normalSum.LengthSquared() > 1e-12f ? Vector3.Normalize(normalSum) : new Vector3(0, 0, 1);
                    map[pixel] = nT; hitMap[pixel] = true;
                    if (worldSum.LengthSquared() < 1e-12f) continue; // guard NaN
                    var world = Vector3.Normalize(worldSum);
                    // ground truth: source normal at the nearest source point to p
                    var p = TexelPosition(target, owners, x, y);
                    var (sFace, sW) = BruteNearest(source, p);
                    if (sFace >= 0)
                    {
                        var truth = Vector3.Normalize(Interp(source.normals, source.indices, sFace, sW));
                        var deg = Math.Acos(Math.Clamp(Vector3.Dot(world, truth), -1, 1)) * 180 / Math.PI;
                        errSum += deg; errN++;
                        if (deg > errMax) errMax = deg;
                        if (deg < 5) errBuckets[0]++; else if (deg < 20) errBuckets[1]++;
                        else if (deg < 45) errBuckets[2]++; else errBuckets[3]++;
                    }
                }
            }
            Console.WriteLine($"== {mode} ==");
            Console.WriteLine($"covered={covered}  misses={misses}  nearestFallbacks={fallbacks}  farSideHits={farHits}");
            Console.WriteLine($"angular error vs truth: n={errN}  mean={errSum / Math.Max(1, errN):F2}deg  max={errMax:F1}deg");
            Console.WriteLine($"buckets deg: <5={errBuckets[0]}  5-20={errBuckets[1]}  20-45={errBuckets[2]}  >45={errBuckets[3]}");
            WritePpm($"map_{modeIndex}.ppm", map, hitMap);
        }
        Console.WriteLine("done");
    }

    // ── geometry ──

    sealed class Mesh
    {
        public Vector3[] positions, normals;
        public Vector2[] uv;
        public int[] indices;
        public Vector3[] cage; // smooth welded normals used as raycast directions
    }

    static Vector3 SurfacePoint(float theta, float phi)
    {
        const float radius = 1f, amp = 0.10f; const int kT = 7, kP = 5;
        float r = radius + amp * MathF.Sin(kT * theta) * MathF.Cos(kP * phi);
        return new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi)) * r;
    }

    static Vector3 AnalyticNormal(float theta, float phi)
    {
        float e = 1e-3f;
        var dT = SurfacePoint(theta + e, phi) - SurfacePoint(theta - e, phi);
        var dP = SurfacePoint(theta, phi + e) - SurfacePoint(theta, phi - e);
        var n = Vector3.Cross(dP, dT);
        if (Vector3.Dot(n, SurfacePoint(theta, phi)) < 0) n = -n;
        if (n.LengthSquared() < 1e-20f) return Vector3.Normalize(SurfacePoint(theta, phi)); // poles
        return Vector3.Normalize(n);
    }

    static Mesh BuildSphere(int rows, int cols)
    {
        int vcols = cols + 1;
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uvL = new List<Vector2>();
        for (int i = 0; i <= rows; i++)
        {
            float theta = MathF.PI * i / rows;
            for (int j = 0; j <= cols; j++)
            {
                float phi = 2 * MathF.PI * j / cols;
                pos.Add(SurfacePoint(theta, phi));
                nrm.Add(AnalyticNormal(theta, phi));
                uvL.Add(new Vector2((float)j / cols, (float)i / rows));
            }
        }
        var idx = new List<int>();
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
            {
                int a = i * vcols + j, b = a + 1, c = a + vcols, d = c + 1;
                idx.AddRange(new[] { a, c, b, b, c, d });
            }
        // meshopt emits triangles wound so cross(b-a, c-a) points outward; my grid
        // emits the opposite. Flip winding globally to match the production convention.
        {
            int a = idx[0], b = idx[1], c = idx[2];
            var fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            if (Vector3.Dot(fn, pos[a]) < 0)
                for (int i = 0; i < idx.Count; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        }
        return new Mesh { positions = pos.ToArray(), normals = nrm.ToArray(), uv = uvL.ToArray(), indices = idx.ToArray() };
    }

    // Split the grid mesh into ChartCount longitude charts, each in its own atlas
    // rect, duplicating border vertices (xatlas-style). hard=true -> SmoothWithinSplitVertices.
    static Mesh SplitIntoCharts(Mesh m, int rowsReal, int colsReal, int modeIndex)
    {
        int vcols = colsReal + 1;
        int cw = ChartCount, layout = 3;
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uvL = new List<Vector2>();
        var idx = new List<int>();
        var map = new Dictionary<long, int>();
        for (int c = 0; c < cw; c++)
        {
            map.Clear();
            int j0 = c * colsReal / cw, j1 = (c + 1) * colsReal / cw;
            float rx = (c % layout) / (float)layout, ry = (c / layout) / 2f;
            const float rw = 1f / 3, rh = 1f / 2;
            int AddVertex(int i, int j)
            {
                long key = (long)i * vcols + j;
                if (map.TryGetValue(key, out var id)) return id;
                id = pos.Count;
                int src = i * vcols + j;
                pos.Add(m.positions[src]);
                nrm.Add(m.normals[src]);
                float u = (j - j0) / MathF.Max(1, j1 - j0);
                uvL.Add(new Vector2(rx + 0.02f + u * (rw - 0.04f), ry + 0.02f + m.uv[src].Y * (rh - 0.04f)));
                map[key] = id;
                return id;
            }
            for (int i = 0; i < rowsReal; i++)
                for (int j = j0; j < j1; j++)
                {
                    int a = AddVertex(i, j), b = AddVertex(i, j + 1), cc = AddVertex(i + 1, j), d = AddVertex(i + 1, j + 1);
                    idx.AddRange(new[] { a, cc, b, b, cc, d });
                }
        }
        // keep the meshopt convention: cross(b-a, c-a) points outward
        {
            double outward = 0;
            for (int i = 0; i < idx.Count; i += 3)
            {
                var pa = pos[idx[i]]; var pb = pos[idx[i + 1]]; var pc = pos[idx[i + 2]];
                var fn = Vector3.Cross(pb - pa, pc - pa);
                outward += Vector3.Dot(fn, (pa + pb + pc) / 3f);
            }
            bool needFlip = outward < 0;
            Console.WriteLine($"[winding] aggregated cross·centroid = {outward:F1}  needFlip={needFlip}");
            if (needFlip)
                for (int i = 0; i < idx.Count; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        }
        var mesh = new Mesh { positions = pos.ToArray(), normals = nrm.ToArray(), uv = uvL.ToArray(), indices = idx.ToArray() };
        // Diagnostic: does the C# Cross(b-a, c-a) smoothing agree in direction with
        // the "native" smooth normals (analytic here, meshopt_generateNormals there)?
        var before = (Vector3[])mesh.normals.Clone();
        SmoothAcrossEverything(mesh);
        mesh.cage = (Vector3[])mesh.normals.Clone();
        if (modeIndex == 0)
        {
            // production order: smooth welded normals exist first (native crease=pi),
            // then SmoothWithinSplitVertices hardens the chart borders.
            SmoothWithinSplitVertices(mesh);
        }
        else if (modeIndex == 2)
        {
            // crease=0 worst case: every corner its own normal group (flat shading),
            // vertices split per face like the native crease-split output.
            var fp = new List<Vector3>(); var fn = new List<Vector3>(); var fu = new List<Vector2>();
            var fc = new List<Vector3>();
            var fi = new List<int>();
            for (int i = 0; i < mesh.indices.Length; i += 3)
            {
                int a = mesh.indices[i], b = mesh.indices[i + 1], c = mesh.indices[i + 2];
                var faceN = Vector3.Normalize(Vector3.Cross(mesh.positions[b] - mesh.positions[a], mesh.positions[c] - mesh.positions[a]));
                int i0 = fp.Count;
                fp.AddRange(new[] { mesh.positions[a], mesh.positions[b], mesh.positions[c] });
                fn.AddRange(new[] { faceN, faceN, faceN });
                fu.AddRange(new[] { mesh.uv[a], mesh.uv[b], mesh.uv[c] });
                fc.AddRange(new[] { mesh.cage[a], mesh.cage[b], mesh.cage[c] });
                fi.AddRange(new[] { i0, i0 + 1, i0 + 2 });
            }
            mesh.positions = fp.ToArray(); mesh.normals = fn.ToArray(); mesh.uv = fu.ToArray(); mesh.indices = fi.ToArray();
            mesh.cage = fc.ToArray();
        }
        float dotSum = 0; int dotN = 0, flipped = 0;
        int diagCount = Math.Min(before.Length, mesh.normals.Length);
        for (int i = 0; i < diagCount; i++)
        {
            float d = Vector3.Dot(Vector3.Normalize(before[i]), mesh.normals[i]);
            dotSum += d; dotN++;
            if (d < 0) flipped++;
        }
        Console.WriteLine($"[diag] modeIndex={modeIndex}: mean dot(pre-smooth, post-smooth normals) = {dotSum / Math.Max(1, dotN):F3}  flipped={flipped}/{dotN}");
        return mesh;
    }

    // exact port of RemeshNative.SmoothWithinSplitVertices
    static void SmoothWithinSplitVertices(Mesh g)
    {
        var sum = new Vector3[g.positions.Length];
        for (int i = 0; i < g.indices.Length; i += 3)
        {
            int a = g.indices[i], b = g.indices[i + 1], c = g.indices[i + 2];
            var n = Vector3.Cross(g.positions[b] - g.positions[a], g.positions[c] - g.positions[a]);
            sum[a] += n; sum[b] += n; sum[c] += n;
        }
        for (int i = 0; i < sum.Length; i++)
            if (sum[i].LengthSquared() > 1e-30f) g.normals[i] = Vector3.Normalize(sum[i]);
    }

    static void SmoothAcrossEverything(Mesh g)
    {
        var map = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < g.positions.Length; i++)
        {
            var k = ((int)MathF.Round(g.positions[i].X * 10000), (int)MathF.Round(g.positions[i].Y * 10000), (int)MathF.Round(g.positions[i].Z * 10000));
            if (!map.TryGetValue(k, out var list)) map[k] = list = new List<int>();
            list.Add(i);
        }
        var sum = new Vector3[g.positions.Length];
        for (int i = 0; i < g.indices.Length; i += 3)
        {
            int a = g.indices[i], b = g.indices[i + 1], c = g.indices[i + 2];
            var n = Vector3.Cross(g.positions[b] - g.positions[a], g.positions[c] - g.positions[a]);
            sum[a] += n; sum[b] += n; sum[c] += n;
        }
        foreach (var list in map.Values)
        {
            var total = Vector3.Zero;
            foreach (var i in list) total += sum[i];
            if (total.LengthSquared() > 1e-30f)
                foreach (var i in list) g.normals[i] = Vector3.Normalize(total);
        }
    }

    // Lengyel tangents — what Unity's RecalculateTangents implements.
    static Vector4[] LengyelTangents(Mesh m)
    {
        var tan1 = new Vector3[m.positions.Length];
        var tan2 = new Vector3[m.positions.Length];
        for (int i = 0; i < m.indices.Length; i += 3)
        {
            int i1 = m.indices[i], i2 = m.indices[i + 1], i3 = m.indices[i + 2];
            var v1 = m.positions[i1]; var v2 = m.positions[i2]; var v3 = m.positions[i3];
            var w1 = m.uv[i1]; var w2 = m.uv[i2]; var w3 = m.uv[i3];
            float x1 = v2.X - v1.X, x2 = v3.X - v1.X;
            float y1 = v2.Y - v1.Y, y2 = v3.Y - v1.Y;
            float z1 = v2.Z - v1.Z, z2 = v3.Z - v1.Z;
            float s1 = w2.X - w1.X, s2 = w3.X - w1.X;
            float t1 = w2.Y - w1.Y, t2 = w3.Y - w1.Y;
            float d = s1 * t2 - s2 * t1;
            if (MathF.Abs(d) < 1e-10f) continue;
            float r = 1f / d;
            var sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
            var tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);
            tan1[i1] += sdir; tan1[i2] += sdir; tan1[i3] += sdir;
            tan2[i1] += tdir; tan2[i2] += tdir; tan2[i3] += tdir;
        }
        var result = new Vector4[m.positions.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var n = m.normals[i];
            var t = tan1[i] - n * Vector3.Dot(n, tan1[i]);
            if (t.LengthSquared() < 1e-20f) t = new Vector3(1, 0, 0);
            t = Vector3.Normalize(t);
            float w = Vector3.Dot(Vector3.Cross(n, t), tan2[i]) < 0 ? -1 : 1;
            result[i] = new Vector4(t, w);
        }
        return result;
    }

    // 1:1 port of RemeshNative.SmoothNormals for kernel verification.
    static void SmoothNormalsPort(Mesh g, float smoothing)
    {
        if (smoothing <= 0) return;
        int passes = Math.Min(10, (int)Math.Ceiling(smoothing));
        var normals = g.normals;
        var indices = g.indices;
        var delta = new Vector3[normals.Length];
        var edges = new float[normals.Length];
        for (int pass = 0; pass < passes; ++pass)
        {
            float alpha = 0.5f * MathF.Min(1f, smoothing - pass);
            Array.Clear(delta, 0, delta.Length);
            Array.Clear(edges, 0, edges.Length);
            for (int i = 0; i < indices.Length; i += 3)
            {
                SmoothEdgePort(normals, delta, edges, indices[i], indices[i + 1]);
                SmoothEdgePort(normals, delta, edges, indices[i + 1], indices[i + 2]);
                SmoothEdgePort(normals, delta, edges, indices[i + 2], indices[i]);
            }
            for (int i = 0; i < normals.Length; ++i)
            {
                if (edges[i] <= 0) continue;
                Vector3 n = normals[i] + delta[i] * (alpha / edges[i]);
                float length = n.Length();
                if (length > 1e-12f) normals[i] = n / length;
            }
        }
    }

    static void SmoothEdgePort(Vector3[] normals, Vector3[] delta, float[] edges, int a, int b)
    {
        float dp = Vector3.Dot(normals[a], normals[b]);
        float w = dp > 0f ? dp * dp : 0f;
        Vector3 d = (normals[b] - normals[a]) * w;
        delta[a] += d; edges[a] += 1f;
        delta[b] -= d; edges[b] += 1f;
    }

    // ── exact bake ports ──

    static Vector2[] SampleOffsets(int grid)
    {
        var offsets = new Vector2[grid * grid];
        for (int y = 0; y < grid; y++)
            for (int x = 0; x < grid; x++)
                offsets[y * grid + x] = new Vector2((x + 0.5f) / grid, (y + 0.5f) / grid);
        return offsets;
    }

    static int[] Rasterize(Mesh target, int size, Vector2[] offsets)
    {
        var owners = new int[size * size];
        var centred = new bool[owners.Length];
        for (int i = 0; i < owners.Length; i++) owners[i] = -1;
        for (int face = 0; face < target.indices.Length / 3; face++)
        {
            int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
            var ua = target.uv[a]; var ub = target.uv[b]; var uc = target.uv[c];
            int x0 = Clamp((int)MathF.Floor(MathF.Min(ua.X, MathF.Min(ub.X, uc.X)) * size), 0, size - 1);
            int x1 = Clamp((int)MathF.Ceiling(MathF.Max(ua.X, MathF.Max(ub.X, uc.X)) * size), 0, size - 1);
            int y0 = Clamp((int)MathF.Floor(MathF.Min(ua.Y, MathF.Min(ub.Y, uc.Y)) * size), 0, size - 1);
            int y1 = Clamp((int)MathF.Ceiling(MathF.Max(ua.Y, MathF.Max(ub.Y, uc.Y)) * size), 0, size - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int pixel = y * size + x;
                    if (Inside(target, face, new Vector2((x + 0.5f) / size, (y + 0.5f) / size), out _))
                    { owners[pixel] = face; centred[pixel] = true; }
                    else if (offsets.Length > 1 && !centred[pixel] && owners[pixel] < 0)
                        foreach (var offset in offsets)
                            if (Inside(target, face, new Vector2((x + offset.X) / size, (y + offset.Y) / size), out _))
                            { owners[pixel] = face; break; }
                }
        }
        return owners;
    }

    static bool Inside(Mesh target, int face, Vector2 uv, out Vector3 w)
    {
        int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
        return Barycentric(uv, target.uv[a], target.uv[b], target.uv[c], out w) &&
            w.X >= -1e-6f && w.Y >= -1e-6f && w.Z >= -1e-6f;
    }

    static bool Project(Mesh source, Mesh target, Vector4[] tangents, int face, Vector3 w, float distance,
        out Vector3 nTangent, out bool usedFallback, ref int farHits)
    {
        int a = target.indices[face * 3], b = target.indices[face * 3 + 1], c = target.indices[face * 3 + 2];
        Vector3 p = target.positions[a] * w.X + target.positions[b] * w.Y + target.positions[c] * w.Z;
        Vector3 n = Vector3.Normalize(target.normals[a] * w.X + target.normals[b] * w.Y + target.normals[c] * w.Z);
        // raycast direction from the smooth welded cage; the tangent frame below
        // stays the hard island-border normal the final mesh shades with.
        Vector3 rayN = Vector3.Normalize(target.cage[a] * w.X + target.cage[b] * w.Y + target.cage[c] * w.Z);
        Vector4 tangent = tangents[a] * w.X + tangents[b] * w.Y + tangents[c] * w.Z;
        var hit = RaycastAll(source, p + rayN * distance, -rayN, distance * 2);
        int sourceFace = hit.triangleIndex;
        Vector3 sw = hit.barycentric;
        usedFallback = false;
        if (sourceFace < 0)
        {
            var nearest = BruteNearest(source, p, distance);
            sourceFace = nearest.sFace; sw = nearest.sW;
            usedFallback = true;
        }
        if (sourceFace < 0) { nTangent = default; return false; }
        if (hit.t > distance * 1.05f) farHits++;
        var ns = Vector3.Normalize(source.normals[source.indices[sourceFace * 3]] * sw.X +
                                   source.normals[source.indices[sourceFace * 3 + 1]] * sw.Y +
                                   source.normals[source.indices[sourceFace * 3 + 2]] * sw.Z);
        Basis(n, tangent, out var tt, out var tb);
        nTangent = new Vector3(Vector3.Dot(ns, tt), Vector3.Dot(ns, tb), Vector3.Dot(ns, n));
        return true;
    }

    static void Basis(Vector3 n, Vector4 tangent, out Vector3 t, out Vector3 b)
    {
        t = new Vector3(tangent.X, tangent.Y, tangent.Z);
        t = Vector3.Normalize(t - n * Vector3.Dot(t, n));
        if (t.LengthSquared() < 1e-10f) t = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        b = Vector3.Cross(n, t) * (tangent.W < 0 ? -1 : 1);
    }

    static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 weights)
    {
        Vector2 ab = b - a, ac = c - a, ap = p - a;
        float det = ab.X * ac.Y - ab.Y * ac.X;
        if (MathF.Abs(det) < 1e-15f) { weights = Vector3.Zero; return false; }
        float v = (ap.X * ac.Y - ap.Y * ac.X) / det;
        float w = (ab.X * ap.Y - ab.Y * ap.X) / det;
        weights = new Vector3(1 - v - w, v, w); return true;
    }

    static (int sFace, Vector3 sW) BruteNearest(Mesh m, Vector3 p, float maxDist = float.MaxValue)
    {
        int best = -1; float bestSq = maxDist * maxDist; Vector3 bestW = default;
        for (int f = 0; f < m.indices.Length / 3; f++)
        {
            var (cp, w) = ClosestOnTriangle(p, m, f);
            float d = (cp - p).LengthSquared();
            if (d < bestSq) { bestSq = d; best = f; bestW = w; }
        }
        return (best, bestW);
    }

    static (int triangleIndex, float t, Vector3 barycentric) RaycastAll(Mesh m, Vector3 origin, Vector3 dir, float maxDist)
    {
        int best = -1; float bestT = maxDist; float bu = 0, bv = 0;
        for (int f = 0; f < m.indices.Length / 3; f++)
        {
            int i0 = m.indices[f * 3], i1 = m.indices[f * 3 + 1], i2 = m.indices[f * 3 + 2];
            var a = m.positions[i0]; var b = m.positions[i1]; var c = m.positions[i2];
            var edge1 = b - a; var edge2 = c - a;
            var h = Vector3.Cross(dir, edge2);
            float det = Vector3.Dot(edge1, h);
            if (det > -1e-7f && det < 1e-7f) continue;
            float inv = 1f / det;
            var s = origin - a;
            float u = inv * Vector3.Dot(s, h);
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, edge1);
            float v = inv * Vector3.Dot(dir, q);
            if (v < 0 || u + v > 1) continue;
            float t = inv * Vector3.Dot(edge2, q);
            if (t >= 0 && t < bestT) { bestT = t; best = f; bu = u; bv = v; }
        }
        return (best, bestT, new Vector3(1 - bu - bv, bu, bv));
    }

    static (Vector3 point, Vector3 w) ClosestOnTriangle(Vector3 p, Mesh m, int f)
    {
        var a = m.positions[m.indices[f * 3]]; var b = m.positions[m.indices[f * 3 + 1]]; var c = m.positions[m.indices[f * 3 + 2]];
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return (a, new Vector3(1, 0, 0));
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return (b, new Vector3(0, 1, 0));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return (c, new Vector3(0, 0, 1));
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v0 = d1 / (d1 - d3); return (a + v0 * ab, new Vector3(1 - v0, v0, 0)); }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w0 = d2 / (d2 - d6); return (a + w0 * ac, new Vector3(1 - w0, 0, w0)); }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { float w0 = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (b + w0 * (c - b), new Vector3(0, 1 - w0, w0)); }
        float denom = 1f / (va + vb + vc);
        float sv = vb * denom, sw2 = vc * denom;
        return (a + sv * ab + sw2 * ac, new Vector3(1 - sv - sw2, sv, sw2));
    }

    static Vector3 Interp(Vector3[] arr, int[] idx, int face, Vector3 w) =>
        arr[idx[face * 3]] * w.X + arr[idx[face * 3 + 1]] * w.Y + arr[idx[face * 3 + 2]] * w.Z;
    static Vector4 Interp4(Vector4[] arr, int[] idx, int face, Vector3 w) =>
        arr[idx[face * 3]] * w.X + arr[idx[face * 3 + 1]] * w.Y + arr[idx[face * 3 + 2]] * w.Z;

    static Vector3 TexelPosition(Mesh target, int[] owners, int x, int y)
    {
        int face = owners[y * AtlasSize + x];
        if (Inside(target, face, new Vector2((x + 0.5f) / AtlasSize, (y + 0.5f) / AtlasSize), out var w))
            return Interp(target.positions, target.indices, face, w);
        return target.positions[target.indices[face * 3]];
    }

    static float Diagonal(Mesh m)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in m.positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mx - mn).Length();
    }

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    static void WritePpm(string path, Vector3[] map, bool[] hit)
    {
        using var f = File.CreateText(path);
        f.WriteLine($"P3 {AtlasSize} {AtlasSize} 255");
        for (int i = 0; i < map.Length; i++)
        {
            var c = hit[i] ? new Vector3(
                Math.Clamp(map[i].X * 0.5f + 0.5f, 0, 1),
                Math.Clamp(map[i].Y * 0.5f + 0.5f, 0, 1),
                Math.Clamp(map[i].Z * 0.5f + 0.5f, 0, 1)) : new Vector3(0, 0, 0);
            f.Write($"{(int)(c.X * 255)} {(int)(c.Y * 255)} {(int)(c.Z * 255)} ");
            if (i % 16 == 15) f.WriteLine();
        }
    }
}
