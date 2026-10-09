using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodQslimTransferTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        Mesh Track(Mesh mesh) { meshes.Add(mesh); return mesh; }
        [TearDown] public void Cleanup() { foreach (var mesh in meshes) if (mesh) Object.DestroyImmediate(mesh); meshes.Clear(); }
        Mesh Source()
        {
            var source = Track(new Mesh { name = "QsTransferSeams",vertices = new[] {Vector3.zero,Vector3.right,Vector3.one-Vector3.forward,Vector3.zero,Vector3.one-Vector3.forward,Vector3.up},
                triangles = new[] {0,1,2,3,4,5},normals = new[] {Vector3.forward,Vector3.forward,Vector3.forward,Vector3.back,Vector3.back,Vector3.back},
                colors = new[] {new Color(2,0,-1,.1f),new Color(3,0,-1,.2f),new Color(3,1,-1,.3f),new Color(0,2,0,.9f),new Color(1,3,0,.8f),new Color(0,3,0,.7f)} });
            source.SetUVs(0,new List<Vector3> {new Vector3(0,0,2),new Vector3(1,0,2),new Vector3(1,1,2),new Vector3(10,10,3),new Vector3(11,11,3),new Vector3(10,11,3)});
            source.SetUVs(1,Enumerable.Repeat(new Vector4(1,2,3,4),6).ToList());
            source.tangents = Enumerable.Repeat(new Vector4(1,0,0,-1),6).ToArray(); return source;
        }
        static LodQslimComparison.Level Geometry(Vector3 corner) => new LodQslimComparison.Level { valid = true,submeshes = new[] {
            new LodQslimComparison.OutputSubmesh { positions = new[] {Vector3.zero,corner,Vector3.one-Vector3.forward,Vector3.up},
                triangles = new[] {0,1,2,0,2,3},birthFaces = new[] {0,1} } } };

        [Test]
        public void BirthFaceCharts_PreserveHardNormalColorUvSeamsAndHdrAlphaFormats()
        {
            var source = Source(); var colors = source.colors;
            var mesh = Track(LodQslimComparison.Transfer(source,new[] {0,1,2,0,2,3},Geometry(Vector3.right)));
            Assert.That(mesh.vertexCount,Is.EqualTo(6)); Assert.That(LodMeshData.TriangleCount(mesh),Is.EqualTo(2));
            CollectionAssert.AreEqual(source.normals,mesh.normals); CollectionAssert.AreEqual(colors,mesh.colors);
            CollectionAssert.AreEqual(source.tangents,mesh.tangents); CollectionAssert.AreEqual(colors,source.colors);
            Assert.That(mesh.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(VertexAttributeFormat.Float32));
            Assert.That(mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0),Is.EqualTo(3));
            Assert.That(mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord1),Is.EqualTo(4));
            var uv = new List<Vector3>(); mesh.GetUVs(0,uv); Assert.That(uv[0].x,Is.Zero); Assert.That(uv[3].x,Is.EqualTo(10));
        }

        [Test]
        public void MovedQslimVertex_InterpolatesFromSourceSurfaceInsteadOfBirthVertexCopy()
        {
            var source = Source(); var mesh = Track(LodQslimComparison.Transfer(source,new[] {0,1,2,0,2,3},Geometry(new Vector3(.8f,.1f,0))));
            Assert.That(mesh.colors[1].r,Is.EqualTo(2.8f).Within(1e-5)); Assert.That(mesh.colors[1].g,Is.EqualTo(.1f).Within(1e-5));
            Assert.That(mesh.colors[1].a,Is.EqualTo(.19f).Within(1e-5));
            var uv = new List<Vector3>(); mesh.GetUVs(0,uv); Assert.That(uv[1],Is.EqualTo(new Vector3(.8f,.1f,2)).Using(Vector3ComparerWithTolerance()));
        }

        [Test]
        public void RotatedOrFlippedFace_KeepsItsBirthChartAndReportsFacingFallback()
        {
            var source = Source(); var level = Geometry(Vector3.right); level.submeshes[0].triangles = new[] {2,1,0,0,2,3};
            var mesh = Track(LodQslimComparison.Transfer(source,new[] {0,1,2,0,2,3},level));
            Assert.That(level.facingFallbacks,Is.GreaterThan(0));
            Assert.That(mesh.colors.Take(3).Min(c => c.r),Is.GreaterThanOrEqualTo(2));
            var geometry = LodQslimComparison.MeasureGeometry(source,mesh);
            Assert.That(geometry.rms,Is.LessThan(1e-6)); Assert.That(geometry.max,Is.LessThan(1e-6));
        }
        static IEqualityComparer<Vector3> Vector3ComparerWithTolerance() => new ApproximateVector3Comparer();
        sealed class ApproximateVector3Comparer : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a,Vector3 b) => (a-b).sqrMagnitude < 1e-10f;
            public int GetHashCode(Vector3 obj) => 0;
        }
    }
}
