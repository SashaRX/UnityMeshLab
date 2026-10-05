using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Physical face neighbours retained independently of UV and normal vertex splits.</summary>
    internal static class RemeshSurfaceTopology
    {
        struct Edge
        {
            internal int first, second, count, balance;
        }

        struct FaceMatch
        {
            internal int face, rotation;
        }

        readonly struct PositionKey : IEquatable<PositionKey>, IComparable<PositionKey>
        {
            readonly int x, y, z;

            internal PositionKey(Vector3 position)
            {
                x = BitConverter.SingleToInt32Bits(position.x);
                y = BitConverter.SingleToInt32Bits(position.y);
                z = BitConverter.SingleToInt32Bits(position.z);
            }

            public bool Equals(PositionKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is PositionKey other && Equals(other);
            public override int GetHashCode() => unchecked(((x * 397) ^ y) * 397 ^ z);

            public int CompareTo(PositionKey other)
            {
                int comparison = x.CompareTo(other.x);
                if (comparison == 0) comparison = y.CompareTo(other.y);
                return comparison == 0 ? z.CompareTo(other.z) : comparison;
            }
        }

        // Canonical cyclic rotation preserves winding: reversed faces have different keys.
        readonly struct FaceKey : IEquatable<FaceKey>
        {
            readonly PositionKey a, b, c;

            FaceKey(PositionKey first, PositionKey second, PositionKey third)
            {
                a = first; b = second; c = third;
            }

            internal static FaceKey Create(Vector3 a, Vector3 b, Vector3 c, out int rotation)
            {
                var ka = new PositionKey(a); var kb = new PositionKey(b); var kc = new PositionKey(c);
                if (kb.CompareTo(ka) < 0 && kb.CompareTo(kc) < 0) {
                    rotation = 1; return new FaceKey(kb, kc, ka);
                }
                if (kc.CompareTo(ka) < 0 && kc.CompareTo(kb) < 0) {
                    rotation = 2; return new FaceKey(kc, ka, kb);
                }
                rotation = 0; return new FaceKey(ka, kb, kc);
            }

            internal FaceKey Reversed => new FaceKey(first: a, second: c, third: b);
            public bool Equals(FaceKey other) => a.Equals(other.a) && b.Equals(other.b) && c.Equals(other.c);
            public override bool Equals(object obj) => obj is FaceKey other && Equals(other);
            public override int GetHashCode() => unchecked(((a.GetHashCode() * 397) ^ b.GetHashCode()) * 397 ^ c.GetHashCode());
        }

        /// <summary>
        /// Slot face*3+k is the edge opposite barycentric corner k. Its value is the
        /// neighbour's corresponding slot, or -1 for a boundary/untrusted edge.
        /// Uses physical index identities, never position welding.
        /// </summary>
        internal static int[] Build(Vector3[] positions, int[] indices, CancellationToken token)
        {
            ValidateArrays(positions, indices);
            token.ThrowIfCancellationRequested();
            var neighbours = EmptyNeighbours(indices.Length, token);
            int faceCount = indices.Length / 3;
            var valid = new bool[faceCount];
            var keys = new FaceKey[faceCount];
            var edges = new Dictionary<(int, int), Edge>();
            for (int face = 0; face < faceCount; ++face) {
                if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                valid[face] = TryFace(positions, indices, face, out keys[face], out _);
                for (int corner = 0; corner < 3; ++corner) {
                    int a = indices[face * 3 + (corner + 1) % 3];
                    int b = indices[face * 3 + (corner + 2) % 3];
                    if (!InRange(a, positions.Length) || !InRange(b, positions.Length) || a == b) continue;
                    var key = a < b ? (a, b) : (b, a);
                    int slot = face * 3 + corner;
                    if (!edges.TryGetValue(key, out var edge)) edge = new Edge { first = slot, second = -1 };
                    else if (edge.count == 1) edge.second = slot;
                    ++edge.count; edge.balance += a < b ? 1 : -1;
                    edges[key] = edge;
                }
            }
            int processed = 0;
            foreach (var edge in edges.Values) {
                if ((processed++ & 1023) == 0) token.ThrowIfCancellationRequested();
                if (edge.count != 2 || edge.balance != 0) continue;
                int firstFace = edge.first / 3, secondFace = edge.second / 3;
                if (firstFace == secondFace || !valid[firstFace] || !valid[secondFace]) continue;
                // A reversed shared edge plus the same three nondegenerate positions
                // proves coincident, coplanar opposite faces: never cross to a back sheet.
                if (keys[firstFace].Equals(keys[secondFace].Reversed)) continue;
                neighbours[edge.first] = edge.second;
                neighbours[edge.second] = edge.first;
            }
            token.ThrowIfCancellationRequested();
            return neighbours;
        }

        /// <summary>
        /// Carries original indexed connectivity onto final split geometry. Exact
        /// oriented corner positions permit cyclic rotation, but never reversed
        /// winding, coincident ambiguous faces, or a jump over a deleted neighbour.
        /// </summary>
        internal static int[] Transfer(Vector3[] originalPositions, int[] originalIndices,
            RemeshNative.Geometry result, CancellationToken token)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            ValidateArrays(originalPositions, originalIndices);
            ValidateArrays(result.positions, result.indices);
            token.ThrowIfCancellationRequested();
            var originalNeighbours = Build(originalPositions, originalIndices, token);
            var originals = new Dictionary<FaceKey, FaceMatch>();
            for (int face = 0; face < originalIndices.Length / 3; ++face) {
                if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                if (!TryFace(originalPositions, originalIndices, face, out var key, out int rotation)) continue;
                if (originals.TryGetValue(key, out var match)) {
                    match.face = -1; originals[key] = match;
                }
                else originals.Add(key, new FaceMatch { face = face, rotation = rotation });
            }

            int resultFaceCount = result.indices.Length / 3;
            var resultToOriginal = EmptyNeighbours(resultFaceCount, token);
            var originalToResult = EmptyNeighbours(originalIndices.Length / 3, token);
            var cornerOffsets = new int[resultFaceCount];
            for (int face = 0; face < resultFaceCount; ++face) {
                if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                if (!TryFace(result.positions, result.indices, face, out var key, out int rotation) ||
                    !originals.TryGetValue(key, out var match) || match.face < 0) continue;
                int previous = originalToResult[match.face];
                if (previous == -2) continue;
                if (previous >= 0) {
                    resultToOriginal[previous] = -1;
                    originalToResult[match.face] = -2; // every copy of this result face is ambiguous
                    continue;
                }
                resultToOriginal[face] = match.face;
                originalToResult[match.face] = face;
                cornerOffsets[face] = (match.rotation - rotation + 3) % 3;
            }

            var neighbours = EmptyNeighbours(result.indices.Length, token);
            for (int face = 0; face < resultFaceCount; ++face) {
                if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                int originalFace = resultToOriginal[face];
                if (originalFace < 0) continue;
                for (int corner = 0; corner < 3; ++corner) {
                    int originalSlot = originalFace * 3 + (corner + cornerOffsets[face]) % 3;
                    int neighbour = originalNeighbours[originalSlot];
                    if (neighbour < 0) continue;
                    int otherFace = originalToResult[neighbour / 3];
                    if (otherFace < 0) continue;
                    int otherCorner = (neighbour % 3 - cornerOffsets[otherFace] + 3) % 3;
                    neighbours[face * 3 + corner] = otherFace * 3 + otherCorner;
                }
            }
            token.ThrowIfCancellationRequested();
            return neighbours;
        }

        static int[] EmptyNeighbours(int length, CancellationToken token)
        {
            var values = new int[length];
            for (int i = 0; i < values.Length; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                values[i] = -1;
            }
            return values;
        }

        static void ValidateArrays(Vector3[] positions, int[] indices)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (indices.Length % 3 != 0) throw new ArgumentException("Surface connectivity requires triangle indices.", nameof(indices));
        }

        static bool InRange(int vertex, int count) => (uint)vertex < (uint)count;
        static bool Finite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        static bool TryFace(Vector3[] positions, int[] indices, int face, out FaceKey key, out int rotation)
        {
            key = default; rotation = 0;
            int a = indices[face * 3], b = indices[face * 3 + 1], c = indices[face * 3 + 2];
            if (!InRange(a, positions.Length) || !InRange(b, positions.Length) || !InRange(c, positions.Length)) return false;
            var pa = positions[a]; var pb = positions[b]; var pc = positions[c];
            if (!Finite(pa) || !Finite(pb) || !Finite(pc) || !HasArea(pa, pb, pc)) return false;
            key = FaceKey.Create(pa, pb, pc, out rotation);
            return true;
        }

        // Double intermediates avoid float underflow/overflow and an absolute scale cutoff.
        static bool HasArea(Vector3 a, Vector3 b, Vector3 c)
        {
            double abx = (double)b.x - a.x, aby = (double)b.y - a.y, abz = (double)b.z - a.z;
            double acx = (double)c.x - a.x, acy = (double)c.y - a.y, acz = (double)c.z - a.z;
            double x = aby * acz - abz * acy, y = abz * acx - abx * acz, z = abx * acy - aby * acx;
            return x * x + y * y + z * z > 0;
        }
    }
}
