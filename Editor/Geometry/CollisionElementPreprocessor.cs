using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Geometry-only preparation of independently connected elements.
    /// Every replacement passes topology and bidirectional surface-distance checks.</summary>
    internal static class CollisionElementPreprocessor
    {
        internal static bool ValidSettings(CollisionMeshBuilder.ConvexDecompSettings s) =>
            Finite(s.boxFillThreshold) && s.boxFillThreshold >= .5f && s.boxFillThreshold <= 1 &&
            Finite(s.elementFitError) && s.elementFitError >= .0001f && s.elementFitError <= .1f &&
            s.elementRemeshResolution >= 16 && s.elementRemeshResolution <= 256 &&
            s.elementTargetTriangles >= 12 && s.elementTargetTriangles <= 4096;

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static RemeshNative.IndexedMesh Prepare(Vector3[] positions, List<int> indices,
            CollisionMeshBuilder.ConvexDecompSettings settings, out CollisionMeshBuilder.ElementReport report) =>
            PrepareCore(Compact(positions, indices), settings, out report,
                input => RemeshAndDecimate(input, settings), input => Decimate(input, settings));

        internal static RemeshNative.IndexedMesh PrepareCore(RemeshNative.IndexedMesh source,
            CollisionMeshBuilder.ConvexDecompSettings settings, out CollisionMeshBuilder.ElementReport report,
            Func<RemeshNative.IndexedMesh, RemeshNative.IndexedMesh> remesh,
            Func<RemeshNative.IndexedMesh, RemeshNative.IndexedMesh> decimate)
        {
            var points = new List<Vector3>(new HashSet<Vector3>(source.positions));
            var bounds = MeshGeometry.PrincipalBounds(points);
            var topology = RemeshTopology.Inspect(source.positions, source.indices, CancellationToken.None);
            bool closed = topology.Valid && topology.boundary.Count == 0;
            var size = bounds.Size;
            double boxVolume = (double)size.x * size.y * size.z;
            float fill = closed && boxVolume > 0 ? Mathf.Clamp01((float)(Volume(source, bounds.origin) / boxVolume)) : -1f;
            var extents = new[] { size.x, size.y, size.z };
            Array.Sort(extents);
            report = new CollisionMeshBuilder.ElementReport {
                sourceTriangles = source.TriangleCount, preparedTriangles = source.TriangleCount,
                principalSize = new Vector3(extents[2], extents[1], extents[0]), boxFill = fill,
                preparation = CollisionMeshBuilder.ElementPreparation.Source
            };
            float diagonal = size.magnitude;
            float reach = diagonal * settings.elementFitError;
            if (closed && fill >= settings.boxFillThreshold && size.x > 0 && size.y > 0 && size.z > 0)
            {
                var box = Box(bounds);
                if (Accept(source, box, topology, reach, out float distance, out _))
                {
                    report.preparation = CollisionMeshBuilder.ElementPreparation.Box;
                    report.preparedTriangles = box.TriangleCount;
                    report.sampledError = distance / diagonal;
                    report.detail = "Closed, densely filled oriented bounds; box passes surface fit and topology checks.";
                    return box;
                }
            }
            string reason;
            try
            {
                var candidate = remesh(source);
                if (!Useful(source, candidate, closed))
                    reason = "Remesh did not reduce the element triangle count.";
                else if (Accept(source, candidate, topology, reach, out float distance, out reason, allowClosure: true))
                {
                    report.preparation = CollisionMeshBuilder.ElementPreparation.RemeshDecimate;
                    report.preparedTriangles = candidate.TriangleCount;
                    report.sampledError = distance / diagonal;
                    report.detail = "Per-element remesh and geometry-only decimation passed topology and surface fit checks.";
                    return candidate;
                }
            }
            catch (InvalidOperationException e) { reason = "Remesh rejected: " + e.Message; }
            try
            {
                var candidate = decimate(source);
                if (Useful(source, candidate, true) && Accept(source, candidate, topology, reach, out float distance, out _))
                {
                    report.preparation = CollisionMeshBuilder.ElementPreparation.Decimate;
                    report.preparedTriangles = candidate.TriangleCount;
                    report.sampledError = distance / diagonal;
                    report.detail = reason + " Used geometry-only source decimation instead.";
                    return candidate;
                }
            }
            catch (InvalidOperationException e) { reason += " Decimation rejected: " + e.Message; }
            report.detail = reason + " Kept the original element geometry.";
            return source;
        }

        static bool Useful(RemeshNative.IndexedMesh source, RemeshNative.IndexedMesh candidate, bool sourceClosed) =>
            candidate != null && candidate.TriangleCount > 0 && (!sourceClosed || candidate.TriangleCount < source.TriangleCount);

        internal static bool Accept(RemeshNative.IndexedMesh source, RemeshNative.IndexedMesh candidate,
            RemeshTopology.Snapshot original, float reach, out float sampledDistance, out string reason, bool allowClosure = false)
        {
            sampledDistance = 0;
            var after = RemeshTopology.Inspect(candidate.positions, candidate.indices, CancellationToken.None);
            if (!after.Valid || allowClosure && after.boundary.Count != 0)
            {
                reason = "Replacement introduces invalid faces or fails to build a closed solid.";
                return false;
            }
            // Solid remeshing intentionally closes an open element. Its Euler
            // characteristic and borders then change, so use component count and
            // bidirectional surface distance to judge that repair. Closed sources
            // retain their genus; direct decimation retains authored open borders.
            bool closingOpenSource = allowClosure && original.Valid && original.boundary.Count > 0;
            if (original.Valid && (closingOpenSource
                ? after.euler.Count != original.euler.Count
                : !after.PreservesComponents(original, false)))
            {
                reason = "Replacement changes component topology.";
                return false;
            }
            if (original.Valid && !closingOpenSource && !after.PreservesBoundary(original))
            {
                reason = "Replacement changes an authored open border.";
                return false;
            }
            sampledDistance = MaxSurfaceDistance(source, candidate);
            reason = sampledDistance <= reach ? null : "Replacement exceeds bidirectional sampled surface distance.";
            return reason == null;
        }

        internal static float MaxSurfaceDistance(RemeshNative.IndexedMesh a, RemeshNative.IndexedMesh b) =>
            Mathf.Sqrt(Mathf.Max(SampleDistanceSquared(a, new TriangleBvh(b.positions, b.indices)),
                SampleDistanceSquared(b, new TriangleBvh(a.positions, a.indices))));

        static float SampleDistanceSquared(RemeshNative.IndexedMesh mesh, TriangleBvh target)
        {
            float distance = 0;
            foreach (var point in mesh.positions) distance = Mathf.Max(distance, target.FindNearest(point).distSq);
            for (int i = 0; i < mesh.indices.Length; i += 3)
            {
                var a = mesh.positions[mesh.indices[i]];
                var b = mesh.positions[mesh.indices[i + 1]];
                var c = mesh.positions[mesh.indices[i + 2]];
                distance = Mathf.Max(distance, target.FindNearest((a + b) * .5f).distSq);
                distance = Mathf.Max(distance, target.FindNearest((b + c) * .5f).distSq);
                distance = Mathf.Max(distance, target.FindNearest((c + a) * .5f).distSq);
                distance = Mathf.Max(distance, target.FindNearest((a + b + c) / 3f).distSq);
            }
            return distance;
        }

        static RemeshNative.IndexedMesh RemeshAndDecimate(RemeshNative.IndexedMesh source, CollisionMeshBuilder.ConvexDecompSettings s)
        {
            RemeshNative.CheckAvailable();
            var options = Options(s);
            var voxel = RemeshNative.Voxelize(source.positions, source.indices, options, CancellationToken.None);
            return RemeshNative.Simplify(voxel, options, CancellationToken.None, out _);
        }

        static RemeshNative.IndexedMesh Decimate(RemeshNative.IndexedMesh source, CollisionMeshBuilder.ConvexDecompSettings s) =>
            RemeshNative.Simplify(source, Options(s), CancellationToken.None, out _);

        static RemeshSettings Options(CollisionMeshBuilder.ConvexDecompSettings s) => new RemeshSettings {
            voxelResolution = s.elementRemeshResolution, solve = true, shell = false, trimToSource = false,
            maximumError = s.elementFitError * .5f, targetTriangles = s.elementTargetTriangles,
            regularize = RemeshRegularize.Light, preserveFolds = true, pruneSmallParts = false
        };

        static RemeshNative.IndexedMesh Compact(Vector3[] positions, List<int> indices)
        {
            var remap = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var triangles = new int[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                int index = indices[i];
                if (!remap.TryGetValue(index, out int slot))
                {
                    slot = vertices.Count;
                    remap.Add(index, slot);
                    vertices.Add(positions[index]);
                }
                triangles[i] = slot;
            }
            return new RemeshNative.IndexedMesh { positions = vertices.ToArray(), indices = triangles };
        }

        static double Volume(RemeshNative.IndexedMesh mesh, Vector3 origin)
        {
            double volume = 0;
            for (int i = 0; i < mesh.indices.Length; i += 3)
            {
                var a = mesh.positions[mesh.indices[i]] - origin;
                var b = mesh.positions[mesh.indices[i + 1]] - origin;
                var c = mesh.positions[mesh.indices[i + 2]] - origin;
                volume += (double)a.x * ((double)b.y * c.z - (double)b.z * c.y) +
                    (double)a.y * ((double)b.z * c.x - (double)b.x * c.z) +
                    (double)a.z * ((double)b.x * c.y - (double)b.y * c.x);
            }
            return Math.Abs(volume) / 6;
        }

        static RemeshNative.IndexedMesh Box(MeshGeometry.OrientedBounds bounds)
        {
            var p = new Vector3[8];
            var min = bounds.min; var max = bounds.max;
            p[0] = bounds.Point(new Vector3(min.x, min.y, min.z));
            p[1] = bounds.Point(new Vector3(max.x, min.y, min.z));
            p[2] = bounds.Point(new Vector3(max.x, max.y, min.z));
            p[3] = bounds.Point(new Vector3(min.x, max.y, min.z));
            p[4] = bounds.Point(new Vector3(min.x, min.y, max.z));
            p[5] = bounds.Point(new Vector3(max.x, min.y, max.z));
            p[6] = bounds.Point(new Vector3(max.x, max.y, max.z));
            p[7] = bounds.Point(new Vector3(min.x, max.y, max.z));
            return new RemeshNative.IndexedMesh { positions = p,
                indices = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 } };
        }
    }
}
