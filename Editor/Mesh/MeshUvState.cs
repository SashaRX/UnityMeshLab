using System.Runtime.CompilerServices;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Transient UV0 provenance for owned working meshes. Weak keys do
    /// not retain destroyed meshes; this state is not written to imported assets.</summary>
    internal static class MeshUvState
    {
        static readonly ConditionalWeakTable<Mesh, object> DraftMeshes = new ConditionalWeakTable<Mesh, object>();

        internal static bool IsDraft(Mesh mesh) => !ReferenceEquals(mesh, null) && DraftMeshes.TryGetValue(mesh, out _);

        internal static void SetDraft(Mesh mesh, bool draft)
        {
            if (ReferenceEquals(mesh, null)) return;
            if (draft) DraftMeshes.GetValue(mesh, _ => new object());
            else DraftMeshes.Remove(mesh);
        }
    }
}
