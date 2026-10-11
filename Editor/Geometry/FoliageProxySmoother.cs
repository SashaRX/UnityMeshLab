using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class FoliageProxySmoother
    {
        internal sealed class Diagnostics
        {
            internal Bounds beforeBounds, afterBounds;
            internal double beforeVolume, afterVolume;
            internal double VolumeChange => Math.Abs(afterVolume / beforeVolume - 1);
        }

        internal static RemeshNative.IndexedMesh Smooth(RemeshNative.IndexedMesh input, FoliageNormalsSettings settings, CancellationToken token, out Diagnostics diagnostics)
        {
            settings.Validate(); FoliageProxyBuilder.Validate(input, token, false);
            var p = (Vector3[])input.positions.Clone(); var scratch = new Vector3[p.Length];
            var t = (int[])input.indices.Clone();
            var neighbours = Neighbours(p.Length, t, token);
            diagnostics = new Diagnostics { beforeBounds = FoliageProxyBuilder.BoundsOf(p), beforeVolume = FoliageProxyBuilder.SignedVolume(p, t) };
            for (int iteration = 0; iteration < settings.smoothingIterations; iteration++)
            {
                Pass(ref p, ref scratch, neighbours, settings.lambda * settings.smoothingStrength, token);
                Pass(ref p, ref scratch, neighbours, settings.mu * settings.smoothingStrength, token);
                token.ThrowIfCancellationRequested();
            }
            var result = new RemeshNative.IndexedMesh { positions = p, indices = t };
            FoliageProxyBuilder.Validate(result, token, false);
            diagnostics.afterBounds = FoliageProxyBuilder.BoundsOf(p); diagnostics.afterVolume = FoliageProxyBuilder.SignedVolume(p, t);
            if (diagnostics.afterVolume / diagnostics.beforeVolume < .5) throw new InvalidOperationException("Proxy lost more than half its volume. Reduce smoothing strength/iterations.");
            result.normals = MeshGeometry.AveragedNormals(p, t, token);
            if (result.normals.Any(n => !FoliageTargetSnapshot.Finite(n) || Math.Abs(n.magnitude - 1) > .001f))
                throw new InvalidOperationException("Invalid proxy normals.");
            return result;
        }

        static int[][] Neighbours(int count, int[] t, CancellationToken token)
        {
            var adjacency = new HashSet<int>[count];
            for (int i = 0; i < count; i++) adjacency[i] = new HashSet<int>();
            for (int k = 0; k < t.Length; k += 3)
            {
                if ((k & 4095) == 0) token.ThrowIfCancellationRequested();
                for (int j = 0; j < 3; j++) { int a = t[k + j], b = t[k + (j + 1) % 3]; adjacency[a].Add(b); adjacency[b].Add(a); }
            }
            // Sorted neighbour order guarantees repeated runs produce the same float sums.
            var neighbours = new int[count][];
            for (int i = 0; i < count; i++) { neighbours[i] = new int[adjacency[i].Count]; adjacency[i].CopyTo(neighbours[i]); Array.Sort(neighbours[i]); }
            return neighbours;
        }

        static void Pass(ref Vector3[] p, ref Vector3[] scratch, int[][] neighbours, float factor, CancellationToken token)
        {
            for (int i = 0; i < p.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                Vector3 sum = Vector3.zero; foreach (int j in neighbours[i]) sum += p[j] - p[i];
                scratch[i] = neighbours[i].Length == 0 ? p[i] : p[i] + sum * (factor / neighbours[i].Length);
            }
            (p, scratch) = (scratch, p);
        }
    }
}
