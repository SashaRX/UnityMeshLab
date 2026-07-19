// MeshAreaHelper.cs — shared 3D surface-area math used by the Repack UI
// info labels and the Auto-from-texel-density resolution calculator.
//
// All inputs are assumed to be in mesh-local space and unit-meters (the
// Unity convention). Transform scale is intentionally not applied — that
// is an artist-side concern (the same FBX should bake the same lightmap
// regardless of where it is placed in the scene).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class MeshAreaHelper
    {
        /// <summary>
        /// Sum of triangle areas across all meshes, in mesh-local units
        /// (assumed meters). Null meshes and meshes with zero vertices
        /// are skipped silently. Sub-mesh triangle topology is honoured.
        /// </summary>
        internal static double ComputeTotal3DAreaMeters(IEnumerable<Mesh> meshes)
        {
            if (meshes == null) return 0.0;
            double total = 0.0;
            foreach (var m in meshes)
            {
                if (m == null) continue;
                var verts = m.vertices;
                if (verts == null || verts.Length == 0) continue;
                int subMeshCount = m.subMeshCount;
                for (int s = 0; s < subMeshCount; s++)
                {
                    var tris = m.GetTriangles(s);
                    if (tris == null) continue;
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        int i0 = tris[i];
                        int i1 = tris[i + 1];
                        int i2 = tris[i + 2];
                        if ((uint)i0 >= (uint)verts.Length ||
                            (uint)i1 >= (uint)verts.Length ||
                            (uint)i2 >= (uint)verts.Length) continue;
                        var p0 = verts[i0];
                        var p1 = verts[i1];
                        var p2 = verts[i2];
                        total += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// Compute the smallest power-of-two atlas resolution (clamped to
        /// [64, 4096]) that satisfies the requested texel density given
        /// total 3D area and the active UV coverage budget.
        ///
        /// needed = sqrt(area * density² / coverage)
        ///        = sqrt(area / coverage) * density
        ///
        /// Returned value is suitable for direct assignment to
        /// <c>RepackOptions.resolution</c>.
        /// </summary>
        internal static uint ComputeAutoResolution(double total3DArea, float texelsPerMeter, float coverage)
        {
            if (texelsPerMeter <= 0f) texelsPerMeter = 1f;
            if (coverage <= 0f) coverage = 1f;
            if (total3DArea <= 0.0) return 64u;

            double density = texelsPerMeter;
            double needed = Math.Sqrt(total3DArea * density * density / coverage);
            int ceil = (int)Math.Ceiling(needed);
            uint pow2 = NextPow2((uint)Mathf.Max(1, ceil));
            if (pow2 < 64u) pow2 = 64u;
            if (pow2 > 4096u) pow2 = 4096u;
            return pow2;
        }

        /// <summary>Smallest power of two ≥ v (for v ≥ 1).</summary>
        internal static uint NextPow2(uint v)
        {
            if (v <= 1u) return 1u;
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1u;
        }
    }
}
