using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Mask applied to the voxel remesh: the voxelizer closes every surface, so an open
    /// source (a wall sheet, a roof plane, a curtain) comes back as a slab with a
    /// front, a back and rims — and since the remesher fits its vertices onto the
    /// input surface, the front and the back lie ON the sheet, a zero-thickness
    /// double-sided surface. A remesh face stays only when the source has a face near
    /// it (within a couple of voxel cells) whose normal points the same way; the back
    /// side of a one-sided sheet and the slab's rims have none and go. Closed sources
    /// are left whole, and a source that is double-sided where it matters keeps both
    /// sides. Unreferenced vertices are compacted away.
    /// </summary>
    internal static class RemeshTrim
    {
        /// <summary>Minimum alignment between a remesh face and the source face that keeps it.</summary>
        internal const float MinDot = 0.25f;

        internal static RemeshNative.IndexedMesh Trim(RemeshNative.IndexedMesh mesh, Vector3[] sourcePositions, int[] sourceIndices,
            float maxDistance, CancellationToken token, out int removedFaces)
            => Trim(mesh, sourcePositions, sourceIndices, maxDistance, token, out removedFaces, out _);

        /// <summary>gaveUp: neither the source's winding nor its inverse kept a tenth of
        /// the remesh, so nothing was trimmed — the source is far from the remesh or
        /// its winding is mixed beyond a single flip.</summary>
        internal static RemeshNative.IndexedMesh Trim(RemeshNative.IndexedMesh mesh, Vector3[] sourcePositions, int[] sourceIndices,
            float maxDistance, CancellationToken token, out int removedFaces, out bool gaveUp)
        {
            removedFaces = 0; gaveUp = false;
            int faces = mesh.indices.Length / 3;
            if (faces == 0 || sourceIndices.Length < 3) return mesh;
            var bvh = new TriangleBvh(sourcePositions, sourceIndices);
            var sourceNormals = FaceNormals(sourcePositions, sourceIndices);
            var keep = Classify(mesh, bvh, sourceNormals, maxDistance, token, out int kept);
            // A source wound inside out would reject everything; judge it by its flipped
            // normals instead, and give up (keep all) when neither reading keeps a tenth.
            if (kept * 10 < faces) {
                for (int i = 0; i < sourceNormals.Length; ++i) sourceNormals[i] = -sourceNormals[i];
                var flipped = Classify(mesh, bvh, sourceNormals, maxDistance, token, out int keptFlipped);
                if (keptFlipped > kept) { keep = flipped; kept = keptFlipped; }
                if (kept * 10 < faces) { gaveUp = true; return mesh; }
            }
            removedFaces = faces - kept;
            if (removedFaces == 0) return mesh;
            return Compact(mesh, keep, kept);
        }

        // A face stays when a source face lies within reach that points the SAME way
        // (dot ≥ MinDot). Orientation is the only usable test: the remesher fits its
        // output vertices onto the input surface, so the slab around a sheet is not a
        // cell thick but collapses onto the sheet with its front and back faces
        // coincident — a position test ("is the centroid behind the sheet") sees both
        // at zero distance and keeps both, which is exactly the double-sided result
        // that breaks the cage and the bake. The back face's normal is opposite to the
        // sheet's, the rims are perpendicular; both fail the alignment and go. A source
        // wound inside out is handled by the caller's flipped retry.
        static bool[] Classify(RemeshNative.IndexedMesh mesh, TriangleBvh bvh, Vector3[] sourceNormals, float maxDistance, CancellationToken token, out int kept)
        {
            int faces = mesh.indices.Length / 3;
            var keep = new bool[faces];
            int count = 0;
            Parallel.For(0, faces, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f => {
                int a = mesh.indices[f * 3], b = mesh.indices[f * 3 + 1], c = mesh.indices[f * 3 + 2];
                Vector3 pa = mesh.positions[a], pb = mesh.positions[b], pc = mesh.positions[c];
                Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
                if (normal.sqrMagnitude < 1e-30f) return;   // degenerate: drop
                normal.Normalize();
                Vector3 centroid = (pa + pb + pc) / 3f;
                var hit = bvh.FindNearestNormalFiltered(centroid, normal, sourceNormals, MinDot, maxDistance);
                if (hit.triangleIndex < 0) return;
                keep[f] = true; Interlocked.Increment(ref count);
            });
            kept = count;
            return keep;
        }

        static RemeshNative.IndexedMesh Compact(RemeshNative.IndexedMesh mesh, bool[] keep, int kept)
        {
            var remap = new int[mesh.positions.Length];
            for (int i = 0; i < remap.Length; ++i) remap[i] = -1;
            var indices = new int[kept * 3];
            int next = 0, write = 0;
            for (int f = 0; f < keep.Length; ++f) {
                if (!keep[f]) continue;
                for (int k = 0; k < 3; ++k) {
                    int v = mesh.indices[f * 3 + k];
                    if (remap[v] < 0) remap[v] = next++;
                    indices[write++] = remap[v];
                }
            }
            var positions = new Vector3[next];
            for (int i = 0; i < remap.Length; ++i) if (remap[i] >= 0) positions[remap[i]] = mesh.positions[i];
            return new RemeshNative.IndexedMesh { positions = positions, indices = indices };
        }

        static Vector3[] FaceNormals(Vector3[] positions, int[] indices)
        {
            var normals = new Vector3[indices.Length / 3];
            for (int f = 0; f < normals.Length; ++f) {
                int a = indices[f * 3], b = indices[f * 3 + 1], c = indices[f * 3 + 2];
                normals[f] = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).normalized;
            }
            return normals;
        }
    }
}
