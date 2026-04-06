// MeshEntry.cs — Per-renderer mesh state shared across all tools.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public class MeshEntry
    {
        public int lodIndex;
        public Renderer renderer;
        public MeshFilter meshFilter;
        public Mesh originalMesh;
        public Mesh fbxMesh;
        public bool include = true;
        public bool wasWelded;
        public bool wasEdgeWelded;
        public bool wasSymmetrySplit;
        public Mesh repackedMesh;
        public Mesh transferredMesh;
        public TargetTransferState transferState;
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
