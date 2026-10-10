using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodSmoothingRegionTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        Mesh Track(Mesh mesh) { if (mesh) meshes.Add(mesh); return mesh; }
        [TearDown] public void Cleanup() { foreach (var mesh in meshes) if (mesh) Object.DestroyImmediate(mesh); meshes.Clear(); }
        static MeshSimplifier.SimplifySettings Settings => new MeshSimplifier.SimplifySettings { normalWeight = 1,uvChannel = 1 };
        Mesh SplitSquare(bool hard = false)
        {
            var mesh = Track(new Mesh { vertices = new[] {Vector3.zero,Vector3.right,new Vector3(1,1,0),
                Vector3.zero,new Vector3(1,1,0),Vector3.up} });
            mesh.triangles = new[] {0,1,2,3,4,5};
            mesh.normals = Enumerable.Repeat(Vector3.forward,3).Concat(Enumerable.Repeat(hard ? Vector3.right : Vector3.forward,3)).ToArray();
            mesh.uv2 = new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.one,Vector2.zero,Vector2.up};
            return mesh;
        }
        Mesh Reference()
        {
            const int size = 8;
            var p = new List<Vector3>(); var n = new List<Vector3>(); var f = new List<int>();
            for (int y = 0; y <= size; y++) for (int x = 0; x <= size; x++)
            {
                float u = (float)x/size,v = (float)y/size;
                p.Add(new Vector3(u,v,0)); n.Add(Quaternion.Euler(0,30*Mathf.Sin(u*Mathf.PI)*Mathf.Sin(v*Mathf.PI)*(1+u*.3f),0)*Vector3.forward);
            }
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            { int i = y*(size+1)+x; f.AddRange(new[] {i,i+1,i+size+2,i,i+size+2,i+size+1}); }
            var mesh = Track(new Mesh { vertices = p.ToArray(),normals = n.ToArray() }); mesh.triangles = f.ToArray(); return mesh;
        }

        [Test]
        public void SmoothUvDuplicates_UseSharedFitAndRetainGeometryAndUvs()
        {
            var source = Reference(); var reduced = SplitSquare();
            // Simulate a transfer-created normal mismatch across an authored smooth UV seam.
            reduced.normals = reduced.normals.Select((n,i) => i >= 3 ? Quaternion.Euler(0,1,0)*n : n).ToArray();
            var oldNormals = reduced.normals; Assert.That(oldNormals[0],Is.Not.EqualTo(oldNormals[3]));
            var corrected = Track(LodAttributeCorrection.Correct(source,reduced,Settings,default,out var report,out _));
            Assert.That(report.normalsAccepted,Is.True); Assert.That(corrected,Is.Not.Null);
            Assert.That(report.smoothingRegions,Is.EqualTo(1)); Assert.That(report.linkedNormalDuplicates,Is.EqualTo(2));
            Assert.That(corrected.normals[0],Is.EqualTo(corrected.normals[3])); Assert.That(corrected.normals[2],Is.EqualTo(corrected.normals[4]));
            CollectionAssert.AreEqual(reduced.vertices,corrected.vertices); CollectionAssert.AreEqual(reduced.triangles,corrected.triangles);
            CollectionAssert.AreEqual(reduced.uv2,corrected.uv2); CollectionAssert.AreEqual(oldNormals,reduced.normals);
            Assert.That(LodSmoothingRegions.NoRegression(report.regionNormalsAfter,report.regionNormalsBefore),Is.True);
        }

        [Test]
        public void HardNormalBoundary_RemainsSeparateAndPinned()
        {
            var source = SplitSquare(true); var reduced = Track(Object.Instantiate(source));
            var regions = new LodSmoothingRegions(new LodMeshData(source),new LodMeshData(reduced),null);
            Assert.That(regions.count,Is.EqualTo(2)); Assert.That(regions.linkedDuplicates,Is.Zero);
            Assert.That(regions.owners[0],Is.Not.EqualTo(regions.owners[3]));
            Assert.That(LodAttributeCorrection.Correct(source,reduced,Settings,default,out _,out _),Is.Null);
            Assert.That(reduced.normals[0],Is.EqualTo(Vector3.forward)); Assert.That(reduced.normals[3],Is.EqualTo(Vector3.right));
        }

        [Test]
        public void CornerContactAndAmbiguousOverlappingEdges_DoNotJoinRegions()
        {
            var mesh = SplitSquare();
            mesh.vertices = new[] {Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.left,Vector3.down};
            Assert.That(LodSmoothingRegions.Regions(new LodMeshData(mesh),mesh.triangles).Distinct().Count(),Is.EqualTo(2));
            mesh = SplitSquare(); mesh.triangles = new[] {0,1,2,3,4,5,0,1,2,3,4,5};
            Assert.That(LodSmoothingRegions.Regions(new LodMeshData(mesh),mesh.triangles).Distinct().Count(),Is.EqualTo(4));
            mesh = SplitSquare(); mesh.vertices = mesh.vertices.Take(3).Concat(mesh.vertices.Take(3)).ToArray();
            mesh.triangles = new[] {0,1,2,5,4,3};
            Assert.That(LodSmoothingRegions.Regions(new LodMeshData(mesh),mesh.triangles).Distinct().Count(),Is.EqualTo(2));
        }

        [Test]
        public void MaterialSlots_NeverShareSmoothingVariables()
        {
            var mesh = SplitSquare(); mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] {0,1,2},0); mesh.SetTriangles(new[] {3,4,5},1);
            var regions = new LodSmoothingRegions(new LodMeshData(mesh),new LodMeshData(mesh),null);
            Assert.That(regions.count,Is.EqualTo(2)); Assert.That(regions.linkedDuplicates,Is.Zero);
        }

        [Test]
        public void TriangleCrossingSourceHardBoundary_IsPinnedAndReported()
        {
            var source = SplitSquare(true);
            var target = Track(new Mesh { vertices = new[] {new Vector3(0,.2f,0),new Vector3(1,.2f,0),new Vector3(.2f,1,0)},
                normals = Enumerable.Repeat(Vector3.forward,3).ToArray(),triangles = new[] {0,1,2} });
            var regions = new LodSmoothingRegions(new LodMeshData(source),new LodMeshData(target),null);
            Assert.That(regions.mixedFaces,Is.EqualTo(1)); Assert.That(regions.pinned,Is.All.True);
            Assert.That(regions.missingRegions,Is.EqualTo(2));
        }

        [Test]
        public void SmallRegionRegression_CannotHideBehindLargeRegionImprovement()
        {
            var before = new[] {new LodSmoothingRegions.Error { region = 0,rms = 30,maximum = 60,samples = 1000 },
                new LodSmoothingRegions.Error { region = 1,rms = 1,maximum = 2,samples = 10 }};
            var after = (LodSmoothingRegions.Error[])before.Clone(); after[0].rms = 10; after[1].rms = 2;
            Assert.That(LodSmoothingRegions.NoRegression(after,before),Is.False);
            after[1] = before[1]; after[1].maximum = 3;
            Assert.That(LodSmoothingRegions.NoRegression(after,before),Is.False);
            after[1] = before[1]; Assert.That(LodSmoothingRegions.NoRegression(after,before),Is.True);
        }

        [Test]
        public void SmoothingAnalysisCancellation_LeavesMeshesUntouched()
        {
            var source = Reference(); var target = SplitSquare(); var normals = target.normals;
            Assert.Throws<OperationCanceledException>(() => LodAttributeCorrection.Correct(source,target,Settings,default,out _,out _,() => true));
            CollectionAssert.AreEqual(normals,target.normals);
        }
    }
}
