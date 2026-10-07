// FbxSpaceFit.cs — the map between an FBX mesh's control-point space and the space of
// the Unity mesh imported from it, recovered from the import itself rather than from
// the importer's settings. Axis conversion, unit scale, mirroring and baked geometric
// pivots all end up in one affine map; fitting it to the tagged import's corner
// positions (each Unity vertex knows its FBX corner) gives that map exactly, whatever
// Unity did. Geometry MeshLab created in the Unity mesh's space (generated LODs,
// collision, edited faces) goes back into the FBX through its inverse.
//
// Plain arrays in and out: no UnityEngine and no FBX SDK types.

using System;
using System.Collections.Generic;

namespace SashaRX.UnityMeshLab
{
    internal sealed class FbxSpaceFit
    {
        // unity = M · fbx + T, M row-major.
        readonly double[] m = new double[9];
        readonly double[] inverse = new double[9];
        readonly double[] translation = new double[3];
        // Unity positions the reference import has, with their exact control points: a
        // vertex MeshLab kept from the source goes back bit for bit.
        readonly Dictionary<FbxCornerMatch.PositionKey, (double x, double y, double z)> exact =
            new Dictionary<FbxCornerMatch.PositionKey, (double, double, double)>();

        /// <summary>Largest distance between a fitted point and the imported one, in Unity units.</summary>
        public double Residual { get; private set; }
        public double Determinant { get; private set; }

        FbxSpaceFit() { }

        /// <summary>
        /// Fits the map from the control points of an FBX mesh to its Unity import, one pair
        /// per control point the import has a vertex for. <paramref name="determinantSign"/>
        /// decides a mirror the points cannot (a flat mesh fits its mirror image equally well):
        /// -1 when the import reversed the polygon winding (see <see cref="WindingRelation"/>).
        /// Returns null with <paramref name="error"/> set when the import is not an affine
        /// image of the control points (skinned, blend-shaped, too few points).
        /// </summary>
        internal static FbxSpaceFit FitCorners(double[] controlPoints, int[] cornerControlPoint, float[] cornerPositions,
            int determinantSign, out string error)
        {
            if (controlPoints == null) throw new ArgumentNullException(nameof(controlPoints));
            if (cornerControlPoint == null) throw new ArgumentNullException(nameof(cornerControlPoint));
            if (cornerPositions == null) throw new ArgumentNullException(nameof(cornerPositions));
            var fbx = new List<double>();
            var unity = new List<float>();
            var seen = new HashSet<int>();
            int corners = Math.Min(cornerControlPoint.Length, cornerPositions.Length / 3);
            for (int c = 0; c < corners; c++)
            {
                if (float.IsNaN(cornerPositions[c * 3])) continue;
                int cp = cornerControlPoint[c];
                if (cp < 0 || cp * 3 + 2 >= controlPoints.Length || !seen.Add(cp)) continue;
                fbx.Add(controlPoints[cp * 3]); fbx.Add(controlPoints[cp * 3 + 1]); fbx.Add(controlPoints[cp * 3 + 2]);
                unity.Add(cornerPositions[c * 3]); unity.Add(cornerPositions[c * 3 + 1]); unity.Add(cornerPositions[c * 3 + 2]);
            }
            return Fit(fbx.ToArray(), unity.ToArray(), determinantSign, out error);
        }

