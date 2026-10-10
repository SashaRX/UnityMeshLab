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
