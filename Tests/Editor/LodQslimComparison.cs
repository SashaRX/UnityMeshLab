using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    // Opt-in external-backend experiment. Not shipped as a runtime/Editor dependency.
    internal static class LodQslimComparison
    {
        [Serializable] internal sealed class Submesh { public int[] triangles; }
        [Serializable] internal sealed class Source
        {
            public string fixture, sourceSha256;
            public float scale;
            public Vector3[] positions;
            public int[] controlPoints, matchedTargets;
            public Submesh[] submeshes;
        }
        [Serializable] internal sealed class OutputSubmesh
        { public Vector3[] positions; public int[] triangles, birthFaces; }
        [Serializable] internal sealed class Level
        { public int level, target, actual; public bool valid, budgetReached; public double nativeMs; public string error; public OutputSubmesh[] submeshes; [NonSerialized] public int facingFallbacks; }
        [Serializable] internal sealed class Output
        { public string fixture, sourceSha256, exportSha256, library; public bool blockIntersections; public Level[] levels; }

        internal static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args,name);
            Assert.That(i,Is.GreaterThanOrEqualTo(0),"Missing experiment argument "+name);
            Assert.That(i+1,Is.LessThan(args.Length)); return args[i+1];
        }
        static string FileFor(string fixture,string suffix) => Path.Combine(LodVisualQualityTests.OutputDirectory(),fixture+suffix);

        internal static void Export(LodProjectVisualQualityTests.Case model,Mesh mesh,Dictionary<string,object> imports)
        {
            Assert.That(LodSourceTopology.TryLoad(new MeshEntry { fbxMesh = mesh },mesh,imports,out var topology,out string error,false),Is.True,error);
            var points = Enumerable.Repeat(-1,mesh.vertexCount).ToArray();
            foreach (var face in topology.faces) for (int i = 0; i < face.vertices.Length; i++)
            {
                int vertex = face.vertices[i];
                Assert.That(points[vertex] < 0 || points[vertex] == face.points[i],Is.True,"Ambiguous source control-point mapping");
                points[vertex] = face.points[i];
            }
            var baseline = JsonUtility.FromJson<LodProjectVisualQualityTests.Report>(File.ReadAllText(Path.Combine(Argument("-meshlabQslimBaseline"),model.name+"-metrics.json")));
            Assert.That(baseline.sourceSha256,Is.EqualTo(model.sourceSha256));
            Assert.That(baseline.sourceTriangles,Is.EqualTo(LodMeshData.TriangleCount(mesh)));
            var submeshes = Enumerable.Range(0,mesh.subMeshCount).Select(s => new Submesh { triangles = LodMeshData.Triangles(mesh,s) }).ToArray();
            foreach (int vertex in submeshes.SelectMany(s => s.triangles)) Assert.That(points[vertex],Is.GreaterThanOrEqualTo(0));
            var data = new Source { fixture = model.name,sourceSha256 = model.sourceSha256,scale = new LodMeshData(mesh).scale,
                positions = mesh.vertices,controlPoints = points,submeshes = submeshes,
                matchedTargets = new[] {1,2}.Select(level => baseline.captures.Single(c => c.view == "front" && c.variant == $"corrected-lod{level}").triangles).ToArray() };
            File.WriteAllText(FileFor(model.name,"-source.json"),JsonUtility.ToJson(data,true));
        }

        internal static void Append(LodProjectVisualQualityTests.Case model,Mesh source,
            List<(string name,Mesh mesh,LodPipelineOps.LodInfo info,double ms)> variants,List<Mesh> generated)
        {
            var exported = JsonUtility.FromJson<Source>(File.ReadAllText(FileFor(model.name,"-source.json")));
            var output = JsonUtility.FromJson<Output>(File.ReadAllText(FileFor(model.name,"-qslim.json")));
            Assert.That(output.sourceSha256,Is.EqualTo(model.sourceSha256)); Assert.That(output.library,Is.EqualTo("2.6.3"));
            using (var hash = System.Security.Cryptography.SHA256.Create())
                Assert.That(output.exportSha256,Is.EqualTo(BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(FileFor(model.name,"-source.json")))).Replace("-","").ToLowerInvariant()));
            CollectionAssert.AreEqual(exported.positions,source.vertices);
            for (int s = 0; s < source.subMeshCount; s++) CollectionAssert.AreEqual(exported.submeshes[s].triangles,LodMeshData.Triangles(source,s));
            foreach (var level in output.levels)
            {
                int comparisonIndex = variants.FindIndex(v => v.name == $"meshopt-lod{level.level}");
                var comparison = variants[comparisonIndex];
                var baselineGeometry = MeasureGeometry(source,comparison.mesh);
                comparison.info.sourceDistance = baselineGeometry.max; comparison.info.sourceDistanceRms = baselineGeometry.rms;
                variants[comparisonIndex] = comparison;
                Assert.That(comparison.info.simplifiedTris,Is.EqualTo(level.target),"Matched baseline changed; regenerate the external jobs.");
                var settings = new MeshSimplifier.SimplifySettings { uvChannel = 1,uv2Weight = 20,
                    normalWeight = comparison.info.normalWeight,colorWeight = comparison.info.colorWeight };
                var options = new LodPipelineOps.Options { maxColorError = comparison.info.allowedColorError,maxNormalAngle = comparison.info.allowedNormalAngle };
                Mesh mesh = null;
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    mesh = level.valid ? Transfer(source,exported.controlPoints,level) : Object.Instantiate(source);
                    var metrics = LodSurfaceValidation.MeasureMeshes(source,mesh,settings,ignoreDegenerateFaces:true);
                    LodAttributeCorrection.Report correction = null;
                    if (level.valid)
                    {
                        var corrected = LodAttributeCorrection.Correct(source,mesh,settings,options,out correction,out metrics,baseline:metrics);
                        if (corrected) { Object.DestroyImmediate(mesh); mesh = corrected; }
                    }
                    var silhouette = new LodSilhouetteValidation(source).Measure(mesh); watch.Stop();
                    var geometry = MeasureGeometry(source,mesh);
                    var info = comparison.info;
                    info.meshName = source.name+"_QSLIM_LOD"+level.level; mesh.name = info.meshName;
                    info.simplifiedTris = LodMeshData.TriangleCount(mesh); info.targetTris = level.target;
                    if (level.valid) Assert.That(info.simplifiedTris,Is.EqualTo(level.actual));
                    info.actualRatio = (float)info.simplifiedTris/LodMeshData.TriangleCount(source);
                    info.targetNotReached = !level.valid || info.simplifiedTris > level.target;
                    info.nativeProbes = 1; info.evaluatedCandidates = 1; info.selectedCandidate = 1; info.budgetCandidates = null;
                    info.sourceDistance = geometry.max; info.sourceDistanceRms = geometry.rms;
                    info.normalError = metrics.normalAngle; info.normalRms = metrics.NormalRms;
                    info.colorError = metrics.colorError; info.colorMax = metrics.colorMax; info.colorRms = metrics.ColorRms;
                    info.uvRms = metrics.UvRms; info.silhouetteMean = silhouette.mean; info.silhouetteMax = silhouette.maximum;
                    info.selectionScore = LodBudgetTriangleSimplifier.Score(metrics,silhouette,settings,options); info.attributeCorrection = correction;
                    info.qualityLimitsExceeded = metrics.normalAngle > options.maxNormalAngle || metrics.colorError > options.maxColorError;
                    info.reductionNote = level.valid
                        ? $"libigl {output.library} QSlim, 3D geometry only; source control-point connectivity, separate material slots, intersections blocked={output.blockIntersections}. Matched actual meshoptimizer budget {level.target}, got {level.actual}. Native {level.nativeMs:F1} ms; transfer/QA/correction {watch.Elapsed.TotalMilliseconds:F1} ms. Birth-face attribute charts reconstructed; {level.facingFallbacks} corners used same-chart nearest projection when orientation preference failed. Geometric projection and verified surface fitting. "+correction?.note
                        : "QSLIM INPUT REFUSED; source reference shown, no simplified QSlim mesh. "+level.error;
                    if (float.IsInfinity(metrics.weightedError) || float.IsNaN(metrics.weightedError))
                        info.reductionNote += " SURFACE ATTRIBUTE CORRESPONDENCE INVALID: partial attribute metrics cannot be interpreted as a quality score.";
                    generated.Add(mesh); variants.Add(($"qslim-lod{level.level}",mesh,info,level.nativeMs+watch.Elapsed.TotalMilliseconds)); mesh = null;
                }
                finally { if (mesh) Object.DestroyImmediate(mesh); }
            }
        }

        internal static Mesh Transfer(Mesh source,int[] points,Level level)
        {
            var data = new LodMeshData(source);
            var sourceNormals = data.normals.Select(n => (Vector4)n).ToArray();
            var sourceColors = data.colors.Select(c => (Vector4)c).ToArray();
            var positions = new List<Vector3>(); var normals = new List<Vector3>(); var colors = new List<Color>(); var tangents = new List<Vector4>();
            var uv = new List<Vector4>[8]; for (int ch = 0; ch < 8; ch++) if (data.uvs[ch] != null) uv[ch] = new List<Vector4>();
            var indices = new List<int>[source.subMeshCount];
            for (int slot = 0; slot < source.subMeshCount; slot++)
            {
                indices[slot] = new List<int>(); var output = level.submeshes[slot];
                if (output.triangles.Length == 0) continue;
                int[] reference = LodMeshData.Triangles(source,slot);
                var charts = Charts(data,reference,points);
                var trees = new Dictionary<int,(int[] faces,TriangleBvh tree,Vector3[] normals)>();
                foreach (int chart in charts.Distinct())
                {
                    int[] faces = Enumerable.Range(0,charts.Length).Where(i => charts[i] == chart).SelectMany(i => reference.Skip(i*3).Take(3)).ToArray();
                    var geometric = Enumerable.Range(0,faces.Length/3).Select(i => LodSurfaceValidation.GeometricNormal(data,faces,i*3)).ToArray();
                    trees[chart] = (faces,new TriangleBvh(data.positions,faces),geometric);
                }
                Mesh geometry = null;
                try
                {
                    geometry = new Mesh { indexFormat = IndexFormat.UInt32,vertices = output.positions,triangles = output.triangles };
                    var reduced = new LodMeshData(geometry); var remap = new Dictionary<(int,int),int>();
                    Assert.That(output.birthFaces.Length,Is.EqualTo(output.triangles.Length/3));
                    for (int i = 0; i < output.triangles.Length; i += 3)
                    {
                        int birth = output.birthFaces[i/3]; Assert.That(birth,Is.InRange(0,charts.Length-1));
                        int chart = charts[birth]; var region = trees[chart];
                        var faceNormal = LodSurfaceValidation.GeometricNormal(reduced,output.triangles,i);
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int vertex = output.triangles[i+corner]; var key = (vertex,chart);
                            if (!remap.TryGetValue(key,out int mapped))
                            {
                                var b = Vector3.zero; b[corner] = 1;
                                var hit = LodSurfaceValidation.CorrespondingHit(reduced,output.triangles,i,b,data,region.faces,region.tree,region.normals,faceNormal,false);
                                if (hit.triangleIndex < 0)
                                {
                                    // QEM placement can rotate a face beyond its birth
                                    // chart's normal hemisphere. Retain chart provenance;
                                    // record this fallback instead of sampling another chart.
                                    hit = region.tree.FindNearest(output.positions[vertex]); level.facingFallbacks++;
                                }
                                Assert.That(hit.triangleIndex,Is.GreaterThanOrEqualTo(0));
                                int j = hit.triangleIndex*3; var bary = hit.barycentric;
                                mapped = positions.Count; remap[key] = mapped; positions.Add(output.positions[vertex]);
                                Vector3 normal = Vector3.zero;
                                if (data.normals.Length == source.vertexCount)
                                { normal = ((Vector3)Sample(sourceNormals,region.faces,j,bary)).normalized; normals.Add(normal); }
                                if (data.colors.Length == source.vertexCount) colors.Add((Color)Sample(sourceColors,region.faces,j,bary));
                                if (data.tangents.Length == source.vertexCount)
                                {
                                    var value = Sample(data.tangents,region.faces,j,bary); Vector3 t = (Vector3)value;
                                    if (normal.sqrMagnitude > .5f) t -= normal*Vector3.Dot(normal,t);
                                    t.Normalize(); tangents.Add(new Vector4(t.x,t.y,t.z,value.w < 0 ? -1 : 1));
                                }
                                for (int ch = 0; ch < 8; ch++) if (uv[ch] != null)
                                    uv[ch].Add(data.uvs[ch][region.faces[j]]*bary.x + data.uvs[ch][region.faces[j+1]]*bary.y + data.uvs[ch][region.faces[j+2]]*bary.z);
                            }
                            indices[slot].Add(mapped);
                        }
                    }
                }
                finally { if (geometry) Object.DestroyImmediate(geometry); }
            }
            var mesh = new Mesh { name = source.name+"_QSlim",indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : source.indexFormat };
            try
            {
                mesh.SetVertices(positions);
                if (normals.Count > 0) mesh.SetNormals(normals);
                if (colors.Count > 0)
                {
                    if (source.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8) mesh.SetColors(colors.ConvertAll(c => (Color32)c));
                    else mesh.SetColors(colors);
                }
                if (tangents.Count > 0) mesh.SetTangents(tangents);
                for (int ch = 0; ch < 8; ch++) if (uv[ch] != null)
                {
                    if (data.uvDimensions[ch] == 2) mesh.SetUVs(ch,uv[ch].ConvertAll(v => (Vector2)v));
                    else if (data.uvDimensions[ch] == 3) mesh.SetUVs(ch,uv[ch].ConvertAll(v => (Vector3)v));
                    else mesh.SetUVs(ch,uv[ch]);
                }
                mesh.subMeshCount = source.subMeshCount;
                for (int s = 0; s < indices.Length; s++) mesh.SetTriangles(indices[s],s);
                mesh.RecalculateBounds(); return mesh;
            }
            catch { Object.DestroyImmediate(mesh); throw; }
        }

        static Vector4 Sample(Vector4[] values,int[] faces,int i,Vector3 b) => values[faces[i]]*b.x + values[faces[i+1]]*b.y + values[faces[i+2]]*b.z;

        // Geometry distance must not depend on the attribute transfer's facing
        // preference. Compare both backends with unrestricted nearest surface points.
        internal static (float rms,float max) MeasureGeometry(Mesh source,Mesh target)
        {
            var original = new LodMeshData(source); var reduced = new LodMeshData(target);
            var interior = new[] {new Vector3(1f/3,1f/3,1f/3),new Vector3(2f/3,1f/6,1f/6),new Vector3(1f/6,2f/3,1f/6),new Vector3(1f/6,1f/6,2f/3)};
            var extrema = new[] {Vector3.right,Vector3.up,Vector3.forward,new Vector3(.5f,.5f,0),new Vector3(.5f,0,.5f),new Vector3(0,.5f,.5f)};
            double squared = 0, area = 0; float maximum = 0;
            for (int slot = 0; slot < source.subMeshCount; slot++)
            {
                var a = LodSurfaceValidation.SurfaceTriangles(original,LodMeshData.Triangles(source,slot));
                var b = LodSurfaceValidation.SurfaceTriangles(reduced,LodMeshData.Triangles(target,slot));
                if (a.Length == 0 && b.Length == 0) continue;
                if (a.Length == 0 || b.Length == 0) return (float.PositiveInfinity,float.PositiveInfinity);
                Direction(original,a,reduced,b); Direction(reduced,b,original,a);
            }
            return (area > 0 ? Mathf.Sqrt((float)(squared/area)) : 0,maximum);
            void Direction(LodMeshData from,int[] faces,LodMeshData to,int[] other)
            {
                var tree = new TriangleBvh(to.positions,other);
                for (int i = 0; i < faces.Length; i += 3)
                {
                    double weight = Vector3.Cross(from.positions[faces[i+1]]-from.positions[faces[i]],from.positions[faces[i+2]]-from.positions[faces[i]]).magnitude/8;
                    foreach (var bary in interior)
                    {
                        float d = Distance(bary); squared += d*d*weight; area += weight; maximum = Mathf.Max(maximum,d);
                    }
                    foreach (var bary in extrema) maximum = Mathf.Max(maximum,Distance(bary));
                    float Distance(Vector3 bary)
                    {
                        var point = from.positions[faces[i]]*bary.x + from.positions[faces[i+1]]*bary.y + from.positions[faces[i+2]]*bary.z;
                        var hit = tree.FindNearest(point); return hit.triangleIndex < 0 ? float.PositiveInfinity : Mathf.Sqrt(hit.distSq)/original.scale;
                    }
                }
            }
        }
        static int[] Charts(LodMeshData data,int[] faces,int[] points)
        {
            var parent = Enumerable.Range(0,faces.Length/3).ToArray();
            int Root(int v) { while (parent[v] != v) { parent[v] = parent[parent[v]]; v = parent[v]; } return v; }
            var edges = new Dictionary<(int,int),(int face,int a,int b)>();
            for (int i = 0; i < faces.Length; i += 3) for (int corner = 0; corner < 3; corner++)
            {
                int a = faces[i+corner],b = faces[i+(corner+1)%3];
                if (points[a] > points[b]) { int swap = a; a = b; b = swap; }
                var key = (points[a],points[b]);
                if (edges.TryGetValue(key,out var edge) && data.SameAttributes(a,edge.a) && data.SameAttributes(b,edge.b))
                    parent[Root(i/3)] = Root(edge.face);
                else edges[key] = (i/3,a,b);
            }
            return parent.Select((_,i) => Root(i)).ToArray();
        }
    }
}
