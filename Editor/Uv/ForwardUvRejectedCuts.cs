using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static partial class ReverseUvTransfer
    {
        internal sealed class RejectedForwardOutput
        {
            internal int[] sourceFaces, originalFaces, sources;
            internal Vector3[] barycentrics;
            internal Vector2[] uv;
            internal int restoredFaces;
        }

        internal sealed class RejectedForwardInput
        {
            internal Vector3[] positions;
            internal int[] indices, originalIndices, legacySources, legacyCornerSources;
            internal Vector2[] legacyUv, legacyCorners;
            internal HashSet<int> projected;
            internal RejectedForwardOutput output;
        }

        sealed class RejectedCutGroup
        {
            internal int count;
            internal bool unchanged = true;
        }

        // Undo only complete rejected subdivisions. Geometry keys include both
        // exact original edge endpoints, across UV and normal vertex splits.
        internal static RejectedForwardOutput RestoreRejectedForwardCuts(RejectedForwardInput input,
            HashSet<(int,int)> allowedContacts, Matrix4x4 frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var groups = RejectedCutGroups(input, token);
            var parent = Enumerable.Range(0, input.indices.Length / 3).ToArray();
            var blocked = new bool[parent.Length];
            var owners = RejectedCutEdgeOwners(input, token);
            JoinRejectedCutEdges(input, owners, parent, blocked, token);
            var refused = new HashSet<int>();
            foreach (var item in groups)
                if (item.Value.count < 2 || !item.Value.unchanged || blocked[item.Key])
                    refused.Add(RejectedCutRoot(parent, item.Key));
            var restore = new HashSet<int>(groups.Keys.Where(f => !refused.Contains(RejectedCutRoot(parent, f))));
            if (restore.Count == 0) return null;
            var result = RejectedCutOutput(input, restore, token);
            bool metricFits=RejectedCutMetricsFit(input,result,restore,frame,token);
            if(!metricFits) return null;
            bool contactFits=RejectedCutContactsFit(result,allowedContacts,token);
            return contactFits?result:null;
        }

        static Dictionary<int,RejectedCutGroup> RejectedCutGroups(RejectedForwardInput input, CancellationToken token)
        {
            var groups = new Dictionary<int,RejectedCutGroup>();
            for (int f = 0; f < input.output.sourceFaces.Length; ++f)
            {
                token.ThrowIfCancellationRequested();
                int face = input.output.sourceFaces[f];
                if (!groups.TryGetValue(face, out var group)) {group = new RejectedCutGroup(); groups.Add(face, group);}
                ++group.count;
                group.unchanged &= RejectedCutKeepsLegacy(input, f);
            }
            return groups;
        }

        static bool RejectedCutKeepsLegacy(RejectedForwardInput input, int face)
        {
            if (input.projected.Contains(face)) return false;
            for (int c = face * 3; c < face * 3 + 3; ++c)
                if (!input.output.uv[c].Equals(input.legacyCorners[c]) || input.output.sources[c] != input.legacyCornerSources[c])
                    return false;
            return true;
        }

        static Dictionary<(Vector3,Vector3),List<int>> RejectedCutEdgeOwners(RejectedForwardInput input, CancellationToken token)
        {
            var result = new Dictionary<(Vector3,Vector3),List<int>>();
            for (int f = 0; f < input.indices.Length / 3; ++f)
            {
                token.ThrowIfCancellationRequested();
                for (int e = 0; e < 3; ++e)
                {
                    var key = RejectedCutEdgeKey(input, f, e);
                    if (!result.TryGetValue(key, out var owners)) {owners = new List<int>(); result.Add(key, owners);}
                    owners.Add(f);
                }
            }
            return result;
        }

        static void JoinRejectedCutEdges(RejectedForwardInput input, Dictionary<(Vector3,Vector3),List<int>> owners,
            int[] parent, bool[] blocked, CancellationToken token)
        {
            var first = new Dictionary<(Vector3,Vector3),int>();
            for (int f = 0; f < input.output.sourceFaces.Length; ++f)
            {
                token.ThrowIfCancellationRequested();
                int face = input.output.sourceFaces[f];
                for (int c = f * 3; c < f * 3 + 3; ++c)
                {
                    int edge = RejectedCutBoundaryEdge(input.output.barycentrics[c]);
                    if (edge < 0) continue;
                    var key = RejectedCutEdgeKey(input, face, edge);
                    if (owners[key].Count > 2) blocked[face] = true;
                    if (first.TryGetValue(key, out int other)) parent[RejectedCutRoot(parent, face)] = RejectedCutRoot(parent, other);
                    else first.Add(key, face);
                }
            }
        }

        static int RejectedCutBoundaryEdge(Vector3 weights)
        {
            if (weights.Equals(Vector3.right) || weights.Equals(Vector3.up) || weights.Equals(Vector3.forward)) return -1;
            if (weights.z == 0) return 0;
            if (weights.x == 0) return 1;
            return weights.y == 0 ? 2 : -1;
        }

        static (Vector3,Vector3) RejectedCutEdgeKey(RejectedForwardInput input, int face, int edge)
        {
            var a = input.positions[input.indices[face * 3 + edge]];
            var b = input.positions[input.indices[face * 3 + (edge + 1) % 3]];
            int compare = a.x.CompareTo(b.x);
            if (compare == 0) compare = a.y.CompareTo(b.y);
            if (compare == 0) compare = a.z.CompareTo(b.z);
            return compare <= 0 ? (a,b) : (b,a);
        }

        static int RejectedCutRoot(int[] parent, int face)
        {
            while (parent[face] != face)
            {
                parent[face] = parent[parent[face]]; face = parent[face];
            }
            return face;
        }

        static RejectedForwardOutput RejectedCutOutput(RejectedForwardInput input, HashSet<int> restore, CancellationToken token)
        {
            var faces = new List<int>(); var originals = new List<int>(); var bary = new List<Vector3>();
            var uv = new List<Vector2>(); var sources = new List<int>(); var emitted = new HashSet<int>();
            for (int f = 0; f < input.output.sourceFaces.Length; ++f)
            {
                token.ThrowIfCancellationRequested();
                int face = input.output.sourceFaces[f], original = input.output.originalFaces[f];
                if (restore.Contains(face))
                {
                    if (!emitted.Add(face)) continue;
                    faces.Add(face); originals.Add(original);
                    bary.Add(Vector3.right); bary.Add(Vector3.up); bary.Add(Vector3.forward);
                    for (int k = 0; k < 3; ++k)
                    {
                        int vertex = input.originalIndices[original * 3 + k];
                        uv.Add(input.legacyUv[vertex]); sources.Add(input.legacySources[vertex]);
                    }
                }
                else
                {
                    faces.Add(face); originals.Add(original);
                    for (int c = f * 3; c < f * 3 + 3; ++c)
                    { bary.Add(input.output.barycentrics[c]); uv.Add(input.output.uv[c]); sources.Add(input.output.sources[c]); }
                }
            }
            return new RejectedForwardOutput { sourceFaces = faces.ToArray(), originalFaces = originals.ToArray(),
                barycentrics = bary.ToArray(), uv = uv.ToArray(), sources = sources.ToArray(), restoredFaces = emitted.Count };
        }

        static bool RejectedCutMetricsFit(RejectedForwardInput input, RejectedForwardOutput output, HashSet<int> restored, Matrix4x4 frame, CancellationToken token)
        {
            for (int f = 0; f < output.sourceFaces.Length; ++f)
            {
                token.ThrowIfCancellationRequested();
                // Unchanged pieces are copied exactly from the already validated input.
                // Only a restored original triangle receives new geometry and needs a new face check.
                if (!restored.Contains(output.sourceFaces[f]))
                {
                    continue;
                }
                if (!RejectedCutFaceMetricFits(input, output, f, frame)) return false;
            }
            return true;
        }

        static bool RejectedCutFaceMetricFits(RejectedForwardInput input, RejectedForwardOutput output, int face, Matrix4x4 frame)
        {
            int old = output.originalFaces[face] * 3, t = output.sourceFaces[face] * 3, c = face * 3;
            var a = input.positions[input.originalIndices[old]];
            var b = input.positions[input.originalIndices[old + 1]];
            var d = input.positions[input.originalIndices[old + 2]];
            var ua = input.legacyUv[input.originalIndices[old]];
            var ub = input.legacyUv[input.originalIndices[old + 1]];
            var ud = input.legacyUv[input.originalIndices[old + 2]];
            double before = ForwardAnisotropy(a,b,d,ua,ub,ud,frame);
            var p = input.positions[input.indices[t]];
            var q = input.positions[input.indices[t + 1]];
            var r = input.positions[input.indices[t + 2]];
            double current = ForwardAnisotropy(ReverseUvRefinedMesh.InterpolatePosition(p,q,r,output.barycentrics[c]),
                ReverseUvRefinedMesh.InterpolatePosition(p,q,r,output.barycentrics[c + 1]),
                ReverseUvRefinedMesh.InterpolatePosition(p,q,r,output.barycentrics[c + 2]),output.uv[c],output.uv[c + 1],output.uv[c + 2],frame);
            double beforeArea = ((double)ub.x - ua.x) * ((double)ud.y - ua.y) - ((double)ub.y - ua.y) * ((double)ud.x - ua.x);
            double afterArea = ForwardSignedUvArea(output.uv, c);
            return !(beforeArea > 0 && afterArea < 0 || beforeArea < 0 && afterArea > 0)
                && (before > 4 || current <= 4) && UvTransferTolerance.MetricFits(current, before);
        }

        static bool RejectedCutContactsFit(RejectedForwardOutput output, HashSet<(int,int)> allowed, CancellationToken token)
        {
            var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = output.uv,
                indices = Enumerable.Range(0, output.uv.Length).ToArray(), charts = new int[output.uv.Length] },
                token, comparisonBudget:2000000, collectConflicts:true);
            if (!scan.complete) return false;
            foreach (var contact in scan.conflicts)
            {
                token.ThrowIfCancellationRequested();
                int a = output.originalFaces[contact.a], b = output.originalFaces[contact.b];
                if (!allowed.Contains((Math.Min(a,b), Math.Max(a,b)))) return false;
            }
            return true;
        }
    }
}
