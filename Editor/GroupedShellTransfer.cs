// GroupedShellTransfer.cs — UV2 transfer via shell-level matching
//
// Algorithm: "Shell-First Matching"
// Phase 1: Extract UV0 shells from source and target
// Phase 2: Match each target shell → best source shell by 3D centroid
// Phase 3: For each vertex in target shell, UV0→UV2 lookup within matched
//          source shell's faces only (no disambiguation needed — UV0 is
//          unique within a single shell)
//
// This guarantees shell topology is preserved: all vertices in a target
// shell reference the same source shell.

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

            // ─── Diagnostics ───
            /// Per-vertex: which source shell was matched (-1 = unmatched)
            public int[] vertexToSourceShell;
            /// Per target shell index → matched source shell index (-1 = unmatched)
            public int[] targetShellToSourceShell;
            /// Per target shell → 3D centroid
            public Vector3[] targetShellCentroids;
            /// Per target shell → match distance (sqr)
            public float[] targetShellMatchDistSqr;
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
        //  Transfer: Shell-first matching
        //
        //  Phase 1: Extract UV0 shells from source & target
        //  Phase 2: Match each target shell → source shell by 3D centroid
        //  Phase 3: Per-vertex UV0→UV2 lookup within matched shell only
        //
        //  Within a single source shell, UV0 doesn't overlap — no
        //  disambiguation needed. Shell topology is guaranteed preserved.
        // ═══════════════════════════════════════════════════════════

        public static TransferResult Transfer(Mesh targetMesh, Mesh sourceMesh)
        {
            var result = new TransferResult();

            // Source data
            var srcVerts = sourceMesh.vertices;
            var srcTris = sourceMesh.triangles;
            var srcUv0List = new List<Vector2>(); sourceMesh.GetUVs(0, srcUv0List);
            var srcUv2List = new List<Vector2>(); sourceMesh.GetUVs(2, srcUv2List);
            var srcUv0 = srcUv0List.ToArray();
            var srcUv2 = srcUv2List.ToArray();

            if (srcUv0.Length == 0 || srcUv2.Length == 0)
            { Debug.LogError("[GroupedTransfer] Source missing UV0/UV2"); return result; }

            // Target data
            var tVerts = targetMesh.vertices;
            var tUv0List = new List<Vector2>(); targetMesh.GetUVs(0, tUv0List);
            var tUv0 = tUv0List.ToArray();
            int vertCount = targetMesh.vertexCount;

            if (tUv0.Length == 0)
            { Debug.LogError("[GroupedTransfer] Target missing UV0"); return result; }

            result.uv2 = new Vector2[vertCount];
            result.verticesTotal = vertCount;
            result.vertexToSourceShell = new int[vertCount];
            for (int i = 0; i < vertCount; i++) result.vertexToSourceShell[i] = -1;

            // ── Pre-compute source triangle data ──
            int srcTriCount = srcTris.Length / 3;
            var triUv0A = new Vector2[srcTriCount];
            var triUv0B = new Vector2[srcTriCount];
            var triUv0C = new Vector2[srcTriCount];
            var triUv2A = new Vector2[srcTriCount];
            var triUv2B = new Vector2[srcTriCount];
            var triUv2C = new Vector2[srcTriCount];

            for (int f = 0; f < srcTriCount; f++)
            {
                int i0 = srcTris[f * 3], i1 = srcTris[f * 3 + 1], i2 = srcTris[f * 3 + 2];
                triUv0A[f] = srcUv0[i0]; triUv0B[f] = srcUv0[i1]; triUv0C[f] = srcUv0[i2];
                triUv2A[f] = srcUv2[i0]; triUv2B[f] = srcUv2[i1]; triUv2C[f] = srcUv2[i2];
            }

            // ── Phase 1: Extract shells ──
            var srcShells = UvShellExtractor.Extract(srcUv0, srcTris);
            var tgtTris = targetMesh.triangles;
            var tgtShells = UvShellExtractor.Extract(tUv0, tgtTris);

            // Compute 3D centroid for each source shell
            var srcCentroid3D = new Vector3[srcShells.Count];
            for (int si = 0; si < srcShells.Count; si++)
            {
                Vector3 sum = Vector3.zero; int n = 0;
                foreach (int vi in srcShells[si].vertexIndices)
                {
                    if (vi < srcVerts.Length) { sum += srcVerts[vi]; n++; }
                }
                srcCentroid3D[si] = n > 0 ? sum / n : Vector3.zero;
            }

            // ── Phase 2 & 3: Match shells + transfer vertices ──
            int transferred = 0;
            int shellsMatched = 0;

            // Diagnostics: per target shell arrays
            result.targetShellToSourceShell = new int[tgtShells.Count];
            result.targetShellCentroids = new Vector3[tgtShells.Count];
            result.targetShellMatchDistSqr = new float[tgtShells.Count];
            for (int i = 0; i < tgtShells.Count; i++)
            {
                result.targetShellToSourceShell[i] = -1;
                result.targetShellMatchDistSqr[i] = float.MaxValue;
            }

            for (int tsi = 0; tsi < tgtShells.Count; tsi++)
            {
                var tShell = tgtShells[tsi];

                // Compute target shell 3D centroid
                Vector3 tCentroid = Vector3.zero; int tN = 0;
                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi < tVerts.Length) { tCentroid += tVerts[vi]; tN++; }
                }
                if (tN > 0) tCentroid /= tN;

                // Find best source shell by 3D centroid distance
                float bestDist = float.MaxValue;
                int bestSrc = -1;
                for (int si = 0; si < srcShells.Count; si++)
                {
                    float d = (tCentroid - srcCentroid3D[si]).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; bestSrc = si; }
                }

                // Record diagnostics
                result.targetShellCentroids[tsi] = tCentroid;

                if (bestSrc < 0) continue;
                shellsMatched++;

                result.targetShellToSourceShell[tsi] = bestSrc;
                result.targetShellMatchDistSqr[tsi] = bestDist;

                // Get matched source shell's face list
                var srcFaces = srcShells[bestSrc].faceIndices;

                // Per-vertex UV0→UV2 lookup within matched shell only
                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi >= tUv0.Length) continue;
                    Vector2 tUv = tUv0[vi];

                    float bestDSq = float.MaxValue;
                    int bestF = -1;
                    float bestU = 0, bestV = 0, bestW = 0;

                    for (int fi = 0; fi < srcFaces.Count; fi++)
                    {
                        int f = srcFaces[fi];
                        float dSq = PointToTri2D(tUv, triUv0A[f], triUv0B[f], triUv0C[f],
                            out float u, out float v, out float w);
                        if (dSq < bestDSq)
                        {
                            bestDSq = dSq; bestF = f;
                            bestU = u; bestV = v; bestW = w;
                        }
                    }

                    if (bestF >= 0)
                    {
                        result.uv2[vi] = triUv2A[bestF] * bestU
                                       + triUv2B[bestF] * bestV
                                       + triUv2C[bestF] * bestW;
                        result.vertexToSourceShell[vi] = bestSrc;
                        transferred++;
                    }
                }
            }

            result.verticesTransferred = transferred;
            result.shellsMatched = shellsMatched;

            Debug.Log($"[GroupedTransfer] '{targetMesh.name}': shell-first, " +
                      $"{tgtShells.Count} target → {shellsMatched} matched, " +
                      $"{transferred}/{vertCount} verts");

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
