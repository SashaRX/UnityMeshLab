using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodAttributeCorrectionTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        Mesh Track(Mesh mesh) { if (mesh) meshes.Add(mesh); return mesh; }
        [TearDown] public void Cleanup() { foreach (var mesh in meshes) if (mesh) Object.DestroyImmediate(mesh); meshes.Clear(); }
        static MeshSimplifier.SimplifySettings Settings => new MeshSimplifier.SimplifySettings { uvChannel = 1,normalWeight = 1,colorWeight = 1 };
        Mesh Grid(int size,bool nonlinear)
        {
            var p = new List<Vector3>(); var n = new List<Vector3>(); var c = new List<Color>(); var uv = new List<Vector2>(); var faces = new List<int>();
            for (int y = 0; y <= size; y++) for (int x = 0; x <= size; x++)
            {
                float u = (float)x/size,v = (float)y/size;
                float bump = nonlinear ? Mathf.Sin(u*Mathf.PI)*Mathf.Sin(v*Mathf.PI) : 0;
                p.Add(new Vector3(u,v,0)); n.Add(Quaternion.Euler(0,30*bump,0)*Vector3.forward);
                c.Add(new Color(.2f+.6f*bump,2+bump,-1+.5f*bump,.2f+.8f*bump)); uv.Add(new Vector2(u,v));
            }
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            { int i = y*(size+1)+x; faces.AddRange(new[] {i,i+1,i+size+2,i,i+size+2,i+size+1}); }
            var mesh = Track(new Mesh { name = "AttributeGrid",vertices = p.ToArray(),normals = n.ToArray(),colors = c.ToArray(),uv2 = uv.ToArray() });
            mesh.SetTriangles(faces,0); mesh.tangents = p.Select(_ => new Vector4(1,0,0,-1)).ToArray(); return mesh;
        }

        [Test]
        public void NonlinearInteriorFit_ImprovesNormalsAndAllRgbaWithoutChangingGeometryOrInputs()
        {
            var source = Grid(8,true); var reduced = Grid(1,false);
            var originalColors = source.colors; var originalNormals = source.normals; var reducedColors = reduced.colors;
            var corrected = Track(LodAttributeCorrection.Correct(source,reduced,Settings,new LodPipelineOps.Options { maxColorError = .1f },out var report,out var measured));
            Assert.That(corrected,Is.Not.Null); Assert.That(report.normalsAccepted,Is.True); Assert.That(report.colorsAccepted,Is.True);
            Assert.That(report.normalRmsAfter,Is.LessThan(report.normalRmsBefore));
            Assert.That(report.normalMaxAfter,Is.LessThanOrEqualTo(report.normalMaxBefore+1e-4f));
            for (int ch = 0; ch < 4; ch++)
            { Assert.That(report.colorRmsAfter[ch],Is.LessThan(report.colorRmsBefore[ch])); Assert.That(report.colorMaxAfter[ch],Is.LessThanOrEqualTo(report.colorMaxBefore[ch]+1e-6f)); }
            Assert.That(measured.DistanceRms,Is.LessThan(1e-6));
            CollectionAssert.AreEqual(reduced.vertices,corrected.vertices); CollectionAssert.AreEqual(reduced.GetIndices(0),corrected.GetIndices(0));
            CollectionAssert.AreEqual(reduced.uv2,corrected.uv2); CollectionAssert.AreEqual(originalColors,source.colors);
            CollectionAssert.AreEqual(originalNormals,source.normals); CollectionAssert.AreEqual(reducedColors,reduced.colors);
            Assert.That(corrected.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(VertexAttributeFormat.Float32));
            Assert.That(corrected.colors.Min(c => c.g),Is.GreaterThan(1)); Assert.That(corrected.colors.Max(c => c.b),Is.LessThan(0));
            for (int i = 0; i < corrected.vertexCount; i++)
            { Assert.That(Vector3.Dot(corrected.normals[i],(Vector3)corrected.tangents[i]),Is.EqualTo(0).Within(1e-5)); Assert.That(corrected.tangents[i].w,Is.EqualTo(-1)); }
        }

        [Test]
        public void FarColorCorrectionImprovesAllVisibleRgbaChannelsIncludingAlpha()
        {
            var source = Grid(8,true); var reduced = Grid(1,false);
            var positions = reduced.vertices; var colors = reduced.colors;
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var before = screen.Measure(reduced);
            var fitted = Track(LodAttributeCorrection.Correct(source,reduced,Settings,
                new LodPipelineOps.Options { screenBudget = true,maxColorError = .1f },out var report,out _,screen:screen));
            Assert.That(fitted,Is.Not.Null); Assert.That(report.colorsAccepted,Is.True);
            var after = screen.Measure(fitted);
            Assert.That(after.rgbaRms,Is.LessThan(before.rgbaRms)); Assert.That(after.DoesNotWorsen(before,true),Is.True);
            for (int ch = 0; ch < 4; ch++) Assert.That(after.rgbaChannelRms[ch],Is.LessThanOrEqualTo(before.rgbaChannelRms[ch]+1e-6f));
            Assert.That(report.note,Does.Contain("paired visible samples"));
            CollectionAssert.AreEqual(positions,fitted.vertices); CollectionAssert.AreEqual(colors,reduced.colors);
            Assert.That(fitted.colors.Max(c => c.g),Is.GreaterThan(1)); Assert.That(fitted.colors.Min(c => c.b),Is.LessThan(0));
        }

        Mesh PolygonPanels(bool contaminated,bool packed)
        {
            var mesh = Track(new Mesh { vertices = new[] { new Vector3(-1,-1,0),new Vector3(0,-1,0),new Vector3(0,1,0),new Vector3(-1,1,0),
                new Vector3(0,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(0,1,0) }, triangles = new[] {0,1,2,0,2,3,4,5,6,4,6,7} });
            var colors = Enumerable.Range(0,8).Select(i => i < 4 ? new Color(1,0,0,.8f) : new Color(0,0,1,.4f)).ToArray();
            if (contaminated) for (int i = 0; i < 8; i++) colors[i] = Color.Lerp(colors[i],Color.gray,i%2 == 0 ? .25f : .1f);
            if (packed) mesh.SetColors(colors.Select(c => (Color32)c).ToArray()); else mesh.colors = colors;
            mesh.normals = Enumerable.Repeat(Vector3.forward,8).ToArray(); mesh.tangents = Enumerable.Repeat(new Vector4(1,0,0,1),8).ToArray();
            mesh.SetUVs(1,mesh.vertices.Select(p => new Vector4(p.x,p.y,2,3)).ToList()); return mesh;
        }

        [TestCase(false)] [TestCase(true)]
        public void PolygonPaintRestoresFlatRgbaAndRetainsRenderBuffers(bool packed)
        {
            var source = PolygonPanels(false,packed); var reduced = PolygonPanels(true,packed); var colors = reduced.colors;
            var sourceColors = source.colors; var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var before = screen.Measure(reduced);
            var painted = Track(LodAttributeCorrection.PaintFaceBoundaries(source,reduced,screen,out var report));
            Assert.That(painted,Is.Not.Null); Assert.That(report.accepted,Is.True);
            Assert.That(screen.Measure(painted).DoesNotWorsen(before,true),Is.True);
            var allSides = screen.WithAllSides(source);
            Assert.That(allSides.Measure(painted).ColorViewsDoNotWorsen(allSides.Measure(reduced)),Is.True);
            Assert.That(screen.Measure(painted).rgbaRms,Is.LessThan(before.rgbaRms));
            Assert.That(painted.vertexCount,Is.EqualTo(reduced.vertexCount)); CollectionAssert.AreEqual(reduced.triangles,painted.triangles);
            CollectionAssert.AreEqual(reduced.vertices,painted.vertices); CollectionAssert.AreEqual(reduced.normals,painted.normals);
            CollectionAssert.AreEqual(reduced.tangents,painted.tangents);
            var a = new List<Vector4>(); var b = new List<Vector4>(); reduced.GetUVs(1,a); painted.GetUVs(1,b); CollectionAssert.AreEqual(a,b);
            Assert.That(painted.GetVertexAttributes(),Is.EqualTo(reduced.GetVertexAttributes()));
            foreach (int f in new[] {0,3,6,9})
            {
                var indices = painted.triangles; Assert.That(painted.colors[indices[f]],Is.EqualTo(painted.colors[indices[f+1]]));
                Assert.That(painted.colors[indices[f]],Is.EqualTo(painted.colors[indices[f+2]]));
            }
            CollectionAssert.AreEqual(colors,reduced.colors); CollectionAssert.AreEqual(sourceColors,source.colors);
        }

        [Test]
        public void PolygonPaintDoesNotFlattenAnAlreadyMatchingFieldAndCancelsWithoutMutatingInputs()
        {
            var source = PolygonPanels(false,false); var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(LodAttributeCorrection.PaintFaceBoundaries(source,source,screen,out _),Is.Null);
            var reduced = PolygonPanels(true,false); var colors = reduced.colors;
            Assert.Throws<OperationCanceledException>(() => LodAttributeCorrection.PaintFaceBoundaries(source,reduced,screen,out _,() => true));
            CollectionAssert.AreEqual(colors,reduced.colors);
        }

        [Test]
        public void PolygonPaintViewGateRejectsARegressionHiddenByLowerWorstRgbaRms()
        {
            var before = new LodScreenValidation.Report { rgbaRms=.3f,rgbaChannelRms=Vector4.one*.3f,
                rgbaViewRms=new[] {.01f,.3f},rgbaChannelViewRms=new[] {Vector4.one*.01f,Vector4.one*.3f} };
            var after = new LodScreenValidation.Report { rgbaRms=.2f,rgbaChannelRms=Vector4.one*.2f,
                rgbaViewRms=new[] {.1f,.2f},rgbaChannelViewRms=new[] {Vector4.one*.1f,Vector4.one*.2f} };
            Assert.That(after.ColorsDoNotWorsen(before),Is.True);
            Assert.That(after.ColorViewsDoNotWorsen(before),Is.False);
            Assert.That(new LodScreenValidation.Report().ColorViewsDoNotWorsen(before),Is.False);
        }

        [Test]
        public void PolygonPaintViewAllowanceIsBoundedInEveryRgbaChannel()
        {
            var before = new LodScreenValidation.Report { rgbaViewRms=new[] {.01f},rgbaChannelViewRms=new[] {Vector4.one*.01f} };
            var after = new LodScreenValidation.Report { rgbaViewRms=new[] {.011f},rgbaChannelViewRms=new[] {Vector4.one*.011f} };
            Assert.That(after.ColorViewsWithin(before,1f/255),Is.True);
            after.rgbaChannelViewRms[0].w = .014f;
            Assert.That(after.ColorViewsWithin(before,1f/255),Is.False);
        }

        [Test]
        public void PolygonPaintSkipsDenseMeshesAndDeformers()
        {
            var large = Grid(9,true); var largeScreen = new LodScreenValidation(large,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(LodAttributeCorrection.PaintFaceBoundaries(large,large,largeScreen,out var skipped),Is.Null);
            Assert.That(skipped.trials,Is.Zero);
            var source = PolygonPanels(false,false); var reduced = PolygonPanels(true,false);
            reduced.AddBlendShapeFrame("Move",100,Enumerable.Repeat(Vector3.right,reduced.vertexCount).ToArray(),null,null);
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.That(LodAttributeCorrection.PaintFaceBoundaries(source,reduced,screen,out skipped),Is.Null);
            Assert.That(skipped.trials,Is.Zero);
            var materialSplit = PolygonPanels(true,false); materialSplit.subMeshCount = 2;
            materialSplit.SetTriangles(new[] {0,1,2,0,2,3},0); materialSplit.SetTriangles(new[] {4,5,6,4,6,7},1);
            Assert.That(LodAttributeCorrection.PaintFaceBoundaries(source,materialSplit,screen,out skipped),Is.Null);
            Assert.That(skipped.trials,Is.Zero);
        }

        [Test]
        public void PolygonPaintCancellationAfterCloningReleasesItsOwnedMesh()
        {
            var source = PolygonPanels(false,false); var reduced = PolygonPanels(true,false); var colors = reduced.colors;
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length,checks = 0;
            Assert.Throws<OperationCanceledException>(() => LodAttributeCorrection.PaintFaceBoundaries(source,reduced,screen,out _,
                () => ++checks%128 == 0 && Resources.FindObjectsOfTypeAll<Mesh>().Length > meshCount));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length,Is.EqualTo(meshCount));
            CollectionAssert.AreEqual(colors,reduced.colors);
        }
        [Test]
        public void FarVisibleNormalsImproveEveryViewAndLightingWithoutChangingGeometryOrRgba()
        {
            var source = Grid(8,false); var reduced = Grid(1,false);
            reduced.normals = Enumerable.Repeat(Vector3.right,reduced.vertexCount).ToArray();
            var oldNormals = reduced.normals; var sourceNormals = source.normals;
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var before = screen.Measure(reduced);
            var fitted = Track(LodAttributeCorrection.CorrectVisibleNormals(source,reduced,Settings,screen,out var report,out _));
            Assert.That(fitted,Is.Not.Null); Assert.That(report.accepted,Is.True); Assert.That(report.samples,Is.GreaterThan(0));
            var after = screen.Measure(fitted);
            Assert.That(after.normalRms,Is.LessThan(before.normalRms)); Assert.That(after.headlightRms,Is.LessThan(before.headlightRms));
            for (int v = 0; v < before.views; v++)
            {
                Assert.That(after.normalViewRms[v],Is.LessThanOrEqualTo(before.normalViewRms[v]+1e-4f));
                Assert.That(after.headlightViewRms[v],Is.LessThanOrEqualTo(before.headlightViewRms[v]+1e-6f));
            }
            CollectionAssert.AreEqual(reduced.vertices,fitted.vertices); CollectionAssert.AreEqual(reduced.triangles,fitted.triangles);
            CollectionAssert.AreEqual(reduced.colors,fitted.colors); CollectionAssert.AreEqual(reduced.uv2,fitted.uv2);
            CollectionAssert.AreEqual(oldNormals,reduced.normals); CollectionAssert.AreEqual(sourceNormals,source.normals);
            foreach (var n in fitted.normals) Assert.That(n.magnitude,Is.EqualTo(1).Within(1e-5));
            for (int i = 0; i < fitted.vertexCount; i++)
            { Assert.That(Vector3.Dot(fitted.normals[i],(Vector3)fitted.tangents[i]),Is.EqualTo(0).Within(1e-5)); Assert.That(fitted.tangents[i].w,Is.EqualTo(-1)); }
        }
        [Test]
        public void FarVisibleNormalsCancelDuringOwnedVerificationWithoutLeaks()
        {
            var source = Grid(4,false); var reduced = Grid(1,false);
            reduced.normals = Enumerable.Repeat(Vector3.right,reduced.vertexCount).ToArray(); reduced.name = "FarNormalsCancel_"+Guid.NewGuid().ToString("N");
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.Throws<OperationCanceledException>(() => LodAttributeCorrection.CorrectVisibleNormals(source,reduced,Settings,screen,out _,out _,
                () => Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name == reduced.name) > 1));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Where(m => m.name == reduced.name).ToArray(),Is.EqualTo(new[] { reduced }));
        }
        [Test]
        public void SmoothColorUvSeam_UsesSharedFitWithoutChangingUvOrInventingAColorStep()
        {
            var source = Grid(8,true);
            var reduced = Grid(1,false);
            var indices = reduced.triangles;
            reduced.vertices = indices.Select(i => reduced.vertices[i]).ToArray();
            reduced.normals = null;
            source.normals = null;
            reduced.colors = indices.Select(_ => new Color(.2f,2,-1,.2f)).ToArray();
            var uv = Enumerable.Range(0,indices.Length).Select(i => new Vector2(i,0)).ToArray();
            reduced.uv2 = uv;
            reduced.tangents = null;
            reduced.triangles = Enumerable.Range(0,indices.Length).ToArray();
            var owners = LodAttributeCorrection.ColorOwners(new LodMeshData(reduced));
            Assert.That(owners[0],Is.EqualTo(owners[3]));
            Assert.That(owners[2],Is.EqualTo(owners[4]));

            var corrected = Track(LodAttributeCorrection.Correct(source,reduced,Settings,default,out var report,out _));

            Assert.That(report.colorsAccepted,Is.True);
            Assert.That(corrected,Is.Not.Null);
            Assert.That(corrected.colors[0],Is.EqualTo(corrected.colors[3]));
            Assert.That(corrected.colors[2],Is.EqualTo(corrected.colors[4]));
            Assert.That(corrected.uv2,Is.EqualTo(uv));
            Assert.That(corrected.triangles,Is.EqualTo(reduced.triangles));
        }

        [Test]
        public void ColorOwners_DoNotJoinDisconnectedCornerContactsOrMaterialSlots()
        {
            var mesh = Track(new Mesh { vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,
                Vector3.zero,Vector3.left,Vector3.down }, triangles = new[] {0,1,2,3,4,5} });
            mesh.colors = Enumerable.Repeat(Color.gray,6).ToArray();
            var owners = LodAttributeCorrection.ColorOwners(new LodMeshData(mesh));
            Assert.That(owners[0],Is.Not.EqualTo(owners[3]));
            mesh.vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.up,Vector3.left };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] {0,1,2},0); mesh.SetTriangles(new[] {3,4,5},1);
            owners = LodAttributeCorrection.ColorOwners(new LodMeshData(mesh));
            Assert.That(owners[0],Is.Not.EqualTo(owners[3]));
            Assert.That(owners[2],Is.Not.EqualTo(owners[4]));
        }

        [Test]
        public void ConstantExactFields_ReturnNoReplacementAndDoNotInventChannels()
        {
            var source = Grid(4,false); var reduced = Grid(1,false);
            var corrected = Track(LodAttributeCorrection.Correct(source,reduced,Settings,default,out var report,out _));
            Assert.That(corrected,Is.Null); Assert.That(report.colorsAccepted || report.normalsAccepted,Is.False);
            source.colors = null; reduced.colors = null; source.normals = null; reduced.normals = null;
            Assert.That(LodAttributeCorrection.Correct(source,reduced,Settings,default,out report,out _),Is.Null);
            Assert.That(reduced.HasVertexAttribute(VertexAttribute.Color),Is.False);
        }

        [Test]
        public void Color32_IsVerifiedAfterQuantizationIncludingAlpha()
        {
            var source = Grid(3,false); var reduced = Grid(1,false);
            source.SetColors(Enumerable.Repeat(new Color32(160,100,90,30),source.vertexCount).ToArray());
            reduced.SetColors(Enumerable.Repeat(new Color32(80,60,50,200),reduced.vertexCount).ToArray());
            var corrected = Track(LodAttributeCorrection.Correct(source,reduced,Settings,default,out var report,out _));
            Assert.That(report.colorsAccepted,Is.True); Assert.That(corrected,Is.Not.Null);
            Assert.That(corrected.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(VertexAttributeFormat.UNorm8));
            Assert.That(report.colorRmsAfter.w,Is.LessThan(report.colorRmsBefore.w));
            var measured = LodSurfaceValidation.MeasureMeshes(source,corrected,Settings,ignoreDegenerateFaces:true);
            Assert.That(measured.ColorRms.w,Is.EqualTo(report.colorRmsAfter.w).Within(1e-6));
        }

        [Test]
        public void AuthoredNormalAndAlphaMaximum_CannotHideBehindGeometricOrRgbImprovement()
        {
            var before = new LodSurfaceValidation.Metrics { normalAngle = 120,authoredNormalAngle = 20,surfaceArea = 1,normalSquaredIntegral = 100,
                colorArea = 1,colorMax = Vector4.one*.5f,colorSquaredIntegral = Vector4.one*.1f };
            var after = before; after.normalSquaredIntegral = 25; after.authoredNormalAngle = 21;
            Assert.That(LodAttributeCorrection.NormalImproved(after,before),Is.False);
            after = before; after.colorSquaredIntegral = Vector4.one*.01f; after.colorMax.w = .6f;
            Assert.That(LodAttributeCorrection.ColorImproved(after,before),Is.False);
            after = before; after.colorSquaredIntegral.w *= 2; after.colorSquaredIntegral.x *= .1f;
            Assert.That(LodAttributeCorrection.ColorImproved(after,before),Is.False);
        }

        Mesh Layers(int size,bool reference)
        {
            var lower = Grid(size,false); var positions = lower.vertices; var faces = lower.triangles;
            var all = positions.Concat(positions.Select(p => p+Vector3.forward*.0001f)).ToArray();
            var mesh = Track(new Mesh { name = "ThinLayers",vertices = all });
            mesh.triangles = faces.Concat(faces.Reverse().Select(i => i+positions.Length)).ToArray();
            mesh.normals = positions.Select(_ => Vector3.forward).Concat(positions.Select(_ => Vector3.back)).ToArray();
            mesh.colors = positions.Select(_ => reference ? Color.black : Color.gray).Concat(positions.Select(_ => reference ? Color.white : Color.gray)).ToArray();
            return mesh;
        }

        [Test]
        public void ThinOppositeSurfaces_UseGeometryAndWindingForCorrespondence()
        {
            var source = Layers(4,true); var reduced = Layers(1,false);
            var corrected = Track(LodAttributeCorrection.Correct(source,reduced,Settings,default,out var report,out _));
            Assert.That(report.colorsAccepted,Is.True); Assert.That(corrected.colors.Take(4).Max(c => c.r),Is.LessThan(.1f));
            Assert.That(corrected.colors.Skip(4).Min(c => c.r),Is.GreaterThan(.9f));
        }

        [Test]
        public void DuplicatedHardColorAndNormalSeams_ArePinned()
        {
            var source = Layers(3,true); var reduced = Layers(1,false);
            source.vertices = source.vertices.Select(p => new Vector3(p.x,p.y,0)).ToArray();
            reduced.vertices = reduced.vertices.Select(p => new Vector3(p.x,p.y,0)).ToArray();
            reduced.colors = Enumerable.Repeat(new Color(.2f,0,0,1),4).Concat(Enumerable.Repeat(new Color(.8f,1,1,1),4)).ToArray();
            reduced.normals = Enumerable.Repeat(Quaternion.Euler(0,10,0)*Vector3.forward,4)
                .Concat(Enumerable.Repeat(Quaternion.Euler(0,10,0)*Vector3.back,4)).ToArray();
            var corrected = Track(LodAttributeCorrection.Correct(source,reduced,Settings,default,out var report,out _));
            Assert.That(corrected,Is.Null); Assert.That(report.normalsAccepted || report.colorsAccepted,Is.False);
        }

        [Test]
        public void CancellationDuringVerification_DestroysTemporaryClone()
        {
            var source = Grid(4,true); var reduced = Grid(1,false); reduced.name = "CorrectionCancel_"+Guid.NewGuid().ToString("N");
            Assert.Throws<OperationCanceledException>(() => LodAttributeCorrection.Correct(source,reduced,Settings,default,out _,out _,
                () => Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name == reduced.name) > 1));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Where(m => m.name == reduced.name).ToArray(),Is.EqualTo(new[] { reduced }));
        }

        Mesh PaintPanels(bool reference,bool quantized)
        {
            var p = new[] { new Vector3(-1,-1,0),new Vector3(0,-1,0),new Vector3(0,1,0),new Vector3(-1,1,0),
                new Vector3(0,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(0,1,0) };
            var values = Enumerable.Range(0,8).Select(i => reference ? i < 4 ? new Color(2,-1,.3f,.2f) : new Color(3,-.4f,.6f,.8f) : new Color(2.5f,-.7f,.45f,.5f)).ToArray();
            var mesh = Track(new Mesh { vertices=p,triangles=new[] {0,1,2,0,2,3,4,5,6,4,6,7} });
            if (quantized) mesh.SetColors(values.Select(c => (Color32)c).ToArray()); else mesh.colors = values;
            mesh.RecalculateNormals(); mesh.tangents = p.Select(_ => new Vector4(1,0,0,-1)).ToArray();
            mesh.SetUVs(0,p.Select(v => new Vector4(v.x,v.y,2,3)).ToList()); mesh.uv2 = p.Select(v => new Vector2(v.x,v.y)).ToArray();
            mesh.RecalculateBounds(); return mesh;
        }

        [TestCase(false)] [TestCase(true)]
        public void FarBoundaryCornersRestoreRgbAndAlphaWithoutAddingTrianglesOrChangingOtherChannels(bool quantized)
        {
            var source = PaintPanels(true,quantized); var reduced = PaintPanels(false,quantized);
            var sourceColors = source.colors; var reducedColors = reduced.colors; var indices = reduced.triangles;
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            var fitted = Track(LodAttributeCorrection.CorrectVisibleColorBoundaries(source,reduced,screen,out var report));
            Assert.That(fitted,Is.Not.Null); Assert.That(report.accepted,Is.True);
            Assert.That(report.boundaryAfter,Is.LessThan(report.boundaryBefore)); Assert.That(report.rgbaAfter,Is.LessThan(report.rgbaBefore));
            Assert.That(LodMeshData.TriangleCount(fitted),Is.EqualTo(LodMeshData.TriangleCount(reduced)));
            Assert.That(screen.Measure(fitted).ColorsDoNotWorsen(screen.Measure(reduced)),Is.True);
            Assert.That(fitted.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(reduced.GetVertexAttributeFormat(VertexAttribute.Color)));
            var oldData = new LodMeshData(reduced); var newData = new LodMeshData(fitted);
            var fittedIndices = fitted.triangles;
            for (int i = 0; i < indices.Length; i++)
            {
                int vertex = fittedIndices[i];
                Assert.That(newData.positions[vertex],Is.EqualTo(oldData.positions[indices[i]]));
                Assert.That(newData.normals[vertex],Is.EqualTo(oldData.normals[indices[i]]));
                Assert.That(newData.tangents[vertex],Is.EqualTo(oldData.tangents[indices[i]]));
                for (int ch = 0; ch < 8; ch++) if (oldData.uvs[ch] != null)
                { Assert.That(newData.uvDimensions[ch],Is.EqualTo(oldData.uvDimensions[ch])); Assert.That(newData.uvs[ch][vertex],Is.EqualTo(oldData.uvs[ch][indices[i]])); }
            }
            CollectionAssert.AreEqual(sourceColors,source.colors); CollectionAssert.AreEqual(reducedColors,reduced.colors);
        }

        [TestCase(false)] [TestCase(true)]
        public void ExactCornerSharingRetainsEveryTriangleFieldAndSharpRgbaSeam(bool quantized)
        {
            var source = PaintPanels(true,quantized);
            // Also exercise UV channels whose third/fourth components must not be lost.
            source.SetUVs(4,source.vertices.Select(p => new Vector3(p.x,p.y,7)).ToList());
            source.SetUVs(7,source.vertices.Select(p => new Vector4(p.x,p.y,8,9)).ToList());
            var expanded = Track(new LodMeshData(source).ExpandTriangleCorners());
            var compact = Track(new LodMeshData(expanded).ShareRenderCorners(0));
            Assert.That(compact,Is.Not.Null); Assert.That(compact.vertexCount,Is.EqualTo(8));
            Assert.That(expanded.vertexCount,Is.EqualTo(12));
            var a = new LodMeshData(expanded); var b = new LodMeshData(compact); var indices = compact.triangles;
            for (int i = 0; i < indices.Length; i++)
            {
                int v = indices[i]; Assert.That(b.positions[v],Is.EqualTo(a.positions[i]));
                Assert.That(b.colors[v],Is.EqualTo(a.colors[i])); Assert.That(b.normals[v],Is.EqualTo(a.normals[i]));
                Assert.That(b.tangents[v],Is.EqualTo(a.tangents[i]));
                for (int ch = 0; ch < 8; ch++) if (a.uvs[ch] != null)
                { Assert.That(b.uvDimensions[ch],Is.EqualTo(a.uvDimensions[ch])); Assert.That(b.uvs[ch][v],Is.EqualTo(a.uvs[ch][i])); }
            }
            Assert.That(compact.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(source.GetVertexAttributeFormat(VertexAttribute.Color)));
            Assert.That(new LodMeshData(compact).ShareRenderCorners(0),Is.Null);
        }

        [Test]
        public void SharingDoesNotCrossHardNormalsTangentsUvSeamsOrMaterialSlots()
        {
            var source = PaintPanels(true,false);
            var expanded = Track(new LodMeshData(source).ExpandTriangleCorners());
            // Corners 0/3 and 2/4 originally shared. Different fields must keep both splits.
            var normals = expanded.normals; normals[3] = Vector3.back; expanded.normals = normals;
            var tangents = expanded.tangents; tangents[4].w = 1; expanded.tangents = tangents;
            var uv = new List<Vector4>(); expanded.GetUVs(0,uv); var changedUv = uv[9]; changedUv.w += 1; uv[9] = changedUv; expanded.SetUVs(0,uv);
            expanded.subMeshCount = 2; expanded.SetTriangles(new[] {0,1,2,3,4,5},0); expanded.SetTriangles(new[] {6,7,8,9,10,11},1);
            var compact = Track(new LodMeshData(expanded).ShareRenderCorners(0));
            Assert.That(compact,Is.Not.Null); Assert.That(compact.vertexCount,Is.EqualTo(11));
            var first = compact.GetIndices(0); Assert.That(first[0],Is.Not.EqualTo(first[3])); Assert.That(first[2],Is.Not.EqualTo(first[4]));
            Assert.That(compact.GetIndices(0).Intersect(compact.GetIndices(1)),Is.Empty);
            var same = Track(new LodMeshData(source).ExpandTriangleCorners());
            same.subMeshCount = 2; same.SetTriangles(new[] {0,1,2},0); same.SetTriangles(new[] {3,4,5,6,7,8,9,10,11},1);
            compact = Track(new LodMeshData(same).ShareRenderCorners(0));
            Assert.That(compact.GetIndices(0).Intersect(compact.GetIndices(1)),Is.Empty);
        }

        [Test]
        public void ApproximateSharingBoundsRgbaDiameterAndRejectsUnverifiedColorLoss()
        {
            var source = PaintPanels(true,false); var expanded = Track(new LodMeshData(source).ExpandTriangleCorners());
            var values = expanded.colors; values[3].a += .02f; expanded.colors = values;
            var original = expanded.colors;
            var bounded = Track(new LodMeshData(expanded).ShareRenderCorners(.01f));
            Assert.That(bounded.vertexCount,Is.EqualTo(9));
            var screen = new LodScreenValidation(expanded,true,resolution:72,objectPixels:64,measureFields:true);
            var verified = Track(LodAttributeCorrection.ShareVisibleColorCorners(expanded,screen,out var report));
            Assert.That(verified,Is.Not.Null); Assert.That(report.colorTolerance,Is.EqualTo(0));
            Assert.That(verified.vertexCount,Is.EqualTo(9));
            Assert.That(screen.Measure(verified).ColorsDoNotWorsen(screen.Measure(expanded)),Is.True);
            CollectionAssert.AreEqual(original,expanded.colors);
        }

        [Test]
        public void SharingRetainsCornerContactsAndOverlappingShells()
        {
            var contact = Track(new Mesh { vertices=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.left,Vector3.down},
                triangles=new[] {0,1,2,3,4,5},colors=Enumerable.Repeat(Color.gray,6).ToArray(),normals=Enumerable.Repeat(Vector3.forward,6).ToArray() });
            Assert.That(new LodMeshData(contact).ShareRenderCorners(0),Is.Null);
            var overlap = Track(new Mesh { vertices=new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.up,Vector3.right},
                triangles=new[] {0,1,2,3,4,5},colors=Enumerable.Repeat(Color.gray,6).ToArray(),normals=Enumerable.Repeat(Vector3.forward,6).ToArray() });
            Assert.That(new LodMeshData(overlap).ShareRenderCorners(0),Is.Null);
        }

        [Test]
        public void SharingCancellationReleasesCandidatesAndDeformingMeshesBypassSharing()
        {
            var source = PaintPanels(true,false); var expanded = Track(new LodMeshData(source).ExpandTriangleCorners());
            expanded.name = "SharingCancel_"+Guid.NewGuid().ToString("N");
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            Assert.Throws<OperationCanceledException>(() => LodAttributeCorrection.ShareVisibleColorCorners(expanded,screen,out _,
                () => Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name == expanded.name) > 1));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Where(m => m.name == expanded.name).ToArray(),Is.EqualTo(new[] {expanded}));
            var delta = new Vector3[expanded.vertexCount]; expanded.AddBlendShapeFrame("Shape",100,delta,delta,delta);
            Assert.That(new LodMeshData(expanded).ShareRenderCorners(0),Is.Null);
            Assert.That(LodAttributeCorrection.ShareVisibleColorCorners(expanded,screen,out _),Is.Null);
        }

        [Test]
        public void FarBoundaryCancellationReleasesExpandedMeshAndRetainsInputs()
        {
            var source = PaintPanels(true,false); var reduced = PaintPanels(false,false);
            reduced.name = "BoundaryCancel_"+Guid.NewGuid().ToString("N"); var colors = reduced.colors;
            var screen = new LodScreenValidation(source,true,resolution:72,objectPixels:64,measureFields:true);
            UvProgress.Begin("Boundary cancellation",cancelable:true);
            try
            {
                Assert.Throws<OperationCanceledException>(() => LodAttributeCorrection.CorrectVisibleColorBoundaries(source,reduced,screen,out _,
                    () => UvProgress.Current.detail?.Contains("verify far color boundary corners") == true));
                Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Where(m => m.name == reduced.name).ToArray(),Is.EqualTo(new[] { reduced }));
                CollectionAssert.AreEqual(colors,reduced.colors);
            }
            finally { UvProgress.End(); }
        }
    }
}
