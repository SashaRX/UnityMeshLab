using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The one home for the small geometry routines every projecting or baking tool
    /// needs — face normals, bit-exact position welding, evenly spread sample
    /// directions, 2D barycentrics, point–box distance. Pure math, thread-safe, no
    /// UnityEngine.Object access, so it runs inside the bake workers. <see cref="TriangleBvh"/>
    /// (3D) and <see cref="TriangleBvh2D"/> (UV space) are the spatial queries; this
    /// class is what feeds them and reads their answers.
    /// </summary>
    internal static class MeshGeometry
    {
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
