using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodScreenValidationTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        Mesh Track(Mesh mesh) { meshes.Add(mesh); return mesh; }
        [TearDown] public void Cleanup() { foreach (var mesh in meshes) if (mesh) Object.DestroyImmediate(mesh); meshes.Clear(); }
        Mesh Panels(bool detail = true,float detailSize = .02f)
        {
            var positions = new List<Vector3>(); var colors = new List<Color>(); var indices = new List<int>();
            void Add(float x,float y,float width,float height,float z,Color color)
            {
                int i = positions.Count;
                positions.AddRange(new[] {new Vector3(x,y,z),new Vector3(x+width,y,z),new Vector3(x+width,y+height,z),new Vector3(x,y+height,z)});
                colors.AddRange(Enumerable.Repeat(color,4)); indices.AddRange(new[] {i,i+1,i+2,i,i+2,i+3});
            }
            Add(-1,-1,1,2,0,new Color(1,0,0,.2f)); Add(0,-1,1,2,0,new Color(1,0,0,.8f));
            if (detail) Add(1.2f,0,detailSize,detailSize,0,Color.white);
            var mesh = Track(new Mesh { vertices = positions.ToArray(),colors = colors.ToArray(),triangles = indices.ToArray() });
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        [Test]
        public void SourceTopologySeparatesContinuousRgbaFromPaintAndMaterialInterfaces()
        {
            var continuous=Panels(false);
            continuous.colors=continuous.vertices.Select(p => new Color(20*p.x,0,0,1)).ToArray();
            var colors=continuous.colors; var indices=continuous.triangles;
            // Normal splits do not turn a continuous vertex-color field into paint.
            continuous.normals=continuous.vertices.Select((p,i) => i<4 ? Vector3.forward : Vector3.up).ToArray();
            var screen=new LodScreenValidation(continuous,true,resolution:72,objectPixels:64,measureFields:true);
            var exact=screen.Measure(continuous);
            Assert.That(exact.sharpColorPairs,Is.Zero); Assert.That(exact.gradientPairs,Is.GreaterThan(100));
            Assert.That(exact.gradientWorstRegionRms,Is.Zero); CollectionAssert.AreEqual(colors,continuous.colors);
            CollectionAssert.AreEqual(indices,continuous.triangles);
            continuous.subMeshCount=2; continuous.SetTriangles(indices.Take(6).ToArray(),0); continuous.SetTriangles(indices.Skip(6).ToArray(),1);
            screen=new LodScreenValidation(continuous,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(screen.Measure(continuous).sharpColorPairs,Is.GreaterThan(0),"Material boundaries remain conservative.");
            var painted=Panels(false);
            screen=new LodScreenValidation(painted,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(screen.Measure(painted).sharpColorPairs,Is.GreaterThan(0));
            Assert.That(screen.Measure(painted).gradientPairs,Is.Zero);
        }

        [Test]
        public void GradientRegionLossCannotHideBehindBetterGlobalRgba()
        {
            var before=new LodScreenValidation.Report { rgbaRms=.2f,gradientWorstRegionRms=.1f,gradientMeanRms=.05f,gradientViewRms=new[] { .05f } };
            var after=new LodScreenValidation.Report { rgbaRms=.1f,gradientWorstRegionRms=.3f,gradientMeanRms=.1f,gradientViewRms=new[] { .1f } };
            Assert.That(after.DoesNotWorsen(before),Is.False); Assert.That(after.PaintViewsDoNotWorsen(before),Is.False);
            Assert.That(after.ColorsDoNotWorsen(before),Is.False);
            before.gradientWorstRegionRms=after.gradientWorstRegionRms=.3f;
            before.gradientMeanRms=after.gradientMeanRms=.05f;
            before.gradientViewRms=after.gradientViewRms=new[] { .05f };
            before.gradientRegionRms=new[] { .3f,.01f }; after.gradientRegionRms=new[] { .2f,.1f };
            Assert.That(after.PaintViewsDoNotWorsen(before),Is.False,"A small region cannot hide behind a better worst/mean error.");
        }

        [Test]
        public void LostGradientFacesStillParticipateInDiagonalCorrections()
        {
            var source=Panels(false); source.colors=source.vertices.Select(p => new Color(20*p.x,0,0,1)).ToArray();
            var reduced=Track(Object.Instantiate(source)); reduced.colors=Enumerable.Repeat(Color.black,reduced.vertexCount).ToArray();
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(screen.Measure(reduced).sharpColorPairs,Is.Zero);
            Assert.That(screen.LostPaintFaces(reduced),Is.Not.Empty);
        }

        [Test]
        public void ConnectedColorFieldStillProtectsAbruptProfileTransitions()
        {
            var source=Panels(false);
            source.colors=source.vertices.Select(p => new Color(20*(1-Mathf.Abs(p.x)),0,0,1)).ToArray();
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var exact=screen.Measure(source);
            Assert.That(exact.sharpColorPairs,Is.GreaterThan(0),"Connected faces do not authorize flattening a narrow color peak.");
            Assert.That(exact.gradientPairs,Is.GreaterThan(100));
            Assert.That(exact.gradientWorstRegionRms,Is.Zero);
        }

        [Test]
        public void BoundaryTrainingWeightsAlphaEdgesWithoutBorrowingAcrossMaterials()
        {
            var source = Panels(false); var target = Panels(false); var colors = source.colors;
            var validation = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var samples = new List<(Vector4 desired,double weight)>();
            int count = validation.AddVisibleBoundaryColorSamples(target,(_,__,___,desired,weight) => samples.Add((desired,weight)),8);
            Assert.That(count,Is.EqualTo(samples.Count).And.GreaterThan(0));
            Assert.That(samples.Any(s => s.weight == 2),Is.True,"Sharp alpha edges need the same weight as RGB edges.");
            Assert.That(samples.Any(s => s.weight == .25),Is.True,"Interior samples must still constrain the field.");
            Assert.That(samples.All(s => s.desired.w >= .2f-1e-6f && s.desired.w <= .8f+1e-6f),Is.True);
            CollectionAssert.AreEqual(colors,source.colors);
            var indices = target.triangles; target.subMeshCount = 2; target.SetTriangles(new int[0],0); target.SetTriangles(indices,1);
            Assert.That(validation.AddVisibleBoundaryColorSamples(target,(_,__,___,____,_____) => Assert.Fail("Different materials cannot lend color."),8),Is.Zero);
            Assert.Throws<System.OperationCanceledException>(() => validation.AddVisibleBoundaryColorSamples(source,(_,__,___,____,_____) => {},8,() => true));
        }
        [Test]
        public void SmoothColorGradientDoesNotReceiveSharpBoundaryTrainingWeights()
        {
            var source = Panels(false);
            source.colors = source.vertices.Select(p => new Color(2+.01f*p.x,-1,3,.3f)).ToArray();
            var validation = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(validation.AddVisibleBoundaryColorSamples(source,(_,__,___,desired,weight) => {
                Assert.That(weight,Is.EqualTo(.25)); Assert.That(desired.z,Is.EqualTo(3).Within(1e-6));
            },8),Is.GreaterThan(0));
        }
        [Test]
        public void SelfComparisonPreservesAlphaBoundaryAndMissingDetailIsLocalLoss()
        {
            var source = Panels(true,.08f); var validation = new LodScreenValidation(source,true);
            var self = validation.Measure(source);
            Assert.That(self.detailLoss,Is.Zero); Assert.That(self.colorLoss,Is.Zero);
            Assert.That(self.colorViews,Is.GreaterThan(0)); Assert.That(self.colorAccepted,Is.True);
            Assert.That(validation.Measure(Panels(false)).detailLoss,Is.GreaterThan(.9f));
        }
        [Test]
        public void FarFootprintIgnoresUnresolvablePartsButKeepsLargePaintBoundaries()
        {
            var source = Panels(true,.04f); var target = Panels(false);
            Assert.That(new LodScreenValidation(source,true).Measure(target).detailLoss,Is.GreaterThan(.9f));
            var far = new LodScreenValidation(source,true,resolution:72,objectPixels:64);
            var report = far.Measure(target);
            Assert.That(report.objectPixels,Is.EqualTo(64)); Assert.That(report.resolution,Is.EqualTo(72));
            Assert.That(report.detailLoss,Is.Zero,"A subpixel component must not count as a visible lost region.");
            Assert.That(report.colorViews,Is.GreaterThan(0));
            target.colors = Enumerable.Repeat(new Color(1,0,0,.5f),target.vertexCount).ToArray();
            Assert.That(far.Measure(target).colorLoss,Is.GreaterThan(.9f),"Broad authored alpha regions remain visible at 64 pixels.");
        }
        [Test]
        public void DepthTestingIgnoresHiddenColorAndReversedWinding()
        {
            var source = Panels(false); var target = Track(Object.Instantiate(source));
            target.triangles = source.triangles.Reverse().ToArray();
            var validation = new LodScreenValidation(source,true);
            Assert.That(validation.Measure(target).colorLoss,Is.Zero);
            // Close the painted front into a box; a green interior plane is occluded in every view.
            var box = new[] {new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0),
                new Vector3(-1,-1,-.1f),new Vector3(1,-1,-.1f),new Vector3(1,1,-.1f),new Vector3(-1,1,-.1f)};
            var faces = new[] {4,6,5,4,7,6,0,5,1,0,4,5,3,2,6,3,6,7,0,3,7,0,7,4,1,5,6,1,6,2};
            target.vertices = source.vertices.Concat(box).ToArray();
            target.colors = source.colors.Concat(Enumerable.Repeat(Color.white,box.Length)).ToArray();
            target.triangles = source.triangles.Concat(faces.Select(i => i+source.vertexCount)).ToArray(); target.RecalculateBounds();
            var closed = new LodScreenValidation(target,true);
            var hidden = Track(Object.Instantiate(target)); int start = hidden.vertexCount;
            hidden.vertices = hidden.vertices.Concat(new[] {new Vector3(-.8f,-.8f,-.05f),new Vector3(.8f,-.8f,-.05f),new Vector3(.8f,.8f,-.05f),new Vector3(-.8f,.8f,-.05f)}).ToArray();
            hidden.colors = target.colors.Concat(Enumerable.Repeat(Color.green,4)).ToArray();
            hidden.triangles = target.triangles.Concat(new[] {start,start+1,start+2,start,start+2,start+3}).ToArray(); hidden.RecalculateBounds();
            Assert.That(closed.Measure(hidden).colorLoss,Is.Zero);
        }
        [Test]
        public void AlphaBoundaryLossIsMeasuredOnlyWhenColorChecksEnabled()
        {
            var source = Panels(false); var target = Track(Object.Instantiate(source));
            target.colors = Enumerable.Repeat(new Color(1,0,0,.5f),target.vertexCount).ToArray();
            Assert.That(new LodScreenValidation(source,true).Measure(target).colorLoss,Is.GreaterThan(.9f));
            var disabled = new LodScreenValidation(source,false).Measure(target);
            Assert.That(disabled.colorViews,Is.Zero); Assert.That(disabled.colorAccepted,Is.False);
            Assert.That(disabled.colorLoss,Is.Zero);
        }
        [Test]
        public void WholePartGuardRetainsVisiblePartsAndAllowsSubpixelRemoval()
        {
            var source = Panels(true,.08f); var retained = Panels(false);
            Assert.That(LodSmallParts.ScreenSafe(source,retained,false,Matrix4x4.identity,100),Is.False);
            Assert.That(LodSmallParts.ScreenSafe(source,retained,false,Matrix4x4.identity,4),Is.True);
            Assert.That(LodSmallParts.ScreenSafe(source,source,true,Matrix4x4.Scale(new Vector3(2,1,.5f)),30),Is.True);
            Assert.That(LodSmallParts.ScreenSafe(source,retained,false,Matrix4x4.identity,1000),Is.False,"Oversized probes retain parts instead of shrinking visible details.");
        }
        [Test]
        public void CancellationStopsSourceAndTargetRasterization()
        {
            var source = Panels();
            Assert.Throws<System.OperationCanceledException>(() => new LodScreenValidation(source,true,() => true));
            var validation = new LodScreenValidation(source,true);
            Assert.Throws<System.OperationCanceledException>(() => validation.Measure(source,() => true));
        }
        [Test]
        public void LocalPenaltyAffectsComparableCandidatesButPreservesBudgetPriorityAndFarWeights()
        {
            var loss = new LodScreenValidation.Report { detailLoss = .5f,colorLoss = 1 };
            Assert.That(loss.Penalty(.25f),Is.LessThan(loss.Penalty(1)));
            Assert.That(LodBudgetTriangleSimplifier.Better(100,1,100,1+loss.Penalty(1),100),Is.True);
            Assert.That(LodBudgetTriangleSimplifier.Better(101,0,100,loss.Penalty(1),100),Is.False);
            Assert.That(new LodScreenValidation.Report { detailLoss = .49f,colorLoss = 1.01f }.DoesNotWorsen(loss),Is.False);
        }
        [Test]
        public void VisibleFieldsMeasureNormalColorAndSilhouetteErrorsAtFarSize()
        {
            var source = Panels(false); var target = Track(Object.Instantiate(source));
            var validation = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var self = validation.Measure(target);
            Assert.That(self.silhouetteMax,Is.Zero); Assert.That(self.normalRms,Is.Zero); Assert.That(self.rgbaRms,Is.Zero);
            target.normals = Enumerable.Repeat(Vector3.right,target.vertexCount).ToArray();
            target.colors = Enumerable.Repeat(Color.green,target.vertexCount).ToArray();
            var changed = validation.Measure(target);
            Assert.That(changed.normalRms,Is.GreaterThan(89)); Assert.That(changed.rgbaRms,Is.GreaterThan(.5f));
            Assert.That(changed.silhouetteMean,Is.Zero); Assert.That(changed.ScreenScore(1,1),Is.GreaterThan(self.ScreenScore(1,1)));
            Assert.That(changed.DoesNotWorsen(self,true),Is.False);
            target.vertices = target.vertices.Select(v => v*.5f).ToArray(); target.RecalculateBounds();
            Assert.That(validation.Measure(target).silhouetteMean,Is.GreaterThan(.5f));
        }
        [Test]
        public void SilhouetteRefitImprovesCoverageWithoutChangingTopologyAttributesOrInputs()
        {
            var source = Panels(false); var target = Track(Object.Instantiate(source));
            target.vertices = target.vertices.Select(p => p*.9f).ToArray(); target.RecalculateBounds();
            target.uv2 = target.vertices.Select(p => new Vector2(p.x,p.y)).ToArray();
            var positions = target.vertices; var indices = target.triangles; var colors = target.colors; var uv = target.uv2;
            var validation = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var before = validation.Measure(target);
            var fitted = Track(validation.RefineSilhouette(target,out int moves,out int trials));
            Assert.That(fitted,Is.Not.Null); Assert.That(moves,Is.GreaterThan(0)); Assert.That(trials,Is.GreaterThan(moves));
            var after = validation.Measure(fitted);
            Assert.That(after.silhouetteMean,Is.LessThan(before.silhouetteMean)); Assert.That(after.silhouetteMax,Is.LessThanOrEqualTo(before.silhouetteMax));
            CollectionAssert.AreEqual(indices,fitted.triangles); CollectionAssert.AreEqual(colors,fitted.colors); CollectionAssert.AreEqual(uv,fitted.uv2);
            CollectionAssert.AreEqual(positions,target.vertices);
            var values = fitted.vertices;
            for (int i = 0; i < values.Length; i++)
            {
                Assert.That(Vector3.Distance(values[i],positions[i]),Is.LessThanOrEqualTo(4f/64+1e-6f));
                for (int j = 0; j < i; j++) if (positions[i].Equals(positions[j])) Assert.That(values[i],Is.EqualTo(values[j]));
            }
            for (int f = 0; f < indices.Length; f += 3)
                Assert.That(Vector3.Dot(Vector3.Cross(values[indices[f+1]]-values[indices[f]],values[indices[f+2]]-values[indices[f]]),Vector3.forward),Is.GreaterThan(0));
        }
        [Test]
        public void SilhouetteRefitPinsMaterialInterfacesAndCancelsWithoutAnOwnedMeshLeak()
        {
            var source = Panels(false); var target = Track(Object.Instantiate(source));
            target.vertices = target.vertices.Select(p => p*.9f).ToArray(); target.RecalculateBounds();
            target.subMeshCount = 2; target.SetTriangles(new[] {0,1,2,0,2,3},0); target.SetTriangles(new[] {4,5,6,4,6,7},1);
            var validation = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var fitted = Track(validation.RefineSilhouette(target,out _,out _));
            Assert.That(fitted,Is.Not.Null);
            foreach (int vertex in new[] {1,2,4,7}) Assert.That(fitted.vertices[vertex],Is.EqualTo(target.vertices[vertex]));
            target.name = "RefitCancel_"+System.Guid.NewGuid().ToString("N"); int checks = 0;
            Assert.Throws<System.OperationCanceledException>(() => validation.RefineSilhouette(target,out _,out _,() => ++checks > 100));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Where(m => m.name == target.name).ToArray(),Is.EqualTo(new[] {target}));
        }
        [Test]
        public void VisibleColorTrainingDoesNotCrossMaterialsOrOppositeGeometricFaces()
        {
            var source = Panels(false); var target = Track(Object.Instantiate(source));
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(screen.AddVisibleColorSamples(target,(indices,at,bary,value,weight) => {
                Assert.That(bary.x+bary.y+bary.z,Is.EqualTo(1).Within(1e-6f)); Assert.That(weight,Is.EqualTo(.25));
            }),Is.GreaterThan(100));
            Assert.That(screen.AddVisibleNormalSamples(target,(_,__,bary,n,weight) => {
                Assert.That(((Vector3)n).magnitude,Is.EqualTo(1).Within(1e-6f)); Assert.That(weight,Is.EqualTo(.25));
                Assert.That(Vector3.Dot((Vector3)n,Vector3.forward),Is.GreaterThan(.99f));
            }),Is.GreaterThan(100));
            target.triangles = target.triangles.Reverse().ToArray();
            Assert.That(screen.AddVisibleColorSamples(target,(_,__,___,____,_____) => Assert.Fail("Opposite face borrowed color")),Is.Zero);
            Assert.That(screen.AddVisibleNormalSamples(target,(_,__,___,____,_____) => Assert.Fail("Opposite face borrowed normal")),Is.Zero);
            target.triangles = source.triangles; target.subMeshCount = 2;
            target.SetTriangles(System.Array.Empty<int>(),0); target.SetTriangles(source.triangles,1);
            Assert.That(screen.AddVisibleColorSamples(target,(_,__,___,____,_____) => Assert.Fail("Material slot borrowed color")),Is.Zero);
            Assert.That(screen.AddVisibleNormalSamples(target,(_,__,___,____,_____) => Assert.Fail("Material slot borrowed normal")),Is.Zero);
            Assert.Throws<System.OperationCanceledException>(() => screen.AddVisibleColorSamples(source,(_,__,___,____,_____) => {},() => true));
            Assert.Throws<System.OperationCanceledException>(() => screen.AddVisibleNormalSamples(source,(_,__,___,____,_____) => {},() => true));
        }
        [Test]
        public void FarBudgetUsesUnprotectedCandidatesAndMeasuresAllVariantsAt64Pixels()
        {
            var source = Panels(false); var positions = source.vertices; var colors = source.colors;
            var result = LodBudgetTriangleSimplifier.Simplify(source,new MeshSimplifier.SimplifySettings {
                targetRatio = .5f,targetError = .3f,normalWeight = 1,colorWeight = 1,preserveHardEdges = true },
                new LodPipelineOps.Options { screenBudget = true,candidateCount = 3 },out var diagnostics,out string note,null);
            Track(result.simplifiedMesh); Assert.That(result.ok,Is.True,result.error);
            Assert.That(result.simplifiedTriCount,Is.LessThanOrEqualTo(2));
            Assert.That(diagnostics.screenQuality.objectPixels,Is.EqualTo(64));
            Assert.That(diagnostics.budgetCandidates.All(c => c.screenQuality.normalsEvaluated && c.screenQuality.objectPixels == 64),Is.True);
            Assert.That(diagnostics.budgetCandidates.Take(3).Select(c => c.name),Is.EqualTo(new[] {"balanced","shape","RGBA"}));
            Assert.That(note,Does.Contain("Far screen budget"));
            CollectionAssert.AreEqual(positions,source.vertices); CollectionAssert.AreEqual(colors,source.colors);
        }
        [Test]
        public void BudgetGenerationMeasuresEveryVariantAndKeepsTheSourceUntouched()
        {
            var source = Panels(); var positions = source.vertices; var colors = source.colors;
            var result = LodBudgetTriangleSimplifier.Simplify(source,new MeshSimplifier.SimplifySettings {
                targetRatio = .75f,targetError = .2f,colorWeight = 1,normalWeight = 1 },
                new LodPipelineOps.Options { candidateCount = 3,screenGuidedSelection = true,correctSurfaceAttributes = true,maxColorError = .1f },
                out var diagnostics,out string note,null);
            Track(result.simplifiedMesh); Assert.That(result.ok,Is.True,result.error);
            Assert.That(diagnostics.screenQuality,Is.Not.Null);
            Assert.That(diagnostics.budgetCandidates.All(c => c.screenQuality != null),Is.True);
            Assert.That(note,Does.Contain("CPU screen guide"));
            CollectionAssert.AreEqual(positions,source.vertices); CollectionAssert.AreEqual(colors,source.colors);
        }
    }
}
