using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshPlanarCapTests
    {
        static readonly Vector3[] Box = {
            new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
            new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
        static readonly int[] Faces = { 0,2,1,0,3,2, 4,5,6,4,6,7, 0,1,5,0,5,4, 3,7,6,3,6,2, 0,4,7,0,7,3, 1,2,6,1,6,5 };

        [TestCase(1)] [TestCase(16)] [TestCase(32)]
        public void ManyHolesCloseSequentiallyAsSeparatePatches(int holes)
        {
            var p = new System.Collections.Generic.List<Vector3>(); var ix = new System.Collections.Generic.List<int>();
            for (int n = 0; n < holes; ++n) {
                int offset = p.Count; p.AddRange(Box.Select(v => v + new Vector3(n * 4, 0, 0)));
                ix.AddRange(Faces.Skip(6).Select(v => v + offset));
            }
            var before = ix.ToArray(); var progress = new System.Collections.Generic.List<(int, int, int)>();
            var a = RemeshPlanarCap.Prepare(p.ToArray(), before, "all", default,
                loopCompleted: (loop, done, total) => progress.Add((loop, done, total)));
            Assert.AreEqual(holes, a.loops); Assert.AreEqual(holes, a.boundaryLoops.Length);
            Assert.AreEqual(holes * 2, a.addedFaces); Assert.AreEqual(holes, a.patchEnds.Count);
            Assert.AreEqual(0, RemeshTopology.Inspect(a.positions, a.indices).boundary.Count);
            for (int n = 0; n < holes; ++n) {
                Assert.AreEqual((n, n + 1, holes), progress[n]);
                Assert.AreEqual(holes * 10 + (n + 1) * 2, a.patchEnds[n]);
                for (int k = (holes * 10 + n * 2) * 3; k < (holes * 10 + (n + 1) * 2) * 3; ++k)
                    Assert.AreEqual(n, a.indices[k] / 8, "A cap must stay on its own box, never bridge unrelated holes");
            }
            CollectionAssert.AreEqual(before, ix);
        }

        [TestCase(false)] [TestCase(true)]
        public void CancellationBetweenHolesLeavesDonorUntouchedAndPublishesNoSupport(bool partial)
        {
            var p = Box.Concat(Box.Select(v => v + Vector3.right * 4)).ToArray();
            var ix = Faces.Skip(6).Concat(Faces.Skip(6).Select(v => v + 8)).ToArray();
            var saved = (int[])ix.Clone();
            using var cancel = new CancellationTokenSource();
            int completed = 0;
            Assert.Throws<OperationCanceledException>(() => RemeshPlanarCap.Prepare(p, ix, "all", cancel.Token,
                loopCompleted: (_, _, _) => { ++completed; cancel.Cancel(); }, continueOnRefusal: partial));
            Assert.AreEqual(1, completed); CollectionAssert.AreEqual(saved, ix);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void WeldsAttributeSplitBoxBeforeCapAndPreservesDonor(int missing)
        {
            var source = Faces.Where((v, k) => k / 6 != missing).Select(v => Box[v]).ToArray();
            var indices = Enumerable.Range(0, source.Length).ToArray();
            var saved = (Vector3[])source.Clone(); var savedIndices = (int[])indices.Clone();
            var a = RemeshPlanarCap.Prepare(source, indices, "0", default);
            var b = RemeshPlanarCap.Prepare(source, indices, "0", default);
            Assert.AreEqual(8, a.positions.Length); Assert.AreEqual(22, a.weldedVertices);
            Assert.AreEqual(2, a.addedFaces); Assert.AreEqual(1, a.loops);
            Assert.IsTrue(RemeshTopology.Inspect(a.positions, a.indices).Valid);
            Assert.AreEqual(0, RemeshTopology.Inspect(a.positions, a.indices).boundary.Count);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(a.positions, a.indices, default).All(v => v));
            CollectionAssert.AreEqual(saved, source); CollectionAssert.AreEqual(savedIndices, indices);
            CollectionAssert.AreEqual(a.positions, b.positions); CollectionAssert.AreEqual(a.indices, b.indices);
            // Geometry of every donor face is preserved after the support remap.
            for (int i = 0; i < indices.Length; ++i) Assert.AreEqual(source[indices[i]], a.positions[a.indices[i]]);
        }

        [TestCase(false)] [TestCase(true)]
        public void OversizedRimDoesNotBlockAnIndependentSmallCap(bool local)
        {
            const int count = 513;
            var p = Enumerable.Range(0, count).Select(i => {
                float a = i * 2 * Mathf.PI / count;
                return new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
            }).Concat(new[] {Vector3.forward}).Concat(Box.Select(v => v + Vector3.right * 5)).ToArray();
            var ix = Enumerable.Range(0, count).SelectMany(i => new[] {i, (i + 1) % count, count})
                .Concat(Faces.Skip(6).Select(v => v + count + 1)).ToArray();
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, local, continueOnRefusal: true);
            Assert.AreEqual(2, cap.addedFaces); Assert.AreEqual(1, cap.loopFailures.Count);
            StringAssert.Contains("512 edges", cap.loopFailures[0]);
            Assert.AreEqual(count, cap.remainingBoundaryEdges);
            Assert.IsTrue(RemeshTopology.Inspect(cap.positions, cap.indices).Valid);
            CollectionAssert.AreEqual(ix, cap.indices.Take(ix.Length));
        }

        [TestCase("16", 0)] [TestCase("0,16", 2)] [TestCase("0,0", 2)]
        [TestCase("all,1", 2)] [TestCase("", 0)] [TestCase("0,,1", 4)]
        public void InvalidSelectionEntriesKeepInspectableContoursAndOnlyExplicitValidCaps(string selection, int added)
        {
            var ix = Faces.Skip(12).ToArray();
            var cap = RemeshPlanarCap.Prepare(Box, ix, selection, default, continueOnRefusal: true);
            Assert.AreEqual(added, cap.addedFaces); Assert.AreEqual(2, cap.boundaryLoops.Length);
            StringAssert.Contains("Available loop numbers: 0..1", cap.selectionWarning);
            Assert.IsEmpty(cap.loopFailures, "Selection mistakes are not geometric contour refusals.");
            Assert.AreEqual(8 - added * 2, cap.remainingBoundaryEdges);
            CollectionAssert.AreEqual(ix, cap.indices.Take(ix.Length)); CollectionAssert.AreEqual(Box, cap.positions);
            Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.Prepare(Box, ix, selection, default));
        }

        [TestCase("0,1,16")] [TestCase("0,16")]
        public void InvalidBridgeSelectionDoesNotInventAPair(string selection)
        {
            var ix = Faces.Skip(12).ToArray();
            var cap = RemeshPlanarCap.Prepare(Box, ix, selection, default, mode: RemeshClosureMode.Bridge, continueOnRefusal: true);
            Assert.AreEqual(0, cap.addedFaces); Assert.AreEqual(8, cap.remainingBoundaryEdges);
            Assert.IsNotNull(cap.selectionWarning); Assert.IsEmpty(cap.patchEnds);
            CollectionAssert.AreEqual(ix, cap.indices);
        }

        [Test]
        public void FrozenGabionInvalidNumberKeepsAllThreeContoursForInspection()
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_SELECTION_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_SELECTION_SOURCE to the decoded MilitaryGabion source.bin.");
            using var reader = new BinaryReader(File.OpenRead(path));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            var p = new Vector3[vertices]; var ix = new int[count];
            for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < count; ++i) ix[i] = reader.ReadInt32();
            var cap = RemeshPlanarCap.Prepare(p, ix, "16", default, true, planeTolerance: 1e-5, continueOnRefusal: true);
            Assert.AreEqual(3, cap.boundaryLoops.Length); Assert.AreEqual(0, cap.addedFaces);
            Assert.AreEqual(47, cap.remainingBoundaryEdges); Assert.IsEmpty(cap.loopFailures);
            StringAssert.Contains("'16'", cap.selectionWarning); StringAssert.Contains("0..2", cap.selectionWarning);
            Assert.IsTrue(RemeshTopology.Inspect(cap.positions, cap.indices).Valid);
            for (int i = 0; i < ix.Length; ++i) Assert.AreEqual(p[ix[i]], cap.positions[cap.indices[i]]);
        }

        [Test]
        public void AutomaticBudgetRefusalsRemainInspectableWithoutInventingClosures()
        {
            var p = Enumerable.Range(0,17).SelectMany(n => Box.Select(v => v + Vector3.right * n * 4)).ToArray();
            var ix = Enumerable.Range(0,17).SelectMany(n => Faces.Skip(6).Select(v => v + n * 8)).ToArray();
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, mode: RemeshClosureMode.Automatic, continueOnRefusal: true);
            Assert.AreEqual(17, cap.loopFailures.Count); Assert.AreEqual(0, cap.addedFaces);
            Assert.AreEqual(68, cap.remainingBoundaryEdges); Assert.IsEmpty(cap.patchEnds);
            Assert.IsTrue(cap.loopFailures.Values.All(reason => reason.Contains("16 selected loops")));
            CollectionAssert.AreEqual(ix, cap.indices); CollectionAssert.AreEqual(p, cap.positions);
        }

        [TestCase(false)] [TestCase(true)]
        public void FrozenBushPartialPreparationKeepsTheRefusedRimAndReason(bool local)
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_OBSTACLE_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_OBSTACLE_SOURCE to the decoded Bush source.bin.");
            using var reader = new BinaryReader(File.OpenRead(path));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            var p = new Vector3[vertices]; var ix = new int[count];
            for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < count; ++i) ix[i] = reader.ReadInt32();
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, local, planeTolerance: 1e-5, continueOnRefusal: true);
            Assert.AreEqual(0, cap.addedFaces); Assert.AreEqual(4, cap.remainingBoundaryEdges);
            Assert.AreEqual(1, cap.loopFailures.Count); Assert.AreEqual(4, cap.boundaryLoops[0].Length);
            StringAssert.Contains("contacts face 26", cap.loopFailures[0]); Assert.IsEmpty(cap.patchEnds);
            Assert.IsTrue(cap.facePatches.All(id => id == 0));
            for (int i = 0; i < ix.Length; ++i) Assert.AreEqual(p[ix[i]], cap.positions[cap.indices[i]]);
        }

        [Test]
        public void SelectionPreservesOtherOpeningAndDoesNotInferBridge()
        {
            var ix = Faces.Skip(12).ToArray(); // opposite front/back faces absent
            var one = RemeshPlanarCap.Prepare(Box, ix, "0", default);
            Assert.AreEqual(2, one.loops); Assert.AreEqual(4, RemeshTopology.Inspect(one.positions, one.indices).boundary.Count);
            var both = RemeshPlanarCap.Prepare(Box, ix, "0,1", default);
            Assert.AreEqual(4, both.addedFaces); Assert.AreEqual(0, RemeshTopology.Inspect(both.positions, both.indices).boundary.Count);
            var all = RemeshPlanarCap.Prepare(Box, ix, " ALL ", default);
            CollectionAssert.AreEqual(both.indices, all.indices);
            foreach (string invalid in new[] { "", "bridge", "all,0", "0,0", "2", "-1" })
                Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.Prepare(Box, ix, invalid, default));
        }

        [TestCase(false)] [TestCase(true)]
        public void CapturedParkBenchMicrometreRimUsesExplicitPlaneToleranceWithoutMovingVertices(bool local)
        {
            var rim = new[] {
                new Vector3(-1.018702507019043f,.054338473826646805f,-.24817068874835968f),
                new Vector3(-1.0107016563415527f,.05163917690515518f,-.24064117670059204f),
                new Vector3(-1.002703070640564f,.054338473826646805f,-.24817068874835968f),
                new Vector3(-1.0107016563415527f,.05703570321202278f,-.25570225715637207f) };
            var direction=Vector3.Cross(rim[1]-rim[0],rim[2]-rim[0]).normalized*.02f;
            var p=rim.Concat(rim.Select(v=>v+direction)).ToArray();
            var ix=new System.Collections.Generic.List<int>();
            for(int i=0;i<4;++i) {int j=(i+1)%4; ix.AddRange(new[] {i,j,j+4,i,j+4,i+4});}
            ix.AddRange(new[] {4,5,6,4,6,7}); var source=ix.ToArray();
            var saved=(Vector3[])p.Clone();
            Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(p,source,"all",default,local));
            var cap=RemeshPlanarCap.Prepare(p,source,"all",default,local,planeTolerance:1e-5);
            Assert.AreEqual(2,cap.addedFaces); Assert.AreEqual(0,RemeshTopology.Inspect(cap.positions,cap.indices).boundary.Count);
            CollectionAssert.AreEqual(saved,p); CollectionAssert.AreEqual(p,cap.positions);
            CollectionAssert.AreEqual(source,cap.indices.Take(source.Length));
            // Real warping remains outside the explicit micrometre allowance.
            p[2]+=direction*.1f;
            Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(p,source,"all",default,local,planeTolerance:1e-5));
        }

        [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)] [TestCase(-1)]
        public void InvalidPlaneToleranceRefusesBeforePublishing(double tolerance)
        {
            Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(Box,Faces,"all",default,planeTolerance:tolerance));
        }

        [TestCase(false)] [TestCase(true)]
        public void ExplicitToleranceDoesNotBypassIntersectionOrCompoundChecks(bool local)
        {
            var p=Box.Concat(new[] {new Vector3(0,-2,0),new Vector3(0,0,0),new Vector3(.4f,-1,.4f)}).ToArray();
            var ix=Faces.Where((v,k)=>k/6!=2).Concat(new[] {8,9,10}).ToArray();
            StringAssert.Contains("contacts face",Assert.Throws<InvalidOperationException>(()=>
                RemeshPlanarCap.Prepare(p,ix,"0",default,local,planeTolerance:1e-5)).Message);
            var adjacent=Faces.Where((v,k)=>k/6!=0 && k/6!=2).ToArray();
            if(!local) Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(Box,adjacent,"all",default,planeTolerance:1e-5));
            else Assert.AreEqual(4,RemeshPlanarCap.Prepare(Box,adjacent,"all",default,true,planeTolerance:1e-5).addedFaces);
        }

        [TestCase(false)] [TestCase(true)]
        public void FrozenBushPlanarRimRefusesInteriorObstacleWithoutChangingDonor(bool local)
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_OBSTACLE_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_OBSTACLE_SOURCE to the decoded Bush source.bin.");
            using var reader = new BinaryReader(File.OpenRead(path));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            var positions = new Vector3[vertices]; var indices = new int[count];
            for (int i = 0; i < vertices; ++i)
                positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < count; ++i) indices[i] = reader.ReadInt32();
            var saved = (Vector3[])positions.Clone(); var savedIndices = (int[])indices.Clone();
            var topology = RemeshTopology.Inspect(positions, indices);
            Assert.IsTrue(topology.Valid); Assert.AreEqual(4, topology.boundary.Count);
            var error = Assert.Throws<InvalidOperationException>(() =>
                RemeshPlanarCap.Prepare(positions, indices, "all", default, local, planeTolerance: 1e-5));
            StringAssert.Contains("contacts face 26", error.Message);
            CollectionAssert.AreEqual(saved, positions); CollectionAssert.AreEqual(savedIndices, indices);
            TestContext.WriteLine(error.Message);
        }

        [TestCase(0,false)] [TestCase(0,true)] [TestCase(1,false)] [TestCase(1,true)]
        public void UserFrozenCapFailuresPreserveDonorsAndRefuseIntersectingClosure(int model,bool local)
        {
            string sources=Environment.GetEnvironmentVariable("MESH_LAB_CAP_FAILURE_SOURCES");
            if(string.IsNullOrEmpty(sources)) Assert.Ignore("Set MESH_LAB_CAP_FAILURE_SOURCES to decoded source.bin captures.");
            foreach(string path in sources.Split(';').Skip(model).Take(1))
            {
                using var reader=new BinaryReader(File.OpenRead(path));
                int vertices=reader.ReadInt32(),count=reader.ReadInt32();
                var p=new Vector3[vertices]; var ix=new int[count];
                for(int i=0;i<vertices;++i) p[i]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                for(int i=0;i<count;++i) ix[i]=reader.ReadInt32();
                var saved=(Vector3[])p.Clone(); var savedIndices=(int[])ix.Clone();
                for(int loop=0;loop<(model==0?10:4);++loop) {
                    try {var single=RemeshPlanarCap.Prepare(p,ix,loop.ToString(),default,local,planeTolerance:1e-5);
                        TestContext.WriteLine($"Loop {loop}: {single.Description}");}
                    catch(InvalidOperationException ex) {TestContext.WriteLine($"Loop {loop}: {ex.Message}");}
                }
                var error=Assert.Throws<InvalidOperationException>(()=>RemeshPlanarCap.Prepare(p,ix,"all",default,local,planeTolerance:1e-5));
                StringAssert.Contains("contacts face",error.Message);
                CollectionAssert.AreEqual(saved,p); CollectionAssert.AreEqual(savedIndices,ix);
                // A non-intersecting subset succeeds but cannot be passed to solid Remesh.
                var subset=RemeshPlanarCap.Prepare(p,ix,model==0?"3,4,6,8":"0,2,3",default,local,planeTolerance:1e-5);
                Assert.Greater(subset.addedFaces,0);
                Assert.Greater(RemeshTopology.Inspect(subset.positions,subset.indices).boundary.Count,0);
                for(int i=0;i<ix.Length;++i) Assert.AreEqual(p[ix[i]],subset.positions[subset.indices[i]]);
            }
        }

        [Test]
        public void NonplanarAdjacentFacesAndInvalidWeldTopologyRefuseAtomically()
        {
            var adjacent = Faces.Where((v, k) => k / 6 != 0 && k / 6 != 2).ToArray();
            StringAssert.Contains("not wholly planar", Assert.Throws<InvalidOperationException>(() =>
                RemeshPlanarCap.Prepare(Box, adjacent, "0", default)).Message);
            var p = new[] { Vector3.zero, Vector3.zero, Vector3.right };
            StringAssert.Contains("after weld", Assert.Throws<InvalidOperationException>(() =>
                RemeshPlanarCap.Prepare(p, new[] { 0, 1, 2 }, "0", default)).Message);
        }

        [TestCase(false, 1)] [TestCase(true, 1)] [TestCase(true, 2)] [TestCase(true, 3)]
        public void OtherClosedMeshContactsAreDiagnosticButSameMeshOrUnknownOwnershipStillRefuses(bool local, int missing)
        {
            int[] absent = missing == 1 ? new[] { 2 } : missing == 2 ? new[] { 0, 2 } : new[] { 0, 2, 4 };
            var donor = Faces.Where((_, k) => !absent.Contains(k / 6)).ToArray();
            var points = Box.Concat(new[] {
                new Vector3(-1.3f,-1.3f,-1.3f), new Vector3(-.7f,-1.3f,-.7f),
                new Vector3(-.7f,-.7f,-1.3f), new Vector3(-1.3f,-.7f,-.7f) }).ToArray();
            var indices = donor.Concat(new[] {8,10,9,8,9,11,9,10,11,10,8,11}).ToArray();
            var owners = Enumerable.Repeat(0, donor.Length / 3).Concat(Enumerable.Repeat(1, 4)).ToArray();
            var saved = (Vector3[])points.Clone(); var savedIndices = (int[])indices.Clone();
            var support = RemeshPlanarCap.Prepare(points, indices, "all", default, local, sourceFaceOwners: owners);
            Assert.AreEqual(missing * 2, support.addedFaces);
            Assert.Greater(support.externalContacts.count, 0);
            var topology = RemeshTopology.Inspect(support.positions, support.indices);
            Assert.IsTrue(topology.Valid); Assert.AreEqual(0, topology.boundary.Count);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(support.positions, support.indices, default).All(v => v));
            for (int i = 0; i < indices.Length; ++i) Assert.AreEqual(points[indices[i]], support.positions[support.indices[i]]);
            CollectionAssert.AreEqual(saved, points); CollectionAssert.AreEqual(savedIndices, indices);
            foreach (var strictOwners in new[] { new int[owners.Length], owners.Select(v => v == 1 ? -1 : v).ToArray(), owners.Select(v => v == 0 ? -1 : v).ToArray() })
                Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.Prepare(points, indices, "all", default, local, sourceFaceOwners: strictOwners));
            Assert.Throws<ArgumentException>(() => RemeshPlanarCap.Prepare(points, indices, "all", default, local, sourceFaceOwners: new[] { 0 }));
            TestContext.WriteLine($"External closed mesh: {support.externalContacts.count} diagnostic contacts; {support.addedFaces} added faces.");
        }

        [Test]
        public void NativeGuardStillRefusesMeasuredOpenUnionOfIntersectingClosedDonors()
        {
            var points = Box.Concat(new[] {
                new Vector3(-1.3f,-1.3f,-1.3f), new Vector3(-.7f,-1.3f,-.7f),
                new Vector3(-.7f,-.7f,-1.3f), new Vector3(-1.3f,-.7f,-.7f) }).ToArray();
            var indices = Faces.Where((_, k) => k / 6 != 2).Concat(new[] {8,10,9,8,9,11,9,10,11,10,8,11}).ToArray();
            var owners = Enumerable.Repeat(0, 10).Concat(Enumerable.Repeat(1, 4)).ToArray();
            var support = RemeshPlanarCap.Prepare(points, indices, "all", default, sourceFaceOwners: owners);
            Assert.Greater(support.externalContacts.count, 0);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(support.positions, support.indices, default).All(v => v));
            var saved = (int[])support.indices.Clone(); var savedPoints = (Vector3[])support.positions.Clone();
            var error = Assert.Throws<InvalidOperationException>(() =>
                RemeshNative.Voxelize(support.positions, support.indices, new RemeshSettings { voxelResolution = 64, solve = false }, default));
            StringAssert.Contains("Solid voxel remesh is not a valid closed surface", error.Message);
            StringAssert.Contains("boundary 9", error.Message);
            CollectionAssert.AreEqual(saved, support.indices); CollectionAssert.AreEqual(savedPoints, support.positions);
            TestContext.WriteLine("Measured native limitation after successful Cap: " + error.Message);
        }

        [TestCase(false, 1, false)] [TestCase(false, 1, true)]
        [TestCase(true, 2, false)] [TestCase(true, 2, true)]
        [TestCase(true, 3, false)] [TestCase(true, 3, true)]
        public void OtherConnectedElementsInTheSameMeshDoNotBlockCaps(bool local, int missing, bool ownersPresent)
        {
            int[] absent = missing == 1 ? new[] {2} : missing == 2 ? new[] {0,2} : new[] {0,2,4};
            var donor = Faces.Where((_, k) => !absent.Contains(k / 6)).ToArray();
            var p = Box.Concat(new[] {
                new Vector3(-1.3f,-1.3f,-1.3f),new Vector3(-.7f,-1.3f,-.7f),
                new Vector3(-.7f,-.7f,-1.3f),new Vector3(-1.3f,-.7f,-.7f)}).ToArray();
            var ix = donor.Concat(new[] {8,10,9,8,9,11,9,10,11,10,8,11}).ToArray();
            var saved = (int[])ix.Clone(); var savedPoints = (Vector3[])p.Clone();
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, local,
                sourceFaceOwners: ownersPresent ? new int[ix.Length / 3] : null, elementScopedContacts: true);
            Assert.AreEqual(missing * 2, cap.addedFaces); Assert.Greater(cap.externalContacts.excludedElementPairs, 0);
            Assert.AreNotEqual(cap.externalContacts.faceElements[0], cap.externalContacts.faceElements[donor.Length / 3]);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions, cap.indices, default).All(v => v));
            CollectionAssert.AreEqual(saved, ix); CollectionAssert.AreEqual(savedPoints, p);
        }

        [TestCase(false)] [TestCase(true)]
        public void ElementScopeStillRefusesContactWithItsOwnFoldedSurface(bool local)
        {
            var p = (Vector3[])Box.Clone(); p[2] = new Vector3(0,-2,0);
            var ix = Faces.Where((_, k) => k / 6 != 2).ToArray();
            Assert.IsTrue(RemeshTopology.Inspect(p, ix).Valid);
            var error = Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.Prepare(p, ix, "all", default, local, elementScopedContacts: true));
            StringAssert.Contains("contacts face", error.Message);
        }

        [Test]
        public void CapsMayCrossEarlierCapsOfAnotherElementInTheSameMesh()
        {
            var p = Box.Concat(Box.Select(v => v + new Vector3(.5f,-.5f,.5f))).ToArray();
            var ix = Faces.Where((_, k) => k / 6 != 2).Concat(Faces.Skip(6).Select(v => v + 8)).ToArray();
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, sourceFaceOwners: new int[ix.Length / 3], elementScopedContacts: true);
            Assert.AreEqual(4, cap.addedFaces); Assert.AreEqual(0, cap.remainingBoundaryEdges);
            Assert.AreEqual(cap.faceElements[0], cap.faceElements[20]);
            Assert.AreEqual(cap.faceElements[10], cap.faceElements[22]);
            Assert.AreNotEqual(cap.faceElements[20], cap.faceElements[22]);
            Assert.Greater(cap.externalContacts.excludedElementPairs, 40);
            CollectionAssert.AreEqual(ix, cap.indices.Take(ix.Length));
        }

        [Test]
        public void AnEarlierCapInTheSameElementStillBlocksAContactAndExternalPairsDoNotConsumeItsBudget()
        {
            var p = new[] {new Vector3(10,10,10),new Vector3(11,10,10),new Vector3(10,11,10),
                Vector3.zero,Vector3.right,Vector3.up,
                new Vector3(.25f,.25f,-1),new Vector3(.25f,.25f,1),new Vector3(.75f,.25f,0)};
            var external = new RemeshPlanarCap.ExternalContacts {faceElements = new[] {1,0}, closingElements = new System.Collections.Generic.HashSet<int> {0}};
            int trials = 1999999;
            var error = Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.AuditContacts(p, p.Select(RemeshCapIntersection.Point).ToArray(),
                Enumerable.Range(0,9).ToArray(), 2, default, ref trials, out _, external));
            StringAssert.Contains("new face 2 contacts face 1", error.Message);
            Assert.AreEqual(2000000, trials); Assert.AreEqual(1, external.excludedElementPairs);
        }

        [Test]
        public void FrozenGarbageChuteClosesLoopFourDespiteFace467InAnotherElement()
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_ELEMENT_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_ELEMENT_SOURCE to the decoded Garbage_Chute source.bin.");
            using var reader = new BinaryReader(File.OpenRead(path));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            var p = new Vector3[vertices]; var ix = new int[count];
            for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            for (int i = 0; i < count; ++i) ix[i] = reader.ReadInt32();
            var old = RemeshPlanarCap.Prepare(p, ix, "all", default, true, planeTolerance: 1e-5, continueOnRefusal: true);
            TestContext.WriteLine("Legacy contact scope: " + old.Description);
            Assert.AreEqual(52, old.addedFaces);
            StringAssert.Contains("face 467", old.loopFailures[4]);
            var cap = RemeshPlanarCap.Prepare(p, ix, "all", default, true, planeTolerance: 1e-5,
                sourceFaceOwners: new int[count / 3], continueOnRefusal: true, elementScopedContacts: true);
            Assert.IsFalse(cap.loopFailures.ContainsKey(4)); Assert.GreaterOrEqual(cap.addedFaces, 46);
            TestContext.WriteLine("Element contact scope: " + cap.Description);
            foreach (var failure in cap.loopFailures) TestContext.WriteLine($"Loop {failure.Key}: {failure.Value}");
            Assert.IsEmpty(cap.loopFailures); Assert.AreEqual(66, cap.addedFaces);
            Assert.AreEqual(0, cap.remainingBoundaryEdges);
            Assert.Greater(cap.externalContacts.excludedElementPairs, 0);
            Assert.AreNotEqual(cap.externalContacts.faceElements[467], cap.faceElements[cap.faceElements.Length - 1]);
            Assert.IsTrue(RemeshTopology.Inspect(cap.positions, cap.indices).Valid);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(cap.positions, cap.indices, default).All(v => v));
            for (int i = 0; i < ix.Length; ++i) Assert.AreEqual(p[ix[i]], cap.positions[cap.indices[i]]);
            TestContext.WriteLine(cap.Description);
        }

        [Test]
        public void ElementScopeCaptureKeepsSourceAndPreparedFaceMapsAndExcludedPairCount()
        {
            var p = Box.Concat(Box.Select(v => v + new Vector3(.5f,-.5f,.5f))).ToArray();
            var ix = Faces.Where((_, k) => k / 6 != 2).Concat(Faces.Skip(6).Select(v => v + 8)).ToArray();
            var support = RemeshPlanarCap.Prepare(p, ix, "all", default, elementScopedContacts: true);
            string folder = Path.Combine(Path.GetTempPath(), "meshlab-cap-test-" + Guid.NewGuid().ToString("N"));
            try {
                string path = RemeshGeometryDiagnostics.WriteFailure(folder, p, ix, null, null,
                    new RemeshGeometryDiagnostics.FailureMetadata {stage = "Element scope"}, support);
                using var reader = new BinaryReader(File.OpenRead(path));
                Assert.AreEqual(0x524D4C42, reader.ReadInt32()); Assert.AreEqual(3, reader.ReadInt32());
                var metadata = JsonUtility.FromJson<RemeshGeometryDiagnostics.FailureMetadata>(reader.ReadString());
                CollectionAssert.AreEqual(support.externalContacts.faceElements, metadata.sourceFaceElements);
                CollectionAssert.AreEqual(support.faceElements, metadata.preparedFaceElements);
                Assert.AreEqual(20, metadata.sourceFaceElements.Length); Assert.AreEqual(24, metadata.preparedFaceElements.Length);
                Assert.AreEqual(support.externalContacts.excludedElementPairs, metadata.excludedElementPairs);
                Assert.Greater(metadata.excludedElementPairs, 0);
            }
            finally {
                string expected = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "meshlab-cap-test-");
                if (!Path.GetFullPath(folder).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
                Directory.Delete(folder, true);
            }
        }

        [TestCase(1)] [TestCase(2)]
        public void ExternalSourcePolicyNeverExemptsEarlierSyntheticFaces(int priorFaces)
        {
            var positions = new[] {
                new Vector3(10,10,10), new Vector3(11,10,10), new Vector3(10,11,10),
                Vector3.zero, Vector3.right, Vector3.up,
                new Vector3(.25f,.25f,-1), new Vector3(.25f,.25f,1), new Vector3(.75f,.25f,0) };
            var exact = positions.Select(RemeshCapIntersection.Point).ToArray();
            var contacts = new RemeshPlanarCap.ExternalContacts { faceOwners = new[] { 1 }, closingOwners = new System.Collections.Generic.HashSet<int> { 0 } };
            int trials = 0;
            var error = Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.AuditContacts(positions, exact,
                Enumerable.Range(0, 9).ToArray(), priorFaces, default, ref trials, out _, contacts));
            StringAssert.Contains("new face 2 contacts face 1", error.Message);
            Assert.AreEqual(0, contacts.count);
        }

        [Test]
        public void IntersectingObstacleRejectsCapEvenThoughSourceTopologyIsValid()
        {
            var p = Box.Concat(new[] { new Vector3(0,-2,0), new Vector3(0,0,0), new Vector3(.4f,-1,.4f) }).ToArray();
            var ix = Faces.Where((v, k) => k / 6 != 2).Concat(new[] { 8, 9, 10 }).ToArray();
            StringAssert.Contains("contacts face", Assert.Throws<InvalidOperationException>(() =>
                RemeshPlanarCap.Prepare(p, ix, "0", default)).Message);
        }

        [Test]
        public void CancellationAndInvalidGeometryDoNotPublishSupport()
        {
            var cancellation = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => RemeshPlanarCap.Prepare(Box, Faces, "0", cancellation));
            Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.Prepare(new[] { new Vector3(float.NaN,0,0) }, new[] {0,0,0}, "0", default));
        }

        [Test]
        public void SettingsInvalidateRemeshAndOlderSettingsKeepCapOff()
        {
            var a = new RemeshSettings(); string key = RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null);
            a.planarCap = true; Assert.AreNotEqual(key, RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null));
            key = RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null);
            a.planarCapLoops = "1"; Assert.AreNotEqual(key, RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null));
            Assert.IsFalse(RemeshSettings.FromSavedJson("{\"voxelResolution\":64}").planarCap);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeBoxCapSurvivesTrimAndSourceFittedSimplify(bool solve)
        {
            RemeshNative.CheckAvailable();
            var ix = Faces.Where((v, k) => k / 6 != 2).ToArray();
            var support = RemeshPlanarCap.Prepare(Box, ix, "0", default);
            var settings = new RemeshSettings { voxelResolution = 32, solve = solve, maximumError = .02f };
            RunGeometry(support, settings);
        }

        [TestCase(64, false)] [TestCase(64, true)]
        [TestCase(128, false)] [TestCase(128, true)]
        public void PrivateSandbagReplayThroughNativeTrimSimplify(int resolution, bool solve)
        {
            var support = PrivateSandbag();
            Assert.AreEqual(45, support.addedFaces); Assert.AreEqual(1, support.loops);
            var settings = new RemeshSettings { voxelResolution = resolution, solve = solve, maximumError = .005f,
                normalCrease = 133, normalSmoothing = 3, normalWeighting = RemeshNormalWeighting.FaceAreaAndCornerAngle,
                textureResolution = 512, padding = 3, mergeCharts = true };
            var simplified = RunGeometry(support, settings);
            var unwrapped = RemeshNative.Unwrap(simplified, settings, default);
            Assert.AreEqual(unwrapped.positions.Length, unwrapped.uv.Length);
            Assert.IsTrue(unwrapped.uv.All(uv => float.IsFinite(uv.x) && float.IsFinite(uv.y)));
            Assert.IsTrue(RemeshBaker.ClassifyCapFaces(unwrapped, support, default).Any(v => v));
            TestContext.WriteLine($"Sandbag r{resolution} solve={solve}: {support.Description}; simplified {simplified.TriangleCount}; charts {unwrapped.chartCount}");
        }

        static RemeshPlanarCap.Support PrivateSandbag()
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_SOURCE to the private decoded source.bin for this optional real-model replay.");
            using (var reader = new BinaryReader(File.OpenRead(path))) {
                int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
                var p = new Vector3[vertices]; var ix = new int[indices];
                for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (int i = 0; i < indices; ++i) ix[i] = reader.ReadInt32();
                return RemeshPlanarCap.Prepare(p, ix, "0", default);
            }
        }

        // Accept the native fin-pair repair only after full closed topology validation.
        [TestCase(false)] [TestCase(true)]
        public void PrivateSandbag256RepairsOpposingFinBeforeTrim(bool solve)
        {
            var support = PrivateSandbag();
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(support.positions, support.indices, default).All(v => v));
            var settings = new RemeshSettings { voxelResolution = 256, solve = solve };
            var voxel = RemeshNative.Voxelize(support.positions,support.indices,settings,default);
            var topology = RemeshTopology.Inspect(voxel.positions,voxel.indices);
            Assert.AreEqual(102644,voxel.TriangleCount);
            Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(voxel.positions,voxel.indices,default).All(v=>v));
        }

        [Test]
        public void BakeUsesOriginalDonorAndCountsMissingSyntheticSurfaceProjections()
        {
            var source = new RemeshSource { positions = (Vector3[])Box.Clone(), indices = Faces.Where((v,k) => k/6 != 2).ToArray(),
                normals = Box.Select(v => v.normalized).ToArray(), tangents = Box.Select(v => new Vector4(1,0,0,1)).ToArray(),
                uv = Box.Select(v => new Vector2(v.x,v.z)).ToArray(), uv2 = Box.Select(v => new Vector2(v.y,v.z)).ToArray(),
                colors = Box.Select(v => new Color(.2f,.3f,.7f,.4f)).ToArray(), hasColors = true,
                faceMaterials = new int[10], diagonal = Mathf.Sqrt(12), materials = new[] { new RemeshSource.Surface {
                    color = new RemeshSource.Map(), normal = new RemeshSource.Map(), metal = new RemeshSource.Map(),
                    ao = new RemeshSource.Map(), emission = new RemeshSource.Map(), tint = Color.white } } };
            var uv = (Vector2[])source.uv.Clone(); var uv2 = (Vector2[])source.uv2.Clone();
            var normals = (Vector3[])source.normals.Clone(); var tangents = (Vector4[])source.tangents.Clone();
            var colors = (Color[])source.colors.Clone(); var indices = (int[])source.indices.Clone();
            var support = RemeshPlanarCap.Prepare(source.positions, source.indices, "0", default);
            var target = new RemeshNative.Geometry { positions = new[] { Box[0],Box[1],Box[5],Box[4] },
                indices = new[] {0,1,2,0,2,3}, normals = Enumerable.Repeat(Vector3.down,4).ToArray(),
                uv = new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up}, charts = new int[4], chartCount = 1 };
            var maps = RemeshBaker.Bake(source, target, Enumerable.Repeat(new Vector4(1,0,0,1),4).ToArray(),
                new RemeshSettings { textureResolution = 64, padding = 1, dilationRadius = 0, projectionDistance = .001f, cageFit = false, bakeSamples = 1 },
                default, support: support);
            Assert.AreEqual(maps.covered, maps.capCovered); Assert.Greater(maps.capMisses,0);
            Assert.AreEqual(maps.misses,maps.capMisses); Assert.AreEqual(new Color32(255,0,255,255),maps.color[32*64+32]);
            CollectionAssert.AreEqual(uv,source.uv); CollectionAssert.AreEqual(uv2,source.uv2);
            CollectionAssert.AreEqual(normals,source.normals); CollectionAssert.AreEqual(tangents,source.tangents);
            CollectionAssert.AreEqual(colors,source.colors); CollectionAssert.AreEqual(indices,source.indices);
        }

        [Test]
        public void Version3CaptureKeepsOriginalAndWeldedPreparedGeometrySeparate()
        {
            string folder = Path.Combine(Path.GetTempPath(),"meshlab-cap-test-"+Guid.NewGuid().ToString("N"));
            var source = Faces.Where((v,k) => k/6 != 2).Select(v => Box[v]).ToArray();
            var ix = Enumerable.Range(0,source.Length).ToArray(); var support = RemeshPlanarCap.Prepare(source,ix,"0",default);
            try {
                string path = RemeshGeometryDiagnostics.WriteFailure(folder,source,ix,null,null,
                    new RemeshGeometryDiagnostics.FailureMetadata { stage = "Cap preparation" },support);
                using (var reader = new BinaryReader(File.OpenRead(path))) {
                    Assert.AreEqual(0x524D4C42,reader.ReadInt32()); Assert.AreEqual(3,reader.ReadInt32()); reader.ReadString();
                    Assert.IsTrue(reader.ReadBoolean()); Assert.AreEqual(30,reader.ReadInt32()); Assert.AreEqual(30,reader.ReadInt32());
                    for(int i=0;i<30;++i) Assert.AreEqual(source[i],new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()));
                    for(int i=0;i<30;++i) Assert.AreEqual(ix[i],reader.ReadInt32());
                    Assert.IsTrue(reader.ReadBoolean()); Assert.AreEqual(8,reader.ReadInt32()); Assert.AreEqual(36,reader.ReadInt32());
                    for(int i=0;i<8;++i) Assert.AreEqual(support.positions[i],new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()));
                    for(int i=0;i<36;++i) Assert.AreEqual(support.indices[i],reader.ReadInt32());
                    Assert.IsFalse(reader.ReadBoolean()); Assert.IsFalse(reader.ReadBoolean()); Assert.AreEqual(reader.BaseStream.Length,reader.BaseStream.Position);
                }
            }
            finally {
                string expected = Path.Combine(Path.GetFullPath(Path.GetTempPath()),"meshlab-cap-test-");
                if (!Path.GetFullPath(folder).StartsWith(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
                Directory.Delete(folder,true);
            }
        }

        [Test]
        public void PartialSupportCaptureStoresRefusedLoopReasonsBesidePreservedSource()
        {
            var p = Box.Concat(new[] {new Vector3(0,-2,0),new Vector3(0,0,0),new Vector3(.4f,-1,.4f)}).ToArray();
            var ix = Faces.Where((v,k) => k / 6 != 2).Concat(new[] {8,9,10}).ToArray();
            var support = RemeshPlanarCap.Prepare(p, ix, "0,16", default, continueOnRefusal: true);
            string folder = Path.Combine(Path.GetTempPath(), "meshlab-cap-test-" + Guid.NewGuid().ToString("N"));
            try {
                string path = RemeshGeometryDiagnostics.WriteFailure(folder, p, ix, null, null,
                    new RemeshGeometryDiagnostics.FailureMetadata {stage = "Partial support"}, support);
                using var reader = new BinaryReader(File.OpenRead(path));
                Assert.AreEqual(0x524D4C42, reader.ReadInt32()); Assert.AreEqual(3, reader.ReadInt32());
                var metadata = JsonUtility.FromJson<RemeshGeometryDiagnostics.FailureMetadata>(reader.ReadString());
                CollectionAssert.AreEqual(new[] {0}, metadata.refusedLoops);
                CollectionAssert.AreEqual(new[] {support.loopFailures[0]}, metadata.refusedLoopReasons);
                Assert.AreEqual(support.selectionWarning, metadata.closureSelectionWarning);
                StringAssert.Contains("'16'", metadata.closureSelectionWarning);
                Assert.IsTrue(reader.ReadBoolean()); Assert.AreEqual(p.Length, reader.ReadInt32()); Assert.AreEqual(ix.Length, reader.ReadInt32());
                for (int i = 0; i < p.Length; ++i) Assert.AreEqual(p[i], new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                for (int i = 0; i < ix.Length; ++i) Assert.AreEqual(ix[i], reader.ReadInt32());
            }
            finally {
                string expected = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "meshlab-cap-test-");
                if (!Path.GetFullPath(folder).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
                Directory.Delete(folder, true);
            }
        }

        static RemeshNative.IndexedMesh RunGeometry(RemeshPlanarCap.Support support, RemeshSettings settings)
        {
            var voxel = RemeshNative.Voxelize(support.positions, support.indices, settings, default);
            var trimmed = RemeshTrim.Trim(voxel, support.positions, support.indices, 1f, default);
            Assert.AreEqual(0, trimmed.removed);
            var simplified = settings.solve ? RemeshSurfaceRefine.Simplify(trimmed.mesh, support.positions, support.indices, settings, default, out _)
                : RemeshNative.Simplify(trimmed.mesh, settings, default, out _);
            var topology = RemeshTopology.Inspect(simplified.positions, simplified.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count, topology.Description);
            return simplified;
        }

        [Test]
        public void ConcaveRimTriangulatesWithoutClosingItsNotch()
        {
            var ring = new[] { new Vector3(0,0,0), new Vector3(2,0,0), new Vector3(2,1,0),
                new Vector3(1,1,0), new Vector3(1,2,0), new Vector3(0,2,0) };
            var p = ring.Concat(ring.Select(v => v + Vector3.forward)).ToArray();
            var ix = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 6; ++i) { int j = (i+1)%6; ix.AddRange(new[] {i,j,j+6,i,j+6,i+6}); }
            ix.AddRange(new[] {6,7,9,7,8,9,6,9,11,9,10,11});
            var support = RemeshPlanarCap.Prepare(p, ix.ToArray(), "0", default);
            Assert.AreEqual(4, support.addedFaces); Assert.AreEqual(0, RemeshTopology.Inspect(support.positions, support.indices).boundary.Count);
            float area = 0;
            for (int f = support.originalFaces; f < support.indices.Length/3; ++f) {
                var a = support.positions[support.indices[f*3]]; var b = support.positions[support.indices[f*3+1]];
                var c = support.positions[support.indices[f*3+2]];
                area += Vector3.Cross(b-a,c-a).magnitude*.5f;
                var center = (a+b+c)/3;
                Assert.IsFalse(center.x > 1 && center.y > 1, "Cap covered the concave notch.");
            }
            Assert.AreEqual(3, area, 1e-6f);
        }

        [Test]
        public void ExactContactAuditRejectsCrossingsCoplanarOverlapAndImproperAdjacentFaces()
        {
            var a = new[] { Vector3.zero, Vector3.right, Vector3.up };
            bool Hit(Vector3[] b) => RemeshCapIntersection.Improper(a.Select(RemeshCapIntersection.Point).ToArray(), b.Select(RemeshCapIntersection.Point).ToArray());
            Assert.IsFalse(Hit(new[] { Vector3.right, Vector3.zero, Vector3.down }));
            Assert.IsFalse(Hit(new[] { Vector3.zero, Vector3.back, Vector3.left }));
            Assert.IsTrue(Hit(new[] { Vector3.right, Vector3.zero, new Vector3(.2f,.2f,0) }));
            Assert.IsTrue(Hit(new[] { new Vector3(.2f,.2f,-1), new Vector3(.2f,.2f,1), new Vector3(.7f,.2f,0) }));
            Assert.IsTrue(Hit(new[] { new Vector3(.2f,.2f,0), new Vector3(.3f,.2f,0), new Vector3(.2f,.3f,0) }));
        }
    }
}
