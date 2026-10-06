using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshPreviewNormalPackingTests
    {
        [TestCase(0f, 0f, 0f, false)]
        [TestCase(.45f, .2f, 0f, false)]
        [TestCase(.45f, .2f, 90f, false)]
        [TestCase(-.45f, -.2f, 180f, true)]
        [TestCase(.45f, -.2f, 45f, true)]
        public void ProductionResultMaterialMatchesThePhysicalNormalAcrossChartTangents(float x, float y, float angle, bool mirrored)
        {
            RequireGraphics();
            var normal = new Vector3(x, y, Mathf.Sqrt(1 - x * x - y * y));
            using (var fixture = new Fixture(normal, angle, mirrored))
            using (var preview = new RemeshPreview()) {
                var item = ResultItem(preview, fixture.data);
                Assert.That(item.materials[0].GetTexture("_BumpMap"), Is.InstanceOf<RenderTexture>(),
                    "Production Result must use the Lit-packed GPU texture.");
                var tangent = (Vector3)fixture.mesh.tangents[0];
                var expectedNormal = (tangent * normal.x + Vector3.Cross(Vector3.forward, tangent) *
                    (mirrored ? -normal.y : normal.y) + Vector3.forward * normal.z).normalized;
                AssertLit(item, fixture.mesh, expectedNormal, "preview-packing-" + angle + "-" + mirrored + "-" + x);
            }
        }

        [Test]
        public void AndroidDxt5nmProductionPreviewKeepsANeutralNormalNeutral()
        {
            RequireGraphics();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android ||
                RemeshNormalPreviewPacking.ActiveEncoding() != NormalMapEncoding.DXT5nm)
                Assert.Ignore("Run this fixture in the Android DXT5nm-normal-encoding harness.");
            using (var fixture = new Fixture(Vector3.forward, 90, false))
            using (var preview = new RemeshPreview()) {
                AssertLit(ResultItem(preview, fixture.data), fixture.mesh, Vector3.forward, "preview-android-ag-neutral");
            }
        }

        [Test]
        public void CanonicalMapDisplayAndBytesStaySeparateFromTheLitTextureAndBothCachesRelease()
        {
            RequireGraphics();
            var normal = new Vector3(-.4f, .25f, Mathf.Sqrt(1 - .4f * .4f - .25f * .25f));
            using (var fixture = new Fixture(normal, 0, false))
            using (var preview = new RemeshPreview()) {
                var before = (Color32[])fixture.data.maps.normal.Clone();
                var first = ResultItem(preview, fixture.data).materials[0].GetTexture("_BumpMap");
                var method = typeof(RemeshPreview).GetMethod("MapTexture", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(method);
                var display = (Texture2D)method.Invoke(preview, new object[] { fixture.data.maps, RemeshPreview.Channel.Normal });
                Assert.That(display, Is.Not.SameAs(first));
                CollectionAssert.AreEqual(before, display.GetPixels32(), "Maps displays canonical RGB normal bytes.");
                CollectionAssert.AreEqual(before, fixture.data.maps.normal, "Packing must never mutate exported normal bytes.");
                Assert.That(ResultItem(preview, fixture.data).materials[0].GetTexture("_BumpMap"), Is.SameAs(first));
                preview.Invalidate();
                Assert.IsFalse(first, "Invalidate must release the Lit normal GPU texture.");
                Assert.IsFalse(display, "Invalidate must release the canonical Maps texture too.");
            }
        }

        [Test]
        public void ProductionNormalPackingPreservesTheActiveRenderTarget()
        {
            RequireGraphics();
            var previous = RenderTexture.active;
            var sentinel = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try {
                RenderTexture.active = sentinel;
                using (var fixture = new Fixture(Vector3.forward, 0, false))
                using (var preview = new RemeshPreview()) {
                    ResultItem(preview, fixture.data);
                    Assert.That(RenderTexture.active, Is.SameAs(sentinel), "Fill3D must preserve its caller's active render target.");
                }
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(sentinel); }
        }

        static MeshViewport3D.Item ResultItem(RemeshPreview preview, RemeshPreview.Data data)
        {
            var items = new List<MeshViewport3D.Item>();
            preview.Show(RemeshPreview.Stage.Result);
            Assert.IsTrue(preview.Fill3D(data, items)); Assert.AreEqual(1, items.Count);
            Assert.AreEqual(data.spaceToWorld, items[0].matrix);
            return items[0];
        }

        static void AssertLit(MeshViewport3D.Item item, Mesh mesh, Vector3 expectedNormal, string name)
        {
            // Use the actual production Fill3D material. The independent control
            // has no normal map and shades with explicit mesh normals, so a wrong
            // R/A decoder shared by two textured materials cannot pass the test.
            var actualMaterial = item.materials[0];
            var referenceMaterial = new Material(actualMaterial) { hideFlags = HideFlags.HideAndDontSave };
            var referenceMesh = Object.Instantiate(mesh);
            try {
                referenceMaterial.DisableKeyword("_NORMALMAP"); referenceMaterial.SetTexture("_BumpMap", null);
                referenceMesh.normals = new[] { expectedNormal, expectedNormal, expectedNormal, expectedNormal };
                int index = 0;
                foreach (var light in new[] { new Vector3(.7f, -.25f, 1), new Vector3(-.6f, .4f, 1) }) {
                    var expected = RemeshNormalFrameTests.RenderMean(referenceMesh, referenceMaterial, light, name + "-expected-" + index);
                    var actual = RemeshNormalFrameTests.RenderMean(item.mesh, actualMaterial, light, name + "-production-" + index);
                    ++index;
                    Assert.That(Mathf.Max(expected.r, expected.g, expected.b), Is.GreaterThan(.025f), "A blank control is not a packing oracle.");
                    Assert.That(Mathf.Max(actual.r, actual.g, actual.b), Is.GreaterThan(.025f));
                    float error = Mathf.Max(Mathf.Abs(actual.r - expected.r), Mathf.Abs(actual.g - expected.g), Mathf.Abs(actual.b - expected.b));
                    Assert.That(error, Is.LessThan(.025f), "Production Result normal packing must preserve lighting; target=" +
                        EditorUserBuildSettings.activeBuildTarget + ", encoding=" + RemeshNormalPreviewPacking.ActiveEncoding() +
                        ", shader=" + actualMaterial.shader.name + ", expected=" + expected + ", actual=" + actual);
                }
            }
            finally { Object.DestroyImmediate(referenceMaterial); Object.DestroyImmediate(referenceMesh); }
        }

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Normal preview packing requires a graphics device.");
        }

        sealed class Fixture : IDisposable
        {
            internal readonly Mesh mesh;
            internal readonly RemeshPreview.Data data;
            readonly Texture2D baseColor;

            internal Fixture(Vector3 normal, float tangentAngle, bool mirrored)
            {
                const int size = 16;
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
                mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
                mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
                var t = Quaternion.AngleAxis(tangentAngle, Vector3.forward) * Vector3.right;
                var tangent = new Vector4(t.x, t.y, t.z, mirrored ? -1 : 1);
                mesh.tangents = new[] { tangent, tangent, tangent, tangent };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }; mesh.RecalculateBounds();
                var maps = new RemeshBaker.Maps { size = size, color = new Color32[size * size], normal = new Color32[size * size],
                    metal = new Color32[size * size], ao = new Color32[size * size], emission = new Color[size * size] };
                Color32 encoded = new Color(normal.x * .5f + .5f, normal.y * .5f + .5f, normal.z * .5f + .5f, 1);
                for (int i = 0; i < maps.color.Length; ++i) {
                    maps.color[i] = new Color32(204, 204, 204, 255); maps.normal[i] = encoded;
                    maps.ao[i] = new Color32(255, 255, 255, 255);
                }
                baseColor = TextureAssets.FromPixels(maps.color, size, size, false);
                data = new RemeshPreview.Data { maps = maps, baseColor = baseColor };
                data.meshes[(int)RemeshPreview.Stage.Result] = mesh;
            }

            public void Dispose() { Object.DestroyImmediate(mesh); Object.DestroyImmediate(baseColor); }
        }
    }
}
