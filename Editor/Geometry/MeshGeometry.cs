using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The one home for the small geometry routines every projecting or baking tool
    /// needs — face normals, bit-exact position welding, evenly spread sample
    /// directions, 2D barycentrics, point–box distance. Pure math, thread-safe, no
    /// UnityEngine.Object access except the owned-mesh channel completion helper,
    /// so the array routines run inside the bake workers. <see cref="TriangleBvh"/>
    /// (3D) and <see cref="TriangleBvh2D"/> (UV space) are the spatial queries; this
    /// class is what feeds them and reads their answers.
    /// </summary>
    internal static class MeshGeometry
    {
        /// <summary>Principal extents, largest first, and the axis of least thickness.
        /// Uses unique geometric positions so UV seams do not bias the fit.</summary>
        internal struct OrientedBounds
        {
            internal Vector3 origin, min, max;
            internal Vector3[] axes;
            internal Vector3 Size => max - min;
            internal Vector3 Point(Vector3 local) => origin + axes[0] * local.x + axes[1] * local.y + axes[2] * local.z;
        }

        internal static Vector3 PrincipalExtents(List<Vector3> points, out Vector3 normal)
        {
            var bounds = PrincipalBounds(points);
            var size = bounds.Size;
            int smallest = size.x <= size.y ? 0 : 1;
            if (size.z < size[smallest]) smallest = 2;
            normal = points.Count > 0 ? bounds.axes[smallest] : Vector3.zero;
            var extents = new[] { size.x, size.y, size.z };
            Array.Sort(extents);
            return new Vector3(extents[2], extents[1], extents[0]);
        }

        /// <summary>PCA frame with tight projected bounds. Axes retain their handedness.</summary>
        internal static OrientedBounds PrincipalBounds(List<Vector3> points)
        {
            int n = points.Count;
            if (n == 0) return new OrientedBounds { axes = new[] { Vector3.right, Vector3.up, Vector3.forward } };
            Vector3 mean = Vector3.zero;
            foreach (var p in points) mean += p;
            mean /= n;
            // Covariance (symmetric 3×3), then Jacobi rotations to diagonalize it.
            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            foreach (var p in points) {
                double dx = p.x - mean.x, dy = p.y - mean.y, dz = p.z - mean.z;
                xx += dx * dx; yy += dy * dy; zz += dz * dz; xy += dx * dy; xz += dx * dz; yz += dy * dz;
            }
            var a = new double[3, 3] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 32; ++sweep) {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-24) break;
                for (int p = 0; p < 2; ++p)
                    for (int q = p + 1; q < 3; ++q) {
                        if (Math.Abs(a[p, q]) < 1e-30) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;
                        for (int k = 0; k < 3; ++k) {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - sn * akq; a[k, q] = sn * akp + c * akq;
                        }
                        for (int k = 0; k < 3; ++k) {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - sn * aqk; a[q, k] = sn * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; ++k) {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - sn * vkq; v[k, q] = sn * vkp + c * vkq;
                        }
                    }
            }
            var bounds = new OrientedBounds { origin = mean, axes = new Vector3[3] };
            for (int axis = 0; axis < 3; ++axis)
            {
                var dir = new Vector3((float)v[0, axis], (float)v[1, axis], (float)v[2, axis]);
                bounds.axes[axis] = dir;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var p in points)
                {
                    float d = Vector3.Dot(p - mean, dir);
                    lo = Mathf.Min(lo, d);
                    hi = Mathf.Max(hi, d);
                }
                bounds.min[axis] = lo;
                bounds.max[axis] = hi;
            }
            return bounds;
        }

        /// <summary>Triangle-surface proximity from vertex/face, edge/edge and
        /// edge/face intersection tests; independent of tessellation density.</summary>
        internal static bool TrianglesWithin(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 e, Vector3 f, float reachSq)
        {
            if ((a - TriangleBvh.ClosestPointOnTriangle(a, d, e, f, out _)).sqrMagnitude <= reachSq ||
                (b - TriangleBvh.ClosestPointOnTriangle(b, d, e, f, out _)).sqrMagnitude <= reachSq ||
                (c - TriangleBvh.ClosestPointOnTriangle(c, d, e, f, out _)).sqrMagnitude <= reachSq ||
                (d - TriangleBvh.ClosestPointOnTriangle(d, a, b, c, out _)).sqrMagnitude <= reachSq ||
                (e - TriangleBvh.ClosestPointOnTriangle(e, a, b, c, out _)).sqrMagnitude <= reachSq ||
                (f - TriangleBvh.ClosestPointOnTriangle(f, a, b, c, out _)).sqrMagnitude <= reachSq) return true;
            if (SegmentHitsTriangle(a, b, d, e, f) || SegmentHitsTriangle(b, c, d, e, f) || SegmentHitsTriangle(c, a, d, e, f) ||
                SegmentHitsTriangle(d, e, a, b, c) || SegmentHitsTriangle(e, f, a, b, c) || SegmentHitsTriangle(f, d, a, b, c)) return true;
            return SegmentDistanceSquared(a, b, d, e) <= reachSq || SegmentDistanceSquared(a, b, e, f) <= reachSq || SegmentDistanceSquared(a, b, f, d) <= reachSq ||
                SegmentDistanceSquared(b, c, d, e) <= reachSq || SegmentDistanceSquared(b, c, e, f) <= reachSq || SegmentDistanceSquared(b, c, f, d) <= reachSq ||
                SegmentDistanceSquared(c, a, d, e) <= reachSq || SegmentDistanceSquared(c, a, e, f) <= reachSq || SegmentDistanceSquared(c, a, f, d) <= reachSq;
        }

        static bool SegmentHitsTriangle(Vector3 a, Vector3 b, Vector3 p, Vector3 q, Vector3 r)
        {
            var direction = b - a;
            if (direction.sqrMagnitude == 0) return false;
            var frame = new TriangleBvh.RayFrame(direction);
            return TriangleBvh.Watertight(in frame, a, p, q, r, 1f, out _, out _);
        }

        static double Dot(Vector3 a, Vector3 b) => (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z;

        // Clamped segment/segment closest points; double intermediates avoid an
        // absolute parallelism threshold that would change with mesh scale.
        static double SegmentDistanceSquared(Vector3 p, Vector3 q, Vector3 r, Vector3 t)
        {
            var u = q - p; var v = t - r; var w = p - r;
            double a = Dot(u, u), b = Dot(u, v), c = Dot(v, v), d = Dot(u, w), e = Dot(v, w);
            double s, k;
            if (a == 0) { s = 0; k = c > 0 ? Math.Clamp(e / c, 0, 1) : 0; }
            else if (c == 0) { k = 0; s = Math.Clamp(-d / a, 0, 1); }
            else
            {
                double denominator = a * c - b * b;
                s = denominator > 0 ? Math.Clamp((b * e - c * d) / denominator, 0, 1) : 0;
                k = (b * s + e) / c;
                if (k < 0) { k = 0; s = Math.Clamp(-d / a, 0, 1); }
                else if (k > 1) { k = 1; s = Math.Clamp((b - d) / a, 0, 1); }
            }
            double x = w.x + u.x * s - v.x * k, y = w.y + u.y * s - v.y * k, z = w.z + u.z * s - v.z * k;
            return x * x + y * y + z * z;
        }

        // Unity's Vector3.Normalize applies an absolute 1e-5 length cutoff. Area
        // vectors of valid millimetre-scale triangles are much smaller than that.
        // Callers decide degeneracy before normalizing; preserve every nonzero direction.
        internal static Vector3 UnitDirection(Vector3 vector)
        {
            float length = vector.magnitude;
            return length > 0f ? vector / length : Vector3.zero;
        }

        internal static float CornerAngle(Vector3 u, Vector3 v)
        {
            var a = UnitDirection(u); var b = UnitDirection(v);
            if (a == Vector3.zero || b == Vector3.zero) return 0f;
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f));
        }

        /// <summary>Unit face normals from the winding (Unity front = Cross(b − a, c − a)); zero for degenerate faces.</summary>
        public static Vector3[] FaceNormals(Vector3[] positions, int[] indices)
        {
            var normals = new Vector3[indices.Length / 3];
            for (int f = 0; f < normals.Length; ++f) {
                int a = indices[f * 3], b = indices[f * 3 + 1], c = indices[f * 3 + 2];
                Vector3 n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                float length = n.magnitude;
                normals[f] = length > 1e-30f ? n / length : Vector3.zero;
            }
            return normals;
        }

        /// <summary>Area-weighted smooth normals, welded across identical positions.
        /// A cancelling fan uses its largest face; unused/degenerate vertices use up.</summary>
        internal static Vector3[] AveragedNormals(Vector3[] positions, int[] indices, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var slots = WeldPositions(positions, out int count);
            var sums = new Vector3[count]; var largest = new Vector3[count];
            for (int f = 0; f < indices.Length; f += 3) {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                int a = indices[f], b = indices[f + 1], c = indices[f + 2];
                var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                for (int k = 0; k < 3; ++k) {
                    int slot = slots[indices[f + k]];
                    sums[slot] += face;
                    if (face.sqrMagnitude > largest[slot].sqrMagnitude) largest[slot] = face;
                }
            }
            var result = new Vector3[positions.Length];
            for (int i = 0; i < result.Length; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                int slot = slots[i];
                var n = UnitDirection(sums[slot]);
                if (!UsableNormal(n)) n = UnitDirection(largest[slot]);
                result[i] = UsableNormal(n) ? n : Vector3.up;
            }
            return result;
        }

        internal static bool UsableNormal(Vector3 normal) =>
            !float.IsNaN(normal.x) && !float.IsNaN(normal.y) && !float.IsNaN(normal.z) &&
            !float.IsInfinity(normal.x) && !float.IsInfinity(normal.y) && !float.IsInfinity(normal.z) && normal.sqrMagnitude > 1e-12f;

        /// <summary>Preserve supplied normals, repairing only missing/invalid entries.</summary>
        internal static Vector3[] NormalsOrFallback(Vector3[] positions, int[] indices, Vector3[] normals, CancellationToken token = default)
        {
            if (normals == null || normals.Length != positions.Length) return AveragedNormals(positions, indices, token);
            bool valid = true;
            foreach (var n in normals) if (!UsableNormal(n)) { valid = false; break; }
            if (valid) return normals;
            var result = (Vector3[])normals.Clone(); var fallback = AveragedNormals(positions, indices, token);
            for (int i = 0; i < result.Length; ++i) if (!UsableNormal(result[i])) result[i] = fallback[i];
            return result;
        }

        /// <summary>Temporary planar UV0 from normalized local X/Y; flat axes map to 0.5.</summary>
        internal static Vector2[] NormalizedXYUv(Vector3[] positions, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var uv = new Vector2[positions.Length];
            if (positions.Length == 0) return uv;
            Vector3 mn = positions[0], mx = positions[0];
            for (int i = 0; i < positions.Length; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                mn = Vector3.Min(mn, positions[i]); mx = Vector3.Max(mx, positions[i]);
            }
            double width = (double)mx.x - mn.x, height = (double)mx.y - mn.y;
            for (int i = 0; i < uv.Length; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                uv[i] = new Vector2(width > 0 ? (float)(((double)positions[i].x - mn.x) / width) : .5f,
                    height > 0 ? (float)(((double)positions[i].y - mn.y) / height) : .5f);
            }
            return uv;
        }

        /// <summary>Complete an owned triangle mesh without replacing authored channels.</summary>
        internal static void EnsureMeshChannels(Mesh mesh)
        {
            var p = mesh.vertices;
            if (p.Length == 0) return;
            var n = mesh.normals;
            var repaired = NormalsOrFallback(p, mesh.triangles, n);
            if (!ReferenceEquals(n, repaired)) mesh.normals = repaired;
            if (mesh.uv.Length != p.Length) {
                MeshUvState.SetGeneratedUv(mesh, NormalizedXYUv(p));
            }
        }

        /// <summary>
        /// Welds vertices by bit-exact position: slot[i] is the index of vertex i's
        /// position among the distinct ones (0..count−1). Exact, not tolerance-based,
        /// because every producer in this package (xatlas, the simplifier, the capture)
        /// copies coordinates verbatim when it splits a vertex.
        /// </summary>
        public static int[] WeldPositions(Vector3[] positions, out int count)
        {
            var slots = new int[positions.Length];
            var map = new Dictionary<(int, int, int), int>(positions.Length);
            for (int i = 0; i < positions.Length; ++i) {
                var p = positions[i];
                var key = (BitConverter.SingleToInt32Bits(p.x), BitConverter.SingleToInt32Bits(p.y), BitConverter.SingleToInt32Bits(p.z));
                if (!map.TryGetValue(key, out int slot)) { slot = map.Count; map[key] = slot; }
                slots[i] = slot;
            }
            count = map.Count;
            return slots;
        }

        /// <summary>Transform an owned collision mesh, preserving outward winding under reflection.</summary>
        internal static void TransformCollisionMesh(Mesh mesh, Matrix4x4 matrix)
        {
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; ++i) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            var triangles = mesh.triangles;
            if (matrix.determinant < 0)
                for (int i = 0; i < triangles.Length; i += 3)
                    (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// count unit directions evenly spread over the sphere (Fibonacci / golden
        /// spiral), deterministic. Hemisphere sampling filters by dot(dir, normal).
        /// </summary>
        public static Vector3[] SphereDirections(int count)
        {
            var directions = new Vector3[count];
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < count; ++i) {
                float y = 1f - 2f * (i + 0.5f) / count;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float a = golden * i;
                directions[i] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
            }
            return directions;
        }

        /// <summary>Barycentric weights (u, v, w) of p in the 2D triangle abc, either winding; false when the triangle is degenerate.</summary>
        public static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 weights)
        {
            Vector2 ab = b - a, ac = c - a, ap = p - a;
            float det = ab.x * ac.y - ab.y * ac.x;
            if (Mathf.Abs(det) < 1e-15f) { weights = Vector3.zero; return false; }
            float v = (ap.x * ac.y - ap.y * ac.x) / det;
            float w = (ab.x * ap.y - ab.y * ap.x) / det;
            weights = new Vector3(1 - v - w, v, w);
            return true;
        }

        /// <summary>The axis-aligned box that encloses <paramref name="bounds"/> after <paramref name="matrix"/> (all eight corners carried through).</summary>
        public static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 c = bounds.center, e = bounds.extents;
            var mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int ix = -1; ix <= 1; ix += 2)
                for (int iy = -1; iy <= 1; iy += 2)
                    for (int iz = -1; iz <= 1; iz += 2)
                    {
                        Vector3 w = matrix.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(ix, iy, iz)));
                        mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w);
                    }
            return new Bounds((mn + mx) * 0.5f, mx - mn);
        }

        /// <summary>Distance between two boxes; 0 when they touch or overlap.</summary>
        public static float BoundsDistance(Bounds a, Bounds b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
            float dy = Mathf.Max(0f, Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y));
            float dz = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Squared distance from q to the box [min, max]; 0 inside.</summary>
        public static float SqDistToAabb(Vector3 q, Vector3 min, Vector3 max)
        {
            float dx = q.x < min.x ? min.x - q.x : (q.x > max.x ? q.x - max.x : 0f);
            float dy = q.y < min.y ? min.y - q.y : (q.y > max.y ? q.y - max.y : 0f);
            float dz = q.z < min.z ? min.z - q.z : (q.z > max.z ? q.z - max.z : 0f);
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
