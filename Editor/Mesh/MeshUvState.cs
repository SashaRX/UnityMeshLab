using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>UV0 provenance for owned working meshes. Generated planar UVs store
    /// a reserved Z component so their draft status survives mesh serialization.</summary>
    internal static class MeshUvState
    {
        // A format identifier, compared by its serialized bits rather than as a
        // computed coordinate. Only generated XY UVs use this reserved component.
        const float DraftComponent = -1048576f;
        static readonly int DraftBits = BitConverter.SingleToInt32Bits(DraftComponent);
        static readonly ConditionalWeakTable<Mesh, object> DraftMeshes = new ConditionalWeakTable<Mesh, object>();

        internal static bool IsDraft(Mesh mesh)
        {
            if (!mesh) return false;
            if (DraftMeshes.TryGetValue(mesh, out _)) return true;
            if (!HasGeneratedMarker(mesh)) return false;
            DraftMeshes.GetValue(mesh, _ => new object());
            return true;
        }

        internal static void SetGeneratedUv(Mesh mesh, Vector2[] uv)
        {
            var marked = new List<Vector3>(uv.Length);
            foreach (var point in uv) marked.Add(new Vector3(point.x, point.y, DraftComponent));
            mesh.SetUVs(0, marked);
            SetDraft(mesh, true);
        }

        internal static void SetDraft(Mesh mesh, bool draft)
        {
            if (!mesh) return;
            if (draft) DraftMeshes.GetValue(mesh, _ => new object());
            else {
                DraftMeshes.Remove(mesh);
                // A completed XY unwrap has no provenance component. Clearing an
                // explicit marker also leaves all of its XY coordinates unchanged.
                if (HasGeneratedMarker(mesh)) {
                    var xy = mesh.uv;
                    mesh.SetUVs(0, new List<Vector2>(xy));
                }
            }
        }

        static bool HasGeneratedMarker(Mesh mesh)
        {
            if (mesh.vertexCount == 0 || mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0) != 3) return false;
            if (mesh.isReadable) {
                var uv = new List<Vector3>(); mesh.GetUVs(0, uv);
                foreach (var point in uv) if (BitConverter.SingleToInt32Bits(point.z) != DraftBits) return false;
                return uv.Count == mesh.vertexCount;
            }
            // Native mesh data also preserves the marker on a saved mesh whose
            // CPU vertex getters were disabled by UploadMeshData(true).
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
            using (var uv = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp)) {
                data[0].GetUVs(0, uv);
                for (int i = 0; i < uv.Length; ++i)
                    if (BitConverter.SingleToInt32Bits(uv[i].z) != DraftBits) return false;
                return true;
            }
        }
    }
}
