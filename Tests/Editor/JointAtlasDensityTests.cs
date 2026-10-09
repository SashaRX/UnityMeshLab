using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class JointAtlasDensityTests
    {
        static readonly int[] QuadTriangles = { 0, 1, 2, 0, 2, 3 };

        [TestCase(0.75f, 0.75)]
        [TestCase(0f, 2.0)]
        public void SharedBudget_GivesDifferentSizedSurfacesTheSameDensity(float coverage, double totalUvArea)
        {
            var meshes = new[] { Quad(1), Quad(4) };
            try
            {
                var uv = meshes.Select(m => Flatten(m.uv)).ToArray();
                var tris = meshes.Select(m => m.triangles).ToArray();
                var positions = meshes.Select(m => m.vertices).ToArray();
                var shells = meshes.Select(m => UvShellExtractor.Extract(m.uv, m.triangles)).ToArray();
                TexelDensityNormalizer.NormalizeBatch(uv, shells, tris, positions, coverage);
                double smallArea = UvArea(uv[0], tris[0]), largeArea = UvArea(uv[1], tris[1]);
                Assert.That(smallArea + largeArea, Is.EqualTo(totalUvArea).Within(1e-6));
                Assert.That(largeArea / smallArea, Is.EqualTo(16).Within(1e-4));
                CollectionAssert.AreEqual(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }, meshes[0].uv);
            }
            finally { Destroy(meshes); }
        }

        [Test]
        public void SingleMeshBatch_MatchesSingleMeshNormalization()
        {
            var mesh = Quad(2);
            try
            {
                var separate = Flatten(mesh.uv);
                var batch = (float[])separate.Clone();
                var shells = UvShellExtractor.Extract(mesh.uv, mesh.triangles);
                TexelDensityNormalizer.Normalize(separate, shells, mesh.triangles, mesh.vertices);
                TexelDensityNormalizer.NormalizeBatch(new[] { batch }, new[] { shells },
                    new[] { mesh.triangles }, new[] { mesh.vertices });
                CollectionAssert.AreEqual(separate, batch);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void NativeJointAtlas_RespectsDensityOptionAndPreservesUv0(bool normalize)
        {
            RequireNative();
            var meshes = new[] { Quad(1), Quad(4) };
            var before = meshes.Select(m => m.uv).ToArray();
            try
            {
                var options = Options(); options.normalizeTexelDensity = normalize;
                Pack(meshes, options);
                double ratio = Density(meshes[0]) / Density(meshes[1]);
                Assert.That(ratio, Is.EqualTo(normalize ? 1d : 16d).Within(normalize ? .03 : .1));
                for (int m = 0; m < meshes.Length; ++m) CollectionAssert.AreEqual(before[m], meshes[m].uv);
            }
            finally { Destroy(meshes); }
        }

        // Optional local corpus supplied by prepare-transfer-benchmark.ps1.
        // Imported assets stay untouched; both paths pack owned mesh copies.
        [TestCase(false, false), TestCase(true, false), TestCase(true, true), Category("LocalAssetBenchmark")]
        public void CabinetJointAtlas_RecordsPerMeshAndSharedBudgetComparison(bool postCorrection, bool arap)
        {
            const string asset = "Assets/TransferBenchmarkInputs/Grandma_Cabinet_B_Destructed.fbx";
            var source = AssetDatabase.LoadAllAssetsAtPath(asset).OfType<Mesh>()
                .Where(m => m.name.Contains("_LOD0", StringComparison.Ordinal)).OrderBy(m => m.name).ToArray();
            if (source.Length == 0) Assert.Ignore("Local cabinet corpus is not installed.");
            RequireNative();
            var control = source.Select(m => Object.Instantiate(m)).ToArray();
            var shared = source.Select(m => Object.Instantiate(m)).ToArray();
            try
            {
                var options = Options();
                options.postPackDensityCorrection = postCorrection;
                foreach (var mesh in control.Concat(shared))
                {
                    var uv = mesh.uv;
                    for (int i = 0; i < uv.Length; ++i) uv[i] = Vector2.Scale(uv[i], new Vector2(Mathf.Sqrt(.5f), Mathf.Sqrt(2f)));
                    mesh.uv = uv;
                }
                foreach (var mesh in control)
                {
                    var uv = Flatten(mesh.uv);
                    TexelDensityNormalizer.Normalize(uv, UvShellExtractor.Extract(mesh.uv, mesh.triangles),
                        mesh.triangles, mesh.vertices, targetCoverage: options.targetUvCoverage);
                    mesh.uv = Enumerable.Range(0, mesh.vertexCount).Select(i => new Vector2(uv[i * 2], uv[i * 2 + 1])).ToArray();
                }
                options.normalizeTexelDensity = false;
                Pack(control, options);
                options.normalizeTexelDensity = true;
                options.reparameterizeStretchedShells = arap;
                Pack(shared, options);
                var rows = new List<string> { "mesh,control_median_uv_area_per_3d_area,shared_median_uv_area_per_3d_area,shared_arap,post_correction" };
                var before = control.Select(Density).ToArray();
                var after = shared.Select(Density).ToArray();
                for (int i = 0; i < source.Length; ++i)
                    rows.Add($"{source[i].name},{before[i].ToString("R", CultureInfo.InvariantCulture)},{after[i].ToString("R", CultureInfo.InvariantCulture)},{arap},{postCorrection}");
                string output = Path.GetFullPath(Path.Combine(Application.dataPath,
                    $"../BenchmarkReports/shared-atlas-density-{(postCorrection ? "corrected" : "raw")}-{(arap ? "arap" : "authored")}.csv"));
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllLines(output, rows);
                double oldSpread = Math.Sqrt(before.Max() / before.Min());
                double newSpread = Math.Sqrt(after.Max() / after.Min());
                UvtLog.Info($"[DensityBench] {source.Length} source meshes, linear density spread {oldSpread:F3}x -> {newSpread:F3}x; report: {output}");
                Assert.That(oldSpread, Is.GreaterThan(3d));
                // A uniform chart scale cannot equalise varying triangle densities
                // inside an unrelaxed authored chart. The native quad oracle above
                // checks the exact density contract; this corpus checks the large
                // cross-mesh regression while retaining those real source defects.
                Assert.That(newSpread, Is.LessThan(postCorrection ? 1.2d : 1.5d));
            }
            finally { Destroy(control); Destroy(shared); }
        }

        static RepackOptions Options()
        {
            var options = RepackOptions.Default;
            options.resolution = 512; options.padding = 2;
            options.reparameterizeStretchedShells = false;
            options.postPackDensityCorrection = false;
            return options;
        }

        static void RequireNative()
        {
            if (!XatlasRepack.TryAcquireNativeSession()) Assert.Ignore("xatlas native session is busy.");
            try { XatlasNative.xatlasCreate(); XatlasNative.xatlasDestroy(); }
            catch (DllNotFoundException) { Assert.Ignore("xatlas native plugin unavailable."); }
            catch (EntryPointNotFoundException) { Assert.Ignore("xatlas native entry points unavailable."); }
            finally { XatlasRepack.ReleaseNativeSession(); }
        }

        static void Pack(Mesh[] meshes, RepackOptions options)
        {
            var result = XatlasRepack.RepackMulti(meshes, options);
            for (int m = 0; m < meshes.Length; ++m)
            {
                Assert.IsTrue(result[m].ok, result[m].error);
                SourceTextureUvMetric.NormalizePackedLightmap(meshes[m], result[m].atlasWidth, result[m].atlasHeight);
            }
        }

        static Mesh Quad(float size)
        {
            return new Mesh
            {
                name = "DensityQuad" + size.ToString(CultureInfo.InvariantCulture),
                vertices = new[] { Vector3.zero, Vector3.right * size, (Vector3.right + Vector3.up) * size, Vector3.up * size },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
                triangles = QuadTriangles
            };
        }

        static float[] Flatten(Vector2[] uv) => uv.SelectMany(p => new[] { p.x, p.y }).ToArray();

        static double UvArea(float[] uv, int[] tris)
        {
            double area = 0;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t] * 2, b = tris[t + 1] * 2, c = tris[t + 2] * 2;
                area += Math.Abs((uv[b] - uv[a]) * (uv[c + 1] - uv[a + 1]) - (uv[c] - uv[a]) * (uv[b + 1] - uv[a + 1])) * .5;
            }
            return area;
        }

        static double Density(Mesh mesh)
        {
            var ratios = new List<double>();
            var uv = mesh.uv2; var p = mesh.vertices; var tris = mesh.triangles;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                double world = Vector3.Cross(p[b] - p[a], p[c] - p[a]).magnitude * .5;
                double area = Math.Abs((uv[b].x - uv[a].x) * (uv[c].y - uv[a].y) - (uv[c].x - uv[a].x) * (uv[b].y - uv[a].y)) * .5;
                if (world > 1e-12 && area > 1e-12) ratios.Add(area / world);
            }
            ratios.Sort();
            Assert.IsNotEmpty(ratios, mesh.name);
            return ratios[ratios.Count / 2];
        }

        static void Destroy(IEnumerable<Mesh> meshes)
        {
            foreach (var mesh in meshes) if (mesh) Object.DestroyImmediate(mesh);
        }
    }
}
