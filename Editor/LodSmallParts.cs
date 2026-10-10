using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    // Whole disconnected components only: this stage never cuts a connected surface.
    internal static class LodSmallParts
    {
        internal struct Settings
        {
            internal bool enabled;
            internal int firstLod, screenHeight;
            internal float maxPixels, maxAreaFraction, maxTriangleFraction;
            internal Dictionary<Mesh, Protection> protection;
            internal bool IsValid => firstLod >= 1 && screenHeight > 0 && Finite(maxPixels) && maxPixels > 0 &&
                Finite(maxAreaFraction) && maxAreaFraction >= 0 && maxAreaFraction < 1 &&
                Finite(maxTriangleFraction) && maxTriangleFraction >= 0 && maxTriangleFraction < 1;
            static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        }

        internal sealed class Protection
        {
            internal Analysis analysis;
            internal readonly HashSet<int> ids = new HashSet<int>();
        }

        internal sealed class Part
        {
            internal int id, triangles;
            internal int[] faces;
            internal Bounds bounds;
            internal double area;
            internal string protectedReason;
        }

        internal sealed class Analysis
        {
            internal LodSourceTopology source;
            internal Part[] parts;
            internal double area;
            internal int triangles;
            internal string connectivity;
            internal int[][] indices;

            // Manual selections cannot silently refer to different parts after editing geometry.
            internal bool Matches(Mesh mesh)
            {
                if (!mesh || !mesh.isReadable || mesh.vertexCount != source.data.positions.Length || mesh.subMeshCount != indices.Length) return false;
                if (!mesh.vertices.SequenceEqual(source.data.positions)) return false;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (!LodMeshData.Triangles(mesh,s).SequenceEqual(indices[s])) return false;
                }
                return true;
            }
            internal bool SameParts(Analysis other) => source.faces.Count == other.source.faces.Count &&
                source.faces.Zip(other.source.faces,(a,b) => a.submesh == b.submesh && a.points.SequenceEqual(b.points) &&
                    a.vertices.SequenceEqual(b.vertices) && a.triangles.SequenceEqual(b.triangles)).All(equal => equal);
        }

        internal sealed class Plan
        {
            internal readonly HashSet<int> removed = new HashSet<int>();
            internal int triangles;
            internal double area;
            internal float maxPixels;
            internal string note;
        }

        internal static Analysis Analyze(MeshEntry entry, Mesh mesh, Dictionary<string,object> imports,Func<bool> cancelled = null)
        {
            if (cancelled?.Invoke() == true) throw new OperationCanceledException("Small-part analysis cancelled.");
            if (!mesh || !mesh.isReadable || mesh.vertexCount == 0)
                throw new InvalidOperationException("Small-part source mesh must be readable and nonempty.");
            bool raw = LodSourceTopology.TryLoad(entry,mesh,imports,out var source,out string reason,loopsOnly:false);
            if (!raw) source = TriangleSource(mesh);
            bool nativeQuads = Enumerable.Range(0,mesh.subMeshCount).All(s => mesh.GetTopology(s) == MeshTopology.Quads);
            if (nativeQuads)
            {
                var positions = new Dictionary<Vector3,int>();
                foreach (var face in source.faces)
                    face.points = face.vertices.Select(v => {
                        Vector3 p = source.data.positions[v];
                        if (!positions.TryGetValue(p,out int id)) { id = positions.Count; positions.Add(p,id); }
                        return id;
                    }).ToArray();
            }
            var owners = new Dictionary<int,int>();
            int[] roots = Enumerable.Range(0,source.faces.Count).ToArray();
            int Root(int i) { while (roots[i] != i) { roots[i] = roots[roots[i]]; i = roots[i]; } return i; }
            for (int i = 0; i < source.faces.Count; i++)
            {
                if ((i & 1023) == 0 && cancelled?.Invoke() == true) throw new OperationCanceledException("Small-part analysis cancelled.");
                foreach (int point in source.faces[i].points)
                {
                    if (owners.TryGetValue(point,out int owner)) roots[Root(i)] = Root(owner);
                    else owners[point] = i;
                }
            }
            var groups = Enumerable.Range(0,roots.Length).GroupBy(Root).OrderBy(g => g.Min());
            var parts = new List<Part>();
            foreach (var group in groups)
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException("Small-part analysis cancelled.");
                int[] faces = group.ToArray();
                var bounds = new Bounds(source.data.positions[source.faces[faces[0]].vertices[0]],Vector3.zero);
                var part = new Part { id = faces.Min(), faces = faces };
                foreach (int f in faces)
                {
                    int[] t = source.faces[f].triangles;
                    part.triangles += t.Length/3;
                    foreach (int v in t) bounds.Encapsulate(source.data.positions[v]);
                    for (int j = 0; j < t.Length; j += 3)
                        part.area += Vector3.Cross(source.data.positions[t[j+1]]-source.data.positions[t[j]],
                            source.data.positions[t[j+2]]-source.data.positions[t[j]]).magnitude*.5;
                }
                part.bounds = bounds;
                parts.Add(part);
            }
            var result = new Analysis { source = source, parts = parts.ToArray(), area = parts.Sum(p => p.area),
                triangles = parts.Sum(p => p.triangles), connectivity = raw && !nativeQuads ? "Source polygon control points" : "Exact position connectivity: "+(reason ?? "native quads"),
                indices = Enumerable.Range(0,mesh.subMeshCount).Select(s => LodMeshData.Triangles(mesh,s)).ToArray() };
            if (parts.Count == 0) return result;
            var overall = parts[0].bounds;
            foreach (var part in parts) overall.Encapsulate(part.bounds);
            var main = parts.OrderByDescending(p => p.area).ThenBy(p => p.id).First();
            bool deforming = mesh.blendShapeCount > 0 || mesh.bindposes.Length > 0 ||
                mesh.HasVertexAttribute(VertexAttribute.BlendWeight) || mesh.HasVertexAttribute(VertexAttribute.BlendIndices);
            foreach (var part in parts)
            {
                if (deforming) part.protectedReason = "Deformation channels";
                else if (part == main) part.protectedReason = "Main component";
                else if (part.area <= 0 || double.IsNaN(part.area) || double.IsInfinity(part.area)) part.protectedReason = "Invalid surface area";
                else if (TouchesExtrema(part.bounds,overall,source.data.scale*1e-6f)) part.protectedReason = "Model bounds extremum";
            }
            return result;
        }

        static bool TouchesExtrema(Bounds part,Bounds overall,float epsilon)
        {
            for (int axis = 0; axis < 3; axis++)
                if (overall.size[axis] > epsilon && (part.min[axis] <= overall.min[axis]+epsilon || part.max[axis] >= overall.max[axis]-epsilon)) return true;
            return false;
        }

        static LodSourceTopology TriangleSource(Mesh mesh)
        {
            var data = new LodMeshData(mesh);
            var points = new Dictionary<Vector3,int>();
            int[] ids = new int[data.positions.Length];
            for (int v = 0; v < ids.Length; v++)
            {
                if (!points.TryGetValue(data.positions[v],out int id)) { id = points.Count; points.Add(data.positions[v],id); }
                ids[v] = id;
            }
            var faces = new List<LodSourceTopology.Face>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] t = LodMeshData.Triangles(mesh,s);
                for (int i = 0; i < t.Length; i += 3)
                {
                    int[] v = { t[i],t[i+1],t[i+2] };
                    faces.Add(new LodSourceTopology.Face { vertices = v, points = v.Select(j => ids[j]).ToArray(),
                        triangles = v, referenceTriangles = v, submesh = s });
                }
            }
            return new LodSourceTopology(data,faces);
        }

        internal static float Pixels(Part part,Matrix4x4 localToWorld,float groupWorldSize,float entryHeight,int screenHeight)
        {
            // Sum of transformed edge lengths is a conservative diameter for rotated/nonuniformly scaled bounds.
            Vector3 size = part.bounds.size;
            float diameter = localToWorld.MultiplyVector(new Vector3(size.x,0,0)).magnitude +
                localToWorld.MultiplyVector(new Vector3(0,size.y,0)).magnitude + localToWorld.MultiplyVector(new Vector3(0,0,size.z)).magnitude;
            return diameter/Mathf.Max(groupWorldSize,1e-8f)*entryHeight*screenHeight;
        }

        internal static Plan Select(Analysis analysis,Settings settings,int lod,float entryHeight,Matrix4x4 matrix,float groupWorldSize)
        {
            var plan = new Plan();
            if (!settings.enabled || lod < settings.firstLod) return plan;
            if (!settings.IsValid || !FinitePositive(groupWorldSize) || !FinitePositive(entryHeight))
            { plan.note = "Small parts retained: invalid screen-size settings."; return plan; }
            if (analysis.area <= 0 || double.IsNaN(analysis.area) || double.IsInfinity(analysis.area))
            { plan.note = "Small parts retained: invalid source area."; return plan; }
            HashSet<int> protectedIds = null;
            if (settings.protection != null && settings.protection.TryGetValue(analysis.source.data.source,out var protection))
            {
                if (!protection.analysis.Matches(analysis.source.data.source) || !protection.analysis.SameParts(analysis))
                { plan.note = "Small parts retained: preview geometry changed; analyze again."; return plan; }
                protectedIds = protection.ids;
            }
            foreach (var part in analysis.parts.OrderBy(p => p.bounds.size.sqrMagnitude).ThenBy(p => p.id))
            {
                if (part.protectedReason != null || protectedIds?.Contains(part.id) == true) continue;
                float pixels = Pixels(part,matrix,groupWorldSize,entryHeight,settings.screenHeight);
                if (!FinitePositive(pixels) || pixels > settings.maxPixels) continue;
                if ((plan.area+part.area) > analysis.area*settings.maxAreaFraction ||
                    plan.triangles+part.triangles > analysis.triangles*settings.maxTriangleFraction) continue;
                plan.removed.Add(part.id); plan.area += part.area; plan.triangles += part.triangles;
                plan.maxPixels = Mathf.Max(plan.maxPixels,pixels);
            }
            return plan;
        }

        static bool FinitePositive(float v) => v > 0 && !float.IsNaN(v) && !float.IsInfinity(v);

        internal static LodSourceTopology Retain(Analysis analysis,Plan plan)
        {
            var excluded = new HashSet<int>(analysis.parts.Where(p => plan.removed.Contains(p.id)).SelectMany(p => p.faces));
            var faces = analysis.source.faces.Where((f,i) => !excluded.Contains(i)).ToList();
            Mesh mesh = analysis.source.data.CreateMesh(faces,out var remap);
            try
            {
                var mapped = faces.Select(f => new LodSourceTopology.Face { points = (int[])f.points.Clone(), submesh = f.submesh,
                    vertices = f.vertices.Select(v => remap[v]).ToArray(), triangles = f.triangles.Select(v => remap[v]).ToArray(),
                    referenceTriangles = f.referenceTriangles.Select(v => remap[v]).ToArray() }).ToList();
                return new LodSourceTopology(new LodMeshData(mesh),mapped);
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }
    }
}
