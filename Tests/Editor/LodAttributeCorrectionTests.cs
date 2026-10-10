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
    }
}
