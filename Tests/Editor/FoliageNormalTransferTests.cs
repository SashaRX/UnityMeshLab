using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FoliageNormalTransferTests
    {
        internal sealed class Fixture : IDisposable
        {
            internal readonly GameObject root = new GameObject("Foliage test");
            internal readonly Mesh mesh;
            internal readonly MeshRenderer renderer;
            internal Fixture(Vector3[] positions = null)
            {
                positions ??= new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
                mesh = new Mesh { name = "LeafCards", vertices = positions, normals = Array.ConvertAll(positions, _ => Vector3.forward) };
                mesh.subMeshCount = positions.Length >= 4 ? 2 : 1; mesh.SetTriangles(new[] { 0,1,2 }, 0);
                if (mesh.subMeshCount > 1) mesh.SetTriangles(new[] { 0,2,3 }, 1);
                root.AddComponent<MeshFilter>().sharedMesh = mesh; renderer = root.AddComponent<MeshRenderer>();
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
        }

        internal static RemeshNative.IndexedMesh Plane() => new RemeshNative.IndexedMesh {
            positions = new[] { new Vector3(-10,-10,0), new Vector3(10,-10,0), new Vector3(0,10,0) }, indices = new[] { 0,1,2 },
            normals = new[] { Vector3.up, Vector3.up, Vector3.up }
        };

        [TestCase(0f)] [TestCase(1f)]
        public void BarycentricBlendAndMissRetainOriginals(float blend)
        {
            using var fixture = new Fixture(new[] { new Vector3(.2f,.3f,0), Vector3.right, new Vector3(20,20,0) });
            var s = FoliageSurfaceSamplerTests.Settings(); s.transferBlend = blend;
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, s, CancellationToken.None);
            var proxy = new RemeshNative.IndexedMesh { positions = new[] { Vector3.zero,Vector3.right,Vector3.up }, indices = new[] {0,1,2}, normals = new[] { Vector3.forward,Vector3.right,Vector3.up } };
            var hits = FoliageNormalTransfer.QueryCpu(new TriangleBvh(proxy.positions, proxy.indices), target.positions, 1, CancellationToken.None);
            var result = FoliageNormalTransfer.Resolve(target, proxy, new[] {0,1,2}, hits, s, 1, CancellationToken.None);
            Assert.AreEqual(2, result.diagnostics.transferred); Assert.AreEqual(1, result.diagnostics.misses);
            var expected = blend == 0 ? Vector3.forward : new Vector3(.2f,.3f,.5f).normalized;
            Assert.Greater(Vector3.Dot(result.normals[0], expected), .99999f); Assert.AreEqual(Vector3.forward, result.normals[2]);
            Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void SphereNormalsAreRadialFiniteAndUnit()
        {
            var proxy = FoliageProxyBuilderTests.Spheres(Vector3.zero); proxy.normals = MeshGeometry.AveragedNormals(proxy.positions, proxy.indices);
            var normals = FoliageNormalTransfer.QueryCpu(new TriangleBvh(proxy.positions, proxy.indices), proxy.positions, 2, CancellationToken.None);
            for (int i = 0; i < normals.Length; i++) {
                var n = FoliageNormalTransfer.Interpolate(proxy, normals[i].triangleIndex, normals[i].barycentric);
                Assert.Greater(Vector3.Dot(n, proxy.positions[i].normalized), .98f); Assert.That(n.magnitude, Is.EqualTo(1).Within(1e-5));
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialBoundarySplitPreservesEveryChannelAndExcludedNormals(bool recalculateTangents)
        {
            using var fixture = new Fixture(); var mesh = fixture.mesh;
            var leaf = new Material(Shader.Find("Standard")); var trunk = new Material(Shader.Find("Standard"));
            try {
                fixture.renderer.sharedMaterials = new[] { leaf, trunk };
                mesh.colors = new[] { new Color(3,.2f,.3f,.4f),Color.red,Color.green,Color.blue };
                mesh.tangents = new[] { new Vector4(1,0,0,-1),new Vector4(1,0,0,-1),new Vector4(1,0,0,-1),new Vector4(1,0,0,-1) };
                for (int ch = 0; ch < 8; ch++) mesh.SetUVs(ch, new List<Vector4> { new Vector4(0,0,ch,2), new Vector4(1,0,ch,2),new Vector4(0,1,ch,2),new Vector4(1,1,ch,2) });
                mesh.bindposes = new[] { Matrix4x4.identity }; var weights = new BoneWeight[4];
                for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 }; mesh.boneWeights = weights;
                var delta = new[] { Vector3.one,Vector3.right,Vector3.up,Vector3.forward }; mesh.AddBlendShapeFrame("Leaf", 100, delta, delta, delta);
                var s = FoliageSurfaceSamplerTests.Settings(); s.selection = FoliageFaceSelection.SelectedMaterials; s.materials = new[] { leaf };
                using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, s, CancellationToken.None);
                var n = new[] {Vector3.up,Vector3.up,Vector3.up,Vector3.forward};
                var result = target.BuildResult(n, recalculateTangents, CancellationToken.None, out int duplicated);
                try {
                    Assert.AreEqual(2, duplicated); Assert.AreEqual(6, result.vertexCount); Assert.AreEqual(2, result.subMeshCount);
                    CollectionAssert.AreEqual(new[] {4,1,5}, result.GetIndices(0)); CollectionAssert.AreEqual(mesh.GetIndices(1), result.GetIndices(1));
                    foreach (int v in result.GetIndices(1)) Assert.AreEqual(Vector3.forward, result.normals[v]);
                    foreach (int v in result.GetIndices(0)) Assert.AreEqual(Vector3.up, result.normals[v]);
                    int[] map = {0,1,2,3,0,2};
                    for (int i = 0; i < map.Length; i++) {
                        Assert.AreEqual(mesh.vertices[map[i]], result.vertices[i]); Assert.AreEqual(mesh.colors[map[i]], result.colors[i]);
                        if (!recalculateTangents || i == 0 || i == 2 || i == 3) Assert.AreEqual(mesh.tangents[map[i]], result.tangents[i]);
                        Assert.AreEqual(mesh.boneWeights[map[i]], result.boneWeights[i]);
                    }
                    for (int ch = 0; ch < 8; ch++) {
                        var a = new List<Vector4>(); var b = new List<Vector4>(); mesh.GetUVs(ch,a); result.GetUVs(ch,b);
                        Assert.AreEqual(mesh.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch)), result.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch)));
                        for (int i = 0; i < map.Length; i++) Assert.AreEqual(a[map[i]],b[i]);
                    }
                    var dp = new Vector3[6]; result.GetBlendShapeFrameVertices(0,0,dp,null,null); for (int i = 0; i < map.Length; i++) Assert.AreEqual(delta[map[i]],dp[i]);
                    Assert.AreEqual(mesh.bounds, result.bounds); CollectionAssert.AreEqual(new[] {leaf,trunk}, fixture.renderer.sharedMaterials);
                }
                finally { UnityEngine.Object.DestroyImmediate(result); }
            }
            finally { UnityEngine.Object.DestroyImmediate(leaf); UnityEngine.Object.DestroyImmediate(trunk); }
        }

        [Test]
        public void BoundarySplitPreservesMoreThanFourBoneWeightsPerVertex()
        {
            using var fixture = new Fixture();
            fixture.mesh.bindposes = new[] {Matrix4x4.identity,Matrix4x4.identity,Matrix4x4.identity,Matrix4x4.identity,Matrix4x4.identity};
            var values = new BoneWeight1[20];
            for (int v = 0; v < 4; v++) for (int j = 0; j < 5; j++) values[v*5+j] = new BoneWeight1 {boneIndex = (v+j)%5,weight = (5-j)/15f};
            using (var counts = new NativeArray<byte>(new byte[] {5,5,5,5},Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(values,Allocator.Temp)) {
                fixture.mesh.SetBoneWeights(counts,weights);
            }
            var s = FoliageSurfaceSamplerTests.Settings(); s.selection = FoliageFaceSelection.SelectedSubmeshIndices; s.submeshes = new[] {0};
            using var target = new FoliageTargetSnapshot(fixture.renderer,Matrix4x4.identity,s,CancellationToken.None);
            var result = target.BuildResult(new[] {Vector3.up,Vector3.up,Vector3.up,Vector3.forward},false,CancellationToken.None,out int duplicated);
            try {
                Assert.AreEqual(2,duplicated); int[] map = {0,1,2,3,0,2};
                using var counts = result.GetBonesPerVertex(); using var original = fixture.mesh.GetAllBoneWeights(); using var copied = result.GetAllBoneWeights();
                for (int i = 0; i < map.Length; i++) {
                    Assert.AreEqual(5,counts[i]);
                    for (int j = 0; j < 5; j++) Assert.AreEqual(original[map[i]*5+j],copied[i*5+j]);
                }
                CollectionAssert.AreEqual(fixture.mesh.bindposes,result.bindposes);
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void CaptureNormalMatricesSupportFbxScaleMirrorAndNestedHierarchy(int variant)
        {
            using var fixture = new Fixture(new[] { Vector3.zero,Vector3.right,Vector3.up });
            var child = fixture.root.transform; var parent = new GameObject("Capture root"); var grand = new GameObject("Nested parent");
            try {
                child.SetParent(parent.transform, false);
                if (variant == 1) parent.transform.localRotation = Quaternion.Euler(90,0,0);
                if (variant == 2) parent.transform.localRotation = Quaternion.Euler(-90,0,0);
                if (variant == 3) parent.transform.localScale = Vector3.one * .01f;
                if (variant == 4) child.localScale = new Vector3(2,.5f,3);
                if (variant == 5) child.localScale = new Vector3(-2,3,.5f);
                if (variant == 6) { parent.transform.SetParent(grand.transform,false); grand.transform.localScale = new Vector3(.01f,.02f,-.03f); grand.transform.localRotation = Quaternion.Euler(-90,22,14); child.localRotation = Quaternion.Euler(32,44,5); child.localScale = new Vector3(2,3,1); }
                child.localPosition = new Vector3(.1f,.2f,.3f);
                Vector3 position = child.localPosition, scale = child.localScale; Quaternion rotation = child.localRotation; var world = child.localToWorldMatrix;
                var s = FoliageSurfaceSamplerTests.Settings(); using var target = new FoliageTargetSnapshot(fixture.renderer,parent.transform.worldToLocalMatrix,s,CancellationToken.None);
                var captureNormal = new Vector3(.3f,.8f,.2f).normalized;
                var proxy = new RemeshNative.IndexedMesh { positions = target.positions, indices = new[] {0,1,2}, normals = new[] {captureNormal,captureNormal,captureNormal} };
                var hits = FoliageNormalTransfer.QueryCpu(new TriangleBvh(proxy.positions,proxy.indices),target.positions,10,CancellationToken.None);
                var transferred = FoliageNormalTransfer.Resolve(target,proxy,new[] {0,1,2},hits,s,10,CancellationToken.None);
                foreach (var n in transferred.normals) {
                    var actual = child.worldToLocalMatrix.transpose.MultiplyVector(n).normalized;
                    var expected = parent.transform.worldToLocalMatrix.transpose.MultiplyVector(captureNormal).normalized;
                    Assert.Greater(Vector3.Dot(actual,expected),.99999f);
                }
                Assert.AreEqual(position,child.localPosition); Assert.AreEqual(rotation,child.localRotation); Assert.AreEqual(scale,child.localScale); Assert.AreEqual(world,child.localToWorldMatrix);
                Assert.AreSame(fixture.mesh,fixture.root.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { child.SetParent(null,true); UnityEngine.Object.DestroyImmediate(parent); UnityEngine.Object.DestroyImmediate(grand); }
        }

        [Test]
        public void TangentsNeedNondegenerateUv0AndHemisphereIsOptIn()
        {
            using var fixture = new Fixture(new[] {Vector3.zero,Vector3.right,Vector3.up});
            Assert.IsFalse(FoliageTargetSnapshot.HasValidUv0(fixture.mesh)); fixture.mesh.uv = new[] {Vector2.zero,Vector2.right,Vector2.up}; Assert.IsTrue(FoliageTargetSnapshot.HasValidUv0(fixture.mesh));
            var s = FoliageSurfaceSamplerTests.Settings(); using var target = new FoliageTargetSnapshot(fixture.renderer,Matrix4x4.identity,s,CancellationToken.None);
            var result = target.BuildResult(new[] {Vector3.up,Vector3.up,Vector3.up},true,CancellationToken.None,out _);
            try { Assert.AreEqual(3,result.tangents.Length); foreach (var t in result.tangents) Assert.That(Vector3.Dot((Vector3)t,Vector3.up),Is.EqualTo(0).Within(1e-5)); }
            finally { UnityEngine.Object.DestroyImmediate(result); }
            var proxy = Plane(); proxy.normals = new[] {Vector3.back,Vector3.back,Vector3.back};
            var hits = FoliageNormalTransfer.QueryCpu(new TriangleBvh(proxy.positions,proxy.indices),target.positions,2,CancellationToken.None);
            Assert.AreEqual(Vector3.back,FoliageNormalTransfer.Resolve(target,proxy,new[] {0,1,2},hits,s,2,CancellationToken.None).normals[0]);
            s.alignToOriginalHemisphere = true; var aligned = FoliageNormalTransfer.Resolve(target,proxy,new[] {0,1,2},hits,s,2,CancellationToken.None);
            Assert.AreEqual(Vector3.forward,aligned.normals[0]); Assert.AreEqual(3,aligned.diagnostics.hemisphereFlips);
        }

        [Test]
        public void ExcludedDegenerateUvDoesNotSuppressFoliageTangents()
        {
            using var fixture = new Fixture();
            // Selected triangle 0 has valid UVs. Excluded triangle 1 is UV-degenerate
            // and shares two vertices, which must retain their original trunk frame.
            fixture.mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero };
            fixture.mesh.colors = new[] { Color.red, Color.green, Color.blue, Color.white };
            var before = new[] { new Vector4(0,1,0,-1), new Vector4(0,1,0,-1), new Vector4(0,1,0,-1), new Vector4(0,1,0,-1) };
            fixture.mesh.tangents = before;
            var settings = new FoliageNormalsSettings { selection = FoliageFaceSelection.SelectedSubmeshIndices, submeshes = new[] { 0 } };
            using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, settings, CancellationToken.None);
            var result = target.BuildResult(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.forward }, true, CancellationToken.None, out int duplicates);
            try
            {
                Assert.AreEqual(2, duplicates);
                CollectionAssert.AreEqual(fixture.mesh.GetIndices(1), result.GetIndices(1));
                foreach (int v in result.GetIndices(1))
                {
                    Assert.AreEqual(Vector3.forward, result.normals[v]);
                    Assert.AreEqual(before[v], result.tangents[v]);
                }
                foreach (int v in result.GetIndices(0))
                {
                    Assert.That(((Vector3)result.tangents[v]).magnitude, Is.EqualTo(1).Within(1e-5));
                    Assert.That(Vector3.Dot((Vector3)result.tangents[v], result.normals[v]), Is.EqualTo(0).Within(1e-5));
                }
                int[] map = { 0,1,2,3,0,2 };
                for (int i = 0; i < map.Length; i++)
                {
                    Assert.AreEqual(fixture.mesh.vertices[map[i]], result.vertices[i]);
                    Assert.AreEqual(fixture.mesh.uv[map[i]], result.uv[i]);
                    Assert.AreEqual(fixture.mesh.colors[map[i]], result.colors[i]);
                }
                CollectionAssert.AreEqual(before, fixture.mesh.tangents);
            }
            finally { UnityEngine.Object.DestroyImmediate(result); }
        }

        [Test]
        public void AdditionalVertexStreamsAreRefusedAndInvalidateExistingCapture()
        {
            using var fixture = new Fixture();
            var stream = UnityEngine.Object.Instantiate(fixture.mesh);
            try
            {
                using var target = new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, new FoliageNormalsSettings(), CancellationToken.None);
                Assert.IsTrue(target.IsCurrent(Matrix4x4.identity));
                fixture.renderer.additionalVertexStreams = stream;
                Assert.IsFalse(target.IsCurrent(Matrix4x4.identity));
                var error = Assert.Throws<InvalidOperationException>(() => new FoliageTargetSnapshot(fixture.renderer, Matrix4x4.identity, new FoliageNormalsSettings(), CancellationToken.None));
                Assert.That(error.Message, Does.Contain("Additional vertex streams"));
                Assert.AreSame(fixture.mesh, fixture.root.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreSame(stream, fixture.renderer.additionalVertexStreams);
                Assert.IsNull(target.working);
            }
            finally { fixture.renderer.additionalVertexStreams = null; UnityEngine.Object.DestroyImmediate(stream); }
        }

        [Test]
        public void MissingUvRetainsTangentsAndInvalidHitsRetainUnitOriginals()
        {
            using var fixture = new Fixture(new[] {Vector3.zero,Vector3.right,Vector3.up});
            fixture.mesh.tangents = new[] {new Vector4(1,0,0,-1),new Vector4(1,0,0,-1),new Vector4(1,0,0,-1)};
            var s = FoliageSurfaceSamplerTests.Settings(); using var target = new FoliageTargetSnapshot(fixture.renderer,Matrix4x4.identity,s,CancellationToken.None);
            LogAssert.Expect(LogType.Warning,new System.Text.RegularExpressions.Regex("UV0 is absent or degenerate"));
            var result = target.BuildResult(new[] {Vector3.up,Vector3.up,Vector3.up},true,CancellationToken.None,out _);
            try { CollectionAssert.AreEqual(fixture.mesh.tangents,result.tangents); }
            finally { UnityEngine.Object.DestroyImmediate(result); }
            var bad = new TriangleBvh.HitResult {triangleIndex = 0,distSq = float.NaN,point = Vector3.zero,barycentric = Vector3.right};
            var resolved = FoliageNormalTransfer.Resolve(target,Plane(),new[] {0,1,2},new[] {bad,bad,bad},s,1,CancellationToken.None);
            Assert.AreEqual(3,resolved.diagnostics.invalidHits); Assert.AreEqual(3,resolved.diagnostics.misses); CollectionAssert.AreEqual(fixture.mesh.normals,resolved.normals);
        }

        [UnityTest]
        public IEnumerator GpuNearestAndNormalTransferMatchCpuIncludingMisses()
        {
            if (!GpuBvh.Supported) Assert.Ignore("No compute device.");
            var proxy = FoliageProxyBuilderTests.Spheres(Vector3.zero); proxy.normals = MeshGeometry.AveragedNormals(proxy.positions,proxy.indices);
            var points = new Vector3[300]; for (int i = 0; i < points.Length; i++) points[i] = MeshGeometry.SphereDirections(points.Length)[i] * (i % 7 == 0 ? 10 : 1.1f);
            var tree = new TriangleBvh(proxy.positions,proxy.indices); var cpu = FoliageNormalTransfer.QueryCpu(tree,points,2,CancellationToken.None);
            using var gpu = GpuBvh.TryCreate(tree); Assert.IsNotNull(gpu);
            var task = FoliageNormalTransfer.QueryGpu(gpu,tree,points,2,CancellationToken.None); yield return FoliageNormalsToolTests.Await(task);
            for (int i = 0; i < points.Length; i++) {
                Assert.AreEqual(cpu[i].triangleIndex,task.Result[i].triangleIndex,"point " + i);
                if (cpu[i].triangleIndex < 0) continue;
                Assert.That((cpu[i].barycentric-task.Result[i].barycentric).magnitude,Is.LessThan(1e-4));
                Assert.That((cpu[i].point-task.Result[i].point).magnitude,Is.LessThan(1e-4));
                Assert.Greater(Vector3.Dot(FoliageNormalTransfer.Interpolate(proxy,cpu[i].triangleIndex,cpu[i].barycentric),FoliageNormalTransfer.Interpolate(proxy,task.Result[i].triangleIndex,task.Result[i].barycentric)),.999f);
            }
            using var cancelled = new CancellationTokenSource();
            var pending = FoliageNormalTransfer.QueryGpu(gpu,tree,points,2,cancelled.Token); cancelled.Cancel();
            yield return AwaitCancelled(pending); Assert.IsTrue(pending.IsCanceled);
        }
        static IEnumerator AwaitCancelled(System.Threading.Tasks.Task task) { double end = EditorApplication.timeSinceStartup + 30; while (!task.IsCompleted && EditorApplication.timeSinceStartup < end) yield return null; Assert.IsTrue(task.IsCompleted); }
    }
}
