using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal interface IFoliageProxyBuilder
    {
        RemeshNative.IndexedMesh Build(RemeshNative.IndexedMesh primitives, int resolution, CancellationToken token);
    }

    internal sealed class MeshoptVoxelProxyBuilder : IFoliageProxyBuilder
    {
        public RemeshNative.IndexedMesh Build(RemeshNative.IndexedMesh primitives, int resolution, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RemeshNative.CheckAvailable();
            var settings = new RemeshSettings { voxelResolution = resolution, solve = false, shell = false, trimToSource = false, simplify = false };
            var result = RemeshNative.Voxelize(primitives.positions, primitives.indices, settings, token);
            FoliageProxyBuilder.Validate(result, token, true);
            var before = FoliageProxyBuilder.BoundsOf(primitives.positions);
            var after = FoliageProxyBuilder.BoundsOf(result.positions);
            float cell = Math.Max(before.size.x, Math.Max(before.size.y, before.size.z)) / resolution;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(before.min[axis] - after.min[axis]) > cell * 1.5f || Math.Abs(before.max[axis] - after.max[axis]) > cell * 1.5f)
                {
                    throw new InvalidOperationException("Voxel envelope bounds exceed 1.5 cells of the primitive union. Adjust spacing/resolution.");
                }
            }

            result.normals = MeshGeometry.AveragedNormals(result.positions, result.indices, token);
            return result;
        }
    }

    internal static class FoliageProxyBuilder
    {
        internal static void CheckTriangleBudget(RemeshNative.IndexedMesh mesh, int limit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (mesh?.indices != null && mesh.TriangleCount > limit)
            {
                throw new InvalidOperationException($"Proxy triangle budget exceeded ({limit:N0}). Increase target edge length or reduce voxel resolution.");
            }
        }

        internal static Bounds BoundsOf(Vector3[] p)
        {
            var b = new Bounds(p[0], Vector3.zero);
            foreach (var v in p)
            {
                b.Encapsulate(v);
            }

            return b;
        }

        internal static RemeshTopology.Snapshot Validate(RemeshNative.IndexedMesh mesh, CancellationToken token, bool orient)
        {
            token.ThrowIfCancellationRequested();
            if (mesh == null || mesh.positions == null || mesh.indices == null || mesh.positions.Length < 4 || mesh.indices.Length == 0 || mesh.indices.Length % 3 != 0)
            {
                throw new InvalidOperationException("Empty or malformed foliage proxy.");
            }

            if (mesh.positions.Any(p => !FoliageTargetSnapshot.Finite(p)))
            {
                throw new InvalidOperationException("Proxy has non-finite positions.");
            }

            if (mesh.indices.Any(i => i < 0 || i >= mesh.positions.Length))
            {
                throw new InvalidOperationException("Invalid proxy indices.");
            }

            var topology = RemeshTopology.Inspect(mesh.positions, mesh.indices, token);
            if (!topology.Valid || topology.boundary.Count != 0)
            {
                throw new InvalidOperationException("Proxy must be closed and manifold: " + topology.Description);
            }
            // A disconnected component must also be outward. A positive total could hide one inverted sphere.
            var volumes = ComponentVolumes(mesh, topology);
            if (volumes.Values.Any(volume => Math.Abs(volume) < 1e-30 || double.IsNaN(volume) || double.IsInfinity(volume)))
            {
                throw new InvalidOperationException("Proxy component has no finite enclosed volume.");
            }
            OrientComponents(mesh, topology, volumes, orient);
            return topology;
        }

        internal static Dictionary<int, double> ComponentVolumes(RemeshNative.IndexedMesh mesh, RemeshTopology.Snapshot topology)
        {
            var volumes = new Dictionary<int, double>();
            for (int f = 0; f < mesh.TriangleCount; f++)
            {
                int root = topology.components.Find(f);
                volumes.TryGetValue(root, out double volume);
                var origin = mesh.positions[mesh.indices[root * 3]];
                volumes[root] = volume + FaceVolume(mesh.positions, mesh.indices, f * 3, origin);
            }
            return volumes;
        }

        static void OrientComponents(RemeshNative.IndexedMesh mesh, RemeshTopology.Snapshot topology, Dictionary<int, double> volumes, bool orient)
        {
            for (int f = 0; f < mesh.TriangleCount; f++)
            {
                if (volumes[topology.components.Find(f)] < 0)
                {
                    if (!orient)
                    {
                        throw new InvalidOperationException("Smoothing inverted a proxy component.");
                    }

                    int k = f * 3;
                    (mesh.indices[k + 1], mesh.indices[k + 2]) = (mesh.indices[k + 2], mesh.indices[k + 1]);
                }
            }
        }

        internal static double SignedVolume(Vector3[] p, int[] t)
        {
            double volume = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                volume += FaceVolume(p, t, i, p[0]);
            }

            return volume;
        }
        internal static double FaceVolume(Vector3[] p, int[] t, int k, Vector3 origin)
        {
            var a = p[t[k]] - origin;
            var b = p[t[k + 1]] - origin;
            var c = p[t[k + 2]] - origin;
            return ((double)a.x * ((double)b.y * c.z - (double)b.z * c.y) +
                (double)a.y * ((double)b.z * c.x - (double)b.x * c.z) + (double)a.z * ((double)b.x * c.y - (double)b.y * c.x)) / 6;
        }
    }
}
