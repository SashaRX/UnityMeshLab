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
        MeshSimplifier.SimplifyResult ReduceNative(Mesh source,float ratio = 1f/9)
        {
            var result = MeshSimplifier.Simplify(source,new MeshSimplifier.SimplifySettings {
                targetRatio = ratio,targetError = 1,uvChannel = 1,preserveHardEdges = true,
                nativeHardEdgeConstraints = true,allowAttributeSeamCollapse = true });
            if (result.simplifiedMesh) Track(result.simplifiedMesh);
            Assert.That(result.ok,Is.True,result.error); return result;
        }
        [Test] public void NativeConstraintsReleaseIncidentBeltAndPreserveBothShadingSides()
        {
            var source = Fold(true,true); var original = source.vertices; var indices = source.triangles;
            var strict = Reduce(source,true); var result = ReduceNative(source);
            Assert.That(result.hardEdges.Valid,Is.True); Assert.That(result.hardEdges.nativeConstraints,Is.True);
            Assert.That(result.hardEdges.sourceFallback,Is.False);
            Assert.That(result.simplifiedTriCount,Is.LessThan(LodMeshData.TriangleCount(strict)));
            Assert.That(new LodHardEdges(source).MeasureNative(result.simplifiedMesh).Valid,Is.True);
            Assert.That(new LodHardEdges(source).Measure(result.simplifiedMesh).missingFaces,Is.GreaterThan(0),"Control: neighboring faces are now free to retriangulate.");
            CollectionAssert.AreEqual(original,source.vertices); CollectionAssert.AreEqual(indices,source.triangles);
        }
        [Test] public void NativeCurvedCreaseRetriesWithLockedChainInsteadOfLosingCoverage()
        {
            var source = Fold(true); var p = source.vertices;
            for (int i = 0; i < p.Length; i++) p[i].z += p[i].y*p[i].y*.1f;
            source.vertices = p;
            var result = ReduceNative(source);
            Assert.That(result.hardEdges.Valid,Is.True); Assert.That(result.hardEdges.sourceFallback,Is.False);
            Assert.That(new LodHardEdges(source).MeasureNative(result.simplifiedMesh).Valid,Is.True);
            Assert.That(result.hardEdges.lockedChainRetry,Is.True);
        }
        [Test] public void NativeFlagsLockEndpointsMaterialBordersAndTagUnusedVerticesSafely()
        {
            var source = Fold(true,true,true); source.vertices = source.vertices.Concat(new[] { Vector3.one*10 }).ToArray();
            source.normals = source.normals.Concat(new[] { Vector3.up }).ToArray();
            var flags = new LodHardEdges(source).VertexLocks(false); var positions = source.vertices;
            Assert.That(flags.Last(),Is.Zero);
            for (int i = 0; i < positions.Length-1; i++)
                if (positions[i].x == 0 && positions[i].z == 0) Assert.That(flags[i],Is.EqualTo(MeshoptNative.VertexLock));
            var result = ReduceNative(source);
            Assert.That(result.hardEdges.Valid,Is.True); Assert.That(result.hardEdges.sourceFallback,Is.False);
            Assert.That(result.simplifiedMesh.subMeshCount,Is.EqualTo(2));
        }
        [Test] public void NativeAmbiguousFaceOccurrencesStayProtected()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.up,Vector3.forward },
                normals = new[] { Vector3.forward,Vector3.forward,Vector3.forward,Vector3.right,Vector3.right,Vector3.right },
                triangles = new[] { 0,1,2,3,4,5,0,1,2 } });
            var result = ReduceNative(source);
            Assert.That(result.hardEdges.Valid,Is.True); Assert.That(result.hardEdges.protectedTriangles,Is.EqualTo(3));
            var target = Track(Object.Instantiate(source)); target.triangles = target.triangles.Take(6).ToArray();
            Assert.That(new LodHardEdges(source).MeasureNative(target).missingFaces,Is.EqualTo(1));
        }
        [Test] public void NativeCoverageRejectsSmoothedCreaseAfterCorrection()
        {
            var source = Fold(true); var result = ReduceNative(source);
            result.simplifiedMesh.normals = Enumerable.Repeat(new Vector3(1,0,1).normalized,result.simplifiedMesh.vertexCount).ToArray();
            Assert.That(new LodHardEdges(source).MeasureNative(result.simplifiedMesh).missingEdges,Is.GreaterThan(0));
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
        [Test] public void StraightCreaseCoarseningPreservesBothSidesAndSource()
        {
            var source = Fold(true,true,true); var original = source.vertices; var indices = source.GetIndices(0);
            var colors = original.Select(p => new Color(p.y,2*p.y,-p.y,.3f+p.y*.1f)).ToArray(); source.colors = colors;
            var result = LodFeatureChains.Coarsen(source); if (result.mesh) Track(result.mesh);
            Assert.That(result.removedPoints,Is.GreaterThan(0),result.Note); Assert.That(result.mesh,Is.Not.Null);
            var report = new LodHardEdges(source).MeasureCoarsened(result.mesh,new LodHardEdges(result.mesh).Measure(result.mesh));
            Assert.That(report.Valid,Is.True); Assert.That(new LodHardEdges(source).Measure(result.mesh).missingEdges,Is.GreaterThan(0));
            Assert.That(LodMeshData.TriangleCount(result.mesh),Is.EqualTo(LodMeshData.TriangleCount(source)-result.removedTriangles));
            Assert.That(result.mesh.subMeshCount,Is.EqualTo(2)); CollectionAssert.AreEqual(original,source.vertices);
            CollectionAssert.AreEqual(indices,source.GetIndices(0)); CollectionAssert.AreEqual(colors,source.colors);
        }
        [Test] public void NonlinearRgbaFieldBlocksStraightCreaseCollapse()
        {
            var source = Fold(true); source.colors = source.vertices.Select(p => new Color(p.y*p.y,0,0,1)).ToArray();
            var result = LodFeatureChains.Coarsen(source); if (result.mesh) Track(result.mesh);
            Assert.That(result.removedPoints,Is.Zero); Assert.That(result.mesh,Is.Null);
        }
        [Test] public void CornerContactFansDoNotBlockIndependentCreaseChains()
        {
            var first = Fold(true); var second = Fold(true);
            var source = Track(new Mesh { vertices = first.vertices.Concat(second.vertices.Select(p => p+new Vector3(1,1,0))).ToArray(),
                normals = first.normals.Concat(second.normals).ToArray(),
                triangles = first.triangles.Concat(second.triangles.Select(i => i+first.vertexCount)).ToArray() });
            var result = LodFeatureChains.Coarsen(source); if (result.mesh) Track(result.mesh);
            Assert.That(result.separatedContacts,Is.GreaterThan(0)); Assert.That(result.refusedComponents,Is.Zero,result.Note);
            Assert.That(result.removedPoints,Is.EqualTo(14));
            Assert.That(new LodHardEdges(source).MeasureCoarsened(result.mesh,new LodHardEdges(result.mesh).Measure(result.mesh)).Valid,Is.True);
            Assert.That(result.mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0),Is.False);
        }
        [Test] public void DegenerateIslandDoesNotBlockValidComponentOrDisappear()
        {
            var fold = Fold(true); int offset = fold.vertexCount;
            var source = Track(new Mesh { vertices = fold.vertices.Concat(new[] { Vector3.one*10,Vector3.one*11 }).ToArray(),
                normals = fold.normals.Concat(new[] { Vector3.forward,Vector3.forward }).ToArray(),
                triangles = fold.triangles.Concat(new[] { offset,offset+1,offset }).ToArray() });
            var result = LodFeatureChains.Coarsen(source); if (result.mesh) Track(result.mesh);
            Assert.That(result.removedPoints,Is.EqualTo(7)); Assert.That(result.refusedComponents,Is.GreaterThan(0));
            var p = result.mesh.vertices; var indices = result.mesh.triangles;
            Assert.That(Enumerable.Range(0,indices.Length/3).Count(i => p[indices[i*3]].Equals(p[indices[i*3+2]])),Is.EqualTo(1));
        }
        [Test] public void MaterialJunctionOnCreaseRetainsItsNode()
        {
            var source = Fold(true); var original = source.triangles; var p = source.vertices; var n = source.normals;
            var slots = new[] { new List<int>(),new List<int>(),new List<int>() };
            for (int i = 0; i < original.Length; i += 3)
            {
                int slot = n[original[i]].Equals(Vector3.forward) ? 0 : (p[original[i]].y+p[original[i+1]].y+p[original[i+2]].y)/3 < .5f ? 1 : 2;
                slots[slot].AddRange(original.Skip(i).Take(3));
            }
            source.subMeshCount = 3; for (int i = 0; i < 3; i++) source.SetTriangles(slots[i],i);
            var result = LodFeatureChains.Coarsen(source); if (result.mesh) Track(result.mesh);
            Assert.That(result.removedPoints,Is.GreaterThan(0)); Assert.That(result.mesh.vertices.Contains(new Vector3(0,.5f,0)),Is.True);
            Assert.That(new LodHardEdges(source).MeasureCoarsened(result.mesh,result.Configure(new LodHardEdges(result.mesh).Measure(result.mesh))).Valid,Is.True);
        }
        [Test] public void BentCreaseIsNotTreatedAsStraight()
        {
            var source = Fold(true); var p = source.vertices;
            for (int i = 0; i < p.Length; i++) p[i].z += p[i].y*p[i].y*.1f;
            source.vertices = p;
            var result = LodFeatureChains.Coarsen(source); if (result.mesh) Track(result.mesh);
            Assert.That(result.removedPoints,Is.Zero);
        }
        [Test] public void FeatureCoarseningCancellationLeavesSourceIntact()
        {
            var source = Fold(true); var p = source.vertices; var indices = source.triangles;
            Assert.Throws<System.OperationCanceledException>(() => LodFeatureChains.Coarsen(source,() => true));
            CollectionAssert.AreEqual(p,source.vertices); CollectionAssert.AreEqual(indices,source.triangles);
        }
        [Test] public void BoundedCoarseningTracksOriginalCurvedPolyline()
        {
            var p = new List<Vector3>(); var n = new List<Vector3>(); var triangles = new List<int>();
            for (int side = 0; side < 2; side++)
            {
                int offset = p.Count;
                for (int i = 0; i <= 8; i++) { float y = i/8f; p.Add(new Vector3(0,y,y*y*.1f)); n.Add(side == 0 ? Vector3.forward : Vector3.right); }
                p.Add(side == 0 ? new Vector3(1,.5f,0) : new Vector3(0,.5f,1)); n.Add(side == 0 ? Vector3.forward : Vector3.right);
                for (int i = 0; i < 8; i++) triangles.AddRange(side == 0 ? new[] { offset+i,offset+9,offset+i+1 } : new[] { offset+i,offset+i+1,offset+9 });
            }
            var source = Track(new Mesh()); source.SetVertices(p); source.SetNormals(n); source.SetTriangles(triangles,0);
            var settings = new LodFeatureChains.Settings { relativeDeviation = .005f,normalAngle = 5,uvError = .001f };
            var result = LodFeatureChains.Coarsen(source,settings:settings); if (result.mesh) Track(result.mesh);
            Assert.That(result.removedPoints,Is.GreaterThan(0),result.Note);
            var report = new LodHardEdges(source).MeasureCoarsened(result.mesh,result.Configure(new LodHardEdges(result.mesh).Measure(result.mesh)));
            Assert.That(report.Valid,Is.True,result.Note); Assert.That(new LodHardEdges(source).Measure(result.mesh).missingEdges,Is.GreaterThan(0));
            Assert.That(result.removedPoints,Is.LessThan(7),"A long chord exceeding the original curve tolerance must be rejected.");
        }
        [Test] public void CoarsenedCoverageDoesNotAcceptSmoothedAwayCrease()
        {
            var source = Fold(true); var result = LodFeatureChains.Coarsen(source); Track(result.mesh);
            var reference = result.Configure(new LodHardEdges(result.mesh).Measure(result.mesh)); reference.featureNormalAngle = 180;
            result.mesh.normals = Enumerable.Repeat(new Vector3(1,0,1).normalized,result.mesh.vertexCount).ToArray();
            Assert.That(new LodHardEdges(source).MeasureCoarsened(result.mesh,reference).missingEdges,Is.GreaterThan(0));
        }
        [Test] public void CoarsenedCoverageRejectsLostSideAndChangedNormal()
        {
            var source = Fold(true,true,true); var result = LodFeatureChains.Coarsen(source); if (result.mesh) Track(result.mesh);
            Assert.That(result.mesh,Is.Not.Null); var reference = new LodHardEdges(result.mesh);
            var normals = result.mesh.normals;
            for (int i = 0; i < normals.Length; i++) if (normals[i].Equals(Vector3.forward)) normals[i] = Vector3.up;
            result.mesh.normals = normals;
            Assert.That(new LodHardEdges(source).MeasureCoarsened(result.mesh,reference.Measure(result.mesh)).missingEdges,Is.GreaterThan(0));
        }
        [Test] public void BudgetCandidatesAndCorrectionUseOriginalSourceAfterCoarsening()
        {
            var source = Fold(true,true,true);
            var settings = new MeshSimplifier.SimplifySettings { targetRatio = .1f,targetError = .2f,normalWeight = 1,colorWeight = 1,uvChannel = 1,preserveHardEdges = true };
            var options = new LodPipelineOps.Options { coarsenHardEdgeChains = true,correctSurfaceAttributes = true,candidateCount = 3,
                prioritizeTriangleBudget = true,maxNormalAngle = 15,maxColorError = .02f };
            var result = LodBudgetTriangleSimplifier.Simplify(source,settings,options,out var diagnostics,out string note,null);
            if (result.simplifiedMesh) Track(result.simplifiedMesh);
            Assert.That(result.ok,Is.True,result.error); Assert.That(result.hardEdges.Valid,Is.True,note);
            Assert.That(result.hardEdges.coarsenedPoints,Is.GreaterThan(0)); Assert.That(result.originalTriCount,Is.EqualTo(LodMeshData.TriangleCount(source)));
            Assert.That(diagnostics.metrics.distance,Is.LessThan(1e-5f));
            Assert.That(new LodHardEdges(source).MeasureCoarsened(result.simplifiedMesh,result.hardEdges).Valid,Is.True);
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
