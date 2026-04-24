// CollisionMeshBuilder.cs — High-level API for collision mesh generation.
// Simplified mode reuses MeshSimplifier; Convex Decomposition uses V-HACD via ConvexDecompNative.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    public static class CollisionMeshBuilder
    {
        // ── Simplified mode ──

        public struct SimplifiedResult
        {
            public bool ok;
            public string error;
            public Mesh mesh;
            public int sourceTriCount;
            public int resultTriCount;
            public float resultError;
        }

        /// <summary>
        /// Build a simplified collision mesh by aggressively reducing triangle count.
        /// Reuses MeshSimplifier with collision-appropriate settings (no UV/normal preservation).
        /// </summary>
        public static SimplifiedResult BuildSimplified(Mesh sourceMesh, float targetRatio, float targetError)
        {
            var result = new SimplifiedResult();

            if (sourceMesh == null)
            {
                result.error = "Source mesh is null";
                return result;
            }

            var settings = new MeshSimplifier.SimplifySettings
            {
                targetRatio  = targetRatio,
                targetError  = targetError,
                uv2Weight    = 0f,
                normalWeight = 0f,
                lockBorder   = false,
                uvChannel    = 0
            };

            var sr = MeshSimplifier.Simplify(sourceMesh, settings);
            result.ok              = sr.ok;
            result.error           = sr.error;
            result.mesh            = sr.simplifiedMesh;
            result.sourceTriCount  = sr.originalTriCount;
            result.resultTriCount  = sr.simplifiedTriCount;
            result.resultError     = sr.resultError;

            if (result.mesh != null)
            {
                StripCollisionMesh(result.mesh);
                result.mesh.name = sourceMesh.name + "_collision";
            }

            return result;
        }

        // ── Convex Decomposition mode ──

        public struct ConvexDecompSettings
        {
            public int   maxHulls;
            public int   resolution;
            public int   maxVertsPerHull;
            public float minVolumePerHull;
            public int   maxRecursionDepth;
            public bool  shrinkWrap;
            public int   fillMode;        // 0=FloodFill, 1=SurfaceOnly, 2=RaycastFill
            public int   minEdgeLength;
            public bool  findBestPlane;

            public static ConvexDecompSettings Default => new ConvexDecompSettings
            {
                maxHulls          = 16,
                resolution        = 100000,
                maxVertsPerHull   = 64,
                minVolumePerHull  = 1f,
                maxRecursionDepth = 10,
                shrinkWrap        = true,
                fillMode          = 0,
                minEdgeLength     = 2,
                findBestPlane     = false
            };
        }

        public struct ConvexDecompResult
        {
            public bool ok;
            public string error;
            public List<Mesh> hulls;
            public int sourceTriCount;
        }

        /// <summary>
        /// Decompose a mesh into convex hulls using V-HACD.
        /// Returns one Mesh per convex hull.
        /// </summary>
        public static ConvexDecompResult BuildConvexDecomposition(Mesh sourceMesh, ConvexDecompSettings settings)
        {
            var result = new ConvexDecompResult { hulls = new List<Mesh>() };

            if (sourceMesh == null)
            {
                result.error = "Source mesh is null";
                return result;
            }

            // Extract positions and merge all submesh triangles
            Vector3[] positions = sourceMesh.vertices;
            int vertexCount = positions.Length;
            if (vertexCount == 0)
            {
                result.error = "Mesh has no vertices";
                return result;
            }

            // Flatten positions to float[] (x,y,z interleaved)
            float[] flatVerts = new float[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                flatVerts[i * 3 + 0] = positions[i].x;
                flatVerts[i * 3 + 1] = positions[i].y;
                flatVerts[i * 3 + 2] = positions[i].z;
            }

            // Merge all submesh indices into one triangle list
            var allIndices = new List<int>();
            for (int s = 0; s < sourceMesh.subMeshCount; s++)
            {
                int[] sub = sourceMesh.GetTriangles(s);
                allIndices.AddRange(sub);
            }
            int[] indices = allIndices.ToArray();
            result.sourceTriCount = indices.Length / 3;

            if (indices.Length < 3)
            {
                result.error = "Mesh has no triangles";
                return result;
            }

            // Clamp maxVertsPerHull to PhysX limit
            int maxVPH = Mathf.Clamp(settings.maxVertsPerHull, 8, 255);

            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = ConvexDecompNative.ConvexDecomp_Compute(
                    flatVerts, vertexCount,
                    indices, indices.Length,
                    settings.maxHulls,
                    settings.resolution,
                    maxVPH,
                    settings.minVolumePerHull,
                    settings.maxRecursionDepth,
                    settings.shrinkWrap ? 1 : 0,
                    settings.fillMode,
                    settings.minEdgeLength,
                    settings.findBestPlane ? 1 : 0);

                if (ctx == IntPtr.Zero)
                {
                    result.error = "V-HACD computation failed";
                    return result;
                }

                int hullCount = ConvexDecompNative.ConvexDecomp_GetHullCount(ctx);
                if (hullCount == 0)
                {
                    result.error = "V-HACD produced zero hulls";
                    return result;
                }

                for (int h = 0; h < hullCount; h++)
                {
                    int vCount = ConvexDecompNative.ConvexDecomp_GetHullVertexCount(ctx, h);
                    int iCount = ConvexDecompNative.ConvexDecomp_GetHullIndexCount(ctx, h);

                    float[] hullVerts = new float[vCount * 3];
                    int[]   hullIdx   = new int[iCount];

                    ConvexDecompNative.ConvexDecomp_GetHullVertices(ctx, h, hullVerts, hullVerts.Length);
                    ConvexDecompNative.ConvexDecomp_GetHullIndices(ctx, h, hullIdx, hullIdx.Length);

                    // Build Unity Mesh
                    var hullPositions = new Vector3[vCount];
                    for (int v = 0; v < vCount; v++)
                    {
                        hullPositions[v] = new Vector3(
                            hullVerts[v * 3 + 0],
                            hullVerts[v * 3 + 1],
                            hullVerts[v * 3 + 2]);
                    }

                    var mesh = new Mesh();
                    mesh.name = sourceMesh.name + "_hull" + h;
                    mesh.indexFormat = vCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    mesh.SetVertices(hullPositions);
                    mesh.SetTriangles(hullIdx, 0);
                    mesh.RecalculateBounds();
                    mesh.UploadMeshData(false);

                    result.hulls.Add(mesh);
                }

                result.ok = true;
            }
            finally
            {
                if (ctx != IntPtr.Zero)
                    ConvexDecompNative.ConvexDecomp_Destroy(ctx);
            }

            return result;
        }

        /// <summary>
        /// Extract combined positions and triangles from a mesh (merging all submeshes).
        /// Useful for building collision data from multi-material meshes.
        /// </summary>
        /// <summary>
        /// Strip all channels except positions and triangles from a collision mesh.
        /// Colliders only need geometry — normals, tangents, UVs, colors waste memory.
        /// </summary>
        static void StripCollisionMesh(Mesh mesh)
        {
            var positions = mesh.vertices;
            var triangles = mesh.triangles;

            mesh.Clear();
            mesh.SetVertices(positions);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
        }

        public static void ExtractMeshData(Mesh mesh, out Vector3[] positions, out int[] triangles)
        {
            positions = mesh.vertices;
            var allTris = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
                allTris.AddRange(mesh.GetTriangles(s));
            triangles = allTris.ToArray();
        }
    }
}
