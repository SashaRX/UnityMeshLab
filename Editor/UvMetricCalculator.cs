// UvMetricCalculator.cs — Computes per-triangle UV metrics: perimeter, area, world area
// Used for border repair quality gate and diagnostics

using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public static class UvMetricCalculator
    {
        /// <summary>
        /// Compute metrics for all triangles: UV perimeter, UV area, world area.
        /// </summary>
        public static TriangleMetrics[] ComputeAll(
            Vector3[] vertices, Vector2[] uvs, int[] triangles, int faceCount)
        {
            var metrics = new TriangleMetrics[faceCount];

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = triangles[f * 3];
                int i1 = triangles[f * 3 + 1];
                int i2 = triangles[f * 3 + 2];

                // UV perimeter
                Vector2 uv0 = uvs[i0], uv1 = uvs[i1], uv2 = uvs[i2];
                float d01 = (uv1 - uv0).magnitude;
                float d12 = (uv2 - uv1).magnitude;
                float d20 = (uv0 - uv2).magnitude;
                metrics[f].perimeterUV = d01 + d12 + d20;

                // UV area (signed, take abs)
                float cross = (uv1.x - uv0.x) * (uv2.y - uv0.y) -
                              (uv2.x - uv0.x) * (uv1.y - uv0.y);
                metrics[f].areaUV = Mathf.Abs(cross) * 0.5f;

                // World area
                Vector3 v0 = vertices[i0], v1 = vertices[i1], v2 = vertices[i2];
                Vector3 worldCross = Vector3.Cross(v1 - v0, v2 - v0);
                metrics[f].areaWorld = worldCross.magnitude * 0.5f;
            }

            return metrics;
        }

        /// <summary>
        /// Compute UV perimeter for a single triangle.
        /// </summary>
        public static float Perimeter(Vector2 uv0, Vector2 uv1, Vector2 uv2)
        {
            return (uv1 - uv0).magnitude +
                   (uv2 - uv1).magnitude +
                   (uv0 - uv2).magnitude;
        }

        /// <summary>
        /// Compute UV area for a single triangle (unsigned).
        /// </summary>
        public static float Area(Vector2 uv0, Vector2 uv1, Vector2 uv2)
        {
            float cross = (uv1.x - uv0.x) * (uv2.y - uv0.y) -
                          (uv2.x - uv0.x) * (uv1.y - uv0.y);
            return Mathf.Abs(cross) * 0.5f;
        }
    }
}
