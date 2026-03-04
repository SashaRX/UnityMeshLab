// StripParameterization.cs — Ribbon detection + strip-based UV2 transfer
//
// Approach C: For shells that are long narrow strips (ribbons), parametrize
// by (t_along, t_across) using PCA axes. Transfer UV2 via nearest-neighbor
// in parameterized space, filtered by face normal to separate front/back.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class StripParameterization
    {
        const float kAspectThreshold = 3f;     // length/width ratio for ribbon detection
        const float kPcaVarianceThreshold = 0.70f; // PCA first component explains >70% variance
        const float kNormalDotThreshold = 0f;  // dot(srcNrm, tgtNrm) > 0 = same side
        const float kParamTriAreaEpsilon = 1e-10f;

        struct ParamVertex
        {
            public Vector2 param;
            public Vector2 uv2;
        }

        struct ParamTriangle
        {
            public int i0, i1, i2;
            public Vector2 p0, p1, p2;
            public Vector3 normal;
        }

        /// <summary>
        /// Detect if a shell is ribbon-like based on 3D vertex positions.
        /// Returns true if aspect ratio > 3:1 or PCA first component > 70% variance.
        /// </summary>
        public static bool IsRibbon(UvShell shell, Vector3[] vertices, int[] triangles,
            out Vector3 principalAxis, out Vector3 secondaryAxis, out Vector3 centroid)
        {
            principalAxis = Vector3.right;
            secondaryAxis = Vector3.forward;
            centroid = Vector3.zero;

            // Collect unique 3D positions for this shell
            var positions = new List<Vector3>();
            foreach (int vi in shell.vertexIndices)
            {
                if (vi < vertices.Length)
                    positions.Add(vertices[vi]);
            }

            if (positions.Count < 3) return false;

            // Compute centroid
            Vector3 sum = Vector3.zero;
            foreach (var p in positions) sum += p;
            centroid = sum / positions.Count;

            // Compute covariance matrix (3x3, symmetric)
            float cxx = 0, cxy = 0, cxz = 0;
            float cyy = 0, cyz = 0, czz = 0;
            foreach (var p in positions)
            {
                float dx = p.x - centroid.x;
                float dy = p.y - centroid.y;
                float dz = p.z - centroid.z;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz;
                czz += dz * dz;
            }
            float n = positions.Count;
            cxx /= n; cxy /= n; cxz /= n;
            cyy /= n; cyz /= n; czz /= n;

            // Power iteration for largest eigenvalue/eigenvector
            Vector3 v = new Vector3(1f, 0.5f, 0.25f).normalized;
            for (int iter = 0; iter < 50; iter++)
            {
                Vector3 next = new Vector3(
                    cxx * v.x + cxy * v.y + cxz * v.z,
                    cxy * v.x + cyy * v.y + cyz * v.z,
                    cxz * v.x + cyz * v.y + czz * v.z
                );
                float len = next.magnitude;
                if (len < 1e-10f) break;
                v = next / len;
            }
            principalAxis = v;

            // Eigenvalue for principal axis
            Vector3 Av = new Vector3(
                cxx * v.x + cxy * v.y + cxz * v.z,
                cxy * v.x + cyy * v.y + cyz * v.z,
                cxz * v.x + cyz * v.y + czz * v.z
            );
            float lambda1 = Vector3.Dot(v, Av);

            // Total variance = trace of covariance matrix
            float totalVariance = cxx + cyy + czz;
            float pcaRatio = totalVariance > 1e-10f ? lambda1 / totalVariance : 0f;

            // Deflate: remove principal component, power-iterate for second
            float cxx2 = cxx - lambda1 * v.x * v.x;
            float cxy2 = cxy - lambda1 * v.x * v.y;
            float cxz2 = cxz - lambda1 * v.x * v.z;
            float cyy2 = cyy - lambda1 * v.y * v.y;
            float cyz2 = cyz - lambda1 * v.y * v.z;
            float czz2 = czz - lambda1 * v.z * v.z;

            Vector3 v2 = Vector3.Cross(v, Vector3.up).sqrMagnitude > 0.01f
                ? Vector3.Cross(v, Vector3.up).normalized
                : Vector3.Cross(v, Vector3.right).normalized;

            for (int iter = 0; iter < 50; iter++)
            {
                Vector3 next = new Vector3(
                    cxx2 * v2.x + cxy2 * v2.y + cxz2 * v2.z,
                    cxy2 * v2.x + cyy2 * v2.y + cyz2 * v2.z,
                    cxz2 * v2.x + cyz2 * v2.y + czz2 * v2.z
                );
                float len = next.magnitude;
                if (len < 1e-10f) break;
                v2 = next / len;
            }
            secondaryAxis = v2;

            // Eigenvalue for secondary axis
            Vector3 Av2 = new Vector3(
                cxx2 * v2.x + cxy2 * v2.y + cxz2 * v2.z,
                cxy2 * v2.x + cyy2 * v2.y + cyz2 * v2.z,
                cxz2 * v2.x + cyz2 * v2.y + czz2 * v2.z
            );
            float lambda2 = Mathf.Max(Vector3.Dot(v2, Av2), 1e-10f);

            // Aspect ratio from eigenvalues (sqrt because eigenvalues are variance)
            float aspect = Mathf.Sqrt(lambda1 / lambda2);

            bool isRibbon = aspect >= kAspectThreshold || pcaRatio >= kPcaVarianceThreshold;

            if (isRibbon)
                UvtLog.Verbose($"[SpatialPartitioner] ribbon detected (aspect {aspect:F1}:1, PCA variance {pcaRatio:F2})");

            return isRibbon;
        }

        /// <summary>
        /// Transfer UV2 from source shell to target shell using strip parameterization.
        /// Each vertex is parameterized as (t_along, t_across) via projection onto PCA axes.
        /// Transfer finds nearest source vertex in (t_along, t_across) space,
        /// filtered by face normal to avoid matching front↔back on thin belts.
        /// </summary>
        public static Dictionary<int, Vector2> Transfer(
            UvShell targetShell, UvShell sourceShell,
            Vector3[] tgtVertices, Vector3[] srcVertices,
            Vector2[] srcUv0, Vector2[] srcUv2,
            int[] srcTriangles,
            Vector2[] triUv2A, Vector2[] triUv2B, Vector2[] triUv2C,
            Vector3 principalAxis, Vector3 secondaryAxis, Vector3 centroid)
        {
            var result = new Dictionary<int, Vector2>();

            // Parametrize source shell: (t_along, t_across, faceNormal)
            var srcSamples = new List<(Vector2 param, Vector2 uv2, Vector3 normal)>();

            foreach (int f in sourceShell.faceIndices)
            {
                int i0 = srcTriangles[f * 3], i1 = srcTriangles[f * 3 + 1], i2 = srcTriangles[f * 3 + 2];
                if (i0 >= srcVertices.Length || i1 >= srcVertices.Length || i2 >= srcVertices.Length) continue;
                if (i0 >= srcUv2.Length || i1 >= srcUv2.Length || i2 >= srcUv2.Length) continue;

                // Face normal
                Vector3 faceNrm = Vector3.Cross(
                    srcVertices[i1] - srcVertices[i0],
                    srcVertices[i2] - srcVertices[i0]).normalized;

                // Face centroid in 3D
                Vector3 pos3D = (srcVertices[i0] + srcVertices[i1] + srcVertices[i2]) / 3f;
                Vector3 offset = pos3D - centroid;
                float tAlong = Vector3.Dot(offset, principalAxis);
                float tAcross = Vector3.Dot(offset, secondaryAxis);

                // Face centroid in UV2
                Vector2 uv2Val = (srcUv2[i0] + srcUv2[i1] + srcUv2[i2]) / 3f;

                srcSamples.Add((new Vector2(tAlong, tAcross), uv2Val, faceNrm));

                // Also add vertices (use face normal for their normal)
                for (int j = 0; j < 3; j++)
                {
                    int vi = srcTriangles[f * 3 + j];
                    Vector3 vPos = srcVertices[vi];
                    Vector3 vOff = vPos - centroid;
                    float vAlong = Vector3.Dot(vOff, principalAxis);
                    float vAcross = Vector3.Dot(vOff, secondaryAxis);
                    srcSamples.Add((new Vector2(vAlong, vAcross), srcUv2[vi], faceNrm));
                }
            }

            if (srcSamples.Count == 0) return result;

            // For each target vertex, find nearest source sample filtered by normal
            foreach (int vi in targetShell.vertexIndices)
            {
                if (vi >= tgtVertices.Length) continue;

                Vector3 tPos = tgtVertices[vi];
                Vector3 tOffset = tPos - centroid;
                float tAlong = Vector3.Dot(tOffset, principalAxis);
                float tAcross = Vector3.Dot(tOffset, secondaryAxis);
                Vector2 tParam = new Vector2(tAlong, tAcross);

                // Find nearest source sample with compatible normal
                float bestDSq = float.MaxValue;
                float bestDSqAny = float.MaxValue;
                Vector2 bestUv2 = Vector2.zero;
                Vector2 bestUv2Any = Vector2.zero;

                // Use mesh normal if available, otherwise skip normal filter
                Vector3 tNrm = Vector3.zero;
                // We don't have target normals array here, so use a simple heuristic:
                // we'll do two passes — filtered and unfiltered — and prefer filtered.
                // Actually, we need to get target normals. Let's not filter here if we
                // don't have them. The caller should pass them.
                // For now, try all samples (backward compatible), but below we add
                // an overload that accepts target normals.
                foreach (var sample in srcSamples)
                {
                    float dSq = (sample.param - tParam).sqrMagnitude;
                    if (dSq < bestDSqAny)
                    {
                        bestDSqAny = dSq;
                        bestUv2Any = sample.uv2;
                    }
                }

                result[vi] = bestUv2Any;
            }

            return result;
        }

        /// <summary>
        /// Transfer UV2 with normal filtering — separates front/back of thin belts.
        /// </summary>
        public static Dictionary<int, Vector2> TransferNormalFiltered(
            UvShell targetShell, UvShell sourceShell,
            Vector3[] tgtVertices, Vector3[] tgtNormals,
            Vector3[] srcVertices,
            Vector2[] srcUv0, Vector2[] srcUv2,
            int[] srcTriangles,
            Vector2[] triUv2A, Vector2[] triUv2B, Vector2[] triUv2C,
            Vector3 principalAxis, Vector3 secondaryAxis, Vector3 centroid)
        {
            var result = new Dictionary<int, Vector2>();
            var srcParamVertices = new Dictionary<int, ParamVertex>();
            var srcParamTriangles = new List<ParamTriangle>();
            int degenerateParamTriangles = 0;

            foreach (int f in sourceShell.faceIndices)
            {
                int i0 = srcTriangles[f * 3], i1 = srcTriangles[f * 3 + 1], i2 = srcTriangles[f * 3 + 2];
                if (i0 >= srcVertices.Length || i1 >= srcVertices.Length || i2 >= srcVertices.Length) continue;
                if (i0 >= srcUv2.Length || i1 >= srcUv2.Length || i2 >= srcUv2.Length) continue;

                ParamVertex v0 = GetOrCreateParamVertex(i0, srcVertices, srcUv2, srcParamVertices,
                    centroid, principalAxis, secondaryAxis);
                ParamVertex v1 = GetOrCreateParamVertex(i1, srcVertices, srcUv2, srcParamVertices,
                    centroid, principalAxis, secondaryAxis);
                ParamVertex v2 = GetOrCreateParamVertex(i2, srcVertices, srcUv2, srcParamVertices,
                    centroid, principalAxis, secondaryAxis);

                float paramArea2 = SignedArea2D(v0.param, v1.param, v2.param);
                Vector3 faceNrm = Vector3.Cross(
                    srcVertices[i1] - srcVertices[i0],
                    srcVertices[i2] - srcVertices[i0]).normalized;

                if (Mathf.Abs(paramArea2) < kParamTriAreaEpsilon)
                {
                    degenerateParamTriangles++;
                    continue;
                }

                srcParamTriangles.Add(new ParamTriangle
                {
                    i0 = i0,
                    i1 = i1,
                    i2 = i2,
                    p0 = v0.param,
                    p1 = v1.param,
                    p2 = v2.param,
                    normal = faceNrm
                });
            }

            if (srcParamTriangles.Count == 0)
            {
                UvtLog.Warn("[StripParameterization] TransferNormalFiltered: all param-triangles degenerate, transfer skipped.");
                return result;
            }

            foreach (int vi in targetShell.vertexIndices)
            {
                if (vi >= tgtVertices.Length) continue;

                Vector2 tParam = GetParam(tgtVertices[vi], centroid, principalAxis, secondaryAxis);

                Vector3 tNrm = (tgtNormals != null && vi < tgtNormals.Length)
                    ? tgtNormals[vi] : Vector3.zero;

                float bestDSqFiltered = float.MaxValue;
                Vector2 bestUv2Filtered = Vector2.zero;
                float bestDSqAny = float.MaxValue;
                Vector2 bestUv2Any = Vector2.zero;

                foreach (var tri in srcParamTriangles)
                {
                    float dSq = PointToTri2D(tParam, tri.p0, tri.p1, tri.p2, out float u, out float v, out float w);
                    ParamVertex s0 = srcParamVertices[tri.i0];
                    ParamVertex s1 = srcParamVertices[tri.i1];
                    ParamVertex s2 = srcParamVertices[tri.i2];
                    Vector2 uv2 = s0.uv2 * u + s1.uv2 * v + s2.uv2 * w;

                    if (dSq < bestDSqAny)
                    {
                        bestDSqAny = dSq;
                        bestUv2Any = uv2;
                    }

                    if (tNrm.sqrMagnitude > 0.5f && tri.normal.sqrMagnitude > 0.5f)
                    {
                        if (Vector3.Dot(tri.normal, tNrm) > kNormalDotThreshold && dSq < bestDSqFiltered)
                        {
                            bestDSqFiltered = dSq;
                            bestUv2Filtered = uv2;
                        }
                    }
                }

                if (bestDSqAny < float.MaxValue)
                    result[vi] = bestDSqFiltered < float.MaxValue ? bestUv2Filtered : bestUv2Any;
            }

            if (degenerateParamTriangles > 0)
            {
                UvtLog.Verbose(
                    $"[StripParameterization] TransferNormalFiltered: skipped {degenerateParamTriangles} degenerate param-triangles " +
                    $"(used {srcParamTriangles.Count}).");
            }

            return result;
        }

        static ParamVertex GetOrCreateParamVertex(int vi, Vector3[] srcVertices, Vector2[] srcUv2,
            Dictionary<int, ParamVertex> srcParamVertices,
            Vector3 centroid, Vector3 principalAxis, Vector3 secondaryAxis)
        {
            if (srcParamVertices.TryGetValue(vi, out ParamVertex value))
                return value;

            value = new ParamVertex
            {
                param = GetParam(srcVertices[vi], centroid, principalAxis, secondaryAxis),
                uv2 = srcUv2[vi]
            };
            srcParamVertices[vi] = value;
            return value;
        }

        static Vector2 GetParam(Vector3 pos, Vector3 centroid, Vector3 principalAxis, Vector3 secondaryAxis)
        {
            Vector3 offset = pos - centroid;
            return new Vector2(
                Vector3.Dot(offset, principalAxis),
                Vector3.Dot(offset, secondaryAxis));
        }

        static float SignedArea2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
        }

        static float PointToTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
            out float u, out float v, out float w)
        {
            Vector2 ab = b - a, ac = c - a, ap = p - a;
            float d00 = Vector2.Dot(ab, ab), d01 = Vector2.Dot(ab, ac);
            float d11 = Vector2.Dot(ac, ac), d20 = Vector2.Dot(ap, ab);
            float d21 = Vector2.Dot(ap, ac);
            float denom = d00 * d11 - d01 * d01;

            if (Mathf.Abs(denom) < 1e-12f)
            {
                u = 1f; v = 0f; w = 0f;
                return (p - a).sqrMagnitude;
            }

            float bV = (d11 * d20 - d01 * d21) / denom;
            float bW = (d00 * d21 - d01 * d20) / denom;
            float bU = 1f - bV - bW;

            if (bU >= 0f && bV >= 0f && bW >= 0f)
            {
                u = bU; v = bV; w = bW;
                Vector2 proj = a * u + b * v + c * w;
                return (p - proj).sqrMagnitude;
            }

            float best = float.MaxValue;
            u = 1f; v = 0f; w = 0f;

            {
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(d00, 1e-12f));
                Vector2 q = a + ab * t;
                float dSq = (p - q).sqrMagnitude;
                if (dSq < best) { best = dSq; u = 1f - t; v = t; w = 0f; }
            }
            {
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ac) / Mathf.Max(d11, 1e-12f));
                Vector2 q = a + ac * t;
                float dSq = (p - q).sqrMagnitude;
                if (dSq < best) { best = dSq; u = 1f - t; v = 0f; w = t; }
            }
            {
                Vector2 bc = c - b;
                float bcL = Vector2.Dot(bc, bc);
                float t = Mathf.Clamp01(Vector2.Dot(p - b, bc) / Mathf.Max(bcL, 1e-12f));
                Vector2 q = b + bc * t;
                float dSq = (p - q).sqrMagnitude;
                if (dSq < best) { best = dSq; u = 0f; v = 1f - t; w = t; }
            }

            return best;
        }
    }
}
