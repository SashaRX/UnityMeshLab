// MeshEntry.cs — Per-renderer mesh state shared across all tools.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Per-renderer mesh state shared across all tools.
    /// Created by <see cref="UvToolContext.Refresh"/> when a LODGroup is selected.
    /// Working meshes are destroyed by <see cref="UvToolHub.OnDisable"/> and
    /// <c>LightmapTransferTool.ResetPipelineState</c>.
    /// </summary>
    public class MeshEntry
    {
        public int lodIndex;
        public Renderer renderer;
        public MeshFilter meshFilter;

        /// <summary>
        /// Working copy of the mesh. Initially == <see cref="fbxMesh"/>.
        /// Replaced with a modified copy after weld or symmetry-split steps.
        /// Destroyed on reset only when it differs from fbxMesh (i.e. was cloned).
        /// </summary>
        public Mesh originalMesh;

        /// <summary>
        /// The imported FBX asset mesh — owned by the Unity asset database.
        /// Never destroyed by the tool. Set once during Refresh, never changes.
        /// Used as the restore target when pipeline state is reset.
        /// </summary>
        public Mesh fbxMesh;

        public bool include = true;

        /// <summary>Pipeline step flag — true after UV0 false-seam welding. Prevents re-running the step. Reset on ResetPipelineState.</summary>
        public bool wasWelded;
        /// <summary>Pipeline step flag — true after edge-seam welding. Prevents re-running the step. Reset on ResetPipelineState.</summary>
        public bool wasEdgeWelded;
        /// <summary>Pipeline step flag — true after symmetry-split. Prevents re-running the step. Reset on ResetPipelineState.</summary>
        public bool wasSymmetrySplit;

        /// <summary>
        /// UV2-repacked mesh for the source LOD. Null until the Repack step runs.
        /// Destroyed on pipeline reset or window close.
        /// </summary>
        public Mesh repackedMesh;

        /// <summary>
        /// UV2-transferred mesh for target LODs. Null until the Transfer step runs.
        /// Destroyed on pipeline reset or window close.
        /// </summary>
        public Mesh transferredMesh;

        /// <summary>Intermediate transfer solver state. Set during Transfer, cleared on reset.</summary>
        public TargetTransferState transferState;

        // ── Pipeline diagnostic outputs (cleared on reset) ──
        public TransferQualityEvaluator.TransferReport? report;
        public GroupedShellTransfer.TransferResult shellTransferResult;
        public TransferValidator.ValidationReport validationReport;
        public BorderRepairAdapter.AdapterReport? borderRepairReport;

        public bool hasExistingUv2;
        /// <summary>
        /// Name with LOD/COL suffixes stripped — used to isolate source/target
        /// matching when multiple sub-mesh groups share the same LODGroup.
        /// </summary>
        public string meshGroupKey;
    }

    public struct ShellUvHit
    {
        public MeshEntry meshEntry;
        public int shellId;
        public int faceIndex;
        public Vector2 uvHit;
        public Vector3 barycentric;
    }

    public class PreviewShellData
    {
        public List<UvShell> shells;
        public Dictionary<int, int> faceToShell;
        public Dictionary<int, UvShell> shellById;
        public Bounds[] shellBounds;
        public int[] triangles;
        public Vector2[] uvs;
    }

    public class FaceToShellCache
    {
        readonly Dictionary<long, int[]> faceToShellByMeshAndChannel = new Dictionary<long, int[]>();

        public void Clear() => faceToShellByMeshAndChannel.Clear();

        public int[] GetFaceToShell(Mesh mesh, int channel, Vector2[] uv, int[] triangles)
        {
            if (mesh == null || uv == null || triangles == null) return null;
            long key = (((long)mesh.GetInstanceID()) << 8) ^ (uint)channel;
            if (faceToShellByMeshAndChannel.TryGetValue(key, out var cached))
                return cached;

            int faceCount = triangles.Length / 3;
            var faceToShell = new int[faceCount];
            for (int i = 0; i < faceToShell.Length; i++) faceToShell[i] = -1;

            try
            {
                var shells = UvShellExtractor.Extract(uv, triangles);
                foreach (var shell in shells)
                {
                    if (shell?.faceIndices == null) continue;
                    foreach (int f in shell.faceIndices)
                        if (f >= 0 && f < faceToShell.Length)
                            faceToShell[f] = shell.shellId;
                }
            }
            catch
            {
                // Keep -1 mapping when shell extraction fails.
            }

            faceToShellByMeshAndChannel[key] = faceToShell;
            return faceToShell;
        }
    }
}
