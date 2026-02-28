// GroupedShellTransfer.cs — UV2 transfer via per-shell similarity transforms
// Core idea: xatlas repack preserves shell internal structure, only changes
// placement (translate + rotate + uniform scale). We compute this transform
// from LOD0's UV0→UV2 mapping and apply it to all LODs via UV0 matching.
// Handles mirrored shells: computes both normal and reflected similarity
// transforms, picks the correct one based on UV0 winding (signed area).

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public static class GroupedShellTransfer
    {
        // ─── Similarity transform ───
        // Normal:    UV2 = [a -b; b  a] * UV0 + [tx, ty]  (det = a²+b² > 0)
        // Reflected: UV2 = [a  b; b -a] * UV0 + [tx, ty]  (det = -(a²+b²) < 0)
        public struct ShellTransform
        {
            public float a, b, tx, ty;
            public float residual;
            public bool mirrored;

            public Vector2 Apply(Vector2 uv0)
            {
                if (mirrored)
                    return new Vector2(
                        a * uv0.x + b * uv0.y + tx,
                        b * uv0.x - a * uv0.y + ty);
                else
                    return new Vector2(
                        a * uv0.x - b * uv0.y + tx,
                        b * uv0.x + a * uv0.y + ty);
            }

            public float Scale => Mathf.Sqrt(a * a + b * b);
            public float AngleDeg => Mathf.Atan2(b, a) * Mathf.Rad2Deg;
        }

        // ─── Shell info for cross-LOD matching ───
        public class SourceShellInfo
        {
            public int shellId;
            public Vector2 uv0BoundsMin, uv0BoundsMax;
            public Vector2 uv0Centroid;
            public Vector3 worldCentroid;
            public ShellTransform transform;        // normal (same winding)
            public ShellTransform mirrorTransform;   // reflected (opposite winding)
            public float signedAreaUv0;              // positive = CCW, negative = CW
            public int vertexCount;
            public Vector2[] shellUv0;               // per-vertex UV0 for nearest-vertex matching
            public Vector2[] shellUv2;               // per-vertex UV2 (transfer targets)
        }

        // ─── Result of transfer for one target mesh ───
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
        //  Step 1: Analyze source mesh — extract UV0 shells and
        //          compute similarity transform UV0→UV2 per shell
        //          (both normal and reflected variants)
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

            var shells = UvShellExtractor.Extract(uv0, tris);
            var infos = new SourceShellInfo[shells.Count];

            for (int si = 0; si < shells.Count; si++)
            {
                var shell = shells[si];

                // Collect UV0→UV2 point pairs + centroids
                var from = new List<Vector2>();
                var to = new List<Vector2>();
                Vector2 uv0Sum = Vector2.zero;
                Vector3 worldSum = Vector3.zero;
                int n = 0;

                foreach (int vi in shell.vertexIndices)
                {
                    if (vi >= uv0.Length || vi >= uv2.Length) continue;
                    from.Add(uv0[vi]);
                    to.Add(uv2[vi]);
                    uv0Sum += uv0[vi];
                    if (vi < verts.Length) worldSum += verts[vi];
                    n++;
                }

                float signedArea = ComputeSignedArea(tris, uv0, shell.faceIndices);

                var xform = ComputeSimilarityTransform(from, to, false);
                var mxform = ComputeSimilarityTransform(from, to, true);
                mxform.mirrored = true;

                infos[si] = new SourceShellInfo
                {
                    shellId = shell.shellId,
                    uv0BoundsMin = shell.boundsMin,
                    uv0BoundsMax = shell.boundsMax,
                    uv0Centroid = n > 0 ? uv0Sum / n : Vector2.zero,
                    worldCentroid = n > 0 ? worldSum / n : Vector3.zero,
                    transform = xform,
                    mirrorTransform = mxform,
                    signedAreaUv0 = signedArea,
                    vertexCount = n,
                    shellUv0 = from.ToArray(),
                    shellUv2 = to.ToArray()
                };
            }

            // Log diagnostics
            float maxResidual = 0;
            int mirrorCount = 0;
            foreach (var info in infos)
            {
                if (info.transform.residual > maxResidual)
                    maxResidual = info.transform.residual;
                if (info.signedAreaUv0 < 0) mirrorCount++;
            }

            Debug.Log($"[GroupedTransfer] Source '{sourceMesh.name}': " +
                      $"{infos.Length} shells, max_residual={maxResidual:F6}" +
                      (mirrorCount > 0 ? $", {mirrorCount} CW-wound" : ""));

            return infos;
        }

        // ═══════════════════════════════════════════════════════════
        //  Step 2: Transfer UV2 to target mesh
        //  - Extract target UV0 shells
        //  - Match each to a source shell by UV0 overlap + 3D proximity
        //  - Detect mirror via signed area, pick correct transform
        // ═══════════════════════════════════════════════════════════

        public static TransferResult Transfer(
            Mesh targetMesh, SourceShellInfo[] sourceInfos)
        {
            var result = new TransferResult();

            var tUv0List = new List<Vector2>();
            targetMesh.GetUVs(0, tUv0List);
            if (tUv0List.Count == 0)
            {
                Debug.LogError("[GroupedTransfer] Target mesh has no UV0");
                return result;
            }

            var tUv0 = tUv0List.ToArray();
            var tTris = targetMesh.triangles;
            var tVerts = targetMesh.vertices;
            int vertCount = targetMesh.vertexCount;

            result.uv2 = new Vector2[vertCount];
            result.verticesTotal = vertCount;

            // Extract target UV0 shells
            var targetShells = UvShellExtractor.Extract(tUv0, tTris);

            bool[] vertexDone = new bool[vertCount];

            foreach (var tShell in targetShells)
            {
                // Compute target shell UV0 + 3D centroids
                Vector2 tUv0Centroid = Vector2.zero;
                Vector3 tWorldCentroid = Vector3.zero;
                int tn = 0;

                foreach (int vi in tShell.vertexIndices)
                {
                    if (vi < tUv0.Length) tUv0Centroid += tUv0[vi];
                    if (vi < tVerts.Length) tWorldCentroid += tVerts[vi];
                    tn++;
                }
                if (tn > 0) { tUv0Centroid /= tn; tWorldCentroid /= tn; }

                // Compute target shell signed area in UV0
                float tSignedArea = ComputeSignedArea(tTris, tUv0, tShell.faceIndices);

                // Find best matching source shell
                int bestIdx = -1;
                float bestScore = -1f;

                for (int si = 0; si < sourceInfos.Length; si++)
                {
                    var src = sourceInfos[si];

                    // UV0 bounding box overlap ratio
                    float overlap = BboxOverlapRatio(
                        tShell.boundsMin, tShell.boundsMax,
                        src.uv0BoundsMin, src.uv0BoundsMax);

                    // UV0 centroid distance
                    float uv0Dist = (tUv0Centroid - src.uv0Centroid).magnitude;

                    // Quick reject: no UV0 overlap and far centroids
                    if (overlap < 0.05f && uv0Dist > 0.15f) continue;

                    // 3D centroid distance (critical for overlap disambiguation)
                    float worldDist = (tWorldCentroid - src.worldCentroid).magnitude;

                    // Scoring: UV0 overlap dominates, 3D breaks ties
                    float score = overlap * 10f
                                + 1f / (1f + uv0Dist * 20f)
                                + 1f / (1f + worldDist * 5f);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdx = si;
                    }
                }

                if (bestIdx >= 0)
                {
                    var src = sourceInfos[bestIdx];

                    // Detect mirror via signed area comparison
                    bool isMirrored = (tSignedArea * src.signedAreaUv0) < 0f;
                    if (isMirrored) result.shellsMirrored++;

                    // ── Per-target-shell transform via nearest-vertex matching ──
                    // Instead of reusing source's precomputed UV0→UV2 transform
                    // (which assumes target UV0 ≈ source UV0), we build point pairs
                    // (target_UV0, nearest_source_UV2) and compute a fresh transform.
                    // This handles LODs with different UV0 layout/scale correctly.

                    var ptFrom = new List<Vector2>();
                    var ptTo   = new List<Vector2>();

                    foreach (int vi in tShell.vertexIndices)
                    {
                        if (vi >= tUv0.Length) continue;
                        Vector2 tPt = tUv0[vi];

                        // Find nearest source vertex in UV0 space
                        float bestDist = float.MaxValue;
                        int bestSi = 0;
                        for (int si = 0; si < src.shellUv0.Length; si++)
                        {
                            float d = (tPt - src.shellUv0[si]).sqrMagnitude;
                            if (d < bestDist) { bestDist = d; bestSi = si; }
                        }

                        ptFrom.Add(tPt);
                        ptTo.Add(src.shellUv2[bestSi]);
                    }

                    ShellTransform xform;
                    if (ptFrom.Count >= 2)
                    {
                        xform = ComputeSimilarityTransform(ptFrom, ptTo, isMirrored);
                        xform.mirrored = isMirrored;
                    }
                    else
                    {
                        // Fallback for tiny shells: use precomputed source transform
                        xform = isMirrored ? src.mirrorTransform : src.transform;
                    }

                    int idx = 0;
                    foreach (int vi in tShell.vertexIndices)
                    {
                        if (vi < tUv0.Length && !vertexDone[vi])
                        {
                            result.uv2[vi] = xform.Apply(tUv0[vi]);
                            vertexDone[vi] = true;
                            result.verticesTransferred++;
                        }
                        idx++;
                    }
                    result.shellsMatched++;
                }
                else
                {
                    result.shellsUnmatched++;
                    Debug.LogWarning($"[GroupedTransfer] Unmatched target shell " +
                                    $"(uv0=[{tShell.boundsMin}..{tShell.boundsMax}], " +
                                    $"{tShell.vertexIndices.Count} verts)");
                }
            }

            Debug.Log($"[GroupedTransfer] '{targetMesh.name}': " +
                      $"{result.shellsMatched} matched, {result.shellsUnmatched} unmatched, " +
                      $"{result.verticesTransferred}/{result.verticesTotal} verts");

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  Similarity transform: least-squares fit
        //  Normal:    UV2 = [a -b; b  a] * UV0 + [tx, ty]
        //  Reflected: UV2 = [a  b; b -a] * UV0 + [tx, ty]
        // ═══════════════════════════════════════════════════════════

        static ShellTransform ComputeSimilarityTransform(
            List<Vector2> from, List<Vector2> to, bool reflected)
        {
            int n = Mathf.Min(from.Count, to.Count);
            if (n < 2)
                return new ShellTransform { a = 1, b = 0, tx = 0, ty = 0, residual = 0 };

            // Centroids
            double cx = 0, cy = 0, dx = 0, dy = 0;
            for (int i = 0; i < n; i++)
            {
                cx += from[i].x; cy += from[i].y;
                dx += to[i].x;   dy += to[i].y;
            }
            cx /= n; cy /= n;
            dx /= n; dy /= n;

            // Centered sums
            double norm2 = 0;
            double sum_fxtx = 0, sum_fyty = 0, sum_fxty = 0, sum_fytx = 0;
            for (int i = 0; i < n; i++)
            {
                double fx = from[i].x - cx, fy = from[i].y - cy;
                double tx = to[i].x - dx,   ty = to[i].y - dy;
                norm2   += fx * fx + fy * fy;
                sum_fxtx += fx * tx;
                sum_fyty += fy * ty;
                sum_fxty += fx * ty;
                sum_fytx += fy * tx;
            }

            if (norm2 < 1e-14)
                return new ShellTransform { a = 1, b = 0, tx = 0, ty = 0, residual = 0 };

            double a, b;
            if (reflected)
            {
                // Reflected: UV2 = [a b; b -a] * UV0 + t
                a = (sum_fxtx - sum_fyty) / norm2;
                b = (sum_fytx + sum_fxty) / norm2;
            }
            else
            {
                // Normal: UV2 = [a -b; b a] * UV0 + t
                a = (sum_fxtx + sum_fyty) / norm2;  // dot
                b = (sum_fxty - sum_fytx) / norm2;   // cross
            }

            double txf, tyf;
            if (reflected)
            {
                txf = dx - (a * cx + b * cy);
                tyf = dy - (b * cx - a * cy);
            }
            else
            {
                txf = dx - (a * cx - b * cy);
                tyf = dy - (b * cx + a * cy);
            }

            // Compute residual (mean squared error)
            double sumSqErr = 0;
            for (int i = 0; i < n; i++)
            {
                double px, py;
                if (reflected)
                {
                    px = a * from[i].x + b * from[i].y + txf;
                    py = b * from[i].x - a * from[i].y + tyf;
                }
                else
                {
                    px = a * from[i].x - b * from[i].y + txf;
                    py = b * from[i].x + a * from[i].y + tyf;
                }
                double ex = px - to[i].x;
                double ey = py - to[i].y;
                sumSqErr += ex * ex + ey * ey;
            }

            return new ShellTransform
            {
                a = (float)a,
                b = (float)b,
                tx = (float)txf,
                ty = (float)tyf,
                residual = (float)(sumSqErr / n),
                mirrored = reflected
            };
        }

        // ═══════════════════════════════════════════════════════════
        //  Signed area of shell in UV space (positive = CCW, negative = CW)
        // ═══════════════════════════════════════════════════════════

        static float ComputeSignedArea(int[] tris, Vector2[] uvs, List<int> faceIndices)
        {
            double area = 0;
            foreach (int f in faceIndices)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                var a = uvs[i0]; var b = uvs[i1]; var c = uvs[i2];
                area += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return (float)(area * 0.5);
        }

        // ═══════════════════════════════════════════════════════════
        //  Utility
        // ═══════════════════════════════════════════════════════════

        static float BboxOverlapRatio(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax)
        {
            float oMinX = Mathf.Max(aMin.x, bMin.x);
            float oMinY = Mathf.Max(aMin.y, bMin.y);
            float oMaxX = Mathf.Min(aMax.x, bMax.x);
            float oMaxY = Mathf.Min(aMax.y, bMax.y);
            if (oMaxX <= oMinX || oMaxY <= oMinY) return 0f;
            float overlapArea = (oMaxX - oMinX) * (oMaxY - oMinY);
            float aArea = Mathf.Max(1e-10f, (aMax.x - aMin.x) * (aMax.y - aMin.y));
            float bArea = Mathf.Max(1e-10f, (bMax.x - bMin.x) * (bMax.y - bMin.y));
            return overlapArea / Mathf.Min(aArea, bArea);
        }
    }
}
