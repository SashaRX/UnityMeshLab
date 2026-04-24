using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public static partial class VertexAOBaker
    {
        // ── CPU Fallback ──

        static Dictionary<Mesh, float[]> BakeMultiMeshCPU(
            List<(Mesh mesh, Matrix4x4 transform)> targets,
            List<(Mesh mesh, Matrix4x4 transform)> occluders,
            VertexAOSettings settings)
        {
            UvtLog.Info("[Vertex AO] Using CPU mode (BVH ray tracing).");
            var result = new Dictionary<Mesh, float[]>();

            // Build combined BVH from all meshes
            var allVerts = new List<Vector3>();
            var allTris = new List<int>();
            var cpuCopies = new List<Mesh>();
            AppendGeometryBuffers(targets, allVerts, allTris, cpuCopies);
            AppendGeometryBuffers(occluders, allVerts, allTris, cpuCopies);
            if (allVerts.Count == 0 || allTris.Count == 0)
                return result;

            var allVertsArr = allVerts.ToArray();
            var allTrisArr = allTris.ToArray();
            var bvh = new TriangleBvh(allVertsArr, allTrisArr);
            var directions = GenerateSphereDirections(settings.sampleCount);

            // Precompute face normals for backface culling
            Vector3[] faceNormals = null;
            if (settings.backfaceCulling)
            {
                int faceCount = allTrisArr.Length / 3;
                faceNormals = new Vector3[faceCount];
                for (int f = 0; f < faceCount; f++)
                {
                    var a = allVertsArr[allTrisArr[f * 3]];
                    var b = allVertsArr[allTrisArr[f * 3 + 1]];
                    var c = allVertsArr[allTrisArr[f * 3 + 2]];
                    faceNormals[f] = Vector3.Cross(b - a, c - a).normalized;
                }
            }

            Bounds combinedBounds = ComputeCombinedBounds(targets);
            float extent = Mathf.Max(combinedBounds.extents.magnitude, 0.0001f);
            float normalOffset = 0.001f * extent;
            float minHitDist   = 0.003f * extent; // skip self-intersection on sharp edges
            float groundY = settings.groundPlane
                ? combinedBounds.min.y - settings.groundOffset
                : float.NegativeInfinity;

            int totalVerts = 0;
            foreach (var (mesh, _) in targets) totalVerts += mesh.vertexCount;
            int processed = 0;

            foreach (var (mesh, xform) in targets)
            {
                var readable = EnsureReadable(mesh);
                var verts = readable.vertices;
                var norms = readable.normals;
                if (norms == null || norms.Length != verts.Length)
                {
                    readable.RecalculateNormals();
                    norms = readable.normals;
                }

                var ao = new float[verts.Length];
                float maxDist = settings.maxRadius > 0 ? settings.maxRadius : float.MaxValue;

                // Transform verts/normals to world space once (main thread)
                var worldPos  = new Vector3[verts.Length];
                var worldNorm = new Vector3[verts.Length];
                for (int v = 0; v < verts.Length; v++)
                {
                    worldPos[v]  = xform.MultiplyPoint3x4(verts[v]);
                    worldNorm[v] = xform.MultiplyVector(norms[v]).normalized;
                }

                // Parallel bake — BVH is read-only, each vertex is independent
                int progressCounter = 0;
                bool cancelled = false;

                bool isThickness = settings.bakeType == AOBakeType.Thickness;

                Parallel.For(0, verts.Length, (v, loopState) =>
                {
                    if (cancelled) { loopState.Stop(); return; }

                    // Thickness: flip hemisphere — rays go into the mesh
                    Vector3 hemisphereNorm = isThickness ? -worldNorm[v] : worldNorm[v];
                    // Thickness: offset inward; AO: offset outward
                    Vector3 origin = worldPos[v] + hemisphereNorm * normalOffset;

                    // Per-vertex jitter: rotate all directions by a small random angle
                    // to break banding artifacts with low sample counts
                    float jitterAngle = (v * 2654435761u % 360) * Mathf.Deg2Rad;
                    float jCos = Mathf.Cos(jitterAngle), jSin = Mathf.Sin(jitterAngle);

                    bool cosW = settings.cosineWeighted;
                    float occludedWeight = 0f, totalWeight = 0f;
                    for (int d = 0; d < directions.Length; d++)
                    {
                        // Apply Y-axis rotation jitter
                        var dir = directions[d];
                        float rx = dir.x * jCos - dir.z * jSin;
                        float rz = dir.x * jSin + dir.z * jCos;
                        var jitteredDir = new Vector3(rx, dir.y, rz);

                        float ndot = Vector3.Dot(jitteredDir, hemisphereNorm);
                        if (ndot <= 0) continue;

                        // Cosine-weighted: rays near normal contribute more;
                        // uniform: all hemisphere directions contribute equally.
                        float weight = cosW ? ndot : 1f;
                        totalWeight += weight;

                        var hit = bvh.Raycast(origin, jitteredDir, maxDist);
                        if (hit.triangleIndex >= 0 && hit.t > minHitDist)
                        {
                            // Backface culling: skip if ray hit the back side of a triangle
                            // (skip for thickness — we want all inward hits)
                            if (!isThickness && faceNormals != null &&
                                Vector3.Dot(jitteredDir, faceNormals[hit.triangleIndex]) > 0)
                            {
                                // Hit backface — treat as no geometry (fall through to ground check)
                            }
                            else
                            {
                                // Distance falloff: closer hits occlude more
                                float falloff = 1f - hit.t / maxDist;
                                occludedWeight += weight * falloff;
                                continue;
                            }
                        }

                        // Ground plane only for AO, not thickness
                        if (!isThickness && settings.groundPlane && jitteredDir.y < -0.001f)
                        {
                            float t = (groundY - origin.y) / jitteredDir.y;
                            if (t > 0 && t < maxDist)
                            {
                                float falloff = 1f - t / maxDist;
                                occludedWeight += weight * falloff;
                            }
                        }
                    }

                    float aoVal = totalWeight > 0 ? 1f - occludedWeight / totalWeight : 1f;
                    // Thickness: invert so closer hits = brighter (1=thin, 0=thick)
                    if (isThickness) aoVal = 1f - aoVal;
                    ao[v] = Mathf.Pow(Mathf.Clamp01(aoVal), settings.intensity);

                    Interlocked.Increment(ref progressCounter);
                });

                // Progress bar on main thread (poll after parallel completes per mesh)
                if (EditorUtility.DisplayCancelableProgressBar("Baking Vertex AO (CPU, parallel)",
                    $"Mesh {processed + verts.Length}/{totalVerts} vertices", (float)(processed + verts.Length) / totalVerts))
                {
                    EditorUtility.ClearProgressBar();
                    result[mesh] = ao;
                    return result;
                }

                processed += verts.Length;

                if (readable != mesh) UnityEngine.Object.DestroyImmediate(readable);
                result[mesh] = ao;
            }

            foreach (var c in cpuCopies)
                UnityEngine.Object.DestroyImmediate(c);
            EditorUtility.ClearProgressBar();
            return result;
        }
    }
}