        /// <summary>Fits unity ≈ M·fbx + T to paired points (xyz each); see <see cref="FitCorners"/>.</summary>
        internal static FbxSpaceFit Fit(double[] fbx, float[] unity, int determinantSign, out string error)
        {
            error = null;
            int n = Math.Min(fbx.Length, unity.Length) / 3;
            if (n < 3) { error = "fewer than three points to fit"; return null; }
            var moments = new Moments(fbx, unity, n);

            SymmetricEigen(moments.cpp, out var lp, out var vp);
            if (lp[0] <= 0 || lp[1] <= 1e-12 * lp[0]) { error = "the points are collinear"; return null; }

            var fit = new FbxSpaceFit();
            bool solved = lp[2] > 1e-8 * lp[0]
                ? Solve(moments.cup, moments.cpp, fit.m)
                : FitFlat(moments, lp, vp, determinantSign, fit.m);
            if (!solved) { error = "the fit is singular"; return null; }

            fit.Determinant = Det(fit.m);
            if (Math.Abs(fit.Determinant) < 1e-300 || !Invert(fit.m, fit.inverse)) { error = "the map is not invertible"; return null; }
            for (int r = 0; r < 3; r++)
                fit.translation[r] = moments.uc[r] - Row(fit.m, r, moments.pc[0], moments.pc[1], moments.pc[2]);

            double scale = Math.Sqrt((moments.cuu[0] + moments.cuu[4] + moments.cuu[8]) / n);
            for (int i = 0; i < n; i++)
            {
                fit.Residual = Math.Max(fit.Residual, fit.Distance(fbx, unity, i));
                scale = Math.Max(scale, Math.Max(Math.Abs(unity[i * 3]), Math.Max(Math.Abs(unity[i * 3 + 1]), Math.Abs(unity[i * 3 + 2]))));
                var key = new FbxCornerMatch.PositionKey(unity[i * 3], unity[i * 3 + 1], unity[i * 3 + 2]);
                if (!fit.exact.ContainsKey(key)) fit.exact[key] = (fbx[i * 3], fbx[i * 3 + 1], fbx[i * 3 + 2]);
            }
            // Unity computes in single precision: a few ulps of the coordinates' magnitude.
            if (fit.Residual > 1e-5 * scale + 1e-12)
            {
                error = $"the import is not an affine image of the control points (residual {fit.Residual:G3} at scale {scale:G3})";
                return null;
            }
            return fit;
        }

        // Centroids and centred second moments of the paired points (row-major 3×3).
        sealed class Moments
        {
            public readonly double[] pc = new double[3], uc = new double[3];
            public readonly double[] cpp = new double[9], cup = new double[9], cuu = new double[9];

            public Moments(double[] fbx, float[] unity, int n)
            {
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < 3; k++) { pc[k] += fbx[i * 3 + k]; uc[k] += unity[i * 3 + k]; }
                for (int k = 0; k < 3; k++) { pc[k] /= n; uc[k] /= n; }
                var p = new double[3];
                var u = new double[3];
                for (int i = 0; i < n; i++)
                {
                    for (int k = 0; k < 3; k++) { p[k] = fbx[i * 3 + k] - pc[k]; u[k] = unity[i * 3 + k] - uc[k]; }
                    Accumulate(cpp, p, p);
                    Accumulate(cup, u, p);
                    Accumulate(cuu, u, u);
                }
            }

