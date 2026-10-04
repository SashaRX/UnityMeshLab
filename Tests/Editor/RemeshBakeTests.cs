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

        [Test]
        public void RecommendedXatlasSettings_PreserveOtherStagesAndExplicitSavedSettings()
        {
            var settings = new RemeshSettings {
                voxelResolution = 64, maximumError = .004f, hardEdges = RemeshHardEdges.Smooth,
                textureResolution = 512, padding = 2, reduceUvFragmentation = false, mergeCharts = true,
                chartMaxCost = 10, chartNormalDeviation = 7.1f, chartRoundness = .73f,
                chartStraightness = 20, chartNormalSeam = 251, chartIterations = 16,
                maxChartArea = 2, maxChartBoundary = 3, packBruteForce = true, packRotate = false, packBlockAlign = true,
                projectionDistance = .01f, bakeSamples = 16
            };
            // Upgrading defaults must not replace manually saved chart settings.
            string saved = JsonUtility.ToJson(settings);
            Assert.AreEqual(saved, JsonUtility.ToJson(RemeshSettings.FromSavedJson(saved)));
            var unaffected = new[] { RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Simplify, RemeshPipeline.Stage.Bake };
            var keys = Array.ConvertAll(unaffected, stage => RemeshPipeline.Key(stage, settings, null));
            string unwrap = RemeshPipeline.Key(RemeshPipeline.Stage.Unwrap, settings, null);
            settings.ApplyDefaultXatlasSettings();
            for (int i = 0; i < unaffected.Length; ++i)
                Assert.AreEqual(keys[i], RemeshPipeline.Key(unaffected[i], settings, null));
            Assert.AreNotEqual(unwrap, RemeshPipeline.Key(RemeshPipeline.Stage.Unwrap, settings, null));
            Assert.AreEqual(RemeshHardEdges.Smooth, settings.hardEdges);
            Assert.AreEqual(512, settings.textureResolution); Assert.AreEqual(2, settings.padding);
            Assert.IsFalse(settings.reduceUvFragmentation); Assert.IsTrue(settings.mergeCharts);
            var defaults = new RemeshSettings();
            Assert.AreEqual(defaults.chartMaxCost, settings.chartMaxCost);
            Assert.AreEqual(defaults.chartNormalDeviation, settings.chartNormalDeviation);
            Assert.AreEqual(defaults.chartRoundness, settings.chartRoundness);
            Assert.AreEqual(defaults.chartStraightness, settings.chartStraightness);
            Assert.AreEqual(defaults.chartNormalSeam, settings.chartNormalSeam);
            Assert.AreEqual(defaults.chartIterations, settings.chartIterations);
            Assert.AreEqual(0, settings.maxChartArea); Assert.AreEqual(0, settings.maxChartBoundary);
            Assert.AreEqual(defaults.packRotate, settings.packRotate);
            Assert.AreEqual(defaults.packBruteForce, settings.packBruteForce);
            Assert.AreEqual(defaults.packBlockAlign, settings.packBlockAlign);
        }

        [Test]
        public void FragmentationSettingMigratesWithoutReplacingManualChartSettings()
        {
            var settings = new RemeshSettings { chartMaxCost = 9, chartIterations = 7, reduceUvFragmentation = false };
            string json = JsonUtility.ToJson(settings);
            Assert.IsFalse(RemeshSettings.FromSavedJson(json).reduceUvFragmentation);
            string legacy = json.Replace("\"reduceUvFragmentation\":false,", "");
            Assert.IsFalse(legacy.Contains("\"reduceUvFragmentation\""));
            var restored = RemeshSettings.FromSavedJson(legacy);
            Assert.IsTrue(restored.reduceUvFragmentation);
            Assert.AreEqual(9, restored.chartMaxCost); Assert.AreEqual(7, restored.chartIterations);
            foreach (RemeshPipeline.Stage stage in new[] { RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Simplify,
                RemeshPipeline.Stage.Unwrap, RemeshPipeline.Stage.Bake }) {
                string before = RemeshPipeline.Key(stage, settings, null);
                settings.reduceUvFragmentation = true;
                Assert.AreEqual(stage == RemeshPipeline.Stage.Unwrap, before != RemeshPipeline.Key(stage, settings, null), stage.ToString());
                settings.reduceUvFragmentation = false;
            }
        }

        [TestCase(.000001f)]
        [TestCase(1f)]
        [TestCase(1000000f)]
        public void ChartStretchIsScaleIndependentAndRejectsCollapsedOrFlippedFaces(float scale)
        {
            var geometry = new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right * scale, Vector3.one * scale, Vector3.up * scale },
                uv = new[] { Vector2.zero, new Vector2(2, 0), new Vector2(2, 1), Vector2.up },
                indices = new[] { 0, 1, 3 }, charts = new int[4], chartCount = 1,
            };
            var quality = UvChartQuality.Measure(geometry, CancellationToken.None);
            Assert.IsTrue(quality.valid); Assert.AreEqual(1, quality.smallCharts);
            Assert.That(quality.meanStretch, Is.EqualTo(2).Within(1e-5));
            Assert.That(quality.maxStretch, Is.EqualTo(2).Within(1e-5));
            geometry.uv[3] = Vector2.right;
            Assert.IsFalse(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
            geometry.uv[3] = Vector2.up;
            geometry.indices = new[] { 0, 1, 3, 3, 1, 0 };
            Assert.IsFalse(UvChartQuality.Measure(geometry, CancellationToken.None).valid);
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => UvChartQuality.Measure(geometry, cancellation.Token));
            }
        }

        [Test]
        public void FewerChartsCannotTradeForMoreSmallFragmentsOrExcessiveStretch()
        {
            var original = new UvChartQuality(10, 5, 1, 2, true);
            Assert.IsTrue(new UvChartQuality(9, 4, 1.14, 3.9, true).Improves(original, original));
            Assert.IsTrue(new UvChartQuality(10, 4, 1, 2, true).Improves(original, original));
            Assert.IsFalse(new UvChartQuality(9, 6, 1, 2, true).Improves(original, original));
            Assert.IsFalse(new UvChartQuality(11, 4, 1, 2, true).Improves(original, original));
            Assert.IsFalse(original.Improves(original, original));
            Assert.IsFalse(new UvChartQuality(9, 4, 1.16, 2, true).Improves(original, original));
            Assert.IsFalse(new UvChartQuality(9, 4, 1, 4.01, true).Improves(original, original));
            Assert.IsFalse(new UvChartQuality(9, 4, 1, 2, false).Improves(original, original));
        }

        [TestCase(RemeshHardEdges.Smooth)]
        [TestCase(RemeshHardEdges.UvIslands)]
        public void NativeFragmentationSearchReducesCurvedSurfaceChartsWithoutChangingTriangles(RemeshHardEdges hardEdges)
        {
            RequireRemeshNative();
            const int cells = 12, stride = cells + 1;
            var p = new Vector3[stride * stride]; var t = new int[cells * cells * 6];
            for (int y = 0; y <= cells; ++y)
                for (int x = 0; x <= cells; ++x) {
                    float u = x / (float)cells, v = y / (float)cells;
                    p[y * stride + x] = new Vector3(u, .2f * Mathf.Sin(u * Mathf.PI * 2) * Mathf.Sin(v * Mathf.PI * 2), v);
                }
            for (int y = 0; y < cells; ++y)
                for (int x = 0; x < cells; ++x) {
                    int a = y * stride + x, f = (y * cells + x) * 6;
                    t[f] = a; t[f + 1] = a + stride; t[f + 2] = a + 1;
                    t[f + 3] = a + 1; t[f + 4] = a + stride; t[f + 5] = a + stride + 1;
                }
            var input = new RemeshNative.IndexedMesh { positions = p, indices = t };
            var settings = new RemeshSettings { hardEdges = hardEdges, textureResolution = 512, chartMaxCost = .1f,
                chartStraightness = 0, reduceUvFragmentation = false };
            string configured = JsonUtility.ToJson(settings);
            var original = RemeshNative.Unwrap(input, settings, CancellationToken.None);
            settings.reduceUvFragmentation = true;
            var result = RemeshNative.Unwrap(input, settings, CancellationToken.None);
            Assert.Less(result.chartCount, original.chartCount);
            Assert.LessOrEqual(result.smallChartCount, original.smallChartCount);
            Assert.AreEqual(original.chartCount, result.originalChartCount);
            Assert.AreEqual(t.Length, result.indices.Length);
            var before = UvChartQuality.Measure(original, CancellationToken.None);
            Assert.IsTrue(UvChartQuality.Measure(result, CancellationToken.None).Improves(before, before));
            for (int i = 0; i < t.Length; ++i)
                Assert.AreEqual(original.positions[original.indices[i]], result.positions[result.indices[i]], "Every source corner stays in place");
            foreach (var uv in result.uv) { Assert.That(uv.x, Is.InRange(0f, 1f)); Assert.That(uv.y, Is.InRange(0f, 1f)); }
            var mesh = new Mesh();
            try {
                mesh.vertices = result.positions; mesh.triangles = result.indices; mesh.uv = result.uv;
                var report = TransferValidator.Validate(mesh, result.uv);
                TransferValidator.DetectUv2Overlaps(mesh, result.uv, report);
                Assert.AreEqual(0, report.zeroAreaCount); Assert.AreEqual(0, report.oobCount);
                Assert.AreEqual(0, report.overlapShellPairs + report.overlapSameSrcPairs);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
            settings.reduceUvFragmentation = false;
            Assert.AreEqual(configured, JsonUtility.ToJson(settings), "The search must not overwrite manual options");
        }

        static void RequireRemeshNative()
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException error) when (error.InnerException is DllNotFoundException || error.InnerException is EntryPointNotFoundException || error.InnerException is BadImageFormatException) {
                Assert.Ignore("Remesh native plugin unavailable: " + error.Message);
            }
        }

        [Test]
        public void NativeFragmentationSearchKeepsAtlasWhenFinalCreasesChange()
        {
            RequireRemeshNative();
            float bend = 20 * Mathf.Deg2Rad;
            var fold = new RemeshNative.IndexedMesh {
                positions = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up,
                    new Vector3(0, -Mathf.Cos(bend), Mathf.Sin(bend)), new Vector3(1, -Mathf.Cos(bend), Mathf.Sin(bend)) },
                indices = new[] { 0, 1, 2, 0, 2, 3, 1, 0, 4, 1, 4, 5 },
            };
            var settings = new RemeshSettings { hardEdges = RemeshHardEdges.Angle, normalCrease = 10,
                normalSmoothing = 0, chartNormalSeam = 1000, chartMaxCost = .1f, textureResolution = 512 };
            settings.hardEdges = RemeshHardEdges.Smooth;
            var smooth = RemeshNative.Unwrap(fold, settings, CancellationToken.None);
            settings.hardEdges = RemeshHardEdges.Angle;
            var result = RemeshNative.Unwrap(fold, settings, CancellationToken.None);
            Assert.AreEqual(smooth.chartCount, result.chartCount, "Final crease normals must not add atlas seams");
            for (int c = 0; c < result.indices.Length; ++c) {
                int a = smooth.indices[c], b = result.indices[c];
                Assert.AreEqual(smooth.positions[a], result.positions[b]);
                Assert.AreEqual(smooth.uv[a], result.uv[b]);
                Assert.AreEqual(smooth.charts[a], result.charts[b]);
            }
            int shared = -1;
            for (int i = 0; i < result.positions.Length; ++i) {
                if (result.positions[i] != Vector3.zero) continue;
                if (shared < 0) { shared = i; continue; }
                if (Vector3.Dot(result.normals[shared], result.normals[i]) < Mathf.Cos(10 * Mathf.Deg2Rad)) {
                    Assert.Less(Vector3.Dot(result.normals[shared], result.normals[i]), Mathf.Cos(10 * Mathf.Deg2Rad));
                    shared = -2; break;
                }
            }
            Assert.AreEqual(-2, shared, "The final mesh must keep both crease-normal groups without requiring a UV seam");
            var plane = new RemeshNative.IndexedMesh {
                positions = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up },
                indices = new[] { 0, 1, 2, 0, 2, 3 },
            };
            settings.reduceUvFragmentation = false;
            var manual = RemeshNative.Unwrap(plane, settings, CancellationToken.None);
            settings.reduceUvFragmentation = true;
            var compact = RemeshNative.Unwrap(plane, settings, CancellationToken.None);
            Assert.AreEqual(1, compact.chartCount);
            CollectionAssert.AreEqual(manual.uv, compact.uv); CollectionAssert.AreEqual(manual.indices, compact.indices);
            CollectionAssert.AreEqual(manual.normals, compact.normals); CollectionAssert.AreEqual(manual.tangents, compact.tangents);
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(16)]
        [TestCase(31)]
        public void DilationNearestPixelsMatchExactDistanceIncludingTies(int size)
        {
            var random = new System.Random(1729 + size);
            for (int pass = 0; pass < 12; ++pass) {
                var filled = new int[size * size]; var seeds = new System.Collections.Generic.List<int>();
                for (int i = 0; i < filled.Length; ++i) {
                    bool seed = pass == 1 || pass > 1 && random.Next(5) == 0;
                    filled[i] = seed ? i : -1;
                    if (seed) seeds.Add(i);
                }
                var nearest = TextureDilation.NearestFilled(filled, size, CancellationToken.None);
                for (int i = 0; i < nearest.Length; ++i) {
                    int expected = -1; long best = long.MaxValue;
                    foreach (int seed in seeds) {
                        long dx = i % size - seed % size, dy = i / size - seed / size;
                        long distance = dx * dx + dy * dy;
                        if (distance < best) { best = distance; expected = seed; }
                    }
                    Assert.AreEqual(expected, nearest[i], $"size={size}, pass={pass}, pixel={i}");
                }
            }
        }

        static RemeshBaker.Maps EmptyDilationMaps(int size) => new RemeshBaker.Maps {
            size = size, color = new Color32[size * size], normal = new Color32[size * size],
            metal = new Color32[size * size], ao = new Color32[size * size], emission = new Color[size * size] };

        static void DilationSeed(RemeshBaker.Maps maps, int i, byte red, byte alpha)
        {
            maps.color[i] = new Color32(red, 70, 220, alpha);
            maps.normal[i] = new Color32(red, 144, 230, 255);
            maps.metal[i] = new Color32(191, 22, 31, 83);
            maps.ao[i] = new Color32(41, 41, 41, 255);
            maps.emission[i] = new Color(8, 2, .5f, .25f);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DilationAddsCircularRadiusAfterPaddingAndPreservesEveryChannel(bool transparent)
        {
            const int size = 13, center = 6 * size + 6;
            var maps = EmptyDilationMaps(size); maps.covered = 1; maps.misses = 2;
            DilationSeed(maps, center, 207, transparent ? (byte)0 : (byte)193);
            var owners = new int[size * size]; Array.Fill(owners, -1); owners[center] = 0;
            var originalOwners = (int[])owners.Clone();
            RemeshBaker.PadAndDilate(maps, owners, 1, 2, CancellationToken.None);
            for (int y = 0; y < size; ++y)
                for (int x = 0; x < size; ++x) {
                    int i = y * size + x;
                    int dx = Math.Max(0, Math.Abs(x - 6) - 1), dy = Math.Max(0, Math.Abs(y - 6) - 1);
                    bool extended = dx * dx + dy * dy <= 4;
                    Assert.AreEqual(extended ? maps.color[center] : default, maps.color[i], "color " + i);
                    Assert.AreEqual(extended ? maps.normal[center] : default, maps.normal[i], "normal " + i);
                    Assert.AreEqual(extended ? maps.metal[center] : default, maps.metal[i], "packed channels " + i);
                    Assert.AreEqual(extended ? maps.ao[center] : default, maps.ao[i], "AO " + i);
                    Assert.AreEqual(extended ? maps.emission[center] : default, maps.emission[i], "HDR " + i);
                }
            CollectionAssert.AreEqual(originalOwners, owners, "The pass does not change UV coverage or chart ownership");
            Assert.AreEqual(1, maps.covered); Assert.AreEqual(2, maps.misses);
            Assert.AreEqual(transparent ? 0 : 193, maps.color[center].a);
            Assert.AreEqual(8f, maps.emission[center].r);
        }

        [Test]
        public void DilationCopiesNearestIslandWithoutBlendingAndCanBeDisabled()
        {
            const int size = 17, left = 8 * size + 4, right = 8 * size + 12;
            var maps = EmptyDilationMaps(size);
            DilationSeed(maps, left, 35, 255); DilationSeed(maps, right, 220, 255);
            var owners = new int[size * size]; Array.Fill(owners, -1); owners[left] = 0; owners[right] = 1;
            RemeshBaker.PadAndDilate(maps, owners, 1, 3, CancellationToken.None);
            Assert.AreEqual(maps.normal[left], maps.normal[8 * size + 7]);
            Assert.AreEqual(maps.normal[left], maps.normal[8 * size + 8], "Equidistant borders use a stable seed");
            Assert.AreEqual(maps.normal[right], maps.normal[8 * size + 9]);
            Assert.AreEqual(maps.color[left], maps.color[8 * size + 8]);
            var paddingOnly = EmptyDilationMaps(size); DilationSeed(paddingOnly, left, 35, 255);
            owners[right] = -1;
            RemeshBaker.PadAndDilate(paddingOnly, owners, 1, 0, CancellationToken.None);
            Assert.AreEqual(paddingOnly.normal[left], paddingOnly.normal[8 * size + 5]);
            Assert.AreEqual(default(Color32), paddingOnly.normal[8 * size + 6]);
        }

        [Test]
        public void DilationSettingsPersistMigrateAndOnlyInvalidateBake()
        {
            var settings = new RemeshSettings { padding = 4, textureResolution = 1024 };
            Assert.AreEqual(64, settings.dilationRadius);
            string legacy = JsonUtility.ToJson(settings).Replace("\"dilationRadius\":64,", "");
            Assert.IsFalse(legacy.Contains("\"dilationRadius\""));
            var restored = RemeshSettings.FromSavedJson(legacy);
            Assert.AreEqual(64, restored.dilationRadius); Assert.AreEqual(4, restored.padding); Assert.AreEqual(1024, restored.textureResolution);
            foreach (RemeshPipeline.Stage stage in new[] { RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Simplify,
                RemeshPipeline.Stage.Unwrap, RemeshPipeline.Stage.Bake }) {
                string before = RemeshPipeline.Key(stage, settings, null);
                settings.dilationRadius = 128;
                string after = RemeshPipeline.Key(stage, settings, null);
                Assert.AreEqual(stage == RemeshPipeline.Stage.Bake, before != after, stage.ToString());
                settings.dilationRadius = 64;
            }
            settings.dilationRadius = 0;
            Assert.AreEqual(0, RemeshSettings.FromSavedJson(JsonUtility.ToJson(settings)).dilationRadius);
            Assert.DoesNotThrow(settings.Validate);
            settings.dilationRadius = -1; Assert.Throws<ArgumentException>(settings.Validate);
            settings.dilationRadius = RemeshSettings.MaxDilationRadius + 1; Assert.Throws<ArgumentException>(settings.Validate);
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => TextureDilation.NearestFilled(new[] { 0 }, 1, cancellation.Token));
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator CpuAndGpuBakesApplyDilationToExistingUvAo()
        {
            var source = Source();
            var target = new RemeshNative.Geometry { positions = source.positions, normals = source.normals,
                indices = source.indices, tangents = source.tangents,
                uv = new[] { new Vector2(8.1f / 64, 8.1f / 64), new Vector2(8.9f / 64, 8.1f / 64), new Vector2(8.1f / 64, 8.9f / 64) } };
            var settings = new RemeshSettings { textureResolution = 64, bakeSamples = 1, padding = 1,
                dilationRadius = 0, bakeSourceAO = true, gpuProjection = false };
            settings.sourceAO.groundPlane = false; settings.sourceAO.samples = 16;
            var padding = RemeshBaker.Bake(source, target, source.tangents, settings, CancellationToken.None);
            Assert.AreEqual(default(Color32), padding.ao[11 + 8 * 64]);
            settings.dilationRadius = 3;
            var cpu = TextureAoBakePanel.BakeMaps(source, target, settings, CancellationToken.None);
            double deadline = EditorApplication.timeSinceStartup + 10;
            while (!cpu.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsTrue(cpu.IsCompleted);
            Assert.IsFalse(cpu.IsFaulted, cpu.Exception?.ToString());
            Assert.AreEqual(cpu.Result.ao[8 + 8 * 64], cpu.Result.ao[11 + 8 * 64]);
            Assert.AreEqual(padding.covered, cpu.Result.covered); Assert.AreEqual(padding.misses, cpu.Result.misses);
            if (!SourceAoBaker.GpuSupported) yield break;
            settings.gpuProjection = true;
            var gpu = TextureAoBakePanel.BakeMaps(source, target, settings, CancellationToken.None);
            while (!gpu.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsTrue(gpu.IsCompleted);
            Assert.IsFalse(gpu.IsFaulted, gpu.Exception?.ToString());
            Assert.IsTrue(gpu.Result.gpu); Assert.IsTrue(gpu.Result.gpuAO);
            CollectionAssert.AreEqual(cpu.Result.ao, gpu.Result.ao);
            CollectionAssert.AreEqual(cpu.Result.normal, gpu.Result.normal);
            CollectionAssert.AreEqual(cpu.Result.color, gpu.Result.color);
            CollectionAssert.AreEqual(cpu.Result.metal, gpu.Result.metal);
            CollectionAssert.AreEqual(cpu.Result.emission, gpu.Result.emission);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator GpuQueriesAsyncPreserveBatchOffsetsAndDrainCancellation()
        {
            if (!GpuBvh.Supported || !SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Async GPU queries unavailable on this device.");
            var source = Source(); var bvh = new TriangleBvh(source.positions, source.indices);
            using (var gpu = GpuBvh.TryCreate(bvh, new[] { Vector3.forward })) {
                Assert.IsNotNull(gpu);
                const int count = 16401; // crosses the asynchronous dispatch boundary
                var origins = new Vector4[count]; var directions = new Vector4[count];
                var points = new Vector4[count]; var normals = new Vector4[count];
                var hits = new GpuBvh.RayHit[count]; var nearest = new GpuBvh.NearestHit[count];
                for (int i = 0; i < count; ++i) {
                    origins[i] = i % 2 == 0 ? new Vector4(.2f, .2f, 1, 2) : new Vector4(2, 2, 1, 2);
                    directions[i] = (Vector4)Vector3.back;
                    points[i] = i % 2 == 0 ? new Vector4(.2f, .2f, .5f, 1) : new Vector4(8, 8, .5f, .1f);
                    normals[i] = (Vector4)Vector3.forward;
                }
                var rays = gpu.RaycastAsync(origins, directions, count, true, hits, CancellationToken.None);
                Assert.IsFalse(rays.IsCompleted, "GPU results are awaited instead of synchronously read back");
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!rays.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(rays.IsCompleted); Assert.IsFalse(rays.IsFaulted, rays.Exception?.ToString());
                var near = gpu.NearestAsync(points, normals, count, true, nearest, CancellationToken.None);
                while (!near.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(near.IsCompleted); Assert.IsFalse(near.IsFaulted, near.Exception?.ToString());
                for (int i = 0; i < count; ++i) {
                    Assert.AreEqual(i % 2 == 0 ? 0 : -1, hits[i].tri, "ray " + i);
                    Assert.AreEqual(i % 2 == 0 ? 0 : -1, nearest[i].tri, "nearest " + i);
                }
                foreach (int i in new[] { 0, 16382, 16384, 16400 }) {
                    Assert.That(hits[i].t, Is.EqualTo(1f).Within(1e-5f));
                    Assert.That(nearest[i].distSq, Is.EqualTo(.25f).Within(1e-5f));
                }
                using (var cancellation = new CancellationTokenSource()) {
                    var cancelled = gpu.RaycastAsync(origins, directions, count, false, hits, cancellation.Token);
                    cancellation.Cancel();
                    while (!cancelled.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.IsTrue(cancelled.IsCanceled, "Cancellation waits until the submitted GPU readback has drained");
                }
                var reused = gpu.RaycastAsync(origins, directions, 1, false, hits, CancellationToken.None);
                while (!reused.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(reused.IsCompleted); Assert.IsFalse(reused.IsFaulted, reused.Exception?.ToString());
                Assert.AreEqual(0, hits[0].tri, "The same buffers remain usable after cancellation");
            }
        }


        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator AsyncRemeshCapturePreservesByteMapsAndHdrEmission()
        {
            if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Graphics device has no async readback.");
            var root = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var material = new Material(Shader.Find("Standard"));
            var color = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var emission = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            try {
                color.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white }); color.Apply();
                emission.SetPixels(new[] { new Color(4, 2, 1, 1), Color.black, Color.white, new Color(8, 1, 2, 1) }); emission.Apply();
                material.mainTexture = color; material.color = Color.white;
                material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", Color.white);
                material.SetTexture("_EmissionMap", emission);
                var renderer = root.GetComponent<Renderer>(); renderer.sharedMaterial = material;
                var expected = RemeshSource.Capture(Matrix4x4.identity, new[] { renderer });
                var captured = RemeshSource.Capture(Matrix4x4.identity, new[] { renderer }, asyncTextures: true);
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!captured.TextureReadbacks.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(captured.TextureReadbacks.IsCompleted);
                Assert.IsFalse(captured.TextureReadbacks.IsFaulted, captured.TextureReadbacks.Exception?.ToString());
                var actual = captured.materials[0]; var original = expected.materials[0];
                CollectionAssert.AreEqual(original.color.image.pixels, actual.color.image.pixels);
                Assert.IsFalse(actual.emission.image.srgb);
                for (int i = 0; i < original.emission.image.hdrPixels.Length; ++i) {
                    Assert.That(actual.emission.image.hdrPixels[i].r, Is.EqualTo(original.emission.image.hdrPixels[i].r).Within(1e-5f));
                    Assert.That(actual.emission.image.hdrPixels[i].g, Is.EqualTo(original.emission.image.hdrPixels[i].g).Within(1e-5f));
                    Assert.That(actual.emission.image.hdrPixels[i].b, Is.EqualTo(original.emission.image.hdrPixels[i].b).Within(1e-5f));
                }
                Assert.That(actual.emission.image.hdrPixels[0].r, Is.GreaterThan(1f));
            }
            finally {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(color); UnityEngine.Object.DestroyImmediate(emission);
            }
        }

        [TestCase(1f, false, false)]
        [TestCase(1f, true, false)]
        [TestCase(1f, true, true)]
        [TestCase(0.0001f, false, false)]
        [TestCase(0.0001f, true, false)]
        [TestCase(0.0001f, true, true)]
        public void GpuBvhMatchesCpuHitsMissesAndFilters(float scale, bool filtered, bool twoSided)
        {
            if (!GpuBvh.Supported) Assert.Ignore("Compute shaders unavailable on this device.");
            // Enough separated faces to exercise internal nodes and both child orders.
            var vertices = new Vector3[48]; var indices = new int[48];
            var normals = new Vector3[16]; var either = new bool[16];
            for (int f = 0; f < 16; ++f) {
                var origin = new Vector3((f % 4) * 2, (f / 4) * 2, 0) * scale;
                vertices[f * 3] = origin;
                vertices[f * 3 + 1] = origin + Vector3.right * scale;
                vertices[f * 3 + 2] = origin + Vector3.up * scale;
                for (int j = 0; j < 3; ++j) indices[f * 3 + j] = f * 3 + j;
                normals[f] = Vector3.forward; either[f] = twoSided;
            }
            var bvh = new TriangleBvh(vertices, indices);
            using (var gpu = GpuBvh.TryCreate(bvh, normals, either)) {
                Assert.IsNotNull(gpu, "GPU query kernels must compile and be supported.");
                var origins = new[] {
                    new Vector4(.25f, .25f, 1, 2) * scale,
                    new Vector4(6.25f, 6.25f, 1, 2) * scale,
                    new Vector4(.25f, .25f, -1, 2) * scale,
                    new Vector4(10, 10, 1, 2) * scale,
                    new Vector4(.25f, .25f, 1, 0) * scale };
                var dirs = new[] { (Vector4)Vector3.back, (Vector4)Vector3.back,
                    (Vector4)Vector3.forward, (Vector4)Vector3.back, (Vector4)Vector3.back };
                var hits = new GpuBvh.RayHit[origins.Length];
                gpu.Raycast(origins, dirs, hits.Length, filtered, hits);
                for (int i = 0; i < hits.Length; ++i) {
                    var o = origins[i];
                    var cpu = filtered ? bvh.RaycastFacingFiltered(o, dirs[i], o.w, normals, either)
                        : bvh.Raycast(o, dirs[i], o.w);
                    Assert.AreEqual(cpu.triangleIndex, hits[i].tri, "ray " + i);
                    if (hits[i].tri < 0) continue;
                    Assert.That(hits[i].t, Is.EqualTo(cpu.t).Within(scale * 1e-4f));
                    Assert.That(hits[i].u, Is.EqualTo(cpu.barycentric.y).Within(1e-4f));
                    Assert.That(hits[i].v, Is.EqualTo(cpu.barycentric.z).Within(1e-4f));
                }
                var points = new[] {
                    new Vector4(.25f, .25f, .25f, .5f) * scale,
                    new Vector4(6.25f, 6.25f, .25f, .5f) * scale,
                    new Vector4(.25f, .25f, -.25f, .5f) * scale,
                    new Vector4(10, 10, 1, .5f) * scale,
                    new Vector4(.25f, .25f, .25f, 0) * scale };
                var queryNormals = new[] { (Vector4)Vector3.forward, (Vector4)Vector3.forward,
                    (Vector4)Vector3.back, (Vector4)Vector3.forward, (Vector4)Vector3.forward };
                var nearest = new GpuBvh.NearestHit[points.Length];
                gpu.Nearest(points, queryNormals, nearest.Length, filtered, nearest);
                for (int i = 0; i < nearest.Length; ++i) {
                    var q = points[i];
                    if (q.w == 0) { Assert.AreEqual(-1, nearest[i].tri, "skipped query"); continue; }
                    var cpu = filtered ? bvh.FindNearestNormalFiltered(q, queryNormals[i], normals, 0f, q.w, either)
                        : bvh.FindNearest(q, q.w);
                    Assert.AreEqual(cpu.triangleIndex, nearest[i].tri, "nearest " + i);
                    if (nearest[i].tri < 0) continue;
                    Assert.That(nearest[i].distSq, Is.EqualTo(cpu.distSq).Within(scale * scale * 1e-4f));
                    Assert.That((nearest[i].point - cpu.point).magnitude, Is.LessThan(scale * 1e-4f));
                    Assert.That((nearest[i].bary - cpu.barycentric).magnitude, Is.LessThan(1e-4f));
                }
            }
        }

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
            Assert.Throws<ArgumentException>(() => new RemeshSettings { hullResolution = 2 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { minPartSize = 0.6f }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { minRodVoxels = float.NaN }.Validate());
            Assert.DoesNotThrow(() => new RemeshSettings { targetTriangles = 0 }.Validate());
        }
        [Test]
        public void BarycentricSupportsBothWindings()
        {
            Assert.IsTrue(MeshGeometry.Barycentric(new Vector2(0.2f,0.3f), Vector2.zero, Vector2.right, Vector2.up, out var a));
            Assert.IsTrue(MeshGeometry.Barycentric(new Vector2(0.2f,0.3f), Vector2.zero, Vector2.up, Vector2.right, out var b));
            Assert.That(a.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(a.y, Is.EqualTo(b.z).Within(1e-5f));
            Assert.IsFalse(MeshGeometry.Barycentric(Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, out _));
        }
        [Test]
        public void BvhRaycastHitsMillimetreScaleTriangles()
        {
            // Edges of 1e-4: the old absolute parallel epsilon rejected det ≈ 1e-8 and the
            // ray passed straight through. The test is relative to the triangle now.
            float s = 1e-4f;
            var p = new[] { new Vector3(0,0,0), new Vector3(s,0,0), new Vector3(0,s,0) };
            var bvh = new TriangleBvh(p, new[] { 0,1,2 });
            var hit = bvh.Raycast(new Vector3(s*0.25f, s*0.25f, 1f), Vector3.back, 2f);
            Assert.AreEqual(0, hit.triangleIndex);
            Assert.That(hit.t, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(hit.barycentric.x + hit.barycentric.y + hit.barycentric.z, Is.EqualTo(1f).Within(1e-4f));
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(s*2f, s*2f, 1f), Vector3.back, 2f).triangleIndex, "beside the triangle");
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(s*0.25f, s*0.25f, 1f), Vector3.right, 2f).triangleIndex, "parallel to it");
        }
        [Test]
        public void BvhRaysAreWatertightAcrossSharedEdgesAndVertices()
        {
            // A 4×4 grid of quads split into triangles, all in z = 0; rays cast exactly
            // through every shared vertex and through points along every shared edge
            // must all hit (Möller–Trumbore can leak a ray between two triangles there).
            const int n = 4; var p = new System.Collections.Generic.List<Vector3>(); var idx = new System.Collections.Generic.List<int>();
            for (int y = 0; y <= n; ++y) for (int x = 0; x <= n; ++x) p.Add(new Vector3(x * 0.37f, y * 0.53f, 0));
            for (int y = 0; y < n; ++y) for (int x = 0; x < n; ++x) {
                int a = y * (n + 1) + x; idx.AddRange(new[] { a, a + 1, a + n + 2, a, a + n + 2, a + n + 1 });
            }
            var bvh = new TriangleBvh(p.ToArray(), idx.ToArray());
            var dirs = new[] { Vector3.back, new Vector3(0.3f, -0.2f, -1f).normalized, new Vector3(-0.7f, 0.4f, 1f).normalized };
            int tested = 0;
            foreach (var d in dirs) {
                // Shared vertices.
                for (int y = 1; y < n; ++y) for (int x = 1; x < n; ++x) {
                    var target = new Vector3(x * 0.37f, y * 0.53f, 0);
                    var hit = bvh.Raycast(target - d * 2f, d, 5f);
                    Assert.GreaterOrEqual(hit.triangleIndex, 0, $"vertex {x},{y} along {d}"); ++tested;
                }
                // Points along shared edges (horizontal, vertical and diagonal).
                for (int i = 1; i < 7; ++i) {
                    float s = i / 7f;
                    foreach (var target in new[] { new Vector3((1 + s) * 0.37f, 2 * 0.53f, 0), new Vector3(2 * 0.37f, (1 + s) * 0.53f, 0), new Vector3((1 + s) * 0.37f, (1 + s) * 0.53f, 0) }) {
                        var hit = bvh.Raycast(target - d * 2f, d, 5f);
                        Assert.GreaterOrEqual(hit.triangleIndex, 0, $"edge point {target} along {d}"); ++tested;
                        Assert.That(hit.t, Is.EqualTo(2f).Within(1e-4f));
                        var w = hit.barycentric; int f = hit.triangleIndex;
                        var at = p[idx[f * 3]] * w.x + p[idx[f * 3 + 1]] * w.y + p[idx[f * 3 + 2]] * w.z;
                        Assert.That((at - target).magnitude, Is.LessThan(1e-4f), "barycentric weights locate the hit");
                    }
                }
            }
            Assert.Greater(tested, 50);
            // Behind the origin and beyond the reach: no hit.
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(0.5f, 0.5f, -1f), Vector3.back, 5f).triangleIndex);
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(0.5f, 0.5f, 3f), Vector3.back, 2f).triangleIndex);
        }
        [Test]
        public void BvhQueriesMatchBruteForceOnARandomSoup()
        {
            // The SAH build must not change what the queries answer: nearest point and
            // first ray hit agree with an exhaustive scan over 400 random triangles.
            var rng = new System.Random(7);
            float R() => (float)rng.NextDouble();
            int faces = 400; var p = new Vector3[faces * 3]; var idx = new int[faces * 3];
            for (int f = 0; f < faces; ++f) {
                var centre = new Vector3(R() * 4 - 2, R() * 4 - 2, R() * 4 - 2);
                for (int k = 0; k < 3; ++k) { p[f * 3 + k] = centre + new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f) * 0.6f; idx[f * 3 + k] = f * 3 + k; }
            }
            var bvh = new TriangleBvh(p, idx);
            var frameDir = Vector3.zero;
            for (int q = 0; q < 200; ++q) {
                var point = new Vector3(R() * 5 - 2.5f, R() * 5 - 2.5f, R() * 5 - 2.5f);
                var near = bvh.FindNearest(point);
                float brute = float.MaxValue;
                for (int f = 0; f < faces; ++f) {
                    var cp = TriangleBvh.ClosestPointOnTriangle(point, p[f * 3], p[f * 3 + 1], p[f * 3 + 2], out _);
                    brute = Mathf.Min(brute, (cp - point).sqrMagnitude);
                }
                Assert.That(near.distSq, Is.EqualTo(brute).Within(1e-5f), "nearest " + q);
                var dir = new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f).normalized;
                var hit = bvh.Raycast(point, dir, 10f);
                float bruteT = 10f; var frame = new TriangleBvh.RayFrame(dir);
                for (int f = 0; f < faces; ++f)
                    if (TriangleBvh.Watertight(in frame, point, p[f * 3], p[f * 3 + 1], p[f * 3 + 2], bruteT, out float t, out _)) bruteT = t;
                if (bruteT < 10f) { Assert.GreaterOrEqual(hit.triangleIndex, 0, "ray " + q); Assert.That(hit.t, Is.EqualTo(bruteT).Within(1e-5f)); }
                else Assert.AreEqual(-1, hit.triangleIndex, "ray " + q);
            }
        }
        [Test]
        public void MeshGeometryHelpersAgreeWithTheirDefinitions()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.zero, Vector3.right };
            var slots = MeshGeometry.WeldPositions(p, out int count);
            Assert.AreEqual(3, count); Assert.AreEqual(slots[0], slots[3]); Assert.AreEqual(slots[1], slots[4]); Assert.AreNotEqual(slots[0], slots[2]);
            var n = MeshGeometry.FaceNormals(p, new[] { 0,1,2, 0,0,1 });
            Assert.That(Vector3.Angle(n[0], Vector3.forward), Is.LessThan(1e-4f)); Assert.AreEqual(Vector3.zero, n[1]);
            var dirs = MeshGeometry.SphereDirections(64);
            Assert.AreEqual(64, dirs.Length);
            Vector3 sum = Vector3.zero;
            foreach (var d in dirs) { Assert.That(d.magnitude, Is.EqualTo(1f).Within(1e-5f)); sum += d; }
            Assert.That(sum.magnitude, Is.LessThan(2f), "evenly spread: the directions nearly cancel");
            Assert.That(MeshGeometry.SqDistToAabb(new Vector3(2,0,0), Vector3.zero, Vector3.one), Is.EqualTo(1f).Within(1e-6f));
            Assert.AreEqual(0f, MeshGeometry.SqDistToAabb(new Vector3(0.5f,0.5f,0.5f), Vector3.zero, Vector3.one));
            // A box carried through a 90° yaw and a translation stays axis-aligned and the right size.
            var yaw90 = new Matrix4x4(new Vector4(0, 0, -1, 0), new Vector4(0, 1, 0, 0), new Vector4(1, 0, 0, 0), new Vector4(10, 0, 0, 1));
            var yawed = MeshGeometry.TransformBounds(new Bounds(Vector3.zero, new Vector3(2, 1, 4)), yaw90);
            Assert.That((yawed.center - new Vector3(10, 0, 0)).magnitude, Is.LessThan(1e-4f));
            Assert.That((yawed.size - new Vector3(4, 1, 2)).magnitude, Is.LessThan(1e-4f));
            Assert.That(MeshGeometry.BoundsDistance(new Bounds(Vector3.zero, Vector3.one), new Bounds(new Vector3(3, 0, 0), Vector3.one)), Is.EqualTo(2f).Within(1e-6f));
            Assert.AreEqual(0f, MeshGeometry.BoundsDistance(new Bounds(Vector3.zero, Vector3.one), new Bounds(new Vector3(0.5f, 0, 0), Vector3.one)));
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
        [TestCase(1)]
        [TestCase(16)]
        public void ConservativeCoveragePreservesChartsBetweenSampleCentres(int samples)
        {
            var source=Source();
            // A band narrower than a texel: preserve it even without a sample hit.
            var sliver=new RemeshNative.Geometry { positions=source.positions, normals=source.normals,
                uv=new[] { new Vector2(0.1f,0.103f), new Vector2(0.9f,0.103f), new Vector2(0.9f,0.104f) }, indices=source.indices };
            var super=RemeshBaker.Bake(source,sliver,source.tangents,new RemeshSettings { textureResolution=64,padding=1,bakeSamples=samples },CancellationToken.None);
            Assert.That(super.covered, Is.GreaterThan(0));
            Assert.That(super.misses, Is.Zero);
            Assert.AreEqual(255, super.ao[6 * 64 + 40].g);
            Assert.AreEqual(255, super.ao[6 * 64 + 40].a);
        }

        [TestCase(1, .02f)]
        [TestCase(4, .02f)]
        [TestCase(16, .02f)]
        [TestCase(1, .000003f)]
        [TestCase(4, .000003f)]
        [TestCase(16, .000003f)]
        public void TinyCentredChartAlwaysEvaluatesItsCoveredTexel(int samples, float halfWidth)
        {
            var source = Source();
            var target = new RemeshNative.Geometry { positions = source.positions, normals = source.normals,
                uv = new[] { new Vector2(8.5f - halfWidth, 8.5f - halfWidth) / 64, new Vector2(8.5f + halfWidth, 8.5f - halfWidth) / 64,
                    new Vector2(8.5f, 8.5f + halfWidth) / 64 }, indices = source.indices };
            var maps = RemeshBaker.Bake(source, target, source.tangents, new RemeshSettings {
                textureResolution = 64, padding = 1, bakeSamples = samples, bakeSourceAO = true }, CancellationToken.None);
            Assert.AreEqual(1, maps.covered);
            Assert.AreEqual(0, maps.misses);
            Assert.AreEqual(new Color32(255, 255, 255, 255), maps.ao[8 * 64 + 8], "coverage is an evaluated surface, never a black padding seed");
        }

        [Test]
        public void ConservativeCoverageRejectsZeroAreaAndOutOfAtlasCharts()
        {
            var source = Source();
            var target = new RemeshNative.Geometry { positions = source.positions, normals = source.normals,
                uv = new[] { Vector2.zero, Vector2.zero, Vector2.zero }, indices = source.indices };
            var settings = new RemeshSettings { textureResolution = 64, padding = 1 };
            Assert.Throws<InvalidOperationException>(() => RemeshBaker.Bake(source, target, source.tangents, settings, CancellationToken.None));
            target.uv = new[] { new Vector2(2, 2), new Vector2(3, 2), new Vector2(2, 3) };
            Assert.Throws<InvalidOperationException>(() => RemeshBaker.Bake(source, target, source.tangents, settings, CancellationToken.None));
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
        [TestCase(1f)]
        [TestCase(0.001f)]
        [TestCase(0.0001f)]
        public void CageKeepsDoubleSidedSheetApart(float scale)
        {
            // A quad with both windings on the SAME four vertices: a wall thinner than a
            // voxel after the simplifier collapsed its slab. A position weld sums the two
            // sides to nothing; the sided cage gives every front corner +z and every back
            // corner -z, with a uniform reach when no source is given.
            var p=new[] { Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up };
            for (int i = 0; i < p.Length; ++i) p[i] *= scale;
            var sheet=new RemeshNative.Geometry { positions=p, normals=new Vector3[4], indices=new[] { 0,1,2, 0,2,3, 0,2,1, 0,3,2 } };
            var cage=RemeshBaker.BuildCage(sheet, 0.1f, 2f, null);
            for (int c=0;c<6;++c) Assert.That(Vector3.Angle(cage.directions[c], Vector3.forward), Is.LessThan(0.01f), "front corner "+c);
            for (int c=6;c<12;++c) Assert.That(Vector3.Angle(cage.directions[c], Vector3.back), Is.LessThan(0.01f), "back corner "+c);
            Assert.AreEqual(4, cage.positions); Assert.AreEqual(8, cage.sides); Assert.AreEqual(4, cage.folded);
            Assert.AreEqual(4, cage.zeroNormals);
            foreach (float r in cage.reach) Assert.That(r, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(cage.Direction(0, new Vector3(0.2f,0.3f,0.5f)).z, Is.GreaterThan(0.99f));
            Assert.That(cage.Reach(2, new Vector3(0.2f,0.3f,0.5f)), Is.EqualTo(0.1f).Within(1e-6f));
        }
        [TestCase(1f)]
        [TestCase(0.001f)]
        [TestCase(0.0001f)]
        public void CageWeldsSplitCopiesAcrossACrease(float scale)
        {
            // The 90° fold split along its shared edge (a chart border): the copies at one
            // position agree within 120°, so they share one side whose direction is the
            // average of both faces, and the sheet's far corners keep their own face.
            var p=new[] { Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, Vector3.zero, Vector3.right };
            for (int i = 0; i < p.Length; ++i) p[i] *= scale;
            var fold=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 5,4,3 } };
            RemeshNative.GenerateSplitNormals(fold, RemeshNormalWeighting.FaceArea);
            var cage=RemeshBaker.BuildCage(fold, 0.1f, 0f, null);
            var average=Vector3.Normalize(new Vector3(0,-1,-1));
            Assert.AreEqual(4, cage.positions); Assert.AreEqual(4, cage.sides); Assert.AreEqual(0, cage.folded);
            Assert.AreEqual(cage.side[0], cage.side[4], "coincident copies share a side");
            Assert.That(Vector3.Angle(cage.directions[0], average), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(cage.directions[4], average), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(cage.directions[2], Vector3.down), Is.LessThan(0.01f), "far corner of the floor face");
            Assert.That(Vector3.Angle(cage.directions[5], Vector3.back), Is.LessThan(0.01f), "far corner of the wall face");
            // Every direction leaves the front of its own face.
            for (int c=0;c<6;++c) {
                int f=c/3; int a=fold.indices[f*3], b=fold.indices[f*3+1], d=fold.indices[f*3+2];
                var fn=MeshGeometry.UnitDirection(Vector3.Cross(p[b]-p[a], p[d]-p[a]));
                Assert.That(Vector3.Dot(cage.directions[c], fn), Is.GreaterThan(RemeshBaker.Cage.MinFacing));
            }
            Assert.AreEqual(4, cage.oneSided, "all four split copies along the crease sit 45° off the welded cage");
        }
        [Test]
        public void CageFitReachesTheSource()
        {
            // A flat target under a source sheet 0.3 above it: with a 0.1 projection
            // distance the plain cage misses the source; the fitted reach measures 0.3
            // along the ray, doubles it for oblique surfaces and stays within 8×.
            var target=new RemeshNative.Geometry { positions=new[] { Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up },
                normals=new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward }, indices=new[] { 0,1,2, 0,2,3 } };
            var sp=new[] { new Vector3(-2,-2,0.3f), new Vector3(3,-2,0.3f), new Vector3(3,3,0.3f), new Vector3(-2,3,0.3f) };
            var source=new TriangleBvh(sp, new[] { 0,1,2, 0,2,3 });
            var fitted=RemeshBaker.BuildCage(target, 0.1f, 2f, source);
            foreach (float r in fitted.reach) Assert.That(r, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(fitted.maxReach, Is.EqualTo(0.6f).Within(1e-4f));
            // Far beyond the 8× range the fit falls back to the projection distance.
            var far=new TriangleBvh(new[] { new Vector3(-2,-2,5), new Vector3(3,-2,5), new Vector3(3,3,5), new Vector3(-2,3,5) }, new[] { 0,1,2, 0,2,3 });
            foreach (float r in RemeshBaker.BuildCage(target, 0.1f, 2f, far).reach) Assert.That(r, Is.EqualTo(0.1f).Within(1e-6f));
            // Closer than the projection distance the reach stays at the distance.
            var near=new TriangleBvh(new[] { new Vector3(-2,-2,0.02f), new Vector3(3,-2,0.02f), new Vector3(3,3,0.02f), new Vector3(-2,3,0.02f) }, new[] { 0,1,2, 0,2,3 });
            foreach (float r in RemeshBaker.BuildCage(target, 0.1f, 2f, near).reach) Assert.That(r, Is.EqualTo(0.1f).Within(1e-6f));
        }
        [Test]
        public void UvIslandNormalsAreHardOnlyAtSplitVertices()
        {
            // Two faces folded 90° along x. Shared vertices smooth; split vertices stay hard.
            var p=new[] { Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, Vector3.zero, Vector3.right };
            var shared=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 1,0,3 } };
            RemeshNative.GenerateSplitNormals(shared, RemeshNormalWeighting.FaceArea);
            Assert.That(Vector3.Angle(shared.normals[0], Vector3.Normalize(new Vector3(0,-1,-1))), Is.LessThan(0.01f));
            var split=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 5,4,3 } };
            RemeshNative.GenerateSplitNormals(split, RemeshNormalWeighting.FaceArea);
            Assert.That(Vector3.Angle(split.normals[0], split.normals[4]), Is.EqualTo(90).Within(0.01f));
            // Corner-angle weighting keeps the same directions on this symmetric fold.
            var angled=new RemeshNative.Geometry { positions=(Vector3[])p.Clone(), normals=new Vector3[6], indices=new[] { 0,1,2, 1,0,3 } };
            RemeshNative.GenerateSplitNormals(angled, RemeshNormalWeighting.CornerAngle);
            Assert.That(Vector3.Angle(angled.normals[0], shared.normals[0]), Is.LessThan(0.01f));
            var both=new RemeshNative.Geometry { positions=(Vector3[])p.Clone(), normals=new Vector3[6], indices=new[] { 0,1,2, 1,0,3 } };
            RemeshNative.GenerateSplitNormals(both, RemeshNormalWeighting.FaceAreaAndCornerAngle);
            Assert.That(Vector3.Angle(both.normals[0], shared.normals[0]), Is.LessThan(0.01f));
        }
        [Test]
        public void SmoothModeWeldsChartBorderCopiesByNativeNormalGroup()
        {
            // The same 90° fold, split along its shared edge as xatlas splits a chart
            // border. With the native (smooth) normals as the group key the copies weld
            // back into one smooth normal; with crease-split native normals they stay hard.
            var p=new[] { Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, Vector3.zero, Vector3.right };
            var indices=new[] { 0,1,2, 5,4,3 };
            var smooth=Vector3.Normalize(new Vector3(0,-1,-1));
            var welded=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=indices };
            var groups=RemeshNative.GenerateSplitNormals(welded, RemeshNormalWeighting.FaceArea, new[] { smooth,smooth,smooth,smooth,smooth,smooth });
            Assert.That(Vector3.Angle(welded.normals[0], welded.normals[4]), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(welded.normals[0], smooth), Is.LessThan(0.01f));
            Assert.AreEqual(groups[0], groups[4], "coincident copies share a normal group");
            var creased=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=indices };
            RemeshNative.GenerateSplitNormals(creased, RemeshNormalWeighting.FaceArea,
                new[] { Vector3.back,Vector3.back,Vector3.back, Vector3.down,Vector3.down,Vector3.down });
            Assert.That(Vector3.Angle(creased.normals[0], creased.normals[4]), Is.EqualTo(90).Within(0.01f));
            // Smoothing over the welded groups keeps every member of a group identical.
            welded.normals[4]=Vector3.up;
            RemeshNative.SmoothNormals(welded, 1, groups);
            Assert.That(Vector3.Angle(welded.normals[0], welded.normals[4]), Is.LessThan(0.01f));
            // Tangents come out orthogonal to the final normal, handedness intact.
            welded.tangents=new Vector4[6];
            for (int i=0;i<6;++i) welded.tangents[i]=new Vector4(0,1,1,-1);
            RemeshNative.OrthogonalizeTangents(welded);
            Assert.That(Mathf.Abs(Vector3.Dot(welded.tangents[0], welded.normals[0])), Is.LessThan(1e-5f));
            Assert.AreEqual(-1f, welded.tangents[0].w);
        }
        [Test]
        public void LightmapRegionCoversTheRendererRectAndRemapsUv2()
        {
            // A renderer using the quarter [0.5,1]×[0.25,0.5] of a 1024² lightmap.
            var st=new Vector4(0.5f,0.25f,0.5f,0.25f);
            var region=RemeshBeauty.LightmapRegion(st,1024,1024,out var regionSt);
            float pad=1f/1024;
            Assert.That(region.xMin,Is.EqualTo(0.5f-pad).Within(1e-6f));
            Assert.That(region.xMax,Is.EqualTo(1f).Within(1e-6f));
            Assert.That(region.yMin,Is.EqualTo(0.25f-pad).Within(1e-6f));
            Assert.That(region.yMax,Is.EqualTo(0.5f+pad).Within(1e-6f));
            // uv2 (0,0) and (1,1) map to the rect's corners inside the region.
            Vector2 lo=new Vector2(regionSt.z,regionSt.w), hi=new Vector2(regionSt.x+regionSt.z,regionSt.y+regionSt.w);
            Assert.That(lo.x*region.width+region.x,Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(lo.y*region.height+region.y,Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(hi.x*region.width+region.x,Is.EqualTo(1f).Within(1e-5f));
            Assert.That(hi.y*region.height+region.y,Is.EqualTo(0.5f).Within(1e-5f));
            // A degenerate rect still yields a readable region.
            var tiny=RemeshBeauty.LightmapRegion(new Vector4(0,0,0.3f,0.3f),256,256,out _);
            Assert.That(tiny.width,Is.GreaterThan(0)); Assert.That(tiny.height,Is.GreaterThan(0));
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

        // A 1×1 wall sheet at the origin, a 5-long × 0.01-wide rod strip at z = 3 (optionally
        // yawed 45° so its axis-aligned bounds read fat), two renderers, no shared vertices.
        // Rodrigues rotation: the test runs outside Unity too, where Quaternion is native.
        static Vector3 Rotate(Vector3 p, Vector3 axis, float degrees)
        {
            axis.Normalize();
            float c = Mathf.Cos(degrees * Mathf.Deg2Rad), s = Mathf.Sin(degrees * Mathf.Deg2Rad);
            return p * c + Vector3.Cross(axis, p) * s + axis * (Vector3.Dot(axis, p) * (1 - c));
        }
        static RemeshSource SheetAndRod(float diagonal, bool diagonalRod)
        {
            Vector3 R(float x, float y) => (diagonalRod ? Rotate(new Vector3(x, y, 0), Vector3.forward, 45) : new Vector3(x, y, 0)) + new Vector3(0, 0, 3);
            return new RemeshSource {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0),
                    R(0, 0), R(5, 0), R(5, 0.01f), R(0, 0.01f) },
                normals = new Vector3[8], tangents = new Vector4[8], uv = new Vector2[8],
                colors = new[] { Color.red, Color.red, Color.red, Color.red, Color.blue, Color.blue, Color.blue, Color.blue }, hasColors = true,
                indices = new[] { 0,1,2, 1,3,2, 4,5,6, 4,6,7 }, faceMaterials = new[] { 0, 0, 0, 0 }, faceLightmaps = new[] { -1, -1, -1, -1 },
                vertexRenderer = new[] { 0,0,0,0, 1,1,1,1 }, rendererToSpace = new[] { Matrix4x4.identity, Matrix4x4.identity },
                diagonal = diagonal, materials = new[] { new RemeshSource.Surface() }
            };
        }
        [Test]
        public void FilterSmallPartsDropsRodsNotSheetsAndCompactsEveryStream()
        {
            // Longest side 5 (x) at resolution 50 → one cell = 0.1: the 0.01-wide rod's section is
            // under a cell, the sheet's second extent (1) is ten cells — it stays.
            var source = SheetAndRod(10, false);
            Assert.IsTrue(source.FilterSmallParts(0, 1f, 50, out int small, out int rods));
            Assert.AreEqual(0, small); Assert.AreEqual(1, rods);
            Assert.AreEqual(4, source.positions.Length);
            Assert.AreEqual(new[] { 0,1,2, 1,3,2 }, source.indices);
            Assert.AreEqual(2, source.faceMaterials.Length); Assert.AreEqual(2, source.faceLightmaps.Length);
            Assert.AreEqual(4, source.colors.Length); Assert.AreEqual(Color.red, source.colors[3]);
            Assert.AreEqual(new[] { 0,0,0,0 }, source.vertexRenderer);
            Assert.AreEqual(10, source.diagonal);
            // The same rod yawed 45°: its axis-aligned box is 3.5 × 3.5, its principal section still 0.01.
            var yawed = SheetAndRod(10, true);
            Assert.IsTrue(yawed.FilterSmallParts(0, 1f, 50, out small, out rods));
            Assert.AreEqual(1, rods); Assert.AreEqual(4, yawed.positions.Length);
            // A coarser grid (cell 0.5) still keeps the sheet: one thin extent is not a rod.
            var coarse = SheetAndRod(10, false);
            Assert.IsTrue(coarse.FilterSmallParts(0, 1f, 10, out _, out rods));
            Assert.AreEqual(1, rods); Assert.AreEqual(4, coarse.positions.Length);
            // Size test: the rod's extent (5) passes a 0.2 × 10 floor, the sheet's (√2) does not.
            var sized = SheetAndRod(10, false);
            Assert.IsTrue(sized.FilterSmallParts(0.2f, 0, 50, out small, out rods));
            Assert.AreEqual(1, small); Assert.AreEqual(0, rods); Assert.AreEqual(4, sized.positions.Length);
            Assert.AreEqual(Color.blue, sized.colors[0]);
            // A filter that would drop everything is refused and changes nothing.
            var all = SheetAndRod(10, false);
            Assert.IsFalse(all.FilterSmallParts(0.9f, 0, 50, out small, out rods));
            Assert.AreEqual(0, small); Assert.AreEqual(0, rods); Assert.AreEqual(8, all.positions.Length);
            // 0/0 is a no-op.
            Assert.IsTrue(SheetAndRod(10, false).FilterSmallParts(0, 0, 50, out _, out _));
        }
        [Test]
        public void ClassifyPartsLabelsFacesWithoutChangingTheCapture()
        {
            var source = SheetAndRod(10, false);
            var cls = source.ClassifyParts(0, 1f, 50);
            Assert.AreEqual(new[] { RemeshSource.PartKept, RemeshSource.PartKept, RemeshSource.PartRod, RemeshSource.PartRod }, cls);
            Assert.AreEqual(8, source.positions.Length); Assert.AreEqual(12, source.indices.Length);
            Assert.AreEqual(RemeshSource.PartSmall, SheetAndRod(10, false).ClassifyParts(0.2f, 0, 50)[0]);
            Assert.IsNull(SheetAndRod(10, false).ClassifyParts(0, 0, 50));
        }
        static void Cube(out Vector3[] positions, out int[] indices, bool inverted)
        {
            positions = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1) };
            // Unity-style clockwise-from-outside faces, the winding OrientedBoxes emits.
            int[] faces = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            indices = new int[36];
            for (int i = 0; i < 36; i += 3) { indices[i] = faces[i]; indices[i+1] = faces[inverted ? i+2 : i+1]; indices[i+2] = faces[inverted ? i+1 : i+2]; }
        }
        [Test]
        public void WindingProbeJudgesFromOutsideAndStaysOffOnOpenSheets()
        {
            Cube(out var p, out var tri, false);
            var normals = new Vector3[12];
            for (int f = 0; f < 12; ++f) normals[f] = Vector3.Cross(p[tri[f*3+1]] - p[tri[f*3]], p[tri[f*3+2]] - p[tri[f*3]]).normalized;
            int outward = RemeshBaker.ProbeWinding(new TriangleBvh(p, tri), p, normals);
            Cube(out p, out tri, true);
            for (int f = 0; f < 12; ++f) normals[f] = Vector3.Cross(p[tri[f*3+1]] - p[tri[f*3]], p[tri[f*3+2]] - p[tri[f*3]]).normalized;
            int inverted = RemeshBaker.ProbeWinding(new TriangleBvh(p, tri), p, normals);
            // The two windings are told apart with certainty, whatever the sign convention names them.
            Assert.AreNotEqual(0, outward); Assert.AreEqual(-outward, inverted);
            // A single sheet is seen from both sides half the time: no verdict, filter stays off.
            var sheet = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) };
            var sheetTri = new[] { 0, 1, 2, 1, 3, 2 };
            var sheetN = new[] { Vector3.back, Vector3.back };
            Assert.AreEqual(0, RemeshBaker.ProbeWinding(new TriangleBvh(sheet, sheetTri), sheet, sheetN));
        }
        [TestCase(1f)]
        [TestCase(0.001f)]
        [TestCase(0.0001f)]
        public void NearestPointStaysOnSmallTriangleInterior(float scale)
        {
            var p = new Vector3(.25f, .25f, .25f) * scale;
            var closest = TriangleBvh.ClosestPointOnTriangle(p, Vector3.zero,
                Vector3.right * scale, Vector3.up * scale, out var bary);
            Assert.That((closest - new Vector3(.25f, .25f, 0) * scale).magnitude, Is.LessThan(scale * 1e-5f));
            Assert.That((bary - new Vector3(.5f, .25f, .25f)).magnitude, Is.LessThan(1e-5f));
        }

        [TestCase(1f)]
        [TestCase(0.001f)]
        [TestCase(0.0001f)]
        public void SmallScaleTrimKeepsFrontAndDropsBack(float scale)
        {
            var p = new[] { Vector3.zero, Vector3.right * scale, Vector3.up * scale };
            var mesh = new RemeshNative.IndexedMesh { positions = p, indices = new[] { 0, 1, 2, 0, 2, 1 } };
            var trimmed = RemeshTrim.Trim(mesh, p, new[] { 0, 1, 2 }, scale * 0.1f, CancellationToken.None);
            Assert.IsFalse(trimmed.gaveUp);
            Assert.AreEqual(1, trimmed.removed);
            Assert.AreEqual(RemeshTrim.Kept, trimmed.classes[0]);
            Assert.AreEqual(RemeshTrim.Back, trimmed.classes[1]);
            Assert.AreEqual(1, trimmed.mesh.TriangleCount);
        }

        [TestCase(RemeshNormalWeighting.FaceArea)]
        [TestCase(RemeshNormalWeighting.CornerAngle)]
        [TestCase(RemeshNormalWeighting.FaceAreaAndCornerAngle)]
        public void SplitNormalsPreserveDirectionsAtSmallScales(RemeshNormalWeighting weighting)
        {
            var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
            Vector3[] expected = null;
            foreach (float scale in new[] { 1f, 0.001f, 0.0001f }) {
                var p = new Vector3[vertices.Length];
                for (int i = 0; i < p.Length; ++i) p[i] = vertices[i] * scale;
                var geometry = new RemeshNative.Geometry { positions = p, normals = new Vector3[p.Length],
                    indices = new[] { 0, 1, 2, 0, 3, 1 } };
                RemeshNative.GenerateSplitNormals(geometry, weighting);
                if (expected == null) expected = (Vector3[])geometry.normals.Clone();
                for (int i = 0; i < p.Length; ++i) {
                    Assert.That(geometry.normals[i].magnitude, Is.EqualTo(1f).Within(1e-5f));
                    Assert.That(Vector3.Dot(geometry.normals[i], expected[i]), Is.GreaterThan(0.99999f));
                }
            }
        }

        [TestCase(RemeshNormalWeighting.FaceArea)]
        [TestCase(RemeshNormalWeighting.CornerAngle)]
        [TestCase(RemeshNormalWeighting.FaceAreaAndCornerAngle)]
        public void SplitNormalsRecoverSmallFacesAndCancellingFaces(RemeshNormalWeighting weighting)
        {
            // A valid sliver below the mesh-relative accumulation floor, then a
            // two-sided sheet whose incident face normals cancel exactly.
            var geometry = new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up,
                    new Vector3(2, 0, 0), new Vector3(2, 0.0001f, 0), new Vector3(2, 0, 0.0001f),
                    new Vector3(3, 0, 0), new Vector3(4, 0, 0), new Vector3(3, 1, 0) },
                normals = new Vector3[9], indices = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 6, 8, 7 }
            };
            RemeshNative.GenerateSplitNormals(geometry, weighting);
            for (int i = 0; i < geometry.normals.Length; ++i)
                Assert.That(geometry.normals[i].magnitude, Is.EqualTo(1f).Within(1e-5f), "vertex " + i);
            Assert.That(Vector3.Dot(geometry.normals[3], Vector3.right), Is.GreaterThan(.999f));
            Assert.That(Vector3.Dot(geometry.normals[6], Vector3.forward), Is.GreaterThan(.999f));
        }

        [Test]
        public void SplitNormalsDoNotInventDirectionsForDegenerateFaces()
        {
            var geometry = new RemeshNative.Geometry { positions = new[] { Vector3.zero, Vector3.right, Vector3.right * 2 },
                normals = new Vector3[3], indices = new[] { 0, 1, 2 } };
            RemeshNative.GenerateSplitNormals(geometry, RemeshNormalWeighting.FaceArea);
            foreach (var normal in geometry.normals) Assert.AreEqual(Vector3.zero, normal);
        }

        [Test]
        public void MissingOrParallelTangentsGetAValidFrame()
        {
            var geometry = new RemeshNative.Geometry { normals = new[] { Vector3.forward, Vector3.up },
                tangents = new[] { Vector4.zero, new Vector4(0, 1, 0, -1) } };
            RemeshNative.OrthogonalizeTangents(geometry);
            for (int i = 0; i < 2; ++i) {
                Vector3 t = geometry.tangents[i];
                Assert.That(t.magnitude, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(Vector3.Dot(t, geometry.normals[i]), Is.EqualTo(0f).Within(1e-5f));
            }
            Assert.AreEqual(1f, geometry.tangents[0].w);
            Assert.AreEqual(-1f, geometry.tangents[1].w);
        }

        [Test]
        public void TrimKeepsTheSideTheSourceHasAndDropsTheSlabsBackAndRims()
        {
            // Source: one quad whose front faces -z (Unity's Quad winding). Remesh: a thin
            // slab around it (a box 1×1×0.05), as the voxelizer returns for an open sheet.
            var sourcePositions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
            var sourceIndices = new[] { 0, 2, 1, 0, 3, 2 };
            Cube(out var p, out var tri, false);
            for (int i = 0; i < p.Length; ++i) p[i] = new Vector3(p[i].x, p[i].y, p[i].z * 0.05f - 0.025f);
            var slab = new RemeshNative.IndexedMesh { positions = p, indices = tri };
            var trimmed = RemeshTrim.Trim(slab, sourcePositions, sourceIndices, 0.1f, CancellationToken.None, out int removed);
            // The two faces on the source's front side (-z) survive; the back and the four rims (10 faces) go.
            Assert.AreEqual(10, removed);
            Assert.AreEqual(2, trimmed.TriangleCount);
            Assert.AreEqual(4, trimmed.positions.Length);
            foreach (var v in trimmed.positions) Assert.That(v.z, Is.EqualTo(-0.025f).Within(1e-5f));
            // Orientation decides, not position: the remesher fits both slab faces onto the
            // sheet itself, so only the face whose normal agrees with the source can be
            // told apart. The same slab wound inside out keeps the face at +z whose
            // (inverted) normal points -z like the source.
            Cube(out var pi, out var triInverted, true);
            for (int i = 0; i < pi.Length; ++i) pi[i] = new Vector3(pi[i].x, pi[i].y, pi[i].z * 0.05f - 0.025f);
            var inverted = RemeshTrim.Trim(new RemeshNative.IndexedMesh { positions = pi, indices = triInverted }, sourcePositions, sourceIndices, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(10, removed);
            foreach (var v in inverted.positions) Assert.That(v.z, Is.EqualTo(0.025f).Within(1e-5f));
            for (int f = 0; f < inverted.indices.Length; f += 3) {
                var fn = Vector3.Cross(inverted.positions[inverted.indices[f + 1]] - inverted.positions[inverted.indices[f]],
                    inverted.positions[inverted.indices[f + 2]] - inverted.positions[inverted.indices[f]]).normalized;
                Assert.That(Vector3.Dot(fn, Vector3.back), Is.GreaterThan(0.99f));
            }
            // A zero-thickness double-sided sheet — what the remesher really returns for an
            // open source — keeps exactly the winding that matches the source.
            var flat = new RemeshNative.IndexedMesh { positions = (Vector3[])sourcePositions.Clone(),
                indices = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 } };
            var oneSided = RemeshTrim.Trim(flat, sourcePositions, sourceIndices, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(2, removed); Assert.AreEqual(2, oneSided.TriangleCount); Assert.AreEqual(4, oneSided.positions.Length);
            for (int f = 0; f < oneSided.indices.Length; f += 3) {
                var fn = Vector3.Cross(oneSided.positions[oneSided.indices[f + 1]] - oneSided.positions[oneSided.indices[f]],
                    oneSided.positions[oneSided.indices[f + 2]] - oneSided.positions[oneSided.indices[f]]).normalized;
                Assert.That(Vector3.Dot(fn, Vector3.back), Is.GreaterThan(0.99f));
            }
            // A source facing +z keeps the +z side instead.
            var flippedSource = RemeshTrim.Trim(slab, sourcePositions, new[] { 0, 1, 2, 0, 2, 3 }, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(10, removed);
            foreach (var v in flippedSource.positions) Assert.That(v.z, Is.EqualTo(0.025f).Within(1e-5f));
            // A closed source keeps the whole remesh: the slab against a copy of itself.
            var whole = RemeshTrim.Trim(slab, p, tri, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(0, removed); Assert.AreEqual(12, whole.TriangleCount);
            // The classes name why each raw face went: 2 kept, 2 back (a source face within
            // reach faces the other way), 8 rims (nothing parallel within reach).
            var result = RemeshTrim.Trim(slab, sourcePositions, sourceIndices, 0.1f, CancellationToken.None);
            int kept = 0, back = 0, rim = 0;
            foreach (byte c in result.classes) { if (c == RemeshTrim.Kept) ++kept; else if (c == RemeshTrim.Back) ++back; else ++rim; }
            Assert.AreEqual(2, kept); Assert.AreEqual(2, back); Assert.AreEqual(8, rim);
            Assert.IsFalse(result.gaveUp); Assert.AreEqual(0, result.flipped);
        }
        [Test]
        public void OrientConsistentlyRewindsTheMinorityOfEachPiece()
        {
            // A 3-quad strip with the middle quad wound the other way: both of its
            // triangles flip; the majority (4 faces) keeps its winding. A detached quad
            // keeps its own orientation (no shared edge constrains it).
            var p = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(2,0,0), new Vector3(3,0,0),
                            new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(2,1,0), new Vector3(3,1,0),
                            new Vector3(5,0,0), new Vector3(6,0,0), new Vector3(6,1,0), new Vector3(5,1,0) };
            var mesh = new RemeshNative.IndexedMesh { positions = p, indices = new[] {
                0,1,5, 0,5,4,        // +z
                1,6,2, 1,5,6,        // -z (flipped quad)
                2,3,7, 2,7,6,        // +z
                8,10,9, 8,11,10 } }; // detached, -z
            int flipped = RemeshTrim.OrientConsistently(mesh);
            Assert.AreEqual(2, flipped);
            for (int f = 0; f < 6; ++f) {
                var n = Vector3.Cross(p[mesh.indices[f*3+1]] - p[mesh.indices[f*3]], p[mesh.indices[f*3+2]] - p[mesh.indices[f*3]]);
                Assert.That(n.z, Is.GreaterThan(0), "strip face " + f);
            }
            for (int f = 6; f < 8; ++f) {
                var n = Vector3.Cross(p[mesh.indices[f*3+1]] - p[mesh.indices[f*3]], p[mesh.indices[f*3+2]] - p[mesh.indices[f*3]]);
                Assert.That(n.z, Is.LessThan(0), "detached face " + f);
            }
            // A sheet trimmed from a source with alternating winding comes out orientable:
            // the remesh keeps per face the side agreeing with the source, then re-winds.
            var src = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(2,0,0), new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(2,1,0) };
            var srcIdx = new[] { 0,1,4, 0,4,3,  1,2,5, 1,5,4 };  // both +z
            srcIdx = new[] { 0,1,4, 0,4,3,  1,5,2, 1,4,5 };      // right quad -z
            var sheet = new RemeshNative.IndexedMesh { positions = (Vector3[])src.Clone(),
                indices = new[] { 0,1,4, 0,4,3, 1,2,5, 1,5,4,  0,4,1, 0,3,4, 1,5,2, 1,4,5 } };  // both sides, both windings
            var trimmed = RemeshTrim.Trim(sheet, src, srcIdx, 0.1f, CancellationToken.None);
            Assert.AreEqual(4, trimmed.removed); Assert.AreEqual(4, trimmed.mesh.TriangleCount);
            Assert.AreEqual(2, trimmed.flipped, "the right quad's two faces, kept as -z like the source, are re-wound to the majority");
            var tp = trimmed.mesh.positions; var ti = trimmed.mesh.indices;
            for (int f = 0; f < 4; ++f)
                Assert.That(Vector3.Cross(tp[ti[f*3+1]] - tp[ti[f*3]], tp[ti[f*3+2]] - tp[ti[f*3]]).z, Is.GreaterThan(0));
        }
        [Test]
        public void TwoSidedFacesFollowTheMaterialsOrTheSetting()
        {
            var source = Source();
            Assert.IsNull(source.TwoSidedFaces(RemeshBackfaces.FromMaterials), "no two-sided material");
            Assert.IsNull(source.TwoSidedFaces(RemeshBackfaces.Never));
            CollectionAssert.AreEqual(new[] { true }, source.TwoSidedFaces(RemeshBackfaces.Always));
            source.materials[0].twoSided = true;
            CollectionAssert.AreEqual(new[] { true }, source.TwoSidedFaces(RemeshBackfaces.FromMaterials));
            Assert.IsNull(source.TwoSidedFaces(RemeshBackfaces.Never), "Never overrides the material");
            // The bake accepts a two-sided face from behind: a sheet facing away from the
            // target bakes without misses when its material is two-sided.
            var target = new RemeshNative.Geometry { positions = source.positions, normals = source.normals, uv = source.uv, indices = source.indices };
            var away = Source(); away.indices = new[] { 0, 2, 1 }; away.materials[0].twoSided = true;
            var maps = RemeshBaker.Bake(away, target, source.tangents, new RemeshSettings { textureResolution = 64, padding = 2, sourceBackfaces = RemeshBackfaces.FromMaterials }, CancellationToken.None);
            Assert.AreEqual(0, maps.misses); Assert.AreEqual(1, maps.twoSidedFaces);
        }
        [Test]
        public void PrincipalExtentsFollowThePointSetsOwnAxes()
        {
            var pts = new System.Collections.Generic.List<Vector3>();
            foreach (var x in new[] { 0f, 4f }) foreach (var y in new[] { 0f, 2f }) foreach (var z in new[] { 0f, 0.5f })
                pts.Add(Rotate(Rotate(new Vector3(x, y, z), Vector3.up, 45), new Vector3(1, 0, 1), 30) + Vector3.one * 7);
            var e = RemeshSource.PrincipalExtents(pts);
            Assert.That(e.x, Is.EqualTo(4).Within(1e-3f));
            Assert.That(e.y, Is.EqualTo(2).Within(1e-3f));
            Assert.That(e.z, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.AreEqual(Vector3.zero, RemeshSource.PrincipalExtents(new System.Collections.Generic.List<Vector3>()));
        }
        [Test]
        public void OrientedBoxesFollowTheRendererAxesAndWindOutward()
        {
            // A 2×1 rectangle authored in renderer-local XZ, the renderer yawed 45°.
            var yaw = Matrix4x4.TRS(new Vector3(3,0,0), Quaternion.Euler(0,45,0), Vector3.one);
            Vector3[] local = { Vector3.zero, new Vector3(2,0,0), new Vector3(2,0,1), new Vector3(0,0,1) };
            var source = new RemeshSource {
                positions = new Vector3[4], indices = new[] { 0,1,2, 0,2,3 },
                vertexRenderer = new[] { 0,0,0,0 }, rendererToSpace = new[] { yaw }
            };
            for (int i = 0; i < 4; ++i) source.positions[i] = yaw.MultiplyPoint3x4(local[i]);
            var box = source.OrientedBoxes();
            Assert.AreEqual(8, box.positions.Length); Assert.AreEqual(36, box.indices.Length);
            // Corners land on the local 2×0×1 box, not on the ~2.1×2.1 axis-aligned one.
            var inv = yaw.inverse; var mn = Vector3.one * float.MaxValue; var mx = Vector3.one * float.MinValue;
            foreach (var p in box.positions) { var l = inv.MultiplyPoint3x4(p); mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l); }
            Assert.That(mn.x, Is.EqualTo(0).Within(1e-5f)); Assert.That(mx.x, Is.EqualTo(2).Within(1e-5f));
            Assert.That(mn.z, Is.EqualTo(0).Within(1e-5f)); Assert.That(mx.z, Is.EqualTo(1).Within(1e-5f));
            // The flat axis is padded to 0.4% of the longest side, centred on the sheet.
            Assert.That(mx.y - mn.y, Is.EqualTo(0.008f).Within(1e-5f));
            Assert.That(mx.y + mn.y, Is.EqualTo(0).Within(1e-5f));
            // A mirrored renderer gets its winding flipped back, so the signed volume keeps its sign.
            float Volume(RemeshNative.IndexedMesh m) {
                float v = 0;
                for (int i = 0; i < m.indices.Length; i += 3)
                    v += Vector3.Dot(m.positions[m.indices[i]], Vector3.Cross(m.positions[m.indices[i+1]], m.positions[m.indices[i+2]]));
                return v / 6;
            }
            var cube = new RemeshSource { positions = new[] { Vector3.zero, Vector3.one }, indices = new int[0],
                vertexRenderer = new[] { 0, 0 }, rendererToSpace = new[] { Matrix4x4.identity } };
            var mirrored = new RemeshSource { positions = new[] { Vector3.zero, new Vector3(-1,1,1) }, indices = new int[0],
                vertexRenderer = new[] { 0, 0 }, rendererToSpace = new[] { Matrix4x4.Scale(new Vector3(-1,1,1)) } };
            float straight = Volume(cube.OrientedBoxes()), flipped = Volume(mirrored.OrientedBoxes());
            Assert.That(Mathf.Abs(straight), Is.EqualTo(1).Within(1e-5f));
            Assert.That(flipped, Is.EqualTo(straight).Within(1e-5f));
            // No vertices at all → no box.
            Assert.IsNull(new RemeshSource { positions = new Vector3[0], indices = new int[0], vertexRenderer = new int[0],
                rendererToSpace = new[] { Matrix4x4.identity } }.OrientedBoxes());
        }
        [Test]
        public void VertexColorTintMultipliesTheLinearAlbedo()
        {
            var source = Source();
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(1,0,0,1),true,
                out var color, out _, out _, out _, out _);
            // Vertex 0 is pure red: the tinted albedo keeps only the red channel of the material tint.
            Assert.That(color.r, Is.EqualTo(new Color(0.25f,0.5f,0.75f).gamma.r).Within(1e-5f));
            Assert.That(color.g, Is.EqualTo(0).Within(1e-5f)); Assert.That(color.b, Is.EqualTo(0).Within(1e-5f));
            Assert.AreEqual(1, color.a);
            // Half-way between red and green vertices: both channels at half strength, blue gone.
            RemeshBaker.Evaluate(source,0,new Vector3(0.5f,0.5f,0),Vector3.forward,new Vector4(1,0,0,1),true,
                out color, out _, out _, out _, out _);
            Assert.That(color.r, Is.EqualTo(new Color(0.125f,0,0).gamma.r).Within(1e-5f));
            Assert.That(color.g, Is.EqualTo(new Color(0,0.25f,0).gamma.g).Within(1e-5f));
            Assert.That(color.b, Is.EqualTo(0).Within(1e-5f));
            // Off: untouched.
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(1,0,0,1),false,
                out color, out _, out _, out _, out _);
            Assert.That(color.g, Is.EqualTo(new Color(0.25f,0.5f,0.75f).gamma.g).Within(1e-5f));
        }
        static RemeshSource AoSource(bool roof)
        {
            var source = Source();
            source.positions = new[] { new Vector3(-2, -2, 0), new Vector3(2, -2, 0), new Vector3(0, 2, 0),
                new Vector3(0, -4, .2f), new Vector3(4, -4, .2f), new Vector3(4, 4, .2f), new Vector3(0, 4, .2f) };
            source.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            source.tangents = new Vector4[7]; source.uv = new Vector2[7];
            for (int i = 0; i < 7; ++i) source.tangents[i] = new Vector4(1, 0, 0, 1);
            source.indices = roof ? new[] { 0, 1, 2, 3, 5, 4, 3, 6, 5 } : new[] { 0, 1, 2 };
            source.faceMaterials = roof ? new[] { 0, 0, 0 } : new[] { 0 };
            source.diagonal = 10;
            return source;
        }

        [Test]
        public void SourceAoUsesGeometryAndNormalMapAtTheSourceHit()
        {
            var source = AoSource(true);
            var bvh = new TriangleBvh(source.positions, source.indices);
            var normals = MeshGeometry.FaceNormals(source.positions, source.indices);
            var settings = new SourceAoSettings { samples = 512, radius = 1, binaryHit = true, normalMap = false };
            Vector3 weights = new Vector3(.25f, .25f, .5f);
            float smooth = new SourceAoBaker(source, bvh, normals, null, settings).Sample(0, weights, 7, CancellationToken.None);
            Assert.That(smooth, Is.InRange(.2f, .8f), "the roof covers roughly half of the upper hemisphere");
            source.materials[0].normal.image = new RemeshSource.Image { width = 1, height = 1,
                pixels = new[] { new Color32(230, 128, 255, 255) }, wrapU = TextureWrapMode.Clamp, wrapV = TextureWrapMode.Clamp };
            settings.normalMap = true;
            float bumped = new SourceAoBaker(source, bvh, normals, null, settings).Sample(0, weights, 7, CancellationToken.None);
            Assert.That(bumped, Is.LessThan(smooth - .15f), "tilting the source normal toward the roof increases its occlusion");
            source.materials[0].normalScale = 0;
            float disabledStrength = new SourceAoBaker(source, bvh, normals, null, settings).Sample(0, weights, 7, CancellationToken.None);
            Assert.That(disabledStrength, Is.EqualTo(smooth).Within(1e-5f), "material normal strength is respected");
        }

        [TestCase(90f, false)]
        [TestCase(-90f, true)]
        public void SourceAoFloorUsesViewportWorldUpUnderRotatedScaledFbxRoot(float angle, bool facesUp)
        {
            var root = new GameObject("Rotated AO root");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }, normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward } };
            try {
                root.transform.SetPositionAndRotation(new Vector3(5, 12, -7), Quaternion.Euler(angle, 0, 0));
                root.transform.localScale = new Vector3(-2, 3, .5f);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = root.AddComponent<MeshRenderer>();
                var source = RemeshSource.Capture(root.transform.worldToLocalMatrix, new[] { renderer }, aoOnly: true);
                // Carrying the capture-space plane normal back to world space must
                // produce the viewport's Y-up, even with nonuniform mirrored scale.
                var worldNormal = root.transform.worldToLocalMatrix.transpose.MultiplyVector(source.groundNormal).normalized;
                Assert.That(Vector3.Dot(worldNormal, Vector3.up), Is.GreaterThan(.9999f));
                var settings = new SourceAoSettings { groundPlane = true, groundOffset = .01f,
                    normalMap = false, samples = 512, radius = 1, binaryHit = true };
                var bvh = new TriangleBvh(source.positions, source.indices);
                var normals = MeshGeometry.FaceNormals(source.positions, source.indices);
                float ao = new SourceAoBaker(source, bvh, normals, null, settings).Sample(0, new Vector3(.5f, .25f, .25f), 7, CancellationToken.None);
                if (facesUp) Assert.That(ao, Is.GreaterThan(.99f), "floor cannot occlude an upward hemisphere");
                else Assert.That(ao, Is.LessThan(.05f), "downward hemisphere faces the horizontal floor");
                settings.groundPlane = false;
                Assert.That(new SourceAoBaker(source, bvh, normals, null, settings).Sample(0,
                    new Vector3(.5f, .25f, .25f), 7, CancellationToken.None), Is.GreaterThan(.99f));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void TextureBakeCanRecalculateAoOrMultiplyTheSourceMap()
        {
            var source = Source();
            source.materials[0].ao.image = new RemeshSource.Image { width = 1, height = 1,
                pixels = new[] { new Color32(64, 64, 64, 255) }, wrapU = TextureWrapMode.Clamp, wrapV = TextureWrapMode.Clamp };
            var target = new RemeshNative.Geometry { positions = source.positions, normals = source.normals, uv = source.uv,
                indices = source.indices, tangents = source.tangents };
            var settings = new RemeshSettings { textureResolution = 64, padding = 1, bakeSamples = 1, bakeSourceAO = true };
            int pixel = 8 * 64 + 8;
            var calculated = RemeshBaker.Bake(source, target, source.tangents, settings, CancellationToken.None);
            Assert.IsTrue(calculated.sourceAO); Assert.That(calculated.misses, Is.Zero);
            Assert.AreEqual(255, calculated.ao[pixel].g, "an open plane has no geometric occlusion");
            settings.multiplySourceAO = true;
            var multiplied = RemeshBaker.Bake(source, target, source.tangents, settings, CancellationToken.None);
            Assert.That(multiplied.ao[pixel].g, Is.EqualTo(64).Within(1));
            settings.bakeSourceAO = false;
            var transferred = RemeshBaker.Bake(source, target, source.tangents, settings, CancellationToken.None);
            Assert.IsFalse(transferred.sourceAO);
            Assert.That(transferred.ao[pixel].g, Is.EqualTo(64).Within(1), "the existing material AO transfer remains available");
        }

        [Test]
        public void SourceAoCancellationAndSettingsAreValidated()
        {
            var source = AoSource(false);
            var sampler = new SourceAoBaker(source, new TriangleBvh(source.positions, source.indices),
                MeshGeometry.FaceNormals(source.positions, source.indices), null, new SourceAoSettings());
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => sampler.Sample(0, new Vector3(.25f, .25f, .5f), 0, cancellation.Token));
            }
            Assert.Throws<ArgumentException>(() => new SourceAoSettings { radius = float.NaN }.Validate());
            Assert.Throws<ArgumentException>(() => new SourceAoSettings { samples = 0 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { bakeSourceAO = true, sourceAO = null }.Validate());
            var settings = new RemeshSettings();
            string key = RemeshPipeline.Key(RemeshPipeline.Stage.Bake, settings, null);
            settings.bakeSourceAO = true;
            Assert.AreNotEqual(key, RemeshPipeline.Key(RemeshPipeline.Stage.Bake, settings, null));
            key = RemeshPipeline.Key(RemeshPipeline.Stage.Bake, settings, null);
            settings.sourceAO.normalMap = false;
            Assert.AreNotEqual(key, RemeshPipeline.Key(RemeshPipeline.Stage.Bake, settings, null));
        }

        static System.Collections.IEnumerator AwaitBakeTask(System.Threading.Tasks.Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "asynchronous bake must finish without blocking the editor");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SourceAoGpuMatchesCpuNormalMapsFloorAndTwoSidedFiltering()
        {
            if (!SourceAoBaker.GpuSupported) Assert.Ignore("GPU AO needs compute shaders and async readback.");
            Assert.AreEqual(32, System.Runtime.InteropServices.Marshal.SizeOf<SourceAoBaker.SurfacePoint>());
            var source = AoSource(true);
            source.groundNormal = new Vector3(0, .5f, .5f).normalized;
            source.materials[0].normal.image = new RemeshSource.Image { width = 1, height = 1,
                pixels = new[] { new Color32(230, 128, 255, 255) }, wrapU = TextureWrapMode.Clamp, wrapV = TextureWrapMode.Clamp };
            var bvh = new TriangleBvh(source.positions, source.indices);
            var normals = MeshGeometry.FaceNormals(source.positions, source.indices);
            for (int variant = 0; variant < 4; ++variant) {
                var settings = new SourceAoSettings { samples = 128, radius = 1, groundPlane = true,
                    binaryHit = (variant & 1) != 0, cosineWeighted = (variant & 2) == 0,
                    backfaceCulling = (variant & 1) == 0, normalMap = (variant & 2) == 0 };
                var twoSided = variant == 2 ? new[] { false, true, true } : null;
                var sampler = new SourceAoBaker(source, bvh, normals, twoSided, settings);
                using (var tree = GpuBvh.TryCreate(bvh, normals, twoSided))
                using (var gpu = sampler.TryCreateGpu(tree)) {
                    Assert.IsNotNull(gpu, "Source AO shader must compile on this GPU");
                    var points = new SourceAoBaker.SurfacePoint[5]; var expected = new float[5]; var actual = new float[5];
                    for (int i = 0; i < 4; ++i) {
                        var w = new Vector3(.25f + i * .05f, .25f, .5f - i * .05f);
                        int seed = i == 3 ? -1 : i * 379;
                        points[i] = sampler.SamplePoint(0, w, seed);
                        expected[i] = sampler.Sample(0, w, seed, CancellationToken.None);
                    }
                    expected[4] = 1; // invalid normal: unoccluded on both backends
                    var task = gpu.SampleAsync(points, points.Length, actual, CancellationToken.None);
                    yield return AwaitBakeTask(task);
                    for (int i = 0; i < actual.Length; ++i)
                        Assert.That(actual[i], Is.EqualTo(expected[i]).Within(1e-4f), $"variant {variant}, sample {i}");
                }
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SourceAoGpuCancellationDrainsDispatchAndAllowsNextBatch()
        {
            if (!SourceAoBaker.GpuSupported) Assert.Ignore("GPU AO needs compute shaders and async readback.");
            var source = AoSource(true); var bvh = new TriangleBvh(source.positions, source.indices);
            var normals = MeshGeometry.FaceNormals(source.positions, source.indices);
            var sampler = new SourceAoBaker(source, bvh, normals, null, new SourceAoSettings { samples = 1024 });
            using (var tree = GpuBvh.TryCreate(bvh, normals))
            using (var gpu = sampler.TryCreateGpu(tree))
            using (var cts = new CancellationTokenSource()) {
                Assert.IsNotNull(gpu);
                var points = new SourceAoBaker.SurfacePoint[SourceAoBaker.Gpu.PointBatch + 1];
                var values = new float[points.Length];
                for (int i = 0; i < points.Length; ++i) points[i] = sampler.SamplePoint(0, new Vector3(.25f, .25f, .5f), i);
                var task = gpu.SampleAsync(points, points.Length, values, cts.Token);
                cts.Cancel();
                yield return AwaitBakeTask(task);
                Assert.IsTrue(task.IsCanceled, "cancel after a queued dispatch, drain readback before cleanup");
                task = gpu.SampleAsync(points, 1, values, CancellationToken.None);
                yield return AwaitBakeTask(task);
                Assert.That(values[0], Is.EqualTo(sampler.Sample(0, new Vector3(.25f, .25f, .5f), 0, CancellationToken.None)).Within(1e-4f));
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator TextureAoBackendSelectionRoutesCpuGpuAndFallback()
        {
            var source = Source();
            var target = new RemeshNative.Geometry { positions = source.positions, normals = source.normals,
                uv = source.uv, indices = source.indices, tangents = source.tangents };
            var options = new RemeshSettings { textureResolution = 64, padding = 1, bakeSourceAO = true,
                gpuProjection = false, sourceAO = new SourceAoSettings { samples = 16 } };
            var cpuTask = TextureAoBakePanel.BakeMaps(source, target, options, CancellationToken.None);
            yield return AwaitBakeTask(cpuTask);
            Assert.IsFalse(cpuTask.Result.gpu); Assert.IsFalse(cpuTask.Result.gpuAO);
            Assert.AreEqual("CPU", TextureAoBakePanel.BackendLabel(cpuTask.Result));
            options.gpuProjection = true;
            var gpuTask = TextureAoBakePanel.BakeMaps(source, target, options, CancellationToken.None);
            yield return AwaitBakeTask(gpuTask);
            Assert.AreEqual(SourceAoBaker.GpuSupported, gpuTask.Result.gpuAO);
            Assert.AreEqual(SourceAoBaker.GpuSupported, gpuTask.Result.gpu);
            CollectionAssert.AreEqual(cpuTask.Result.ao, gpuTask.Result.ao, "open source has identical AO on either device");
            var fallbackTask = RemeshBaker.BakeAsync(source, target, source.tangents, options, CancellationToken.None,
                null, _ => null, gpuSourceAO: true);
            yield return AwaitBakeTask(fallbackTask);
            Assert.IsFalse(fallbackTask.Result.gpuAO); Assert.IsFalse(fallbackTask.Result.gpu);
            Assert.AreEqual("CPU", TextureAoBakePanel.BackendLabel(fallbackTask.Result));
            CollectionAssert.AreEqual(cpuTask.Result.ao, fallbackTask.Result.ao);
            Assert.AreEqual("CPU AO / GPU projection", TextureAoBakePanel.BackendLabel(new RemeshBaker.Maps { gpu = true }));
        }

        [Test]
        public void TextureAoCapturesSelectedUvAndTransformsMirrorsWithoutTouchingTheMesh()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try {
                mesh.SetUVs(2, new[] { Vector2.one, Vector2.right, Vector2.up });
                var geometry = TextureAoBakePanel.CaptureTarget(mesh, 2);
                TextureAoBakePanel.TransformTarget(geometry, Matrix4x4.Scale(new Vector3(-2, 3, 1)));
                Assert.AreEqual(new Vector3(-2, 0, 0), geometry.positions[1]);
                CollectionAssert.AreEqual(new[] { 0, 2, 1 }, geometry.indices);
                Assert.AreEqual(Vector3.forward, geometry.normals[0]);
                Assert.AreEqual(Vector2.one, geometry.uv[0]);
                Assert.AreEqual(Vector3.right, mesh.vertices[1], "the live target is never mutated");
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, mesh.triangles);
                Assert.Throws<InvalidOperationException>(() => TextureAoBakePanel.CaptureTarget(mesh, 1));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator TextureAoStandalonePublishesExistingUvPreviewAndCancelsWithoutPartialResults()
        {
            var root = new GameObject("AO existing UV source");
            var mesh = new Mesh { name = "AO target", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up } };
            var panel = new TextureAoBakePanel();
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            var entry = new MeshEntry { originalMesh = mesh, renderer = renderer };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var settings = (RemeshSettings)typeof(TextureAoBakePanel).GetField("settings", flags).GetValue(panel);
            settings.textureResolution = 64; settings.sourceAO.samples = 16;
            var bake = typeof(TextureAoBakePanel).GetMethod("Bake", flags);
            var entries = new System.Collections.Generic.List<MeshEntry>();
            try {
                var task = (System.Threading.Tasks.Task)bake.Invoke(panel, new object[] { new[] { entry }, root });
                double deadline = EditorApplication.timeSinceStartup + 15;
                while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(task.IsCompleted, "the standalone worker must finish without blocking the editor loop");
                Assert.IsFalse(task.IsFaulted);
                Assert.IsTrue(panel.GetUvContent(entries));
                Assert.AreNotSame(mesh, entries[0].originalMesh);
                Assert.IsNotNull(entries[0].previewTexture);
                var texture = (Texture2D)entries[0].previewTexture;
                Assert.AreEqual(255, texture.GetPixels32()[8 * 64 + 8].g);
                var items = new System.Collections.Generic.List<MeshViewport3D.Item>();
                Assert.IsTrue(panel.Get3DContent(items));
                Assert.AreSame(entries[0].originalMesh, items[0].mesh);
                Assert.AreSame(mesh, root.GetComponent<MeshFilter>().sharedMesh);
                Assert.That(mesh.normals.Length, Is.Zero, "source readback must not regenerate normals on the live mesh");
                entries.Clear();
                task = (System.Threading.Tasks.Task)bake.Invoke(panel, new object[] { new[] { entry }, root });
                panel.Clear();
                deadline = EditorApplication.timeSinceStartup + 15;
                while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(task.IsCompleted);
                Assert.IsFalse(panel.GetUvContent(entries), "cancelled/stale work publishes no preview");
            }
            finally { panel.Deactivate(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator TextureAoSourceMapsReadBackAsynchronouslyAndSkipDisabledMaps()
        {
            var root = new GameObject("AO textured source");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles = new[] { 0, 1, 2 } };
            var normal = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var ao = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var material = new Material(Shader.Find("Standard"));
            try {
                if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Graphics device has no async readback.");
                normal.SetPixel(0, 0, new Color(0.5f, 0.5f, 1, 1)); normal.Apply(false, true);
                ao.SetPixel(0, 0, new Color32(64, 64, 64, 255)); ao.Apply(false, true);
                material.SetTexture("_BumpMap", normal); material.SetTexture("_OcclusionMap", ao);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = root.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                var options = new RemeshSettings { bakeSourceAO = true, multiplySourceAO = true };
                var source = RemeshSource.Capture(Matrix4x4.identity, new[] { renderer }, aoOnly: true, aoReadbackSettings: options);
                double deadline = EditorApplication.timeSinceStartup + 15;
                while (!source.TextureReadbacks.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(source.TextureReadbacks.IsCompleted, "GPU completion must arrive without blocking Unity");
                Assert.IsFalse(source.TextureReadbacks.IsFaulted, source.TextureReadbacks.Exception?.ToString());
                Assert.AreEqual(64, source.materials[0].ao.image.pixels[0].g);
                var n = source.materials[0].normal.Sample(Vector2.zero, Color.clear);
                Assert.That(n.b, Is.GreaterThan(0.99f)); Assert.That(n.r, Is.EqualTo(0.5f).Within(0.01f));
                Assert.IsFalse(normal.isReadable); Assert.IsFalse(ao.isReadable);
                Assert.AreSame(material, renderer.sharedMaterial);
                options.sourceAO.normalMap = false; options.multiplySourceAO = false;
                source = RemeshSource.Capture(Matrix4x4.identity, new[] { renderer }, aoOnly: true, aoReadbackSettings: options);
                Assert.IsTrue(source.TextureReadbacks.IsCompleted);
                Assert.IsNull(source.materials[0].normal.image); Assert.IsNull(source.materials[0].ao.image);
            }
            finally {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(normal); UnityEngine.Object.DestroyImmediate(ao); UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void AoSourceCaptureIncludesUntexturedGeometryWithoutUvOrMaterials()
        {
            var root = new GameObject("AO untextured source");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = root.AddComponent<MeshRenderer>();
                var source = RemeshSource.Capture(Matrix4x4.identity, new[] { renderer }, aoOnly: true);
                Assert.AreEqual(3, source.indices.Length);
                Assert.AreEqual(3, source.normals.Length);
                Assert.IsNull(source.materials[0].normal.image);
                Assert.IsNull(source.materials[0].ao.image);
                Assert.That(mesh.normals.Length, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void TextureAoTgaRleEncodingRunsOnWorkerAndPreservesPixelsAndRows()
        {
            const int width = 260, height = 2;
            var pixels = new Color32[width * height];
            for (int i = 0; i < width; ++i) pixels[i] = new Color32(64, 32, 16, 255);
            for (int i = width; i < pixels.Length; ++i) pixels[i] = new Color32((byte)i, (byte)(i * 3), (byte)(i * 7), 255);
            var bytes = System.Threading.Tasks.Task.Run(() => TextureAoBakePanel.EncodeTgaRle(pixels, width, height)).GetAwaiter().GetResult();
            Assert.AreEqual(10, bytes[2], "RLE true-colour TGA");
            Assert.AreEqual(24, bytes[16]); Assert.AreEqual(0, bytes[17], "bottom-left origin");
            Assert.AreEqual(width, bytes[12] | bytes[13] << 8);
            Assert.AreEqual(height, bytes[14] | bytes[15] << 8);
            Assert.That(bytes.Length, Is.LessThan(18 + pixels.Length * 3));
            int cursor = 18, written = 0, runs = 0, raws = 0;
            while (written < pixels.Length) {
                byte packet = bytes[cursor++]; int count = (packet & 127) + 1;
                Assert.That(written % width + count, Is.LessThanOrEqualTo(width), "packets cannot cross scanlines");
                bool run = (packet & 128) != 0;
                if (run) ++runs; else ++raws;
                for (int i = 0; i < count; ++i) {
                    var p = new Color32(bytes[cursor + 2], bytes[cursor + 1], bytes[cursor], 255);
                    Assert.AreEqual(pixels[written++], p, "linear BGR bytes, preserved bottom-up rows");
                    if (!run || i == count - 1) cursor += 3;
                }
            }
            Assert.AreEqual(bytes.Length, cursor); Assert.GreaterOrEqual(runs, 3); Assert.GreaterOrEqual(raws, 3);
            Assert.Throws<ArgumentException>(() => TextureAoBakePanel.EncodeTgaRle(pixels, 1, 1));
            using (var cts = new CancellationTokenSource()) {
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() => TextureAoBakePanel.EncodeTgaRle(pixels, width, height, cts.Token));
            }
        }

        [Test]
        public void TextureAoTgaRleImportsIntoUnityAsLinearTexture()
        {
            string path = "Assets/AO_RLE_" + Guid.NewGuid().ToString("N") + ".tga";
            var pixels = new[] { new Color32(64, 32, 16, 255), new Color32(64, 32, 16, 255), new Color32(12, 90, 210, 255),
                new Color32(20, 40, 60, 255), new Color32(50, 80, 110, 255), new Color32(170, 190, 220, 255) };
            try {
                System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(path), TextureAoBakePanel.EncodeTgaRle(pixels, 3, 2));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                TextureAssets.Configure(path, TextureAssets.Kind.Linear, 64, mipmaps: false);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.IsFalse(importer.sRGBTexture);
                importer.isReadable = true; importer.npotScale = TextureImporterNPOTScale.None; importer.SaveAndReimport();
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                Assert.IsNotNull(texture); Assert.AreEqual(3, texture.width); Assert.AreEqual(2, texture.height);
                CollectionAssert.AreEqual(pixels, texture.GetPixels32());
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void NearestSeedsAssignsEveryTexelToItsClosestSeed()
        {
            var seed = new bool[16]; seed[0] = true; seed[15] = true;
            var nearest = RemeshBaker.NearestSeeds(seed, 4, CancellationToken.None);
            Assert.AreEqual(0, nearest[0]); Assert.AreEqual(0, nearest[1]); Assert.AreEqual(0, nearest[4]);
            Assert.AreEqual(15, nearest[15]); Assert.AreEqual(15, nearest[14]); Assert.AreEqual(15, nearest[11]);
            foreach (var n in nearest) Assert.That(n, Is.GreaterThanOrEqualTo(0));
            // No seeds at all: everything stays unassigned.
            foreach (var n in RemeshBaker.NearestSeeds(new bool[16], 4, CancellationToken.None)) Assert.AreEqual(-1, n);
        }
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator TextureReadbackWaitCancelsWithoutWaitingForGpuCompletion()
        {
            var gpu = new System.Threading.Tasks.TaskCompletionSource<bool>();
            using (var cancellation = new CancellationTokenSource()) {
                var wait = GpuReadback.AwaitReadbacks(gpu.Task, cancellation.Token);
                Assert.IsFalse(wait.IsCompleted);
                cancellation.Cancel();
                var deadline = EditorApplication.timeSinceStartup + 3;
                while (!wait.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(wait.IsCanceled, "Cancel must release the AO panel while GPU readback is still pending");
                Assert.IsFalse(gpu.Task.IsCompleted, "GPU callbacks still own their cleanup");
                gpu.SetException(new InvalidOperationException("Late GPU failure"));
                yield return null;
                Assert.IsTrue(wait.IsCanceled);
            }
        }

        [Test]
        public void TextureReadbackFailureStillReachesTheCaller()
        {
            var failure = new InvalidOperationException("GPU readback failed");
            var readback = System.Threading.Tasks.Task.FromException(failure);
            var wait = GpuReadback.AwaitReadbacks(readback, CancellationToken.None);
            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() => wait.GetAwaiter().GetResult()));
            var observer = GpuReadback.ObserveFailure(readback);
            Assert.IsTrue(observer.IsCompleted);
            Assert.DoesNotThrow(() => observer.GetAwaiter().GetResult());
        }

    }
}
