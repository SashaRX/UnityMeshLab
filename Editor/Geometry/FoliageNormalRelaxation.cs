using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Runs on captured arrays after projection/blending and before boundary splitting/tangents.
    internal static class FoliageNormalRelaxation
    {
        internal static void Apply(FoliageTargetSnapshot target, Vector3[] normals, bool[] transferred,
            FoliageNormalsSettings settings, CancellationToken token)
        {
            if (!settings.relaxTransferredNormals || settings.normalRelaxIterations == 0 ||
                settings.normalRelaxStrength == 0 || settings.transferBlend == 0) return;

            token.ThrowIfCancellationRequested();
            var neighbours = Neighbours(target.selectedIndices, transferred, token);
            var current = new Vector3[normals.Length];
            var next = new Vector3[normals.Length];
            for (int v = 0; v < current.Length; v++)
            {
                if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                if (neighbours[v].Length > 0)
                    current[v] = MeshGeometry.UnitDirection(target.normalLocalToCapture.MultiplyVector(normals[v]));
            }
            // Jacobi updates avoid dependence on vertex traversal order. Capture space keeps
            // smoothing consistent under non-uniform and mirrored renderer transforms.
            for (int pass = 0; pass < settings.normalRelaxIterations; pass++)
            {
                RelaxPass(current, next, neighbours, target.captureNormals, settings, token);
                (current, next) = (next, current);
            }
            for (int v = 0; v < normals.Length; v++)
            {
                if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                if (neighbours[v].Length > 0)
                    normals[v] = MeshGeometry.UnitDirection(target.normalCaptureToLocal.MultiplyVector(current[v]));
            }
        }

        static int[][] Neighbours(int[] indices, bool[] transferred, CancellationToken token)
        {
            var sets = new HashSet<int>[transferred.Length];
            for (int t = 0; t < indices.Length; t += 3)
            {
                if ((t & 1023) == 0) token.ThrowIfCancellationRequested();
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                AddEdge(a, b, transferred, sets); AddEdge(b, c, transferred, sets); AddEdge(c, a, transferred, sets);
            }
            var neighbours = new int[sets.Length][];
            for (int v = 0; v < sets.Length; v++)
            {
                if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                var set = sets[v];
                if (set == null) { neighbours[v] = Array.Empty<int>(); continue; }
                neighbours[v] = new int[set.Count];
                set.CopyTo(neighbours[v]);
                Array.Sort(neighbours[v]);
            }
            return neighbours;
        }

        static void AddEdge(int a, int b, bool[] transferred, HashSet<int>[] sets)
        {
            if (a == b || !transferred[a] || !transferred[b]) return;
            (sets[a] ??= new HashSet<int>()).Add(b);
            (sets[b] ??= new HashSet<int>()).Add(a);
        }

        static void RelaxPass(Vector3[] current, Vector3[] next, int[][] neighbours, Vector3[] originals,
            FoliageNormalsSettings settings, CancellationToken token)
        {
            for (int v = 0; v < current.Length; v++)
            {
                if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                next[v] = RelaxVertex(v, current, neighbours[v], originals[v], settings);
            }
        }

        static Vector3 RelaxVertex(int v, Vector3[] current, int[] neighbours, Vector3 original, FoliageNormalsSettings settings)
        {
            var sum = current[v];
            foreach (int neighbour in neighbours) sum += current[neighbour];
            var average = MeshGeometry.UnitDirection(sum);
            if (!MeshGeometry.UsableNormal(average)) return current[v];
            var direction = MeshGeometry.UnitDirection(Vector3.Lerp(current[v], average, settings.normalRelaxStrength));
            if (!MeshGeometry.UsableNormal(direction)) return current[v];
            if (settings.alignToOriginalHemisphere && Vector3.Dot(direction, original) < 0) direction = -direction;
            return direction;
        }
    }
}
