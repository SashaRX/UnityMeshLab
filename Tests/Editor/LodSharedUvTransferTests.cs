using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodSharedUvTransferTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        Mesh Track(Mesh mesh) { meshes.Add(mesh); return mesh; }
        [TearDown] public void Cleanup() { foreach (var mesh in meshes) if (mesh) UnityEngine.Object.DestroyImmediate(mesh); meshes.Clear(); }
        Mesh Source()
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[] { Vector3.zero,Vector3.right,Vector3.up,Vector3.zero,Vector3.right,Vector3.up };
            mesh.normals = Enumerable.Repeat(Vector3.forward,6).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1,0,0,-1),6).ToArray();
            mesh.colors32 = Enumerable.Range(0,6).Select(i => new Color32((byte)(i*30),(byte)(255-i*20),64,(byte)(100+i))).ToArray();
            mesh.SetUVs(0,Enumerable.Range(0,6).Select(i => new Vector3(i,.2f,.3f)).ToList());
            mesh.uv2 = new[] { new Vector2(.1f,.1f),new Vector2(.2f,.1f),new Vector2(.1f,.2f),
                new Vector2(.7f,.7f),new Vector2(.8f,.7f),new Vector2(.7f,.8f) };
            mesh.SetUVs(3,Enumerable.Range(0,6).Select(i => new Vector4(i,2,3,4)).ToList());
            mesh.subMeshCount = 2; mesh.SetTriangles(new[] {0,1,2},0); mesh.SetTriangles(new[] {3,4,5},1); mesh.RecalculateBounds();
            return mesh;
        }
        [Test] public void SharedAtlasTransferKeepsCornerGeometryAndAllOtherFieldsAndSeparatesMaterials()
        {
            var source = Source(); var reduced = Track(UnityEngine.Object.Instantiate(source));
            reduced.uv2 = Enumerable.Repeat(new Vector2(.5f,.5f),6).ToArray();
            var before = reduced.uv2; var original = source.uv2;
            var result = Track(LodSharedUvTransfer.Transfer(source,reduced,new MeshSimplifier.SimplifySettings { uvChannel = 1 },out var report));
            Assert.That(result,Is.Not.Null); Assert.That(report.accepted,Is.True); Assert.That(report.charts,Is.EqualTo(2));
            Assert.That(report.uvRmsAfter,Is.LessThan(1e-6)); Assert.That(LodMeshData.TriangleCount(result),Is.EqualTo(2));
            CollectionAssert.AreEqual(original,source.uv2); CollectionAssert.AreEqual(before,reduced.uv2);
            for (int slot = 0; slot < 2; slot++)
            {
                var old = reduced.GetIndices(slot); var now = result.GetIndices(slot);
                for (int i = 0; i < old.Length; i++)
                {
                    Assert.That(result.vertices[now[i]],Is.EqualTo(reduced.vertices[old[i]]));
                    Assert.That(result.normals[now[i]],Is.EqualTo(reduced.normals[old[i]]));
                    Assert.That(result.tangents[now[i]],Is.EqualTo(reduced.tangents[old[i]]));
                    Assert.That(result.colors32[now[i]],Is.EqualTo(reduced.colors32[old[i]]));
                    Assert.That(result.uv2[now[i]],Is.EqualTo(source.uv2[old[i]]));
                    foreach (int ch in new[] {0,3})
                    {
                        var a = new List<Vector4>(); var b = new List<Vector4>(); result.GetUVs(ch,a); reduced.GetUVs(ch,b);
                        Assert.That(a[now[i]],Is.EqualTo(b[old[i]]));
                    }
                }
            }
            Assert.That(result.GetVertexAttributes(),Is.EqualTo(reduced.GetVertexAttributes()));
        }
        [Test] public void MissingUv2SkipsTheTransfer()
        {
            var source = Source(); var reduced = Track(UnityEngine.Object.Instantiate(source));
            reduced.uv2 = Array.Empty<Vector2>();
            Assert.That(LodSharedUvTransfer.Transfer(source,reduced,new MeshSimplifier.SimplifySettings(),out var report),Is.Null);
            Assert.That(report.accepted,Is.False);
        }
        [Test] public void CancellationDuringVerificationDestroysOwnedMesh()
        {
            var source = Source(); var reduced = Track(UnityEngine.Object.Instantiate(source));
            reduced.uv2 = Enumerable.Repeat(new Vector2(.5f,.5f),6).ToArray(); var original = reduced.uv2;
            int count = Resources.FindObjectsOfTypeAll<Mesh>().Length, checks = 0;
            Assert.Throws<OperationCanceledException>(() => LodSharedUvTransfer.Transfer(source,reduced,
                new MeshSimplifier.SimplifySettings { uvChannel = 1 },out _,() => ++checks > 9));
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length,Is.EqualTo(count));
            CollectionAssert.AreEqual(original,reduced.uv2);
        }
    }
}
