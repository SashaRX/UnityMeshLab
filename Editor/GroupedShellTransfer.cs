// GroupedShellTransfer.cs — UV2 transfer via per-vertex UV0 lookup + triangle region lock
//
// Algorithm: "Per-Vertex with Region Coherence"
// Phase 1: For each target triangle, find source 3D region by centroid UV0+3D
// Phase 2: For each target vertex:
//   1. Take its UV0 coordinate, find nearest source triangle in UV0 2D space
//   2. If multiple candidates (tiling/overlap), disambiguate using the
//      region anchor from adjacent triangles (not raw 3D vertex position)
//   3. Compute UV0 barycentric → interpolate UV2
//
// Per-vertex UV0 gives correct barycentrics; region lock ensures coherence.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class GroupedShellTransfer
    {
        // ─── Shell info for cross-LOD analysis (UI stats) ───
        public class SourceShellInfo
        {
            public int shellId;
            public Vector2 uv0BoundsMin, uv0BoundsMax;
            public Vector2 uv0Centroid;
            public Vector3 worldCentroid;
            public float signedAreaUv0;
            public int vertexCount;
            public int[] vertexIndices;
            public Vector3[] worldPositions;
            public Vector3[] normals;
            public Vector2[] shellUv0;
            public Vector2[] shellUv2;
            public List<int> faceIndices;
        }

        public class TransferResult
        {
            public Vector2[] uv2;
            public int shellsMatched;
            public int shellsUnmatched;
            public int shellsMirrored;
            public int verticesTransferred;
            public int verticesTotal;
        }

        // ═══════════════════════════════════════════════════════════
        //  AnalyzeSource — extract UV0 shells for UI display
        // ═══════════════════════════════════════════════════════════

        public static SourceShellInfo[] AnalyzeSource(Mesh sourceMesh)
        {
            var uv0List = new List<Vector2>();
            var uv2List = new List<Vector2>();
            sourceMesh.GetUVs(0, uv0List);
            sourceMesh.GetUVs(2, uv2List);
            if (uv0List.Count == 0 || uv2List.Count == 0)
            {
                Debug.LogError("[GroupedTransfer] Source mesh missing UV0 or UV2");
                return null;
            }
            var uv0 = uv0List.ToArray();
            var uv2 = uv2List.ToArray();
            var tris = sourceMesh.triangles;
            var verts = sourceMesh.vertices;
            var norms = sourceMesh.normals;
            bool hasN = norms != null && norms.Length == verts.Length;

            var shells = UvShellExtractor.Extract(uv0, tris);
            var infos = new SourceShellInfo[shells.Count];
            for (int si = 0; si < shells.Count; si++)
            {
                var sh = shells[si];
                var idx = new List<int>(); var pos = new List<Vector3>();
                var nrm = new List<Vector3>(); var u0s = new List<Vector2>();
                var u2s = new List<Vector2>();
                Vector2 u0Sum = Vector2.zero; Vector3 wSum = Vector3.zero; int n = 0;
                foreach (int vi in sh.vertexIndices)
                {
                    if (vi >= uv0.Length || vi >= uv2.Length || vi >= verts.Length) continue;
                    idx.Add(vi); pos.Add(verts[vi]);
                    nrm.Add(hasN ? norms[vi] : Vector3.up);
                    u0s.Add(uv0[vi]); u2s.Add(uv2[vi]);
                    u0Sum += uv0[vi]; wSum += verts[vi]; n++;
                }
                float sa = ComputeSignedArea(tris, uv0, sh.faceIndices);
                infos[si] = new SourceShellInfo
                {
                    shellId = sh.shellId,
                    uv0BoundsMin = sh.boundsMin, uv0BoundsMax = sh.boundsMax,
                    uv0Centroid = n > 0 ? u0Sum / n : Vector2.zero,
                    worldCentroid = n > 0 ? wSum / n : Vector3.zero,
                    signedAreaUv0 = sa, vertexCount = n,
                    vertexIndices = idx.ToArray(), worldPositions = pos.ToArray(),
                    normals = nrm.ToArray(), shellUv0 = u0s.ToArray(),
                    shellUv2 = u2s.ToArray(), faceIndices = sh.faceIndices
                };
            }
            Debug.Log($"[GroupedTransfer] Source '{sourceMesh.name}': {infos.Length} shells");
            return infos;
        }

        // ═══════════════════════════════════════════════════════════
        //  Transfer: Per-vertex UV0 lookup with triangle-coherent 3D region
        //
        //  Phase 1: For each target triangle, find source region by centroid
        //  Phase 2: For each vertex, UV0 lookup → if disambiguation needed,
        //           constrain to region from adjacent triangles
        //  This gives per-vertex UV0 accuracy with triangle coherence.
        // ═══════════════════════════════════════════════════════════

        const float OVERLAP_THRESHOLD = 1e-4f; // UV0 dist² threshold to detect overlapping candidates
        const float NORMAL_DOT_MIN = 0.3f;

        public static TransferResult Transfer(Mesh targetMesh, Mesh sourceMesh)
        {
            var result = new TransferResult();

            // Source data
            var srcVerts = sourceMesh.vertices;
            var srcNormals = sourceMesh.normals;
            var srcTris = sourceMesh.triangles;
            var srcUv0List = new List<Vector2>(); sourceMesh.GetUVs(0, srcUv0List);
            var srcUv2List = new List<Vector2>(); sourceMesh.GetUVs(2, srcUv2List);
            var srcUv0 = srcUv0List.ToArray();
            var srcUv2 = srcUv2List.ToArray();
            bool srcHasN = srcNormals != null && srcNormals.Length == srcVerts.Length;

            if (srcUv0.Length == 0 || srcUv2.Length == 0)
            { Debug.LogError("[GroupedTransfer] Source missing UV0/UV2"); return result; }

            // Target data
            var tVerts = targetMesh.vertices;
            var tNormals = targetMesh.normals;
            var tUv0List = new List<Vector2>(); targetMesh.GetUVs(0, tUv0List);
            var tUv0 = tUv0List.ToArray();
            int vertCount = targetMesh.vertexCount;
            bool tHasN = tNormals != null && tNormals.Length == vertCount;

            if (tUv0.Length == 0)
            { Debug.LogError("[GroupedTransfer] Target missing UV0"); return result; }

            result.uv2 = new Vector2[vertCount];
            result.verticesTotal = vertCount;

            // ── Pre-compute source triangle data in UV0 space ──
            int srcTriCount = srcTris.Length / 3;

            // Per-triangle: UV0 coords, UV2 coords, 3D centroid, face normal
            var triUv0A = new Vector2[srcTriCount];
            var triUv0B = new Vector2[srcTriCount];
            var triUv0C = new Vector2[srcTriCount];
            var triUv2A = new Vector2[srcTriCount];
            var triUv2B = new Vector2[srcTriCount];
            var triUv2C = new Vector2[srcTriCount];
            var tri3DCentroid = new Vector3[srcTriCount];
            var triFaceN = new Vector3[srcTriCount];

            for (int f = 0; f < srcTriCount; f++)
            {
                int i0 = srcTris[f * 3], i1 = srcTris[f * 3 + 1], i2 = srcTris[f * 3 + 2];
                triUv0A[f] = srcUv0[i0]; triUv0B[f] = srcUv0[i1]; triUv0C[f] = srcUv0[i2];
                triUv2A[f] = srcUv2[i0]; triUv2B[f] = srcUv2[i1]; triUv2C[f] = srcUv2[i2];
                tri3DCentroid[f] = (srcVerts[i0] + srcVerts[i1] + srcVerts[i2]) / 3f;
                Vector3 e1 = srcVerts[i1] - srcVerts[i0];
                Vector3 e2 = srcVerts[i2] - srcVerts[i0];
                triFaceN[f] = Vector3.Cross(e1, e2).normalized;
                if (triFaceN[f].sqrMagnitude < 0.5f && srcHasN)
                    triFaceN[f] = ((srcNormals[i0] + srcNormals[i1] + srcNormals[i2]) / 3f).normalized;
            }

            // ── Phase 1: Per-triangle region anchoring ──
            // For each target triangle, find which source 3D region it maps to
            var tTris = targetMesh.triangles;
            int tTriCount = tTris.Length / 3;
            var triRegionAnchor = new Vector3[tTriCount]; // 3D centroid of best source tri

            for (int ti = 0; ti < tTriCount; ti++)
            {
                int tI0 = tTris[ti * 3], tI1 = tTris[ti * 3 + 1], tI2 = tTris[ti * 3 + 2];
                Vector2 tCentUv0 = (tUv0[tI0] + tUv0[tI1] + tUv0[tI2]) / 3f;
                Vector3 tCent3D = (tVerts[tI0] + tVerts[tI1] + tVerts[tI2]) / 3f;
                Vector3 tE1 = tVerts[tI1] - tVerts[tI0];
                Vector3 tE2 = tVerts[tI2] - tVerts[tI0];
                Vector3 tFN = Vector3.Cross(tE1, tE2).normalized;
                if (tFN.sqrMagnitude < 0.5f && tHasN)
                    tFN = ((tNormals[tI0] + tNormals[tI1] + tNormals[tI2]) / 3f).normalized;

                float bestUvDSq = float.MaxValue;
                int bestF = -1;
                for (int f = 0; f < srcTriCount; f++)
                {
                    float dSq = PointToTri2D(tCentUv0, triUv0A[f], triUv0B[f], triUv0C[f],
                        out _, out _, out _);
                    if (dSq < bestUvDSq) { bestUvDSq = dSq; bestF = f; }
                }

                float th = bestUvDSq + OVERLAP_THRESHOLD;
                int cc = 0;
                for (int f = 0; f < srcTriCount; f++)
                {
                    if (PointToTri2D(tCentUv0, triUv0A[f], triUv0B[f], triUv0C[f],
                        out _, out _, out _) <= th) cc++;
                    if (cc > 1) break;
                }

                if (cc > 1)
                {
                    float bestD = float.MaxValue;
                    for (int f = 0; f < srcTriCount; f++)
                    {
                        if (PointToTri2D(tCentUv0, triUv0A[f], triUv0B[f], triUv0C[f],
                            out _, out _, out _) > th) continue;
                        float dot = Vector3.Dot(tFN, triFaceN[f]);
                        if (dot < NORMAL_DOT_MIN) continue;
                        float d = (tCent3D - tri3DCentroid[f]).sqrMagnitude;
                        if (d < bestD) { bestD = d; bestF = f; }
                    }
                }

                triRegionAnchor[ti] = bestF >= 0 ? tri3DCentroid[bestF] : Vector3.zero;
            }

            // Build vertex → adjacent triangles lookup
            var vertTris = new List<int>[vertCount];
            for (int i = 0; i < vertCount; i++) vertTris[i] = new List<int>(6);
            for (int ti = 0; ti < tTriCount; ti++)
            {
                vertTris[tTris[ti * 3]].Add(ti);
                vertTris[tTris[ti * 3 + 1]].Add(ti);
                vertTris[tTris[ti * 3 + 2]].Add(ti);
            }

            // Compute per-vertex region anchor (average of adjacent triangle anchors)
            var vertRegion = new Vector3[vertCount];
            for (int vi = 0; vi < vertCount; vi++)
            {
                Vector3 sum = Vector3.zero;
                foreach (int ti in vertTris[vi]) sum += triRegionAnchor[ti];
                vertRegion[vi] = vertTris[vi].Count > 0 ? sum / vertTris[vi].Count : tVerts[vi];
            }

            // ── Phase 2: Per-vertex UV0 lookup with region-constrained disambiguation ──
            int transferred = 0;
            int disambiguated = 0;

            for (int vi = 0; vi < vertCount; vi++)
            {
                Vector2 tUv = tUv0[vi];
                Vector3 tPos = tVerts[vi];
                Vector3 tN = tHasN ? tNormals[vi] : Vector3.up;
                Vector3 regionAnchor = vertRegion[vi];

                // Step 1: Find nearest source triangle in UV0 space
                float bestUv0DistSq = float.MaxValue;
                int bestFace = -1;
                float bestU = 0, bestV = 0, bestW = 0;

                for (int f = 0; f < srcTriCount; f++)
                {
                    float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                        out float u, out float v, out float w);
                    if (dSq < bestUv0DistSq)
                    {
                        bestUv0DistSq = dSq;
                        bestFace = f;
                        bestU = u; bestV = v; bestW = w;
                    }
                }

                if (bestFace < 0) continue;

                // Step 2: Disambiguation using region anchor
                float threshold = bestUv0DistSq + OVERLAP_THRESHOLD;

                int candidateCount = 0;
                for (int f = 0; f < srcTriCount; f++)
                {
                    float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                        out _, out _, out _);
                    if (dSq <= threshold) candidateCount++;
                    if (candidateCount > 1) break;
                }

                if (candidateCount > 1)
                {
                    disambiguated++;
                    float best3DDistSq = float.MaxValue;

                    for (int f = 0; f < srcTriCount; f++)
                    {
                        float uvDSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                            out float u, out float v, out float w);
                        if (uvDSq > threshold) continue;

                        float dot = Vector3.Dot(tN, triFaceN[f]);
                        if (dot < NORMAL_DOT_MIN) continue;

                        // Distance from source centroid to vertex's REGION anchor
                        float d3D = (regionAnchor - tri3DCentroid[f]).sqrMagnitude;
                        if (d3D < best3DDistSq)
                        {
                            best3DDistSq = d3D;
                            bestFace = f;
                            bestU = u; bestV = v; bestW = w;
                        }
                    }
                }

                // Step 3: Interpolate UV2 using UV0 barycentrics
                result.uv2[vi] = triUv2A[bestFace] * bestU
                               + triUv2B[bestFace] * bestV
                               + triUv2C[bestFace] * bestW;
                transferred++;
            }

            result.verticesTransferred = transferred;

            var srcShells = UvShellExtractor.Extract(srcUv0, srcTris);
            result.shellsMatched = srcShells.Count;

            Debug.Log($"[GroupedTransfer] '{targetMesh.name}': " +
                      $"per-vertex UV0 + region-lock, " +
                      $"{transferred}/{vertCount} verts" +
                      (disambiguated > 0 ? $", {disambiguated} disambiguated" : ""));

            // UV2 bounds check
            int oob = 0;
            Vector2 uvMin = Vector2.one * float.MaxValue, uvMax = Vector2.one * float.MinValue;
            for (int i = 0; i < result.uv2.Length; i++)
            {
                var uv = result.uv2[i];
                uvMin = Vector2.Min(uvMin, uv); uvMax = Vector2.Max(uvMax, uv);
                if (uv.x < -0.01f || uv.x > 1.01f || uv.y < -0.01f || uv.y > 1.01f) oob++;
            }
            if (oob > 0)
                Debug.LogWarning($"[GroupedTransfer] '{targetMesh.name}': {oob} verts outside 0-1! " +
                    $"UV2=[{uvMin.x:F3},{uvMin.y:F3}]-[{uvMax.x:F3},{uvMax.y:F3}]");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  2D point-to-triangle distance (UV0 space)
        //  Returns squared distance; outputs barycentric (u,v,w)
        // ═══════════════════════════════════════════════════════════

        static float PointToTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
            out float u, out float v, out float w)
        {
            Vector2 ab = b - a, ac = c - a, ap = p - a;
            float d00 = Vector2.Dot(ab, ab), d01 = Vector2.Dot(ab, ac);
            float d11 = Vector2.Dot(ac, ac), d20 = Vector2.Dot(ap, ab);
            float d21 = Vector2.Dot(ap, ac);
            float denom = d00 * d11 - d01 * d01;

            if (Mathf.Abs(denom) < 1e-12f)
            { u = 1f; v = 0f; w = 0f; return (p - a).sqrMagnitude; }

            float bV = (d11 * d20 - d01 * d21) / denom;
            float bW = (d00 * d21 - d01 * d20) / denom;
            float bU = 1f - bV - bW;

            if (bU >= 0f && bV >= 0f && bW >= 0f)
            {
                u = bU; v = bV; w = bW;
                Vector2 proj = a * u + b * v + c * w;
                return (p - proj).sqrMagnitude;
            }

            // Clamp to edges
            float best = float.MaxValue; u = 1; v = 0; w = 0;
            { // AB
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(d00, 1e-12f));
                float d = (p - (a + ab * t)).sqrMagnitude;
                if (d < best) { best = d; u = 1f - t; v = t; w = 0f; }
            }
            { // AC
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ac) / Mathf.Max(d11, 1e-12f));
                float d = (p - (a + ac * t)).sqrMagnitude;
                if (d < best) { best = d; u = 1f - t; v = 0f; w = t; }
            }
            { // BC
                Vector2 bc = c - b; float bcL = Vector2.Dot(bc, bc);
                float t = Mathf.Clamp01(Vector2.Dot(p - b, bc) / Mathf.Max(bcL, 1e-12f));
                float d = (p - (b + bc * t)).sqrMagnitude;
                if (d < best) { best = d; u = 0f; v = 1f - t; w = t; }
            }
            return best;
        }

        // ═══════════════════════════════════════════════════════════
        //  3D closest point on triangle — returns squared distance
        // ═══════════════════════════════════════════════════════════

        static float ClosestPointOnTri3DSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d00 = Vector3.Dot(ab, ab), d01 = Vector3.Dot(ab, ac);
            float d11 = Vector3.Dot(ac, ac), d20 = Vector3.Dot(ap, ab);
            float d21 = Vector3.Dot(ap, ac);
            float denom = d00 * d11 - d01 * d01;

            if (Mathf.Abs(denom) < 1e-12f)
                return (p - a).sqrMagnitude;

            float bv = (d11 * d20 - d01 * d21) / denom;
            float bw = (d00 * d21 - d01 * d20) / denom;
            float bu = 1f - bv - bw;

            if (bu >= 0f && bv >= 0f && bw >= 0f)
            {
                Vector3 proj = a * bu + b * bv + c * bw;
                return (p - proj).sqrMagnitude;
            }

            // Clamp to edges
            float best = float.MaxValue;
            { // AB
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(d00, 1e-12f));
                float d = (p - (a + ab * t)).sqrMagnitude;
                if (d < best) best = d;
            }
            { // AC
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ac) / Mathf.Max(d11, 1e-12f));
                float d = (p - (a + ac * t)).sqrMagnitude;
                if (d < best) best = d;
            }
            { // BC
                Vector3 bc = c - b; float bcL = Vector3.Dot(bc, bc);
                float t = Mathf.Clamp01(Vector3.Dot(p - b, bc) / Mathf.Max(bcL, 1e-12f));
                float d = (p - (b + bc * t)).sqrMagnitude;
                if (d < best) best = d;
            }
            return best;
        }

        static float ComputeSignedArea(int[] tris, Vector2[] uvs, List<int> faces)
        {
            double area = 0;
            foreach (int f in faces)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                var a = uvs[i0]; var b = uvs[i1]; var c = uvs[i2];
                area += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return (float)(area * 0.5);
        }

        // Legacy overload
        public static TransferResult Transfer(Mesh targetMesh, SourceShellInfo[] sourceInfos)
        {
            Debug.LogWarning("[GroupedTransfer] Legacy Transfer called.");
            return new TransferResult { uv2 = new Vector2[targetMesh.vertexCount], verticesTotal = targetMesh.vertexCount };
        }
    }
}
