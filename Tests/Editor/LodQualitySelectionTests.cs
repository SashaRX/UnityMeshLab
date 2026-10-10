using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodQualitySelectionTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        Mesh Track(Mesh mesh) { meshes.Add(mesh); return mesh; }
        [TearDown] public void Cleanup() { foreach (var mesh in meshes) if (mesh) Object.DestroyImmediate(mesh); meshes.Clear(); }
        Mesh Grid(int size)
        {
            var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var faces = new List<int>();
            for (int y = 0; y <= size; y++) for (int x = 0; x <= size; x++)
            { vertices.Add(new Vector3(x,y,0)); uv.Add(new Vector2((float)x/size,(float)y/size)); }
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            { int i = y*(size+1)+x; faces.AddRange(new[] {i,i+1,i+size+2,i+size+1}); }
            var mesh = Track(new Mesh { name = "QualityGrid",vertices = vertices.ToArray() });
            mesh.SetIndices(faces.ToArray(),MeshTopology.Quads,0); mesh.uv2 = uv.ToArray(); mesh.RecalculateNormals(); return mesh;
        }

        [TestCase(101,0,100,10,100,false)]
        [TestCase(99,3,100,8,100,true)]
        [TestCase(20,0,100,8,100,false)]
        [TestCase(105,0,104,100,100,false)]
        [TestCase(100,3,100,3,100,false)]
        public void CandidateSelection_PreservesBudgetAndAvoidsExcessiveUndershoot(int triangles,float score,int bestTriangles,float bestScore,int target,bool wins)
            => Assert.That(LodBudgetTriangleSimplifier.Better(triangles,score,bestTriangles,bestScore,target),Is.EqualTo(wins));

        [Test]
        public void AreaRms_MeasuresUniformNormalChangeOnUnchangedGeometry()
        {
            var source = Grid(4); var target = Track(Object.Instantiate(source));
            target.normals = source.normals.Select(n => Quaternion.Euler(0,60,0)*n).ToArray();
            var metrics = LodSurfaceValidation.MeasureMeshes(source,target,new MeshSimplifier.SimplifySettings { uvChannel = 1 });
            Assert.That(metrics.DistanceRms,Is.LessThan(1e-6f)); Assert.That(metrics.NormalRms,Is.EqualTo(60).Within(.01));
            Assert.That(metrics.UvRms,Is.LessThan(1e-6f)); Assert.That(metrics.surfaceArea,Is.GreaterThan(0));
        }

        [Test]
        public void TinyValidFaces_HaveFiniteMetricsAndZeroAreaFacesCannotHideMissingSurface()
        {
            var source = Grid(2); source.vertices = source.vertices.Select(v => v*.0001f).ToArray();
            source.RecalculateBounds(); var copy = Track(Object.Instantiate(source));
            var settings = new MeshSimplifier.SimplifySettings { uvChannel = 1 };
            var metrics = LodSurfaceValidation.MeasureMeshes(source,copy,settings);
            Assert.That(float.IsInfinity(metrics.weightedError),Is.False); Assert.That(metrics.surfaceArea,Is.GreaterThan(0));
            Assert.That(metrics.DistanceRms,Is.Zero);
            var zeroArea = Track(Object.Instantiate(source)); zeroArea.SetIndices(new[] {0,0,0},MeshTopology.Triangles,0);
            var lost = LodSurfaceValidation.MeasureMeshes(source,zeroArea,settings,ignoreDegenerateFaces:true);
            Assert.That(lost.weightedError,Is.EqualTo(float.PositiveInfinity));
            var withZero = Track(Object.Instantiate(source));
            withZero.SetIndices(LodMeshData.Triangles(source,0).Concat(new[] {0,0,0}).ToArray(),MeshTopology.Triangles,0);
            var ranked = LodSurfaceValidation.MeasureMeshes(source,withZero,settings,ignoreDegenerateFaces:true);
            Assert.That(float.IsInfinity(ranked.weightedError),Is.False); Assert.That(ranked.DistanceRms,Is.Zero);
        }

        [Test]
        public void Silhouette_SeesMissingSurfaceAndIgnoresFaceOrderAndWinding()
        {
            var source = Grid(4); var reordered = Track(Object.Instantiate(source));
            reordered.SetIndices(source.GetIndices(0).Reverse().ToArray(),MeshTopology.Quads,0);
            var validator = new LodSilhouetteValidation(source);
            Assert.That(validator.Measure(reordered).maximum,Is.Zero);
            var missing = Track(Object.Instantiate(source));
            missing.SetIndices(source.GetIndices(0).Take(source.GetIndices(0).Length/2).ToArray(),MeshTopology.Quads,0);
            var error = validator.Measure(missing);
            Assert.That(error.maximum,Is.GreaterThan(.4f)); Assert.That(error.mean,Is.GreaterThan(.1f));
        }

        [Test]
        public void FarProfile_ReducesColorPenaltyWithoutChangingItsMeasuredError()
        {
            var metrics = new LodSurfaceValidation.Metrics { colorArea = 1,colorSquaredIntegral = Vector4.one*.0025f };
            var near = LodBudgetTriangleSimplifier.Score(metrics,default,new MeshSimplifier.SimplifySettings { colorWeight = 1 },
                new LodPipelineOps.Options { maxColorError = .02f });
            var far = LodBudgetTriangleSimplifier.Score(metrics,default,new MeshSimplifier.SimplifySettings { colorWeight = .25f },
                new LodPipelineOps.Options { maxColorError = .1f });
            Assert.That(far,Is.GreaterThan(0).And.LessThan(near)); Assert.That(metrics.ColorRms.x,Is.EqualTo(.05f).Within(1e-6));
        }

        [TestCase(3)]
        [TestCase(5)]
        public void Quality_EvaluatesSourceCandidatesAndReturnsBestBudgetScore(int count)
        {
            var source = Grid(8); source.vertices = source.vertices.Select(v => new Vector3(v.x,v.y,.3f*Mathf.Sin(v.x)*Mathf.Cos(v.y))).ToArray();
            source.RecalculateNormals(); var original = source.vertices; var faces = source.GetIndices(0);
            var settings = new MeshSimplifier.SimplifySettings { targetRatio = 1f/3,targetError = .2f,normalWeight = 1,uv2Weight = 20,colorWeight = 1,uvChannel = 1 };
            var options = new LodPipelineOps.Options { candidateCount = count,maxColorError = .02f,maxNormalAngle = 15 };
            var result = LodBudgetTriangleSimplifier.Simplify(source,settings,options,out var diagnostics,out _,null); Track(result.simplifiedMesh);
            Assert.That(result.ok,Is.True,result.error); Assert.That(result.simplifiedTriCount,Is.InRange(1,43));
            Assert.That(diagnostics.evaluatedCandidates,Is.EqualTo(count)); Assert.That(diagnostics.budgetCandidates,Has.Count.EqualTo(count));
            Assert.That(diagnostics.budgetCandidates.Select(c => c.name),Is.EqualTo(new[] {"balanced","shape","normals","RGBA","UV"}.Take(count)));
            var eligible = diagnostics.budgetCandidates.Where(c => c.triangles <= 43 && c.triangles >= 40).ToArray();
            Assert.That(eligible,Is.Not.Empty);
            Assert.That(diagnostics.selectionScore,Is.EqualTo(eligible.Min(c => c.score)).Within(1e-5));
            CollectionAssert.AreEqual(original,source.vertices); CollectionAssert.AreEqual(faces,source.GetIndices(0));
        }

        [Test]
        public void CancellationBetweenVariants_DestroysRetainedCandidate()
        {
            var source = Grid(4); source.name = "CancelledQuality_"+System.Guid.NewGuid().ToString("N");
            UvProgress.Begin("Quality cancellation regression",cancelable:true);
            try
            {
                Assert.Throws<System.OperationCanceledException>(() => LodBudgetTriangleSimplifier.Simplify(source,
                    new MeshSimplifier.SimplifySettings { targetRatio = .5f,targetError = .2f,uvChannel = 1 },
                    new LodPipelineOps.Options { candidateCount = 3 },out _,out _,
                    () => UvProgress.Current.detail?.Contains("quality variant 2/3") == true));
                var remaining = Resources.FindObjectsOfTypeAll<Mesh>().Where(m => m.name == source.name).ToArray();
                Assert.That(remaining,Is.EqualTo(new[] {source}),"The retained first candidate must be destroyed when the second variant is cancelled.");
            }
            finally { UvProgress.End(); }
        }
    }
}
