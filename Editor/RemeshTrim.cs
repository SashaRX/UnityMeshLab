using System;
using System.Collections.Generic;
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
    /// side of a sheet (opposite normal) and the slab's rims (perpendicular, or no
    /// source nearby) go. Closed sources are left whole, a source modeled with both
    /// faces of a wall keeps both. A two-sided MATERIAL does not keep the back: the
    /// result is one sheet, and the result material renders both sides instead.
    /// After the cut the kept sheet is re-wound to one consistent orientation per
    /// connected piece (a two-sided source is often modeled with arbitrary winding),
    /// the majority of each piece keeping the side that agreed with the source.
    /// Unreferenced vertices are compacted away.
    /// </summary>
    internal static class RemeshTrim
    {
        /// <summary>Minimum alignment between a remesh face and the source face that keeps it.</summary>
        internal const float MinDot = 0.25f;

        /// <summary>Per raw remesh face: kept, the back of a sheet (a source face lies
        /// within reach but faces the other way), or a rim (nothing parallel within reach).</summary>
        internal const byte Kept = 0, Back = 1, Rim = 2;

        internal sealed class Result
        {
            public RemeshNative.IndexedMesh mesh;   // trimmed (or the input when nothing went)
            public byte[] classes;                  // per face of the INPUT mesh
            public int removed, flipped;
            public bool gaveUp;                     // neither winding kept a tenth: nothing trimmed
        }

        internal static RemeshNative.IndexedMesh Trim(RemeshNative.IndexedMesh mesh, Vector3[] sourcePositions, int[] sourceIndices,
            float maxDistance, CancellationToken token, out int removedFaces)
        {
            var result = Trim(mesh, sourcePositions, sourceIndices, maxDistance, token);
            removedFaces = result.removed;
            return result.mesh;
        }

        internal static Result Trim(RemeshNative.IndexedMesh mesh, Vector3[] sourcePositions, int[] sourceIndices,
            float maxDistance, CancellationToken token)
        {
            int faces = mesh.indices.Length / 3;
            var result = new Result { mesh = mesh, classes = new byte[faces] };
            if (faces == 0 || sourceIndices.Length < 3) { result.mesh.PrepareChannels(token); return result; }
            var bvh = new TriangleBvh(sourcePositions, sourceIndices);
            var sourceNormals = MeshGeometry.FaceNormals(sourcePositions, sourceIndices);
            var closedSource = RemeshTopology.ClosedVolumeFaces(sourcePositions, sourceIndices, token);
            var classes = Classify(mesh, bvh, sourceNormals, closedSource, maxDistance, token, out int kept);
            // A source wound inside out would reject everything; judge it by its flipped
            // normals instead, and give up (keep all) when neither reading keeps a tenth.
            if (kept * 10 < faces) {
                for (int i = 0; i < sourceNormals.Length; ++i) sourceNormals[i] = -sourceNormals[i];
                var flipped = Classify(mesh, bvh, sourceNormals, closedSource, maxDistance, token, out int keptFlipped);
                if (keptFlipped > kept) { classes = flipped; kept = keptFlipped; }
                if (kept * 10 < faces) { result.gaveUp = true; result.mesh.PrepareChannels(token); return result; }
            }
            result.classes = classes;
            result.removed = faces - kept;
            if (result.removed > 0) result.mesh = Compact(mesh, classes, kept);
            result.flipped = OrientConsistently(result.mesh);
            result.mesh.PrepareChannels(token);
            return result;
        }

        // A face stays when a source face lies within reach that points the SAME way
        // (dot ≥ MinDot). Orientation is the only usable test: the remesher fits its
        // output vertices onto the input surface, so the slab around a sheet is not a
        // cell thick but collapses onto the sheet with its front and back faces
        // coincident — a position test ("is the centroid behind the sheet") sees both
        // at zero distance and keeps both, which is exactly the double-sided result
        // that breaks the cage and the bake. The back face's normal is opposite to the
        // sheet's (Back), the rims are perpendicular (Rim); both go. A source wound
        // inside out is handled by the caller's flipped retry.
        static byte[] Classify(RemeshNative.IndexedMesh mesh, TriangleBvh bvh, Vector3[] sourceNormals, bool[] closedSource, float maxDistance, CancellationToken token, out int kept)
        {
            int faces = mesh.indices.Length / 3;
            var classes = new byte[faces];
            bool allClosed = closedSource.Length > 0;
            foreach (bool closed in closedSource) allClosed &= closed;
            int count = 0;
            Parallel.For(0, faces, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f => {
                int a = mesh.indices[f * 3], b = mesh.indices[f * 3 + 1], c = mesh.indices[f * 3 + 2];
                Vector3 pa = mesh.positions[a], pb = mesh.positions[b], pc = mesh.positions[c];
                Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
                if (normal.sqrMagnitude < 1e-30f) { classes[f] = Rim; return; }   // degenerate: drop
                normal = MeshGeometry.UnitDirection(normal);
                Vector3 centroid = (pa + pb + pc) / 3f;
                // The normal of a tiny fitted voxel face may oppose the nearest
                // source face at a sharp fold. Closed volumes have no sheet back;
                // deleting such a face cuts a hole in an otherwise closed remesh.
                var nearest = allClosed ? default : bvh.FindNearest(centroid, maxDistance);
                if (allClosed || (nearest.triangleIndex >= 0 && closedSource[nearest.triangleIndex])) {
                    classes[f] = Kept; Interlocked.Increment(ref count); return;
                }
                if (bvh.FindNearestNormalFiltered(centroid, normal, sourceNormals, MinDot, maxDistance).triangleIndex >= 0) {
                    classes[f] = Kept; Interlocked.Increment(ref count); return;
                }
                classes[f] = bvh.FindNearestNormalFiltered(centroid, -normal, sourceNormals, MinDot, maxDistance).triangleIndex >= 0 ? Back : Rim;
            });
            kept = count;
            return classes;
        }

        static RemeshNative.IndexedMesh Compact(RemeshNative.IndexedMesh mesh, byte[] classes, int kept)
        {
            var remap = new int[mesh.positions.Length];
            for (int i = 0; i < remap.Length; ++i) remap[i] = -1;
            var indices = new int[kept * 3];
            int next = 0, write = 0;
            for (int f = 0; f < classes.Length; ++f) {
                if (classes[f] != Kept) continue;
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

        /// <summary>
        /// Re-winds faces so every connected piece (through edges shared by exactly two
        /// faces, positions welded bit-exactly) has one consistent orientation: two
        /// neighbours must traverse their shared edge in opposite directions. Within a
        /// piece the majority keeps its current winding — after a trim that is the side
        /// that agreed with the source — so a sheet cut from a source modeled with
        /// arbitrary winding comes out orientable and mostly as the source had it.
        /// Returns the number of faces flipped. Non-manifold edges do not propagate.
        /// </summary>
        internal static int OrientConsistently(RemeshNative.IndexedMesh mesh)
        {
            var positions = mesh.positions; var indices = mesh.indices;
            int faces = indices.Length / 3;
            if (faces < 2) return 0;
            var slots = MeshGeometry.WeldPositions(positions, out _);
            // Edge → the faces along it with the direction each traverses it (low→high).
            var edges = new Dictionary<(int, int), List<(int face, bool dir)>>(faces * 3);
            for (int f = 0; f < faces; ++f)
                for (int k = 0; k < 3; ++k) {
                    int a = slots[indices[f * 3 + k]], b = slots[indices[f * 3 + (k + 1) % 3]];
                    if (a == b) continue;
                    var key = a < b ? (a, b) : (b, a);
                    if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<(int, bool)>(2);
                    list.Add((f, a < b));
                }
            var neighbours = new List<(int face, bool myDir, bool theirDir)>[faces];
            for (int f = 0; f < faces; ++f) neighbours[f] = new List<(int, bool, bool)>(3);
            foreach (var list in edges.Values) {
                if (list.Count != 2) continue;   // open border or non-manifold: no constraint
                var (fa, da) = list[0]; var (fb, db) = list[1];
                neighbours[fa].Add((fb, da, db)); neighbours[fb].Add((fa, db, da));
            }
            var visited = new bool[faces]; var flip = new bool[faces];
            var queue = new Queue<int>(); var component = new List<int>();
            for (int seed = 0; seed < faces; ++seed) {
                if (visited[seed]) continue;
                component.Clear(); visited[seed] = true; queue.Enqueue(seed);
                while (queue.Count > 0) {
                    int f = queue.Dequeue(); component.Add(f);
                    foreach (var (nb, myDir, theirDir) in neighbours[f]) {
                        if (visited[nb]) continue;
                        bool mine = myDir ^ flip[f];
                        flip[nb] = theirDir == mine;   // consistent: opposite directions
                        visited[nb] = true; queue.Enqueue(nb);
                    }
                }
                int flips = 0;
                foreach (int f in component) if (flip[f]) ++flips;
                if (flips * 2 > component.Count) foreach (int f in component) flip[f] = !flip[f];
            }
            int flipped = 0;
            for (int f = 0; f < faces; ++f) {
                if (!flip[f]) continue;
                int t = indices[f * 3 + 1]; indices[f * 3 + 1] = indices[f * 3 + 2]; indices[f * 3 + 2] = t;
                ++flipped;
            }
            return flipped;
        }
    }
}