            static void Accumulate(double[] m, double[] a, double[] b)
            {
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++) m[r * 3 + c] += a[r] * b[c];
            }
        }

        static double Row(double[] m, int r, double x, double y, double z) => m[r * 3] * x + m[r * 3 + 1] * y + m[r * 3 + 2] * z;

        // |M·fbx_i + T − unity_i|
        double Distance(double[] fbx, float[] unity, int i)
        {
            double d2 = 0;
            for (int r = 0; r < 3; r++)
            {
                double d = Row(m, r, fbx[i * 3], fbx[i * 3 + 1], fbx[i * 3 + 2]) + translation[r] - unity[i * 3 + r];
                d2 += d * d;
            }
            return Math.Sqrt(d2);
        }

        // A flat point set fixes the map only within its plane; the plane normal goes to the
        // imported plane's normal, scaled like the plane, on the side the mirror sign asks for.
        static bool FitFlat(Moments moments, double[] lp, double[] vp, int determinantSign, double[] m)
        {
            SymmetricEigen(moments.cuu, out var lu, out var vu);
            double s = Math.Sqrt((lu[0] + lu[1]) / (lp[0] + lp[1]));
            double w = lp[0];
            var normalP = new[] { vp[2], vp[5], vp[8] };
            var normalU = new[] { vu[2], vu[5], vu[8] };
            foreach (double sign in determinantSign >= 0 ? new[] { 1.0, -1.0 } : new[] { -1.0, 1.0 })
            {
                var a = (double[])moments.cpp.Clone();
                var b = (double[])moments.cup.Clone();
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        a[r * 3 + c] += w * normalP[r] * normalP[c];
                        b[r * 3 + c] += w * sign * s * normalU[r] * normalP[c];
                    }
                if (!Solve(b, a, m)) return false;
                if (determinantSign == 0 || Math.Sign(Det(m)) == Math.Sign(determinantSign)) return true;
            }
            return true;
        }

        /// <summary>An FBX control point for a Unity position: the reference's own when the import had it, else the inverse map.</summary>
        public void ToFbx(float x, float y, float z, out double fx, out double fy, out double fz)
        {
            if (exact.TryGetValue(new FbxCornerMatch.PositionKey(x, y, z), out var p)) { fx = p.x; fy = p.y; fz = p.z; return; }
            double dx = x - translation[0], dy = y - translation[1], dz = z - translation[2];
            fx = inverse[0] * dx + inverse[1] * dy + inverse[2] * dz;
            fy = inverse[3] * dx + inverse[4] * dy + inverse[5] * dz;
            fz = inverse[6] * dx + inverse[7] * dy + inverse[8] * dz;
        }

        /// <summary>A Unity direction (tangent, binormal) in FBX space, unit length: directions map by M, so back by its inverse.</summary>
        public void DirectionToFbx(float x, float y, float z, out double fx, out double fy, out double fz)
        {
            fx = inverse[0] * x + inverse[1] * y + inverse[2] * z;
            fy = inverse[3] * x + inverse[4] * y + inverse[5] * z;
            fz = inverse[6] * x + inverse[7] * y + inverse[8] * z;
            double len = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (len > 0) { fx /= len; fy /= len; fz /= len; }
        }

        /// <summary>A Unity normal in FBX space: normals map by the inverse transpose, so back by the transpose.</summary>
        public void NormalToFbx(float x, float y, float z, out double fx, out double fy, out double fz)
        {
            fx = m[0] * x + m[3] * y + m[6] * z;
            fy = m[1] * x + m[4] * y + m[7] * z;
            fz = m[2] * x + m[5] * y + m[8] * z;
            double len = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (len > 0) { fx /= len; fy /= len; fz /= len; }
        }

        /// <summary>
        /// +1 when the import's triangles run the way their FBX polygons do, -1 when the
        /// import reversed them (it mirrored the mesh), 0 when no triangle tells. Read from
        /// corner order alone: <paramref name="triangleCorners"/> holds the FBX corner of each
        /// imported triangle's vertices, -1 where unknown.
        /// </summary>
        internal static int WindingRelation(int[] polygonSizes, int[] triangleCorners)
        {
            int corners = 0;
            foreach (int size in polygonSizes) corners += size;
            var polygonOf = new int[corners];
            var start = new int[polygonSizes.Length];
            for (int p = 0, c = 0; p < polygonSizes.Length; p++)
            {
                start[p] = c;
                for (int k = 0; k < polygonSizes[p]; k++) polygonOf[c++] = p;
            }
            int votes = 0;
            for (int t = 0; t + 2 < triangleCorners.Length; t += 3)
            {
                int a = triangleCorners[t], b = triangleCorners[t + 1], c = triangleCorners[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= corners || b >= corners || c >= corners) continue;
                int p = polygonOf[a];
                if (polygonOf[b] != p || polygonOf[c] != p || a == b || b == c || a == c) continue;
                int size = polygonSizes[p];
                // Steps around the polygon a→b→c→a: one lap when the triangle follows the polygon, two when it runs against it.
                int laps = (Step(a, b, size) + Step(b, c, size) + Step(c, a, size)) / size;
                votes += laps == 1 ? 1 : -1;
            }
            return Math.Sign(votes);
        }

        static int Step(int from, int to, int size) => ((to - from) % size + size) % size;

        // ── 3×3 helpers (row-major) ──

        static double Det(double[] a)
            => a[0] * (a[4] * a[8] - a[5] * a[7]) - a[1] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * a[7] - a[4] * a[6]);

        static bool Invert(double[] a, double[] result)
        {
            double det = Det(a);
            if (det == 0 || double.IsNaN(det) || double.IsInfinity(det)) return false;
            result[0] = (a[4] * a[8] - a[5] * a[7]) / det;
            result[1] = (a[2] * a[7] - a[1] * a[8]) / det;
            result[2] = (a[1] * a[5] - a[2] * a[4]) / det;
            result[3] = (a[5] * a[6] - a[3] * a[8]) / det;
            result[4] = (a[0] * a[8] - a[2] * a[6]) / det;
            result[5] = (a[2] * a[3] - a[0] * a[5]) / det;
            result[6] = (a[3] * a[7] - a[4] * a[6]) / det;
            result[7] = (a[1] * a[6] - a[0] * a[7]) / det;
            result[8] = (a[0] * a[4] - a[1] * a[3]) / det;
            return true;
        }

        // m = b · a⁻¹
        static bool Solve(double[] b, double[] a, double[] m)
        {
            var inv = new double[9];
            if (!Invert(a, inv)) return false;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    m[r * 3 + c] = b[r * 3] * inv[c] + b[r * 3 + 1] * inv[3 + c] + b[r * 3 + 2] * inv[6 + c];
            return true;
        }

        // Jacobi rotations; eigenvalues descending, eigenvectors as the columns of vectors.
        static void SymmetricEigen(double[] matrix, out double[] values, out double[] vectors)
        {
            var a = (double[])matrix.Clone();
            var v = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            for (int sweep = 0; sweep < 64; sweep++)
            {
                double off = a[1] * a[1] + a[2] * a[2] + a[5] * a[5];
                double diag = a[0] * a[0] + a[4] * a[4] + a[8] * a[8];
                if (off <= 1e-30 * diag || off == 0) break;
                Rotate(a, v, 0, 1); Rotate(a, v, 0, 2); Rotate(a, v, 1, 2);
            }
            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (i, j) => a[j * 4].CompareTo(a[i * 4]));
            values = new double[3];
            vectors = new double[9];
            for (int k = 0; k < 3; k++)
            {
                values[k] = a[order[k] * 4];
                for (int r = 0; r < 3; r++) vectors[r * 3 + k] = v[r * 3 + order[k]];
            }
        }

        static void Rotate(double[] a, double[] v, int p, int q)
        {
            double apq = a[p * 3 + q];
            if (apq == 0) return;
            double theta = (a[q * 3 + q] - a[p * 3 + p]) / (2 * apq);
            double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
            double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
            for (int k = 0; k < 3; k++)
            {
                double akp = a[k * 3 + p], akq = a[k * 3 + q];
                a[k * 3 + p] = c * akp - s * akq;
                a[k * 3 + q] = s * akp + c * akq;
            }
            for (int k = 0; k < 3; k++)
            {
                double apk = a[p * 3 + k], aqk = a[q * 3 + k];
                a[p * 3 + k] = c * apk - s * aqk;
                a[q * 3 + k] = s * apk + c * aqk;
            }
            for (int k = 0; k < 3; k++)
            {
                double vkp = v[k * 3 + p], vkq = v[k * 3 + q];
                v[k * 3 + p] = c * vkp - s * vkq;
                v[k * 3 + q] = s * vkp + c * vkq;
            }
        }
    }
}
