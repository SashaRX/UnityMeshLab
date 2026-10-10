using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodHardEdgeTests
    {
        readonly List<Mesh> owned = new List<Mesh>();
        [TearDown] public void Cleanup() { foreach (var mesh in owned) if (mesh) Object.DestroyImmediate(mesh); owned.Clear(); }
        Mesh Track(Mesh mesh) { owned.Add(mesh); return mesh; }
        Mesh Fold(bool crease,bool uvSplit = false,bool materialSplit = false)
        {
            // Two tessellated perpendicular panels. Render duplicates on the join
            // distinguish a hard normal seam from a smooth UV-only seam.
            const int n = 8;
            var positions = new List<Vector3>(); var normals = new List<Vector3>();
            var uv = new List<Vector2>(); var slots = new[] { new List<int>(),new List<int>() };
            for (int side = 0; side < 2; side++)
            {
                int offset = positions.Count;
                for (int y = 0; y <= n; y++) for (int x = 0; x <= n; x++)
                {
                    positions.Add(side == 0 ? new Vector3(x/(float)n,y/(float)n,0) : new Vector3(0,y/(float)n,x/(float)n));
                    normals.Add(crease ? (side == 0 ? Vector3.forward : Vector3.right) : new Vector3(1,0,1).normalized);
                    uv.Add(new Vector2(x/(float)n+(uvSplit ? side : 0),y/(float)n));
                }
                var triangles = slots[materialSplit ? side : 0];
                for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    int a = offset+y*(n+1)+x, b = a+1, c = a+n+1, d = c+1;
                    triangles.AddRange(side == 0 ? new[] { a,b,c,b,d,c } : new[] { a,c,b,b,c,d });
                }
            }
            var mesh = Track(new Mesh()); mesh.SetVertices(positions); mesh.SetNormals(normals); mesh.SetUVs(0,uv);
            mesh.subMeshCount = materialSplit ? 2 : 1;
            for (int slot = 0; slot < mesh.subMeshCount; slot++) mesh.SetTriangles(slots[slot],slot);
            mesh.RecalculateBounds(); return mesh;
        }
        Mesh Reduce(Mesh source,bool preserve,float ratio = 1f/9)
        {
            var result = MeshSimplifier.Simplify(source,new MeshSimplifier.SimplifySettings {
                targetRatio = ratio,targetError = 1,uvChannel = 1,preserveHardEdges = preserve,allowAttributeSeamCollapse = true });
            Assert.That(result.ok,Is.True,result.error); return Track(result.simplifiedMesh);
        }
        [Test] public void AuthoredCreaseSurvivesPermissiveZeroWeightBudgetProbe()
        {
            var source = Fold(true,true); var original = source.vertices; var indices = source.triangles;
            var features = new LodHardEdges(source); Assert.That(features.Measure(source).edges,Is.EqualTo(8));
            var result = Reduce(source,true); var report = features.Measure(result);
            Assert.That(report.Valid,Is.True); Assert.That(report.interfaces,Is.GreaterThan(0));
            Assert.That(LodMeshData.TriangleCount(result),Is.LessThan(LodMeshData.TriangleCount(source)));
            Assert.That(features.Measure(Reduce(source,false)).missingEdges,Is.GreaterThan(0),"Positive control must erase an authored crease segment.");
            CollectionAssert.AreEqual(original,source.vertices); CollectionAssert.AreEqual(indices,source.triangles);
        }
        [Test] public void SmoothUvSplitDoesNotCreateProtectedBelt()
        {
            var source = Fold(false,true); var features = new LodHardEdges(source);
            Assert.That(features.protectedTriangles,Is.Zero); Assert.That(features.Measure(source).edges,Is.Zero);
            var ordinary = Reduce(source,false); var protectedMesh = Reduce(source,true);
            CollectionAssert.AreEqual(ordinary.vertices,protectedMesh.vertices); CollectionAssert.AreEqual(ordinary.triangles,protectedMesh.triangles);
        }
        [Test] public void CreaseAcrossMaterialSlotsRetainsBothSides()
        {
            var source = Fold(true,true,true); var reduced = Reduce(source,true);
            Assert.That(reduced.subMeshCount,Is.EqualTo(2)); Assert.That(new LodHardEdges(source).Measure(reduced).Valid,Is.True);
            Assert.That(reduced.GetIndexCount(0),Is.GreaterThan(0)); Assert.That(reduced.GetIndexCount(1),Is.GreaterThan(0));
        }
        [Test] public void FullHardBeltReportsMissedBudgetInsteadOfDeletingFeatures()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.up,Vector3.forward },
                normals = new[] { Vector3.forward,Vector3.forward,Vector3.forward,Vector3.right,Vector3.right,Vector3.right }, triangles = new[] { 0,1,2,3,4,5 } });
            var reduced = Reduce(source,true,.1f); var report = new LodHardEdges(source).Measure(reduced);
            Assert.That(report.Valid,Is.True); Assert.That(report.protectedTriangles,Is.EqualTo(2));
            Assert.That(LodMeshData.TriangleCount(reduced),Is.EqualTo(2));
        }
        [Test] public void CornerContactDoesNotInventHardAdjacency()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.left,Vector3.back },
                normals = new[] { Vector3.forward,Vector3.forward,Vector3.forward,Vector3.up,Vector3.up,Vector3.up }, triangles = new[] { 0,1,2,3,4,5 } });
            Assert.That(new LodHardEdges(source).protectedTriangles,Is.Zero);
        }
        [Test] public void AmbiguousNonManifoldEdgeIsConservativelyProtected()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,Vector3.down,Vector3.forward },
                normals = Enumerable.Repeat(Vector3.up,5).ToArray(), triangles = new[] { 0,1,2,1,0,3,0,1,4 } });
            var features = new LodHardEdges(source); Assert.That(features.ambiguousEdges,Is.EqualTo(1));
            Assert.That(features.Measure(Reduce(source,true)).Valid,Is.True);
        }
        [Test] public void ValidationDetectsLostFeatureAndChangedAuthoredNormal()
        {
            var source = Fold(true); var features = new LodHardEdges(source); var target = Track(Object.Instantiate(source));
            var normals = target.normals; normals[0] = Vector3.up; target.normals = normals;
            Assert.That(features.Measure(target).missingEdges,Is.GreaterThan(0));
            target.normals = source.normals; target.triangles = target.triangles.Skip(3).ToArray();
            Assert.That(features.Measure(target).missingFaces,Is.GreaterThan(0));
        }
        [Test] public void CoincidentProtectedTriangleCannotHideDeletedDuplicate()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.up,Vector3.forward },
                normals = new[] { Vector3.forward,Vector3.forward,Vector3.forward,Vector3.right,Vector3.right,Vector3.right },
                triangles = new[] { 0,1,2,3,4,5,0,1,2 } });
            var features = new LodHardEdges(source); Assert.That(features.Measure(source).Valid,Is.True);
            var target = Track(Object.Instantiate(source)); target.triangles = target.triangles.Take(6).ToArray();
            Assert.That(features.Measure(target).missingFaces,Is.EqualTo(1));
            Assert.That(features.Measure(target).Valid,Is.False);
        }
        [Test] public void UnreachableProtectedFloorRetainsCostsInsteadOfAggressiveBudgetRetries()
        {
            var source = Fold(true,true);
            var settings = new MeshSimplifier.SimplifySettings { targetRatio = .01f,targetError = .2f,normalWeight = 1,colorWeight = 1,uvChannel = 1,preserveHardEdges = true };
            var options = new LodPipelineOps.Options { candidateCount = 3,prioritizeTriangleBudget = true,maxNormalAngle = 15,maxColorError = .02f };
            var result = LodBudgetTriangleSimplifier.Simplify(source,settings,options,out var diagnostics,out string note,null);
            Track(result.simplifiedMesh); Assert.That(result.ok,Is.True,result.error);
            Assert.That(diagnostics.nativeProbes,Is.EqualTo(3)); Assert.That(diagnostics.evaluatedCandidates,Is.EqualTo(3));
            Assert.That(note,Does.Contain("no error/weight relaxation"));
            Assert.That(new LodHardEdges(source).Measure(result.simplifiedMesh).Valid,Is.True);
        }
        [Test] public void KnownUnreachableBudgetRanksQualityButNeverBeatsReachedBudget()
        {
            Assert.That(LodBudgetTriangleSimplifier.Better(120,1,110,3,100,true),Is.True);
            Assert.That(LodBudgetTriangleSimplifier.Better(120,0,90,3,100,true),Is.False);
            Assert.That(LodBudgetTriangleSimplifier.Better(90,3,120,0,100,true),Is.True);
            Assert.That(LodBudgetTriangleSimplifier.Better(120,1,110,3,100),Is.False,"Legacy over-budget selection remains count-first.");
        }
        [Test] public void LaterProtectedDensityUsesSourceDerivedCopyWhenAllNewStrategiesAreDenser()
        {
            var source = Fold(true,true);
            source.uv2 = source.uv.Select(v => new Vector2(v.x*v.x,v.y*v.y)).ToArray();
            var prior = Reduce(source,true);
            var original = prior.vertices; var indices = prior.triangles;
            var settings = new MeshSimplifier.SimplifySettings { targetRatio = .01f,targetError = .001f,normalWeight = 1,uv2Weight = 500,uvChannel = 1,preserveHardEdges = true };
            var options = new LodPipelineOps.Options { candidateCount = 3,prioritizeTriangleBudget = true,maxNormalAngle = 15,maxColorError = .02f };
            var result = LodBudgetTriangleSimplifier.Simplify(source,settings,options,out var diagnostics,out string note,null,prior);
            Track(result.simplifiedMesh); Assert.That(result.ok,Is.True,result.error);
            Assert.That(diagnostics.selectedCandidate,Is.EqualTo(4)); Assert.That(diagnostics.nativeProbes,Is.EqualTo(3));
            Assert.That(result.simplifiedTriCount,Is.EqualTo(LodMeshData.TriangleCount(prior)));
            Assert.That(note,Does.Contain("No recursive collapse"));
            CollectionAssert.AreEqual(original,result.simplifiedMesh.vertices); CollectionAssert.AreEqual(indices,result.simplifiedMesh.triangles);
            CollectionAssert.AreEqual(original,prior.vertices); CollectionAssert.AreEqual(indices,prior.triangles);
            Assert.That(LodBudgetTriangleSimplifier.Better(294,1,284,3,63,true,284),Is.False);
        }
    }
}
