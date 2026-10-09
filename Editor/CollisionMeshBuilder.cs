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
        // V-HACD's intermediate hull count grows exponentially with recursion depth and
        // its merge phase allocates O(n²) work. Keep the historical V-HACD default as
        // a hard work-budget boundary; the native bridge enforces the same limit.
        public const int MaxConvexRecursionDepth = 10;
        // A closed triangulated convex polyhedron has 2V - 4 faces. Unity allows
        // 255 triangles per convex collider, so 128 vertices gives at most 252.
        public const int MaxConvexVertices = 128;
        public const int MaxConvexTriangles = 255;

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
            Mesh readable = null;
            bool isCopy = false;
            try
            {
                readable = MeshAccess.Readable(sourceMesh, out isCopy);
                return BuildSimplifiedReadable(readable, targetRatio, targetError);
            }
            catch (Exception e) when (IsNativeLoadFailure(e))
            {
                return new SimplifiedResult { error = "Collision native plugin unavailable: " + e.Message };
            }
            finally { if (isCopy && readable != null) UnityEngine.Object.DestroyImmediate(readable); }
        }

        static SimplifiedResult BuildSimplifiedReadable(Mesh sourceMesh, float targetRatio, float targetError)
        {
            var result = new SimplifiedResult();

            if (sourceMesh == null)
            {
                result.error = "Source mesh is null";
                return result;
            }

            if (!ValidateSource(sourceMesh, out string error))
            {
                result.error = error;
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

            MeshSimplifier.SimplifyResult sr;
            var geometry = BuildSimplificationGeometry(sourceMesh);
            try { sr = MeshSimplifier.Simplify(geometry, settings); }
            finally { UnityEngine.Object.DestroyImmediate(geometry); }
            result.ok              = sr.ok;
            result.error           = sr.error;
            result.mesh            = sr.simplifiedMesh;
            result.sourceTriCount  = sr.originalTriCount;
            result.resultTriCount  = sr.simplifiedTriCount;
            result.resultError     = sr.resultError;

            if (result.ok && (result.mesh == null || result.mesh.vertexCount == 0 || result.resultTriCount == 0))
            {
                if (result.mesh != null) UnityEngine.Object.DestroyImmediate(result.mesh);
                result.mesh = null;
                result.ok = false;
                result.error = "Simplification removed all triangles; increase Target Ratio or lower Target Error";
                return result;
            }

            if (result.mesh != null)
            {
                StripCollisionMesh(result.mesh);
                result.mesh.name = sourceMesh.name + "_collision";
            }

            return result;
        }

        // Collision has no material, UV, colour or shading boundaries. Merely setting
        // attribute weights to zero still leaves the source's split-vertex topology
        // and per-material submesh borders in the general-purpose simplifier.
        static Mesh BuildSimplificationGeometry(Mesh source)
        {
            ExtractMeshData(source, out var positions, out var triangles);
            var slots = MeshGeometry.WeldPositions(positions, out int count);
            var welded = new Vector3[count];
            for (int v = 0; v < positions.Length; ++v) welded[slots[v]] = positions[v];
            for (int i = 0; i < triangles.Length; ++i) triangles[i] = slots[triangles[i]];
            var geometry = new Mesh { name = source.name, hideFlags = HideFlags.HideAndDontSave,
                indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                geometry.SetVertices(welded);
                geometry.SetTriangles(triangles, 0);
                geometry.RecalculateBounds();
                return geometry;
            }
            catch { UnityEngine.Object.DestroyImmediate(geometry); throw; }
        }

        // ── Convex Decomposition mode ──

        public struct ConvexDecompSettings
        {
            public int   maxHulls;
            public int   resolution;
            public int   maxVertsPerHull;
            /// <summary>Allowed volume error (%) relative to occupied voxels, not a minimum hull volume.</summary>
            public float minVolumePerHull;
            public int   maxRecursionDepth;
            public bool  shrinkWrap;
            public int   fillMode;        // 0=FloodFill, 1=SurfaceOnly, 2=RaycastFill
            public int   minEdgeLength;
            public bool  findBestPlane;
            public bool  groupNearbyParts;
            public float partMergeDistance; // fraction of source bounds diagonal; 0 = disconnected pieces only
            public float partMergeAngle;    // principal-plane angle in degrees
            public bool prepareElements;
            public float boxFillThreshold;  // closed volume / oriented bounding box volume
            public float elementFitError;   // sampled distance / element bounds diagonal
            public int elementRemeshResolution;
            public int elementTargetTriangles;

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
                findBestPlane     = false,
                partMergeDistance = .02f,
                partMergeAngle    = 50f,
                boxFillThreshold  = .9f,
                elementFitError   = .02f,
                elementRemeshResolution = 32,
                elementTargetTriangles = 64
            };

            /// <summary>Coarse prop colliders: close small detail within similarly oriented parts.</summary>
            public static ConvexDecompSettings CoarseParts
            {
                get
                {
                    var settings = Default;
                    settings.maxHulls = 8;
                    settings.maxVertsPerHull = 16;
                    settings.minVolumePerHull = 85f;
                    settings.groupNearbyParts = true;
                    settings.prepareElements = true;
                    return settings;
                }
            }
        }

        public enum ElementPreparation { Source, Box, RemeshDecimate, Decimate }

        public struct ElementReport
        {
            public int groupIndex, elementIndex, sourceTriangles, preparedTriangles;
            public Vector3 principalSize;
            public float boxFill; // -1 when the source has no valid closed volume
            public float sampledError; // relative to the element bounds diagonal
            public ElementPreparation preparation;
            public string detail;
        }

        public struct ConvexDecompResult
        {
            public bool ok;
            public string error;
            public List<Mesh> hulls;
            public int sourceTriCount;
            public int partGroupCount;
            public List<ElementReport> elementReports;
        }

        /// <summary>
        /// Decompose a mesh into convex hulls using V-HACD.
        /// Returns one Mesh per convex hull.
        /// </summary>
        public static ConvexDecompResult BuildConvexDecomposition(Mesh sourceMesh, ConvexDecompSettings settings)
        {
            Mesh readable = null;
            bool isCopy = false;
            try
            {
                readable = MeshAccess.Readable(sourceMesh, out isCopy);
                return BuildConvexDecompositionReadable(readable, settings);
            }
            catch (Exception e) when (IsNativeLoadFailure(e))
            {
                return new ConvexDecompResult { hulls = new List<Mesh>(), error = "Collision native plugin unavailable: " + e.Message };
            }
            finally { if (isCopy && readable != null) UnityEngine.Object.DestroyImmediate(readable); }
        }

        static bool IsNativeLoadFailure(Exception e) => e is DllNotFoundException ||
            e is EntryPointNotFoundException || e is BadImageFormatException;

        static bool ValidateSource(Mesh mesh, out string error)
        {
            error = null;
            var vertices = mesh.vertices;
            if (vertices.Length == 0) { error = "Mesh has no vertices"; return false; }
            foreach (var v in vertices)
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                    float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                { error = "Mesh contains non-finite positions"; return false; }
            int count = 0;
            for (int s = 0; s < mesh.subMeshCount; ++s)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                { error = "Collision generation requires triangle submeshes"; return false; }
                var indices = mesh.GetTriangles(s);
                if (indices.Length % 3 != 0) { error = "Invalid triangle count"; return false; }
                foreach (int index in indices)
                    if (index < 0 || index >= vertices.Length)
                    { error = "Triangle index out of bounds"; return false; }
                count += indices.Length;
            }
            if (count < 3) { error = "Mesh has no triangles"; return false; }
            return true;
        }

        static ConvexDecompResult BuildConvexDecompositionReadable(Mesh sourceMesh, ConvexDecompSettings settings)
        {
            var result = new ConvexDecompResult { hulls = new List<Mesh>(), partGroupCount = 1 };

            if (sourceMesh == null)
            {
                result.error = "Source mesh is null";
                return result;
            }

            if (!ValidateSource(sourceMesh, out string error))
            {
                result.error = error;
                return result;
            }

            if (float.IsNaN(settings.minVolumePerHull) || float.IsInfinity(settings.minVolumePerHull) || settings.minVolumePerHull < 0)
            {
                result.error = "Volume error threshold must be finite and non-negative";
                return result;
            }

            if (settings.groupNearbyParts || settings.prepareElements)
            {
                if (!ValidPartSettings(settings))
                {
                    result.error = "Part gap must be 0 or 0.0001-10% of bounds diagonal; part angle must be 0-90 degrees";
                    return result;
                }
                if (settings.prepareElements && !CollisionElementPreprocessor.ValidSettings(settings))
                {
                    result.error = "Invalid element preparation settings: fill 50-100%, fit error 0.01-10%, voxel resolution 16-256, triangle budget 12-4096";
                    return result;
                }
                return BuildGroupedDecomposition(sourceMesh, settings);
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

            // Bound vertices by the triangle budget, not PhysX's vertex limit.
            int maxVPH = Mathf.Clamp(settings.maxVertsPerHull, 8, MaxConvexVertices);
            int maxRecursionDepth = Mathf.Clamp(settings.maxRecursionDepth, 1, MaxConvexRecursionDepth);

            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = ConvexDecompNative.ConvexDecomp_Compute(
                    flatVerts, vertexCount,
                    indices, indices.Length,
                    Mathf.Clamp(settings.maxHulls, 1, 64),
                    Mathf.Clamp(settings.resolution, 10000, 1000000),
                    maxVPH,
                    settings.minVolumePerHull,
                    maxRecursionDepth,
                    settings.shrinkWrap ? 1 : 0,
                    Mathf.Clamp(settings.fillMode, 0, 2),
                    Mathf.Clamp(settings.minEdgeLength, 1, 8),
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
                    if (vCount < 4 || vCount > MaxConvexVertices || iCount < 12 ||
                        iCount % 3 != 0 || iCount / 3 > MaxConvexTriangles)
                    {
                        result.error = $"Hull {h} exceeds Unity's convex budget or has no closed volume ({vCount} vertices, {iCount / 3} triangles)";
                        return result;
                    }

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
                if (!result.ok)
                {
                    foreach (var mesh in result.hulls) UnityEngine.Object.DestroyImmediate(mesh);
                    result.hulls.Clear();
                }
            }

            return result;
        }

        static bool ValidPartSettings(ConvexDecompSettings settings) =>
            !float.IsNaN(settings.partMergeDistance) && !float.IsInfinity(settings.partMergeDistance) &&
            (settings.partMergeDistance == 0 || settings.partMergeDistance >= .000001f && settings.partMergeDistance <= .1f) &&
            !float.IsNaN(settings.partMergeAngle) && !float.IsInfinity(settings.partMergeAngle) &&
            settings.partMergeAngle >= 0 && settings.partMergeAngle <= 90;

        static ConvexDecompResult BuildGroupedDecomposition(Mesh source, ConvexDecompSettings settings)
        {
            ExtractMeshData(source, out var positions, out var indices);
            var parts = CollisionPartGrouper.Group(positions, indices, settings.groupNearbyParts ? settings.partMergeDistance : 0, settings.partMergeAngle);
            var result = new ConvexDecompResult {
                hulls = new List<Mesh>(), sourceTriCount = indices.Length / 3, partGroupCount = parts.Count, elementReports = new List<ElementReport>()
            };
            int budget = Mathf.Clamp(settings.maxHulls, 1, 64);
            if (parts.Count > budget)
            {
                result.error = $"Found {parts.Count} part groups, but Max Hulls is {budget}. Increase Max Hulls or group more nearby parts. Whole-mesh decomposition requires Group Nearby Parts and Prepare Elements off.";
                return result;
            }
            var meshes = new List<Mesh>();
            var outputs = new List<ConvexDecompResult>();
            var limits = new int[parts.Count];
            bool prepareElements = settings.prepareElements;
            settings.groupNearbyParts = false;
            settings.prepareElements = false;
            try
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    var mesh = prepareElements
                        ? BuildPreparedPartMesh(source.name, positions, parts[i], settings, i, result.elementReports)
                        : BuildPartMesh(source.name, positions, parts[i].indices);
                    meshes.Add(mesh);
                    limits[i] = budget / parts.Count + (i < budget % parts.Count ? 1 : 0);
                    settings.maxHulls = limits[i];
                    var output = BuildConvexDecompositionReadable(mesh, settings);
                    outputs.Add(output);
                    if (!output.ok) { result.error = $"Part group {i}: {output.error}"; return result; }
                }
                // Simple parts may use fewer hulls than their share. Give that unused
                // budget only to groups that still hit their limit, never across groups.
                while (true)
                {
                    int used = 0;
                    var capped = new List<int>();
                    for (int i = 0; i < outputs.Count; i++)
                    {
                        used += outputs[i].hulls.Count;
                        if (outputs[i].hulls.Count == limits[i]) capped.Add(i);
                    }
                    int spare = budget - used;
                    if (spare <= 0 || capped.Count == 0) break;
                    for (int n = 0; n < capped.Count && n < spare; n++)
                    {
                        int i = capped[n];
                        limits[i] += spare / capped.Count + (n < spare % capped.Count ? 1 : 0);
                        settings.maxHulls = limits[i];
                        var output = BuildConvexDecompositionReadable(meshes[i], settings);
                        DestroyHulls(outputs[i].hulls);
                        outputs[i] = output;
                        if (!output.ok) { result.error = $"Part group {i}: {output.error}"; return result; }
                    }
                }
                foreach (var output in outputs)
                    foreach (var hull in output.hulls)
                    {
                        hull.name = source.name + "_hull" + result.hulls.Count;
                        result.hulls.Add(hull);
                    }
                result.ok = true;
                return result;
            }
            finally
            {
                foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
                if (!result.ok)
                    foreach (var output in outputs) DestroyHulls(output.hulls);
            }
        }

        static Mesh BuildPreparedPartMesh(string name, Vector3[] positions, CollisionPartGrouper.Part part,
            ConvexDecompSettings settings, int group, List<ElementReport> reports)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var element in part.Elements)
            {
                var prepared = CollisionElementPreprocessor.Prepare(positions, element.indices, settings, out var report);
                report.groupIndex = group;
                report.elementIndex = reports.Count;
                reports.Add(report);
                int offset = vertices.Count;
                vertices.AddRange(prepared.positions);
                foreach (int index in prepared.indices) triangles.Add(offset + index);
            }
            var mesh = new Mesh { name = name, indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildPartMesh(string name, Vector3[] positions, List<int> indices)
        {
            var remap = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var triangles = new int[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                int source = indices[i];
                if (!remap.TryGetValue(source, out int index))
                {
                    index = vertices.Count;
                    remap.Add(source, index);
                    vertices.Add(positions[source]);
                }
                triangles[i] = index;
            }
            var mesh = new Mesh { name = name, indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void DestroyHulls(List<Mesh> hulls)
        {
            foreach (var hull in hulls) UnityEngine.Object.DestroyImmediate(hull);
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

            mesh.Clear(false);
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
