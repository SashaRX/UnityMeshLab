using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using Autodesk.Fbx;
using System.IO;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public class LodLoopSimplifierTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        GameObject root;
        string assetFolder;

        [TearDown]
        public void Cleanup()
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            foreach (var mesh in meshes) if (mesh != null && !AssetDatabase.Contains(mesh)) UnityEngine.Object.DestroyImmediate(mesh);
            meshes.Clear();
            if (assetFolder != null) AssetDatabase.DeleteAsset(assetFolder);
            assetFolder = null;
        }

        Mesh Track(Mesh mesh) { meshes.Add(mesh); return mesh; }
        Mesh Grid(int width, int height)
        {
            var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var quads = new List<int>();
            for (int y = 0; y <= height; y++) for (int x = 0; x <= width; x++)
            { vertices.Add(new Vector3(x, y, 0)); uv.Add(new Vector2((float)x/width, (float)y/height)); }
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            { int a = y*(width+1)+x; quads.AddRange(new[] { a, a+1, a+width+2, a+width+1 }); }
            var mesh = Track(new Mesh { name = "SourceGrid", vertices = vertices.ToArray(), uv = uv.ToArray(), uv2 = uv.ToArray() });
            mesh.SetIndices(quads.ToArray(), MeshTopology.Quads, 0); mesh.RecalculateNormals();
            return mesh;
        }
        static LodLoopSimplifier.Settings Settings(float ratio = .5f) => new LodLoopSimplifier.Settings
        {
            simplify = new MeshSimplifier.SimplifySettings { targetRatio = ratio, targetError = .05f, normalWeight = 1, colorWeight = 1, uv2Weight = 1, uvChannel = 1 },
            maxNormalAngle = 15, maxColorError = .02f
        };
        static LodSourceTopology Source(Mesh mesh)
        {
            Assert.That(LodSourceTopology.TryLoad(new MeshEntry { fbxMesh = mesh }, mesh, new Dictionary<string, object>(), out var source, out var error), Is.True, error);
            return source;
        }

        [Test]
        public void OpenLoops_ReduceWholeRowsAndKeepWorkingChannels()
        {
            var mesh = Grid(4, 4);
            var color = new Color(.1234567f, .2345678f, .3456789f, .4567891f);
            mesh.colors = Enumerable.Repeat(color, mesh.vertexCount).ToArray();
            var original = mesh.GetIndices(0);
            var result = LodLoopSimplifier.Simplify(Source(mesh), Settings(.25f));
            Assert.That(result.ok, Is.True, result.error); Track(result.mesh);
            Assert.That(LodMeshData.TriangleCount(result.mesh), Is.EqualTo(8));
            Assert.That(result.removedLoops, Is.GreaterThan(0));
            CollectionAssert.AreEqual(original, mesh.GetIndices(0));
            Assert.That(result.mesh.GetTopology(0), Is.EqualTo(MeshTopology.Triangles));
            Assert.That(result.mesh.GetVertexAttributeFormat(VertexAttribute.Color), Is.EqualTo(VertexAttributeFormat.Float32));
            foreach (var c in result.mesh.colors) Assert.That(c, Is.EqualTo(color));
            var sourceVertices = mesh.vertices; var uv = mesh.uv2;
            for (int i = 0; i < result.mesh.vertexCount; i++)
            {
                int v = Array.IndexOf(sourceVertices, result.mesh.vertices[i]);
                Assert.That(v, Is.GreaterThanOrEqualTo(0)); Assert.That(result.mesh.uv2[i], Is.EqualTo(uv[v]));
            }
        }

        [Test]
        public void BudgetPriority_ReducesDespiteColorLimitsWithoutDroppingChannels()
        {
            var mesh = Grid(8,8); mesh.colors = mesh.vertices.Select(v => new Color(.1234567f,.2f,.3f,
                (((int)v.x*73856093)^((int)v.y*19349663))%101/100f)).ToArray();
            var colors = mesh.colors; var indices = mesh.GetIndices(0);
            var settings = new MeshSimplifier.SimplifySettings { targetRatio = 1f/3,targetError = .001f,colorWeight = 1000,uvChannel = 1 };
            var options = new LodPipelineOps.Options { maxColorError = .001f,maxNormalAngle = 15,prioritizeTriangleBudget = true };
            var result = LodBudgetTriangleSimplifier.Simplify(mesh,settings,options,out var diagnostics,out string note,null); Track(result.simplifiedMesh);
            Assert.That(result.ok,Is.True,result.error); Assert.That(result.simplifiedTriCount,Is.InRange(1,43));
            Assert.That(diagnostics.nativeProbes,Is.GreaterThan(1)); Assert.That(diagnostics.maxColorError,Is.GreaterThan(options.maxColorError));
            Assert.That(result.simplifiedMesh.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(VertexAttributeFormat.Float32));
            Assert.That(result.simplifiedMesh.HasVertexAttribute(VertexAttribute.Normal),Is.True);
            Assert.That(result.simplifiedMesh.HasVertexAttribute(VertexAttribute.TexCoord1),Is.True);
            for (int i = 0; i < result.simplifiedMesh.vertexCount; i++)
            {
                int original = Array.IndexOf(mesh.vertices,result.simplifiedMesh.vertices[i]);
                Assert.That(original,Is.GreaterThanOrEqualTo(0));
                Assert.That(result.simplifiedMesh.colors[i],Is.EqualTo(colors[original]));
                Assert.That(result.simplifiedMesh.uv2[i],Is.EqualTo(mesh.uv2[original]));
            }
            Assert.That(note,Does.Contain("exceeds requested")); CollectionAssert.AreEqual(colors,mesh.colors); CollectionAssert.AreEqual(indices,mesh.GetIndices(0));
        }

        [Test]
        public void BudgetPriority_KeepsLockedBoundaryWhenBudgetCannotBeMet()
        {
            var mesh = Grid(4,4);
            var settings = new MeshSimplifier.SimplifySettings { targetRatio = .05f,targetError = 1,normalWeight = 1,uv2Weight = 1,uvChannel = 1,lockBorder = true };
            var result = LodBudgetTriangleSimplifier.Simplify(mesh,settings,new LodPipelineOps.Options(),out var diagnostics,out _,null);
            Track(result.simplifiedMesh); Assert.That(result.ok,Is.True,result.error);
            Assert.That(result.simplifiedTriCount,Is.GreaterThan(2)); Assert.That(diagnostics.nativeProbes,Is.EqualTo(4));
            foreach (var vertex in mesh.vertices.Where(v => v.x == 0 || v.y == 0 || v.x == 4 || v.y == 4))
                Assert.That(result.simplifiedMesh.vertices,Does.Contain(vertex),"Lock Border survives permissive budget retries.");
        }

        [Test]
        public void BudgetPriority_CancellationDoesNotReturnAnOwnedMesh()
        {
            var mesh = Grid(8,8); int calls = 0;
            Assert.Throws<OperationCanceledException>(() => LodBudgetTriangleSimplifier.Simplify(mesh,Settings().simplify,
                new LodPipelineOps.Options(),out _,out _,() => ++calls > 1));
        }

        [Test]
        public void LockedBoundary_BlocksOpenLoopsWithoutPartialDeletion()
        {
            var mesh = Grid(4, 4); var settings = Settings(.1f); settings.simplify.lockBorder = true;
            var result = LodLoopSimplifier.Simplify(Source(mesh), settings); Track(result.mesh);
            Assert.That(result.removedLoops, Is.Zero);
            Assert.That(LodMeshData.TriangleCount(result.mesh), Is.EqualTo(32));
        }

        [Test]
        public void OffsetPivot_DoesNotMakeFlatFacesAppearDegenerate()
        {
            var mesh = Grid(4,4); mesh.vertices = mesh.vertices.Select(v => v+new Vector3(10000,10000,10000)).ToArray();
            var result = LodLoopSimplifier.Simplify(Source(mesh),Settings()); Track(result.mesh);
            Assert.That(result.ok, Is.True,result.error); Assert.That(result.removedLoops, Is.GreaterThan(0));
            Assert.That(LodMeshData.TriangleCount(result.mesh), Is.LessThan(32));
        }

        [Test]
        public void ClosedCylinderLoop_ReducesEvenWithLockedBoundaries()
        {
            const int sides = 12;
            var vertices = new Vector3[sides*3]; var quads = new List<int>();
            for (int y = 0; y < 3; y++) for (int x = 0; x < sides; x++)
                vertices[y*sides+x] = new Vector3(Mathf.Cos(x*2*Mathf.PI/sides), y, Mathf.Sin(x*2*Mathf.PI/sides));
            for (int y = 0; y < 2; y++) for (int x = 0; x < sides; x++)
                quads.AddRange(new[] { y*sides+x, (y+1)*sides+x, (y+1)*sides+(x+1)%sides, y*sides+(x+1)%sides });
            var mesh = Track(new Mesh { vertices = vertices }); mesh.SetIndices(quads.ToArray(), MeshTopology.Quads, 0); mesh.RecalculateNormals();
            var source = Source(mesh); var settings = Settings(); settings.simplify.lockBorder = true;
            var result = LodLoopSimplifier.Simplify(source, settings); Track(result.mesh);
            Assert.That(result.ok, Is.True, result.error); Assert.That(result.removedLoops, Is.EqualTo(1));
            Assert.That(result.mesh.vertexCount, Is.EqualTo(sides*2)); Assert.That(LodMeshData.TriangleCount(result.mesh), Is.EqualTo(sides*2));
            Assert.That(LodTopologyGraph.TryBuild(source.faces, source.data, out var graph, out _), Is.True);
            Assert.That(graph.signature, Is.EqualTo((1, 0, 2)));
        }

        [Test]
        public void FaceInteriorColorCheck_BlocksDeletionOfAnUnrepresentedPaintedRow()
        {
            var mesh = Grid(1, 2);
            mesh.colors = mesh.vertices.Select(v => new Color(v.y == 1 ? 1 : 0, .123456f, 0, 1)).ToArray();
            var settings = Settings(); settings.simplify.targetError = .5f;
            var preserved = LodLoopSimplifier.Simplify(Source(mesh), settings); Track(preserved.mesh);
            Assert.That(preserved.removedLoops, Is.Zero); Assert.That(preserved.blockedLoops, Is.GreaterThan(0));
            settings.simplify.colorWeight = 0;
            settings.skipColorValidation = true;
            var relaxed = LodLoopSimplifier.Simplify(Source(mesh), settings); Track(relaxed.mesh);
            Assert.That(relaxed.removedLoops, Is.EqualTo(1));
            Assert.That(relaxed.maxColorError, Is.GreaterThan(.9f));
        }

        [Test]
        public void GeometricError_BlocksDeletionOfARaisedRow()
        {
            var mesh = Grid(1, 2); var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) if (vertices[i].y == 1) vertices[i].z = .2f;
            mesh.vertices = vertices; mesh.RecalculateNormals();
            var settings = Settings(); settings.simplify.targetError = .001f;
            var result = LodLoopSimplifier.Simplify(Source(mesh), settings); Track(result.mesh);
            Assert.That(result.removedLoops, Is.Zero); Assert.That(LodMeshData.TriangleCount(result.mesh), Is.EqualTo(4));
        }

        [Test]
        public void ZeroColorWeight_DoesNotDisableExplicitColorValidation()
        {
            var mesh = Grid(1,2);
            mesh.colors = mesh.vertices.Select(v => new Color(0,0,0,v.y == 1 ? 1 : 0)).ToArray();
            var settings = Settings(); settings.simplify.colorWeight = 0;
            var result = LodLoopSimplifier.Simplify(Source(mesh),settings); Track(result.mesh);
            Assert.That(result.ok, Is.True,result.error);
            Assert.That(result.removedLoops, Is.Zero, "Alpha masks must survive even when attribute cost is zero.");
        }

        [Test]
        public void ColorMetrics_ArePerChannelAndWeightedBySurfaceArea()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero, new Vector3(2,0,0), Vector3.up,
                new Vector3(10,0,0), new Vector3(10.2f,0,0), new Vector3(10,.1f,0) }, triangles = new[] {0,1,2,3,4,5} });
            source.colors = Enumerable.Repeat(Color.clear,6).ToArray();
            var target = Track(UnityEngine.Object.Instantiate(source));
            target.colors = new[] { new Color(.1f,.2f,.3f,.4f), new Color(.1f,.2f,.3f,.4f), new Color(.1f,.2f,.3f,.4f),
                new Color(1,0,0,.8f),new Color(1,0,0,.8f),new Color(1,0,0,.8f) };
            var metrics = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify);
            Assert.That(metrics.colorMax.x, Is.EqualTo(1).Within(1e-5));
            Assert.That(metrics.colorMax.y, Is.EqualTo(.2f).Within(1e-5));
            Assert.That(metrics.colorMax.z, Is.EqualTo(.3f).Within(1e-5));
            Assert.That(metrics.colorMax.w, Is.EqualTo(.8f).Within(1e-5));
            Assert.That(metrics.ColorRms.x, Is.EqualTo(Mathf.Sqrt(.02f/1.01f)).Within(1e-4));
            Assert.That(metrics.ColorRms.w, Is.EqualTo(Mathf.Sqrt((.16f+.0064f)/1.01f)).Within(1e-4));
            Assert.That(metrics.colorArea, Is.EqualTo(2.02).Within(1e-4), "Both directions must be integrated with actual area.");
        }

        [Test]
        public void ColorValidation_RefusesDroppedChannelEvenWithZeroWeight()
        {
            var source = Grid(1,1); source.colors = Enumerable.Repeat(Color.white,source.vertexCount).ToArray();
            var target = Track(UnityEngine.Object.Instantiate(source)); target.colors = Array.Empty<Color>();
            var settings = Settings().simplify; settings.colorWeight = 0;
            var metrics = LodSurfaceValidation.MeasureMeshes(source,target,settings);
            Assert.That(float.IsPositiveInfinity(metrics.colorError), Is.True);
            Assert.That(float.IsPositiveInfinity(metrics.weightedError), Is.True);
        }

        [Test]
        public void ColorOnlyValidation_IgnoresZeroAreaTrianglesWithoutWeakeningStrictShapeChecks()
        {
            var source = Grid(1,1); source.SetTriangles(LodMeshData.Triangles(source,0),0);
            source.colors = Enumerable.Repeat(new Color(.1234567f,.2f,.3f,.4f),source.vertexCount).ToArray();
            var target = Track(UnityEngine.Object.Instantiate(source));
            target.SetTriangles(source.triangles.Concat(new[] {0,0,1}).ToArray(),0);
            var color = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify,colorsOnly:true);
            Assert.That(color.colorError, Is.LessThan(1e-5));
            Assert.That(float.IsInfinity(color.weightedError), Is.False);
            var strict = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify);
            Assert.That(float.IsInfinity(strict.weightedError), Is.True);
        }

        [Test]
        public void SurfaceValidation_ResolvesColorSeamsWithoutHidingChangedColors()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,
                Vector3.right,Vector3.right+Vector3.up,Vector3.up }, triangles = new[] {0,1,2,3,4,5},
                colors = new[] { new Color(1,0,0,.2f),new Color(1,0,0,.2f),new Color(1,0,0,.2f),
                    new Color(0,0,1,.8f),new Color(0,0,1,.8f),new Color(0,0,1,.8f) } });
            source.RecalculateNormals(); source.RecalculateBounds();
            var copy = Track(UnityEngine.Object.Instantiate(source));
            copy.triangles = new[] {3,4,5,0,1,2};
            var metrics = LodSurfaceValidation.MeasureMeshes(source,copy,Settings().simplify);
            Assert.That(metrics.colorError,Is.LessThan(1e-5),"An unchanged seam must not sample its other side.");
            Assert.That(metrics.ColorRms.magnitude,Is.LessThan(1e-5));
            copy.colors = Enumerable.Repeat(new Color(1,0,0,.2f),6).ToArray();
            copy.triangles = source.triangles;
            metrics = LodSurfaceValidation.MeasureMeshes(source,copy,Settings().simplify);
            Assert.That(metrics.colorMax.z,Is.EqualTo(1).Within(1e-5));
            Assert.That(metrics.colorMax.w,Is.EqualTo(.6f).Within(1e-5));
            Assert.That(metrics.ColorRms.z,Is.GreaterThan(.6f),"Changed field must still fail, despite identical geometry.");
        }

        [Test]
        public void SurfaceValidation_CanBeCancelledInsideFinalMeasurement()
        {
            var source = Grid(1,1); source.colors = Enumerable.Repeat(Color.white,source.vertexCount).ToArray();
            int calls = 0;
            Assert.Throws<OperationCanceledException>(() => LodSurfaceValidation.MeasureMeshes(source,source,Settings().simplify,() => ++calls > 1));
        }

        [Test]
        public void ColorInteriorSampling_IncreasesDensityForGradientsOnEitherSurface()
        {
            var source = Grid(1,1); source.colors = Enumerable.Repeat(Color.black,source.vertexCount).ToArray();
            var target = Track(UnityEngine.Object.Instantiate(source));
            int flat = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify).colorSamples;
            target.colors = target.vertices.Select(v => new Color(v.x,0,0,1)).ToArray();
            var metrics = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify);
            Assert.That(metrics.colorSamples, Is.GreaterThan(flat));
            Assert.That(metrics.colorMax.x, Is.EqualTo(1).Within(1e-5));
            Assert.That(metrics.ColorRms.x, Is.EqualTo(Mathf.Sqrt(1f/3)).Within(.002));
        }

        [Test]
        public void ReplacementQuad_SelectsDiagonalThatPreservesSourceColorField()
        {
            var mesh = Grid(1,1);
            // These samples lie on the second diagonal's piecewise-linear field.
            mesh.colors = mesh.vertices.Select(v => new Color(Mathf.Min(v.x, 1-v.y),0,0,1)).ToArray();
            var source = Source(mesh);
            var face = source.faces[0]; var v = face.vertices;
            var expected = new[] {v[0],v[1],v[3],v[1],v[2],v[3]};
            face.referenceTriangles = expected;
            var settings = Settings(); settings.simplify.targetError = .5f; settings.maxColorError = .01f;
            var intersections = new LodTriangleIntersections(source.faces,source.data.positions);
            Assert.That(LodLoopSimplifier.TryChooseDiagonal(face,source.data,settings,intersections,new HashSet<int> {0},out var metrics), Is.True);
            CollectionAssert.AreEqual(expected,face.triangles);
            Assert.That(metrics.colorError, Is.LessThan(.001f), "The first diagonal would erase the authored field.");
        }

        [Test]
        public void QualityCandidates_IncludeFastResultAndSelectDeterministicallyAtEqualBudget()
        {
            var mesh = Grid(4,4);
            mesh.colors = mesh.vertices.Select(v => new Color(.002f*v.x*v.y,.003f*v.y*v.y,0,1)).ToArray();
            var source = Source(mesh); var settings = Settings(.25f); settings.maxColorError = .1f;
            var candidates = new List<LodLoopSimplifier.Result>();
            for (int strategy = 0; strategy < 5; strategy++)
            {
                settings.strategyOffset = strategy;
                var candidate = LodLoopSimplifier.Simplify(source,settings); Track(candidate.mesh);
                Assert.That(candidate.ok, Is.True,candidate.error); candidates.Add(candidate);
            }
            settings.strategyOffset = 0; settings.candidateCount = 5;
            var selected = LodLoopSimplifier.Simplify(source,settings); Track(selected.mesh);
            var repeated = LodLoopSimplifier.Simplify(source,settings); Track(repeated.mesh);
            Assert.That(selected.evaluatedCandidates, Is.EqualTo(5));
            Assert.That(selected.selectedCandidate, Is.InRange(1,5));
            foreach (var candidate in candidates.Where(c => LodMeshData.TriangleCount(c.mesh) == LodMeshData.TriangleCount(selected.mesh)))
                Assert.That(selected.metrics.weightedError, Is.LessThanOrEqualTo(candidate.metrics.weightedError+1e-7f));
            CollectionAssert.AreEqual(selected.mesh.triangles,repeated.mesh.triangles);
            CollectionAssert.AreEqual(selected.mesh.colors,repeated.mesh.colors);
            Assert.That(repeated.selectedCandidate, Is.EqualTo(selected.selectedCandidate));
            Assert.That(mesh.vertexCount, Is.EqualTo(25));
        }

        [Test]
        public void QualityCancellation_ReleasesPreviouslyCompletedCandidates()
        {
            var mesh = Grid(1,1); var settings = Settings(1); settings.candidateCount = 5;
            int calls = 0;
            var before = Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name == mesh.name);
            bool cancelled = false;
            var result = LodLoopSimplifier.Simplify(Source(mesh),settings,() => cancelled,(index, count) => { calls++; cancelled = index == 2; });
            Track(result.mesh);
            Assert.That(result.cancelled, Is.True);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name == mesh.name), Is.EqualTo(before));
        }

        [Test]
        public void NormalField_BlocksDeletionOfAnAuthoredShadingRow()
        {
            var mesh = Grid(1,2);
            mesh.normals = mesh.vertices.Select(v => v.y == 1 ? new Vector3(.5f,0,1).normalized : Vector3.forward).ToArray();
            var settings = Settings(); settings.simplify.normalWeight = 0; settings.maxNormalAngle = 5;
            var result = LodLoopSimplifier.Simplify(Source(mesh),settings); Track(result.mesh);
            Assert.That(result.removedLoops, Is.Zero, "Normal-angle validation must work independently of the native cost weight.");
        }

        [Test]
        public void AdditionalUvChannelsAndByteColors_KeepTheirFormats()
        {
            var mesh = Grid(4,4);
            mesh.SetUVs(3,mesh.vertices.Select(v => new Vector3(v.x,v.y,.123456f)).ToList());
            mesh.SetUVs(7,mesh.vertices.Select(v => new Vector4(v.x,v.y,.234567f,.345678f)).ToList());
            mesh.colors32 = Enumerable.Repeat(new Color32(13,27,41,55),mesh.vertexCount).ToArray();
            var result = LodLoopSimplifier.Simplify(Source(mesh),Settings()); Track(result.mesh);
            Assert.That(result.removedLoops, Is.GreaterThan(0));
            Assert.That(result.mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord3), Is.EqualTo(3));
            Assert.That(result.mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord7), Is.EqualTo(4));
            Assert.That(result.mesh.GetVertexAttributeFormat(VertexAttribute.Color), Is.EqualTo(VertexAttributeFormat.UNorm8));
            var uv = new List<Vector4>(); result.mesh.GetUVs(7,uv);
            Assert.That(uv.All(v => v.z == .234567f && v.w == .345678f), Is.True);
        }

        [Test]
        public void MaterialBoundary_IsProtectedEvenWithLooseErrorLimits()
        {
            var mesh = Grid(1, 2); var quads = mesh.GetIndices(0); mesh.subMeshCount = 2;
            mesh.SetIndices(quads.Take(4).ToArray(), MeshTopology.Quads, 0); mesh.SetIndices(quads.Skip(4).ToArray(), MeshTopology.Quads, 1);
            var result = LodLoopSimplifier.Simplify(Source(mesh), Settings()); Track(result.mesh);
            Assert.That(result.removedLoops, Is.Zero); Assert.That(result.mesh.subMeshCount, Is.EqualTo(2));
        }

        [Test]
        public void Cancellation_ReturnsNoTemporaryMeshAndDoesNotEditSource()
        {
            var mesh = Grid(4, 4); var indices = mesh.GetIndices(0);
            var result = LodLoopSimplifier.Simplify(Source(mesh), Settings(), () => true);
            Assert.That(result.cancelled, Is.True); Assert.That(result.mesh, Is.Null); CollectionAssert.AreEqual(indices, mesh.GetIndices(0));
        }

        [Test]
        public void TriangleOnlyProceduralMesh_IsRefusedInsteadOfInventingQuads()
        {
            var mesh = Grid(1, 2); mesh.SetTriangles(LodMeshData.Triangles(mesh, 0), 0);
            Assert.That(LodSourceTopology.TryLoad(new MeshEntry { fbxMesh = mesh }, mesh, new Dictionary<string, object>(), out _, out var error), Is.False);
            Assert.That(error, Does.Contain("FBX"));
        }

        [Test]
        public void TopologyCheck_RejectsBowTieVertexFans()
        {
            var mesh = Track(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.left, Vector3.down } });
            var faces = new List<LodSourceTopology.Face>();
            foreach (int[] v in new[] { new[] {0,1,2}, new[] {0,3,4} }) faces.Add(new LodSourceTopology.Face { points = v, vertices = v, triangles = v, referenceTriangles = v });
            Assert.That(LodTopologyGraph.TryBuild(faces, new LodMeshData(mesh), out _, out _), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Intersections_DetectCrossingsAndCoplanarOverlap(bool coplanar)
        {
            Vector3[] p = { new Vector3(0,0,0), new Vector3(2,0,0), new Vector3(0,2,0),
                new Vector3(.2f,.2f,coplanar ? 0 : -1), new Vector3(.8f,.2f,coplanar ? 0 : 1), new Vector3(.2f,.8f,0) };
            Assert.That(LodTriangleIntersections.Overlap(p,new[] {0,1,2},0,new[] {3,4,5},0), Is.True);
        }

        [Test]
        public void Intersections_AllowSharedEdgesAndVertices()
        {
            Vector3[] p = { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1,1,0), new Vector3(0,0,1) };
            Assert.That(LodTriangleIntersections.Overlap(p,new[] {0,1,2},0,new[] {1,3,2},0), Is.False);
            Assert.That(LodTriangleIntersections.Overlap(p,new[] {0,1,2},0,new[] {0,4,1},0), Is.False);
        }

        [Test]
        public void Generate_UsesOriginalForEveryLevelAndReportsLoopDiagnostics()
        {
            var mesh = Grid(4, 4);
            root = new GameObject("Grid"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var group = LodGenerationTool.CreateLodGroupFromRenderers(root);
            var context = new UvToolContext(); context.Refresh(group);
            var options = new LodPipelineOps.Options { count = 2, ratios = new[] {.5f,.25f}, targetError = .05f, uv2Weight = 1, normalWeight = 1,
                colorWeight = 1, reductionMode = LodReductionMode.FullLoops, maxNormalAngle = 15, maxColorError = .02f };
            var expectedFirst = LodLoopSimplifier.Simplify(Source(mesh),Settings(.5f)); Track(expectedFirst.mesh);
            var expectedSecond = LodLoopSimplifier.Simplify(Source(mesh),Settings(.25f)); Track(expectedSecond.mesh);
            var result = LodPipelineOps.Generate(context,1,options);
            Assert.That(result.ok, Is.True, result.error); Assert.That(result.perLod, Has.Count.EqualTo(2));
            foreach (var go in result.generatedObjects) Track(go.GetComponent<MeshFilter>().sharedMesh);
            Assert.That(result.perLod[0].simplifiedTris, Is.EqualTo(LodMeshData.TriangleCount(expectedFirst.mesh)), "First LOD must match independent source reduction.");
            Assert.That(result.perLod[1].simplifiedTris, Is.EqualTo(LodMeshData.TriangleCount(expectedSecond.mesh)), "Every ratio is applied to LOD0, not the preceding reduced mesh.");
            Assert.That(result.perLod.All(r => r.sourceQuads == 16 && r.removedLoops > 0), Is.True);
            Assert.That(LodMeshData.TriangleCount(mesh), Is.EqualTo(32));
        }

        [Test]
        public void FailedSourcePreflight_PreservesExistingGeneratedObjects()
        {
            var mesh = Grid(4,4); mesh.SetTriangles(LodMeshData.Triangles(mesh,0),0);
            root = new GameObject("Grid"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var context = new UvToolContext(); context.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var tool = new LodGenerationTool(); tool.OnActivate(context,null);
            typeof(LodGenerationTool).GetMethod("ExecGenerateLods",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(tool,new object[] {1});
            var generated = context.GeneratedLodObjects.ToArray();
            foreach (var go in generated) Track(go.GetComponent<MeshFilter>().sharedMesh);
            typeof(LodGenerationTool).GetField("generateReductionMode",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(tool,LodReductionMode.FullLoops);
            typeof(LodGenerationTool).GetMethod("ExecGenerateLods",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(tool,new object[] {3});
            CollectionAssert.AreEqual(generated,context.GeneratedLodObjects); Assert.That(generated.All(go => go != null), Is.True);
        }

        [Test]
        public void HybridMode_AppliesTriangleStageAndValidatesAgainstLod0()
        {
            var mesh = Grid(8,8);
            root = new GameObject("Grid"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var context = new UvToolContext(); context.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var options = new LodPipelineOps.Options { count = 1, ratios = new[] {.25f}, targetError = .05f, uv2Weight = 1, normalWeight = 1,
                colorWeight = 1, lockBorder = true, reductionMode = LodReductionMode.LoopsThenTriangles, maxNormalAngle = 15, maxColorError = .02f };
            var result = LodPipelineOps.Generate(context,1,options);
            Assert.That(result.ok, Is.True,result.error);
            foreach (var go in result.generatedObjects) Track(go.GetComponent<MeshFilter>().sharedMesh);
            Assert.That(result.perLod, Has.Count.EqualTo(1)); Assert.That(result.perLod[0].removedLoops, Is.Zero);
            Assert.That(result.perLod[0].simplifiedTris, Is.LessThan(128));
            Assert.That(result.perLod[0].reductionNote, Does.Contain("Triangle stage applied"));
            Assert.That(result.perLod[0].sourceDistance, Is.LessThan(.001f));
        }

        [Test]
        public void TriangleSimplifier_ColorCostAffectsReductionAndFloatColorsSurvive()
        {
            var mesh = Grid(12,12); mesh.SetTriangles(LodMeshData.Triangles(mesh,0),0);
            mesh.colors = mesh.vertices.Select(v => new Color(((int)v.x+(int)v.y)%2,.1234567f,.2345678f,.4567891f)).ToArray();
            var settings = new MeshSimplifier.SimplifySettings { targetRatio = .1f, targetError = .001f, uvChannel = 1 };
            var unweighted = MeshSimplifier.Simplify(mesh,settings); Assert.That(unweighted.ok, Is.True, unweighted.error); Track(unweighted.simplifiedMesh);
            settings.colorWeight = 1;
            var weighted = MeshSimplifier.Simplify(mesh,settings); Assert.That(weighted.ok, Is.True, weighted.error); Track(weighted.simplifiedMesh);
            Assert.That(weighted.simplifiedTriCount, Is.GreaterThan(unweighted.simplifiedTriCount));
            foreach (var color in weighted.simplifiedMesh.colors) Assert.That(color.g, Is.EqualTo(.1234567f));
        }

        [Test]
        public void TriangleGeneration_RejectsColorLossEvenWhenNativeColorWeightIsZero()
        {
            var mesh = Grid(6,6); mesh.SetTriangles(LodMeshData.Triangles(mesh,0),0);
            mesh.colors = mesh.vertices.Select(v => new Color(0,0,0,((int)v.x+(int)v.y)%2)).ToArray();
            root = new GameObject("PaintedGrid"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var context = new UvToolContext(); context.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var options = new LodPipelineOps.Options { count = 2, ratios = new[] {.1f,.05f}, targetError = .05f,
                colorWeight = 0, reductionMode = LodReductionMode.Triangles, maxColorError = .02f };
            var result = LodPipelineOps.Generate(context,1,options);
            foreach (var go in result.generatedObjects) Track(go.GetComponent<MeshFilter>().sharedMesh);
            Assert.That(result.ok, Is.True,result.error); Assert.That(result.perLod, Has.Count.EqualTo(2));
            Assert.That(result.perLod[0].simplifiedTris, Is.EqualTo(72));
            Assert.That(result.perLod[0].colorError, Is.LessThan(.001f));
            Assert.That(result.perLod[0].reductionNote, Does.Contain("vertex-color validation"));
            Assert.That(result.perLod[0].targetNotReached, Is.True);
            Assert.That(result.perLod[1].simplifiedTris, Is.EqualTo(72));
            Assert.That(result.perLod[1].reductionNote, Does.Contain("kept source mesh"));
        }

        [Test]
        public void TriangleBudgetSearch_KeepsAPaintedFeatureWithoutKeepingAllSourceTriangles()
        {
            var mesh = Grid(12,12); mesh.SetTriangles(LodMeshData.Triangles(mesh,0),0);
            mesh.colors = mesh.vertices.Select(v => new Color(0,0,0,v.x == 6 && v.y == 6 ? 1 : 0)).ToArray();
            var settings = Settings(.1f).simplify; settings.colorWeight = 0; settings.uv2Weight = 0;
            var options = new LodPipelineOps.Options { maxColorError = .02f, candidateCount = 1 };
            var result = LodValidatedTriangleSimplifier.Simplify(mesh,settings,options,out var diagnostics,out string note);
            Assert.That(result.ok,Is.True,result.error); Track(result.simplifiedMesh);
            Assert.That(result.simplifiedTriCount,Is.LessThan(result.originalTriCount),"A local color feature must not force the whole source to survive.");
            Assert.That(diagnostics.evaluatedCandidates,Is.GreaterThan(1),"The requested budget should fail and trigger safer probes.");
            Assert.That(note,Does.Contain("safer triangle budget"));
            var measured = LodSurfaceValidation.MeasureMeshes(mesh,result.simplifiedMesh,settings,colorsOnly:true);
            Assert.That(measured.colorError,Is.LessThanOrEqualTo(.02f));
            Assert.That((measured.ColorRms-diagnostics.metrics.ColorRms).magnitude,Is.LessThan(1e-6));
            settings.targetRatio = .01f;
            var farther = LodValidatedTriangleSimplifier.Simplify(mesh,settings,options,out _,out _,previousValidated:result.simplifiedMesh);
            Assert.That(farther.ok,Is.True,farther.error); Track(farther.simplifiedMesh);
            Assert.That(farther.simplifiedTriCount,Is.LessThanOrEqualTo(result.simplifiedTriCount));
            Assert.That(farther.simplifiedMesh,Is.Not.SameAs(result.simplifiedMesh),"Levels must own separate meshes.");
        }

        [Test]
        public void FarLodQuality_RelaxesOnlyLaterLevelsAndUsesActualLodNumbers()
        {
            var baseline = new LodPipelineOps.Options { count = 3,targetError = .2f,normalWeight = 1,colorWeight = 1,maxNormalAngle = 15,maxColorError = .02f };
            var far = new LodPipelineOps.LevelQuality { targetError = .3f,normalWeight = .5f,colorWeight = .25f,maxNormalAngle = 30,maxColorError = .1f };
            var options = LodPipelineOps.RelaxFarLods(baseline,1,2,far);
            var near = LodPipelineOps.ForLevel(options,0); var middle = LodPipelineOps.ForLevel(options,1); var last = LodPipelineOps.ForLevel(options,2);
            Assert.That(near.targetError,Is.EqualTo(.2f)); Assert.That(near.maxColorError,Is.EqualTo(.02f));
            Assert.That(middle.targetError,Is.EqualTo(.25f).Within(1e-6)); Assert.That(middle.maxColorError,Is.EqualTo(.06f).Within(1e-6));
            Assert.That(last.targetError,Is.EqualTo(.3f)); Assert.That(last.maxColorError,Is.EqualTo(.1f));
            Assert.That(last.normalWeight,Is.EqualTo(.5f)); Assert.That(last.colorWeight,Is.EqualTo(.25f)); Assert.That(last.maxNormalAngle,Is.EqualTo(30));
            Assert.That(baseline.levelQuality,Is.Null); Assert.That(baseline.targetError,Is.EqualTo(.2f));
            baseline.count = 1;
            Assert.That(LodPipelineOps.ForLevel(LodPipelineOps.RelaxFarLods(baseline,1,2,far),0).maxColorError,Is.EqualTo(.02f));
            Assert.That(LodPipelineOps.ForLevel(LodPipelineOps.RelaxFarLods(baseline,3,2,far),0).maxColorError,Is.EqualTo(.1f));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void FarLodQuality_AllowsBoundedAlphaLossAndReportsEffectiveLimits(int modeIndex)
        {
            var mode = (LodReductionMode)modeIndex;
            var mesh = Grid(6,6);
            if (mode == LodReductionMode.Triangles) mesh.SetTriangles(LodMeshData.Triangles(mesh,0),0);
            mesh.colors = mesh.vertices.Select(v => new Color(.1f,.2f,.3f,.08f*(((int)v.x+(int)v.y)%2))).ToArray();
            var sourceColors = mesh.colors;
            root = new GameObject("FarPaintedGrid"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var context = new UvToolContext(); context.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var baseline = new LodPipelineOps.Options { count = 2,ratios = new[] {.5f,.125f},targetError = .05f,
                reductionMode = mode,maxNormalAngle = 15,maxColorError = .01f,colorWeight = 0,candidateCount = 1 };
            var options = LodPipelineOps.RelaxFarLods(baseline,1,2,new LodPipelineOps.LevelQuality {
                targetError = .1f,normalWeight = 0,colorWeight = 0,maxNormalAngle = 30,maxColorError = .1f
            });
            var result = LodPipelineOps.Generate(context,1,options);
            foreach (var go in result.generatedObjects) Track(go.GetComponent<MeshFilter>().sharedMesh);
            Assert.That(result.ok,Is.True,result.error); Assert.That(result.perLod,Has.Count.EqualTo(2));
            Assert.That(result.perLod[0].colorError,Is.LessThanOrEqualTo(.01f));
            Assert.That(result.perLod[0].allowedColorError,Is.EqualTo(.01f));
            Assert.That(result.perLod[1].colorError,Is.InRange(.01f,.1f));
            Assert.That(result.perLod[1].allowedColorError,Is.EqualTo(.1f));
            Assert.That(result.perLod[1].targetError,Is.EqualTo(.1f));
            Assert.That(result.perLod[1].simplifiedTris,Is.LessThan(result.perLod[0].simplifiedTris));
            var reduced = result.generatedObjects[1].GetComponent<MeshFilter>().sharedMesh;
            Assert.That(reduced.HasVertexAttribute(VertexAttribute.Color),Is.True);
            var measured = LodSurfaceValidation.MeasureMeshes(mesh,reduced,new MeshSimplifier.SimplifySettings { targetError = .1f },colorsOnly:true);
            Assert.That(measured.colorError,Is.LessThanOrEqualTo(.1f));
            Assert.That(mesh.colors,Is.EqualTo(sourceColors),"Relaxation must not rewrite LOD0 colors.");
        }

        [Test]
        public void InvalidPerLodQuality_IsRefusedBeforeChangingTheGroup()
        {
            var mesh = Grid(1,1); root = new GameObject("InvalidProfile");
            root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var context = new UvToolContext(); context.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var options = new LodPipelineOps.Options { count = 2,ratios = new[] {.5f,.25f},
                levelQuality = new[] { new LodPipelineOps.LevelQuality { targetError = .1f } } };
            var result = LodPipelineOps.Generate(context,1,options);
            Assert.That(result.ok,Is.False); Assert.That(result.error,Does.Contain("Missing per-LOD quality"));
            Assert.That(context.LodGroup.GetLODs(),Has.Length.EqualTo(1));
            Assert.That(root.GetComponent<MeshFilter>().sharedMesh,Is.SameAs(mesh));
            options.count = 1; options.levelQuality[0].maxColorError = float.NaN;
            Assert.That(LodPipelineOps.Generate(context,1,options).ok,Is.False);
            Assert.That(root.GetComponentsInChildren<MeshRenderer>(),Has.Length.EqualTo(1));
        }

        [Test]
        public void ColorValidation_EarlyRejectsFailuresButFullyMeasuresAcceptedCandidates()
        {
            var source = Grid(6,6); source.colors = Enumerable.Repeat(Color.black,source.vertexCount).ToArray();
            var target = Track(UnityEngine.Object.Instantiate(source));
            target.colors = Enumerable.Repeat(new Color(.1f,0,0,1),target.vertexCount).ToArray();
            var limits = LodSurfaceValidation.Limits.Unbounded; limits.color = .02f;
            var full = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify,colorsOnly:true);
            var stopped = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify,colorsOnly:true,limits:limits);
            Assert.That(stopped.Rejected(limits),Is.True);
            Assert.That(stopped.colorSamples,Is.LessThan(full.colorSamples));
            limits.color = .2f;
            var accepted = LodSurfaceValidation.MeasureMeshes(source,target,Settings().simplify,colorsOnly:true,limits:limits);
            Assert.That(accepted.Rejected(limits),Is.False);
            Assert.That(accepted.colorSamples,Is.EqualTo(full.colorSamples));
            Assert.That(accepted.ColorRms,Is.EqualTo(full.ColorRms));
        }

        [Test]
        public void HybridQuality_SelectsAmongCompleteResultsAndReportsChannelMetrics()
        {
            var mesh = Grid(4,4); mesh.colors = mesh.vertices.Select(v => new Color(.002f*v.x*v.y,.1f,.2f,.3f)).ToArray();
            root = new GameObject("Grid"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var context = new UvToolContext(); context.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var options = new LodPipelineOps.Options { count = 1, ratios = new[] {.25f}, targetError = .05f, uv2Weight = 1, normalWeight = 1,
                colorWeight = 1, reductionMode = LodReductionMode.LoopsThenTriangles, maxNormalAngle = 15, maxColorError = .02f, candidateCount = 3 };
            var result = LodPipelineOps.Generate(context,1,options);
            foreach (var go in result.generatedObjects) Track(go.GetComponent<MeshFilter>().sharedMesh);
            Assert.That(result.ok, Is.True,result.error); Assert.That(result.perLod, Has.Count.EqualTo(1));
            var info = result.perLod[0];
            Assert.That(info.evaluatedCandidates, Is.EqualTo(3)); Assert.That(info.selectedCandidate, Is.InRange(1,3));
            var measured = LodSurfaceValidation.MeasureMeshes(mesh,result.generatedObjects[0].GetComponent<MeshFilter>().sharedMesh,Settings().simplify);
            Assert.That((info.colorRms-measured.ColorRms).magnitude, Is.LessThan(1e-5));
            Assert.That((info.colorMax-measured.colorMax).magnitude, Is.LessThan(1e-5));
            Assert.That(info.colorError, Is.LessThanOrEqualTo(.02f));
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [TestCase(ModelImporterMeshCompression.Off,4)]
        [TestCase(ModelImporterMeshCompression.Low,4)]
        [TestCase(ModelImporterMeshCompression.Medium,4)]
        [TestCase(ModelImporterMeshCompression.High,4)]
        [TestCase(ModelImporterMeshCompression.Low,32)]
        public void SourceFbx_UsesOriginalQuadsAndEditedWorkingColorsAndUv2(ModelImporterMeshCompression compression,int width)
        {
            var authored = Grid(width,width);
            assetFolder = "Assets/__LodTopologyTest_"+Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets",assetFolder.Substring(7));
            string path = assetFolder+"/source.fbx";
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager,Globals.IOSROOT); manager.SetIOSettings(io);
                var scene = FbxScene.Create(manager,"Source"); var fbxMesh = FbxMesh.Create(scene,"SourceGrid");
                fbxMesh.InitControlPoints(authored.vertexCount);
                var vertices = authored.vertices;
            for (int i = 0; i < vertices.Length; i++) fbxMesh.SetControlPointAt(new FbxVector4(vertices[i].x*100+(i%5)*.017,vertices[i].y*100,0),i);
                var quads = authored.GetIndices(0);
                for (int i = 0; i < quads.Length; i += 4)
                { fbxMesh.BeginPolygon(); for (int j = 0; j < 4; j++) fbxMesh.AddPolygon(quads[i+j]); fbxMesh.EndPolygon(); }
                var node = FbxNode.Create(scene,"SourceGrid"); node.SetNodeAttribute(fbxMesh); scene.GetRootNode().AddChild(node);
                using var exporter = FbxExporter.Create(manager,"Export");
                Assert.That(exporter.Initialize(Path.GetFullPath(path),-1,io), Is.True); Assert.That(exporter.Export(scene), Is.True);
            }
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            byte[] originalBytes = File.ReadAllBytes(path);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = true; importer.globalScale = .37f; importer.meshCompression = compression; importer.SaveAndReimport();
            var imported = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Single();
            var working = Track(UnityEngine.Object.Instantiate(imported));
            working.colors = Enumerable.Repeat(new Color(.1234567f,.3456789f,0,1),working.vertexCount).ToArray();
            working.uv2 = working.vertices.Select(v => new Vector2(v.x,v.y)*.03f+new Vector2(.2f,.3f)).ToArray();
            if (width == 32) { importer.isReadable = false; importer.SaveAndReimport(); }
            var entry = new MeshEntry { fbxMesh = imported, originalMesh = working };
            Assert.That(LodSourceTopology.TryLoad(entry,working,new Dictionary<string,object>(),out var source,out var error), Is.True,error);
            Assert.That(source.QuadCount, Is.EqualTo(width*width));
            if (width == 4)
            {
                var result = LodLoopSimplifier.Simplify(source,Settings()); Track(result.mesh);
                Assert.That(result.removedLoops, Is.GreaterThan(0)); Assert.That(LodMeshData.TriangleCount(result.mesh), Is.EqualTo(16));
                Assert.That(result.mesh.colors.All(c => c.r == .1234567f), Is.True);
            }
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(originalBytes), "Raw import must preserve the original FBX bytes.");
            Assert.That(((ModelImporter)AssetImporter.GetAtPath(path)).meshCompression, Is.EqualTo(compression));
            Assert.That(((ModelImporter)AssetImporter.GetAtPath(path)).isReadable, Is.EqualTo(width != 32));
            Assert.That(AssetDatabase.IsValidFolder("Assets/__MeshLabRawLod"), Is.False, "Raw import must clean its temporary asset.");
            Assert.That(AssetDatabase.IsValidFolder("Assets/__MeshLabTemp"), Is.False, "Tagged import must clean its temporary asset.");
            var changed = working.triangles; var reversed = (int[])changed.Clone();
            int temporary = reversed[0]; reversed[0] = reversed[1]; reversed[1] = temporary; working.SetTriangles(reversed,0);
            Assert.That(LodSourceTopology.TryLoad(entry,working,new Dictionary<string,object>(),out _,out _), Is.False, "Changed winding must be refused.");
            working.SetTriangles(changed.Take(changed.Length-3).ToArray(),0);
            Assert.That(LodSourceTopology.TryLoad(entry,working,new Dictionary<string,object>(),out _,out _), Is.False);
        }
#endif
    }
}
