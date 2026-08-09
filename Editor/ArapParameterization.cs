// ArapParameterization.cs — As-Rigid-As-Possible (ARAP) UV parameterization.
//
// Implements Liu et al. 2008 "A Local/Global Approach to Mesh Parameterization"
// for re-unwrapping individual ribbon-shaped shells whose authored / xatlas-LSCM
// UV0 produces stretched slivers. The algorithm alternates a local SVD-style
// step (find best 2x2 rotation per triangle) with a global Poisson-style step
// (cotangent-Laplacian linear solve for new UV positions).
//
// The cotangent weights can be negative on obtuse triangles which breaks SPD
// of the Laplacian — we clamp negative cotangents to 0 (Mullen et al. fix).
// One vertex is pinned to (0,0) to kill the rigid-translation nullspace; the
// global solve is therefore on the free×free sub-block of L.
//
// Author: SashaRX.UnityMeshLab

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ArapParameterization
    {
        // ── Numerical tolerances ────────────────────────────────────────────
        // Triangles with rest-area below this (in 3D units²) are skipped —
        // they would produce inf cot weights and corrupt the Laplacian.
        const float kDegenerateTriArea = 1e-12f;
        // CG relative residual tolerance and max iter cap.
        const double kCgRelTol = 1e-6;
        const int    kCgMaxIter = 500;
        // ARAP early-exit: if max |R_T - R_T_prev| (Frobenius) drops below
        // this in two consecutive iterations we stop early.
        const float kRotationDeltaTol = 1e-5f;

        // ────────────────────────────────────────────────────────────────────
        // Public API
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-parameterize the UV0 coordinates of a single shell using ARAP
        /// (As-Rigid-As-Possible) local-global iterations. Modifies uvFlat in
        /// place for vertices in shellVertexIndices.
        /// </summary>
        /// <param name="positions">Mesh 3D positions (full mesh).</param>
        /// <param name="globalTris">Mesh triangle indices (full mesh, length = 3 × triCount).</param>
        /// <param name="shellTriIndices">Indices into globalTris/3 — which triangles belong to this shell.</param>
        /// <param name="shellVertexIndices">Global vertex indices for this shell.</param>
        /// <param name="uvFlat">Flat UV0 array, indexed by globalVertexIdx × 2. Modified in place.</param>
        /// <param name="iterations">Number of local-global iterations. 10 is usually sufficient.</param>
        /// <param name="initialFlipCount">Out: how many triangles were flipped in the initial UV0 (informational).</param>
        /// <returns>True if reparameterization converged and modified UVs, false if degenerate (returns uvFlat unmodified).</returns>
        internal static bool Reparameterize(
            Vector3[] positions,
            int[] globalTris,
            int[] shellTriIndices,
            ICollection<int> shellVertexIndices,
            float[] uvFlat,
            int iterations,
            out int initialFlipCount)
        {
            initialFlipCount = 0;

            if (positions == null || globalTris == null || uvFlat == null ||
                shellTriIndices == null || shellVertexIndices == null ||
                shellTriIndices.Length == 0 || shellVertexIndices.Count < 3 ||
                iterations <= 0)
            {
                return false;
            }

            int triCount = shellTriIndices.Length;
            int n = shellVertexIndices.Count;

            // Early-exit on trivial shells. ARAP on a 2-tri / <4-vert patch is
            // numerically meaningless (single rotation, two-vertex pin) and on
            // ribbon-classified flat plates only burns cycles and risks UV
            // corruption — keep the original UV0 untouched.
            if (n < 4 || triCount < 2)
            {
                UvtLog.Verbose(UvtLog.Category.Repack,
                    $"[ARAP] shell verts={n} tris={triCount} below minimum size — skipped");
                return false;
            }

            // ── Build global → local vertex index map ───────────────────────
            // Sorted local-index assignment by global index keeps the
            // resulting linear system deterministic across runs.
            var localOf = new Dictionary<int, int>(n);
            int[] globalOf;
            {
                var sorted = new List<int>(shellVertexIndices);
                sorted.Sort();
                var globalList = new List<int>(sorted.Count);
                for (int si = 0; si < sorted.Count; si++)
                {
                    int gi = sorted[si];
                    if (localOf.ContainsKey(gi)) continue; // dedup just in case
                    localOf[gi] = globalList.Count;
                    globalList.Add(gi);
                }
                globalOf = globalList.ToArray();
                n = globalOf.Length;
            }

            if (n < 3) return false;

            // ── Per-triangle flatten to 2D + half-edge cot weights ──────────
            // tri[t] = (la, lb, lc) with local indices.
            // rest2D[t*6..t*6+5] = (xa, ya, xb, yb, xc, yc).
            // halfCot[t*3..t*3+2] = half-cot of the angle opposite each edge:
            //   halfCot[t*3+0] = 0.5 * cot(angle at lc) = weight on edge (la,lb)
            //   halfCot[t*3+1] = 0.5 * cot(angle at la) = weight on edge (lb,lc)
            //   halfCot[t*3+2] = 0.5 * cot(angle at lb) = weight on edge (lc,la)
            var triLocal = new int[triCount * 3];
            var rest2D   = new float[triCount * 6];
            var halfCot  = new float[triCount * 3];
            int degenerateTris = 0; // tracked for diagnostics

            int triValidCount = 0;
            for (int t = 0; t < triCount; t++)
            {
                int f = shellTriIndices[t];
                int baseIdx = f * 3;
                if ((uint)baseIdx + 2 >= (uint)globalTris.Length) { degenerateTris++; continue; }
                int g0 = globalTris[baseIdx + 0];
                int g1 = globalTris[baseIdx + 1];
                int g2 = globalTris[baseIdx + 2];
                if ((uint)g0 >= (uint)positions.Length ||
                    (uint)g1 >= (uint)positions.Length ||
                    (uint)g2 >= (uint)positions.Length)
                { degenerateTris++; continue; }

                if (!localOf.TryGetValue(g0, out int l0) ||
                    !localOf.TryGetValue(g1, out int l1) ||
                    !localOf.TryGetValue(g2, out int l2))
                { degenerateTris++; continue; }

                Vector3 p0 = positions[g0];
                Vector3 p1 = positions[g1];
                Vector3 p2 = positions[g2];

                Vector3 e01 = p1 - p0;
                float len01 = e01.magnitude;
                if (len01 < 1e-12f) { degenerateTris++; continue; }
                Vector3 e01u = e01 / len01;
                Vector3 e02 = p2 - p0;
                float proj = Vector3.Dot(e02, e01u);
                float perpSq = e02.sqrMagnitude - proj * proj;
                if (perpSq < 0f) perpSq = 0f;
                float perp = Mathf.Sqrt(perpSq);

                // Flat positions: x0 at origin, x1 on +X axis, x2 in upper half-plane.
                float xa = 0f,         ya = 0f;
                float xb = len01,      yb = 0f;
                float xc = proj,       yc = perp;

                // 2 * triangle area in flat space.
                float area2 = (xb - xa) * (yc - ya) - (xc - xa) * (yb - ya);
                if (Mathf.Abs(area2) < kDegenerateTriArea) { degenerateTris++; continue; }

                int tOut = triValidCount;
                triLocal[tOut * 3 + 0] = l0;
                triLocal[tOut * 3 + 1] = l1;
                triLocal[tOut * 3 + 2] = l2;
                rest2D[tOut * 6 + 0] = xa; rest2D[tOut * 6 + 1] = ya;
                rest2D[tOut * 6 + 2] = xb; rest2D[tOut * 6 + 3] = yb;
                rest2D[tOut * 6 + 4] = xc; rest2D[tOut * 6 + 5] = yc;

                // Cotangent of the angle at each vertex from the flat rest
                // triangle. cot(angle at v) = dot(e_a, e_b) / (2 * area) where
                // e_a, e_b are the two edges incident at v. Use signed area2
                // so cotangents pick up the correct sign of obtuse angles.
                // Edges at vertex a (= x0): a→b = (xb-xa,yb-ya), a→c = (xc-xa,yc-ya)
                float ax1 = xb - xa, ay1 = yb - ya;
                float ax2 = xc - xa, ay2 = yc - ya;
                float dotA = ax1 * ax2 + ay1 * ay2;
                float cotA = dotA / area2;
                // Vertex b (= x1): b→a, b→c
                float bx1 = xa - xb, by1 = ya - yb;
                float bx2 = xc - xb, by2 = yc - yb;
                float dotB = bx1 * bx2 + by1 * by2;
                float cotB = dotB / area2;
                // Vertex c (= x2): c→a, c→b
                float cx1 = xa - xc, cy1 = ya - yc;
                float cx2 = xb - xc, cy2 = yb - yc;
                float dotC = cx1 * cx2 + cy1 * cy2;
                float cotC = dotC / area2;

                // Clamp negative cot weights to 0 (Mullen et al. "Spectral
                // Conformal Parameterization") to keep L SPD on obtuse triangles.
                if (cotA < 0f) cotA = 0f;
                if (cotB < 0f) cotB = 0f;
                if (cotC < 0f) cotC = 0f;

                // Half-cot stored per edge as defined above.
                halfCot[tOut * 3 + 0] = 0.5f * cotC; // edge (l0,l1)
                halfCot[tOut * 3 + 1] = 0.5f * cotA; // edge (l1,l2)
                halfCot[tOut * 3 + 2] = 0.5f * cotB; // edge (l2,l0)

                triValidCount++;
            }

            if (triValidCount == 0)
            {
                return false;
            }

            // ── Build cotangent Laplacian as a dictionary-of-rows ───────────
            // diag[i] = Σ_j w_ij ; off[i] = list of (j, w_ij). We then expand
            // to CSR for fast CG. w_ij accumulates contributions from each
            // incident triangle.
            var rowMaps = new Dictionary<int, double>[n];
            for (int i = 0; i < n; i++) rowMaps[i] = new Dictionary<int, double>();
            double[] diag = new double[n];

            for (int t = 0; t < triValidCount; t++)
            {
                int l0 = triLocal[t * 3 + 0];
                int l1 = triLocal[t * 3 + 1];
                int l2 = triLocal[t * 3 + 2];
                double w01 = halfCot[t * 3 + 0];
                double w12 = halfCot[t * 3 + 1];
                double w20 = halfCot[t * 3 + 2];

                AccumulateLaplacian(rowMaps, diag, l0, l1, w01);
                AccumulateLaplacian(rowMaps, diag, l1, l2, w12);
                AccumulateLaplacian(rowMaps, diag, l2, l0, w20);
            }

            // ── Determine pinned vertex and free set ────────────────────────
            // Pin one boundary vertex if a boundary loop exists; otherwise
            // pin local index 0. Pinning kills the rigid-translation
            // nullspace and makes L_FF SPD for the CG solve.
            bool hasBoundary;
            var boundaryLoop = FindBoundaryLoop(triLocal, triValidCount, n, out hasBoundary);
            int pinned = hasBoundary && boundaryLoop.Count > 0 ? boundaryLoop[0] : 0;

            // free → local mapping. freeOfLocal[localIdx] = position in the
            // dense free-vector or -1 if pinned.
            int nFree = n - 1;
            var freeOfLocal = new int[n];
            int fIdx = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == pinned) { freeOfLocal[i] = -1; continue; }
                freeOfLocal[i] = fIdx++;
            }

            // ── Build CSR of L restricted to free×free ──────────────────────
            // For an SPD Laplacian on free DOFs we only need the free×free
            // sub-block (the pinned column contribution is zero because we
            // pin u[pinned] = (0,0)). diag[freeRow] - includes the full
            // diagonal Σ_j w_ij; off-diagonals are weights to other free
            // vertices only.
            var csrRowPtr = new int[nFree + 1];
            var rowFillCounts = new int[nFree];
            for (int i = 0; i < n; i++)
            {
                int fi = freeOfLocal[i];
                if (fi < 0) continue;
                int count = 0;
                foreach (var kv in rowMaps[i])
                {
                    int fj = freeOfLocal[kv.Key];
                    if (fj < 0) continue;
                    if (kv.Value == 0.0) continue;
                    count++;
                }
                rowFillCounts[fi] = count;
            }
            csrRowPtr[0] = 0;
            for (int i = 0; i < nFree; i++) csrRowPtr[i + 1] = csrRowPtr[i] + rowFillCounts[i];
            int nnz = csrRowPtr[nFree];
            var csrCol = new int[nnz];
            var csrVal = new double[nnz];
            var csrDiag = new double[nFree];
            var fillCursor = new int[nFree];
            for (int i = 0; i < n; i++)
            {
                int fi = freeOfLocal[i];
                if (fi < 0) continue;
                csrDiag[fi] = diag[i];
                int baseOff = csrRowPtr[fi];
                int cursor = 0;
                foreach (var kv in rowMaps[i])
                {
                    int fj = freeOfLocal[kv.Key];
                    if (fj < 0) continue;
                    if (kv.Value == 0.0) continue;
                    csrCol[baseOff + cursor] = fj;
                    csrVal[baseOff + cursor] = -kv.Value; // off-diagonal sign
                    cursor++;
                }
                fillCursor[fi] = cursor;
            }

            // ── Build initial UV (centered around shell centroid) ───────────
            // Reads current UV0 from uvFlat for this shell's vertices and
            // recentres on its centroid so the local linear system has a
            // small-magnitude initial state.
            var uLocal = new double[n];
            var vLocal = new double[n];
            double cx = 0.0, cy = 0.0;
            for (int i = 0; i < n; i++)
            {
                int gi = globalOf[i];
                int idx = gi * 2;
                if ((uint)idx + 1 >= (uint)uvFlat.Length)
                {
                    return false;
                }
                uLocal[i] = uvFlat[idx];
                vLocal[i] = uvFlat[idx + 1];
                cx += uLocal[i];
                cy += vLocal[i];
            }
            cx /= n; cy /= n;
            for (int i = 0; i < n; i++) { uLocal[i] -= cx; vLocal[i] -= cy; }

            // Initial bbox + flip-count diagnostics on the recentred UV.
            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (uLocal[i] < minU) minU = uLocal[i];
                if (uLocal[i] > maxU) maxU = uLocal[i];
                if (vLocal[i] < minV) minV = vLocal[i];
                if (vLocal[i] > maxV) maxV = vLocal[i];
            }
            double bboxArea = Math.Max(0.0, maxU - minU) * Math.Max(0.0, maxV - minV);

            int flipped = 0;
            for (int t = 0; t < triValidCount; t++)
            {
                int l0 = triLocal[t * 3 + 0];
                int l1 = triLocal[t * 3 + 1];
                int l2 = triLocal[t * 3 + 2];
                double a = (uLocal[l1] - uLocal[l0]) * (vLocal[l2] - vLocal[l0])
                         - (uLocal[l2] - uLocal[l0]) * (vLocal[l1] - vLocal[l0]);
                if (a < 0) flipped++;
            }
            initialFlipCount = flipped;

            // Mirror-fix: if every triangle is CW in input UV0 (a common
            // authoring choice on symmetric models — the unwrap is mirrored)
            // the rest 2D triangles built above are CCW (x̃₂ in upper
            // half-plane), so ARAP's local rotation step would have to encode
            // a reflection it cannot represent in SO(2). Tutte fallback would
            // then discard topology entirely. Cheaper and faithful: flip uLocal
            // around 0 (centroid) so every tri becomes CCW, and run ARAP
            // normally. Recompute flipped — should drop to 0.
            if (flipped == triValidCount && triValidCount > 0)
            {
                for (int i = 0; i < n; i++) uLocal[i] = -uLocal[i];
                int reflipped = 0;
                for (int t = 0; t < triValidCount; t++)
                {
                    int l0 = triLocal[t * 3 + 0];
                    int l1 = triLocal[t * 3 + 1];
                    int l2 = triLocal[t * 3 + 2];
                    double a = (uLocal[l1] - uLocal[l0]) * (vLocal[l2] - vLocal[l0])
                             - (uLocal[l2] - uLocal[l0]) * (vLocal[l1] - vLocal[l0]);
                    if (a < 0) reflipped++;
                }
                UvtLog.Verbose(UvtLog.Category.Repack,
                    $"[ARAP] shell verts={n} tris={triValidCount}: input UV is mirrored (all {triValidCount} tris CW) → flip-fix and continue (post-flip flipped={reflipped})");
                flipped = reflipped;
            }

            // Fallback: collapsed or majority-flipped initial UV → Tutte embed
            // onto the boundary circle. Tutte requires a discoverable boundary
            // loop (open shell with a topological boundary).
            bool needFallback = (flipped > triValidCount / 2) || (bboxArea < 1e-12);
            if (needFallback)
            {
                if (!hasBoundary || boundaryLoop.Count < 3)
                {
                    // Closed shell or no usable boundary — ARAP cannot proceed.
                    return false;
                }
                if (!TutteEmbed(boundaryLoop, rowMaps, diag, n, uLocal, vLocal))
                    return false;
                // Pin must be a boundary vertex for the Tutte solution to be
                // consistent with subsequent ARAP iterations (we pin to (0,0)
                // but Tutte pins all boundary verts; we just keep our chosen
                // pinned = boundaryLoop[0] which Tutte already placed on the
                // unit circle — translate so that pinned vertex sits at (0,0)).
                double tx = uLocal[pinned];
                double ty = vLocal[pinned];
                for (int i = 0; i < n; i++) { uLocal[i] -= tx; vLocal[i] -= ty; }
            }
            else
            {
                // Standard pin: translate so the pinned vertex sits at (0,0).
                double tx = uLocal[pinned];
                double ty = vLocal[pinned];
                for (int i = 0; i < n; i++) { uLocal[i] -= tx; vLocal[i] -= ty; }
            }

            // ── ARAP local-global iterations ────────────────────────────────
            // R_T stored as (c, s) per triangle: 2x2 rotation [[c,-s],[s,c]].
            var rotC = new double[triValidCount];
            var rotS = new double[triValidCount];
            var rotCPrev = new double[triValidCount];
            var rotSPrev = new double[triValidCount];

            var bX = new double[nFree];
            var bY = new double[nFree];
            var solX = new double[nFree];
            var solY = new double[nFree];
            // Seed the CG solver with the current free-vertex UVs.
            for (int i = 0; i < n; i++)
            {
                int fi = freeOfLocal[i];
                if (fi < 0) continue;
                solX[fi] = uLocal[i];
                solY[fi] = vLocal[i];
            }

            int convergedRuns = 0;
            bool converged = false;
            int itersDone = 0;

            for (int iter = 0; iter < iterations; iter++)
            {
                itersDone = iter + 1;
                // Local step: best 2x2 rotation per triangle.
                for (int t = 0; t < triValidCount; t++)
                {
                    int l0 = triLocal[t * 3 + 0];
                    int l1 = triLocal[t * 3 + 1];
                    int l2 = triLocal[t * 3 + 2];
                    double w01 = halfCot[t * 3 + 0];
                    double w12 = halfCot[t * 3 + 1];
                    double w20 = halfCot[t * 3 + 2];
                    double rxa = rest2D[t * 6 + 0], rya = rest2D[t * 6 + 1];
                    double rxb = rest2D[t * 6 + 2], ryb = rest2D[t * 6 + 3];
                    double rxc = rest2D[t * 6 + 4], ryc = rest2D[t * 6 + 5];

                    double ux01 = uLocal[l1] - uLocal[l0], vy01 = vLocal[l1] - vLocal[l0];
                    double ux12 = uLocal[l2] - uLocal[l1], vy12 = vLocal[l2] - vLocal[l1];
                    double ux20 = uLocal[l0] - uLocal[l2], vy20 = vLocal[l0] - vLocal[l2];

                    double rx01 = rxb - rxa, ry01 = ryb - rya;
                    double rx12 = rxc - rxb, ry12 = ryc - ryb;
                    double rx20 = rxa - rxc, ry20 = rya - ryc;

                    // J = Σ w * u * x̃^T (2×2). For 2D vectors,
                    // J00 += w * u_x * x̃_x, J01 += w * u_x * x̃_y,
                    // J10 += w * u_y * x̃_x, J11 += w * u_y * x̃_y.
                    double j00 = w01 * ux01 * rx01 + w12 * ux12 * rx12 + w20 * ux20 * rx20;
                    double j01 = w01 * ux01 * ry01 + w12 * ux12 * ry12 + w20 * ux20 * ry20;
                    double j10 = w01 * vy01 * rx01 + w12 * vy12 * rx12 + w20 * vy20 * rx20;
                    double j11 = w01 * vy01 * ry01 + w12 * vy12 * ry12 + w20 * vy20 * ry20;

                    // Closed-form SO(2) closest rotation. R minimises
                    // ||J - R||² over R ∈ SO(2); R = (cos α, -sin α; sin α, cos α)
                    // with α = atan2(J10 - J01, J00 + J11).
                    double alpha = Math.Atan2(j10 - j01, j00 + j11);
                    rotC[t] = Math.Cos(alpha);
                    rotS[t] = Math.Sin(alpha);
                }

                // Global step: assemble RHS, solve L_FF · u_free = b_free.
                Array.Clear(bX, 0, nFree);
                Array.Clear(bY, 0, nFree);
                for (int t = 0; t < triValidCount; t++)
                {
                    int l0 = triLocal[t * 3 + 0];
                    int l1 = triLocal[t * 3 + 1];
                    int l2 = triLocal[t * 3 + 2];
                    double w01 = halfCot[t * 3 + 0];
                    double w12 = halfCot[t * 3 + 1];
                    double w20 = halfCot[t * 3 + 2];
                    double rxa = rest2D[t * 6 + 0], rya = rest2D[t * 6 + 1];
                    double rxb = rest2D[t * 6 + 2], ryb = rest2D[t * 6 + 3];
                    double rxc = rest2D[t * 6 + 4], ryc = rest2D[t * 6 + 5];
                    double c = rotC[t], s = rotS[t];

                    // R · x̃_edge for each oriented edge.
                    // edge01 = x̃1 - x̃0
                    double rx01 = rxb - rxa, ry01 = ryb - rya;
                    double rx12 = rxc - rxb, ry12 = ryc - ryb;
                    double rx20 = rxa - rxc, ry20 = rya - ryc;

                    double Re01x = c * rx01 - s * ry01;
                    double Re01y = s * rx01 + c * ry01;
                    double Re12x = c * rx12 - s * ry12;
                    double Re12y = s * rx12 + c * ry12;
                    double Re20x = c * rx20 - s * ry20;
                    double Re20y = s * rx20 + c * ry20;

                    // For each oriented edge (a,b): b[a] += w * R·(x̃_a - x̃_b)
                    // i.e. b[a] += -w * R·(x̃_b - x̃_a); b[b] += w * R·(x̃_b - x̃_a)
                    AddToRhs(bX, bY, freeOfLocal, l0, -w01 * Re01x, -w01 * Re01y);
                    AddToRhs(bX, bY, freeOfLocal, l1,  w01 * Re01x,  w01 * Re01y);
                    AddToRhs(bX, bY, freeOfLocal, l1, -w12 * Re12x, -w12 * Re12y);
                    AddToRhs(bX, bY, freeOfLocal, l2,  w12 * Re12x,  w12 * Re12y);
                    AddToRhs(bX, bY, freeOfLocal, l2, -w20 * Re20x, -w20 * Re20y);
                    AddToRhs(bX, bY, freeOfLocal, l0,  w20 * Re20x,  w20 * Re20y);
                }

                // Solve L_FF · x = bX (and same for Y) — Jacobi-preconditioned CG.
                ConjugateGradientSolve(csrRowPtr, csrCol, csrVal, csrDiag, bX, solX, kCgMaxIter, kCgRelTol);
                ConjugateGradientSolve(csrRowPtr, csrCol, csrVal, csrDiag, bY, solY, kCgMaxIter, kCgRelTol);

                // Write back into uLocal/vLocal; pinned vertex stays at 0.
                for (int i = 0; i < n; i++)
                {
                    int fi = freeOfLocal[i];
                    if (fi < 0) { uLocal[i] = 0.0; vLocal[i] = 0.0; continue; }
                    uLocal[i] = solX[fi];
                    vLocal[i] = solY[fi];
                }

                // Early-exit check: per-triangle rotation delta.
                if (iter > 0)
                {
                    double maxDelta = 0.0;
                    for (int t = 0; t < triValidCount; t++)
                    {
                        double dc = rotC[t] - rotCPrev[t];
                        double ds = rotS[t] - rotSPrev[t];
                        // Frobenius² of (R - R_prev) on 2×2 rot = 2*(dc² + ds²).
                        double frob2 = 2.0 * (dc * dc + ds * ds);
                        if (frob2 > maxDelta) maxDelta = frob2;
                    }
                    double frobMax = Math.Sqrt(maxDelta);
                    if (frobMax < kRotationDeltaTol)
                    {
                        convergedRuns++;
                        if (convergedRuns >= 2) { converged = true; break; }
                    }
                    else convergedRuns = 0;
                }

                Array.Copy(rotC, rotCPrev, triValidCount);
                Array.Copy(rotS, rotSPrev, triValidCount);
            }

            // ── Rescale/translate new UVs to match original shell bbox ──────
            // Goal: preserve the shell's UV0 footprint location so downstream
            // passes (texel density normalize, perturb, xatlas pack) operate
            // on the same UV scale.
            double newMinU = double.MaxValue, newMaxU = double.MinValue;
            double newMinV = double.MaxValue, newMaxV = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (uLocal[i] < newMinU) newMinU = uLocal[i];
                if (uLocal[i] > newMaxU) newMaxU = uLocal[i];
                if (vLocal[i] < newMinV) newMinV = vLocal[i];
                if (vLocal[i] > newMaxV) newMaxV = vLocal[i];
            }
            double newW = Math.Max(1e-20, newMaxU - newMinU);
            double newH = Math.Max(1e-20, newMaxV - newMinV);
            double oldW = Math.Max(1e-20, maxU - minU);
            double oldH = Math.Max(1e-20, maxV - minV);

            // ── Quality gate: only accept the new UV if it's actually better ──
            // ARAP can converge to a degenerate / heavily-flipped layout when
            // the input is pathological (Tutte fallback used, near-collinear
            // boundary, high-curvature ribbon). In those cases overwriting the
            // original UV0 with the worse result actively hurts downstream
            // pack/density passes. Reject if the output is worse on any
            // tracked metric and let the caller keep the original UVs.
            int outFlipCount = 0;
            int outDegenerate = 0;
            for (int t = 0; t < triValidCount; t++)
            {
                int l0 = triLocal[t * 3 + 0];
                int l1 = triLocal[t * 3 + 1];
                int l2 = triLocal[t * 3 + 2];
                double a = (uLocal[l1] - uLocal[l0]) * (vLocal[l2] - vLocal[l0])
                         - (uLocal[l2] - uLocal[l0]) * (vLocal[l1] - vLocal[l0]);
                if (a < 0) outFlipCount++;
                if (Math.Abs(a) < 1e-12) outDegenerate++;
            }
            double outBboxArea = Math.Max(0.0, newMaxU - newMinU) * Math.Max(0.0, newMaxV - newMinV);
            // initialFlipCount above is the count BEFORE mirror-fix; compare
            // against `flipped`, the post-mirror count, so the gate is
            // consistent with the layout we actually started ARAP with.
            int gateInitFlips = flipped;
            bool rejectFlips      = outFlipCount > gateInitFlips;
            bool rejectDegenerate = outDegenerate > triValidCount * 0.05;
            bool rejectCollapse   = outBboxArea < bboxArea * 0.3;
            if (rejectFlips || rejectDegenerate || rejectCollapse)
            {
                UvtLog.Verbose(UvtLog.Category.Repack,
                    $"[ARAP] shell verts={n} tris={triValidCount}/{triCount} REJECTED: " +
                    $"outFlips={outFlipCount}/{triValidCount} (init={gateInitFlips}), " +
                    $"outDegenerate={outDegenerate}, bboxArea={outBboxArea:G3}/{bboxArea:G3} " +
                    $"→ keeping original UV0");
                return false;
            }

            // Uniform area-preserving scale so the new UV bbox has the same
            // area as the original (texel-density normalization later will
            // re-scale anyway, but we keep the order of magnitude sane).
            double oldArea = oldW * oldH;
            double newArea = newW * newH;
            double scale = Math.Sqrt(oldArea / newArea);
            if (!IsFiniteD(scale) || scale <= 0.0) scale = 1.0;

            double targetCx = 0.5 * (minU + maxU) + cx; // back into global UV system
            double targetCy = 0.5 * (minV + maxV) + cy;
            double srcCx = 0.5 * (newMinU + newMaxU);
            double srcCy = 0.5 * (newMinV + newMaxV);

            // Two-pass write so a NaN bail-out doesn't leave UVs in a mixed
            // state (first compute everything into temporary buffers; only
            // copy to uvFlat after all values pass the finite-ness check).
            var outU = new double[n];
            var outV = new double[n];
            for (int i = 0; i < n; i++)
            {
                outU[i] = (uLocal[i] - srcCx) * scale + targetCx;
                outV[i] = (vLocal[i] - srcCy) * scale + targetCy;
                if (!IsFiniteD(outU[i]) || !IsFiniteD(outV[i]) ||
                    Math.Abs(outU[i]) > float.MaxValue || Math.Abs(outV[i]) > float.MaxValue)
                    return false;
            }
            for (int i = 0; i < n; i++)
            {
                int gi = globalOf[i];
                int idx = gi * 2;
                if ((uint)idx + 1 >= (uint)uvFlat.Length) continue;
                uvFlat[idx]     = (float)outU[i];
                uvFlat[idx + 1] = (float)outV[i];
            }

            UvtLog.Verbose(UvtLog.Category.Repack,
                $"[ARAP] shell verts={n} tris={triValidCount}/{triCount} (degenerate={degenerateTris}) iters={itersDone}/{iterations} initFlipped={initialFlipCount} converged={converged}");

            return true;
        }

        // ────────────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────────────

        static void AccumulateLaplacian(
            Dictionary<int, double>[] rowMaps, double[] diag,
            int i, int j, double w)
        {
            if (i == j || w == 0.0) return;
            // Off-diag += w (so L[i,j] = -Σ contributions).
            rowMaps[i].TryGetValue(j, out double cur);
            rowMaps[i][j] = cur + w;
            rowMaps[j].TryGetValue(i, out double cur2);
            rowMaps[j][i] = cur2 + w;
            diag[i] += w;
            diag[j] += w;
        }

        static void AddToRhs(double[] bX, double[] bY, int[] freeOfLocal,
                              int localIdx, double dx, double dy)
        {
            int fi = freeOfLocal[localIdx];
            if (fi < 0) return;
            bX[fi] += dx;
            bY[fi] += dy;
        }

        /// <summary>
        /// Find a boundary loop for the shell. Boundary edges are half-edges
        /// that appear only once in this shell's triangle list. Walks the
        /// boundary as an ordered cycle starting from the lowest local index
        /// for determinism. Returns the longest cycle found.
        /// </summary>
        static List<int> FindBoundaryLoop(int[] triLocal, int triCount, int n, out bool hasBoundary)
        {
            hasBoundary = false;
            // Half-edge count per directed pair (a,b) with a<b.
            var edgeCount = new Dictionary<long, int>();
            // Directed half-edges for boundary traversal: from each vertex
            // we store the next vertex in the half-edge that has no twin.
            for (int t = 0; t < triCount; t++)
            {
                int a = triLocal[t * 3 + 0];
                int b = triLocal[t * 3 + 1];
                int c = triLocal[t * 3 + 2];
                BumpEdge(edgeCount, a, b);
                BumpEdge(edgeCount, b, c);
                BumpEdge(edgeCount, c, a);
            }

            // Collect boundary edges as undirected pairs whose count == 1.
            // For each boundary vertex, list its boundary neighbors.
            var nbrs = new Dictionary<int, List<int>>();
            foreach (var kv in edgeCount)
            {
                if (kv.Value != 1) continue;
                long key = kv.Key;
                int lo = (int)(key >> 32);
                int hi = (int)(key & 0xFFFFFFFFL);
                AddNeighbor(nbrs, lo, hi);
                AddNeighbor(nbrs, hi, lo);
            }

            if (nbrs.Count == 0) return new List<int>();
            hasBoundary = true;

            // Walk the boundary starting from the lowest-index boundary vertex
            // and following any available neighbor. This finds a single cycle
            // (boundary vertices have valence-2 in the boundary subgraph for
            // a manifold open patch).
            var visited = new HashSet<int>();
            int start = int.MaxValue;
            foreach (var k in nbrs.Keys) if (k < start) start = k;

            var loop = new List<int>();
            int cur = start;
            int prev = -1;
            while (true)
            {
                if (visited.Contains(cur)) break;
                visited.Add(cur);
                loop.Add(cur);
                if (!nbrs.TryGetValue(cur, out var list) || list.Count == 0) break;
                int next = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != prev) { next = list[i]; break; }
                }
                if (next < 0) break;
                prev = cur;
                cur = next;
                if (cur == start) break;
            }
            return loop;
        }

        static void BumpEdge(Dictionary<long, int> edgeCount, int a, int b)
        {
            if (a == b) return;
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            long key = ((long)lo << 32) | (uint)hi;
            edgeCount.TryGetValue(key, out int c);
            edgeCount[key] = c + 1;
        }

        static void AddNeighbor(Dictionary<int, List<int>> nbrs, int from, int to)
        {
            if (!nbrs.TryGetValue(from, out var list))
            {
                list = new List<int>(2);
                nbrs[from] = list;
            }
            list.Add(to);
        }

        /// <summary>
        /// Tutte embedding: pin boundary vertices to the unit circle, solve
        /// L_inner · u_inner = -L_boundary_inner · u_boundary for interior
        /// positions. Used as ARAP initial guess when the input UV0 is
        /// degenerate.
        /// </summary>
        static bool TutteEmbed(
            List<int> boundaryLoop,
            Dictionary<int, double>[] rowMaps,
            double[] diag,
            int n,
            double[] outU,
            double[] outV)
        {
            if (boundaryLoop == null || boundaryLoop.Count < 3) return false;

            // Mark boundary vertices and assign each one a circle position.
            var isBoundary = new bool[n];
            var bU = new double[n];
            var bV = new double[n];
            int bCount = boundaryLoop.Count;
            for (int i = 0; i < bCount; i++)
            {
                int v = boundaryLoop[i];
                if (v < 0 || v >= n) return false;
                isBoundary[v] = true;
                double theta = 2.0 * Math.PI * i / bCount;
                bU[v] = Math.Cos(theta);
                bV[v] = Math.Sin(theta);
            }

            // Interior free indices.
            int nInner = n - bCount;
            if (nInner == 0)
            {
                for (int i = 0; i < n; i++)
                {
                    outU[i] = bU[i];
                    outV[i] = bV[i];
                }
                return true;
            }
            var freeOfLocal = new int[n];
            int fi = 0;
            for (int i = 0; i < n; i++)
            {
                if (isBoundary[i]) { freeOfLocal[i] = -1; }
                else freeOfLocal[i] = fi++;
            }

            // Build CSR of the inner×inner Laplacian and RHS from boundary
            // constraints.
            var rowPtr = new int[nInner + 1];
            var counts = new int[nInner];
            for (int i = 0; i < n; i++)
            {
                int ri = freeOfLocal[i];
                if (ri < 0) continue;
                int cnt = 0;
                foreach (var kv in rowMaps[i])
                {
                    int rj = freeOfLocal[kv.Key];
                    if (rj < 0) continue;
                    if (kv.Value == 0.0) continue;
                    cnt++;
                }
                counts[ri] = cnt;
            }
            rowPtr[0] = 0;
            for (int i = 0; i < nInner; i++) rowPtr[i + 1] = rowPtr[i] + counts[i];
            int nnz = rowPtr[nInner];
            var col = new int[nnz];
            var val = new double[nnz];
            var diagFree = new double[nInner];
            var rhsU = new double[nInner];
            var rhsV = new double[nInner];
            var cursor = new int[nInner];

            for (int i = 0; i < n; i++)
            {
                int ri = freeOfLocal[i];
                if (ri < 0) continue;
                diagFree[ri] = diag[i];
                int baseOff = rowPtr[ri];
                int cur = 0;
                foreach (var kv in rowMaps[i])
                {
                    int rj = freeOfLocal[kv.Key];
                    double w = kv.Value;
                    if (w == 0.0) continue;
                    if (rj < 0)
                    {
                        // Boundary contribution: L · u_boundary moves into RHS.
                        // L[i,j_bnd] = -w, and we solve L·u = 0 (Tutte = harmonic),
                        // so rhs[ri] = -(-w * u_boundary[j]) = w * u_boundary[j].
                        int gj = kv.Key;
                        rhsU[ri] += w * bU[gj];
                        rhsV[ri] += w * bV[gj];
                        continue;
                    }
                    col[baseOff + cur] = rj;
                    val[baseOff + cur] = -w;
                    cur++;
                }
                cursor[ri] = cur;
            }

            var solU = new double[nInner];
            var solV = new double[nInner];
            ConjugateGradientSolve(rowPtr, col, val, diagFree, rhsU, solU, kCgMaxIter, kCgRelTol);
            ConjugateGradientSolve(rowPtr, col, val, diagFree, rhsV, solV, kCgMaxIter, kCgRelTol);

            for (int i = 0; i < n; i++)
            {
                int ri = freeOfLocal[i];
                if (ri < 0) { outU[i] = bU[i]; outV[i] = bV[i]; }
                else { outU[i] = solU[ri]; outV[i] = solV[ri]; }
                if (!IsFiniteD(outU[i]) || !IsFiniteD(outV[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// Solve L · x = b where L is symmetric positive (semi-)definite,
        /// stored as CSR + separate diagonal array. Uses Jacobi
        /// preconditioning M⁻¹ = diag(L)⁻¹. x carries the initial guess
        /// in and the solution out.
        /// </summary>
        static bool ConjugateGradientSolve(
            int[] rowPtr, int[] col, double[] val, double[] diag,
            double[] b, double[] x,
            int maxIter, double relTol)
        {
            int n = b.Length;
            if (n == 0) return true;
            var r = new double[n];
            var z = new double[n];
            var p = new double[n];
            var Ap = new double[n];

            // r = b - A·x
            SpMv(rowPtr, col, val, diag, x, Ap);
            double bNorm2 = 0.0;
            for (int i = 0; i < n; i++)
            {
                r[i] = b[i] - Ap[i];
                bNorm2 += b[i] * b[i];
            }
            double rNorm2 = 0.0;
            for (int i = 0; i < n; i++) rNorm2 += r[i] * r[i];
            double tol2 = relTol * relTol * Math.Max(bNorm2, 1e-30);
            if (rNorm2 <= tol2) return true;

            // z = M⁻¹ r, p = z
            for (int i = 0; i < n; i++)
            {
                double d = diag[i];
                z[i] = d > 0.0 ? r[i] / d : r[i];
                p[i] = z[i];
            }
            double rz = 0.0;
            for (int i = 0; i < n; i++) rz += r[i] * z[i];

            for (int it = 0; it < maxIter; it++)
            {
                SpMv(rowPtr, col, val, diag, p, Ap);
                double pAp = 0.0;
                for (int i = 0; i < n; i++) pAp += p[i] * Ap[i];
                if (pAp <= 0.0 || double.IsNaN(pAp)) return false;
                double alpha = rz / pAp;
                rNorm2 = 0.0;
                for (int i = 0; i < n; i++)
                {
                    x[i] += alpha * p[i];
                    r[i] -= alpha * Ap[i];
                    rNorm2 += r[i] * r[i];
                }
                if (rNorm2 <= tol2) return true;

                for (int i = 0; i < n; i++)
                {
                    double d = diag[i];
                    z[i] = d > 0.0 ? r[i] / d : r[i];
                }
                double rzNew = 0.0;
                for (int i = 0; i < n; i++) rzNew += r[i] * z[i];
                double beta = rzNew / Math.Max(rz, 1e-30);
                for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
                rz = rzNew;
            }
            return false; // not converged within maxIter — caller still uses x
        }

        /// <summary>Sparse mat-vec: y = A · x for A stored as CSR off-diagonals + separate diag.</summary>
        static void SpMv(int[] rowPtr, int[] col, double[] val, double[] diag,
                          double[] x, double[] y)
        {
            int n = diag.Length;
            for (int i = 0; i < n; i++)
            {
                double acc = diag[i] * x[i];
                int start = rowPtr[i], end = rowPtr[i + 1];
                for (int k = start; k < end; k++) acc += val[k] * x[col[k]];
                y[i] = acc;
            }
        }

        static bool IsFiniteD(double x)
        {
            return !(double.IsNaN(x) || double.IsInfinity(x));
        }
    }
}
