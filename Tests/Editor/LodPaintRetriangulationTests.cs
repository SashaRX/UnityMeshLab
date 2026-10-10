using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodPaintRetriangulationTests
    {
        readonly List<Mesh> owned=new List<Mesh>();
        Mesh Track(Mesh mesh) { if (mesh) owned.Add(mesh); return mesh; }
        [TearDown] public void Cleanup() { foreach (var mesh in owned) if (mesh) Object.DestroyImmediate(mesh); owned.Clear(); }
        static MeshSimplifier.SimplifySettings Settings => new MeshSimplifier.SimplifySettings { uvChannel=1,uv2Weight=20,normalWeight=1,colorWeight=1 };
        Mesh Panel(bool source,bool packed=false,bool seam=false)
        {
            var points=new[] { new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(0,1,0) };
            var corners=source ? new[] { 3,1,2,1,3,0 } : new[] { 0,1,2,2,3,0 };
            var p=corners.Select(v => points[v]).ToArray();
            var mesh=Track(new Mesh { name="PaintDiagonalPanel",vertices=p,normals=p.Select(_ => Vector3.forward).ToArray(),
                tangents=p.Select(_ => new Vector4(1,0,0,1)).ToArray() });
            var colors=Enumerable.Range(0,6).Select(i => i<3 ? Color.red : Color.blue).ToArray();
            if (packed) mesh.SetColors(colors.Select(c => (Color32)c).ToArray()); else mesh.colors=colors;
            mesh.SetUVs(0,p.Select(v => new Vector3(v.x,v.y,.2f)).ToList());
            mesh.uv2=p.Select((v,i) => new Vector2(v.x+(seam && i>=3 ? 2 : 0),v.y)).ToArray();
            mesh.SetUVs(3,p.Select(v => new Vector4(v.x,v.y,.3f,.4f)).ToList());
            mesh.triangles=Enumerable.Range(0,6).ToArray(); return mesh;
        }

        [TestCase(false)] [TestCase(true)]
        public void SourcePaintDiagonalRestoresBoundaryWithoutAddingRenderCorners(bool packed)
        {
            var source=Panel(true,packed); var reduced=Panel(false,packed);
            var originalPositions=reduced.vertices; var originalUv=reduced.uv2; var originalColors=reduced.colors;
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var result=Track(LodPaintRetriangulation.Correct(source,reduced,screen,Settings,out var report));
            Assert.That(result,Is.Not.Null,JsonUtility.ToJson(report)); Assert.That(report.accepted,Is.True); Assert.That(report.flips,Is.EqualTo(1));
            Assert.That(report.boundaryAfter,Is.LessThan(report.boundaryBefore));
            Assert.That(result.vertexCount,Is.EqualTo(6)); Assert.That(LodMeshData.TriangleCount(result),Is.EqualTo(2));
            Assert.That(result.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(reduced.GetVertexAttributeFormat(VertexAttribute.Color)));
            foreach (var i in Enumerable.Range(0,result.vertexCount))
            {
                var p=result.vertices[i]; Assert.That(originalPositions,Does.Contain(p));
                Assert.That(result.uv2[i],Is.EqualTo(new Vector2(p.x,p.y)));
                Assert.That(result.normals[i],Is.EqualTo(Vector3.forward));
            }
            Assert.That(result.GetVertexAttributeDimension(VertexAttribute.TexCoord0),Is.EqualTo(3));
            Assert.That(result.GetVertexAttributeDimension(VertexAttribute.TexCoord3),Is.EqualTo(4));
            Assert.That(report.uvViewsAfter.Zip(report.uvViewsBefore,(a,b) => a<=b+1e-6f).All(v => v),Is.True);
            CollectionAssert.AreEqual(originalPositions,reduced.vertices); CollectionAssert.AreEqual(originalUv,reduced.uv2); CollectionAssert.AreEqual(originalColors,reduced.colors);
        }

        [Test]
        public void PaintSeamSamplesDoNotDependOnTriangleOrder()
        {
            var source=Panel(true); var reordered=Track(Object.Instantiate(source));
            reordered.triangles=new[] { 3,4,5,0,1,2 };
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true,allSides:true);
            var result=screen.Measure(reordered);
            Assert.That(result.colorBoundaryMean,Is.Zero); Assert.That(result.colorLoss,Is.Zero);
            Assert.That(result.rgbaRms,Is.LessThan(1e-6f)); Assert.That(result.silhouetteMax,Is.Zero);
        }

        [Test]
        public void FittedUvCornersStayInsideTheirOriginalAtlasIslands()
        {
            var source=Panel(true,seam:true); var target=Track(Object.Instantiate(source)); var before=target.uv2;
            var values=before.Select((uv,i) => uv+(i<3 ? Vector2.right*3 : Vector2.left*3)).ToArray();
            LodSharedUvTransfer.ProjectOriginalCharts(source,target,values);
            Assert.That(values.Take(3).All(uv => uv.x>=0 && uv.x<=1 && uv.y>=0 && uv.y<=1),Is.True);
            Assert.That(values.Skip(3).All(uv => uv.x>=2 && uv.x<=3 && uv.y>=0 && uv.y<=1),Is.True);
            CollectionAssert.AreEqual(before,target.uv2); CollectionAssert.AreEqual(before,source.uv2);
        }

        [Test]
        public void ExactVisibleUv2DoesNotGetNumericalRefittingDrift()
        {
            var source=Panel(true); var target=Track(Object.Instantiate(source)); var before=target.uv2;
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true,allSides:true);
            LodAttributeCorrection.FitVisibleUv2(source,target,screen);
            CollectionAssert.AreEqual(before,target.uv2);
        }

        [Test]
        public void AuthoredUvChartSeamRefusesTheDiagonal()
        {
            var source=Panel(true); var reduced=Panel(false,seam:true);
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(Track(LodPaintRetriangulation.Correct(source,reduced,screen,Settings,out var report)),Is.Null);
            Assert.That(report.pairs,Is.Zero);
        }

        [Test]
        public void ExactSourceRetainsItsTriangleLayout()
        {
            var source=Panel(true); var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(Track(LodPaintRetriangulation.Correct(source,source,screen,Settings,out var report)),Is.Null);
            Assert.That(report.accepted,Is.False);
        }

        [Test]
        public void MissingUv2OrNormalsCannotPassAnUnevaluatedSharedAtlasGate()
        {
            var source=Panel(true); var reduced=Panel(false);
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            reduced.uv2=Array.Empty<Vector2>();
            Assert.That(Track(LodPaintRetriangulation.Correct(source,reduced,screen,Settings,out _)),Is.Null);
            reduced.uv2=source.uv2; reduced.normals=Array.Empty<Vector3>();
            Assert.That(Track(LodPaintRetriangulation.Correct(source,reduced,screen,Settings,out _)),Is.Null);
        }

        [Test]
        public void DeformerAndMaterialsBypassGeometryEdits()
        {
            var source=Panel(true); var reduced=Panel(false);
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            reduced.AddBlendShapeFrame("deform",100,new Vector3[6],new Vector3[6],new Vector3[6]);
            Assert.That(Track(LodPaintRetriangulation.Correct(source,reduced,screen,Settings,out _)),Is.Null);
            reduced.ClearBlendShapes(); reduced.subMeshCount=2;
            Assert.That(Track(LodPaintRetriangulation.Correct(source,reduced,screen,Settings,out _)),Is.Null);
        }

        [Test]
        public void CancelAfterTrialAllocationReleasesOwnedMeshes()
        {
            var source=Panel(true); var reduced=Panel(false);
            var screen=new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            int count=Resources.FindObjectsOfTypeAll<Mesh>().Length,checks=0;
            Assert.Throws<OperationCanceledException>(() => LodPaintRetriangulation.Correct(source,reduced,screen,Settings,out _,() => {
                return ++checks%128==0 && Resources.FindObjectsOfTypeAll<Mesh>().Length>count;
            }));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length,Is.EqualTo(count));
            CollectionAssert.AreEqual(Enumerable.Range(0,6).ToArray(),reduced.triangles);
        }
    }
}
