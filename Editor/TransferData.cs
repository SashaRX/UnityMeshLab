// TransferData.cs — Shared data structures for UV transfer pipeline
// Covers: SourceMeshData, RegionSignature, PointBinding, TriangleStatus,
//         BorderPair, TargetTransferState, UvShellData (extended)

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    // ─── Triangle status after transfer ───
    public enum TriangleStatus
    {
        None,
        Accepted,
        Ambiguous,
        BorderRisk,
        UnavoidableMismatch,
        Rejected
    }

    // ─── Sample point type ───
    public enum SampleType
    {
        Vertex,
        MidEdge,
        Centroid,
        Extra
    }

    // ─── Region signature: minimal ID for source area ───
    public struct RegionSignature
    {
        public int componentId;   // reserved for multi-component
        public int submeshId;
        public int shellId;       // UV2 shell after repack
        public int materialRegionId; // optional

        public bool Matches(RegionSignature other)
        {
            return submeshId == other.submeshId && shellId == other.shellId;
        }

        public override int GetHashCode()
        {
            return (componentId * 397) ^ (submeshId * 31) ^ shellId;
        }

        public override bool Equals(object obj)
        {
            if (obj is RegionSignature r)
                return componentId == r.componentId &&
                       submeshId == r.submeshId &&
                       shellId == r.shellId &&
                       materialRegionId == r.materialRegionId;
            return false;
        }

        public override string ToString()
        {
            return $"(comp={componentId},sub={submeshId},shell={shellId})";
        }
    }

    // ─── UV shell data (extended from UvShellExtractor.UvShell) ───
    public class UvShellData
    {
        public int shellId;
        public List<int> triangleIds = new List<int>();
        public HashSet<int> vertexIds = new HashSet<int>();
        public List<int> borderPrimitiveIds = new List<int>();
        public Vector2 uvBoundsMin;
        public Vector2 uvBoundsMax;
        public float areaUV;
        public float areaWorld;
        public int submeshId;
        public int componentId;
    }

    // ─── Per-triangle metrics ───
    public struct TriangleMetrics
    {
        public float perimeterUV;
        public float areaUV;
        public float areaWorld;
    }

    // ─── Source mesh prepared data ───
    public class SourceMeshData
    {
        // Raw mesh references
        public Mesh mesh;
        public Vector3[] vertices;
        public Vector3[] normals;
        public int[] triangles;
        public Vector2[] uvSource;   // the source UV channel (UV2 after repack)
        public Vector2[] uv0;        // original UV0

        // Per-face data
        public int[] submeshIds;           // submesh index per face
        public int[] triangleToShellId;    // UV2 shell per face
        public RegionSignature[] triangleSignatures;

        // Shell data
        public List<UvShellData> uvShells;

        // Border data
        public HashSet<long> borderEdges;      // packed edge keys
        public HashSet<int> borderPrimitiveIds; // faces touching border

        // Metrics
        public TriangleMetrics[] triangleMetrics;

        // Spatial index
        public TriangleBvh bvh;

        // Counts
        public int faceCount;
        public int vertCount;
    }

    // ─── Point binding: sample on target → source correspondence ───
    public struct PointBinding
    {
        public int targetTriangleId;
        public SampleType sampleType;
        public Vector3 targetPosition;
        public Vector3 targetBarycentric;

        // Source match
        public int sourceTriangleId;
        public Vector3 sourceBarycentric;
        public Vector3 sourcePosition;
        public Vector2 sourceUV;
        public int sourceShellId;
        public int sourcePrimId;
        public int materialRegionId;

        // Quality
        public float distance3D;
        public float normalDot;
        public float confidence;
        public bool isAmbiguous;
    }

    // ─── Border pair for repair ───
    public struct BorderPair
    {
        public int targetBorderPrimId;
        public int sourceBorderPrimId;
        public float targetPerimeterUV;
        public float sourcePerimeterUV;
        public float perimeterDelta;
        public bool qualityGatePassed;
    }

    // ─── Per-triangle transfer result ───
    public struct TriangleTransferResult
    {
        public int triangleId;
        public RegionSignature dominantRegion;
        public TriangleStatus status;
        public float meanError;
        public float maxError;
        public int sourcePrimId;
        public bool isBorder;
    }

    // ─── Target mesh transfer state (lives across stages) ───
    public class TargetTransferState
    {
        public Mesh mesh;
        public Vector3[] vertices;
        public Vector3[] normals;
        public int[] triangles;
        public int[] submeshIds;

        // Provisional UV being built
        public Vector2[] targetUv;

        // Per-triangle assignments
        public int[] triangleShellAssignments;  // source shell ID per face
        public TriangleStatus[] triangleStatus;
        public int[] triangleSourcePrimId;
        public bool[] triangleBorderFlags;

        // Bindings
        public List<PointBinding>[] pointBindingsPerFace;

        // Border (populated after Stage 4)
        public HashSet<int> borderPrimitiveIds;
        public float[] perimeterUV;

        // Transfer results
        public TriangleTransferResult[] results;

        public int faceCount;
        public int vertCount;
    }
}
