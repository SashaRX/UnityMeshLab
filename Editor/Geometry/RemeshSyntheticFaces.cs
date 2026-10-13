using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Geometric association to synthetic support patches after voxelization
    /// and collapse. Zero is original; -1 is mixed/uncertain; positive IDs are patches.
    /// This is an estimate from interior samples, not exact native face provenance.</summary>
    internal static class RemeshSyntheticFaces
    {
        internal static int[] Classify(Vector3[] positions, int[] indices, RemeshPlanarCap.Support support, CancellationToken token)
        {
            if (support == null || support.addedFaces == 0) return null;
            var sourceLabels = support.facePatches;
            if (sourceLabels == null) {
                sourceLabels = new int[support.indices.Length/3];
                for (int f = support.originalFaces; f < sourceLabels.Length; ++f) sourceLabels[f] = 1;
            }
            return Project(positions,indices,support.positions,support.indices,sourceLabels,token);
        }

        internal static int[] Project(Vector3[] positions, int[] indices, Vector3[] sourcePositions, int[] sourceIndices,
            int[] sourceLabels, CancellationToken token)
        {
            if (sourceLabels == null) return null;
            var labels = new int[indices.Length / 3];
            var bvh = new TriangleBvh(sourcePositions, sourceIndices);
            var normals = MeshGeometry.FaceNormals(sourcePositions, sourceIndices);
            var weights = new[] { new Vector3(1f/3, 1f/3, 1f/3), new Vector3(.8f, .1f, .1f),
                new Vector3(.1f, .8f, .1f), new Vector3(.1f, .1f, .8f) };
            for (int f = 0; f < labels.Length; ++f) {
                token.ThrowIfCancellationRequested();
                var a = positions[indices[f*3]]; var b = positions[indices[f*3+1]]; var c = positions[indices[f*3+2]];
                var normal = Vector3.Cross(b-a, c-a).normalized;
                int label = int.MinValue;
                foreach (var w in weights) {
                    var hit = bvh.FindNearestNormalFiltered(a*w.x + b*w.y + c*w.z, normal, normals, .5f);
                    int sample = hit.triangleIndex < 0 ? -1 : sourceLabels[hit.triangleIndex];
                    if (label == int.MinValue) label = sample;
                    else if (sample != label) { label = -1; break; }
                }
                labels[f] = label;
            }
            return labels;
        }

        internal sealed class Regions
        {
            internal int[] faceRegion;
            internal readonly List<int[]> faces = new List<int[]>();
            internal readonly List<double> areas = new List<double>();
            internal readonly List<int> labels = new List<int>();
        }

        /// <summary>Connected masked regions, measured in world-space square units.
        /// Internal native/UV vertex splits do not divide a region.</summary>
        internal static Regions Group(RemeshNative.IndexedMesh mesh,int[] labels,Matrix4x4 toWorld)
        {
            var result = new Regions {faceRegion=new int[mesh.TriangleCount]};
            Array.Fill(result.faceRegion,-1);
            var slots=MeshGeometry.WeldPositions(mesh.positions,out _); var sets=new DisjointSet(mesh.TriangleCount);
            var edges=new Dictionary<(int,int),int>();
            for(int f=0;f<mesh.TriangleCount;++f) {
                if(labels[f]==0) continue;
                for(int k=0;k<3;++k) {
                    int a=slots[mesh.indices[f*3+k]],b=slots[mesh.indices[f*3+(k+1)%3]]; var edge=a<b?(a,b):(b,a);
                    if(edges.TryGetValue(edge,out int old)) { if(labels[old]==labels[f]) sets.Union(old,f); }
                    else edges.Add(edge,f);
                }
            }
            var groups=new SortedDictionary<int,List<int>>();
            for(int f=0;f<mesh.TriangleCount;++f) if(labels[f]!=0) {
                int root=sets.Find(f);
                if(!groups.TryGetValue(root,out var list)) {
                    list=new List<int>();
                    groups.Add(root,list);
                }
                list.Add(f);
            }
            foreach(var group in groups.Values) {
                int region=result.faces.Count; double area=0;
                foreach(int f in group) {
                    result.faceRegion[f]=region;
                    var a=toWorld.MultiplyPoint3x4(mesh.positions[mesh.indices[f*3]]);
                    var u=toWorld.MultiplyPoint3x4(mesh.positions[mesh.indices[f*3+1]])-a;
                    var v=toWorld.MultiplyPoint3x4(mesh.positions[mesh.indices[f*3+2]])-a;
                    double x=(double)u.y*v.z-(double)u.z*v.y,y=(double)u.z*v.x-(double)u.x*v.z,z=(double)u.x*v.y-(double)u.y*v.x;
                    area+=Math.Sqrt(x*x+y*y+z*z)*.5;
                }
                result.faces.Add(group.ToArray()); result.areas.Add(area); result.labels.Add(labels[group[0]]);
            }
            return result;
        }

        /// <summary>Remove only explicitly selected face indices. The input remains
        /// available for Restore; every retained triangle and vertex keeps its identity.</summary>
        internal static RemeshNative.IndexedMesh Remove(RemeshNative.IndexedMesh input, bool[] selected)
        {
            if (selected == null || selected.Length != input.TriangleCount) throw new ArgumentException("Face selection does not match this mesh.");
            int kept = 0; foreach (bool face in selected) if (!face) ++kept;
            if (kept == 0) throw new InvalidOperationException("Removing every face would leave no mesh.");
            var indices = new int[kept*3]; int cursor = 0;
            for (int f = 0; f < selected.Length; ++f) {
                if (selected[f]) continue;
                for (int k = 0; k < 3; ++k) indices[cursor++] = input.indices[f*3+k];
            }
            return new RemeshNative.IndexedMesh { positions = input.positions, indices = indices,
                uv = input.uv, draftUv = input.draftUv }.PrepareChannels();
        }

        internal static Mesh Preview(string name, RemeshNative.IndexedMesh mesh, int[] labels, bool[] selected)
        {
            var positions = new Vector3[mesh.indices.Length]; var colors = new Color[positions.Length];
            var indices = new int[positions.Length];
            for (int f = 0; f < mesh.TriangleCount; ++f) {
                var color = selected != null && selected[f] ? ViewportHighlight.Selected : labels[f] < 0
                    ? ViewportHighlight.Cap : labels[f] > 0 ? ViewportHighlight.Closure : new Color(.45f,.5f,.55f);
                for (int k = 0; k < 3; ++k) {
                    int i = f*3+k; positions[i] = mesh.positions[mesh.indices[i]]; colors[i] = color; indices[i] = i;
                }
            }
            return RemeshPipeline.BuildMesh(name, positions, indices, colors: colors);
        }
    }
}
