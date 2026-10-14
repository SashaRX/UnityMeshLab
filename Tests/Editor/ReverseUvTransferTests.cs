using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ReverseUvTransferTests
    {
        static float BoundaryTolerance() => float.TryParse(Environment.GetEnvironmentVariable("MESHLAB_TRANSFER_BOUNDARY_TEXELS"),
            System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)?value:UvTransferTolerance.BoundaryTexels;

        readonly List<Mesh> owned = new List<Mesh>();
        [TearDown] public void Cleanup() { foreach (var m in owned) if (m) Object.DestroyImmediate(m); owned.Clear(); }

        Mesh Quad(float x = 0, float z = 0, float size = 1)
        {
            var mesh = new Mesh { name = "Quad" };
            mesh.vertices = new[] { new Vector3(x, 0, z), new Vector3(x + size, 0, z), new Vector3(x + size, size, z), new Vector3(x, size, z) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.uv2 = mesh.uv.Select(p => p * .5f + Vector2.one * .25f).ToArray();
            mesh.RecalculateNormals(); owned.Add(mesh); return mesh;
        }

        Mesh Combine(params Mesh[] meshes)
        {
            var mesh = new Mesh { name = "Combined" };
            mesh.CombineMeshes(meshes.Select(m => new CombineInstance { mesh = m, transform = Matrix4x4.identity }).ToArray(), true);
            owned.Add(mesh); return mesh;
        }
        static ReverseUvTransfer.Level Level(int lod, params Mesh[] meshes) => new ReverseUvTransfer.Level {
            lod = lod, inputs = meshes.Select((m, i) => new ReverseUvTransfer.Input { mesh = m, key = m.name + i }).ToArray() };
        static ReverseUvTransfer.Options Options(bool overlap = false) => new ReverseUvTransfer.Options {boundaryToleranceTexels=BoundaryTolerance(),
            seedResolution = 128, projectionReach = .1f, preserveProjectedOverlap = overlap };
        static ReverseUvTransfer.Result Build(ReverseUvTransfer.Options options, params ReverseUvTransfer.Level[] levels)
            => ReverseUvTransfer.Build(levels, options).GetAwaiter().GetResult();

        [Test]
        public void PreparedAtlasOversamplingDoesNotChangeBakeToleranceOrFinalLayout()
        {
            var seed=Quad();var fine=Quad();fine.uv2=fine.uv2.Select(p=>p+Vector2.one*.001f).ToArray();
            var levels=new[] {Level(1,seed),Level(0,fine)};
            foreach(var input in levels.SelectMany(l=>l.inputs)) input.usePreparedUv2=true;
            Vector2[] baseline=null;
            foreach(int resolution in new[] {128,1024})
            {
                var options=Options();options.seedResolution=resolution;options.seedDensity=32;
                options.regroupPreparedAtlas=options.repackFinalAtlas=true;options.fillFinalAtlas=false;
                using var result=Build(options,levels);
                Assert.AreEqual(2,result.report.inheritedFaces);
                Assert.AreEqual(32,result.report.texelsPerUnit,.001f);
                var uv=result.meshes[1][0].uv2;
                if(baseline!=null) CollectionAssert.AreEqual(baseline,uv);
                baseline=uv;
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void WholeShellCorrespondenceMovesASeparateOrdinaryIslandInBothOverlapModes(bool overlap)
        {
            var seed=Quad();var fine=Quad();
            fine.uv2=fine.uv2.Select(p=>p*.5f+new Vector2(.5f,.05f)).ToArray();
            var before=TransferMeshSnapshot.Capture(fine);
            var levels=new[] {Level(1,seed),Level(0,fine)};
            foreach(var input in levels.SelectMany(l=>l.inputs)) input.usePreparedUv2=true;
            var options=Options(overlap);options.regroupPreparedAtlas=options.repackFinalAtlas=true;
            using var result=Build(options,levels);
            Assert.AreEqual(2,result.report.nodes.Single(n=>n.lod==0).inheritedFaces,
                "The overlap switch must not disable ordinary whole-shell inheritance.");
            Assert.AreEqual(0,result.report.overlapFaces);
            var parent=result.meshes[0][0].uv2;var child=result.meshes[1][0].uv2;
            for(int i=0;i<parent.Length;++i)
                Assert.LessOrEqual(Vector2.Distance(parent[i],child[i])*result.report.atlasSize,.01,
                    "The complete shell must inherit the donor at bake resolution.");
            Assert.AreEqual(1,UvShellExtractor.Extract(result.meshes[1][0].uv2,result.meshes[1][0].triangles).Count);
            Assert.AreEqual(2,result.meshes[1][0].triangles.Length/3);
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(fine));
        }

        [TestCase(false)] [TestCase(true)]
        public void RepairedZeroUvSeedIsTheActualDonorForTheNextLod(bool overlap)
        {
            var seed=Quad();seed.uv2=new Vector2[seed.vertexCount];var fine=Quad();
            var snapshots=new[] {TransferMeshSnapshot.Capture(seed),TransferMeshSnapshot.Capture(fine)};
            var levels=new[] {Level(1,seed),Level(0,fine)};
            foreach(var input in levels.SelectMany(l=>l.inputs)) input.usePreparedUv2=true;
            var options=Options(overlap);options.regroupPreparedAtlas=options.repackFinalAtlas=true;
            using var result=Build(options,levels);
            Assert.AreEqual(2,result.report.nodes.Single(n=>n.lod==0).inheritedFaces);
            Assert.AreEqual(1,result.report.finalPackingFamilies,"Repair and descendant are one packing item.");
            var parent=result.meshes[0][0].uv2;var child=result.meshes[1][0].uv2;
            for(int i=0;i<parent.Length;++i) Assert.LessOrEqual(Vector2.Distance(parent[i],child[i])*result.report.atlasSize,.01);
            foreach(var mesh in result.meshes.SelectMany(l=>l))
            {
                Assert.AreEqual(2,mesh.triangles.Length/3);
                Assert.AreEqual(1,UvShellExtractor.Extract(mesh.uv2,mesh.triangles).Count);
            }
            CollectionAssert.AreEqual(snapshots[0],TransferMeshSnapshot.Capture(seed));
            CollectionAssert.AreEqual(snapshots[1],TransferMeshSnapshot.Capture(fine));
        }

        [TestCase(false)] [TestCase(true)]
        public void PreparedLod1FoldRepairKeepsTheConnectedQuadAndItsOriginalFaces(bool overlap)
        {
            var coarse=Quad();var fine=Quad();
            // One folded triangle shares the diagonal with the other triangle.
            // The old face rescue emitted two independently packed triangles.
            coarse.uv2=new[] {new Vector2(.25f,.25f),new Vector2(.75f,.25f),
                new Vector2(.75f,.75f),new Vector2(.65f,.30f)};
            var snapshots=new[] {TransferMeshSnapshot.Capture(coarse),TransferMeshSnapshot.Capture(fine)};
            var levels=new[] {Level(1,coarse),Level(0,fine)};
            foreach(var input in levels.SelectMany(l=>l.inputs)) input.usePreparedUv2=true;
            var options=Options(overlap);options.regroupPreparedAtlas=options.repackFinalAtlas=true;
            using var result=Build(options,levels);
            var repaired=result.meshes[0][0];var node=result.report.nodes.Single(n=>n.lod==1);
            Assert.AreEqual(2,repaired.triangles.Length/3);Assert.AreEqual(0,node.splitSourceFaces);
            CollectionAssert.AreEqual(new[] {0,1},node.sourceFaces);
            Assert.AreEqual(1,UvShellExtractor.Extract(repaired.uv2,repaired.triangles).Count,
                "A repaired flat shell must remain a quad, not isolated triangle islands.");
            Assert.Less(TransferUvQuality.Measure(repaired,repaired.uv2,Vector2.one,Matrix4x4.identity).worstAnisotropy,1.001);
            CollectionAssert.AreEqual(snapshots[0],TransferMeshSnapshot.Capture(coarse));
            CollectionAssert.AreEqual(snapshots[1],TransferMeshSnapshot.Capture(fine));
        }

        [Test]
        public void ThinFaceRepairMovesTheSharedCornerWithoutDetachingItsNeighbour()
        {
            var a=Vector3.zero;var b=new Vector3(.25f,1e-6f,0);var c=Vector3.right;var d=new Vector3(.4f,1,0);
            var corners=new[] {a,b,c,b,d,c};
            var uv=new[] {Vector2.zero,new Vector2(.25f,.01f),Vector2.right,new Vector2(.25f,.01f),new Vector2(.4f,1),Vector2.right};
            var snapshot=(Vector2[])uv.Clone();
            var frames=Enumerable.Range(0,2).Select(f=>ReverseUvTransfer.TriangleFrame(corners[f*3],corners[f*3+1],corners[f*3+2])).ToArray();
            var repaired=ReverseUvTransfer.RepairPreparedSharedCorners(corners,uv,frames,4,default);
            Assert.AreEqual(repaired[1],repaired[3]);Assert.AreEqual(repaired[2],repaired[5]);
            for(int f=0;f<2;++f) Assert.LessOrEqual(ReverseUvTransfer.TriangleAnisotropy(frames[f],repaired[f*3],repaired[f*3+1],repaired[f*3+2]),4);
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=repaired,indices=Enumerable.Range(0,6).ToArray(),charts=new int[6]},default);
            Assert.AreEqual(0,scan.pairs);Assert.AreEqual(0,scan.degenerateFaces);
            CollectionAssert.AreEqual(snapshot,uv);
        }

        [TestCase(false)] [TestCase(true)]
        public void ClosedPreparedShellRepairPreservesFacesAndUsesConnectedNativeCharts(bool overlap)
        {
            var primitive=GameObject.CreatePrimitive(PrimitiveType.Cube);Mesh cube;
            try {cube=Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);owned.Add(cube);}
            finally {Object.DestroyImmediate(primitive);}
            cube.uv2=new Vector2[cube.vertexCount];var snapshot=TransferMeshSnapshot.Capture(cube);
            var levels=new[] {Level(1,Quad(-3)),Level(0,cube)};
            foreach(var input in levels.SelectMany(l=>l.inputs)) input.usePreparedUv2=true;
            var options=Options(overlap);options.regroupPreparedAtlas=options.repackFinalAtlas=true;
            try
            {
                using var result=Build(options,levels);var output=result.meshes[1][0];
                Assert.AreEqual(12,output.triangles.Length/3);
                Assert.AreEqual(0,result.report.nodes.Single(n=>n.lod==0).splitSourceFaces);
                Assert.Less(UvShellExtractor.Extract(output.uv2,output.triangles).Count,12,
                    "Native shell repair must retain connected patches instead of twelve rescue triangles.");
                CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(cube));
            }
            catch(DllNotFoundException) {Assert.Ignore("Native xatlas is unavailable on this test host.");}
            catch(EntryPointNotFoundException) {Assert.Ignore("Native xatlas chart parameterization is unavailable on this test host.");}
        }

        [TestCase(false)] [TestCase(true)]
        public void RegroupingOrdinaryTransferKeepsPartiallyMatchedIslandIntact(bool overlap)
        {
            var seed=Quad();seed.triangles=new[] {0,1,2};var fine=Quad();
            var levels=new[] {Level(1,seed),Level(0,fine)};
            foreach(var input in levels.SelectMany(l=>l.inputs)) input.usePreparedUv2=true;
            var options=Options(overlap);options.regroupPreparedAtlas=options.repackFinalAtlas=true;
            var snapshot=TransferMeshSnapshot.Capture(fine);
            using var result=Build(options,levels);
            var node=result.report.nodes.Single(n=>n.lod==0);var output=result.meshes[1][0];
            Assert.AreEqual(1,node.inheritedFaces,"The matched half is recorded without cutting off the unmatched half.");
            Assert.AreEqual(2,node.faces.Length);Assert.AreEqual(0,node.splitSourceFaces);
            Assert.AreEqual(1,UvShellExtractor.Extract(output.uv2,output.triangles).Count);
            Assert.Less(TransferUvQuality.Measure(output,output.uv2,Vector2.one,Matrix4x4.identity).worstAnisotropy,1.001);
            CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(fine));
        }

        [TestCase(false)] [TestCase(true)]
        public void RegroupingMovesTheWholeDetailOntoItsUnderlayOnlyWhenOverlapIsAllowed(bool overlap)
        {
            var seed=Quad();var fine=Quad();var detail=Quad(.25f,.02f,.25f);
            detail.uv2=detail.uv.Select(p=>p*.05f+Vector2.one*.05f).ToArray();
            var levels=new[] {Level(1,seed),Level(0,fine,detail)};
            foreach(var input in levels.SelectMany(l=>l.inputs)) input.usePreparedUv2=true;
            var options=Options(overlap);options.regroupPreparedAtlas=options.repackFinalAtlas=true;
            using var result=Build(options,levels);
            var node=result.report.nodes.Single(n=>n.lod==0 && n.key==detail.name+1);
            Assert.AreEqual(overlap?2:0,node.overlapFaces);
            Assert.AreEqual(0,node.splitSourceFaces);Assert.AreEqual(2,node.faces.Length);
            Assert.DoesNotThrow(()=>ReverseUvAudit.Write(result,levels));
        }

        [TestCase(false,false)] [TestCase(false,true)] [TestCase(true,false)] [TestCase(true,true)]
        public void ExistingSeedRepackingMovesDonorsAndRetainsProjectedDetailAncestry(bool overlap,bool fillAtlas)
        {
            var seed=Combine(Quad(),Quad(3));
            seed.uv2=Enumerable.Range(0,seed.vertexCount).Select(i=>seed.uv[i]*.15f
                +Vector2.one*(i<4?.05f:.75f)).ToArray();
            var fine=Combine(seed,Quad(.25f,.02f,.25f),Quad(6));
            var seedSnapshot=TransferMeshSnapshot.Capture(seed);var fineSnapshot=TransferMeshSnapshot.Capture(fine);
            var options=Options(overlap);options.repackFinalAtlas=true;options.fillFinalAtlas=fillAtlas;
            using var state=ReverseUvJunctionTrial.Build(new[] {Level(1,seed),Level(0,fine)},options,false,0).GetAwaiter().GetResult();
            var result=state.result;var report=result.report;
            Assert.AreEqual("existing-uv2",report.seedPreparation);
            Assert.AreEqual("shared-chart-families",report.finalPacking,report.finalPackingRefusal);
            Assert.IsTrue(string.IsNullOrEmpty(report.finalPackingRefusal));
            Assert.GreaterOrEqual(report.inheritedFaces,4);
            Assert.AreEqual(overlap,report.overlapFaces>0);
            Assert.GreaterOrEqual(report.newFaces,2);
            var donor=result.meshes[0][0];var target=result.meshes[1][0];
            Assert.IsTrue(donor.vertices.Select((p,i)=>
                (donor.uv2[i]-seed.uv2[Array.IndexOf(seed.vertices,p)]).magnitude).Any(d=>d>.03f),
                "Final packing must move the existing seed as well as allocate new charts.");
            foreach(var vertex in seed.vertices)
            {
                var a=donor.uv2[Array.IndexOf(donor.vertices,vertex)];
                var b=target.uv2[Array.IndexOf(target.vertices,vertex)];
                Assert.Less((a-b).magnitude,.0001f,"Inherited recipient corners follow their donor's final move.");
            }
            Assert.DoesNotThrow(()=>ReverseUvAudit.Write(result,state.inputs.levels));
            CollectionAssert.AreEqual(seedSnapshot,TransferMeshSnapshot.Capture(seed));
            CollectionAssert.AreEqual(fineSnapshot,TransferMeshSnapshot.Capture(fine));
        }

        [Test]
        public void RequestedFinalPackingFailureCannotPublishTheIntermediateAtlas()
        {
            var seed=Quad();var fine=Quad();var before=TransferMeshSnapshot.Capture(seed);
            var options=Options();options.padding=64;options.maxAtlasSize=128;options.repackFinalAtlas=true;
            var error=Assert.Throws<InvalidOperationException>(()=>Build(options,Level(1,seed),Level(0,fine)));
            StringAssert.Contains("final atlas packing failed; no result was published",error.Message);
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(seed));
        }

        [TestCase(false)] [TestCase(true)]
        public void NewCoincidentRendererSurfacesRetainIndependentTopologyAndPadding(bool overlap)
        {
            var seed=Quad(); var first=Quad(3); var second=Quad(3);
            var savedFirst=TransferMeshSnapshot.Capture(first); var savedSecond=TransferMeshSnapshot.Capture(second);
            var options=Options(overlap); options.splitDonorSeams=false;
            using var result=Build(options,Level(1,seed),Level(0,first,second));
            Assert.AreEqual(4,result.report.newFaces);
            Assert.AreEqual(2,result.report.nodes[1].newFaces); Assert.AreEqual(2,result.report.nodes[2].newFaces);
            Assert.AreEqual(0,result.report.nodes[1].localFallbackFaces); Assert.AreEqual(0,result.report.nodes[2].localFallbackFaces);
            foreach (var mesh in result.meshes[1]) Clean(mesh);
            var combined=Combine(result.meshes[1]);
            var uv=combined.uv2; var triangles=combined.triangles;
            var labels=Enumerable.Range(0,uv.Length).Select(i=>i<result.meshes[1][0].vertexCount ? 0 : 1).ToArray();
            var atlas=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=uv,indices=triangles,charts=labels},default);
            Assert.IsTrue(atlas.complete); Assert.AreEqual(0,atlas.pairs); Assert.AreEqual(0,atlas.degenerateFaces);
            float padding=(options.padding-.01f)/result.report.atlasSize;
            var a=result.meshes[1][0]; var b=result.meshes[1][1];
            // Unity can split a planar quad into two packed charts. Their boxes
            // may interleave even though each pair retains the pixel gutter.
            for (int f=0;f<a.triangles.Length;f+=3)
                for (int g=0;g<b.triangles.Length;g+=3) {
                    var auv=Enumerable.Range(0,3).Select(k=>a.uv2[a.triangles[f+k]]).ToArray();
                    var buv=Enumerable.Range(0,3).Select(k=>b.uv2[b.triangles[g+k]]).ToArray();
                    var amin=auv.Aggregate(Vector2.Min); var amax=auv.Aggregate(Vector2.Max);
                    var bmin=buv.Aggregate(Vector2.Min); var bmax=buv.Aggregate(Vector2.Max);
                    Assert.IsTrue(bmin.x-amax.x>=padding || amin.x-bmax.x>=padding || bmin.y-amax.y>=padding || amin.y-bmax.y>=padding,
                        $"Faces {f/3}/{g/3}: A {amin}/{amax}; B {bmin}/{bmax}; padding {padding}");
                }
            CollectionAssert.AreEqual(savedFirst,TransferMeshSnapshot.Capture(first));
            CollectionAssert.AreEqual(savedSecond,TransferMeshSnapshot.Capture(second));
        }

        [Test]
        public void PreparedSeedKeepsCoincidentInputNodesSeparateAndPadded()
        {
            var source=Quad(); var snapshot=TransferMeshSnapshot.Capture(source);
            var inputs=Level(1,source,source).inputs;
            var meshes=ReverseUvSeed.Prepare(inputs,64,2,8,default,out int side);
            try
            {
                var a=meshes[0]; var b=meshes[1];
                for(int f=0;f<a.triangles.Length;f+=3)
                    for(int g=0;g<b.triangles.Length;g+=3)
                    {
                        var auv=Enumerable.Range(0,3).Select(k=>a.uv2[a.triangles[f+k]]*side).ToArray();
                        var buv=Enumerable.Range(0,3).Select(k=>b.uv2[b.triangles[g+k]]*side).ToArray();
                        var amin=auv.Aggregate(Vector2.Min);var amax=auv.Aggregate(Vector2.Max);
                        var bmin=buv.Aggregate(Vector2.Min);var bmax=buv.Aggregate(Vector2.Max);
                        Assert.IsTrue(bmin.x-amax.x>=2 || amin.x-bmax.x>=2 || bmin.y-amax.y>=2 || amin.y-bmax.y>=2,
                            "Independent seed renderers need distinct padded charts, even when sharing one Mesh asset.");
                    }
                CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(source));
            }
            finally {foreach(var mesh in meshes) Object.DestroyImmediate(mesh);}
        }

        [Test]
        public void MissingRefinementPiecesStopProtectionAndRetainTheBaseline()
        {
            using var before=new ReverseUvTransfer.Result {report=new ReverseUvTransfer.Report()};
            using var after=new ReverseUvTransfer.Result {report=new ReverseUvTransfer.Report()};
            before.report.nodes.Add(new ReverseUvTransfer.NodeReport {faces=new[] {new ReverseUvTransfer.Face {inherited=true}},sourceFaces=new[] {23}});
            after.report.nodes.Add(new ReverseUvTransfer.NodeReport {faces=Array.Empty<ReverseUvTransfer.Face>(),sourceFaces=Array.Empty<int>()});
            var trial=Options();
            var protect=typeof(ReverseUvTransfer).GetMethod("ProtectDonorAncestors",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
            Assert.IsFalse((bool)protect.Invoke(null,new object[] {before,after,trial,default(CancellationToken)}));
            Assert.IsNull(trial.seamCutExclusions);
        }

        [Test]
        public void CleanupPreservesSourceAttributesAndMaterialSlotsAndTracksFaceIdentity()
        {
            var source = Quad();
            source.subMeshCount = 3;
            source.SetTriangles(new[] { 0, 0, 1, 0, 1, 2 }, 0);
            source.SetTriangles(new[] { 1, 1, 2 }, 1);
            source.SetTriangles(new[] { 0, 2, 3 }, 2);
            var delta = Enumerable.Repeat(Vector3.up, 4).ToArray();
            source.AddBlendShapeFrame("move", 100, delta, delta, delta);
            source.bindposes = new[] { Matrix4x4.identity };
            source.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            var before = source.triangles;
            var prepared = ReverseUvInputs.Prepare(new[] { Level(1, source), Level(0, source) });
            var input = prepared.levels[0].inputs[0]; var copy = input.mesh;
            Assert.AreNotSame(source, copy);
            CollectionAssert.AreEqual(before, source.triangles);
            CollectionAssert.AreEqual(new[] { 1, 3 }, input.sourceFaces);
            CollectionAssert.AreEqual(new[] { 0, 2 }, input.removedSourceFaces);
            Assert.AreEqual(3, copy.subMeshCount); Assert.IsEmpty(copy.GetTriangles(1));
            CollectionAssert.AreEqual(source.vertices, copy.vertices);
            CollectionAssert.AreEqual(source.uv, copy.uv); CollectionAssert.AreEqual(source.uv2, copy.uv2);
            CollectionAssert.AreEqual(source.normals, copy.normals);
            CollectionAssert.AreEqual(source.boneWeights, copy.boneWeights);
            CollectionAssert.AreEqual(source.bindposes, copy.bindposes);
            Assert.AreEqual(1, copy.blendShapeCount);
            var actual = new Vector3[4]; copy.GetBlendShapeFrameVertices(0, 0, actual, null, null);
            CollectionAssert.AreEqual(delta, actual);
            using (prepared)
            using (var result = Build(Options(), prepared.levels))
            {
                CollectionAssert.AreEqual(input.sourceFaces, result.report.nodes[0].sourceFaces);
                CollectionAssert.AreEqual(input.removedSourceFaces, result.report.nodes[0].removedSourceFaces);
                Assert.AreEqual(2, result.meshes[0][0].triangles.Length / 3);
            }
            Assert.IsTrue(copy == null); Assert.IsTrue(source);
        }

        [TestCase(false)] [TestCase(true)]
        public void CleanupPreservesUInt16SubmeshBaseVertices(bool unreadable)
        {
            var positions = new Vector3[100003];
            positions[100001] = Vector3.right; positions[100002] = Vector3.up;
            var source = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt16, vertices = positions };
            source.subMeshCount = 2;
            source.SetTriangles(new[] { 0,1,2, 0,0,1 }, 0, false, 100000);
            source.SetTriangles(new[] { 0,0,1 }, 1, false, 100000);
            var before = source.GetTriangles(0, false);
            if (unreadable) source.UploadMeshData(true);
            try
            {
                using var prepared = ReverseUvInputs.Prepare(new[] { Level(1, source) });
                var input = prepared.levels[0].inputs[0]; var copy = input.mesh;
                Assert.AreEqual(UnityEngine.Rendering.IndexFormat.UInt16, copy.indexFormat);
                Assert.AreEqual(100000, copy.GetBaseVertex(0));
                CollectionAssert.AreEqual(new[] { 0,1,2 }, copy.GetTriangles(0, false));
                CollectionAssert.AreEqual(new[] { 100000,100001,100002 }, copy.GetTriangles(0));
                Assert.IsEmpty(copy.GetTriangles(1));
                CollectionAssert.AreEqual(new[] { 0 }, input.sourceFaces);
                CollectionAssert.AreEqual(new[] { 1,2 }, input.removedSourceFaces);
                CollectionAssert.AreEqual(positions, copy.vertices);
                if (!unreadable) CollectionAssert.AreEqual(before, source.GetTriangles(0, false));
                Assert.AreEqual(!unreadable, source.isReadable);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        [Test]
        public void CleanupKeepsKamazThinFaceAndRemovesOnlyItsCollinearControl()
        {
            var mesh = Quad();
            mesh.vertices = new[] {
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.8186256885528564f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.8186254501342773f),
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.8186254501342773f),
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.723873257637024f) };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            using var prepared = ReverseUvInputs.Prepare(new[] { Level(1, mesh) });
            CollectionAssert.AreEqual(new[] { 0 }, prepared.levels[0].inputs[0].sourceFaces);
            CollectionAssert.AreEqual(new[] { 1 }, prepared.levels[0].inputs[0].removedSourceFaces);
            Assert.AreEqual(6, mesh.triangles.Length);
        }

        [Test]
        public void ReadWriteDisabledStaticInputsPreserveStreamsAndCompleteReverseProjection()
        {
            var source = Quad();
            source.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 0, 0 };
            source.colors = Enumerable.Repeat(new Color(.2f, .4f, .6f, .25f), 4).ToArray();
            source.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 4).ToArray();
            var uv4 = Enumerable.Repeat(new Vector4(.1f, .2f, .3f, .4f), 4).ToList(); source.SetUVs(2, uv4);
            var before = TransferMeshSnapshot.Capture(source); var colors = source.colors; var tangents = source.tangents;
            source.UploadMeshData(true); Assert.IsFalse(source.isReadable);
            Mesh copy;
            using (var prepared = ReverseUvInputs.Prepare(new[] { Level(1, source), Level(0, source) })) {
                copy = prepared.levels[1].inputs[0].mesh;
                Assert.AreNotSame(source, copy); Assert.IsTrue(copy.isReadable);
                CollectionAssert.AreEqual(colors, copy.colors); CollectionAssert.AreEqual(tangents, copy.tangents);
                var actualUv = new List<Vector4>(); copy.GetUVs(2, actualUv); CollectionAssert.AreEqual(uv4, actualUv);
                CollectionAssert.AreEqual(new[] { 0, 1 }, prepared.levels[1].inputs[0].sourceFaces);
                CollectionAssert.AreEqual(new[] { 2 }, prepared.levels[1].inputs[0].removedSourceFaces);
                int size = prepared.PrepareSeed(128, 2, 0, default);
                using var result = Build(new ReverseUvTransfer.Options { seedResolution = size, projectionReach = .1f }, prepared.levels);
                Assert.AreEqual(2, result.report.inheritedFaces); Assert.AreEqual(0, result.report.newFaces);
                Assert.AreEqual(2, result.meshes[1][0].triangles.Length / 3);
            }
            Assert.IsTrue(copy == null); Assert.IsFalse(source.isReadable); Assert.AreEqual(9, source.GetIndexCount(0));
            var witness = MeshAccess.Readable(source, out bool ownsWitness);
            try { CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(witness)); }
            finally { if (ownsWitness) Object.DestroyImmediate(witness); }
        }

        [Test]
        public void UnreadableCleanupFailureReleasesEarlierDetachedCopies()
        {
            var good = Quad(); var empty = Quad(); empty.triangles = new[] { 0, 0, 0 };
            good.UploadMeshData(true); empty.UploadMeshData(true);
            int count = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            for (int attempt = 0; attempt < 3; attempt++) {
                Assert.Throws<InvalidOperationException>(() => ReverseUvInputs.Prepare(new[] { Level(1, good, empty) }));
                Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Mesh>().Length);
                Assert.IsFalse(good.isReadable); Assert.IsFalse(empty.isReadable);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void UnreadableSkinOrBlendShapeRefusesInsteadOfLosingAttributes(bool blend)
        {
            var source = Quad();
            if (blend) {
                var delta = Enumerable.Repeat(Vector3.up, 4).ToArray(); source.AddBlendShapeFrame("move", 100, delta, delta, delta);
            } else {
                source.bindposes = new[] { Matrix4x4.identity };
                source.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            }
            source.UploadMeshData(true); int count = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var error = Assert.Throws<InvalidOperationException>(() => ReverseUvInputs.Prepare(new[] { Level(1, source) }));
            StringAssert.Contains("skinning or blend shapes", error.Message);
            Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Mesh>().Length); Assert.IsFalse(source.isReadable);
        }

        [Test]
        public void CleanupRejectsNonfiniteAndEmptyGeometryAndHonoursCancellation()
        {
            var mesh = Quad(); var before = mesh.triangles;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => ReverseUvInputs.Prepare(new[] { Level(1, mesh) }, canceled.Token));
            CollectionAssert.AreEqual(before, mesh.triangles);
            mesh.triangles = new[] { 0, 0, 0 };
            Assert.Throws<InvalidOperationException>(() => ReverseUvInputs.Prepare(new[] { Level(1, mesh) }));
            mesh.vertices = new[] { Vector3.zero, new Vector3(float.NaN, 0, 0), Vector3.up };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, false);
            Assert.Throws<InvalidOperationException>(() => ReverseUvInputs.Prepare(new[] { Level(1, mesh) }));
        }

        [Serializable] sealed class KamazCapture
        {
            public string kind, key;
            public Vector3[] positions;
            public int[] indices;
            public Matrix4x4 toWorld;
        }
        [Serializable] sealed class KamazTrial
        {
            public int frame, removedFaces;
            public bool overlap, accepted;
            public string stage, error, audit;
            public int[] removedByLod;
        }
        [Serializable] sealed class KamazTrials { public List<KamazTrial> trials = new List<KamazTrial>(); }

        [UnityTest, Timeout(600000)]
        public IEnumerator FrozenKamazFullLodChainAfterCleanup()
        {
            string directory = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_KAMAZ");
            if (string.IsNullOrEmpty(directory)) Assert.Ignore("Set MESHLAB_REVERSE_KAMAZ to the readonly Kamaz captures.");
            string output = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_OUTPUT");
            Assert.IsNotEmpty(output); System.IO.Directory.CreateDirectory(output);
            int resolution = int.TryParse(Environment.GetEnvironmentVariable("MESHLAB_REVERSE_KAMAZ_RESOLUTION"), out int configuredResolution) ? configuredResolution : 256;
            float density = float.TryParse(Environment.GetEnvironmentVariable("MESHLAB_REVERSE_KAMAZ_DENSITY"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float configuredDensity) ? configuredDensity : 0;
            int[] frames = int.TryParse(Environment.GetEnvironmentVariable("MESHLAB_REVERSE_KAMAZ_FRAME"), out int configuredFrame)
                ? new[] { configuredFrame } : Enumerable.Range(0, 4).ToArray();
            Assert.IsTrue(frames.All(f => f >= 0 && f < 4));
            bool workflow = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_KAMAZ_WORKFLOW") == "1";
            var trials = new KamazTrials();
            var previousOutput = BenchmarkRecorder.OutputDirectoryOverride;
            BenchmarkRecorder.OutputDirectoryOverride = output;
            try
            {
                foreach (int frame in frames)
                {
                    var captures = new List<KamazCapture>();
                    for (int file = frame * 15; file < (frame == 3 ? 61 : (frame + 1) * 15); ++file)
                    {
                        var capture = JsonUtility.FromJson<KamazCapture>(System.IO.File.ReadAllText(System.IO.Path.Combine(directory, file + ".json")));
                        if (capture.key.Contains("_LOD")) captures.Add(capture);
                    }
                    var levels = captures.GroupBy(c => int.Parse(c.key.Substring(c.key.LastIndexOf("_LOD", StringComparison.Ordinal) + 4)))
                        .OrderByDescending(g => g.Key).Select(g => new ReverseUvTransfer.Level { lod = g.Key,
                            inputs = g.OrderBy(c => c.key).Select(c => {
                                var mesh = new Mesh { name = c.key, indexFormat = IndexFormat.UInt32 };
                                mesh.vertices = c.positions; mesh.triangles = c.indices; owned.Add(mesh);
                                return new ReverseUvTransfer.Input { mesh = mesh, key = c.key, toWorld = c.toWorld };
                            }).ToArray() }).ToArray();
                    Assert.AreEqual(3, levels.Length); Assert.IsTrue(levels.All(l => l.inputs.Length == 5));
                    var originals = levels.SelectMany(l => l.inputs).Select(i => TransferMeshSnapshot.Capture(i.mesh)).ToArray();
                    foreach (bool overlap in new[] { false, true })
                    {
                        var trial = new KamazTrial { frame = frame, overlap = overlap, stage = "cleanup" };
                        trials.trials.Add(trial);
                        using var prepared = ReverseUvInputs.Prepare(levels);
                        trial.removedByLod = prepared.levels.Select(l => l.inputs.Sum(i => i.removedSourceFaces.Length)).ToArray();
                        trial.removedFaces = trial.removedByLod.Sum();
                        Assert.Greater(trial.removedFaces, 0);
                        for (int l = 0; l < levels.Length; ++l)
                            for (int n = 0; n < levels[l].inputs.Length; ++n)
                            {
                                var input = prepared.levels[l].inputs[n];
                                Assert.AreEqual(levels[l].inputs[n].mesh.triangles.Length / 3,
                                    input.sourceFaces.Length + input.removedSourceFaces.Length);
                            }
                        if (workflow)
                        {
                            trial.stage = "workflow";
                            var layout = ReverseUvJunctionTrial.Build(levels, new ReverseUvTransfer.Options {boundaryToleranceTexels=BoundaryTolerance(),
                                seedResolution = resolution, preserveProjectedOverlap = overlap, projectionReach = .05f,
                                cutNarrowJunctions = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_JUNCTION_CUTS") == "1",
                                splitDonorSeams = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_JUNCTION_CUTS") == "1",
                                fillFinalAtlas = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_FILL_FINAL") == "1",
                                repackFinalAtlas = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_REPACK_FINAL") == "1" }, true, density, true);
                            while (!layout.IsCompleted) yield return null;
                            if (layout.IsFaulted) { trial.error = layout.Exception.GetBaseException().Message; continue; }
                            using var state = layout.Result;
                            trial.stage = "audit";
                            try {
                                TransferBenchmarkExport.Reverse(System.IO.Path.Combine(output, "kamaz-frame" + frame + "-" + (overlap ? "overlap" : "exclusive")), state.result, state.inputs.levels);
                                trial.audit = ReverseUvAudit.Write(state.result, state.inputs.levels);
                                if(Environment.GetEnvironmentVariable("MESHLAB_REVERSE_REPACK_FINAL")=="1")
                                {
                                    Assert.AreEqual("shared-chart-families",state.result.report.finalPacking,state.result.report.finalPackingRefusal);
                                    Assert.Greater(state.result.report.finalPackingFamilies,0);
                                    Assert.GreaterOrEqual(state.result.report.texelsPerUnit,density*(1-state.result.report.relativeStretchTolerance));
                                }
                                trial.accepted = true;
                            }
                            catch (Exception ex) { trial.error = ex.Message; }
                            continue;
                        }
                        trial.stage = "seed";
                        int size = 0;
                        try { size = prepared.PrepareSeed(resolution, 2, density, default); }
                        catch (Exception ex) { trial.error = ex.Message; }
                        if (trial.error != null) continue;
                        trial.stage = "projection";
                        var task = ReverseUvTransfer.Build(prepared.levels, new ReverseUvTransfer.Options {boundaryToleranceTexels=BoundaryTolerance(),
                            seedResolution = size, preserveProjectedOverlap = overlap, projectionReach = .05f,
                            seedDensity = density > 0 ? prepared.seedDensity : 0 }, true);
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { trial.error = task.Exception.GetBaseException().Message; continue; }
                        using var result = task.Result;
                        trial.stage = "audit";
                        try { trial.audit = ReverseUvAudit.Write(result, prepared.levels); trial.accepted = true; }
                        catch (Exception ex) { trial.error = ex.Message; }
                    }
                    int source = 0;
                    foreach (var input in levels.SelectMany(l => l.inputs))
                        CollectionAssert.AreEqual(originals[source++], TransferMeshSnapshot.Capture(input.mesh));
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(output, "kamaz-trials.json"), JsonUtility.ToJson(trials, true));
            }
            finally { BenchmarkRecorder.OutputDirectoryOverride = previousOutput; }
            Assert.AreEqual(frames.Length * 2, trials.trials.Count);
            string required = Environment.GetEnvironmentVariable("MESHLAB_REVERSE_KAMAZ_REQUIRED_FRAMES");
            if (!string.IsNullOrEmpty(required))
                foreach (int frame in required.Split(',').Select(int.Parse))
                {
                    var selected = trials.trials.Where(t => t.frame == frame).ToArray();
                    Assert.AreEqual(2, selected.Length);
                    Assert.IsTrue(selected.All(t => t.accepted), string.Join("\n", selected.Select(t => t.error)));
                }
        }

        static void Clean(Mesh mesh, bool overlaps = false)
        {
            var q = TransferUvQuality.Measure(mesh, mesh.uv2, Vector2.one, Matrix4x4.identity);
            Assert.AreEqual(0, q.invalidFaces); Assert.AreEqual(0, q.degenerateFaces); Assert.AreEqual(0, q.outOfBoundsVertices);
            Assert.LessOrEqual(q.worstAnisotropy, 4.001); Assert.IsTrue(q.overlapScanComplete);
            if (!overlaps) Assert.AreEqual(0, q.overlapPairs);
        }

        [Test] public void FragmentedUv0InheritsOneContinuousParentChart()
        {
            var seed = Quad(); var a = Quad(0, 0, .5f); var b = Quad(.5f, 0, .5f);
            // Disconnected UV0 layouts and separate renderer inputs cannot influence surface projection.
            a.uv = a.uv.Select(p => p * 7 + Vector2.one * 12).ToArray();
            b.uv = b.uv.Select(p => -p * 3).ToArray();
            var beforeA = a.uv; var beforeSeed = seed.uv2;
            using var result = Build(Options(), Level(1, seed), Level(0, a, b));
            Assert.AreEqual(4, result.report.inheritedFaces); Assert.AreEqual(0, result.report.newFaces);
            Assert.AreEqual(128, result.report.atlasSize);
            foreach (var output in result.meshes[1])
            {
                Clean(output);
                for (int i = 0; i < output.vertexCount; ++i)
                    Assert.Less(Vector2.Distance(output.uv2[i], new Vector2(output.vertices[i].x, output.vertices[i].y) * .5f + Vector2.one * .25f), 1e-6f);
            }
            CollectionAssert.AreEqual(beforeA, a.uv); CollectionAssert.AreEqual(beforeSeed, seed.uv2);
            Assert.AreEqual(result.report.nodes[1].faces[0].chart, result.report.nodes[2].faces[0].chart);
        }

        [Test] public void ChainAppendsNewSurfacesAndNormalizesAllLodsTogether()
        {
            var seed = Quad(); var middle = Combine(Quad(), Quad(3)); var fine = Combine(Quad(), Quad(3), Quad(6));
            using var result = Build(Options(), Level(2, seed), Level(1, middle), Level(0, fine));
            Assert.AreEqual(6, result.report.inheritedFaces); Assert.AreEqual(4, result.report.newFaces);
            Assert.Greater(result.report.atlasSize, 128); Assert.AreEqual(64f, result.report.texelsPerUnit, .001f);
            var m = result.meshes[1][0]; var f = result.meshes[2][0];
            for (int i = 0; i < 8; ++i)
            {
                var position = middle.vertices[i];
                var mu = m.uv2[Array.FindIndex(m.vertices, p => p == position)];
                var fu = f.uv2[Array.FindIndex(f.vertices, p => p == position)];
                Assert.Less(Vector2.Distance(mu, fu), 1e-6f, "Inherited atlas coordinates moved.");
            }
            Assert.AreEqual(1, result.report.nodes[2].faces[2].parentLod);
            foreach (var level in result.meshes) foreach (var mesh in level) Clean(mesh);
        }

        [TestCase(false)] [TestCase(true)]
        public void RaisedDetailOverlapIsExplicitAndOptional(bool allow)
        {
            var seed = Quad(); var detail = Quad(.25f, .02f, .25f);
            using var result = Build(Options(allow), Level(1, seed), Level(0, Quad(), detail));
            var node = result.report.nodes[2];
            Assert.AreEqual(allow ? 2 : 0, node.inheritedFaces);
            Assert.AreEqual(allow ? 0 : 2, node.newFaces);
            Assert.AreEqual(allow ? 2 : 0, node.overlapFaces);
            foreach (var face in node.faces)
            {
                Assert.AreEqual(allow, face.intentionalOverlap);
                Assert.AreEqual(allow ? 1 : 0, face.layer);
                if (allow) { Assert.AreEqual(0, face.overlayMesh); Assert.AreEqual(1, face.parentLod); }
                else { Assert.AreEqual(-1, face.parentLod); Assert.AreEqual(-1, face.overlayFace); }
            }
            foreach (var mesh in result.meshes[1]) Clean(mesh);
            var json = ReverseUvAudit.Provenance(result.report);
            var saved = JsonUtility.FromJson<ReverseUvAudit.ProvenanceEntry>(json[2]);
            Assert.AreEqual(3, saved.chain.Length); Assert.AreEqual(allow, saved.IsIntentionalOverlap(0));
            Assert.AreEqual(allow, saved.overlaps.Length > 0);
        }

        [TestCase(false)] [TestCase(true)]
        public void AttachedRaisedDetailKeepsItsUnderlyingUvEvenWithSharedRim(bool overlap)
        {
            var detail = new Mesh {name="Attached detail", vertices=new[] {Vector3.zero, new Vector3(.5f,.5f,.02f),Vector3.up},
                triangles=new[] {0,1,2},uv=new[] {Vector2.zero,new Vector2(.5f,.5f),Vector2.up}};
            detail.RecalculateNormals(); owned.Add(detail);
            using var result=Build(Options(overlap),Level(1,Quad()),Level(0,Quad(),detail));
            var face=result.report.nodes[2].faces[0];
            Assert.AreEqual(overlap,face.inherited,"A raised detail attached to the underlying rim is still a valid projection.");
            Assert.AreEqual(overlap,face.intentionalOverlap);
            if(!overlap) return;
            var parent=result.meshes[1][0]; var output=result.meshes[1][1];
            var origin=parent.uv2[0]; var u=parent.uv2[1]-origin; var v=parent.uv2[3]-origin;
            for(int i=0;i<output.vertexCount;++i)
                Assert.Less((output.uv2[i]-origin-u*output.vertices[i].x-v*output.vertices[i].y).magnitude,1e-6);
        }

        [TestCase(false)] [TestCase(true)]
        public void NewlyCoveredChartDoesNotEvictEstablishedInheritance(bool overlap)
        {
            ReverseUvTransfer.Surface Triangle(int node,int chart,int layer,float depth) => new ReverseUvTransfer.Surface {
                lod=0,node=node,input=new ReverseUvTransfer.Input(),sourceFaces=new[] {0},
                positions=new[] {new Vector3(0,0,depth),new Vector3(1,0,depth),new Vector3(0,1,depth)},
                indices=new[] {0,1,2},pixels=new[] {Vector2.zero,Vector2.right,Vector2.up},
                faces=new[] {new ReverseUvTransfer.Face {inherited=true,chart=chart,layer=layer}}
            };
            var established=Triangle(0,7,0,0);var newlyCovered=Triangle(1,9,0,.02f);
            var options=Options(overlap);
            options.inheritedPriority=new HashSet<(int,int,int)> {(0,0,0)};
            typeof(ReverseUvTransfer).GetMethod("ResolveOverlaps",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)
                .Invoke(null,new object[] {new[] {established,newlyCovered},options,default(System.Threading.CancellationToken)});
            Assert.IsTrue(established.faces[0].inherited,"A donor cut must preserve the already inherited region on this donor layer.");
            Assert.IsFalse(newlyCovered.faces[0].inherited,"Unrelated donor charts cannot acquire an intentional overlap.");
        }

        [Test]
        public void OverlapRebuildDoesNotAccumulatePreviousArbitrationLayers()
        {
            ReverseUvTransfer.Surface Triangle(int node,float depth) => new ReverseUvTransfer.Surface {
                lod=0,node=node,input=new ReverseUvTransfer.Input(),sourceFaces=new[] {0},
                positions=new[] {new Vector3(0,0,depth),new Vector3(1,0,depth),new Vector3(0,1,depth)},
                indices=new[] {0,1,2},pixels=new[] {Vector2.zero,Vector2.right,Vector2.up},
                faces=new[] {new ReverseUvTransfer.Face {inherited=true,chart=7,layer=0,distance=node}}
            };
            var temporary=Triangle(0,0);var under=Triangle(1,.02f);var detail=Triangle(2,.04f);
            var resolve=typeof(ReverseUvTransfer).GetMethod("ResolveOverlaps",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            void Rebuild(ReverseUvTransfer.Surface[] surfaces)=>resolve.Invoke(null,new object[] {surfaces,Options(true),default(System.Threading.CancellationToken)});
            Rebuild(new[] {temporary,under,detail});
            Assert.AreEqual(1,under.faces[0].layer);Assert.AreEqual(2,detail.faces[0].layer);
            Rebuild(new[] {under,detail});
            Assert.AreEqual(0,under.faces[0].layer);Assert.AreEqual(1,detail.faces[0].layer);
            Assert.IsTrue(ReverseUvTransfer.IsOrderedOverlap(under.faces[0],detail.faces[0]));
        }

        [TestCase(false)] [TestCase(true)]
        public void NewStripCutsRetainInheritedOverlapGraphInTheSameMesh(bool overlap)
        {
            var points=new[] {new Vector2(0,0),new Vector2(5,0),new Vector2(5,1),new Vector2(3,1),
                new Vector2(3,5),new Vector2(2,5),new Vector2(2,1),new Vector2(0,1)};
            var strip=new Mesh {name="New T",vertices=points.Select(p=>new Vector3(p.x+3,p.y,0)).ToArray(),
                uv=points,triangles=new[] {0,1,3,1,2,3,0,3,6,0,6,7,3,4,5,3,5,6}};
            strip.RecalculateNormals();owned.Add(strip);
            var fine=Combine(Quad(),Quad(.25f,.02f,.25f),strip);
            var snapshot=TransferMeshSnapshot.Capture(fine);
            var options=Options(overlap);options.cutNarrowJunctions=true;options.splitDonorSeams=false;
            options.repackFinalAtlas=true;options.fillFinalAtlas=true;
            var levels=new[] {Level(1,Quad()),Level(0,fine)};
            using var result=Build(options,levels);
            var node=result.report.nodes[1];
            Assert.Greater(node.faces.Length,fine.triangles.Length/3,"The new T must actually acquire interior cuts.");
            Assert.AreEqual(overlap?2:0,node.overlapFaces);
            foreach(var relation in result.report.overlaps)
                Assert.IsTrue(ReverseUvTransfer.IsOrderedOverlap(node.faces[relation.underFace],node.faces[relation.overFace]),
                    "Contacts must refer to the face records after strip subdivision.");
            Assert.DoesNotThrow(()=>ReverseUvAudit.Write(result,levels));
            CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(fine));
        }

        [TestCase(false)] [TestCase(true)]
        public void NewDetailProjectsOntoNewSameLodUnderlayBeforeFinalPacking(bool overlap)
        {
            var seed=Quad(); var underlay=Quad(3); var detail=Quad(3.25f,.02f,.25f);
            var settings=Options(overlap);settings.repackFinalAtlas=true;settings.fillFinalAtlas=true;
            using var result=Build(settings,Level(1,seed),Level(0,underlay,detail));
            var node=result.report.nodes[2];
            Assert.AreEqual(overlap ? 2 : 0,node.overlapFaces);
            Assert.AreEqual(overlap ? 0 : 2,node.newFaces);
            if(!overlap) return;
            Assert.IsTrue(node.faces.All(f=>f.inherited && f.parentLod==0 && f.parentMesh==0));
            var parent=result.meshes[1][0];var output=result.meshes[1][1];
            var origin=parent.uv2[0];var u=parent.uv2[1]-origin;var v=parent.uv2[3]-origin;
            for(int i=0;i<output.vertexCount;++i)
                Assert.Less((output.uv2[i]-origin-u*(output.vertices[i].x-3)-v*output.vertices[i].y).magnitude,1e-6,
                    "Packing must move the detail and its underlay with the same rigid mapping.");
        }

        [TestCase(false)] [TestCase(true)]
        public void NewUnderlayLayersFollowGeometryWhenRendererOrderIsReversed(bool reverse)
        {
            var under=Quad(3);var middle=Quad(3.25f,.02f,.25f);var top=Quad(3.25f,.04f,.25f);
            var parts=reverse?new[] {top,middle,under}:new[] {under,middle,top};
            var saved=parts.Select(TransferMeshSnapshot.Capture).ToArray();
            using var result=Build(Options(true),Level(1,Quad()),Level(0,parts));
            var middleNode=result.report.nodes[2];var topNode=result.report.nodes[reverse?1:3];
            Assert.IsTrue(middleNode.faces.All(f=>f.inherited && f.intentionalOverlap && f.layer==1));
            Assert.IsTrue(topNode.faces.All(f=>f.inherited && f.intentionalOverlap && f.layer==2));
            Assert.AreEqual(reverse?2:0,middleNode.faces[0].parentMesh);
            Assert.AreEqual(1,topNode.faces[0].parentMesh);
            for(int i=0;i<parts.Length;++i) CollectionAssert.AreEqual(saved[i],TransferMeshSnapshot.Capture(parts[i]));
        }

        [Test]
        public void CoplanarNewRenderersAreNotInventedAsAnUnderlayRelation()
        {
            using var result=Build(Options(true),Level(1,Quad()),Level(0,Quad(3),Quad(3)));
            Assert.AreEqual(4,result.report.newFaces);Assert.AreEqual(0,result.report.overlapFaces);
            Assert.IsEmpty(result.report.overlaps);
        }

        [Test] public void OverlapLayerSurvivesTheNextProjection()
        {
            var seed = Quad(); var middle = new[] { Quad(), Quad(.25f, .02f, .25f) };
            var fine = new[] { Quad(0,.005f), Quad(.25f, .02f, .25f) };
            using var result = Build(Options(true), Level(2, seed), Level(1, middle), Level(0, fine));
            Assert.AreEqual(1, result.report.nodes[4].faces[0].layer, "Repeated LOD projection must not increment an existing layer.");
            Assert.AreEqual(1, result.report.nodes[4].faces[0].parentMesh);
            Assert.AreEqual(0, result.report.nodes[3].faces[0].layer,"A closer detail must not reorder the parent layer.");
        }

        [Test] public void StackedDetailsRetainEveryAuditedOverlapParent()
        {
            var levels = new[] {Level(1,Quad()),Level(0,Quad(),Quad(.25f,.02f,.25f),Quad(.25f,.04f,.25f))};
            using var result = Build(Options(true),levels);
            var top = result.report.nodes[3];
            Assert.IsTrue(top.faces.All(f => f.layer == 2 && f.overlayMesh == 1));
            foreach (int node in new[] {0,1})
                Assert.IsTrue(result.report.overlaps.Any(r => r.underMesh == node && r.overMesh == 2),
                    "A detail has audited edges to both the underlying plane and the intermediate detail.");
            foreach (var relation in result.report.overlaps)
            {
                var under = result.report.nodes[relation.underMesh+1].faces[relation.underFace];
                var over = result.report.nodes[relation.overMesh+1].faces[relation.overFace];
                Assert.IsTrue(ReverseUvTransfer.IsOrderedOverlap(under,over));
            }
        }

        [TestCase("missing-edge")] [TestCase("wrong-chart")]
        public void OverlapAuditRejectsAnIntentionalFlagWithoutMatchingAncestry(string defect)
        {
            var levels = new[] {Level(1,Quad()),Level(0,Quad(),Quad(.25f,.02f,.25f))};
            using var result = Build(Options(true),levels);
            Assert.IsNotEmpty(result.report.overlaps);
            if (defect == "missing-edge") result.report.overlaps.Clear();
            else foreach (var face in result.report.nodes[2].faces) face.chart += 100;
            Assert.Throws<InvalidOperationException>(() => ReverseUvAudit.Write(result,levels));
        }

        [Test] public void OrderedOverlapRequiresTheActualParentAndDonorChart()
        {
            var parent = new ReverseUvTransfer.Face {inherited=true,chart=7};
            var other = new ReverseUvTransfer.Face {inherited=true,chart=7};
            var detail = new ReverseUvTransfer.Face {inherited=true,chart=7,layer=1,intentionalOverlap=true,
                overlapParents=new HashSet<ReverseUvTransfer.Face> {parent}};
            Assert.IsTrue(ReverseUvTransfer.IsOrderedOverlap(parent,detail));
            Assert.IsFalse(ReverseUvTransfer.IsOrderedOverlap(other,detail));
            parent.chart = 8;
            Assert.IsFalse(ReverseUvTransfer.IsOrderedOverlap(parent,detail));
        }

        [TestCase(true)] [TestCase(false)]
        public void OppositeNormalOrDistantSurfaceGetsNewUv(bool opposite)
        {
            var target = Quad(opposite ? 0 : 4);
            if (opposite) target.triangles = target.triangles.Reverse().ToArray();
            using var result = Build(Options(), Level(1, Quad()), Level(0, target));
            Assert.AreEqual(0, result.report.inheritedFaces); Assert.AreEqual(2, result.report.newFaces); Clean(result.meshes[1][0]);
        }

        [Test] public void TriangleAcrossAParentUvSeamDoesNotCreateNeedles()
        {
            var seed = Quad(); var uv = seed.uv2;
            // Separate the two donor faces in UV by duplicating their shared corners.
            var source = ReverseUvMesh.Copy(seed, seed.triangles, new[] { uv[0], uv[1], uv[2], uv[0] + Vector2.right * .5f, uv[2] + Vector2.right * .5f, uv[3] + Vector2.right * .5f });
            owned.Add(source);
            // Fit both charts into one atlas; seam remains discontinuous.
            source.uv2 = source.uv2.Select(p => p * .6f).ToArray();
            var target = Quad(); target.Clear(); target.vertices = new[] { new Vector3(.1f,.1f,0), new Vector3(.9f,.1f,0), new Vector3(.1f,.9f,0) };
            target.triangles = new[] { 0, 1, 2 }; target.uv = new[] { Vector2.zero,Vector2.right,Vector2.up }; target.uv2 = target.uv;
            using var result = Build(Options(), Level(1, source), Level(0, target));
            Assert.Greater(result.report.inheritedFaces, 1); Assert.AreEqual(0, result.report.newFaces);
            Assert.Greater(result.report.splitSourceFaces, 0); Clean(result.meshes[1][0]);
        }

        [Test] public void CoincidentParentChartsAreAmbiguous()
        {
            var a = Quad(); var b = Quad();
            a.uv2 = a.uv2.Select(p => p * .4f).ToArray(); b.uv2 = b.uv2.Select(p => p * .4f + Vector2.right * .5f).ToArray();
            using var result = Build(Options(), Level(1, a, b), Level(0, Quad()));
            Assert.AreEqual(0, result.report.inheritedFaces); Assert.AreEqual(2, result.report.ambiguousFaces);
        }

        [Test] public void CoincidentLobesOfOneContinuousParentChartAreAmbiguous()
        {
            var seed = new Mesh { name = "Closed ribbon with coincident ends" }; owned.Add(seed);
            var path = new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.forward,
                Vector3.forward, Vector3.zero, Vector3.right };
            var positions = new List<Vector3>(); var uv = new List<Vector2>(); var indices = new List<int>();
            for (int i = 0; i < path.Length; ++i)
            {
                positions.Add(path[i]); positions.Add(path[i] + Vector3.up);
                uv.Add(new Vector2(.1f + i * .1f, .1f)); uv.Add(new Vector2(.1f + i * .1f, .2f));
                if (i + 1 < path.Length) indices.AddRange(new[] { i * 2, i * 2 + 2, i * 2 + 3, i * 2, i * 2 + 3, i * 2 + 1 });
            }
            seed.SetVertices(positions); seed.triangles = indices.ToArray(); seed.uv2 = uv.ToArray();
            using var result = Build(Options(), Level(1, seed), Level(0, Quad()));
            Assert.AreEqual(0, result.report.inheritedFaces); Assert.AreEqual(2, result.report.ambiguousFaces);
            Clean(result.meshes[1][0]);
        }

        [Test] public void TinyDensitySeedStillHasAValidMinimumAtlas()
        {
            var input = new ReverseUvTransfer.Input { mesh = Quad(size: .01f), key = "Tiny" };
            var meshes = ReverseUvSeed.Prepare(new[] { input }, 128, 2, 1, default, out int side);
            owned.AddRange(meshes); Assert.AreEqual(16, side);
            using var result = Build(new ReverseUvTransfer.Options { seedResolution = side }, Level(1, meshes), Level(0, input.mesh));
            Clean(result.meshes[0][0]);
        }

        [Test] public void DonorTieScanIncludesExactHitsAndHonoursBudgetAndCancellation()
        {
            var mesh = Quad(); var bvh = new TriangleBvh(mesh.vertices, mesh.triangles);
            var normals = new[] { Vector3.forward, Vector3.forward }; long comparisons = 0;
            Assert.IsTrue(bvh.AnyNormalFilteredWithin(Vector3.zero, Vector3.forward, normals,
                .5f, 0, hit => hit.triangleIndex == 1, ref comparisons, 10, default));
            comparisons = 0;
            Assert.Throws<InvalidOperationException>(() => bvh.AnyNormalFilteredWithin(Vector3.zero, Vector3.forward,
                normals, .5f, 0, hit => false, ref comparisons, 1, default));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => bvh.AnyNormalFilteredWithin(Vector3.zero, Vector3.forward,
                normals, .5f, 0, hit => false, ref comparisons, 10, cancellation.Token));
        }

        [Test] public void ReverseBenchmarkUsesSourceFrameAndRefusesMissingLegacyFrame()
        {
            var pair = new TransferCaseCapture.Pair { source = "Child", sourceMesh = "hash",
                localToWorld = Matrix4x4.Translate(Vector3.right), hasSourceTransform = true,
                sourceLocalToWorld = Matrix4x4.TRS(Vector3.up, Quaternion.Euler(0, 20, 0), Vector3.one * 2) };
            var frames = new List<TransferCaseCapture.MeshState>();
            Assert.AreEqual(pair.sourceLocalToWorld, ReverseUvBenchmark.SourceTransform(pair, frames));
            pair.hasSourceTransform = false;
            Assert.Throws<System.IO.InvalidDataException>(() => ReverseUvBenchmark.SourceTransform(pair, frames));
            frames.Add(new TransferCaseCapture.MeshState { renderer = pair.source, repacked = pair.sourceMesh,
                localToWorld = pair.sourceLocalToWorld });
            Assert.AreEqual(pair.sourceLocalToWorld, ReverseUvBenchmark.SourceTransform(pair, frames));
            frames.Add(new TransferCaseCapture.MeshState { renderer = pair.source, repacked = pair.sourceMesh,
                localToWorld = pair.localToWorld });
            Assert.Throws<System.IO.InvalidDataException>(() => ReverseUvBenchmark.SourceTransform(pair, frames));
        }

        [Test] public void ParentHoleBetweenInteriorProbesCannotBeBorrowed()
        {
            var seed = new Mesh { name = "Perforated" }; owned.Add(seed);
            var positions = new List<Vector3>(); var indices = new List<int>();
            for (int y=0;y<=10;++y) for(int x=0;x<=10;++x) positions.Add(new Vector3(x*.1f,y*.1f,0));
            for (int y=0;y<10;++y) for(int x=0;x<10;++x)
            {
                if(x==1 && y==1) continue;
                int a=y*11+x; indices.AddRange(new[] {a,a+1,a+12,a,a+12,a+11});
            }
            seed.SetVertices(positions); seed.triangles=indices.ToArray();
            seed.uv2=positions.Select(p=>new Vector2(p.x,p.y)*.5f+Vector2.one*.25f).ToArray();
            var target = new Mesh { name="AcrossHole" }; owned.Add(target);
            target.vertices=new[] {Vector3.zero,Vector3.right,Vector3.up}; target.triangles=new[] {0,1,2};
            using var result = Build(Options(),Level(1,seed),Level(0,target));
            Assert.Greater(result.report.inheritedFaces,0); Assert.Greater(result.report.newFaces,0);
            Assert.Greater(result.report.splitSourceFaces,0); Clean(result.meshes[1][0]);
        }

        [TestCase("budget")] [TestCase("atlas")] [TestCase("seed-overlap")] [TestCase("seed-stretch")]
        [TestCase("order")] [TestCase("degenerate")] [TestCase("reach")]
        public void InvalidOrUnverifiedInputsFailWithoutMutatingSources(string issue)
        {
            var a = Quad(); var b = Quad(4); var options = Options(); var levels = new[] { Level(1,a), Level(0,b) };
            switch(issue)
            {
                case "budget": options.comparisonBudget = 1; b.vertices = a.vertices; break;
                case "atlas": options.maxAtlasSize = 128; break;
                case "seed-overlap": levels[0] = Level(1, a, Quad()); break;
                case "seed-stretch": a.uv2 = a.uv2.Select(p => new Vector2(p.x, p.y * .01f)).ToArray(); break;
                case "order": levels[0].lod = 0; break;
                case "degenerate": b.vertices = new Vector3[4]; break;
                case "reach": options.projectionReach = float.NaN; break;
            }
            var av = a.vertices; var au = a.uv2; var bv = b.vertices;
            Assert.Catch(() => { using var result = Build(options, levels); });
            CollectionAssert.AreEqual(av, a.vertices); CollectionAssert.AreEqual(au, a.uv2); CollectionAssert.AreEqual(bv,b.vertices);
        }

        [Test] public void CancelledBuildProducesNoOutput()
        {
            using var token = new CancellationTokenSource(); token.Cancel();
            Assert.Throws<OperationCanceledException>(() => ReverseUvTransfer.Build(new[] { Level(1,Quad()),Level(0,Quad()) }, Options(), token:token.Token).GetAwaiter().GetResult());
        }

        [Test] public void WorldTransformsAndMirroringAreRespected()
        {
            var seed = Quad(); var target = Quad(); var levels = new[] { Level(1,seed), Level(0,target) };
            var transform = Matrix4x4.TRS(new Vector3(30,2,-7), Quaternion.Euler(15,40,0), new Vector3(-2,2,2));
            levels[0].inputs[0].toWorld = levels[1].inputs[0].toWorld = transform;
            using var result = Build(Options(), levels);
            Assert.AreEqual(2,result.report.inheritedFaces); Assert.AreEqual(32f,result.report.texelsPerUnit,.001f);
            CollectionAssert.AreEqual(seed.uv2, result.meshes[1][0].uv2);
        }

        [TestCase(false)] [TestCase(true)]
        public void SmallNonzeroTransformScaleDoesNotImplySingularity(bool thinUnusedAxis)
        {
            var seed=Quad();var target=Quad();var levels=new[] {Level(1,seed),Level(0,target)};
            var transform=Matrix4x4.Scale(thinUnusedAxis?new Vector3(1,1,1e-15f):Vector3.one*1e-5f);
            foreach(var level in levels) level.inputs[0].toWorld=transform;
            var snapshot=TransferMeshSnapshot.Capture(seed);
            using var result=Build(Options(),levels);
            Assert.AreEqual(2,result.report.nodes[1].faces.Length);Clean(result.meshes[1][0]);
            CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(seed));
            foreach(var level in levels) level.inputs[0].toWorld=Matrix4x4.Scale(new Vector3(1,1,0));
            Assert.Throws<ArgumentException>(()=>{using var refused=Build(Options(),levels);});
        }

        [Test] public void RepeatedBuildsAreDeterministic()
        {
            var levels = new[] { Level(1,Quad()),Level(0,Combine(Quad(),Quad(4))) };
            using var a = Build(Options(),levels); using var b = Build(Options(),levels);
            Assert.AreEqual(JsonUtility.ToJson(a.report),JsonUtility.ToJson(b.report));
            CollectionAssert.AreEqual(a.meshes[1][0].uv2,b.meshes[1][0].uv2);
        }

        [UnityTest] public IEnumerator AsyncProjectionCompletesOnTheEditorThread()
        {
            var task = ReverseUvTransfer.Build(new[] { Level(1,Quad()),Level(0,Quad()) }, Options(), true);
            while (!task.IsCompleted) yield return null;
            using var result = task.GetAwaiter().GetResult(); Assert.AreEqual(2,result.report.inheritedFaces); Clean(result.meshes[1][0]);
        }

        [Test] public void RefinedMetricUsesCapturedArraysOnTheProjectionWorker()
        {
            var surface = new ReverseUvTransfer.Surface {
                input = new ReverseUvTransfer.Input {toWorld = Matrix4x4.identity},
                sourcePositions = new[] {Vector3.zero,Vector3.right,Vector3.up}, sourceIndices = new[] {0,1,2},
                sourceFaces = new[] {0,0}, faces = new[] {new ReverseUvTransfer.Face(),new ReverseUvTransfer.Face()},
                sourceBarycentrics = new[] {Vector3.right,Vector3.up,new Vector3(.5f,0,.5f),
                    new Vector3(.5f,0,.5f),Vector3.up,Vector3.forward}
            };
            System.Threading.Tasks.Task.Run(()=>ReverseUvTransfer.RefreshMetricFrames(surface)).GetAwaiter().GetResult();
            Assert.AreEqual(2,surface.metricFrames.Length);
            Assert.AreEqual(.5,surface.metricFrames[0].length*surface.metricFrames[0].y,1e-12);
            Assert.AreEqual(.5,surface.metricFrames[1].length*surface.metricFrames[1].y,1e-12);
        }

        [UnityTest] public IEnumerator FrozenCapturesProduceIndependentReverseBenchReports()
        {
            string manifests=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_MANIFESTS");
            if(string.IsNullOrEmpty(manifests)) Assert.Ignore("Set MESHLAB_REVERSE_MANIFESTS to replay frozen model captures.");
            string directory=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_OUTPUT") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(),"MeshLabReverseBench");
            string[] baselines=Environment.GetEnvironmentVariable("MESHLAB_REVERSE_BASELINES")?.Split(';');
            int index=0;
            foreach(string manifest in manifests.Split(';'))
            {
                int capture=index++;
                var task=ReverseUvBenchmark.Run(manifest,System.IO.Path.Combine(directory,capture.ToString()));
                while(!task.IsCompleted) yield return null;
                var summary=task.GetAwaiter().GetResult();
                Assert.Greater(summary.accepted+summary.refused,0);
                // Refusal is a recorded prototype limitation, not a successful transfer.
                foreach(var trial in summary.trials.Where(t=>t.accepted))
                {
                    var audit=JsonUtility.FromJson<ReverseUvAudit.Audit>(System.IO.File.ReadAllText(trial.audit));
                    foreach(var node in audit.measurements)
                    {
                        Assert.AreEqual(0,node.invalidFaces); Assert.AreEqual(0,node.degenerateFaces);
                        Assert.AreEqual(0,node.outOfBounds); Assert.IsTrue(node.complete);
                        Assert.LessOrEqual(node.worstAnisotropy,4.001);
                    }
                    foreach(var level in audit.levels)
                    {
                        Assert.IsTrue(level.complete); Assert.AreEqual(0,level.unexpectedOverlapPairs);
                        Assert.Greater(level.texelsPerUnitMin,0); Assert.IsFalse(double.IsInfinity(level.texelsPerUnitMax));
                    }
                }
                foreach(var trial in summary.trials.Where(t=>!t.accepted)) Assert.IsNotEmpty(trial.failureStage);
                if(baselines!=null)
                {
                    Assert.Less(capture,baselines.Length);
                    var baseline=JsonUtility.FromJson<ReverseUvBenchmark.Summary>(System.IO.File.ReadAllText(baselines[capture]));
                    Assert.AreEqual(baseline.manifest,summary.manifest);
                    Assert.AreEqual(baseline.trials.Count,summary.trials.Count);
                    foreach(var previous in baseline.trials.Where(t=>t.accepted))
                    {
                        var current=summary.trials.Single(t=>t.pair==previous.pair && t.allowOverlap==previous.allowOverlap);
                        Assert.AreEqual(previous.target,current.target);
                        Assert.IsTrue(current.accepted,$"Previously accepted pair {current.pair}, overlap {current.allowOverlap}: {current.error}");
                    }
                }
            }
        }

        [TestCase(0,1,2)] [TestCase(1,2,0)] [TestCase(2,0,1)]
        [TestCase(0,2,1)] [TestCase(2,1,0)] [TestCase(1,0,2)]
        public void CapturedCountertopMetricDoesNotDependOnCornerOrder(int a,int b,int c)
        {
            // Exact raw coordinates of Kitchen Countertop LOD1 face 38.
            var positions=new[] {
                new Vector3(.3567301630973816f,.06804550439119339f,-.18836303055286407f),
                new Vector3(.3572298288345337f,.06804550439119339f,-.18836303055286407f),
                new Vector3(.632880687713623f,.06803926080465317f,-.18835678696632385f) };
            // Independent double reference intrinsic coordinates from the capture.
            var uv=new[] {Vector2.zero,new Vector2(.0004996657371520996f,0),new Vector2(.27615052461624146f,8.8297647630323e-6f)};
            Assert.Less(ReverseUvTransfer.TriangleAnisotropy(positions[a],positions[b],positions[c],uv[a],uv[b],uv[c]),1.001);
        }

        [TestCase(1e-12f)] [TestCase(1e-8f)] [TestCase(1)] [TestCase(1e12f)]
        public void TriangleMetricIsScaleIndependentAndDetectsStretch(float size)
        {
            var a=Vector3.zero; var b=Vector3.right*size; var c=Vector3.up*size;
            Assert.AreEqual(1,ReverseUvTransfer.TriangleAnisotropy(a,b,c,Vector2.zero,Vector2.right,Vector2.up),1e-6);
            Assert.AreEqual(8,ReverseUvTransfer.TriangleAnisotropy(a,b,c,Vector2.zero,Vector2.right,new Vector2(0,.125f)),1e-6);
        }

        [Test] public void FloatPlacementCollapseIsNotMistakenForGoodIntrinsicUv()
        {
            var positions=new[] {Vector3.zero,new Vector3(.0004996657371520996f,0,0),
                new Vector3(.27615052461624146f,8.8297647630323e-6f,0)};
            var uv=positions.Select(p=>new Vector2(p.x,p.y)).ToArray();
            Assert.AreEqual(1,ReverseUvTransfer.TriangleAnisotropy(positions[0],positions[1],positions[2],uv[0],uv[1],uv[2]),1e-6);
            uv=uv.Select(p=>p+Vector2.one*4096).ToArray();
            Assert.IsTrue(double.IsPositiveInfinity(ReverseUvTransfer.TriangleAnisotropy(positions[0],positions[1],positions[2],uv[0],uv[1],uv[2])));
        }

        [TestCase(false)] [TestCase(true)]
        public void FarTranslatedChainKeepsUvsAndProjectionRelationships(bool overlap)
        {
            var coarse=Quad(size:.001f); var fine=Quad(size:.001f);
            var near=new[] {Level(1,coarse),Level(0,fine)};
            using var expected=Build(Options(overlap),near);
            var translation=Matrix4x4.Translate(new Vector3(1000000,2000000,-1000000));
            foreach(var level in near) foreach(var input in level.inputs) input.toWorld=translation;
            var points=coarse.vertices.Select(translation.MultiplyPoint3x4).ToArray();
            Assert.IsFalse(MeshGeometry.HasArea(points[0],points[1],points[2]),"Control must reproduce world-coordinate collapse.");
            using var actual=Build(Options(overlap),near);
            for(int i=0;i<2;++i) {
                CollectionAssert.AreEqual(expected.meshes[i][0].uv2,actual.meshes[i][0].uv2);
                var source=i==0?coarse:fine;
                var output=actual.meshes[i][0];
                CollectionAssert.AreEqual(source.triangles.Select(v=>source.vertices[v]),output.triangles.Select(v=>output.vertices[v]));
            }
            Assert.AreEqual(expected.report.inheritedFaces,actual.report.inheritedFaces);
        }

        [Test] public void SeedPreparationUsesTheSameRelativeWorldMetric()
        {
            var mesh=Quad(size:.001f); var input=new ReverseUvTransfer.Input {mesh=mesh,key="SmallSeed"};
            var first=ReverseUvSeed.Prepare(new[] {input},128,2,0,default,out int firstSize);
            owned.AddRange(first);
            input.toWorld=Matrix4x4.Translate(new Vector3(1000000,2000000,-1000000));
            var second=ReverseUvSeed.Prepare(new[] {input},128,2,0,default,out int secondSize);
            owned.AddRange(second);
            Assert.AreEqual(firstSize,secondSize);CollectionAssert.AreEqual(first[0].uv2,second[0].uv2);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void CapturedKamazThinFaceSurvivesWorldTranslation(int transform)
        {
            var mesh=new Mesh {name="Kamaz_Typhoon_LOD1"};owned.Add(mesh);
            mesh.vertices=new[] {
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.8186256885528564f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.8186254501342773f),
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.8186254501342773f) };
            mesh.triangles=new[] {0,1,2};
            var input=new ReverseUvTransfer.Input {mesh=mesh,toWorld=Matrix4x4.Translate(new Vector3(52.7f,-.07f,85.75f))};
            if(transform==1) input.toWorld=new Matrix4x4(
                new Vector4(-.04123282432556152f,-8.881784197001252e-15f,-.9991496801376343f,0),
                new Vector4(4.36742055853756e-8f,1,-1.8023467163175155e-9f,0),
                new Vector4(.9991496801376343f,-4.3711374075883214e-8f,-.04123282432556152f,0),
                new Vector4(-47.790000915527344f,-.05875444412231445f,-104.54000091552734f,1));
            if(transform==2) input.toWorld=new Matrix4x4(
                new Vector4(-.8483419418334961f,-1.5260752661561128e-9f,-.5294487476348877f,0),
                new Vector4(2.1834194896541703e-8f,1,-3.786757574175681e-8f,0),
                new Vector4(.5294487476348877f,-4.3684739381433246e-8f,-.8483419418334961f,0),
                new Vector4(52.70000076293945f,-.06999999284744263f,85.75f,1));
            var rounded=mesh.vertices.Select(input.toWorld.MultiplyPoint3x4).ToArray();
            Assert.IsFalse(MeshGeometry.HasArea(rounded[0],rounded[1],rounded[2]));
            var relative=ReverseUvTransfer.RelativePositions(input,input.toWorld.GetColumn(3),1);
            if(transform==0) CollectionAssert.AreEqual(mesh.vertices,relative);
            Assert.IsTrue(MeshGeometry.HasArea(relative[0],relative[1],relative[2]));
        }

        [Test] public void CapturedKamazCollinearFaceRefusesBeforeRoundingCanInventArea()
        {
            var mesh=new Mesh {name="Kamaz_Typhoon_LOD1"};owned.Add(mesh);
            mesh.vertices=new[] {
                new Vector3(-.7542471289634705f,2.827155351638794f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.854128122329712f,-1.723873257637024f),
                new Vector3(-.7542471289634705f,2.827155113220215f,-1.723873257637024f) };
            mesh.triangles=new[] {0,1,2};
            var input=new ReverseUvTransfer.Input {mesh=mesh,toWorld=Matrix4x4.TRS(new Vector3(48.349f,-.113f,-95.336f),Quaternion.Euler(1,224,0),Vector3.one)};
            var before=TransferMeshSnapshot.Capture(mesh);
            var error=Assert.Throws<InvalidOperationException>(()=>ReverseUvTransfer.RelativePositions(input,input.toWorld.GetColumn(3),1));
            StringAssert.Contains("source-local coordinates",error.Message);
            StringAssert.Contains("vertices 0, 1, 2",error.Message);
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(mesh));
        }

        [Test] public void CapturedNanometreBacksplashKeepsGeometryWhenRescuePassesAudit()
        {
            var fine=new Mesh {name="Backsplash nanometre face"}; owned.Add(fine);
            fine.vertices=new[] {
                new Vector3(-.013632268644869328f,.06390988081693649f,-.1929730921983719f),
                new Vector3(-.7002735733985901f,.06392237544059753f,-.19298714399337769f),
                new Vector3(-.7007233500480652f,.06392237544059753f,-.19298714399337769f) };
            fine.triangles=new[] {0,1,2}; fine.uv=new[] {Vector2.zero,Vector2.right,Vector2.up};
            // A normal neighbouring surface keeps Unity's connected unwrap alive.
            // The rescue now passes after reconstruction at its final texel box;
            // acceptance still requires the independent final quality audit.
            fine=Combine(fine,Quad(8));
            var before=TransferMeshSnapshot.Capture(fine);
            Assert.IsTrue(MeshGeometry.HasArea(fine.vertices[0],fine.vertices[1],fine.vertices[2]));
            var levels=new[] {Level(1,Quad(4)),Level(0,fine)};
            using var result=Build(Options(),levels);
            var mesh=result.meshes[1][0]; Clean(mesh);
            var quality=TransferUvQuality.Measure(mesh,mesh.uv2,Vector2.one,Matrix4x4.identity);
            Assert.LessOrEqual(quality.worstAnisotropy,4.001);
            Assert.AreEqual(fine.triangles.Length,mesh.triangles.Length);
            var inputVertices=fine.vertices; var inputTriangles=fine.triangles;
            var outputVertices=mesh.vertices; var outputTriangles=mesh.triangles;
            for(int i=0;i<inputTriangles.Length;++i) Assert.AreEqual(inputVertices[inputTriangles[i]],outputVertices[outputTriangles[i]]);
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(fine));
        }

        [Test] public void AuditRefusesStretchedFinalUvEvenWhenOverlapScanIsClean()
        {
            var levels=new[] {Level(1,Quad()),Level(0,Quad())};
            using var result=Build(Options(),levels);
            var output=result.meshes[1][0];
            output.uv2=output.uv2.Select(p=>new Vector2(p.x*.01f,p.y)).ToArray();
            var quality=TransferUvQuality.Measure(output,output.uv2,Vector2.one,Matrix4x4.identity);
            Assert.IsTrue(quality.overlapScanComplete); Assert.AreEqual(0,quality.overlapPairs);
            Assert.Greater(quality.worstAnisotropy,4);
            Assert.Throws<InvalidOperationException>(()=>ReverseUvAudit.Write(result,levels));
        }

        [Test] public void SplitsPreserveCornerAttributesSubmeshesBlendShapesAndSkinWeights()
        {
            var mesh = Quad(); mesh.subMeshCount = 2; mesh.SetTriangles(new[] {0,1,2},0); mesh.SetTriangles(new[] {0,2,3},1);
            mesh.tangents = new[] { Vector4.one,new Vector4(1,0,0,-1),Vector4.one,Vector4.one };
            mesh.colors32 = new[] {new Color32(1,2,3,4),new Color32(5,6,7,8),new Color32(9,10,11,12),new Color32(13,14,15,16)};
            mesh.SetUVs(3,new List<Vector4> {Vector4.one,Vector4.one*2,Vector4.one*3,Vector4.one*4});
            var delta = mesh.vertices.Select(p => p + Vector3.forward).ToArray(); mesh.AddBlendShapeFrame("Detail",50,delta,delta,delta);
            mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity,5).ToArray();
            using (var counts = new NativeArray<byte>(new byte[] {5,1,1,1},Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(new[] {
                new BoneWeight1 {boneIndex=0,weight=.4f},new BoneWeight1 {boneIndex=1,weight=.3f},new BoneWeight1 {boneIndex=2,weight=.15f},
                new BoneWeight1 {boneIndex=3,weight=.1f},new BoneWeight1 {boneIndex=4,weight=.05f},new BoneWeight1 {boneIndex=0,weight=1},
                new BoneWeight1 {boneIndex=0,weight=1},new BoneWeight1 {boneIndex=0,weight=1}},Allocator.Temp)) mesh.SetBoneWeights(counts,weights);
            var corners = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.one*.1f,Vector2.one*.2f,Vector2.up };
            var output = ReverseUvMesh.Copy(mesh,mesh.triangles,corners); owned.Add(output);
            Assert.AreEqual(6,output.vertexCount); Assert.AreEqual(2,output.subMeshCount);
            var original = mesh.triangles; var mapped = output.triangles;
            var uv3 = new List<Vector4>(); mesh.GetUVs(3,uv3); var newUv3=new List<Vector4>(); output.GetUVs(3,newUv3);
            var newDelta = new Vector3[6]; output.GetBlendShapeFrameVertices(0,0,newDelta,null,null);
            using var newCounts = output.GetBonesPerVertex(); using var newWeights = output.GetAllBoneWeights();
            for(int i=0;i<6;++i)
            {
                int old = original[i], v = mapped[i];
                Assert.AreEqual(mesh.vertices[old],output.vertices[v]); Assert.AreEqual(mesh.normals[old],output.normals[v]);
                Assert.AreEqual(mesh.tangents[old],output.tangents[v]); Assert.AreEqual(mesh.colors32[old],output.colors32[v]);
                Assert.AreEqual(mesh.uv[old],output.uv[v]); Assert.AreEqual(uv3[old],newUv3[v]); Assert.AreEqual(delta[old],newDelta[v]);
                Assert.AreEqual(old==0?5:1,newCounts[v]); Assert.AreEqual(corners[i],output.uv2[v]);
            }
            Assert.AreEqual(14,newWeights.Length); Assert.AreEqual(4,mesh.vertexCount);
        }

        [TestCase(VertexAttributeFormat.Float16,2)] [TestCase(VertexAttributeFormat.Float16,4)]
        [TestCase(VertexAttributeFormat.Float32,1)]
        public void RawUvAndColorBytesSurviveUv2Splits(VertexAttributeFormat format,int dimension)
        {
            var source=new Mesh {name="Raw channels"}; owned.Add(source);
            source.SetVertexBufferParams(4,new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3,0),
                new VertexAttributeDescriptor(VertexAttribute.Color,VertexAttributeFormat.UNorm8,4,1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0,format,dimension,1));
            var geometry=new[] {0f,0,0,1,0,0,1,1,0,0,1,0}; source.SetVertexBufferData(geometry,0,0,geometry.Length,0);
            int stride=source.GetVertexBufferStride(1),offset=source.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
            var bytes=new byte[stride*4]; int componentSize=format==VertexAttributeFormat.Float16?2:4;
            for(int v=0;v<4;++v)
            {
                bytes[v*stride]=(byte)(v*37); bytes[v*stride+3]=255;
                for(int c=0;c<dimension;++c)
                {
                    var value=format==VertexAttributeFormat.Float16?BitConverter.GetBytes((ushort)(v==0?0x8000:0x3800+v*32+c)):
                        BitConverter.GetBytes(v==0?BitConverter.Int32BitsToSingle(int.MinValue):v*.123456789f);
                    Buffer.BlockCopy(value,0,bytes,v*stride+offset+c*componentSize,componentSize);
                }
            }
            source.SetVertexBufferData(bytes,0,0,bytes.Length,1); source.triangles=new[] {0,1,2,0,2,3,0,0,0};
            using var prepared=ReverseUvInputs.Prepare(new[] {Level(1,source)});
            var cleaned=prepared.levels[0].inputs[0].mesh;
            using (var cleanData=Mesh.AcquireReadOnlyMeshData(cleaned))
                CollectionAssert.AreEqual(bytes,cleanData[0].GetVertexData<byte>(1).ToArray());
            var output=ReverseUvMesh.Copy(cleaned,cleaned.triangles,new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.one*.1f,Vector2.one*.2f,Vector2.up}); owned.Add(output);
            Assert.AreEqual(format,output.GetVertexAttributeFormat(VertexAttribute.TexCoord0)); Assert.AreEqual(dimension,output.GetVertexAttributeDimension(VertexAttribute.TexCoord0));
            using var data=Mesh.AcquireReadOnlyMeshData(output);
            int stream=output.GetVertexAttributeStream(VertexAttribute.TexCoord0),newStride=output.GetVertexBufferStride(stream),newOffset=output.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
            var raw=data[0].GetVertexData<byte>(stream);
            for(int i=0;i<6;++i)
                for(int c=0;c<dimension*componentSize;++c)
                    Assert.AreEqual(bytes[source.triangles[i]*stride+offset+c],raw[output.triangles[i]*newStride+newOffset+c]);
            Assert.AreEqual(VertexAttributeFormat.UNorm8,output.GetVertexAttributeFormat(VertexAttribute.Color));
        }

        [Test] public void SidecarRetainsResultUvAndAncestryAndClearsItOnLegacyReplacement()
        {
            var original=Quad(); var working=Object.Instantiate(original); owned.Add(working);
            var result=Object.Instantiate(original); owned.Add(result); result.uv2=result.uv2.Select(p=>p*.3f).ToArray();
            var entry=new MeshEntry { fbxMesh=original, originalMesh=working, transferredMesh=result, reverseTransferJson="ancestry" };
            Assert.IsTrue(SidecarStore.TryBuildEntry(entry,result,false,SidecarStore.AoUvTarget.None,out var saved));
            CollectionAssert.AreEqual(result.uv2,saved.uv2);
            var asset=ScriptableObject.CreateInstance<Uv2DataAsset>();
            try
            {
                asset.Set(saved); Assert.AreEqual("ancestry",asset.Find(original.name).reverseTransferJson);
                saved.reverseTransferJson="replacement"; asset.Set(saved); Assert.AreEqual("replacement",asset.Find(original.name).reverseTransferJson);
                asset.Set(original.name,original.uv2); Assert.IsNull(asset.Find(original.name).reverseTransferJson);
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test] public void LegacyReverseProvenanceIsUpgradedToTheCompactSchemaOnRestore()
        {
            var original = Quad();
            var mesh = ReverseUvMesh.Copy(original, original.triangles,
                new[] { Vector2.zero, Vector2.right * .2f, Vector2.one * .2f, Vector2.one * .6f, Vector2.one * .8f, Vector2.up * .8f });
            owned.Add(mesh);
            var faces = new[] { new ReverseUvTransfer.Face { chart = 3, inherited = true, layer = 1, intentionalOverlap = true },
                new ReverseUvTransfer.Face { chart = 4, projectionSupport = true } };
            string legacy = JsonUtility.ToJson(new ReverseUvAudit.LegacyProvenanceEntry { atlasSize = 64, seedResolution = 32, texelsPerUnit = 8,
                surface = new ReverseUvTransfer.NodeReport { lod = 1, key = mesh.name, faces = faces, removedSourceFaces = new[] { 7 } },
                chain = new[] { new ReverseUvAudit.NodeKey { lod = 1, node = 0, key = mesh.name } },
                overlaps = new[] { new ReverseUvTransfer.OverlapRelation { lod = 1, underFace = 0, overFace = 1 } } });
            var parsed = ReverseUvAudit.ParseProvenance(legacy, out bool upgraded);
            Assert.IsTrue(upgraded); Assert.AreEqual(ReverseUvAudit.ProvenanceEntry.CurrentSchema, parsed.schema); Assert.IsTrue(parsed.Describes(2));
            Assert.AreEqual(1, parsed.lod); Assert.AreEqual(mesh.name, parsed.key); Assert.AreEqual(64, parsed.atlasSize); Assert.AreEqual(8, parsed.texelsPerUnit);
            Assert.IsTrue(parsed.IsInherited(0)); Assert.IsTrue(parsed.IsIntentionalOverlap(0)); Assert.IsFalse(parsed.HasProjectionSupport(0));
            Assert.AreEqual(1, parsed.Layer(0)); Assert.AreEqual(3, parsed.Chart(0));
            Assert.IsFalse(parsed.IsInherited(1)); Assert.IsTrue(parsed.HasProjectionSupport(1)); Assert.AreEqual(0, parsed.Layer(1)); Assert.AreEqual(4, parsed.Chart(1));
            CollectionAssert.AreEqual(new[] { 7 }, parsed.removedSourceFaces); Assert.AreEqual(1, parsed.chain.Length); Assert.AreEqual(1, parsed.overlaps.Length);
            Assert.IsNull(ReverseUvAudit.ParseProvenance("not JSON", out _)); Assert.IsNull(ReverseUvAudit.ParseProvenance(null, out _));
            var saved = new MeshUv2Entry { targetUvChannel = 1, vertPositions = mesh.vertices, vertUv0 = mesh.uv, uv2 = mesh.uv2,
                optimizedTriangles = mesh.triangles, submeshTriangleCounts = new[] { mesh.triangles.Length }, reverseTransferJson = legacy };
            var entry = new MeshEntry { fbxMesh = mesh, originalMesh = mesh };
            SidecarStore.RestoreReverseProvenance(entry, saved);
            var restored = ReverseUvAudit.ParseProvenance(entry.reverseTransferJson, out upgraded);
            Assert.IsFalse(upgraded, "the restored history is rewritten in the compact schema");
            Assert.IsTrue(restored.Describes(2)); Assert.AreEqual(64u, entry.repackedAtlasWidth);
            Assert.Less(entry.reverseTransferJson.Length, legacy.Length);
        }

        [Test] public void AuditSaveKeepsOnlyTheLastReports()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MeshLabReverseAudit_" + Guid.NewGuid().ToString("N"));
            var previous = BenchmarkRecorder.OutputDirectoryOverride;
            try
            {
                BenchmarkRecorder.OutputDirectoryOverride = directory;
                var audit = new ReverseUvAudit.Audit { report = new ReverseUvTransfer.Report() };
                var written = new List<string>();
                for (int i = 0; i < 4; ++i) written.Add(System.IO.Path.GetFileName(ReverseUvAudit.Save(audit, keepLast: 2)));
                string[] Kept() => System.IO.Directory.GetFiles(directory, "reverse_*.json").Select(p => System.IO.Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                // The names carry the UTC stamp the pruning sorts by; saves within one millisecond sort by their suffix, as the pruning does.
                CollectionAssert.AreEqual(written.OrderBy(n => n, StringComparer.Ordinal).Skip(2).ToArray(), Kept(), "the newest reports stay");
                ReverseUvAudit.Save(audit);
                Assert.AreEqual(3, Kept().Length, "a save without a limit prunes nothing");
            }
            finally
            {
                BenchmarkRecorder.OutputDirectoryOverride = previous;
                if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            }
        }

        [Test] public void ReverseSidecarRefusesSplitSeamsAndRemovedFacesWithoutChangingSource()
        {
            var source = Quad(); var before = TransferMeshSnapshot.Capture(source);
            var result = ReverseUvMesh.Copy(source, source.triangles,
                new[] { Vector2.zero, Vector2.right * .2f, Vector2.one * .2f,
                    Vector2.one * .6f, Vector2.one * .8f, Vector2.up * .8f });
            owned.Add(result);
            var entry = new MeshEntry { fbxMesh = source, originalMesh = source, reverseTransferJson = "ancestry" };
            Assert.IsFalse(SidecarStore.TryBuildEntry(entry, result, false, SidecarStore.AoUvTarget.None, out var saved));
            Assert.IsNull(saved);
            var removed = Object.Instantiate(source); owned.Add(removed); removed.triangles = new[] { 0, 1, 2 };
            Assert.IsFalse(SidecarStore.TryBuildEntry(entry, removed, false, SidecarStore.AoUvTarget.None, out saved));
            Assert.IsNull(saved); CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(source));
        }

        [TestCase("valid")] [TestCase("vertex order")] [TestCase("unreadable")]
        [TestCase("changed UV2")] [TestCase("face order")]
        public void ReverseHistoryRecognizesExistingUv2SeamsWithoutReusingEditedFaces(string change)
        {
            var original = Quad();
            var mesh = ReverseUvMesh.Copy(original, original.triangles,
                new[] { Vector2.zero, Vector2.right * .2f, Vector2.one * .2f,
                    Vector2.one * .6f, Vector2.one * .8f, Vector2.up * .8f });
            owned.Add(mesh);
            var saved = new MeshUv2Entry {
                targetUvChannel = 1, vertPositions = mesh.vertices, vertUv0 = mesh.uv, uv2 = mesh.uv2,
                optimizedTriangles = mesh.triangles, submeshTriangleCounts = new[] { mesh.triangles.Length },
                reverseTransferJson = JsonUtility.ToJson(new ReverseUvAudit.ProvenanceEntry {
                    atlasSize = 128, key = mesh.name, faceCount = 2, faceFlags = new int[2], faceChart = new[] { 0, 1 }
                })
            };
            if (change == "vertex order") {
                mesh.vertices = saved.vertPositions.Reverse().ToArray();
                mesh.uv = saved.vertUv0.Reverse().ToArray(); mesh.uv2 = saved.uv2.Reverse().ToArray();
                mesh.triangles = saved.optimizedTriangles.Select(index => mesh.vertexCount - 1 - index).ToArray();
            }
            if (change == "changed UV2") { var uv = mesh.uv2; uv[0] += Vector2.one * .01f; mesh.uv2 = uv; }
            if (change == "face order") mesh.triangles = saved.optimizedTriangles.Skip(3).Concat(saved.optimizedTriangles.Take(3)).ToArray();
            var before = TransferMeshSnapshot.Capture(mesh);
            if (change == "unreadable") mesh.UploadMeshData(true);
            var entry = new MeshEntry { fbxMesh = mesh, originalMesh = mesh };
            SidecarStore.RestoreReverseProvenance(entry, saved);
            bool valid = change == "valid" || change == "vertex order" || change == "unreadable";
            Assert.AreEqual(valid ? saved.reverseTransferJson : null, entry.reverseTransferJson);
            Assert.AreEqual(valid ? 128u : 0u, entry.repackedAtlasWidth);
            Assert.IsFalse(SidecarStore.ReverseLegacyTopologySafe(mesh, saved), "Legacy sidecar replay still cannot introduce an ambiguous UV2 seam.");
            Assert.AreEqual(change != "unreadable", mesh.isReadable);
            var readable = MeshAccess.Readable(mesh, out bool owns);
            try { CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(readable)); }
            finally { if (owns) Object.DestroyImmediate(readable); }
        }

        [Test] public void ReverseProvenanceIsRestoredForTheSceneEditNotTheImport()
        {
            // Cleanup's scene edit is the mesh the entry works from: a record that describes the
            // import's faces is not restored onto an edit with other faces (the save would then skip
            // the edit as a result the file already has); it is onto an edit with the same faces.
            var original = Quad();
            var mesh = ReverseUvMesh.Copy(original, original.triangles,
                new[] { Vector2.zero, Vector2.right * .2f, Vector2.one * .2f,
                    Vector2.one * .6f, Vector2.one * .8f, Vector2.up * .8f });
            owned.Add(mesh);
            var saved = new MeshUv2Entry {
                targetUvChannel = 1, vertPositions = mesh.vertices, vertUv0 = mesh.uv, uv2 = mesh.uv2,
                optimizedTriangles = mesh.triangles, submeshTriangleCounts = new[] { mesh.triangles.Length },
                reverseTransferJson = JsonUtility.ToJson(new ReverseUvAudit.ProvenanceEntry {
                    atlasSize = 128, key = mesh.name, faceCount = 2, faceFlags = new int[2], faceChart = new[] { 0, 1 }
                })
            };
            var reshaped = Object.Instantiate(mesh); reshaped.name = mesh.name; owned.Add(reshaped);
            reshaped.triangles = mesh.triangles.Take(3).ToArray(); // a face removed
            var entry = new MeshEntry { fbxMesh = mesh, originalMesh = reshaped, sceneEditMesh = reshaped };
            SidecarStore.RestoreReverseProvenance(entry, saved);
            Assert.IsNull(entry.reverseTransferJson, "the record describes the import's faces, not the edit's");
            Assert.AreEqual(0u, entry.repackedAtlasWidth);
            var recoloured = Object.Instantiate(mesh); recoloured.name = mesh.name; owned.Add(recoloured);
            recoloured.colors = Enumerable.Repeat(Color.red, mesh.vertexCount).ToArray(); // the faces and the layout as recorded
            entry = new MeshEntry { fbxMesh = mesh, originalMesh = recoloured, sceneEditMesh = recoloured };
            SidecarStore.RestoreReverseProvenance(entry, saved);
            Assert.AreEqual(saved.reverseTransferJson, entry.reverseTransferJson);
            Assert.AreEqual(128u, entry.repackedAtlasWidth);
        }

        [TestCase(false,false,false)] [TestCase(true,false,false)] [TestCase(false,true,false)] [TestCase(false,false,true)]
        public void ReverseSidecarChecksTheReplayPositionBucket(bool distinctBucket, bool distinctUv0, bool sameUv2)
        {
            var source = Quad();
            var entry = new MeshUv2Entry {
                vertPositions = new[] {Vector3.right*.00001f,Vector3.right*(distinctBucket?.0002f:.00002f),Vector3.up,Vector3.one},
                vertUv0 = new[] {Vector2.zero,distinctUv0?Vector2.right:Vector2.zero,Vector2.up,Vector2.one},
                uv2 = new[] {Vector2.zero,sameUv2?Vector2.zero:Vector2.right,Vector2.up,Vector2.one}
            };
            source.vertices = entry.vertPositions; source.uv = entry.vertUv0;
            entry.optimizedTriangles = source.triangles; entry.submeshTriangleCounts = new[] { source.triangles.Length };
            Assert.AreEqual(distinctBucket || distinctUv0 || sameUv2,SidecarStore.ReverseLegacyTopologySafe(source,entry));
        }

        [TestCase("position")] [TestCase("uv0")] [TestCase("triangles")]
        [TestCase("winding")] [TestCase("submeshes")] [TestCase("missing-topology")]
        public void ReverseSidecarRefusesEditedGeometryWithTheSameVertexCount(string change)
        {
            var source = Quad();
            var result = Object.Instantiate(source); owned.Add(result);
            var meshEntry = new MeshEntry { fbxMesh = source, originalMesh = source, reverseTransferJson = "ancestry" };
            Assert.IsTrue(SidecarStore.TryBuildEntry(meshEntry, result, false, SidecarStore.AoUvTarget.None, out var saved));
            if (change == "position") { var p = source.vertices; p[0] += Vector3.forward * .1f; source.vertices = p; }
            if (change == "uv0") { var uv = source.uv; uv[0] += Vector2.one * .1f; source.uv = uv; }
            if (change == "triangles") source.triangles = new[] { 0,1,3,1,2,3 };
            if (change == "winding") source.triangles = new[] { 0,2,1,0,3,2 };
            if (change == "submeshes") { source.subMeshCount = 2; source.SetTriangles(new[] {0,1,2},0); source.SetTriangles(new[] {0,2,3},1); }
            if (change == "missing-topology") saved.optimizedTriangles = null;
            var before = TransferMeshSnapshot.Capture(source);
            Assert.IsFalse(SidecarStore.ReverseLegacyTopologySafe(source, saved));
            CollectionAssert.AreEqual(before, TransferMeshSnapshot.Capture(source));
        }

        [TestCase(false)] [TestCase(true)]
        public void ReverseSidecarAcceptsEquivalentGeometryAfterVertexAndTriangleReordering(bool unreadable)
        {
            var source = Quad(); var result = Object.Instantiate(source); owned.Add(result);
            var meshEntry = new MeshEntry { fbxMesh = source, originalMesh = source, reverseTransferJson = "ancestry" };
            Assert.IsTrue(SidecarStore.TryBuildEntry(meshEntry, result, false, SidecarStore.AoUvTarget.None, out var saved));
            var p = source.vertices; var uv = source.uv;
            source.vertices = new[] {p[2],p[0],p[3],p[1]}; source.uv = new[] {uv[2],uv[0],uv[3],uv[1]};
            source.triangles = new[] {0,2,1,3,0,1};
            if (unreadable) source.UploadMeshData(true);
            Assert.IsTrue(SidecarStore.ReverseLegacyTopologySafe(source,saved));
            Assert.AreEqual(!unreadable,source.isReadable);
        }

        [Test] public void LocalRescueKeepsValidChartsAndUnwrapsNeedlesWithExactMetric()
        {
            var corners=new[] {Vector3.zero,Vector3.right,Vector3.up,new Vector3(3,0,0),new Vector3(4,0,0),new Vector3(3,1,0)};
            var uv=new[] {Vector2.zero,Vector2.right,Vector2.up,new Vector2(2,0),new Vector2(3,0),new Vector2(2,.00001f)};
            var output=ReverseUvNewCharts.Prepare(corners,uv,64,Options(),default,out var fallback);
            CollectionAssert.AreEqual(new[] {false,true},fallback);
            Assert.AreEqual(64f,(output[1]-output[0]).magnitude,.001f);
            Assert.AreEqual(64f,(output[4]-output[3]).magnitude,.001f);
            Assert.AreEqual(64f,(output[5]-output[3]).magnitude,.001f);
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=output,indices=new[] {0,1,2,3,4,5},charts=new int[6]},default);
            Assert.AreEqual(0,scan.pairs); Assert.AreEqual(0,scan.degenerateFaces);
        }

        [TestCase(0,false)] [TestCase(1,false)] [TestCase(2,false)]
        [TestCase(0,true)] [TestCase(1,true)] [TestCase(2,true)]
        public void WoodenBoxThinRescueRetainsMetricAfterAtlasTranslation(int order,bool vertical)
        {
            var captured=new[] {
                new Vector3(.6493524312973022f,.10999730229377747f,.5877611041069031f),
                new Vector3(.6493524312973022f,1.739912986755371f,.5877636075019836f),
                new Vector3(.6493524312973022f,1.860024094581604f,.5877636075019836f)};
            var corners=Enumerable.Range(0,3).Select(k=>captured[(order+k)%3]).ToArray();
            var output=ReverseUvNewCharts.Prepare(corners,new Vector2[3],32,Options(),default,out var fallback);
            Assert.IsTrue(fallback[0]);
            if(vertical) output=output.Select(p=>new Vector2(-p.y,p.x)).ToArray();
            var normalized=output.Select(p=>(p-Vector2.Min(output[0],Vector2.Min(output[1],output[2]))+(vertical?new Vector2(16,238):new Vector2(238,16)))/512).ToArray();
            if(order==0) Assert.Greater(ReverseUvTransfer.TriangleAnisotropy(corners[0],corners[1],corners[2],normalized[0],normalized[1],normalized[2]),4);
            var before=normalized.ToArray();
            ReverseUvNewCharts.Stabilize(corners,normalized,fallback,32f/512,4,1f/512);
            Assert.LessOrEqual(ReverseUvTransfer.TriangleAnisotropy(corners[0],corners[1],corners[2],
                normalized[0],normalized[1],normalized[2]),4);
            var stable=normalized.ToArray();
            ReverseUvNewCharts.Stabilize(corners,normalized,fallback,32f/512,4,1f/512);
            CollectionAssert.AreEqual(stable,normalized);
            var min=Vector2.Min(before[0],Vector2.Min(before[1],before[2]));
            var max=Vector2.Max(before[0],Vector2.Max(before[1],before[2]));
            foreach(var p in normalized)
            {
                Assert.GreaterOrEqual(p.x,min.x); Assert.GreaterOrEqual(p.y,min.y);
                Assert.LessOrEqual(p.x,min.x+Math.Ceiling(((double)max.x-min.x)*512)/512);
                Assert.LessOrEqual(p.y,min.y+Math.Ceiling(((double)max.y-min.y)*512)/512);
            }
        }

        [Test]
        public void MetricStabilizationPreservesOrdinaryAndInheritedCoordinates()
        {
            var corners=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.right,Vector3.up};
            var pixels=new[] {new Vector2(10,10),new Vector2(11,10),new Vector2(10,10.00001f),
                new Vector2(20,20),new Vector2(21,20),new Vector2(20,21)};
            var before=pixels.ToArray();
            ReverseUvNewCharts.Stabilize(corners,pixels,new[] {false,true},1,4);
            CollectionAssert.AreEqual(before,pixels);
            Assert.Throws<OperationCanceledException>(()=>ReverseUvNewCharts.Stabilize(corners,pixels,
                new[] {true,true},1,4,token:new CancellationToken(true)));
        }

        [Test]
        public void MetricStabilizationRetainsUnrepresentableRescueForPublicationRefusal()
        {
            var corners=new[] {Vector3.zero,Vector3.right,new Vector3(0,1e-6f,0)};
            var pixels=new[] {new Vector2(1048576,1048576),new Vector2(1048577,1048576),new Vector2(1048576,1048576)};
            var before=pixels.ToArray();
            Assert.IsTrue(MeshGeometry.HasArea(corners[0],corners[1],corners[2]));
            ReverseUvNewCharts.Stabilize(corners,pixels,new[] {true},1,4);
            CollectionAssert.AreEqual(before,pixels);
            Assert.IsTrue(double.IsInfinity(ReverseUvTransfer.TriangleAnisotropy(corners[0],corners[1],corners[2],pixels[0],pixels[1],pixels[2])));
        }

        [TestCase(0,false)] [TestCase(0,true)] [TestCase(2,false)] [TestCase(2,true)]
        public void AtlasPlacementPreservesCapturedKamazThinFacesAndInheritedCoordinates(int padding,bool overlap)
        {
            var positions=new[] {
                new Vector3(1.76524448f,2.80752397f,.731544495f),
                new Vector3(1.76524448f,2.80752373f,.731544495f),
                new Vector3(1.83134747f,2.80645442f,.79942143f),
                new Vector3(-.754247129f,2.82715535f,-1.72387326f),
                new Vector3(-.754247129f,2.82715511f,-1.72387326f),
                new Vector3(-.754247129f,2.82715511f,-1.81862569f),
                new Vector3(10,0,0),new Vector3(22,0,0),new Vector3(10,12,0)};
            var pixels=new[] {new Vector2(456,267),new Vector2(456,267),new Vector2(456.010681f,267.947479f),
                new Vector2(476,4),new Vector2(476,4),new Vector2(476,4.94752455f),
                Vector2.zero,Vector2.right*120,Vector2.up*120};
            Assert.IsFalse(ReverseUvNewCharts.TryPlaceTriangle(positions,0,10,4,new Vector2(456,267),1,1,out _));
            var occupied=new ReverseUvTransfer.Surface {
                pixels=new[] {Vector2.zero,Vector2.right*400,Vector2.one*400,
                    Vector2.zero,Vector2.one*400,Vector2.up*400},
                faces=new[] {new ReverseUvTransfer.Face {inherited=true},new ReverseUvTransfer.Face {inherited=true}} };
            var inherited=occupied.pixels.ToArray(); var original=pixels.ToArray(); var geometry=positions.ToArray();
            var options=Options(overlap); options.padding=padding;
            var output=ReverseUvAtlasPlacement.Place(positions,pixels,new List<ReverseUvTransfer.Surface[]> {new[] {occupied}},
                Array.Empty<ReverseUvTransfer.Surface>(),options,512,512,default,new[] {true,true,false},10);
            CollectionAssert.AreEqual(inherited,occupied.pixels); CollectionAssert.AreEqual(original,pixels);
            CollectionAssert.AreEqual(geometry,positions);
            for(int t=0;t<positions.Length;t+=3)
                Assert.LessOrEqual(ReverseUvTransfer.TriangleAnisotropy(positions[t],positions[t+1],positions[t+2],
                    output[t]/1024,output[t+1]/1024,output[t+2]/1024),4);
            Assert.AreEqual(120,(output[7]-output[6]).magnitude,1e-4);
            var all=occupied.pixels.Concat(output).ToArray();
            var scan=UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {uv=all,
                indices=Enumerable.Range(0,all.Length).ToArray(),charts=new int[all.Length]},default);
            Assert.IsTrue(scan.complete); Assert.AreEqual(0,scan.pairs); Assert.AreEqual(0,scan.degenerateFaces);
        }

        [Test]
        public void MetricAwarePlacementRefusesWhenNoRepresentableVacancyExists()
        {
            var corners=new[] {Vector3.zero,Vector3.right,new Vector3(0,1e-12f,0)};
            var pixels=new[] {Vector2.zero,Vector2.right,Vector2.zero}; var before=pixels.ToArray();
            var options=Options(); options.maxAtlasSize=128;
            Assert.Throws<InvalidOperationException>(()=>ReverseUvAtlasPlacement.Place(corners,pixels,
                new List<ReverseUvTransfer.Surface[]>(),Array.Empty<ReverseUvTransfer.Surface>(),options,128,128,
                default,new[] {true},1));
            CollectionAssert.AreEqual(before,pixels);
        }

        [Test]
        public void IntrinsicPlacementAcceptsTheExactConfiguredStretchLimit()
        {
            var corners=new[] {Vector3.zero,Vector3.right,Vector3.up};
            Assert.IsTrue(ReverseUvNewCharts.TryPlaceTriangle(corners,0,1,1,Vector2.zero,1,1,out var output));
            Assert.AreEqual(1,ReverseUvTransfer.TriangleAnisotropy(corners[0],corners[1],corners[2],output[0],output[1],output[2]));
        }

        [Test]
        public void MetricAwarePlacementKeepsAlreadyHealthyRescueLayoutsExactly()
        {
            var positions=new[] {Vector3.zero,Vector3.right,Vector3.up,new Vector3(3,0,0),new Vector3(6,0,0),new Vector3(3,2,0)};
            var pixels=new[] {Vector2.zero,Vector2.right,Vector2.up,new Vector2(10,0),new Vector2(13,0),new Vector2(10,2)};
            var previous=new List<ReverseUvTransfer.Surface[]>(); var current=Array.Empty<ReverseUvTransfer.Surface>();
            var options=Options();
            var baseline=ReverseUvAtlasPlacement.Place(positions,pixels,previous,current,options,128,128,default);
            var guarded=ReverseUvAtlasPlacement.Place(positions,pixels,previous,current,options,128,128,default,new[] {true,true},1);
            CollectionAssert.AreEqual(baseline,guarded);
        }

        [Test]
        public void ValidNewChartPromotesLocalRescueWhenTranslatedAltitudeCollapses()
        {
            var positions=new[] {Vector3.zero,Vector3.right,new Vector3(1,1e-8f,0)};
            var pixels=new[] {Vector2.zero,Vector2.right*32,new Vector2(32,3.2e-7f)};
            var occupied=new ReverseUvTransfer.Surface {pixels=new[] {Vector2.zero,new Vector2(256,0),new Vector2(256,128),
                Vector2.zero,new Vector2(256,128),new Vector2(0,128)},
                faces=new[] {new ReverseUvTransfer.Face {inherited=true},new ReverseUvTransfer.Face {inherited=true}}};
            var original=occupied.pixels.ToArray();var flags=new[] {false};
            var output=ReverseUvAtlasPlacement.Place(positions,pixels,new List<ReverseUvTransfer.Surface[]> {new[] {occupied}},
                Array.Empty<ReverseUvTransfer.Surface>(),Options(),256,256,default,flags,32);
            Assert.IsTrue(flags[0],"The local chart was healthy before translation; actual placement must identify its new rounding failure.");
            Assert.LessOrEqual(ReverseUvTransfer.TriangleAnisotropy(positions[0],positions[1],positions[2],output[0],output[1],output[2]),4);
            CollectionAssert.AreEqual(original,occupied.pixels);
        }

        [Test]
        public void NewCoplanarPatchCrossesTextureSeamsWithoutChangingSourceUv0()
        {
            var source=new Mesh {name="Texture seam",vertices=new[] {Vector3.zero,Vector3.right,Vector3.one-Vector3.forward,
                Vector3.zero,Vector3.one-Vector3.forward,Vector3.up},triangles=Enumerable.Range(0,6).ToArray(),
                uv=new[] {Vector2.zero,Vector2.right,Vector2.one,new Vector2(10,10),new Vector2(11,11),new Vector2(10,11)}};
            source.RecalculateNormals();owned.Add(source);var snapshot=TransferMeshSnapshot.Capture(source);
            using var result=Build(Options(),Level(1,Quad(3)),Level(0,source));
            var node=result.report.nodes[1];var mesh=result.meshes[1][0];
            Assert.AreEqual(1,node.faces.Select(f=>f.chart).Distinct().Count(),"A texture seam need not fragment a new planar lightmap patch.");
            foreach(var group in Enumerable.Range(0,mesh.vertexCount).GroupBy(i=>mesh.vertices[i]))
                Assert.AreEqual(1,group.Select(i=>mesh.uv2[i]).Distinct().Count());
            Clean(mesh);CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(source));
        }

        [TestCase(10)] [TestCase(32)]
        public void SeedPackingRetainsCapturedKitchenThinFaceBeforeLargeCharts(int density)
        {
            var positions=new List<Vector3> {
                new Vector3(-.700723529f,.0639223382f,-.188836977f),
                new Vector3(-.70027411f,.0639223307f,-.188836962f),
                new Vector3(-.013632426f,.0639104322f,-.188823327f)};
            var uv=new List<Vector2> {Vector2.zero,Vector2.zero,Vector2.zero};
            for(int q=0;q<40;++q)
                foreach(int k in new[] {0,1,2,0,2,3})
                {
                    var p=new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up}[k];
                    positions.Add(new Vector3(p.x+q*3,p.y,1));uv.Add(p+Vector2.right*(q*3+10));
                }
            var source=positions.ToArray();var original=uv.ToArray();
            var pixels=ReverseUvSeedPacking.Prepare(source,original,density,true,
                new ReverseUvTransfer.Options {padding=2,seedResolution=256},default,out int side);
            Assert.AreEqual(source.Length,pixels.Length,"A positive-area sliver must not be silently discarded.");
            Assert.IsTrue(MeshGeometry.HasArea(source[0],source[1],source[2]));
            Assert.Less(ReverseUvTransfer.TriangleAnisotropy(source[0],source[1],source[2],pixels[0]/side,pixels[1]/side,pixels[2]/side),1.05);
            var mesh=new Mesh {vertices=source.Take(3).ToArray(),triangles=new[] {0,1,2}};owned.Add(mesh);
            var quality=TransferUvQuality.Measure(mesh,pixels.Take(3).Select(p=>p/side).ToArray(),Vector2.one,Matrix4x4.identity);
            Assert.AreEqual(0,quality.degenerateFaces);Assert.Less(quality.worstAnisotropy,1.05,
                "Export audit and placement must use the same geometry metric.");
            CollectionAssert.AreEqual(positions,source);CollectionAssert.AreEqual(uv,original);
        }

        [TestCase(0)] [TestCase(32)]
        public void SeedPreparationIgnoresStretchedTextureUvAndUsesOneWorldMetric(int density)
        {
            var a=Quad(); var b=Quad(3); a.uv=a.uv.Select(p=>new Vector2(p.x,p.y*.5f)).ToArray();
            var transform=Matrix4x4.Scale(new Vector3(1,2,1)); var before=a.uv;
            var inputs=new[] {new ReverseUvTransfer.Input {mesh=a,toWorld=transform},new ReverseUvTransfer.Input {mesh=b,toWorld=transform}};
            var outputs=ReverseUvSeed.Prepare(inputs,128,2,density,default,out int side);
            owned.AddRange(outputs);
            var levels=new[] {new ReverseUvTransfer.Level {lod=1,inputs=inputs.Select((input,i)=>new ReverseUvTransfer.Input {mesh=outputs[i],toWorld=transform}).ToArray()},
                new ReverseUvTransfer.Level {lod=0,inputs=inputs}};
            using var result=Build(new ReverseUvTransfer.Options {boundaryToleranceTexels=BoundaryTolerance(),seedResolution=side,projectionReach=.1f},levels);
            Assert.AreEqual(4,result.report.inheritedFaces); Assert.AreEqual(0,result.report.newFaces);
            foreach(var mesh in outputs)
            {
                var quality=TransferUvQuality.Measure(mesh,mesh.uv2,Vector2.one,transform);
                Assert.Less(quality.worstAnisotropy,1.05);
            }
            if(density>0) Assert.AreEqual(density,result.report.texelsPerUnit,.1f);
            CollectionAssert.AreEqual(before,a.uv);
        }

        [TestCase(false)] [TestCase(true)]
        public void DefaultSeedPreservesAuthoredSeamsAndVertexStreams(bool overlap)
        {
            var source=new Mesh {name="AuthoredSeed"};owned.Add(source);
            source.vertices=new[] {Vector3.zero,Vector3.right,new Vector3(1,1,0),
                Vector3.zero,new Vector3(1,1,0),Vector3.up};
            source.triangles=Enumerable.Range(0,6).ToArray();
            source.uv=new[] {Vector2.zero,Vector2.right,Vector2.one,
                new Vector2(2,0),new Vector2(3,1),new Vector2(2,1)};
            source.uv2=Enumerable.Repeat(Vector2.zero,6).ToArray();
            source.tangents=Enumerable.Range(0,6).Select(i=>new Vector4(1,0,0,i<3?1:-1)).ToArray();
            source.colors=Enumerable.Range(0,6).Select(i=>new Color(i/6f,1,0)).ToArray();
            source.subMeshCount=2;source.SetTriangles(new[] {0,1,2},0);source.SetTriangles(new[] {3,4,5},1);
            var before=TransferMeshSnapshot.Capture(source);
            var options=Options(overlap);options.repackFinalAtlas=true;
            Assert.IsFalse(options.rebuildSeedWithUnity);
            using var state=ReverseUvJunctionTrial.Build(new[] {Level(1,source),Level(0,source)},options,true,32).GetAwaiter().GetResult();
            Assert.AreEqual("source-charts-local-repair",state.result.report.seedPreparation);
            var mesh=state.result.meshes[0][0];var indices=mesh.triangles;var uv=mesh.uv2;
            Assert.Greater((uv[indices[0]]-uv[indices[3]]).magnitude,1f/state.result.report.atlasSize,
                "The authored seam must survive seed preparation and final family packing.");
            Assert.AreEqual(2,mesh.subMeshCount);Assert.AreEqual(2,state.result.report.inheritedFaces);
            for(int i=0;i<indices.Length;++i)
            {
                Assert.AreEqual(source.vertices[i],mesh.vertices[indices[i]]);
                Assert.AreEqual(source.tangents[i],mesh.tangents[indices[i]]);
                Assert.AreEqual(source.colors[i],mesh.colors[indices[i]]);
                Assert.AreEqual(source.uv[i],mesh.uv[indices[i]]);
            }
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(source));
        }

        [TestCase(false)] [TestCase(true)]
        public void SeedChartingModeIsExplicitAndMissingTextureUvGetsLocalRepair(bool rebuildWithUnity)
        {
            var source=Quad();source.uv=Array.Empty<Vector2>();
            var before=TransferMeshSnapshot.Capture(source);
            var options=Options();options.rebuildSeedWithUnity=rebuildWithUnity;
            using var state=ReverseUvJunctionTrial.Build(new[] {Level(1,source),Level(0,source)},options,true,32).GetAwaiter().GetResult();
            Assert.AreEqual(rebuildWithUnity?"unity-unwrapping":"source-charts-local-repair",state.result.report.seedPreparation);
            foreach(var level in state.result.meshes) foreach(var mesh in level) Clean(mesh);
            Assert.AreEqual(2,state.result.report.inheritedFaces);
            CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(source));
        }

        [TestCase(false)] [TestCase(true)]
        public void SeedPackingNormalizesEachChartAndRepairsInvalidFaces(bool fixedDensity)
        {
            var positions = new List<Vector3>(); var uv = new List<Vector2>();
            for (int q = 0; q < 4; ++q)
                foreach (int k in new[] { 0, 1, 2, 0, 2, 3 })
                {
                    var p = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }[k];
                    positions.Add(new Vector3(p.x + q * 3, p.y, 0));
                    uv.Add(q == 3 ? new Vector2(100,100) : p * (q + 1) * 7 + new Vector2(q * 30, 0));
                }
            var original = uv.ToArray();
            var options=new ReverseUvTransfer.Options {seedResolution=128,padding=2};
            var pixels = ReverseUvSeedPacking.Prepare(positions.ToArray(), uv.ToArray(), 32, fixedDensity,
                options, default, out int side);
            var scan = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = pixels,
                indices = Enumerable.Range(0,pixels.Length).ToArray(), charts = new int[pixels.Length] }, default);
            Assert.IsTrue(scan.complete); Assert.AreEqual(0,scan.pairs); Assert.AreEqual(0,scan.degenerateFaces);
            float density = (pixels[1] - pixels[0]).magnitude;
            Assert.AreEqual(density,options.seedDensity,1e-3,"Finer charts must inherit the actual compact seed density.");
            if (fixedDensity) Assert.AreEqual(32,density,1e-3);
            else { Assert.AreEqual(128,side); Assert.Greater(density,32); }
            for (int t = 0; t < pixels.Length; t += 3)
            {
                Assert.AreEqual(density,(pixels[t+1]-pixels[t]).magnitude/(positions[t+1]-positions[t]).magnitude,1e-3);
                Assert.Less(ReverseUvTransfer.TriangleAnisotropy(positions[t],positions[t+1],positions[t+2],
                    pixels[t],pixels[t+1],pixels[t+2]),1.001);
            }
            CollectionAssert.AreEqual(original,uv);
            Assert.IsTrue(pixels.All(p=>p.x>=2 && p.y>=2 && p.x<=side-2 && p.y<=side-2));
        }

        [Test]
        public void SeedQuarterTurnAvoidsAtlasGrowthWithoutChangingDensity()
        {
            var positions = new List<Vector3>(); var uv = new List<Vector2>();
            foreach (var size in new[] { new Vector2(13,4),new Vector2(4,13) })
            {
                int q = positions.Count / 6;
                foreach (int k in new[] {0,1,2,0,2,3})
                {
                    var p = Vector2.Scale(new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up}[k],size);
                    positions.Add(new Vector3(p.x+q*20,p.y,0)); uv.Add(p+Vector2.right*q*20);
                }
            }
            foreach (bool rotate in new[] {false,true})
            {
                var pixels = ReverseUvSeedPacking.Prepare(positions.ToArray(),uv.ToArray(),1,true,
                    new ReverseUvTransfer.Options {boundaryToleranceTexels=BoundaryTolerance(),seedResolution=16,padding=0,rotateCharts=rotate,rotateChartsToAxis=false},default,out int side);
                Assert.AreEqual(rotate?16:32,side);
                for (int t=0;t<pixels.Length;t+=3)
                {
                    Assert.AreEqual((positions[t+1]-positions[t]).magnitude,(pixels[t+1]-pixels[t]).magnitude,1e-4);
                    Assert.Less(ReverseUvTransfer.TriangleAnisotropy(positions[t],positions[t+1],positions[t+2],pixels[t],pixels[t+1],pixels[t+2]),1.001);
                }
            }
        }

        [Test]
        public void NormalizedOriginalSeedPreservesDensityWithinFloat32AreaUncertainty()
        {
            var positions=new[] {Vector3.zero,new Vector3(10,0,0),new Vector3(0,10,0),
                new Vector3(20,0,0),new Vector3(23,0,0),new Vector3(20,.0001f,0)};
            var uv=new[] {Vector2.zero,new Vector2(10,0),new Vector2(0,10),
                new Vector2(257,123.45f),new Vector2(260,123.45f),new Vector2(257,123.4501f)};
            var options=new ReverseUvTransfer.Options {boundaryToleranceTexels=BoundaryTolerance(),seedResolution=512,padding=2};
            var old=ReverseUvNewCharts.Prepare(positions,uv,1,options,default,out var fallback);
            Assert.IsTrue(fallback.All(f=>!f));
            var min=old.Aggregate(Vector2.Min);
            var expected=old.Select(p=>p-min+Vector2.one*options.padding).ToArray();
            var actual=ReverseUvSeedPacking.Prepare(positions,uv,1,true,options,default,out int side,false);
            Assert.AreEqual(512,side); CollectionAssert.AreEqual(expected,actual);
        }

        [Test]
        public void NormalizedOriginalSeedPreservesAlreadyRepairedTriangles()
        {
            var positions=new[] {Vector3.zero,new Vector3(10,0,0),new Vector3(0,10,0),
                new Vector3(20,0,0),new Vector3(23,0,0),new Vector3(19.3f,.04f,0)};
            var uv=new[] {Vector2.zero,new Vector2(10,0),new Vector2(0,10),
                Vector2.zero,Vector2.zero,Vector2.zero};
            var options=new ReverseUvTransfer.Options {boundaryToleranceTexels=BoundaryTolerance(),seedResolution=128,padding=2};
            var repaired=ReverseUvNewCharts.Prepare(positions,uv,1,options,default,out var fallback);
            Assert.IsTrue(fallback[1]);
            var min=repaired.Aggregate(Vector2.Min);
            var expected=repaired.Select(p=>p-min+Vector2.one*options.padding).ToArray();
            var actual=ReverseUvSeedPacking.Prepare(positions,uv,1,true,options,default,out _,false);
            CollectionAssert.AreEqual(expected,actual);
        }
    }
}
