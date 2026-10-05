using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshNormalFrameTests
    {
        static readonly int[] Triangle = { 0, 1, 2 };
        static readonly Vector3 Desired = new Vector3(.6f, .8f / Mathf.Sqrt(2), .8f / Mathf.Sqrt(2));

        [TestCase(false)]
        [TestCase(true)]
        public void EncodingInvertsTheRawShaderInterpolantRatherThanItsOrthonormalApproximation(bool urp)
        {
            var normals = new[] { Vector3.forward, Vector3.up, Vector3.forward };
            var tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            var bary = new Vector3(.25f, .5f, .25f);
            var frame = RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, bary, Mode(urp));
            Assert.IsTrue(frame.TryEncode(Desired, out var encoded));
            AssertAngle(ShaderDecode(normals, tangents, bary, encoded, urp, Matrix4x4.identity), Desired, .03f);
            var oldGsEncoded = new Vector3(.6f, 0, .8f);
            Assert.That(Vector3.Angle(ShaderDecode(normals, tangents, bary, oldGsEncoded, urp, Matrix4x4.identity), Desired),
                Is.GreaterThan(9), "The independent shader oracle must expose the old normalized-column error.");
        }

        [Test]
        public void BuiltInInterpolatesVertexBitangentsWhileUrpConstructsTheFragmentBitangent()
        {
            var normals = new[] { Vector3.forward, Vector3.right, Vector3.forward };
            var tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1), new Vector4(1, 0, 0, 1) };
            var bary = new Vector3(.25f, .5f, .25f);
            var built = RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, bary, RemeshNormalFrame.Mode.BuiltIn);
            var urp = RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, bary, RemeshNormalFrame.Mode.Urp);
            Assert.That((built.bitangent - new Vector3(0, .5f, .5f)).magnitude, Is.LessThan(1e-6f));
            Assert.That((urp.bitangent - new Vector3(-.25f, .25f, .25f)).magnitude, Is.LessThan(1e-6f));
            var desired = Vector3.up;
            Assert.IsTrue(built.TryEncode(desired, out var a)); Assert.IsTrue(urp.TryEncode(desired, out var b));
            AssertAngle(ShaderDecode(normals, tangents, bary, a, false, Matrix4x4.identity), desired, .03f);
            AssertAngle(ShaderDecode(normals, tangents, bary, b, true, Matrix4x4.identity), desired, .03f);
            Assert.That(Vector3.Angle(ShaderDecode(normals, tangents, bary, a, true, Matrix4x4.identity), desired), Is.GreaterThan(5));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void WorldFrameMatchesShaderVertexTransformsUnderNonuniformAndReflectedScale(bool urp, bool reflected)
        {
            var normals = new[] { Vector3.forward, Vector3.right, Vector3.forward };
            var tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(0, 1, 0, -1), new Vector4(1, 0, 0, -1) };
            var bary = new Vector3(.2f, .35f, .45f);
            var matrix = Matrix4x4.TRS(new Vector3(7, -9, 4), Quaternion.Euler(17, -31, 12), new Vector3(reflected ? -2 : 2, .5f, 3));
            var wanted = ShaderDecode(normals, tangents, bary, new Vector3(.3f, -.2f, .9f).normalized, urp, matrix);
            var frame = RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, bary, Mode(urp), matrix);
            Assert.IsTrue(frame.TryEncode(wanted, out var encoded));
            AssertAngle(ShaderDecode(normals, tangents, bary, encoded, urp, matrix), wanted, .03f);
            AssertAngle(frame.Decode(encoded), wanted, .03f);
        }

        [TestCase(1e-20f)]
        [TestCase(1f)]
        [TestCase(1e20f)]
        public void InverseFrameRetainsDirectionAcrossFrameScale(float scale)
        {
            var frame = new RemeshNormalFrame.Frame(Vector3.right * scale, Vector3.up * scale, Vector3.forward * scale);
            Assert.IsTrue(frame.TryEncode(Desired, out var encoded));
            AssertAngle(encoded, Desired, .03f); AssertAngle(frame.Decode(encoded), Desired, .03f);
            AssertAngle(frame.Rotated(Quaternion.Euler(20, 40, -10)).Decode(encoded), Quaternion.Euler(20, 40, -10) * Desired, .03f);
        }

        [Test]
        public void SingularAndNonfiniteFramesFailInsteadOfManufacturingAFlatNormal()
        {
            var singular = new RemeshNormalFrame.Frame(Vector3.right, Vector3.right, Vector3.forward);
            Assert.IsFalse(singular.TryEncode(Desired, out var failed)); Assert.AreEqual(Vector3.zero, failed);
            var invalid = new RemeshNormalFrame.Frame(new Vector3(float.NaN, 0, 0), Vector3.up, Vector3.forward);
            Assert.IsFalse(invalid.TryEncode(Desired, out _));
            var valid = new RemeshNormalFrame.Frame(Vector3.right, Vector3.up, Vector3.forward);
            Assert.IsFalse(valid.TryEncode(new Vector3(float.PositiveInfinity, 0, 0), out _));
            Assert.IsFalse(valid.TryEncode(Vector3.zero, out _));
            var normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            var tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            Assert.IsFalse(RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, Vector3.one / 3,
                RemeshNormalFrame.Mode.Urp, Matrix4x4.Scale(new Vector3(1, 0, 1))).TryEncode(Desired, out _));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ZeroVertexContributionDoesNotInvalidateAnInvertibleInterpolatedShaderFrame(bool urp)
        {
            var normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            var tangents = new[] { new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            var bary = Vector3.one / 3;
            var frame = RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, bary, Mode(urp));
            Assert.IsTrue(frame.TryEncode(Desired, out var encoded));
            AssertAngle(ShaderDecode(normals, tangents, bary, encoded, urp, Matrix4x4.identity), Desired, .05f);
            Assert.IsFalse(RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, Vector3.right, Mode(urp)).TryEncode(Desired, out _));
            tangents[0].x = float.NaN;
            Assert.IsFalse(RemeshNormalFrame.Interpolate(normals, tangents, Triangle, 0, bary, Mode(urp)).TryEncode(Desired, out _));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BakeAveragesTransportedWorldDirectionsBeforeInvertingTheReceiverFrame(bool urp)
        {
            var tangentUp = new Vector4(.5f, Mathf.Sqrt(.75f), 0, 1);
            var tangentDown = new Vector4(.5f, -Mathf.Sqrt(.75f), 0, 1);
            const float halfTexel = .5f / 64;
            var target = new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up }, indices = Triangle,
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                tangents = new[] { tangentUp, tangentDown, tangentUp }, normalFrameMode = Mode(urp),
                uv = new[] { new Vector2(-halfTexel, -halfTexel), new Vector2(1 - halfTexel, -halfTexel), new Vector2(-halfTexel, 1 - halfTexel) }
            };
            target.surfaceNeighbors = RemeshSurfaceTopology.Build(target.positions, target.indices, CancellationToken.None);
            var halfX = Vector3.right * .5f; var middle = new Vector3(.5f, .5f, 0); var detail = new Vector3(.8f, 0, .6f);
            // The physical surface is cut at X=.5: the pixel at (31,15) receives
            // two equal-area samples from each side with different source normals.
            var source = new RemeshSource {
                positions = new[] { Vector3.zero, halfX, middle, Vector3.zero, middle, Vector3.up, halfX, Vector3.right, middle },
                indices = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
                normals = new[] { detail, detail, detail, detail, detail, detail, Vector3.forward, Vector3.forward, Vector3.forward },
                uv = new Vector2[9], tangents = new Vector4[9], faceMaterials = new[] { 0, 0, 0 }, diagonal = Mathf.Sqrt(2),
                materials = new[] { new RemeshSource.Surface { color = new RemeshSource.Map(), normal = new RemeshSource.Map(),
                    metal = new RemeshSource.Map(), ao = new RemeshSource.Map(), emission = new RemeshSource.Map(), tint = Color.white,
                    emissionTint = Color.black, normalScale = 1, aoStrength = 1, normalFrameMode = Mode(urp) } }
            };
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings { textureResolution = 64, padding = 1,
                dilationRadius = 0, bakeSamples = 4, bakeSourceAO = false, vertexColorTint = false }, CancellationToken.None);
            Assert.AreEqual(0, maps.misses); Assert.AreEqual(0, maps.invalidNormalFrames);
            var pixel = maps.normal[15 * 64 + 31]; float nx = pixel.r / 127.5f - 1, ny = pixel.g / 127.5f - 1;
            var unpacked = new Vector3(nx, ny, Mathf.Sqrt(Mathf.Max(0, 1 - nx * nx - ny * ny)));
            var bary = new Vector3(.25f, .5f, .25f); var expected = (detail + Vector3.forward).normalized;
            AssertAngle(ShaderDecode(target.normals, target.tangents, bary, unpacked, urp, Matrix4x4.identity), expected, 1);
            // M=diag(.5,.5,1): normalizing each inverse before the area average
            // changes contribution weights; the oracle must detect that old path.
            var wrongEncoded = (new Vector3(1.6f, 0, .6f).normalized + Vector3.forward).normalized;
            Assert.That(Vector3.Angle(ShaderDecode(target.normals, target.tangents, bary, wrongEncoded, urp, Matrix4x4.identity), expected), Is.GreaterThan(7));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BakedNormalRecoversConstantSourceDirectionAcrossSmoothTargetShading(bool urp)
        {
            var target = SmoothQuad(Mode(urp));
            var maps = Bake(target);
            Assert.AreEqual(4096, maps.covered); Assert.AreEqual(0, maps.misses);
            for (int y = 8; y < 56; y += 7) for (int x = 8; x < 56; x += 7) {
                int face = x + y + 1 < 64 ? 0 : 1;
                var uv = new Vector2((x + .5f) / 64, (y + .5f) / 64);
                Vector3 bary = face == 0 ? new Vector3(1 - uv.x - uv.y, uv.x, uv.y) : new Vector3(1 - uv.x, 1 - uv.y, uv.x + uv.y - 1);
                var color = maps.normal[y * 64 + x];
                float nx = color.r / 127.5f - 1, ny = color.g / 127.5f - 1;
                var unpacked = new Vector3(nx, ny, Mathf.Sqrt(Mathf.Max(0, 1 - nx * nx - ny * ny)));
                var n = new Vector3[3]; var t = new Vector4[3];
                for (int k = 0; k < 3; ++k) { int vertex = target.indices[face * 3 + k]; n[k] = target.normals[vertex]; t[k] = target.tangents[vertex]; }
                AssertAngle(ShaderDecode(n, t, bary, unpacked, urp, Matrix4x4.identity), Desired, 1.15f);
            }
        }

        [Test]
        public void BakePreservesAndReportsNegativeTangentHemisphere()
        {
            var target = SmoothQuad(RemeshNormalFrame.Mode.BuiltIn);
            target.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            var maps = Bake(target, Vector3.back);
            Assert.AreEqual(4096, maps.covered); Assert.AreEqual(0, maps.misses);
            Assert.AreEqual(4096, maps.negativeNormalTexels);
            Assert.AreEqual(0, maps.invalidNormalFrames);
            foreach (var pixel in maps.normal) {
                Assert.That(pixel.b, Is.EqualTo(0), "Negative Z must remain visible to diagnostics rather than be clamped or made positive.");
                Assert.That(pixel.r, Is.EqualTo(128)); Assert.That(pixel.g, Is.EqualTo(128));
            }
        }

        [Test]
        public void BakeReportsSingularTargetFrameAndWritesInitializedNeutralNormal()
        {
            var target = SmoothQuad(RemeshNormalFrame.Mode.Urp);
            target.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            for (int i = 0; i < target.tangents.Length; ++i) target.tangents[i].w = 0;
            var maps = Bake(target);
            Assert.AreEqual(4096, maps.covered); Assert.AreEqual(0, maps.misses);
            Assert.AreEqual(4096, maps.invalidNormalFrames); Assert.AreEqual(0, maps.negativeNormalTexels);
            foreach (var pixel in maps.normal) Assert.AreEqual(new Color32(128, 128, 255, 255), pixel);
        }

        [Test]
        public void ActualLitShaderMatchesConstantSourceShadingAfterBakeAcrossVaryingTargetFrames()
        {
            RequireGraphics();
            var shader = RemeshExporter.ResolveShader(false, out bool urp);
            var target = SmoothQuad(Mode(urp)); var maps = Bake(target);
            var targetMesh = MeshFor(target); var sourceMesh = MeshFor(target);
            sourceMesh.normals = new[] { Desired, Desired, Desired, Desired };
            var map = TextureAssets.FromPixels(maps.normal, 64, 64, true);
            map.filterMode = FilterMode.Bilinear; map.wrapMode = TextureWrapMode.Clamp;
            var legacy = new Color32[4096];
            for (int y = 0; y < 64; ++y) for (int x = 0; x < 64; ++x) {
                float u = (x + .5f) / 64;
                var n = new Vector3(0, u, 1 - u).normalized; var b = Vector3.Cross(n, Vector3.right);
                var encoded = new Vector3(Desired.x, Vector3.Dot(Desired, b), Vector3.Dot(Desired, n));
                legacy[y * 64 + x] = new Color(encoded.x * .5f + .5f, encoded.y * .5f + .5f, encoded.z * .5f + .5f, 1);
            }
            var oldMap = TextureAssets.FromPixels(legacy, 64, 64, true);
            oldMap.filterMode = FilterMode.Bilinear; oldMap.wrapMode = TextureWrapMode.Clamp;
            RenderTexture packed = null, oldPacked = null;
            Material sourceMaterial = null, targetMaterial = null, oldMaterial = null;
            try {
                packed = RemeshNormalPreviewPacking.Create(map, urp);
                oldPacked = RemeshNormalPreviewPacking.Create(oldMap, urp);
                sourceMaterial = Lit(shader, urp, null); targetMaterial = Lit(shader, urp, packed); oldMaterial = Lit(shader, urp, oldPacked);
                float oldError = 0;
                foreach (var light in new[] { new Vector3(-.65f, .35f, 1), new Vector3(.4f, -.5f, 1) }) {
                    var expected = RenderMean(sourceMesh, sourceMaterial, light, "frame-source-" + light.x);
                    var actual = RenderMean(targetMesh, targetMaterial, light, "frame-inverse-" + light.x);
                    var wrong = RenderMean(targetMesh, oldMaterial, light, "frame-legacy-gs-" + light.x);
                    Assert.That(Mathf.Max(expected.r, expected.g, expected.b), Is.GreaterThan(.04f), "Blank or culled renders are not an oracle.");
                    Assert.That(Distance(actual, expected), Is.LessThan(.025f), "The actual Lit shader must recover source shading; URP=" + urp);
                    oldError = Mathf.Max(oldError, Distance(wrong, expected));
                }
                Assert.That(oldError, Is.GreaterThan(.012f), "The render fixture must detect the old Gram-Schmidt encoding.");
            }
            finally {
                Object.DestroyImmediate(sourceMesh); Object.DestroyImmediate(targetMesh); Object.DestroyImmediate(map); Object.DestroyImmediate(oldMap);
                Object.DestroyImmediate(sourceMaterial); Object.DestroyImmediate(targetMaterial); Object.DestroyImmediate(oldMaterial);
                if (packed) { packed.Release(); Object.DestroyImmediate(packed); }
                if (oldPacked) { oldPacked.Release(); Object.DestroyImmediate(oldPacked); }
            }
        }

        // Independent shader oracle: URP 17.2 LitForwardPass creates B=cross(N,T)*w
        // in the fragment; StandardCore interpolates vertex B (ORTHONORMALIZE=0).
        // Vertex normals use inverse transpose, tangents use object-to-world, and
        // GetOddNegativeScale/unity_WorldTransformParams.w flips handedness.
        static Vector3 ShaderDecode(Vector3[] normals, Vector4[] tangents, Vector3 bary, Vector3 encoded, bool urp, Matrix4x4 matrix)
        {
            Vector3 n = Vector3.zero, t = Vector3.zero, b = Vector3.zero; float w = 0;
            for (int k = 0; k < 3; ++k) {
                var vn = matrix.inverse.transpose.MultiplyVector(normals[k]).normalized;
                var vt = matrix.MultiplyVector(new Vector3(tangents[k].x, tangents[k].y, tangents[k].z)).normalized;
                float sign = tangents[k].w * (matrix.determinant < 0 ? -1 : 1);
                n += vn * bary[k]; t += vt * bary[k]; w += sign * bary[k]; b += Vector3.Cross(vn, vt) * (sign * bary[k]);
            }
            if (urp) b = Vector3.Cross(n, t) * w;
            return (t * encoded.x + b * encoded.y + n * encoded.z).normalized;
        }

        static RemeshNative.Geometry SmoothQuad(RemeshNormalFrame.Mode mode)
        {
            var target = new RemeshNative.Geometry {
                positions = new[] { new Vector3(-2, -2, 0), new Vector3(2, -2, 0), new Vector3(-2, 2, 0), new Vector3(2, 2, 0) },
                indices = new[] { 0, 1, 2, 2, 1, 3 }, normals = new[] { Vector3.forward, Vector3.up, Vector3.forward, Vector3.up },
                tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, normalFrameMode = mode
            };
            target.surfaceNeighbors = RemeshSurfaceTopology.Build(target.positions, target.indices, CancellationToken.None);
            return target;
        }

        static RemeshBaker.Maps Bake(RemeshNative.Geometry target, Vector3? desired = null)
        {
            var sourceNormal = desired ?? Desired;
            var source = new RemeshSource { positions = target.positions, indices = target.indices, uv = target.uv, tangents = target.tangents,
                normals = new[] { sourceNormal, sourceNormal, sourceNormal, sourceNormal }, faceMaterials = new[] { 0, 0 }, diagonal = Mathf.Sqrt(32),
                materials = new[] { new RemeshSource.Surface { color = new RemeshSource.Map(), normal = new RemeshSource.Map(),
                    metal = new RemeshSource.Map(), ao = new RemeshSource.Map(), emission = new RemeshSource.Map(), tint = Color.white,
                    emissionTint = Color.black, normalScale = 1, aoStrength = 1, normalFrameMode = target.normalFrameMode } } };
            return RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings { textureResolution = 64, padding = 1,
                dilationRadius = 0, bakeSamples = 1, bakeSourceAO = false, vertexColorTint = false }, CancellationToken.None);
        }

        static Mesh MeshFor(RemeshNative.Geometry target)
        {
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = target.positions; mesh.normals = target.normals; mesh.tangents = target.tangents;
            mesh.uv = target.uv; mesh.triangles = target.indices; mesh.RecalculateBounds(); return mesh;
        }

        static Material Lit(Shader shader, bool urp, Texture normal)
        {
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetColor(urp ? "_BaseColor" : "_Color", new Color(.8f, .8f, .8f, 1));
            material.SetFloat("_Metallic", 0); material.SetFloat(urp ? "_Smoothness" : "_Glossiness", 0);
            if (normal) { material.SetTexture("_BumpMap", normal); material.SetFloat("_BumpScale", 1); material.EnableKeyword("_NORMALMAP"); }
            return material;
        }

        static RemeshNormalFrame.Mode Mode(bool urp) => urp ? RemeshNormalFrame.Mode.Urp : RemeshNormalFrame.Mode.BuiltIn;
        static float Distance(Color a, Color b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));
        static void AssertAngle(Vector3 actual, Vector3 expected, float tolerance) => Assert.That(Vector3.Angle(actual, expected), Is.LessThan(tolerance), "actual=" + actual + "; expected=" + expected);

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Actual Lit frame tests need a graphics device; omit -nographics.");
        }

        // Shared with source-strength tests: +Z-facing meshes, surface-to-light
        // direction, independent actual-Lit draw and linear HDR readback.
        internal static Color RenderMean(Mesh mesh, Material material, Vector3 lightDirection, string captureName = null)
        {
            RequireGraphics();
            var utility = new PreviewRenderUtility();
            try {
                var camera = utility.camera;
                camera.transform.position = new Vector3(0, 0, 3); camera.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
                camera.orthographic = true; camera.orthographicSize = 1.2f; camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.allowHDR = true; camera.allowMSAA = false;
                utility.ambientColor = new Color(.025f, .025f, .025f, 1);
                foreach (var light in utility.lights) { light.type = LightType.Directional; light.color = Color.white; light.shadows = LightShadows.None; }
                utility.lights[0].intensity = 1.1f; utility.lights[0].transform.rotation = Quaternion.LookRotation(-lightDirection);
                utility.lights[1].intensity = .1f; utility.lights[1].transform.rotation = Quaternion.LookRotation(Vector3.back);
                utility.BeginPreview(new Rect(0, 0, 64, 64), GUIStyle.none);
                Texture image;
                try { utility.DrawMesh(mesh, Matrix4x4.identity, material, 0); utility.Render(true); }
                finally { image = utility.EndPreview(); }
                var previous = RenderTexture.active;
                var target = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                var pixels = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
                try {
                    Graphics.Blit(image, target); RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                    string output = Environment.GetEnvironmentVariable("MESHLAB_PREVIEW_RENDER_OUTPUT");
                    if (!string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(captureName)) {
                        Directory.CreateDirectory(output);
                        var png = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                        try {
                            var colors = pixels.GetPixels();
                            if (QualitySettings.activeColorSpace == ColorSpace.Linear) for (int i = 0; i < colors.Length; ++i) colors[i] = colors[i].gamma;
                            png.SetPixels(colors); png.Apply(); File.WriteAllBytes(Path.Combine(output, captureName + ".png"), png.EncodeToPNG());
                        }
                        finally { Object.DestroyImmediate(png); }
                    }
                    var sum = Color.clear;
                    for (int y = 24; y < 40; ++y) for (int x = 24; x < 40; ++x) sum += pixels.GetPixel(x, y);
                    return sum / 256;
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(pixels); }
            }
            finally { utility.Cleanup(); }
        }
    }
}
