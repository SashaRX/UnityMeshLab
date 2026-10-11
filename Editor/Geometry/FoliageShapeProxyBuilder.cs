using System;
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
            if (settings.autoFitProxy) {
                if (selectedIndices.Length == 0) throw new ArgumentException("No selected vertices to fit the proxy.");
                center += bounds.center;
                if (hemisphere) {
                    float lowest = float.PositiveInfinity;
                    for (int i = 0; i < selectedIndices.Length; i++) {
                        if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                        lowest = Math.Min(lowest, Vector3.Dot(positions[selectedIndices[i]] - bounds.center, axis));
                    }
                    center += axis * lowest;
                }
                float distance = 0;
                for (int i = 0; i < selectedIndices.Length; i++) {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    distance = Math.Max(distance, (positions[selectedIndices[i]] - center).magnitude);
                }
                radius = distance * settings.proxyRadiusScale;
            }
            if (!FoliageTargetSnapshot.Finite(center) || !FoliageTargetSnapshot.Finite(radius) || radius <= 0)
                throw new InvalidOperationException("Proxy fit has no finite positive radius.");

            int segments = settings.proxySegments, rings = segments / (hemisphere ? 4 : 2);
            int ringCount = hemisphere ? rings : rings - 1;
            int vertexCount = 1 + ringCount * segments + (hemisphere ? 0 : 1);
            int triangleCount = segments + (ringCount - 1) * segments * 2 + (hemisphere ? 0 : segments);
            var mesh = new RemeshNative.IndexedMesh {
                positions = new Vector3[vertexCount], normals = new Vector3[vertexCount], indices = new int[triangleCount * 3]
            };
            void Vertex(int v, Vector3 direction) {
                var n = MeshGeometry.UnitDirection(rotation * direction);
                mesh.positions[v] = center + n * radius; mesh.normals[v] = n;
            }
            Vertex(0, Vector3.up);
            for (int ring = 1; ring <= ringCount; ring++) {
                token.ThrowIfCancellationRequested();
                double theta = (hemisphere ? Math.PI / 2 : Math.PI) * ring / rings;
                float y = hemisphere && ring == rings ? 0 : (float)Math.Cos(theta), r = (float)Math.Sin(theta);
                for (int j = 0; j < segments; j++) {
                    double phi = Math.PI * 2 * j / segments;
                    Vertex(1 + (ring - 1) * segments + j, new Vector3(r * (float)Math.Cos(phi), y, r * (float)Math.Sin(phi)));
                }
            }
            int index = 0;
            void Triangle(int a, int b, int c) { mesh.indices[index++] = a; mesh.indices[index++] = b; mesh.indices[index++] = c; }
            for (int j = 0; j < segments; j++) Triangle(0, 1 + (j + 1) % segments, 1 + j);
            for (int ring = 0; ring < ringCount - 1; ring++) {
                token.ThrowIfCancellationRequested();
                int upper = 1 + ring * segments, lower = upper + segments;
                for (int j = 0; j < segments; j++) {
                    int next = (j + 1) % segments;
                    Triangle(upper + j, upper + next, lower + j); Triangle(upper + next, lower + next, lower + j);
                }
            }
            if (!hemisphere) {
                int bottom = vertexCount - 1, lastRing = bottom - segments; Vertex(bottom, Vector3.down);
                for (int j = 0; j < segments; j++) Triangle(bottom, lastRing + j, lastRing + (j + 1) % segments);
            }
            foreach (var p in mesh.positions) if (!FoliageTargetSnapshot.Finite(p)) throw new InvalidOperationException("Invalid shape proxy position.");
            var topology = RemeshTopology.Inspect(mesh.positions, mesh.indices, token);
            if (!topology.Valid || topology.euler.Count != 1 || topology.boundary.Count != (hemisphere ? segments : 0))
                throw new InvalidOperationException("Invalid shape proxy topology: " + topology.Description);
            for (int f = 0; f < mesh.indices.Length; f += 3) {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                int a = mesh.indices[f], b = mesh.indices[f + 1], c = mesh.indices[f + 2];
                var face = Vector3.Cross(mesh.positions[b] - mesh.positions[a], mesh.positions[c] - mesh.positions[a]);
                if (Vector3.Dot(face, mesh.normals[a] + mesh.normals[b] + mesh.normals[c]) <= 0)
                    throw new InvalidOperationException("Shape proxy has degenerate or inward triangles. Adjust its center/radius.");
            }
            return new Result { mesh = mesh, center = center, radius = radius, axis = axis };
        }
    }
}
