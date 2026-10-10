using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshLocalCapTests
    {
        static readonly Vector3[] Box = {
            new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
            new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
        static readonly int[] Faces = { 0,2,1,0,3,2, 4,5,6,4,6,7, 0,1,5,0,5,4, 3,7,6,3,6,2, 0,4,7,0,7,3, 1,2,6,1,6,5 };
        static int[] Missing(params int[] missing) => Faces.Where((v,k) => !missing.Contains(k / 6)).ToArray();

        [TestCase(false)] [TestCase(true)]
        public void FrozenGarbageChuteLongClosesAllContoursAndSurvivesNativeTrimSimplifyAndUnwrap(bool solve)
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_LONG_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_LONG_SOURCE to the decoded Garbage_Chute_Long source.bin.");
            using var reader = new BinaryReader(File.OpenRead(path));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            var p = new Vector3[vertices]; var ix = new int[count];
            for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            for (int i = 0; i < count; ++i) ix[i] = reader.ReadInt32();
            var saved = (int[])ix.Clone(); var savedPoints = (Vector3[])p.Clone();
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, true, planeTolerance: 1e-5,
                continueOnRefusal: true, elementScopedContacts: true);
            TestContext.WriteLine("Garbage_Chute_Long: " + cap.Description);
            foreach (var failure in cap.loopFailures) TestContext.WriteLine($"Loop {failure.Key}: {failure.Value}");
            Assert.AreEqual(5, cap.loops); Assert.IsEmpty(cap.loopFailures);
            Assert.AreEqual(0, cap.remainingBoundaryEdges);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions, cap.indices, default).All(v => v));
            for (int i = 0; i < ix.Length; ++i) Assert.AreEqual(p[ix[i]], cap.positions[cap.indices[i]]);
            CollectionAssert.AreEqual(saved, ix); CollectionAssert.AreEqual(savedPoints, p);
            var settings = new RemeshSettings {voxelResolution = 64, solve = solve, maximumError = .02f,
                pruneSmallParts = true, normalCrease = 133, normalSmoothing = 3,
                normalWeighting = RemeshNormalWeighting.FaceAreaAndCornerAngle,
                textureResolution = 512, padding = 3, mergeCharts = true};
            var voxel = RemeshNative.VoxelizeCaptured(cap.positions, cap.indices, settings, default,
                "Garbage_Chute_Long replay", p, ix, cap);
            var topology = RemeshTopology.Inspect(voxel.positions, voxel.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            TestContext.WriteLine($"Garbage_Chute_Long r64 solve={solve}: {voxel.TriangleCount} triangles; {topology.Description}");
            var low = p[0]; var high = p[0];
            foreach (var point in p) {low = Vector3.Min(low, point); high = Vector3.Max(high, point);}
            var extent = high - low;
            float cell = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) / settings.voxelResolution;
            var trimmed = RemeshTrim.Trim(voxel, cap.positions, cap.indices, cell * 2f, default);
            Assert.AreEqual(0, trimmed.removed);
            var simplified = solve ? RemeshSurfaceRefine.Simplify(trimmed.mesh, cap.positions, cap.indices, settings, default, out _)
                : RemeshNative.Simplify(trimmed.mesh, settings, default, out _);
            var after = RemeshTopology.Inspect(simplified.positions, simplified.indices);
            Assert.IsTrue(after.Valid, after.Description); Assert.AreEqual(0, after.boundary.Count);
            var uv = RemeshNative.Unwrap(simplified, settings, default);
            var quality = UvChartQuality.Measure(uv, default);
            var atlas = UvAtlasDiagnostics.Measure(uv, default);
            Assert.IsTrue(quality.valid); Assert.IsTrue(atlas.complete);
            Assert.AreEqual(0, atlas.pairs); Assert.AreEqual(0, atlas.degenerateFaces);
            for (int i = 0; i < uv.indices.Length; ++i) Assert.AreEqual(simplified.positions[simplified.indices[i]], uv.positions[uv.indices[i]]);
            CollectionAssert.AreEqual(saved, ix); CollectionAssert.AreEqual(savedPoints, p);
            TestContext.WriteLine($"Garbage_Chute_Long solve={solve}: simplified {simplified.TriangleCount}, charts {uv.chartCount}; " +
                $"UV stretch mean {quality.meanStretch:G6}, max {quality.maxStretch:G6}; overlaps {atlas.pairs}, degenerate {atlas.degenerateFaces}");
        }

        [TestCase(false, 1f)] [TestCase(true, 1f)] [TestCase(false, .125f)] [TestCase(false, 8f)]
        public void ThreeMissingFacesWithParallelPlanesCloseWithoutAnInferredCorner(bool reverse, float scale)
        {
            var p = Box.Select(v => Quaternion.Euler(27,39,13) * v * scale + new Vector3(3,-2,1)).ToArray();
            var ix = Missing(0,1,2);
            if (reverse) ix = Enumerable.Range(0, ix.Length / 3).SelectMany(f => ix.Skip(f * 3).Take(3).Reverse()).ToArray();
            var original = (int[])ix.Clone();
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, true, elementScopedContacts: true);
            Assert.AreEqual(6, cap.addedFaces); Assert.AreEqual(3, cap.localPatches);
            var topology = RemeshTopology.Inspect(cap.positions, cap.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            CollectionAssert.AreEqual(new[] {2}, topology.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions, cap.indices, default).All(v => v));
            CollectionAssert.AreEqual(p, cap.positions); CollectionAssert.AreEqual(original, ix);
            CollectionAssert.AreEqual(ix, cap.indices.Take(ix.Length));
        }

        [Test]
        public void ParallelPlaneArcCandidatesStillRefuseIntersectingSourceAndCancelAtomically()
        {
            var p = Box.Concat(new[] {new Vector3(0,-2,0),new Vector3(0,0,0),new Vector3(.4f,-1,.4f)}).ToArray();
            var ix = Missing(0,1,2).Concat(new[] {8,9,10}).ToArray();
            var saved = (int[])ix.Clone(); var savedPoints = (Vector3[])p.Clone();
            var support = RemeshPlanarCap.Prepare(p, ix, "0", default, true, continueOnRefusal: true);
            Assert.AreEqual(0, support.addedFaces); Assert.IsNotEmpty(support.loopFailures);
            CollectionAssert.AreEqual(saved, support.indices); CollectionAssert.AreEqual(savedPoints, support.positions);
            CollectionAssert.AreEqual(saved, ix); CollectionAssert.AreEqual(savedPoints, p);
            int trials = 0;
            Assert.Throws<OperationCanceledException>(() => RemeshArcCap.Generate(p, ix, new List<int> {0,1,5,4,7,3},
                new CancellationToken(true), ref trials, 0, null));
        }

        [TestCase(false)] [TestCase(true)]
        public void FrozenFairStallStepsKeepsStrictToleranceAndReplaysExplicitLargerTolerance(bool solve)
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_STEPS_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_STEPS_SOURCE to the decoded FairStall_Steps source.bin.");
            using var reader = new BinaryReader(File.OpenRead(path));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            var p = new Vector3[vertices]; var ix = new int[count];
            for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            for (int i = 0; i < count; ++i) ix[i] = reader.ReadInt32();
            var saved = (int[])ix.Clone(); var savedPoints = (Vector3[])p.Clone();
            var strict = RemeshPlanarCap.Prepare(p, ix, "all", default, true, planeTolerance: 1e-5,
                continueOnRefusal: true, elementScopedContacts: true);
            Assert.AreEqual(0, strict.addedFaces); Assert.AreEqual(12, strict.remainingBoundaryEdges);
            StringAssert.Contains("configured tolerance", strict.loopFailures[0]);
            var analysis = RemeshCapPlanes.Analyze(strict.positions, strict.boundaryLoops[0].ToList(), default, minimumTolerance: .002);
            Assert.AreEqual(RemeshCapPlanes.Kind.TwoPlanes, analysis.kind);
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, true, planeTolerance: .002,
                continueOnRefusal: true, elementScopedContacts: true);
            TestContext.WriteLine(cap.Description);
            foreach (var failure in cap.loopFailures) TestContext.WriteLine(failure.Value);
            Assert.IsEmpty(cap.loopFailures); Assert.AreEqual(10, cap.addedFaces);
            Assert.AreEqual(2, cap.localPatches); Assert.AreEqual(0, cap.remainingBoundaryEdges);
            Assert.AreEqual(62, cap.positions.Length);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions, cap.indices, default).All(v => v));
            for (int i = 0; i < ix.Length; ++i) Assert.AreEqual(p[ix[i]], cap.positions[cap.indices[i]]);
            CollectionAssert.AreEqual(saved, ix); CollectionAssert.AreEqual(savedPoints, p);
            var voxel = RemeshNative.Voxelize(cap.positions, cap.indices, new RemeshSettings {voxelResolution = 64, solve = solve}, default);
            var topology = RemeshTopology.Inspect(voxel.positions, voxel.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            TestContext.WriteLine($"FairStall_Steps r64 solve={solve}: {voxel.TriangleCount} triangles");
        }

        [TestCase(0,2)] [TestCase(0,3)] [TestCase(0,4)] [TestCase(0,5)]
        [TestCase(1,2)] [TestCase(1,3)] [TestCase(1,4)] [TestCase(1,5)]
        [TestCase(2,4)] [TestCase(2,5)] [TestCase(3,4)] [TestCase(3,5)]
        public void AdjacentMissingFacesCloseOnTwoReferencePlanesWithOneSharedEdge(int first, int second)
        {
            var ix = Missing(first, second); var saved = (int[])ix.Clone();
            var support = RemeshPlanarCap.Prepare(Box, ix, "0", default, true);
            Assert.AreEqual(4, support.addedFaces); Assert.AreEqual(2, support.localPatches);
            Assert.AreEqual(2, support.planeRechecks);
            var topology = RemeshTopology.Inspect(support.positions, support.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count);
            CollectionAssert.AreEqual(new[] {2}, topology.euler);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(support.positions, support.indices, default).All(v => v));
            var expected = new[] {first, second}.Select(f => new HashSet<Vector3>(Faces.Skip(f*6).Take(6).Select(v => Box[v]))).ToArray();
            float area = 0;
            for (int f = support.originalFaces; f < support.indices.Length/3; ++f) {
                var triangle = support.indices.Skip(f*3).Take(3).Select(v => support.positions[v]).ToArray();
                Assert.IsTrue(expected.Any(plane => triangle.All(plane.Contains)), "Cap spans across the two missing planes.");
                area += Vector3.Cross(triangle[1]-triangle[0],triangle[2]-triangle[0]).magnitude*.5f;
            }
            Assert.AreEqual(8, area, 1e-6f);
            CollectionAssert.AreEqual(saved, ix); CollectionAssert.AreEqual(Box, support.positions);
            CollectionAssert.AreEqual(ix, support.indices.Take(ix.Length));
        }

        [TestCase(false, .125f)] [TestCase(true, .125f)] [TestCase(false, 8f)] [TestCase(true, 8f)]
        public void RotationsTranslationsScaleWindingAndFaceOrderPreserveLocalClosure(bool reverse, float scale)
        {
            var p = Box.Select(v => Quaternion.Euler(27,39,13)*v*scale + new Vector3(3,-2,1)).ToArray();
            var source = Missing(0,2); var faces = new List<int>();
            for (int f = source.Length/3-1; f >= 0; --f) {
                var triangle = source.Skip(f*3).Take(3).ToArray();
                faces.AddRange(reverse ? triangle.Reverse() : triangle);
            }
            var ix = faces.ToArray();
            var a = RemeshPlanarCap.Prepare(p,ix,"0",default,true);
            var b = RemeshPlanarCap.Prepare(p,ix,"0",default,true);
            Assert.AreEqual(4,a.addedFaces); Assert.AreEqual(2,a.planeRechecks);
            Assert.IsTrue(RemeshTopology.Inspect(a.positions,a.indices).Valid);
            Assert.AreEqual(0,RemeshTopology.Inspect(a.positions,a.indices).boundary.Count);
            CollectionAssert.AreEqual(a.positions,b.positions); CollectionAssert.AreEqual(a.indices,b.indices);
            CollectionAssert.AreEqual(ix,a.indices.Take(ix.Length));
        }

        [Test]
        public void UnevenRimSubdivisionPreservesOriginalEdgesAndTwoPlaneEvidence()
        {
            var p = Box.ToList(); var ix = Missing(0,2).ToList();
            for (int step = 0; step < 4; ++step) {
                var topology = RemeshTopology.Inspect(p.ToArray(),ix.ToArray());
                var pair = topology.edges.First(e => e.Value.count == 1); int f = pair.Value.firstFace;
                int corner = Enumerable.Range(0,3).First(k => {
                    int a = ix[f*3+k], b = ix[f*3+(k+1)%3];
                    return Math.Min(a,b) == pair.Key.Item1 && Math.Max(a,b) == pair.Key.Item2;
                });
                int x = ix[f*3+corner], y = ix[f*3+(corner+1)%3], z = ix[f*3+(corner+2)%3], m = p.Count;
                p.Add((p[x]*3+p[y])/4); ix.RemoveRange(f*3,3); ix.AddRange(new[] {x,m,z,m,y,z});
            }
            var points = p.ToArray(); var source = ix.ToArray();
            var result = RemeshPlanarCap.Prepare(points,source,"0",default,true);
            Assert.AreEqual(8,result.addedFaces); Assert.AreEqual(2,result.localPatches);
            Assert.IsTrue(RemeshTopology.Inspect(result.positions,result.indices).Valid);
            Assert.AreEqual(0,RemeshTopology.Inspect(result.positions,result.indices).boundary.Count);
            CollectionAssert.AreEqual(points,result.positions); CollectionAssert.AreEqual(source,result.indices.Take(source.Length));
        }

        [Test]
        public void AttributeSeamsWeldBeforeLocalCapWithoutChangingDonorCorners()
        {
            var p = Missing(0,2).Select(v => Box[v]).ToArray(); var ix = Enumerable.Range(0,p.Length).ToArray();
            var saved = (Vector3[])p.Clone(); var result = RemeshPlanarCap.Prepare(p,ix,"0",default,true);
            Assert.AreEqual(16,result.weldedVertices); Assert.AreEqual(4,result.addedFaces);
            CollectionAssert.AreEqual(saved,p);
            for (int i=0;i<ix.Length;++i) Assert.AreEqual(p[ix[i]],result.positions[result.indices[i]]);
        }

        [Test]
        public void ALocalCapPreservesAnUnselectedOpeningOnAnotherComponent()
        {
            var p = Box.Concat(Box.Select(v=>v+Vector3.right*5)).ToArray();
            var ix = Missing(0,2).Concat(Missing(0).Select(v=>v+8)).ToArray();
            var result = RemeshPlanarCap.Prepare(p,ix,"0",default,true);
            var topology = RemeshTopology.Inspect(result.positions,result.indices);
            Assert.IsTrue(topology.Valid); Assert.AreEqual(4,topology.boundary.Count);
            Assert.IsTrue(topology.boundary.All(edge=>edge.Item1.x >= 4 && edge.Item2.x >= 4));
            Assert.AreEqual(4,result.addedFaces);
        }

        [TestCase(0)] [TestCase(1)]
        public void ObstaclesAtEitherMissingPlaneRefuseTheWholePreparation(int obstacle)
        {
            var extra = obstacle == 0 ? new[] {new Vector3(0,-.5f,-2),new Vector3(0,-.5f,0),new Vector3(.5f,.5f,-1)} :
                new[] {new Vector3(0,-2,-.5f),new Vector3(0,0,-.5f),new Vector3(.5f,-1,.5f)};
            var p = Box.Concat(extra).ToArray(); var ix = Missing(0,2).Concat(new[] {8,9,10}).ToArray();
            var saved = (int[])ix.Clone(); var savedPoints = (Vector3[])p.Clone();
            var failure = Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(p,ix,"0",default,true));
            StringAssert.Contains("contacts face",failure.Message);
            CollectionAssert.AreEqual(saved,ix); CollectionAssert.AreEqual(savedPoints,p);
        }

        [TestCase(0, false)] [TestCase(1, false)] [TestCase(0, true)] [TestCase(1, true)]
        public void PartialPreparationRollsBackTheWholeFailedCompoundAndKeepsOtherCaps(int obstacle, bool goodFirst)
        {
            var extra = obstacle == 0 ? new[] {new Vector3(0,-.5f,-2),new Vector3(0,-.5f,0),new Vector3(.5f,.5f,-1)} :
                new[] {new Vector3(0,-2,-.5f),new Vector3(0,0,-.5f),new Vector3(.5f,-1,.5f)};
            var p = goodFirst ? Box.Concat(Box.Concat(extra).Select(v => v + Vector3.right * 4)).ToArray() :
                Box.Concat(extra).Concat(Box.Select(v => v + Vector3.right * 4)).ToArray();
            var ix = goodFirst ? Missing(0).Concat(Missing(0,2).Select(v => v + 8)).Concat(new[] {16,17,18}).ToArray() :
                Missing(0,2).Concat(new[] {8,9,10}).Concat(Missing(0).Select(v => v + 11)).ToArray();
            var saved = (int[])ix.Clone(); var savedPoints = (Vector3[])p.Clone();
            var progress = new List<int>();
            var result = RemeshPlanarCap.Prepare(p, ix, goodFirst ? "0,1" : "0,2", default, true,
                loopCompleted: (_, done, _) => progress.Add(done), continueOnRefusal: true);
            Assert.AreEqual(2, result.addedFaces); Assert.AreEqual(1, result.patchEnds.Count);
            Assert.AreEqual(1, result.localPatches); Assert.AreEqual(1, result.planeRechecks);
            Assert.AreEqual(1, result.loopFailures.Count);
            StringAssert.Contains("contacts face", result.loopFailures[goodFirst ? 1 : 0]);
            CollectionAssert.AreEqual(new[] {1,2}, progress);
            var topology = RemeshTopology.Inspect(result.positions, result.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(9, topology.boundary.Count);
            int goodOffset = goodFirst ? 0 : 11;
            Assert.IsTrue(result.indices.Skip(ix.Length).All(v => v >= goodOffset && v < goodOffset + 8));
            Assert.AreEqual(ix.Length / 3 + 2, result.patchEnds[0]);
            Assert.IsTrue(result.facePatches.Take(ix.Length / 3).All(id => id == 0));
            Assert.IsTrue(result.facePatches.Skip(ix.Length / 3).All(id => id == 1));
            CollectionAssert.AreEqual(saved, ix); CollectionAssert.AreEqual(savedPoints, p);
            CollectionAssert.AreEqual(savedPoints, result.positions);
            CollectionAssert.AreEqual(saved, result.indices.Take(ix.Length));
        }

        [Test]
        public void ThreeMissingFacesUseThreePlanesAndSmoothWarpedRimsRemainUnsupported()
        {
            var ix = Missing(0,2,4); var saved = (int[])ix.Clone();
            var cap = RemeshPlanarCap.Prepare(Box,ix,"0",default,true);
            Assert.AreEqual(6,cap.addedFaces); Assert.AreEqual(3,cap.localPatches);
            var closed = RemeshTopology.Inspect(cap.positions,cap.indices);
            Assert.IsTrue(closed.Valid,closed.Description); Assert.AreEqual(0,closed.boundary.Count);
            CollectionAssert.AreEqual(new[] {2},closed.euler);
            CollectionAssert.AreEqual(saved,ix);
            var p = Enumerable.Range(0,64).Select(i=> {
                float a=i*Mathf.PI/32; return new Vector3(Mathf.Cos(a),Mathf.Sin(a),.15f*Mathf.Sin(3*a));
            }).ToArray();
            Assert.AreEqual(RemeshCapPlanes.Kind.Unsupported,RemeshCapPlanes.Analyze(p,Enumerable.Range(0,64).ToList(),default).kind);
        }

        [Test]
        public void AmbiguousTwoPlaneSplitsRetainAllHypothesesAndRefuse()
        {
            // Four noncoplanar corners support two different pairs of triangular
            // disks. The contour alone cannot select either closure diagonal.
            var p = new[] {Vector3.zero,Vector3.right,Vector3.one,Vector3.up};
            var analysis = RemeshCapPlanes.Analyze(p,new List<int>{0,1,2,3},default);
            Assert.AreEqual(RemeshCapPlanes.Kind.Ambiguous,analysis.kind); Assert.AreEqual(2,analysis.hypotheses);
        }

        [Test]
        public void SearchBudgetCancellationAndNearCollinearSupportNeverAcceptPartialResults()
        {
            var loop=new List<int>{0,1,5,4,7,3};
            Assert.Throws<InvalidOperationException>(()=>RemeshCapPlanes.Analyze(Box,loop,default,maxFits:1));
            Assert.Throws<InvalidOperationException>(()=>RemeshCapPlanes.Analyze(Box,loop,default,maxSamples:6));
            Assert.Throws<OperationCanceledException>(()=>RemeshCapPlanes.Analyze(Box,loop,new CancellationToken(true)));
            var p=new[] {Vector3.zero,Vector3.right,new Vector3(2,1e-10f,0)};
            Assert.IsFalse(RemeshCapPlanes.TryPlane(p,new List<int>{0,1,2},1e-5,false,out _));
        }

        [Test]
        public void OptInAndSettingsRoundtripInvalidateOnlyTheEnabledAlgorithm()
        {
            Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(Box,Missing(0,2),"0",default));
            var a=new RemeshSettings {planarCap=true}; string key=RemeshPipeline.Key(RemeshPipeline.Stage.Remesh,a,null);
            a.planarCapLocalPlanes=true; Assert.AreNotEqual(key,RemeshPipeline.Key(RemeshPipeline.Stage.Remesh,a,null));
            Assert.IsFalse(RemeshSettings.FromSavedJson("{\"planarCap\":true}").planarCapLocalPlanes);
            Assert.IsTrue(RemeshSettings.FromSavedJson(JsonUtility.ToJson(a)).planarCapLocalPlanes);
        }

        [Test]
        public void SinglePlanarDiskKeepsTheExistingTriangulation()
        {
            var ix=Missing(0); var legacy=RemeshPlanarCap.Prepare(Box,ix,"0",default);
            var local=RemeshPlanarCap.Prepare(Box,ix,"0",default,true);
            CollectionAssert.AreEqual(legacy.positions,local.positions); CollectionAssert.AreEqual(legacy.indices,local.indices);
        }

        [Test]
        public void PrivateSandbagPlanarResultMatchesTheExistingDiskMode()
        {
            string path=Environment.GetEnvironmentVariable("MESH_LAB_CAP_SOURCE");
            if(string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_SOURCE to the private Sandbag source.bin.");
            using(var reader=new BinaryReader(File.OpenRead(path))) {
                int vertices=reader.ReadInt32(),count=reader.ReadInt32(); var p=new Vector3[vertices]; var ix=new int[count];
                for(int i=0;i<vertices;++i) p[i]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                for(int i=0;i<count;++i) ix[i]=reader.ReadInt32();
                var a=RemeshPlanarCap.Prepare(p,ix,"0",default); var b=RemeshPlanarCap.Prepare(p,ix,"0",default,true);
                CollectionAssert.AreEqual(a.positions,b.positions); CollectionAssert.AreEqual(a.indices,b.indices);
                Assert.AreEqual(45,b.addedFaces);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeLocalBoxCapPassesVoxelTrimSimplifyAndUnwrap(bool solve)
        {
            RemeshNative.CheckAvailable(); var support=RemeshPlanarCap.Prepare(Box,Missing(0,2),"0",default,true);
            var settings=new RemeshSettings {voxelResolution=32,solve=solve,maximumError=.02f,textureResolution=256};
            var voxel=RemeshNative.Voxelize(support.positions,support.indices,settings,default);
            var trimmed=RemeshTrim.Trim(voxel,support.positions,support.indices,1f,default);
            Assert.AreEqual(0,trimmed.removed);
            var simplified=solve ? RemeshSurfaceRefine.Simplify(trimmed.mesh,support.positions,support.indices,settings,default,out _) :
                RemeshNative.Simplify(trimmed.mesh,settings,default,out _);
            var topology=RemeshTopology.Inspect(simplified.positions,simplified.indices);
            Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
            var unwrapped=RemeshNative.Unwrap(simplified,settings,default);
            Assert.AreEqual(unwrapped.positions.Length,unwrapped.uv.Length);
            Assert.IsTrue(unwrapped.uv.All(v=>float.IsFinite(v.x)&&float.IsFinite(v.y)));
            TestContext.WriteLine($"Two-plane box solve={solve}: voxel {voxel.TriangleCount}; simplified {simplified.TriangleCount}; charts {unwrapped.chartCount}");
        }
    }
}
