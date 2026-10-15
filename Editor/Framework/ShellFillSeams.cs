using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Array-only worker preparation. Exact position welding reconnects UV-split
    // corners without changing the source mesh. Creases and non-manifold edges
    // do not mix colours from surfaces which may be hidden behind the model.
    internal static class ShellFillSeams
    {
        struct Edge
        {
            internal int face, corner, otherFace, otherCorner;
            internal bool forward;
        }

        internal static (Vector3[] neighbors, Vector3[] normals) Build(int[] triangles, Vector3[] positions, int[] styles, CancellationToken token)
        {
            var neighbors = new Vector3[styles.Length]; var normals = new Vector3[styles.Length];
            int first = -1; bool different = false;
            for (int face = 0; face < styles.Length; ++face) {
                if ((face & 4095) == 0) token.ThrowIfCancellationRequested();
                neighbors[face] = new Vector3(-1,-1,-1);
                if (styles[face] < 0) continue;
                if (first < 0) first = styles[face];
                else if (first != styles[face]) different = true;
                int t = face * 3;
                normals[face] = Vector3.Cross(positions[triangles[t+1]]-positions[triangles[t]], positions[triangles[t+2]]-positions[triangles[t]]).normalized;
            }
            if (!different) return (neighbors,normals);
            var welded = new Dictionary<Vector3,int>(); var vertices = new int[positions.Length];
            for (int i = 0; i < positions.Length; ++i) {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!welded.TryGetValue(positions[i],out int id)) { id = welded.Count; welded.Add(positions[i],id); }
                vertices[i] = id;
            }
            var edges = new Dictionary<(int,int),Edge>();
            for (int face = 0; face < styles.Length; ++face) {
                token.ThrowIfCancellationRequested();
                if (styles[face] < 0) continue;
                for (int corner = 0; corner < 3; ++corner) {
                    int a = vertices[triangles[face*3+(corner+1)%3]], b = vertices[triangles[face*3+(corner+2)%3]];
                    if (a == b) continue;
                    var key = a < b ? (a,b) : (b,a);
                    if (!edges.TryGetValue(key,out var edge)) { edges.Add(key,new Edge {face=face,corner=corner,otherFace=-1,forward=a<b}); continue; }
                    if (edge.otherFace == -2) continue;
                    if (edge.otherFace >= 0) {
                        var n = neighbors[edge.face]; n[edge.corner] = -1; neighbors[edge.face] = n;
                        n = neighbors[edge.otherFace]; n[edge.otherCorner] = -1; neighbors[edge.otherFace] = n;
                        edge.otherFace = -2;
                    }
                    else {
                        edge.otherFace = face; edge.otherCorner = corner;
                        if (edge.forward != (a < b) && styles[face] != styles[edge.face] && Vector3.Dot(normals[face],normals[edge.face]) > .95f) {
                            var n = neighbors[edge.face]; n[edge.corner] = styles[face]; neighbors[edge.face] = n;
                            n = neighbors[face]; n[corner] = styles[edge.face]; neighbors[face] = n;
                        }
                    }
                    edges[key] = edge;
                }
            }
            return (neighbors,normals);
        }
    }
}
