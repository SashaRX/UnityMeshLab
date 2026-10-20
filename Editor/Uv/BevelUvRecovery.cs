using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Develop a damaged boundary strip from the unchanged healthy chart. Each
    // new point is fixed by an existing full edge and the triangle's rest metric.
    internal static class BevelUvRecovery
    {
        /// <summary>Develops collapsed faces from immutable anchors; returns a complete candidate or null without changing the input.</summary>
        internal static Dictionary<int, Vector2> TryRecover(UvShell shell, int[] indices,
            Vector3[] positions, Dictionary<int, Vector2> original, CancellationToken token)
        {
            var pending = new HashSet<int>();
            var fixedVertices = new HashSet<int>();
            var edges = new Dictionary<(int, int), List<(int face, int opposite)>>();
            foreach (int face in shell.faceIndices)
            {
                token.ThrowIfCancellationRequested();
                int a = indices[face * 3], b = indices[face * 3 + 1], c = indices[face * 3 + 2];
                double metric = ReverseUvTransfer.TriangleAnisotropy(
                    positions[a], positions[b], positions[c], original[a], original[b], original[c]);
                // Only develop effectively collapsed faces. Moderately stretched
                // neighbours still provide a valid boundary and must stay fixed.
                if (!double.IsFinite(metric) || metric > 10000) pending.Add(face);
                else { fixedVertices.Add(a); fixedVertices.Add(b); fixedVertices.Add(c); }
                for (int k = 0; k < 3; ++k)
                {
                    int u = indices[face * 3 + k], v = indices[face * 3 + (k + 1) % 3];
                    var key = u < v ? (u, v) : (v, u);
                    if (!edges.TryGetValue(key, out var uses))
                    {
                        uses = new List<(int, int)>();
                        edges.Add(key, uses);
                    }
                    uses.Add((face, indices[face * 3 + (k + 2) % 3]));
                }
            }
            if (pending.Count == 0 || pending.Count == shell.faceIndices.Count) return null;
            var candidate = new Dictionary<int, Vector2>(original);
            var frontier = new Queue<int>();
            foreach (int face in shell.faceIndices)
                if (pending.Contains(face)) frontier.Enqueue(face);
            while (frontier.Count > 0 && pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                int face = frontier.Dequeue();
                if (!pending.Contains(face)
                    || !TryPlace(face, indices, positions, original, candidate, fixedVertices, edges, pending)) continue;
                pending.Remove(face);
                for (int k = 0; k < 3; ++k)
                {
                    int a = indices[face * 3 + k], b = indices[face * 3 + (k + 1) % 3];
                    foreach (var use in edges[a < b ? (a, b) : (b, a)])
                        if (pending.Contains(use.face)) frontier.Enqueue(use.face);
                }
            }
            return pending.Count == 0 ? candidate : null;
        }

        /// <summary>Places one free corner or validates an already placed closing face across a manifold, completed neighbour.</summary>
        static bool TryPlace(int face, int[] indices, Vector3[] positions,
            Dictionary<int, Vector2> original, Dictionary<int, Vector2> candidate,
            HashSet<int> fixedVertices, Dictionary<(int, int), List<(int face, int opposite)>> edges,
            HashSet<int> pending)
        {
            for (int k = 0; k < 3; ++k)
            {
                int a = indices[face * 3 + k], b = indices[face * 3 + (k + 1) % 3];
                int c = indices[face * 3 + (k + 2) % 3];
                if (!fixedVertices.Contains(a) || !fixedVertices.Contains(b)) continue;
                var uses = edges[a < b ? (a, b) : (b, a)];
                if (uses.Count != 2) continue;
                var neighbour = uses[0].face == face ? uses[1] : uses[0];
                if (pending.Contains(neighbour.face)) continue;
                // Neighbouring quads can place all three corners before this
                // closing face is visited. Validate that pose without moving it.
                var point = candidate[c];
                if (!fixedVertices.Contains(c) && !Develop(positions[a], positions[b], positions[c], candidate[a], candidate[b],
                    candidate[neighbour.opposite], out point)) continue;
                double metric = ReverseUvTransfer.TriangleAnisotropy(
                    positions[a], positions[b], positions[c], candidate[a], candidate[b], point);
                if (!double.IsFinite(metric) || metric > 4) continue;
                double before = Area(original[a], original[b], original[c]);
                double after = Area(candidate[a], candidate[b], point);
                if (!double.IsFinite(before) || !double.IsFinite(after)) continue;
                if (before != 0 && Math.Sign(before) != Math.Sign(after)) continue;
                candidate[c] = point;
                fixedVertices.Add(c);
                return true;
            }
            return false;
        }

        /// <summary>Unfolds the intrinsic 3D triangle across its existing UV edge, opposite the completed neighbour.</summary>
        static bool Develop(Vector3 a, Vector3 b, Vector3 c, Vector2 u, Vector2 v,
            Vector2 neighbour, out Vector2 point)
        {
            point = default;
            var frame = TransferUvQuality.WorldFrame(a, b, c, Matrix4x4.identity);
            double x = (double)v.x - u.x, y = (double)v.y - u.y;
            double side = Area(u, v, neighbour);
            if (!(frame.length > 0 && frame.y > 0) || side == 0) return false;
            double along = frame.x / frame.length;
            double height = frame.y / frame.length * -Math.Sign(side);
            point = new Vector2((float)(u.x + along * x - height * y),
                (float)(u.y + along * y + height * x));
            return float.IsFinite(point.x) && float.IsFinite(point.y);
        }

        /// <summary>Returns signed double UV area, promoting coordinates before subtraction to preserve narrow triangles.</summary>
        static double Area(Vector2 a, Vector2 b, Vector2 c) =>
            ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
    }
}
