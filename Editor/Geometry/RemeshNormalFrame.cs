using System;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>The interpolated TBN used by the material shader, rather than an
    /// orthonormal approximation. Array-only operations are safe on bake workers.</summary>
    internal static class RemeshNormalFrame
    {
        internal enum Mode { BuiltIn, Urp }

        internal readonly struct Frame
        {
            internal readonly Vector3 tangent, bitangent, normal;

            internal Frame(Vector3 tangent, Vector3 bitangent, Vector3 normal)
            {
                this.tangent = tangent; this.bitangent = bitangent; this.normal = normal;
            }

            // Normalizing a matrix's columns changes its inverse. Use Cramer's
            // rule on the actual columns, then normalize only the encoded vector.
            // The common |determinant| cancels during that final normalization.
            internal bool TryEncode(Vector3 direction, out Vector3 tangentNormal)
            {
                tangentNormal = Vector3.zero;
                var t = new Wide(tangent); var b = new Wide(bitangent); var n = new Wide(normal);
                var d = new Wide(direction);
                if (!t.IsFinite || !b.IsFinite || !n.IsFinite || !d.IsFinite || d.Length == 0) return false;
                var bxN = Wide.Cross(b, n);
                double determinant = Wide.Dot(t, bxN), scale = t.Length * b.Length * n.Length;
                if (!(scale > 0) || Math.Abs(determinant) <= scale * 1e-12) return false;
                var coefficients = new Wide(Wide.Dot(d, bxN), Wide.Dot(d, Wide.Cross(n, t)), Wide.Dot(d, Wide.Cross(t, b)));
                if (determinant < 0) coefficients = coefficients * -1;
                tangentNormal = coefficients.Unit();
                return tangentNormal != Vector3.zero;
            }

            internal Vector3 Decode(Vector3 tangentNormal)
            {
                return (new Wide(tangent) * tangentNormal.x + new Wide(bitangent) * tangentNormal.y + new Wide(normal) * tangentNormal.z).Unit();
            }

            internal Frame Rotated(Quaternion rotation) => new Frame(rotation * tangent, rotation * bitangent, rotation * normal);
        }

        internal static Frame Interpolate(RemeshNative.Geometry geometry, Vector4[] tangents, int face, Vector3 bary, Mode mode)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            return Interpolate(geometry.normals, tangents, geometry.indices, face, bary, mode);
        }

        internal static Frame Interpolate(Vector3[] normals, Vector4[] tangents, int[] indices, int face, Vector3 bary, Mode mode)
            => Interpolate(normals, tangents, indices, face, bary, mode, Matrix4x4.identity);

        internal static Frame Interpolate(RemeshNative.Geometry geometry, Vector4[] tangents, int face, Vector3 bary, Mode mode, Matrix4x4 objectToWorld)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            return Interpolate(geometry.normals, tangents, geometry.indices, face, bary, mode, objectToWorld);
        }

        internal static Frame Interpolate(Vector3[] normals, Vector4[] tangents, int[] indices, int face, Vector3 bary, Mode mode, Matrix4x4 objectToWorld)
        {
            if (normals == null || tangents == null || indices == null || normals.Length != tangents.Length || indices.Length % 3 != 0)
                throw new ArgumentException("Normal frame requires complete vertex channels and triangle indices.");
            if ((uint)face >= indices.Length / 3) throw new ArgumentOutOfRangeException(nameof(face));
            if (mode != Mode.BuiltIn && mode != Mode.Urp) throw new ArgumentOutOfRangeException(nameof(mode));
            var transform = new Transform(objectToWorld);
            if (!transform.Valid || !new Wide(bary).IsFinite) return default;
            Wide n = default, t = default, b = default; double sign = 0;
            for (int corner = 0; corner < 3; ++corner) {
                int vertex = indices[face * 3 + corner];
                if ((uint)vertex >= normals.Length) throw new ArgumentException("Normal frame index is outside its vertex channels.", nameof(indices));
                double weight = corner == 0 ? bary.x : corner == 1 ? bary.y : bary.z;
                var tangent = tangents[vertex];
                if (!new Wide(normals[vertex]).IsFinite || !new Wide(new Vector3(tangent.x, tangent.y, tangent.z)).IsFinite) return default;
                var vertexN = new Wide(transform.Normal(normals[vertex]));
                var vertexT = new Wide(transform.Direction(new Vector3(tangent.x, tangent.y, tangent.z)));
                double handedness = tangent.w * transform.Sign;
                if (!vertexN.IsFinite || !vertexT.IsFinite || !Finite(handedness)) return default;
                n += vertexN * weight; t += vertexT * weight; sign += handedness * weight;
                if (mode == Mode.BuiltIn) b += Wide.Cross(vertexN, vertexT) * (handedness * weight);
            }
            // URP LitForwardPass forms B at the fragment. Built-in Standard forms
            // B at each vertex and interpolates it independently (default config).
            if (mode == Mode.Urp) b = Wide.Cross(n, t) * sign;
            return new Frame(t.Single(), b.Single(), n.Single());
        }

        readonly struct Transform
        {
            readonly Wide x, y, z;
            readonly double determinant;
            internal bool Valid => x.IsFinite && y.IsFinite && z.IsFinite && Finite(determinant) && determinant != 0;
            internal double Sign => determinant < 0 ? -1 : 1;

            internal Transform(Matrix4x4 matrix)
            {
                x = new Wide(matrix.m00, matrix.m10, matrix.m20);
                y = new Wide(matrix.m01, matrix.m11, matrix.m21);
                z = new Wide(matrix.m02, matrix.m12, matrix.m22);
                determinant = Wide.Dot(x, Wide.Cross(y, z));
            }

            internal Vector3 Direction(Vector3 direction) => (x * direction.x + y * direction.y + z * direction.z).Unit();

            internal Vector3 Normal(Vector3 normal)
            {
                var cofactor = Wide.Cross(y, z) * normal.x + Wide.Cross(z, x) * normal.y + Wide.Cross(x, y) * normal.z;
                // Magnitude of the determinant cancels when normalizing M^-T*N.
                return (cofactor * Sign).Unit();
            }
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        readonly struct Wide
        {
            readonly double x, y, z;
            internal Wide(Vector3 value) : this(value.x, value.y, value.z) { }
            internal Wide(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            internal bool IsFinite => RemeshNormalFrame.Finite(x) && RemeshNormalFrame.Finite(y) && RemeshNormalFrame.Finite(z);
            internal double Length => Math.Sqrt(x * x + y * y + z * z);
            internal Vector3 Single() => new Vector3((float)x, (float)y, (float)z);
            internal Vector3 Unit()
            {
                double scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
                if (!IsFinite || !(scale > 0)) return Vector3.zero;
                double nx = x / scale, ny = y / scale, nz = z / scale;
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                return new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length));
            }
            internal static double Dot(Wide a, Wide b) => a.x * b.x + a.y * b.y + a.z * b.z;
            internal static Wide Cross(Wide a, Wide b) => new Wide(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            public static Wide operator +(Wide a, Wide b) => new Wide(a.x + b.x, a.y + b.y, a.z + b.z);
            public static Wide operator *(Wide a, double scale) => new Wide(a.x * scale, a.y * scale, a.z * scale);
        }
    }
}
