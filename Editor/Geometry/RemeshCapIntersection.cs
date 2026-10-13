using System;
using System.Collections.Generic;
using BigInteger = System.Numerics.BigInteger;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Exact predicates over float32 coordinates. Shared vertices/edges are
    /// allowed only when the entire triangle contact lies on that shared simplex.</summary>
    internal static class RemeshCapIntersection
    {
        internal readonly struct Q
        {
            readonly BigInteger n, d;
            internal Q(BigInteger numerator) { n = numerator; d = BigInteger.One; }
            Q(BigInteger numerator, BigInteger denominator)
            {
                if (denominator.IsZero) throw new DivideByZeroException();
                if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
                var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
                n = numerator / gcd; d = denominator / gcd;
            }
            internal int Sign => n.Sign;
            internal int CompareTo(Q other) => (n * other.d - other.n * d).Sign;
            public static Q operator +(Q a, Q b) => new Q(a.n * b.d + b.n * a.d, a.d * b.d);
            public static Q operator -(Q a, Q b) => new Q(a.n * b.d - b.n * a.d, a.d * b.d);
            public static Q operator *(Q a, Q b) => new Q(a.n * b.n, a.d * b.d);
            public static Q operator /(Q a, Q b) => new Q(a.n * b.d, a.d * b.n);
        }

        // Every finite binary32 is an integer multiple of 2^-149. No epsilon or
        // float multiplication enters orientation/intersection decisions.
        static Q Coordinate(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value), exponent = (bits >> 23) & 255;
            if (exponent == 255) throw new ArgumentException("Cap positions must be finite.");
            BigInteger mantissa = bits & 0x7fffff;
            if (exponent != 0) mantissa = (mantissa + 0x800000) << (exponent - 1);
            return new Q(bits < 0 ? -mantissa : mantissa);
        }

        internal static Q[] Point(Vector3 p) => new[] { Coordinate(p.x), Coordinate(p.y), Coordinate(p.z) };
        static Q[] Sub(Q[] a, Q[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        static Q Dot(Q[] a, Q[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static Q[] Cross(Q[] a, Q[] b) => new[] {
            a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static Q[] At(Q[] a, Q[] b, Q t) => new[] {
            a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };
        static bool Same(Q[] a, Q[] b) => a[0].CompareTo(b[0]) == 0 && a[1].CompareTo(b[1]) == 0 && a[2].CompareTo(b[2]) == 0;
        internal static Q Orient(Q[] edgeStart, Q[] edgeEnd, Q[] point, int x, int y) =>
            (edgeEnd[x] - edgeStart[x]) * (point[y] - edgeStart[y]) - (edgeEnd[y] - edgeStart[y]) * (point[x] - edgeStart[x]);
        static bool OneSide(Q[] d) => d[0].Sign * d[1].Sign > 0 && d[0].Sign * d[2].Sign > 0;
        static int NonzeroAxis(Q[] p)
        {
            for (int i = 0; i < 3; ++i) {
                if (p[i].Sign != 0) return i;
            }
            return -1;
        }

        static List<Q[]> Section(Q[][] triangle, Q[] distances)
        {
            var result = new List<Q[]>();
            for (int i = 0; i < 3; ++i) {
                int j = (i + 1) % 3;
                if (distances[i].Sign == 0) result.Add(triangle[i]);
                if (distances[i].Sign * distances[j].Sign < 0)
                    result.Add(At(triangle[i], triangle[j], distances[i] / (distances[i] - distances[j])));
            }
            return result;
        }

        static bool Allowed(List<Q[]> contact, List<Q[]> shared)
        {
            if (contact.Count == 0) return true;
            if (shared.Count == 1) {
                foreach (var p in contact) {
                    if (!Same(p, shared[0])) return false;
                }
                return true;
            }
            if (shared.Count != 2) return false;
            var direction = Sub(shared[1], shared[0]); int axis = NonzeroAxis(direction);
            foreach (var p in contact) {
                var t = (p[axis] - shared[0][axis]) / direction[axis];
                if (t.Sign < 0 || t.CompareTo(new Q(1)) > 0 || !Same(p, At(shared[0], shared[1], t))) return false;
            }
            return true;
        }

        static bool Coplanar(Q[][] a, Q[][] b, Q[] normal, List<Q[]> shared)
        {
            int drop = NonzeroAxis(normal), x = (drop + 1) % 3, y = (drop + 2) % 3;
            int sign = Orient(b[0], b[1], b[2], x, y).Sign;
            var polygon = new List<Q[]>(a);
            for (int i = 0; i < 3 && polygon.Count > 0; ++i) {
                var start = b[i]; var end = b[(i + 1) % 3];
                var clipped = new List<Q[]>(); var previous = polygon[polygon.Count - 1];
                var dp = Orient(start, end, previous, x, y);
                foreach (var current in polygon) {
                    var dc = Orient(start, end, current, x, y);
                    if ((dc.Sign * sign >= 0) != (dp.Sign * sign >= 0)) clipped.Add(At(previous, current, dp / (dp - dc)));
                    if (dc.Sign * sign >= 0) clipped.Add(current);
                    previous = current; dp = dc;
                }
                polygon = clipped;
            }
            // Any area overlap, including an adjacent face folded onto the Cap,
            // fails Allowed: a shared edge cannot contain that polygon.
            return !Allowed(polygon, shared);
        }

        internal static bool Improper(Q[][] a, Q[][] b)
        {
            var na = Cross(Sub(a[1], a[0]), Sub(a[2], a[0]));
            var nb = Cross(Sub(b[1], b[0]), Sub(b[2], b[0]));
            if (NonzeroAxis(na) < 0 || NonzeroAxis(nb) < 0) throw new ArgumentException("Degenerate Cap contact input.");
            var da = new Q[3]; var db = new Q[3]; var shared = new List<Q[]>();
            for (int i = 0; i < 3; ++i) {
                da[i] = Dot(na, Sub(b[i], a[0])); db[i] = Dot(nb, Sub(a[i], b[0]));
                foreach (var p in b) if (Same(a[i], p)) { shared.Add(a[i]); break; }
            }
            if (OneSide(da) || OneSide(db)) return false;
            if (da[0].Sign == 0 && da[1].Sign == 0 && da[2].Sign == 0) return Coplanar(a, b, na, shared);
            int axis = NonzeroAxis(Cross(na, nb));
            if (axis < 0) return false; // distinct parallel planes
            var sa = Section(a, db); var sb = Section(b, da);
            if (sa.Count == 0 || sb.Count == 0) return false;
            sa.Sort((p, q) => p[axis].CompareTo(q[axis])); sb.Sort((p, q) => p[axis].CompareTo(q[axis]));
            var first = sa[0]; var last = sa[sa.Count - 1];
            var lo = first[axis].CompareTo(sb[0][axis]) > 0 ? first[axis] : sb[0][axis];
            var hi = last[axis].CompareTo(sb[sb.Count - 1][axis]) < 0 ? last[axis] : sb[sb.Count - 1][axis];
            if (lo.CompareTo(hi) > 0) return false;
            var contact = new List<Q[]>();
            if (first[axis].CompareTo(last[axis]) == 0) contact.Add(first);
            else {
                contact.Add(At(first, last, (lo - first[axis]) / (last[axis] - first[axis])));
                contact.Add(At(first, last, (hi - first[axis]) / (last[axis] - first[axis])));
            }
            return !Allowed(contact, shared);
        }
    }
}
