// Uv2DataAsset.cs — Sidecar ScriptableObject that stores UV2 data per mesh.
// Lives beside the FBX file as "ModelName_uv2data.asset".
// Read by Uv2AssetPostprocessor during import to inject UV2 into meshes.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    /// <summary>
    /// Fingerprint of a raw FBX mesh at the time the sidecar was created.
    /// Used to detect whether the FBX has changed since Apply.
    /// </summary>
    [Serializable]
    public class MeshFingerprint
    {
        public int vertexCount;
        public int triangleCount;
        public int submeshCount;
        public Vector3 boundsCenter;
        public Vector3 boundsSize;
        public int positionsHash;   // FNV-1a over quantized positions
        public int uv0Hash;         // FNV-1a over quantized UV0
        public int[] submeshTriCounts;

        /// <summary>Compute fingerprint from a mesh.</summary>
        public static MeshFingerprint Compute(Mesh mesh)
        {
            if (mesh == null) return null;

            var fp = new MeshFingerprint();
            fp.vertexCount = mesh.vertexCount;
            fp.triangleCount = mesh.triangles.Length / 3;
            fp.submeshCount = mesh.subMeshCount;
            fp.boundsCenter = mesh.bounds.center;
            fp.boundsSize = mesh.bounds.size;

            // Submesh tri counts
            fp.submeshTriCounts = new int[mesh.subMeshCount];
            for (int s = 0; s < mesh.subMeshCount; s++)
                fp.submeshTriCounts[s] = mesh.GetTriangles(s).Length;

            // Order-DEPENDENT hash over quantized positions.
            // Must be order-dependent because vertex remap is order-dependent:
            // if Unity reorders vertices on reimport, we MUST detect it as stale
            // so RebuildRemapFromPositions runs. Order-independent hash would
            // hide the reordering and cause the original remap to be applied to
            // wrong vertices, causing catastrophic 3D stretching.
            var positions = mesh.vertices;
            uint posHash = 2166136261u;
            for (int i = 0; i < positions.Length; i++)
            {
                posHash = FnvStep(posHash, Mathf.RoundToInt(positions[i].x * 10000f));
                posHash = FnvStep(posHash, Mathf.RoundToInt(positions[i].y * 10000f));
                posHash = FnvStep(posHash, Mathf.RoundToInt(positions[i].z * 10000f));
            }
            fp.positionsHash = unchecked((int)posHash);

            // Order-DEPENDENT hash over quantized UV0
            var uv0 = new List<Vector2>();
            mesh.GetUVs(0, uv0);
            uint uvHash = 2166136261u;
            for (int i = 0; i < uv0.Count; i++)
            {
                uvHash = FnvStep(uvHash, Mathf.RoundToInt(uv0[i].x * 10000f));
                uvHash = FnvStep(uvHash, Mathf.RoundToInt(uv0[i].y * 10000f));
            }
            fp.uv0Hash = unchecked((int)uvHash);

            return fp;
        }

        /// <summary>Compare two fingerprints. Returns true if same geometry.</summary>
        public bool Matches(MeshFingerprint other)
        {
            if (other == null) return false;
            return vertexCount == other.vertexCount
                && triangleCount == other.triangleCount
                && submeshCount == other.submeshCount
                && positionsHash == other.positionsHash
                && uv0Hash == other.uv0Hash;
        }

        static uint FnvStep(uint hash, int value)
        {
            unchecked
            {
                hash ^= (uint)(value & 0xFF); hash *= 16777619u;
                hash ^= (uint)((value >> 8) & 0xFF); hash *= 16777619u;
                hash ^= (uint)((value >> 16) & 0xFF); hash *= 16777619u;
                hash ^= (uint)((value >> 24) & 0xFF); hash *= 16777619u;
            }
            return hash;
        }
    }

    [Serializable]
    public class MeshUv2Entry
    {
        public string meshName;
        public Vector2[] uv2;
        /// <summary>If true, postprocessor must run meshopt dedup (WeldInPlace) before applying UV2.</summary>
        public bool welded;
        /// <summary>If true, postprocessor must run UvEdgeWeld after meshopt dedup.</summary>
        public bool edgeWelded;
        /// <summary>Vertex positions at the time UV2 was computed (for order-independent remapping).</summary>
        public Vector3[] vertPositions;
        /// <summary>Vertex UV0 at the time UV2 was computed (for disambiguation at shared positions).</summary>
        public Vector2[] vertUv0;
        /// <summary>Vertex normals at the time UV2 was computed (for disambiguation at shared positions when UV0 is identical).</summary>
        public Vector3[] vertNormals;

        // ── Deterministic replay data (variant B) ──
        /// <summary>
        /// Maps raw FBX vertex index → optimized vertex index.
        /// Length == raw FBX vertexCount. If null, no replay — legacy path.
        /// </summary>
        public int[] vertexRemap;
        /// <summary>Number of vertices in the optimized mesh.</summary>
        public int optimizedVertexCount;
        /// <summary>Triangle indices for the optimized mesh (all submeshes concatenated).</summary>
        public int[] optimizedTriangles;
        /// <summary>Number of triangle indices per submesh (to reconstruct submesh boundaries).</summary>
        public int[] submeshTriangleCounts;

        // ── Schema & provenance (v0.12.0+) ──
        /// <summary>Schema version of this entry. 0 = pre-0.12.0 (no schema version).</summary>
        public int schemaVersion;
        /// <summary>Tool version that created this entry (e.g. "0.12.0").</summary>
        public string toolVersion;

        // ── Source fingerprint (raw FBX at time of Apply) ──
        /// <summary>Fingerprint of the raw FBX mesh when the sidecar was created. Null for pre-0.12.0 entries.</summary>
        public MeshFingerprint sourceFingerprint;

        // ── Applied pipeline steps ──
        /// <summary>Which UV channel was written (default 1 = UV2).</summary>
        public int targetUvChannel;
        /// <summary>Optional extra UV channel saved alongside the primary transfer channel.</summary>
        public Vector2[] auxiliaryUv;
        /// <summary>Target UV channel for <see cref="auxiliaryUv"/>; -1 when absent.</summary>
        public int auxiliaryTargetUvChannel = -1;
        /// <summary>Whether MeshOptimizer dedup was applied.</summary>
        public bool stepMeshopt;
        /// <summary>Whether UvEdgeWeld was applied.</summary>
        public bool stepEdgeWeld;
        /// <summary>Whether symmetry shell split was applied.</summary>
        public bool stepSymmetrySplit;
        /// <summary>Whether xatlas repack was applied.</summary>
        public bool stepRepack;
        /// <summary>Whether UV transfer from source was applied.</summary>
        public bool stepTransfer;
        /// <summary>Whether deterministic replay data is present.</summary>
        public bool hasReplayData;

        // ── Orphan vertex data (SymmetrySplit boundary verts with no raw FBX counterpart) ──
        /// <summary>Indices in the optimized mesh that have no raw FBX source vertex.</summary>
        public int[] orphanIndices;
        /// <summary>Positions for orphan vertices (parallel to orphanIndices).</summary>
        public Vector3[] orphanPositions;
        /// <summary>Normals for orphan vertices (parallel to orphanIndices).</summary>
        public Vector3[] orphanNormals;
        /// <summary>Tangents for orphan vertices (parallel to orphanIndices).</summary>
        public Vector4[] orphanTangents;
        /// <summary>UV0 for orphan vertices (parallel to orphanIndices).</summary>
        public Vector2[] orphanUv0;

        // ── Optimized vertex positions (ground truth for replay validation) ──
        /// <summary>All vertex positions from the optimized mesh at Apply time.
        /// Used to validate and fix replay results — corrects any vertex that
        /// deviates from its original position (dedup collisions, remap gaps, etc).</summary>
        public Vector3[] optimizedPositions;
        /// <summary>All vertex normals from the optimized mesh at Apply time.</summary>
        public Vector3[] optimizedNormals;
        /// <summary>All vertex tangents from the optimized mesh at Apply time.</summary>
        public Vector4[] optimizedTangents;

        // ── Shell descriptors (v0.14.0+) ──
        /// <summary>Stable shell descriptors for the target mesh UV0 shells.</summary>
        public ShellDescriptor[] shellDescriptors;
        /// <summary>Per-vertex mapping: vertex index → source shell descriptor index. -1 = unmapped.</summary>
        public int[] vertexToSourceShellDescriptor;
        /// <summary>Per-target-shell mapping: target shell index → source shell descriptor index. -1 = unmapped.</summary>
        public int[] targetShellToSourceShellDescriptor;
        /// <summary>Stable shell descriptors for the source mesh UV0 shells (at time of transfer).</summary>
        public ShellDescriptor[] sourceShellDescriptors;
    }

    /// <summary>
    /// Per-model tool settings persisted in the sidecar asset.
    /// Restored when the model is selected again.
    /// </summary>
    [Serializable]
    public class ToolSettings
    {
        public int atlasResolution = 1024;
        public int shellPaddingPx = 2;
        public int borderPaddingPx = 0;
        public bool repackPerMesh;
        public int symmetrySplitThresholdMode; // SymmetrySplitShells.ThresholdMode
        public int sourceLodIndex;

        // Pipeline
        public bool saveNewMeshAssets = true;
        public string savePath = "Assets/LightmapUvTool_Output";
    }

    /// <summary>
    /// Stores collision mesh data (simplified or convex decomposition) for persistence.
    /// Uses flattened arrays because Unity serialization does not support jagged arrays.
    /// </summary>
    [Serializable]
    public class CollisionMeshEntry
    {
        public string meshGroupKey;
        public int mode;  // 0 = Simplified, 1 = ConvexDecomp

        public MeshFingerprint sourceFingerprint;

        // Flattened vertex/index data — all hulls (or single mesh) concatenated
        public Vector3[] allPositions;
        public int[] positionOffsets;   // start index per hull/mesh in allPositions
        public int[] allTriangles;
        public int[] triangleOffsets;   // start index per hull/mesh in allTriangles

        // Generation settings (for UI restore and re-generation)
        public float targetRatio;
        public float targetError;
        public int maxHulls;
        public int resolution;
        public int maxVertsPerHull;
    }

    [CreateAssetMenu(menuName = "LightmapUvTool/UV2 Data (internal)", fileName = "uv2data")]
    public class Uv2DataAsset : ScriptableObject, ISerializationCallbackReceiver
    {
        public const int CurrentSchemaVersion = 3;

        public void OnBeforeSerialize() { }

        public void OnAfterDeserialize()
        {
            // Migrate entries from schema v2 (or earlier) to v3.
            // Unity deserializes missing int fields as 0 (default), not the
            // field initializer value (-1). Without this fixup an old entry
            // would have auxiliaryTargetUvChannel == 0, which means "UV0" and
            // could cause UV0 to be overwritten with null auxiliary data.
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.schemaVersion < 3)
                {
                    if (e.auxiliaryUv == null)
                        e.auxiliaryTargetUvChannel = -1;
                }
            }
        }
        public static string ToolVersionStr
        {
            get
            {
                if (_cachedVersion == null)
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                        typeof(Uv2DataAsset).Assembly);
                    _cachedVersion = info?.version ?? "0.0.0";
                }
                return _cachedVersion;
            }
        }
        static string _cachedVersion;

        public List<MeshUv2Entry> entries = new List<MeshUv2Entry>();
        public List<CollisionMeshEntry> collisionEntries = new List<CollisionMeshEntry>();
        public ToolSettings toolSettings;

        /// <summary>Find entry by mesh name, or null.</summary>
        public MeshUv2Entry Find(string meshName)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].meshName == meshName) return entries[i];
            return null;
        }

        /// <summary>
        /// Find entry with multi-step fallback:
        /// 1. Exact name match
        /// 2. Fingerprint match (name changed, geometry same)
        /// </summary>
        public MeshUv2Entry FindRobust(string meshName, MeshFingerprint fp)
        {
            // 1. Exact name match
            var byName = Find(meshName);
            if (byName != null) return byName;

            // 2. Fingerprint fallback (name changed, geometry same)
            if (fp != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].sourceFingerprint != null && entries[i].sourceFingerprint.Matches(fp))
                    {
                        UvtLog.Warn($"[Sidecar] '{meshName}' matched by fingerprint (stored name='{entries[i].meshName}')");
                        return entries[i];
                    }
                }
            }
            return null;
        }

        /// <summary>Set UV2 for a mesh name (add or overwrite).</summary>
        public void Set(string meshName, Vector2[] uv2, bool welded = false, bool edgeWelded = false,
                        bool symmetrySplit = false,
                        Vector3[] vertPositions = null, Vector2[] vertUv0 = null,
                        int[] vertexRemap = null, int optimizedVertexCount = 0,
                        int[] optimizedTriangles = null, int[] submeshTriangleCounts = null,
                        int schemaVersion = 0, string toolVersion = null,
                        MeshFingerprint sourceFingerprint = null, int targetUvChannel = 1,
                        bool stepMeshopt = false, bool stepEdgeWeld = false,
                        bool stepSymmetrySplit = false,
                        bool stepRepack = false, bool stepTransfer = false,
                        bool hasReplayData = false)
        {
            var e = Find(meshName);
            if (e != null)
            {
                e.uv2 = uv2;
                e.welded = welded;
                e.edgeWelded = edgeWelded;
                // backward compat: if explicit step flag absent, keep legacy boolean
                e.stepSymmetrySplit = stepSymmetrySplit || symmetrySplit;
                e.vertPositions = vertPositions;
                e.vertUv0 = vertUv0;
                e.vertexRemap = vertexRemap;
                e.optimizedVertexCount = optimizedVertexCount;
                e.optimizedTriangles = optimizedTriangles;
                e.submeshTriangleCounts = submeshTriangleCounts;
                e.schemaVersion = schemaVersion;
                e.toolVersion = toolVersion;
                e.sourceFingerprint = sourceFingerprint;
                e.targetUvChannel = targetUvChannel;
                e.auxiliaryUv = null;
                e.auxiliaryTargetUvChannel = -1;
                e.stepMeshopt = stepMeshopt;
                e.stepEdgeWeld = stepEdgeWeld;
                e.stepRepack = stepRepack;
                e.stepTransfer = stepTransfer;
                e.hasReplayData = hasReplayData;
            }
            else
            {
                entries.Add(new MeshUv2Entry {
                    meshName = meshName, uv2 = uv2, welded = welded, edgeWelded = edgeWelded,
                    vertPositions = vertPositions, vertUv0 = vertUv0,
                    vertexRemap = vertexRemap, optimizedVertexCount = optimizedVertexCount,
                    optimizedTriangles = optimizedTriangles, submeshTriangleCounts = submeshTriangleCounts,
                    schemaVersion = schemaVersion, toolVersion = toolVersion,
                    sourceFingerprint = sourceFingerprint, targetUvChannel = targetUvChannel,
                    auxiliaryUv = null, auxiliaryTargetUvChannel = -1,
                    stepMeshopt = stepMeshopt, stepEdgeWeld = stepEdgeWeld,
                    stepSymmetrySplit = stepSymmetrySplit || symmetrySplit,
                    stepRepack = stepRepack, stepTransfer = stepTransfer,
                    hasReplayData = hasReplayData
                });
            }
        }

        /// <summary>Set UV2 for a mesh name (add or overwrite) from a fully populated entry.</summary>
        public void Set(MeshUv2Entry source)
        {
            if (source == null || string.IsNullOrEmpty(source.meshName))
                throw new ArgumentNullException(nameof(source));

            var e = Find(source.meshName);
            if (e != null)
            {
                CopyEntryFields(source, e);
            }
            else
            {
                var clone = new MeshUv2Entry();
                CopyEntryFields(source, clone);
                entries.Add(clone);
            }
        }

        static void CopyEntryFields(MeshUv2Entry src, MeshUv2Entry dst)
        {
            dst.meshName = src.meshName;
            dst.uv2 = src.uv2;
            dst.welded = src.welded;
            dst.edgeWelded = src.edgeWelded;
            dst.vertPositions = src.vertPositions;
            dst.vertUv0 = src.vertUv0;
            dst.vertNormals = src.vertNormals;
            dst.vertexRemap = src.vertexRemap;
            dst.optimizedVertexCount = src.optimizedVertexCount;
            dst.optimizedTriangles = src.optimizedTriangles;
            dst.submeshTriangleCounts = src.submeshTriangleCounts;
            dst.schemaVersion = src.schemaVersion;
            dst.toolVersion = src.toolVersion;
            dst.sourceFingerprint = src.sourceFingerprint;
            dst.targetUvChannel = src.targetUvChannel;
            dst.auxiliaryUv = src.auxiliaryUv;
            dst.auxiliaryTargetUvChannel = src.auxiliaryTargetUvChannel;
            dst.stepMeshopt = src.stepMeshopt;
            dst.stepEdgeWeld = src.stepEdgeWeld;
            dst.stepSymmetrySplit = src.stepSymmetrySplit;
            dst.stepRepack = src.stepRepack;
            dst.stepTransfer = src.stepTransfer;
            dst.hasReplayData = src.hasReplayData;
            dst.orphanIndices = src.orphanIndices;
            dst.orphanPositions = src.orphanPositions;
            dst.orphanNormals = src.orphanNormals;
            dst.orphanTangents = src.orphanTangents;
            dst.orphanUv0 = src.orphanUv0;
            dst.optimizedPositions = src.optimizedPositions;
            dst.optimizedNormals = src.optimizedNormals;
            dst.optimizedTangents = src.optimizedTangents;
            dst.shellDescriptors = src.shellDescriptors;
            dst.vertexToSourceShellDescriptor = src.vertexToSourceShellDescriptor;
            dst.targetShellToSourceShellDescriptor = src.targetShellToSourceShellDescriptor;
            dst.sourceShellDescriptors = src.sourceShellDescriptors;
        }

        /// <summary>Remove entry by mesh name. Returns true if found.</summary>
        public bool Remove(string meshName)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].meshName == meshName)
                {
                    entries.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Get the sidecar asset path for a given model asset path.</summary>
        public static string GetSidecarPath(string modelAssetPath)
        {
            // "Assets/Models/MyModel.fbx" → "Assets/Models/MyModel_uv2data.asset"
            string dir = System.IO.Path.GetDirectoryName(modelAssetPath);
            string name = System.IO.Path.GetFileNameWithoutExtension(modelAssetPath);
            return dir + "/" + name + "_uv2data.asset";
        }
    }
}
