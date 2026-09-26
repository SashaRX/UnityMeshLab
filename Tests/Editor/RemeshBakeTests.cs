using System;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class RemeshBakeTests
    {
        static RemeshSource.Map Map() => new RemeshSource.Map();
        static RemeshSource Source()
        {
            return new RemeshSource {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                tangents = new[] { new Vector4(1,0,0,1), new Vector4(1,0,0,1), new Vector4(1,0,0,1) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, indices = new[] { 0,1,2 },
                colors = new[] { new Color(1,0,0,0.25f), new Color(0,1,0,0.5f), new Color(0,0,1,1) }, hasColors = true,
                faceMaterials = new[] { 0 }, diagonal = Mathf.Sqrt(2),
                materials = new[] { new RemeshSource.Surface { color = Map(), normal = Map(), metal = Map(), ao = Map(), emission = Map(),
                    tint = new Color(0.25f,0.5f,0.75f), emissionTint = new Color(4,2,1), metallic = 0.8f,
                    smoothness = 0.6f, aoStrength = 1, normalScale = 1 } }
            };
        }
        [Test]
        public void SettingsRejectNaNAndInvalidResolution()
        {
            Assert.Throws<ArgumentException>(() => new RemeshSettings { maximumError = float.NaN }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { textureResolution = 1000 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { projectionDistance = 0 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { bakeSamples = 3 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { chartIterations = 0 }.Validate());
            Assert.DoesNotThrow(() => new RemeshSettings { targetTriangles = 0 }.Validate());
        }
        [Test]
        public void BarycentricSupportsBothWindings()
        {
            Assert.IsTrue(RemeshBaker.Barycentric(new Vector2(0.2f,0.3f), Vector2.zero, Vector2.right, Vector2.up, out var a));
            Assert.IsTrue(RemeshBaker.Barycentric(new Vector2(0.2f,0.3f), Vector2.zero, Vector2.up, Vector2.right, out var b));
            Assert.That(a.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(a.y, Is.EqualTo(b.z).Within(1e-5f));
            Assert.IsFalse(RemeshBaker.Barycentric(Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, out _));
        }
        [Test]
        public void MaterialChannelsRemainSeparateAndEmissionRetainsHdr()
        {
            var source = Source();
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(1,0,0,1),
                out var color, out var normal, out var metal, out var ao, out var emission);
            Assert.That(color.r, Is.EqualTo(new Color(0.25f,0.5f,0.75f).gamma.r).Within(1e-5f));
            Assert.That(normal.r, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(normal.b, Is.EqualTo(1).Within(1e-5f));
            Assert.That(metal.r, Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(metal.a, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(ao.g, Is.EqualTo(1));
            Assert.That(emission.r, Is.EqualTo(4));
        }
        [Test]
        public void SourceNormalMapIsReprojectedIntoNewTangentFrame()
        {
            var source = Source();
            source.materials[0].normal.image = new RemeshSource.Image {
                width=1,height=1,pixels=new[] { new Color32(204,128,230,255) }, wrapU=TextureWrapMode.Clamp,wrapV=TextureWrapMode.Clamp };
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(0,1,0,1),
                out _,out var normal,out _,out _,out _);
            Assert.That(normal.r, Is.EqualTo(0.5f).Within(0.01f));
            Assert.That(normal.g, Is.EqualTo(0.2f).Within(0.01f));
            Assert.That(normal.b, Is.EqualTo(0.9f).Within(0.01f));
        }
        [Test]
        public void CoincidentSurfaceBakesWithoutMissesAndHonorsCancellation()
        {
            var source=Source();
            var target=new RemeshNative.Geometry { positions=source.positions, normals=source.normals, uv=source.uv, indices=source.indices };
            var settings=new RemeshSettings { textureResolution=64,padding=2 };
            var maps=RemeshBaker.Bake(source,target,source.tangents,settings,CancellationToken.None);
            Assert.That(maps.covered, Is.GreaterThan(1000));
            Assert.That(maps.misses, Is.Zero);
            using (var cancellation=new CancellationTokenSource()) {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => RemeshBaker.Bake(source,target,source.tangents,settings,cancellation.Token));
            }
        }
        [Test]
        public void SourceRootFollowsSelectionAndResolvesLodChildren()
        {
            var root=new GameObject("RemeshSelectionRoot");
            var child=GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.transform.SetParent(root.transform);
            root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(0.5f,new Renderer[] { child.GetComponent<Renderer>() }) });
            var light=new GameObject("RemeshSelectionLight",typeof(Light));
            var previous=Selection.objects;
            try {
                var tool=new RemeshBakeTool();
                Selection.activeGameObject=child; tool.FollowSelection();
                Assert.AreSame(root,tool.Source);
                Selection.activeGameObject=light; tool.FollowSelection();
                Assert.AreSame(root,tool.Source);
            }
            finally {
                Selection.objects=previous;
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(light);
            }
        }
        [Test]
        public void MultisamplingCoversThinChartEdges()
        {
            var source=Source();
            // A band between texel centres at 64² (row 6 centre is v=0.1016): centre-only
            // sampling sees no texel at all, 4×4 samples at v=0.1035 do.
            var sliver=new RemeshNative.Geometry { positions=source.positions, normals=source.normals,
                uv=new[] { new Vector2(0.1f,0.103f), new Vector2(0.9f,0.103f), new Vector2(0.9f,0.104f) }, indices=source.indices };
            Assert.Throws<InvalidOperationException>(() => RemeshBaker.Bake(source,sliver,source.tangents,
                new RemeshSettings { textureResolution=64,padding=1,bakeSamples=1 },CancellationToken.None));
            var super=RemeshBaker.Bake(source,sliver,source.tangents,new RemeshSettings { textureResolution=64,padding=1,bakeSamples=16 },CancellationToken.None);
            Assert.That(super.covered, Is.GreaterThan(0));
            Assert.That(super.misses, Is.Zero);
        }
        [Test]
        public void VertexColorAndAlphaTransferIndependently()
        {
            var source=Source();
            var target=new RemeshNative.Geometry { positions=source.positions, normals=source.normals, uv=source.uv, indices=source.indices };
            var bvh=new TriangleBvh(source.positions,source.indices);
            var both=RemeshBaker.TransferVertexColors(source,target,bvh,new RemeshSettings { transferVertexColor=true,transferVertexAlpha=true },CancellationToken.None);
            Assert.That(both[0].r, Is.EqualTo(1).Within(1e-4f));
            Assert.That(both[1].g, Is.EqualTo(1).Within(1e-4f));
            Assert.That(both[0].a, Is.EqualTo(0.25f).Within(1e-4f));
            var alpha=RemeshBaker.TransferVertexColors(source,target,bvh,new RemeshSettings { transferVertexAlpha=true },CancellationToken.None);
            Assert.That(alpha[2].a, Is.EqualTo(1).Within(1e-4f));
            Assert.That(alpha[2].b, Is.EqualTo(1));
            Assert.That(alpha[1].a, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(alpha[1].g, Is.EqualTo(1).Within(1e-4f));
            Assert.That(alpha[1].r, Is.EqualTo(1).Within(1e-4f));
        }
        [Test]
        public void UvIslandNormalsAreHardOnlyAtSplitVertices()
        {
            // Two faces folded 90° along x. Shared vertices smooth; split vertices stay hard.
            var p=new[] { Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, Vector3.zero, Vector3.right };
            var shared=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 1,0,3 } };
            RemeshNative.SmoothWithinSplitVertices(shared);
            Assert.That(Vector3.Angle(shared.normals[0], Vector3.Normalize(new Vector3(0,-1,-1))), Is.LessThan(0.01f));
            var split=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 5,4,3 } };
            RemeshNative.SmoothWithinSplitVertices(split);
            Assert.That(Vector3.Angle(split.normals[0], split.normals[4]), Is.EqualTo(90).Within(0.01f));
        }
        [Test]
        public void ImageSamplingUsesLinearInterpolationAndWrapModes()
        {
            var image=new RemeshSource.Image { width=2,height=1,pixels=new[] { new Color32(0,0,0,255),new Color32(255,255,255,255) },
                srgb=true,wrapU=TextureWrapMode.Repeat,wrapV=TextureWrapMode.Clamp };
            Assert.That(image.Sample(new Vector2(0.5f,0.5f)).r,Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(image.Sample(new Vector2(1.25f,0.5f)).r,Is.EqualTo(0).Within(1e-5f));
            image.wrapU=TextureWrapMode.Clamp;
            Assert.That(image.Sample(new Vector2(1.25f,0.5f)).r,Is.EqualTo(1).Within(1e-5f));
        }
    }
}
