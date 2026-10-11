using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Renderer correspondence stays independent of the welded scatter geometry. Main-thread capture/write only.</summary>
    internal sealed class FoliageTargetSnapshot : IDisposable
    {
        internal readonly Mesh source, original;
        internal Mesh working, applied;
        internal readonly MeshRenderer renderer;
        internal readonly MeshFilter filter;
        internal readonly Material[] materials;
        internal readonly Matrix4x4 localToCapture, captureToLocal, normalLocalToCapture, normalCaptureToLocal, localToWorld;
        internal readonly Vector3[] localPositions, localNormals, positions, captureNormals;
        internal readonly int[][] indices;
        internal readonly MeshTopology[] topology;
        internal readonly bool[] selectedSubmeshes, selectedVertices, excludedVertices;
        internal readonly int[] selectedIndices;

        internal FoliageTargetSnapshot(MeshRenderer renderer, Matrix4x4 worldToCapture, FoliageNormalsSettings settings, CancellationToken token, bool deferArrayProcessing = false)
        {
            this.renderer = renderer; filter = renderer.GetComponent<MeshFilter>(); source = filter.sharedMesh;
            materials = renderer.sharedMaterials;
            localToWorld = renderer.localToWorldMatrix;
            localToCapture = worldToCapture * localToWorld;
            if (localToCapture.determinant == 0 || !Finite(localToCapture.determinant)) throw new InvalidOperationException("Singular foliage transform.");
            captureToLocal = localToCapture.inverse;
            normalLocalToCapture = captureToLocal.transpose; normalCaptureToLocal = localToCapture.transpose;
            Mesh readable = MeshAccess.Readable(source, out bool owned);
            try {
                // MeshAccess's rare unreadable-skin fallback cannot preserve these streams: refuse rather than strip them.
                if (owned && (source.blendShapeCount > 0 || source.HasVertexAttribute(VertexAttribute.BlendWeight)))
                    throw new InvalidOperationException("Unreadable meshes with skin/blend shape channels cannot be preserved; use a static readable mesh.");
                original = Object.Instantiate(readable); original.hideFlags = HideFlags.HideAndDontSave;
                original.name = source.name;
                localPositions = original.vertices; localNormals = original.normals;
                if (localNormals.Length != localPositions.Length) throw new InvalidOperationException("Source requires authored normals at every vertex.");
                positions = new Vector3[localPositions.Length]; captureNormals = new Vector3[positions.Length];
                selectedVertices = new bool[positions.Length]; excludedVertices = new bool[positions.Length];
                indices = new int[original.subMeshCount][]; topology = new MeshTopology[indices.Length];
                selectedSubmeshes = new bool[indices.Length]; int selectedCount = 0;
                for (int s = 0; s < indices.Length; s++) {
                    token.ThrowIfCancellationRequested();
                    topology[s] = original.GetTopology(s); indices[s] = original.GetIndices(s);
                    selectedSubmeshes[s] = topology[s] == MeshTopology.Triangles && SelectsSubmesh(s, materials, settings);
                    if (selectedSubmeshes[s]) selectedCount = checked(selectedCount + indices[s].Length);
                }
                selectedIndices = new int[selectedCount];
                if (!deferArrayProcessing) PrepareArrays(token);
            }
            catch { if (original) Object.DestroyImmediate(original); throw; }
            finally { if (owned) Object.DestroyImmediate(readable); }
        }

        internal static bool SelectsSubmesh(int submesh, Material[] materials, FoliageNormalsSettings settings) =>
            settings.selection == FoliageFaceSelection.AllTriangleSubmeshes ||
            (settings.selection == FoliageFaceSelection.SelectedSubmeshIndices ? Array.IndexOf(settings.submeshes, submesh) >= 0 :
                submesh < materials.Length && Array.IndexOf(settings.materials, materials[submesh]) >= 0);

        // Only immutable arrays/matrices, safe on a worker after main-thread mesh capture.
        internal void PrepareArrays(CancellationToken token)
        {
            for (int i = 0; i < positions.Length; i++) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!Finite(localPositions[i]) || !MeshGeometry.UsableNormal(localNormals[i]) || Math.Abs(localNormals[i].magnitude - 1) > .01f)
                    throw new InvalidOperationException("Source positions and unit normals must be finite.");
                positions[i] = localToCapture.MultiplyPoint3x4(localPositions[i]);
                captureNormals[i] = MeshGeometry.UnitDirection(normalLocalToCapture.MultiplyVector(localNormals[i]));
                if (!Finite(positions[i]) || !MeshGeometry.UsableNormal(captureNormals[i])) throw new InvalidOperationException("Invalid capture transform.");
            }
            int cursor = 0;
            for (int s = 0; s < indices.Length; s++) {
                token.ThrowIfCancellationRequested(); bool use = selectedSubmeshes[s];
                foreach (int v in indices[s]) {
                    if (v < 0 || v >= positions.Length) throw new InvalidOperationException("Source index outside vertex buffer.");
                    if (use) selectedVertices[v] = true; else excludedVertices[v] = true;
                }
                if (use) { Array.Copy(indices[s], 0, selectedIndices, cursor, indices[s].Length); cursor += indices[s].Length; }
            }
        }

        internal bool IsCurrent(Matrix4x4 worldToCapture) => renderer && renderer.enabled && renderer.gameObject.activeInHierarchy && filter &&
            (filter.sharedMesh == source || filter.sharedMesh == applied) && localToWorld == renderer.localToWorldMatrix &&
            localToCapture == worldToCapture * renderer.localToWorldMatrix && SameMaterials();

        bool SameMaterials()
        {
            var current = renderer.sharedMaterials;
            if (current.Length != materials.Length) return false;
            for (int i = 0; i < current.Length; i++) if (current[i] != materials[i]) return false;
            return true;
        }

        /// <summary>New result for every transfer; selected corners alone refer to the appended boundary copies.</summary>
        internal Mesh BuildResult(Vector3[] normals, bool recalculateTangents, CancellationToken token, out int duplicates)
        {
            token.ThrowIfCancellationRequested(); duplicates = 0;
            var map = new List<int>(localPositions.Length); var selectedMap = new int[localPositions.Length];
            for (int i = 0; i < localPositions.Length; i++) { map.Add(i); selectedMap[i] = i; }
            for (int i = 0; i < localPositions.Length; i++)
                if (selectedVertices[i] && excludedVertices[i]) { selectedMap[i] = map.Count; map.Add(i); duplicates++; }
            var result = duplicates == 0 ? Object.Instantiate(original) : DuplicateChannels(original, map, token);
            try {
                result.name = source.name + "_FoliageNormals"; result.hideFlags = HideFlags.HideAndDontSave;
                if (duplicates > 0) {
                    result.indexFormat = map.Count > 65535 ? IndexFormat.UInt32 : original.indexFormat;
                    result.subMeshCount = indices.Length;
                    for (int s = 0; s < indices.Length; s++) {
                        var remapped = (int[])indices[s].Clone();
                        if (selectedSubmeshes[s]) for (int k = 0; k < remapped.Length; k++) remapped[k] = selectedMap[remapped[k]];
                        result.SetIndices(remapped, topology[s], s, false);
                        var desc = result.GetSubMesh(s); desc.bounds = original.GetSubMesh(s).bounds; result.SetSubMesh(s, desc);
                    }
                }
                var output = new Vector3[map.Count];
                for (int i = 0; i < map.Count; i++) output[i] = localNormals[map[i]];
                for (int i = 0; i < selectedVertices.Length; i++) if (selectedVertices[i]) output[selectedMap[i]] = normals[i];
                result.normals = output; result.bounds = original.bounds;
                if (recalculateTangents) {
                    if (HasValidUv0(result)) {
                        var before = result.tangents; result.RecalculateTangents();
                        // Excluded faces also retain their tangent frame, even after boundary splitting.
                        if (before.Length == map.Count) {
                            var after = result.tangents;
                            for (int i = 0; i < localPositions.Length; i++) if (!selectedVertices[i] || excludedVertices[i]) after[i] = before[i];
                            result.tangents = after;
                        }
                    }
                    else UvtLog.Warn("[Foliage] UV0 is absent or degenerate; original tangents retained.");
                }
                token.ThrowIfCancellationRequested(); VertexChannels.RaiseChanged(result); return result;
            }
            catch { Object.DestroyImmediate(result); throw; }
        }

        internal static bool HasValidUv0(Mesh mesh)
        {
            var uv = mesh.uv;
            if (uv.Length != mesh.vertexCount) return false;
            foreach (var p in uv) if (!Finite(p.x) || !Finite(p.y)) return false;
            bool area = false;
            for (int s = 0; s < mesh.subMeshCount; s++) {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var t = mesh.GetIndices(s);
                for (int i = 0; i < t.Length; i += 3) {
                    var a = uv[t[i + 1]] - uv[t[i]]; var b = uv[t[i + 2]] - uv[t[i]];
                    if (Math.Abs(a.x * b.y - a.y * b.x) <= 1e-20f) return false;
                    area = true;
                }
            }
            return area;
        }

        // Raw vertex-stream remapping retains every attribute, its dimension and storage format, including UV0–UV7/HDR colours.
        static Mesh DuplicateChannels(Mesh mesh, List<int> map, CancellationToken token)
        {
            var result = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try {
                result.SetVertexBufferParams(map.Count, mesh.GetVertexAttributes());
                using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) {
                    for (int stream = 0; stream < mesh.vertexBufferCount; stream++) {
                        var src = data[0].GetVertexData<byte>(stream); int stride = src.Length / mesh.vertexCount;
                        using (var dst = new NativeArray<byte>(checked(map.Count * stride), Allocator.Temp)) {
                            for (int i = 0; i < map.Count; i++) {
                                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                                NativeArray<byte>.Copy(src, map[i] * stride, dst, i * stride, stride);
                            }
                            result.SetVertexBufferData(dst, 0, 0, dst.Length, stream, MeshUpdateFlags.DontRecalculateBounds);
                        }
                    }
                }
                result.bindposes = mesh.bindposes;
                if (mesh.HasVertexAttribute(VertexAttribute.BlendWeight)) CopyWeights(mesh, result, map);
                var dp = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++) {
                        token.ThrowIfCancellationRequested(); mesh.GetBlendShapeFrameVertices(shape, frame, dp, dn, dt);
                        result.AddBlendShapeFrame(mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, frame), Remap(dp, map), Remap(dn, map), Remap(dt, map));
                    }
                return result;
            }
            catch { Object.DestroyImmediate(result); throw; }
        }

        static void CopyWeights(Mesh sourceMesh, Mesh destination, List<int> map)
        {
            using (var counts = sourceMesh.GetBonesPerVertex())
            using (var weights = sourceMesh.GetAllBoneWeights()) {
                var offsets = new int[counts.Length + 1];
                for (int i = 0; i < counts.Length; i++) offsets[i + 1] = offsets[i] + counts[i];
                int total = 0; foreach (int v in map) total = checked(total + counts[v]);
                var newCounts = new NativeArray<byte>(map.Count, Allocator.Temp);
                try {
                    using var newWeights = new NativeArray<BoneWeight1>(total, Allocator.Temp);
                    int cursor = 0;
                    for (int i = 0; i < map.Count; i++) {
                        int v = map[i]; newCounts[i] = counts[v];
                        NativeArray<BoneWeight1>.Copy(weights, offsets[v], newWeights, cursor, counts[v]); cursor += counts[v];
                    }
                    destination.SetBoneWeights(newCounts, newWeights);
                }
                finally { newCounts.Dispose(); }
            }
        }

        static Vector3[] Remap(Vector3[] src, List<int> map)
        { var dst = new Vector3[map.Count]; for (int i = 0; i < dst.Length; i++) dst[i] = src[map[i]]; return dst; }
        internal static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        internal static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        internal static void Destroy(Mesh mesh) { if (mesh) { VertexChannels.RaiseChanged(mesh); Object.DestroyImmediate(mesh); } }
        public void Dispose() { Destroy(working); Destroy(original); working = null; }
    }
}
