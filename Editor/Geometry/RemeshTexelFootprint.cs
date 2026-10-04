using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Integrates a texel over the connected target surface. UV cuts are
    /// crossed through trusted physical half-edges, never through atlas proximity.</summary>
    internal sealed class RemeshTexelFootprint
    {
        internal struct Sample
        {
            public int face;
            public Vector3 weights;
            public float area;
            public bool boundary;
            // Donor geometric frame to the initial receiving geometric frame.
            // Baker separately aligns the donor/receiver shading normals.
            public Quaternion transport;
        }

        const int NavigationLimit = 64;
        const int LocalFaceLimit = 512;
        const double InsideTolerance = 1e-10;
        readonly RemeshNative.Geometry target;
        readonly int[] neighbors;
        readonly int size, faceCount;
        readonly ConcurrentBag<Workspace> workspaces = new ConcurrentBag<Workspace>();
        int navigationFallbacks, navigationLimitHits, localFaceLimitHits, unfoldOverlapTexels;

        internal int NavigationFallbacks => Volatile.Read(ref navigationFallbacks);
        internal int NavigationLimitHits => Volatile.Read(ref navigationLimitHits);
        internal int LocalFaceLimitHits => Volatile.Read(ref localFaceLimitHits);
        internal int UnfoldOverlapTexels => Volatile.Read(ref unfoldOverlapTexels);

        internal RemeshTexelFootprint(RemeshNative.Geometry target, int[] neighbors, int size)
        {
            if (target == null || target.positions == null || target.uv == null || target.indices == null)
                throw new ArgumentException("A target surface with UVs is required.", nameof(target));
            if (target.indices.Length % 3 != 0 || target.uv.Length != target.positions.Length)
                throw new ArgumentException("Target channels do not describe triangles.", nameof(target));
            if (neighbors != null && neighbors.Length != target.indices.Length)
                throw new ArgumentException("Neighbors must be indexed by triangle corner.", nameof(neighbors));
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            this.target = target; this.neighbors = neighbors; this.size = size;
            faceCount = target.indices.Length / 3;
        }

        /// <summary>Appends samples whose areas add to one texel. Boundary samples
        /// extend the last reachable edge when the physical surface stops.</summary>
        internal void Gather(int receiverFace, int x, int y, int grid, List<Sample> output, CancellationToken token)
        {
            if ((uint)receiverFace >= faceCount) throw new ArgumentOutOfRangeException(nameof(receiverFace));
            if (grid <= 0) throw new ArgumentOutOfRangeException(nameof(grid));
            if (output == null) throw new ArgumentNullException(nameof(output));
            token.ThrowIfCancellationRequested();
            var initial = ActualTriangle(receiverFace);
            if (ContainsPixel(initial, x, y)) {
                double step = 1.0 / grid, area = step * step;
                for (int sy = 0; sy < grid; ++sy) {
                    token.ThrowIfCancellationRequested();
                    for (int sx = 0; sx < grid; ++sx) {
                        var point = new Point(x + (sx + .5) * step, y + (sy + .5) * step);
                        Barycentric(initial, point, out var weights);
                        output.Add(new Sample { face = receiverFace, weights = weights.Clamped(), area = (float)area, transport = Quaternion.identity });
                    }
                }
                return;
            }
            if (!workspaces.TryTake(out var work)) work = new Workspace();
            work.Reset();
            try {
                var center = new Point(x + .5, y + .5);
                bool navigated = Navigate(initial, center, work, token, out var anchor);
                if (!navigated) Interlocked.Increment(ref navigationFallbacks);
                // Far outside a stopped edge: no triangle can contribute to this
                // footprint, so a single edge sample avoids a useless grid of rays.
                if (!navigated && !IntersectsBox(anchor, x, y, x + 1, y + 1)) {
                    output.Add(BoundarySample(anchor, center, 1));
                    return;
                }
                BuildLocalPatch(anchor, x, y, work, token);
                double step = 1.0 / grid, cellArea = step * step;
                bool overlap = false;
                for (int sy = 0; sy < grid; ++sy) {
                    token.ThrowIfCancellationRequested();
                    for (int sx = 0; sx < grid; ++sx) {
                        double left = x + sx * step, bottom = y + sy * step;
                        double right = left + step, top = bottom + step;
                        int first = output.Count;
                        double covered = 0; var moment = new Point(0, 0);
                        foreach (var triangle in work.patch) {
                            if (!IntersectsBox(triangle, left, bottom, right, top) ||
                                !Clip(triangle, left, bottom, right, top, work, out double area, out var centroid)) continue;
                            if (!Barycentric(triangle, centroid, out var weights)) continue;
                            if (!((float)area > 0)) continue;
                            output.Add(new Sample { face = triangle.face, weights = weights.Clamped(), area = (float)area, transport = triangle.transport.ToUnity() });
                            covered += area; moment += centroid * area;
                        }
                        if (covered > cellArea) {
                            // A curved neighborhood can unfold over itself around a
                            // vertex. Preserve the cell's measure instead of counting
                            // its texel area twice; the local walk stays bounded.
                            double scale = cellArea / covered;
                            if (covered > cellArea * (1 + 1e-9)) overlap = true;
                            for (int i = first; i < output.Count; ++i) {
                                var sample = output[i]; sample.area *= (float)scale; output[i] = sample;
                            }
                        }
                        else if (covered < cellArea) {
                            double remaining = cellArea - covered;
                            if (remaining <= cellArea * 1e-12 && output.Count > first) {
                                // Do not turn harmless clipping roundoff into an
                                // extra projection ray or a false boundary diagnostic.
                                var sample = output[first]; sample.area += (float)remaining; output[first] = sample;
                                continue;
                            }
                            var cellCenter = new Point((left + right) * .5, (bottom + top) * .5);
                            // The first moment of the uncovered region, followed by
                            // projection onto a reachable edge, preserves linear
                            // variation along a straight open boundary.
                            var outside = (cellCenter * cellArea - moment) / remaining;
                            var boundary = ClosestTriangle(work.patch, anchor, outside);
                            output.Add(BoundarySample(boundary, outside, remaining));
                        }
                    }
                }
                if (overlap) Interlocked.Increment(ref unfoldOverlapTexels);
            }
            finally { workspaces.Add(work); }
        }

        /// <summary>Receiver UV0 coordinates to an inside or closest-edge frame anchor.</summary>
        internal Vector3 ReceiverWeights(int receiverFace, Vector2 receiverUV)
        {
            if ((uint)receiverFace >= faceCount) throw new ArgumentOutOfRangeException(nameof(receiverFace));
            var triangle = ActualTriangle(receiverFace);
            var point = new Point((double)receiverUV.x * size, (double)receiverUV.y * size);
            if (Barycentric(triangle, point, out var weights) && weights.Inside) return weights.Clamped();
            return ClosestWeights(triangle, point, out _).Clamped();
        }

        Triangle ActualTriangle(int face)
        {
            var a = target.uv[target.indices[face * 3]];
            var b = target.uv[target.indices[face * 3 + 1]];
            var c = target.uv[target.indices[face * 3 + 2]];
            return new Triangle { face = face, transport = Rotation.Identity, a = new Point((double)a.x * size, (double)a.y * size),
                b = new Point((double)b.x * size, (double)b.y * size), c = new Point((double)c.x * size, (double)c.y * size) };
        }

        bool Navigate(Triangle initial, Point center, Workspace work, CancellationToken token, out Triangle current)
        {
            current = initial;
            var start = (current.a + current.b + current.c) / 3;
            work.navigation.Add(current.face);
            for (int iteration = 0; iteration < NavigationLimit; ++iteration) {
                if ((iteration & 7) == 0) token.ThrowIfCancellationRequested();
                if (!Barycentric(current, center, out var end)) return false;
                if (end.Inside) return true;
                if (!Barycentric(current, start, out var begin)) return false;
                double first = double.PositiveInfinity; int edge = -1;
                for (int k = 0; k < 3; ++k) {
                    if (end.At(k) >= -InsideTolerance) continue;
                    double distance = begin.At(k) - end.At(k);
                    if (distance <= 0) continue;
                    double t = Math.Max(0, begin.At(k)) / distance;
                    if (t < first) { first = t; edge = k; }
                }
                if (edge < 0 || !Neighbor(current, edge, out var next) || work.navigation.Contains(next.face)) return false;
                start += (center - start) * Math.Min(1, first);
                current = next;
                work.navigation.Add(current.face);
            }
            if (Barycentric(current, center, out var last) && last.Inside) return true;
            Interlocked.Increment(ref navigationLimitHits);
            return false;
        }

        void BuildLocalPatch(Triangle anchor, int x, int y, Workspace work, CancellationToken token)
        {
            work.patch.Add(anchor); work.visited.Add(anchor.face);
            bool limit = false;
            for (int index = 0; index < work.patch.Count; ++index) {
                if ((index & 15) == 0) token.ThrowIfCancellationRequested();
                var triangle = work.patch[index];
                for (int edge = 0; edge < 3; ++edge) {
                    if (!Neighbor(triangle, edge, out var next) || work.visited.Contains(next.face) ||
                        !IntersectsBox(next, x, y, x + 1, y + 1)) continue;
                    if (work.patch.Count >= LocalFaceLimit) { limit = true; continue; }
                    work.visited.Add(next.face);
                    work.patch.Add(next);
                }
            }
            if (limit) Interlocked.Increment(ref localFaceLimitHits);
        }

        bool Neighbor(Triangle current, int opposite, out Triangle next)
        {
            next = default;
            if (neighbors == null) return false;
            int corner = current.face * 3 + opposite, link = neighbors[corner];
            if (link < 0 || link >= neighbors.Length || neighbors[link] != corner || link / 3 == current.face) return false;
            int neighborFace = link / 3, neighborOpposite = link % 3;
            int ca = (opposite + 1) % 3, cb = (opposite + 2) % 3;
            int na = (neighborOpposite + 1) % 3, nb = (neighborOpposite + 2) % 3;
            var a = Position(current.face, ca); var b = Position(current.face, cb);
            var c = Position(current.face, opposite); var d = Position(neighborFace, neighborOpposite);
            if (!a.Equals(Position(neighborFace, nb)) || !b.Equals(Position(neighborFace, na)) || c.Equals(d)) return false;
            var edge = b - a; double length = edge.Length;
            if (!(length > 0)) return false;
            var axis = edge / length;
            var currentNormal = Vector.Cross(edge, c - a).Unit();
            var neighborNormal = Vector.Cross(a - b, d - b).Unit();
            if (currentNormal.Length == 0 || neighborNormal.Length == 0 || Vector.Dot(currentNormal, neighborNormal) <= -1 + 1e-12) return false;
            var qa = current.At(ca); var qb = current.At(cb); Point third;
            if (UvEquals(current.face, ca, neighborFace, nb) && UvEquals(current.face, cb, neighborFace, na)) {
                // In an uncut UV chart retain its authored atlas metric exactly.
                // After a previous seam crossing this is the affine chart map to
                // the receiving chart's virtual coordinates.
                var actual = ActualTriangle(current.face);
                var uv = target.uv[target.indices[neighborFace * 3 + neighborOpposite]];
                if (!Barycentric(actual, new Point((double)uv.x * size, (double)uv.y * size), out var weights)) return false;
                third = current.a * weights.x + current.b * weights.y + current.c * weights.z;
            }
            else {
                // Intrinsic unfolding is the edge-axis rotation that brings the
                // neighbor's geometric normal onto this triangle's normal. Its
                // third vertex lies on the opposite side of the shared edge.
                double xc = Vector.Dot(c - a, axis), xd = Vector.Dot(d - a, axis);
                double hc = ((c - a) - axis * xc).Length, hd = ((d - a) - axis * xd).Length;
                if (!(hc > 0) || !(hd > 0)) return false;
                var along = qb - qa;
                var across = current.At(opposite) - qa - along * (xc / length);
                third = qa + along * (xd / length) - across * (hd / hc);
            }
            if (!third.Finite) return false;
            next.face = neighborFace;
            // UV metric preservation and normal transport are independent: an
            // uncut UV edge can still connect geometrically folded triangles.
            next.transport = (current.transport * Rotation.FromTo(neighborNormal, currentNormal)).Unit();
            next.Set(neighborOpposite, third); next.Set(na, qb); next.Set(nb, qa);
            return Barycentric(next, (next.a + next.b + next.c) / 3, out _);
        }

        Vector Position(int face, int corner) => new Vector(target.positions[target.indices[face * 3 + corner]]);

        bool UvEquals(int face, int corner, int otherFace, int otherCorner)
        {
            var a = target.uv[target.indices[face * 3 + corner]];
            var b = target.uv[target.indices[otherFace * 3 + otherCorner]];
            return a.x == b.x && a.y == b.y;
        }

        static bool ContainsPixel(Triangle triangle, int x, int y) =>
            Contains(triangle, new Point(x, y)) && Contains(triangle, new Point(x + 1, y)) &&
            Contains(triangle, new Point(x, y + 1)) && Contains(triangle, new Point(x + 1, y + 1));

        static bool Contains(Triangle triangle, Point point) => Barycentric(triangle, point, out var weights) && weights.Inside;

        static bool Barycentric(Triangle triangle, Point point, out Weights weights)
        {
            var ab = triangle.b - triangle.a; var ac = triangle.c - triangle.a; var ap = point - triangle.a;
            double determinant = Point.Cross(ab, ac);
            weights = default;
            if (determinant == 0 || !Finite(determinant)) return false;
            double y = Point.Cross(ap, ac) / determinant, z = Point.Cross(ab, ap) / determinant;
            weights = new Weights(1 - y - z, y, z);
            return weights.Finite;
        }

        static Weights ClosestWeights(Triangle triangle, Point point, out double distance)
        {
            if (Barycentric(triangle, point, out var inside) && inside.Inside) { distance = 0; return inside; }
            distance = double.PositiveInfinity; var best = new Weights(1, 0, 0);
            for (int edge = 0; edge < 3; ++edge) {
                int a = (edge + 1) % 3, b = (edge + 2) % 3;
                var start = triangle.At(a); var axis = triangle.At(b) - start;
                double length = axis.LengthSquared;
                double t = length > 0 ? Math.Max(0, Math.Min(1, Point.Dot(point - start, axis) / length)) : 0;
                double candidate = (point - start - axis * t).LengthSquared;
                if (candidate >= distance) continue;
                distance = candidate;
                best = edge == 0 ? new Weights(0, 1 - t, t) : edge == 1 ? new Weights(t, 0, 1 - t) : new Weights(1 - t, t, 0);
            }
            return best;
        }

        static Triangle ClosestTriangle(List<Triangle> patch, Triangle anchor, Point point)
        {
            var best = anchor; ClosestWeights(best, point, out double distance);
            foreach (var triangle in patch) {
                ClosestWeights(triangle, point, out double candidate);
                if (candidate >= distance) continue;
                best = triangle; distance = candidate;
            }
            return best;
        }

        static Sample BoundarySample(Triangle triangle, Point point, double area) => new Sample {
            face = triangle.face, weights = ClosestWeights(triangle, point, out _).Clamped(), area = (float)area, boundary = true,
            transport = triangle.transport.ToUnity() };

        static bool IntersectsBox(Triangle triangle, double left, double bottom, double right, double top) =>
            Math.Max(triangle.a.x, Math.Max(triangle.b.x, triangle.c.x)) >= left &&
            Math.Min(triangle.a.x, Math.Min(triangle.b.x, triangle.c.x)) <= right &&
            Math.Max(triangle.a.y, Math.Max(triangle.b.y, triangle.c.y)) >= bottom &&
            Math.Min(triangle.a.y, Math.Min(triangle.b.y, triangle.c.y)) <= top;

        static bool Clip(Triangle triangle, double left, double bottom, double right, double top,
            Workspace work, out double area, out Point centroid)
        {
            var polygon = work.polygon; var scratch = work.scratch;
            polygon[0] = triangle.a; polygon[1] = triangle.b; polygon[2] = triangle.c;
            int count = 3;
            for (int edge = 0; edge < 4 && count > 0; ++edge) {
                bool horizontal = edge >= 2, minimum = (edge & 1) == 0;
                double boundary = horizontal ? (minimum ? bottom : top) : (minimum ? left : right);
                int written = 0; var previous = polygon[count - 1];
                double previousDistance = ((horizontal ? previous.y : previous.x) - boundary) * (minimum ? 1 : -1);
                for (int i = 0; i < count; ++i) {
                    var current = polygon[i];
                    double currentDistance = ((horizontal ? current.y : current.x) - boundary) * (minimum ? 1 : -1);
                    if (currentDistance != 0 && previousDistance != 0 && (currentDistance >= 0) != (previousDistance >= 0))
                        scratch[written++] = previous + (current - previous) * (previousDistance / (previousDistance - currentDistance));
                    if (currentDistance >= 0) scratch[written++] = current;
                    previous = current; previousDistance = currentDistance;
                }
                count = written; var swap = polygon; polygon = scratch; scratch = swap;
            }
            area = 0; centroid = default;
            if (count < 3) return false;
            // A local fan avoids cancellation from absolute atlas coordinates and
            // gives the area centroid, not the arithmetic mean of polygon vertices.
            double twiceArea = 0; var moment = new Point(0, 0); var origin = polygon[0];
            for (int i = 1; i < count - 1; ++i) {
                var a = polygon[i] - origin; var b = polygon[i + 1] - origin;
                double cross = Point.Cross(a, b);
                twiceArea += cross; moment += (a + b) * cross;
            }
            if (twiceArea == 0 || !Finite(twiceArea)) return false;
            area = Math.Abs(twiceArea) * .5;
            centroid = origin + moment / (3 * twiceArea);
            return area > 0 && centroid.Finite;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        sealed class Workspace
        {
            public readonly HashSet<int> navigation = new HashSet<int>(NavigationLimit + 1);
            public readonly HashSet<int> visited = new HashSet<int>(LocalFaceLimit);
            public readonly List<Triangle> patch = new List<Triangle>(16);
            public readonly Point[] polygon = new Point[16], scratch = new Point[16];
            public void Reset()
            {
                patch.Clear();
                navigation.Clear(); visited.Clear();
            }
        }

        struct Triangle
        {
            public int face;
            public Point a, b, c;
            public Rotation transport;
            public Point At(int corner) => corner == 0 ? a : corner == 1 ? b : c;
            public void Set(int corner, Point point)
            {
                if (corner == 0) a = point; else if (corner == 1) b = point; else c = point;
            }
        }

        readonly struct Weights
        {
            public readonly double x, y, z;
            public Weights(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public double At(int corner) => corner == 0 ? x : corner == 1 ? y : z;
            public bool Inside => x >= -InsideTolerance && y >= -InsideTolerance && z >= -InsideTolerance;
            public bool Finite => RemeshTexelFootprint.Finite(x) && RemeshTexelFootprint.Finite(y) && RemeshTexelFootprint.Finite(z);
            public Vector3 Clamped()
            {
                double a = Math.Max(0, x), b = Math.Max(0, y), c = Math.Max(0, z), sum = a + b + c;
                if (!(sum > 0)) return new Vector3(1, 0, 0);
                var weights = new Vector3((float)(a / sum), (float)(b / sum), (float)(c / sum));
                return weights / (weights.x + weights.y + weights.z);
            }
        }

        readonly struct Point
        {
            public readonly double x, y;
            public Point(double x, double y) { this.x = x; this.y = y; }
            public double LengthSquared => x * x + y * y;
            public bool Finite => RemeshTexelFootprint.Finite(x) && RemeshTexelFootprint.Finite(y);
            public static Point operator +(Point a, Point b) => new Point(a.x + b.x, a.y + b.y);
            public static Point operator -(Point a, Point b) => new Point(a.x - b.x, a.y - b.y);
            public static Point operator *(Point a, double b) => new Point(a.x * b, a.y * b);
            public static Point operator /(Point a, double b) => new Point(a.x / b, a.y / b);
            public static double Dot(Point a, Point b) => a.x * b.x + a.y * b.y;
            public static double Cross(Point a, Point b) => a.x * b.y - a.y * b.x;
        }

        readonly struct Vector
        {
            public readonly double x, y, z;
            public Vector(Vector3 value) { x = value.x; y = value.y; z = value.z; }
            Vector(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public double Length => Math.Sqrt(x * x + y * y + z * z);
            public Vector Unit() { double length = Length; return length > 0 ? this / length : default; }
            public bool Equals(Vector other) => x == other.x && y == other.y && z == other.z;
            public static Vector operator -(Vector a, Vector b) => new Vector(a.x - b.x, a.y - b.y, a.z - b.z);
            public static Vector operator *(Vector a, double b) => new Vector(a.x * b, a.y * b, a.z * b);
            public static Vector operator /(Vector a, double b) => new Vector(a.x / b, a.y / b, a.z / b);
            public static double Dot(Vector a, Vector b) => a.x * b.x + a.y * b.y + a.z * b.z;
            public static Vector Cross(Vector a, Vector b) => new Vector(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        }

        readonly struct Rotation
        {
            readonly double x, y, z, w;
            Rotation(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
            public static Rotation Identity => new Rotation(0, 0, 0, 1);
            public static Rotation FromTo(Vector from, Vector to)
            {
                var axis = Vector.Cross(from, to);
                return new Rotation(axis.x, axis.y, axis.z, 1 + Vector.Dot(from, to)).Unit();
            }
            public Rotation Unit()
            {
                double length = Math.Sqrt(x * x + y * y + z * z + w * w);
                return length > 0 ? new Rotation(x / length, y / length, z / length, w / length) : Identity;
            }
            public Quaternion ToUnity() => new Quaternion((float)x, (float)y, (float)z, (float)w);
            public static Rotation operator *(Rotation a, Rotation b) => new Rotation(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }
    }
}
