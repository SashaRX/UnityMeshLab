using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Whole-attempt transfer defects compared by Full Pipeline auto-tune. Fields
    /// are compared in declaration order, and lower is better except coverage.
    /// Collapsed and invalid UV faces and overlap area come from the final
    /// <see cref="TransferUvQuality"/> of every target. Shell-level counters
    /// (rejected shells, force3D overlap detections) only break ties.
    /// </summary>
    internal struct AutoTuneScore
    {
        /// <summary>Targets whose transfer produced no UVs.</summary>
        internal int failedTargets;
        /// <summary>Targets whose overlap scan stopped at its budget; their overlap count is a lower bound.</summary>
        internal int incompleteScans;
        /// <summary>Invalid plus collapsed UV faces over all targets; zero-area 3D faces are excluded.</summary>
        internal int collapsedFaces;
        /// <summary>Target shells left unwritten after rejection.</summary>
        internal int rejectedShells;
        /// <summary>Summed positive overlap area; unlike pair counts it is not inflated by seam subdivision.</summary>
        internal double overlapArea;
        /// <summary>Overlapping face pairs; used only to decide a perfect result.</summary>
        internal int overlapPairs;
        /// <summary>force3D shells whose UV2 bounds overlap (the previous auto-tune issue counter).</summary>
        internal int overlapsFixed;
        /// <summary>Transferred fraction of target vertices; higher is better.</summary>
        internal float coverage;

        /// <summary>A score no measured attempt can lose to.</summary>
        internal static AutoTuneScore Worst => new AutoTuneScore {
            failedTargets = int.MaxValue, incompleteScans = int.MaxValue, collapsedFaces = int.MaxValue,
            rejectedShells = int.MaxValue, overlapArea = double.PositiveInfinity, overlapPairs = int.MaxValue,
            overlapsFixed = int.MaxValue, coverage = float.NegativeInfinity };

        /// <summary>
        /// Builds a score from every target's final quality, paired with that
        /// target's zero-area 3D face count, and the shell-transfer totals.
        /// </summary>
        internal static AutoTuneScore From(IEnumerable<(TransferUvQuality quality, int geometryDegenerateFaces)> targets,
            int failedTargets, int rejectedShells, int overlapsFixed, float coverage)
        {
            var score = new AutoTuneScore { failedTargets = failedTargets, rejectedShells = rejectedShells,
                overlapsFixed = overlapsFixed, coverage = coverage };
            foreach (var (quality, geometryDegenerateFaces) in targets) score.AddTarget(quality, geometryDegenerateFaces);
            return score;
        }

        /// <summary>Adds one target's aggregate defects; nothing from the quality report is retained.</summary>
        internal void AddTarget(TransferUvQuality quality, int geometryDegenerateFaces)
        {
            if (!quality.overlapScanComplete) ++incompleteScans;
            // The same geometry degeneracy is present in every attempt and no
            // configuration can repair it; it must not block a perfect result.
            collapsedFaces += quality.invalidFaces + Math.Max(0, quality.degenerateFaces - geometryDegenerateFaces);
            overlapArea += quality.overlapPairArea;
            overlapPairs += quality.overlapPairs;
        }

        /// <summary>
        /// Counts faces that <see cref="TransferUvQuality.Measure"/> reports as degenerate
        /// from geometry alone, in the same frame and by the same predicate, before any UV test.
        /// </summary>
        internal static int GeometryDegenerateFaces(Mesh mesh, Matrix4x4 localToWorld)
        {
            var positions = mesh.vertices; var triangles = mesh.triangles;
            int count = 0;
            for (int f = 0; f + 2 < triangles.Length; f += 3)
            {
                var frame = TransferUvQuality.WorldFrame(positions[triangles[f]], positions[triangles[f + 1]],
                    positions[triangles[f + 2]], localToWorld);
                if (double.IsNaN(frame.length) || double.IsInfinity(frame.length)) continue;
                if (frame.length <= 0) { ++count; continue; }
                double cross = frame.length * frame.y;
                if (!double.IsNaN(cross) && !double.IsInfinity(cross) && cross <= 0) ++count;
            }
            return count;
        }

        /// <summary>No measured defect and at least 99% vertex coverage; auto-tune stops here.</summary>
        internal bool Perfect => failedTargets == 0 && incompleteScans == 0 && collapsedFaces == 0
            && rejectedShells == 0 && overlapPairs == 0 && overlapsFixed == 0 && coverage >= .99f;

        /// <summary>Strict lexicographic improvement; equal attempts never replace each other.</summary>
        internal bool IsBetterThan(AutoTuneScore other)
        {
            if (failedTargets != other.failedTargets) return failedTargets < other.failedTargets;
            if (incompleteScans != other.incompleteScans) return incompleteScans < other.incompleteScans;
            if (collapsedFaces != other.collapsedFaces) return collapsedFaces < other.collapsedFaces;
            if (rejectedShells != other.rejectedShells) return rejectedShells < other.rejectedShells;
            // Float sums: differences within one part in a million are noise, not an improvement.
            bool finiteAreas = !double.IsInfinity(overlapArea) && !double.IsInfinity(other.overlapArea);
            double tolerance = 1e-6 * Math.Max(Math.Max(Math.Abs(overlapArea), Math.Abs(other.overlapArea)), 1e-12);
            bool sameArea = finiteAreas
                ? Math.Abs(overlapArea - other.overlapArea) <= tolerance
                : double.IsInfinity(overlapArea) && double.IsInfinity(other.overlapArea)
                    && Math.Sign(overlapArea) == Math.Sign(other.overlapArea);
            if (!sameArea) return overlapArea < other.overlapArea;
            if (overlapsFixed != other.overlapsFixed) return overlapsFixed < other.overlapsFixed;
            return coverage > other.coverage;
        }

        public override string ToString() =>
            $"failed={failedTargets}, incompleteScans={incompleteScans}, collapsed={collapsedFaces}, rejected={rejectedShells}, " +
            $"overlapArea={overlapArea:G4}, overlapPairs={overlapPairs}, force3DOverlaps={overlapsFixed}, vertexCoverage={coverage:P0}";
    }
}
