using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class FoliageSurfaceSampler
    {
        internal sealed class Result
        {
            internal Vector3[] samples;
            internal int[] sampleIslands;
            internal int faces, islands, generated, deduplicated, skippedDegenerate;
            internal double area;
            internal float spacing, minNeighbour, meanNeighbour, maxNeighbour;
        }

        internal static Result Sample(Vector3[] p, int[] t, FoliageNormalsSettings s, CancellationToken token)
        {
            s.Validate();
            token.ThrowIfCancellationRequested();
            var result = new Result { faces = t.Length / 3 };
            var slots = MeshGeometry.WeldPositions(p, out int count);
            var parent = new int[count];
            for (int i = 0; i < count; i++)
            {
                parent[i] = i;
            }

            var areas = new double[t.Length / 3];
            var bounds = new Bounds();
            bool haveBounds = false;
            for (int f = 0; f < areas.Length; f++)
            {
                if ((f & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
                if (a < 0 || b < 0 || c < 0 || a >= p.Length || b >= p.Length || c >= p.Length)
                {
                    throw new ArgumentException("Invalid scatter indices.");
                }

                if (!FoliageTargetSnapshot.Finite(p[a]) || !FoliageTargetSnapshot.Finite(p[b]) || !FoliageTargetSnapshot.Finite(p[c]))
                {
                    throw new ArgumentException("Non-finite scatter geometry.");
                }

                double area = .5 * Vector3.Cross(p[b] - p[a], p[c] - p[a]).magnitude;
                if (!(area > 0) || double.IsInfinity(area))
                {
                    result.skippedDegenerate++;
                    continue;
                }
                areas[f] = area;
                result.area += area;
                Union(parent, slots[a], slots[b]);
                Union(parent, slots[a], slots[c]);
                foreach (int v in new[] { a, b, c })
                {
                    if (!haveBounds)
                    {
                        bounds = new Bounds(p[v], Vector3.zero);
                        haveBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(p[v]);
                    }
                }
            }
            if (!haveBounds)
            {
                throw new InvalidOperationException("No non-degenerate foliage faces.");
            }

            result.spacing = s.relativeSpacing ? s.sampleSpacing * bounds.size.magnitude : s.sampleSpacing;
            if (!(result.spacing > 0) || !FoliageTargetSnapshot.Finite(result.spacing))
            {
                throw new InvalidOperationException("Invalid capture spacing.");
            }

            var groups = new List<List<int>>();
            var lookup = new Dictionary<int, int>();
            for (int f = 0; f < areas.Length; f++)
            {
                if (areas[f] > 0)
            {
                int root = Root(parent, slots[t[f * 3]]);
                if (!lookup.TryGetValue(root, out int g))
                {
                    g = groups.Count;
                    lookup.Add(root, g);
                    groups.Add(new List<int>());
                }
                groups[g].Add(f);
            }
            }

            result.islands = groups.Count;
            if ((long)groups.Count * s.minimumSamplesPerIsland > s.maximumPrimitiveCount)
            {
                throw new InvalidOperationException("Primitive budget cannot cover every island's minimum samples. Raise budget or reduce the selection.");
            }

            var candidates = new List<Vector3>();
            var candidateIslands = new List<int>();
            var byIsland = new List<List<Vector3>>();
            int candidateCount = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                token.ThrowIfCancellationRequested();
                var faces = groups[g];
                var cdf = new double[faces.Count];
                double sum = 0;
                for (int i = 0; i < faces.Count; i++)
                {
                    cdf[i] = sum += areas[faces[i]];
                }

                double desired = Math.Ceiling(sum / ((double)result.spacing * result.spacing));
                if (desired > 1000000 || desired + candidateCount > 1000000)
                {
                    throw new InvalidOperationException("Scatter exceeds one million candidates. Increase spacing.");
                }

                int n = Math.Max(s.minimumSamplesPerIsland, (int)desired);
                var points = new List<Vector3>(n);
                var seen = new HashSet<int>();
                uint seed = Hash(unchecked((uint)s.deterministicSeed) ^ (uint)g);
                for (int i = 0; i < n; i++)
                {
                    if ((i & 1023) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                    // Stratified area CDF + scrambled radical inverses: independent dimensions for face and barycentrics.
                    double choose = (i + .5) / n * sum;
                    int j = Array.BinarySearch(cdf, choose);
                    if (j < 0)
                    {
                        j = ~j;
                    }

                    j = Math.Min(j, faces.Count - 1);
                    int f = faces[j] * 3;
                    points.Add(Inside(p[t[f]], p[t[f + 1]], p[t[f + 2]], RadicalInverse((uint)i ^ seed), RadicalInverse(Hash(seed) ^ (uint)i, 3)));
                }
                foreach (int face in faces)
                {
                    int f = face * 3;
                    if (s.includeSourceVertices)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            if (seen.Add(slots[t[f + k]]))
                            {
                                points.Add(p[t[f + k]]);
                            }
                        }
                    }

                    if (s.includeTriangleCentroids)
                    {
                        points.Add((p[t[f]] + p[t[f + 1]] + p[t[f + 2]]) / 3);
                    }

                    if (points.Count + candidateCount > 1000000)
                    {
                        throw new InvalidOperationException("Scatter candidate budget exceeded. Increase spacing or disable vertex/centroid samples.");
                    }
                }
                candidateCount += points.Count;
                byIsland.Add(points);
                // Reserve each island first, before the global spatial dedup.
                for (int i = 0; i < s.minimumSamplesPerIsland; i++)
                {
                    candidates.Add(points[i]);
                    candidateIslands.Add(g);
                }
            }
            for (int g = 0; g < byIsland.Count; g++)
            {
                for (int i = s.minimumSamplesPerIsland; i < byIsland[g].Count; i++)
                {
                    candidates.Add(byIsland[g][i]);
                    candidateIslands.Add(g);
                }
            }

            result.generated = candidates.Count;
            var unique = Deduplicate(candidates, candidateIslands, result.spacing * s.dedupSpacingFraction, token, out var ids);
            result.deduplicated = unique.Count;
            var counts = new int[groups.Count];
            foreach (int g in ids)
            {
                counts[g]++;
            }

            foreach (int n in counts)
            {
                if (n < s.minimumSamplesPerIsland)
                {
                    throw new InvalidOperationException("Dedup distance prevents the minimum samples per island. Reduce dedup distance or the island minimum.");
                }
            }

            var keep = new List<int>();
            var extra = new List<int>();
            Array.Clear(counts, 0, counts.Length);
            for (int i = 0; i < ids.Count; i++)
            {
                if (counts[ids[i]]++ < s.minimumSamplesPerIsland)
                {
                    keep.Add(i);
                }
                else
                {
                    extra.Add(i);
                }
            }
            int remaining = Math.Min(extra.Count, s.maximumPrimitiveCount - keep.Count);
            for (int i = 0; i < remaining; i++)
            {
                keep.Add(extra[(int)((i + .5) * extra.Count / remaining)]);
            }

            keep.Sort();
            result.samples = new Vector3[keep.Count];
            result.sampleIslands = new int[keep.Count];
            for (int i = 0; i < keep.Count; i++)
            {
                result.samples[i] = unique[keep[i]];
                result.sampleIslands[i] = ids[keep[i]];
            }
            MeasureNeighbours(result, token);
            return result;
        }

        internal static Vector3 Inside(Vector3 a, Vector3 b, Vector3 c, float u, float v)
        {
            float r = Mathf.Sqrt(u);
            return a * (1 - r) + b * (r * (1 - v)) + c * (r * v);
        }

        internal static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352d;
                x ^= x >> 15;
                x *= 0x846ca68b;
                return x ^ (x >> 16);
            }
        }
        static float RadicalInverse(uint x, uint radix = 2)
        {
            double v = 0, f = 1.0 / radix;
            while (x > 0)
            {
                v += x % radix * f;
                x /= radix;
                f /= radix;
            }
            return Math.Min((float)v, .99999994f);
        }
        static int Root(int[] p, int a)
        {
            while (p[a] != a)
            {
                p[a] = p[p[a]];
                a = p[a];
            }
            return a;
        }
        static void Union(int[] p, int a, int b)
        {
            a = Root(p, a);
            b = Root(p, b);
            if (a != b)
            {
                p[b] = a;
            }
        }

        static (long, long, long) Cell(Vector3 p, float cell) => ((long)Math.Floor(p.x / cell), (long)Math.Floor(p.y / cell), (long)Math.Floor(p.z / cell));
        static List<Vector3> Deduplicate(List<Vector3> points, List<int> islands, float distance, CancellationToken token, out List<int> ids)
        {
            var grid = new Dictionary<(long, long, long), List<int>>();
            var result = new List<Vector3>();
            ids = new List<int>();
            float sq = distance * distance;
            for (int i = 0; i < points.Count; i++)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                var p = points[i];
                var key = Cell(p, distance);
                bool close = false;
                for (int x = -1; x <= 1 && !close; x++)
                {
                    for (int y = -1; y <= 1 && !close; y++)
                    {
                        for (int z = -1; z <= 1 && !close; z++)
                        {
                            if (grid.TryGetValue((key.Item1 + x, key.Item2 + y, key.Item3 + z), out var list))
                            {
                                foreach (int j in list)
                                {
                                    if ((p - result[j]).sqrMagnitude < sq)
                        {
                            close = true;
                            break;
                        }
                                }
                            }
                        }
                    }
                }

                if (close)
                {
                    continue;
                }

                if (!grid.TryGetValue(key, out var bucket))
                {
                    grid[key] = bucket = new List<int>();
                }

                bucket.Add(result.Count);
                result.Add(p);
                ids.Add(islands[i]);
            }
            return result;
        }

        // Reuse the package BVH with point triangles, excluding each point's own face. No second spatial tree implementation.
        static void MeasureNeighbours(Result r, CancellationToken token)
        {
            if (r.samples.Length < 2)
            {
                return;
            }

            var t = new int[r.samples.Length * 3];
            for (int i = 0; i < t.Length; i++)
            {
                t[i] = i / 3;
            }

            var bvh = new TriangleBvh(r.samples, t);
            r.minNeighbour = float.MaxValue;
            double sum = 0;
            for (int i = 0; i < r.samples.Length; i++)
            {
                if ((i & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                float d = Mathf.Sqrt(bvh.FindNearestExceptTriangle(r.samples[i], i).distSq);
                r.minNeighbour = Math.Min(r.minNeighbour, d);
                r.maxNeighbour = Math.Max(r.maxNeighbour, d);
                sum += d;
            }
            r.meanNeighbour = (float)(sum / r.samples.Length);
        }
    }
}
