using System;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Smooth radial vertex normals are transferred with the same triangle/barycentric
    // semantics as the voxel proxy. The hemisphere has no flat cap to steal normals from.
    internal static class FoliageShapeProxyBuilder
    {
        internal sealed class Result
        {
            internal RemeshNative.IndexedMesh mesh;
            internal Vector3 center, axis;
            internal float radius;
        }

        internal static Result Build(Vector3[] positions, int[] selectedIndices, Bounds bounds,
            FoliageNormalsSettings settings, CancellationToken token)
        {
            settings.Validate(); token.ThrowIfCancellationRequested();
            bool hemisphere = settings.normalSource == FoliageNormalSource.Hemisphere;
            if (settings.normalSource == FoliageNormalSource.VoxelEnvelope) throw new ArgumentException("Choose sphere or hemisphere.");
            var rotation = hemisphere ? Quaternion.Euler(settings.proxyRotation) : Quaternion.identity;
            var axis = rotation * Vector3.up;
            var center = settings.proxyCenter;
            float radius = settings.proxyRadius;
            if (settings.autoFitProxy)
                Fit(positions, selectedIndices, bounds, hemisphere, axis, settings.proxyRadiusScale, token, ref center, ref radius);
            if (!FoliageTargetSnapshot.Finite(center) || !FoliageTargetSnapshot.Finite(radius) || radius <= 0)
                throw new InvalidOperationException("Proxy fit has no finite positive radius.");
            var mesh = CreateMesh(settings.proxySegments, hemisphere, center, radius, rotation, token);
            Validate(mesh, hemisphere, settings.proxySegments, token);
            return new Result { mesh = mesh, center = center, radius = radius, axis = axis };
        }

        static void Fit(Vector3[] positions, int[] selectedIndices, Bounds bounds, bool hemisphere,
            Vector3 axis, float scale, CancellationToken token, ref Vector3 center, ref float radius)
        {
            if (selectedIndices.Length == 0) throw new ArgumentException("No selected vertices to fit the proxy.");
            center += bounds.center;
            if (hemisphere) center += axis * Lowest(positions, selectedIndices, bounds.center, axis, token);
            float distance = 0;
            for (int i = 0; i < selectedIndices.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                distance = Math.Max(distance, (positions[selectedIndices[i]] - center).magnitude);
            }
            radius = distance * scale;
        }

        static float Lowest(Vector3[] positions, int[] selectedIndices, Vector3 center, Vector3 axis, CancellationToken token)
        {
            float lowest = float.PositiveInfinity;
            for (int i = 0; i < selectedIndices.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                lowest = Math.Min(lowest, Vector3.Dot(positions[selectedIndices[i]] - center, axis));
            }
            return lowest;
        }

        static RemeshNative.IndexedMesh CreateMesh(int segments, bool hemisphere, Vector3 center, float radius, Quaternion rotation, CancellationToken token)
        {
            int rings = segments / (hemisphere ? 4 : 2);
            int ringCount = hemisphere ? rings : rings - 1;
            int vertexCount = 1 + ringCount * segments + (hemisphere ? 0 : 1);
            int triangleCount = segments + (ringCount - 1) * segments * 2 + (hemisphere ? 0 : segments);
            var mesh = new RemeshNative.IndexedMesh
            {
                positions = new Vector3[vertexCount],
                normals = new Vector3[vertexCount],
                indices = new int[triangleCount * 3]
            };
            Vertex(mesh, 0, Vector3.up, center, radius, rotation);
            FillRings(mesh, segments, rings, ringCount, hemisphere, center, radius, rotation, token);
            FillTriangles(mesh, segments, ringCount, token);
            if (!hemisphere)
            {
                int bottom = vertexCount - 1, lastRing = bottom - segments;
                Vertex(mesh, bottom, Vector3.down, center, radius, rotation);
                int index = mesh.indices.Length - segments * 3;
                for (int j = 0; j < segments; j++) Triangle(mesh, ref index, bottom, lastRing + j, lastRing + (j + 1) % segments);
            }
            return mesh;
        }

        static void Vertex(RemeshNative.IndexedMesh mesh, int v, Vector3 direction, Vector3 center, float radius, Quaternion rotation)
        {
            var n = MeshGeometry.UnitDirection(rotation * direction);
            mesh.positions[v] = center + n * radius; mesh.normals[v] = n;
        }

        static void FillRings(RemeshNative.IndexedMesh mesh, int segments, int rings, int ringCount, bool hemisphere,
            Vector3 center, float radius, Quaternion rotation, CancellationToken token)
        {
            for (int ring = 1; ring <= ringCount; ring++)
            {
                token.ThrowIfCancellationRequested();
                double theta = (hemisphere ? Math.PI / 2 : Math.PI) * ring / rings;
                float y = hemisphere && ring == rings ? 0 : (float)Math.Cos(theta), r = (float)Math.Sin(theta);
                for (int j = 0; j < segments; j++)
                {
                    double phi = Math.PI * 2 * j / segments;
                    Vertex(mesh, 1 + (ring - 1) * segments + j, new Vector3(r * (float)Math.Cos(phi), y, r * (float)Math.Sin(phi)), center, radius, rotation);
                }
            }
        }

        static void Triangle(RemeshNative.IndexedMesh mesh, ref int index, int a, int b, int c)
        {
            mesh.indices[index++] = a; mesh.indices[index++] = b; mesh.indices[index++] = c;
        }

        static void FillTriangles(RemeshNative.IndexedMesh mesh, int segments, int ringCount, CancellationToken token)
        {
            int index = 0;
            for (int j = 0; j < segments; j++) Triangle(mesh, ref index, 0, 1 + (j + 1) % segments, 1 + j);
            for (int ring = 0; ring < ringCount - 1; ring++)
            {
                token.ThrowIfCancellationRequested();
                int upper = 1 + ring * segments, lower = upper + segments;
                for (int j = 0; j < segments; j++)
                {
                    int next = (j + 1) % segments;
                    Triangle(mesh, ref index, upper + j, upper + next, lower + j); Triangle(mesh, ref index, upper + next, lower + next, lower + j);
                }
            }
        }

        static void Validate(RemeshNative.IndexedMesh mesh, bool hemisphere, int segments, CancellationToken token)
        {
            if (mesh.positions.Any(p => !FoliageTargetSnapshot.Finite(p))) throw new InvalidOperationException("Invalid shape proxy position.");
            var topology = RemeshTopology.Inspect(mesh.positions, mesh.indices, token);
            if (!topology.Valid || topology.euler.Count != 1 || topology.boundary.Count != (hemisphere ? segments : 0))
                throw new InvalidOperationException("Invalid shape proxy topology: " + topology.Description);
            for (int f = 0; f < mesh.indices.Length; f += 3)
            {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                int a = mesh.indices[f], b = mesh.indices[f + 1], c = mesh.indices[f + 2];
                var face = Vector3.Cross(mesh.positions[b] - mesh.positions[a], mesh.positions[c] - mesh.positions[a]);
                if (Vector3.Dot(face, mesh.normals[a] + mesh.normals[b] + mesh.normals[c]) <= 0)
                    throw new InvalidOperationException("Shape proxy has degenerate or inward triangles. Adjust its center/radius.");
            }
        }
    }
}
