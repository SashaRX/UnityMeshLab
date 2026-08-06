// XatlasRepackTests.cs — exercises XatlasRepack.RepackSingle with synthetic
// tiled-UV0 meshes. Verifies the current pipeline contract:
//   1. Every tile-instance shell ends up with a distinct UV2 region (UV2 is
//      a unique-per-shell channel for lightmap baking; the legacy
//      mergeOverlappingTiles mode that shared UV2 across tiles was removed).
//   2. mesh.uv (UV0) is never mutated by the repack pipeline.
//
// The native xatlas plugin is loaded at runtime; if the DLL is missing
// (CI Linux without xatlas built), the tests are explicitly skipped.

using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class XatlasRepackTests
    {
        static bool s_nativeAvailable;
        static bool s_nativeProbed;

        static bool NativeAvailable()
        {
            if (s_nativeProbed) return s_nativeAvailable;
            s_nativeProbed = true;
            try
            {
                XatlasNative.xatlasCreate();
                XatlasNative.xatlasDestroy();
                s_nativeAvailable = true;
            }
            catch (System.DllNotFoundException)
            {
                s_nativeAvailable = false;
            }
            catch (System.EntryPointNotFoundException)
            {
                s_nativeAvailable = false;
            }
            return s_nativeAvailable;
        }

        static Mesh BuildTiledMesh(int tileCount, float tileSize = 0.3f)
        {
            // N identical quads, each a topologically-disconnected 4-vertex patch,
            // all with the same UV0 bbox (= stacked tiles in UV).
            var verts = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();
            for (int t = 0; t < tileCount; t++)
            {
                // 3D positions translated so each tile is distinct geometry
                float ox = t * 1.0f;
                int v0 = verts.Count;
                verts.Add(new Vector3(ox, 0, 0));
                verts.Add(new Vector3(ox + 1, 0, 0));
                verts.Add(new Vector3(ox + 1, 1, 0));
                verts.Add(new Vector3(ox, 1, 0));
                // Same UV0 for every tile
                uvs.Add(new Vector2(0.1f, 0.1f));
                uvs.Add(new Vector2(0.1f + tileSize, 0.1f));
                uvs.Add(new Vector2(0.1f + tileSize, 0.1f + tileSize));
                uvs.Add(new Vector2(0.1f, 0.1f + tileSize));
                tris.AddRange(new[] { v0, v0 + 1, v0 + 2,  v0, v0 + 2, v0 + 3 });
            }
            var mesh = new Mesh { name = $"Tiled_{tileCount}" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris.ToArray(), 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        [Test]
        public void Tiles_GetDistinctUv2Regions()
        {
            if (!NativeAvailable()) Assert.Ignore("xatlas native plugin not available");

            const int tileCount = 5;
            var mesh = BuildTiledMesh(tileCount);
            try
            {
                var opts = RepackOptions.Default;
                opts.resolution = 512;
                opts.padding = 2;

                var result = XatlasRepack.RepackSingle(mesh, opts);
                Assert.IsTrue(result.ok, $"Repack failed: {result.error}");

                var uv2 = mesh.uv2;
                Assert.AreEqual(tileCount * 4, uv2.Length);

                // Each tile should land in a distinct atlas region — perturb +
                // pre-pack normalisation must keep xatlas from collapsing
                // identical input UVs onto the same slot.
                var centroids = new Vector2[tileCount];
                for (int t = 0; t < tileCount; t++)
                {
                    int b = t * 4;
                    centroids[t] = (uv2[b] + uv2[b + 1] + uv2[b + 2] + uv2[b + 3]) * 0.25f;
                }

                int distinctPairs = 0;
                for (int i = 0; i < tileCount; i++)
                    for (int j = i + 1; j < tileCount; j++)
                        if ((centroids[i] - centroids[j]).sqrMagnitude > 0.0001f) distinctPairs++;

                int totalPairs = tileCount * (tileCount - 1) / 2;
                Assert.GreaterOrEqual(distinctPairs, totalPairs / 2,
                    "At least half of the tile centroids should be distinct (pipeline must not collapse tile-instances onto a shared UV2 slot).");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void RepackSingle_DoesNotModifyUv0()
        {
            if (!NativeAvailable()) Assert.Ignore("xatlas native plugin not available");

            var mesh = BuildTiledMesh(3);
            try
            {
                var uv0Before = mesh.uv;
                Assert.IsNotNull(uv0Before);

                var opts = RepackOptions.Default;
                opts.resolution = 256;
                opts.padding = 2;

                var result = XatlasRepack.RepackSingle(mesh, opts);
                Assert.IsTrue(result.ok);

                var uv0After = mesh.uv;
                Assert.AreEqual(uv0Before.Length, uv0After.Length);
                for (int i = 0; i < uv0Before.Length; i++)
                    Assert.AreEqual(uv0Before[i], uv0After[i], $"UV0 vertex {i} modified — pipeline contract violation");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void PackPreflight_DisablesBruteForce_WhenInternalOversampleIsAboveOne()
        {
            var method = typeof(XatlasRepack).GetMethod(
                "ResolvePackBruteForce",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "XatlasRepack should expose pack preflight as a testable helper");

            object[] args = { 1, 4, 149, (uint)1024, null };
            int resolved = (int)method.Invoke(null, args);

            Assert.AreEqual(0, resolved,
                "Oversampled packs should use the xatlas heuristic packer even when the UI brute-force toggle is enabled.");
            StringAssert.Contains("oversample", (string)args[4]);
        }

        [Test]
        public void PackPreflight_RejectsOverflowingOversampledDimensions()
        {
            var method = typeof(XatlasRepack).GetMethod(
                "TryResolveInternalPackDimensions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "XatlasRepack should validate oversampled dimensions before native packing");

            var opts = RepackOptions.Default;
            opts.resolution = (uint)int.MaxValue;
            opts.internalOversample = 4;
            object[] args = { opts, 0, (uint)0, (uint)0 };

            Assert.IsFalse((bool)method.Invoke(null, args));
            Assert.AreEqual(0u, (uint)args[2]);
            Assert.AreEqual(0u, (uint)args[3]);
        }

        [Test]
        public void PackCost_SaturatesInsteadOfOverflowing()
        {
            var method = typeof(XatlasRepack).GetMethod(
                "ComputePackCost",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "XatlasRepack should expose pack cost calculation as a testable helper");

            long cost = (long)method.Invoke(null, new object[] { 1, uint.MaxValue });

            Assert.AreEqual(long.MaxValue, cost);
        }
    }

    public class GroupedShellTransferTests
    {
        [Test]
        public void CancelTransfer_InvalidatesPartialUv2Result()
        {
            var method = typeof(GroupedShellTransfer).GetMethod(
                "CancelTransfer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "GroupedShellTransfer should invalidate partial results on cancellation");

            var partial = new GroupedShellTransfer.TransferResult
            {
                uv2 = new Vector2[4],
                verticesTotal = 4
            };
            var cancelled = (GroupedShellTransfer.TransferResult)method.Invoke(null, new object[] { partial });

            Assert.AreSame(partial, cancelled);
            Assert.IsNull(cancelled.uv2,
                "Cancelled transfers must use the null-UV2 failure contract expected by transfer callers.");
        }

        [Test]
        public void Uv2PixelMargin_ScalesFromResolvedAtlasSize()
        {
            var method = typeof(GroupedShellTransfer).GetMethod(
                "ComputeUv2PixelMargin",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "GroupedShellTransfer should scale UV2 pixel margins from the resolved atlas size");

            object[] args = { 1389, 1360, 1.25f, 0.005f };
            float margin = (float)method.Invoke(null, args);

            Assert.AreEqual(1.25f / 1360f, margin, 1e-6f);
            Assert.Less(margin, 0.005f,
                "A margin tuned for a 256px atlas must shrink when the resolved atlas grows past 1k.");
        }
    }

    public class LightmapTransferToolUiTests
    {
        static bool ValidateSweep(TestSuiteAsset.SweepMatrix sweep, out int cells, out string error)
        {
            var method = typeof(LightmapTransferTool).GetMethod(
                "TryValidateSweep",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "Sweep validation should remain available as a testable preflight");

            object[] args = { sweep, new UvToolContext(), 0, null };
            bool valid = (bool)method.Invoke(null, args);
            cells = (int)args[2];
            error = (string)args[3];
            return valid;
        }

        [Test]
        public void SweepValidation_RejectsUnsafeNativePackingValues()
        {
            var sweep = new TestSuiteAsset.SweepMatrix
            {
                shellPaddingPxVariants = new[] { -1 },
            };

            Assert.IsFalse(ValidateSweep(sweep, out int cells, out string error));
            Assert.AreEqual(0, cells);
            StringAssert.Contains("shell padding", error);
        }

        [Test]
        public void SweepValidation_RejectsExcessiveCartesianProduct()
        {
            var sweep = new TestSuiteAsset.SweepMatrix
            {
                atlasResolutions = new[] { 64, 128, 256, 512, 1024, 2048, 4096 },
                shellPaddingPxVariants = new[] { 0, 1, 2, 3, 4, 5, 6 },
                borderPaddingPxVariants = new[] { 0, 1, 2, 3, 4, 5 },
            };

            Assert.IsFalse(ValidateSweep(sweep, out int cells, out string error));
            Assert.AreEqual(0, cells);
            StringAssert.Contains("maximum is 256", error);
        }

        [Test]
        public void SweepValidation_AcceptsDefaultsAndCountsCellsSafely()
        {
            Assert.IsTrue(ValidateSweep(new TestSuiteAsset.SweepMatrix(),
                out int cells, out string error), error);
            Assert.AreEqual(24, cells);
        }

        [Test]
        public void BruteForceOption_IsUnavailable_WhenInternalOversampleIsAboveOne()
        {
            var method = typeof(LightmapTransferTool).GetMethod(
                "IsBruteForcePackAvailable",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "LightmapTransferTool should expose the brute-force UI availability rule as a testable helper");

            Assert.IsTrue((bool)method.Invoke(null, new object[] { 1 }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { 0 }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { 2 }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { 4 }));
        }

        [Test]
        public void TransferTargetDetection_IgnoresSourceOnlySelection()
        {
            var method = typeof(LightmapTransferTool).GetMethod(
                "HasIncludedTransferTargets",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "LightmapTransferTool should expose target detection as a testable helper");

            var sourceOnly = new List<MeshEntry>
            {
                new MeshEntry { lodIndex = 0, include = true, originalMesh = new Mesh() }
            };
            try
            {
                Assert.IsFalse((bool)method.Invoke(null, new object[] { sourceOnly, 0 }));

                sourceOnly.Add(new MeshEntry { lodIndex = 1, include = false, originalMesh = new Mesh() });
                Assert.IsFalse((bool)method.Invoke(null, new object[] { sourceOnly, 0 }));

                sourceOnly[1].include = true;
                Assert.IsTrue((bool)method.Invoke(null, new object[] { sourceOnly, 0 }));
            }
            finally
            {
                foreach (var e in sourceOnly)
                    Object.DestroyImmediate(e.originalMesh);
            }
        }

        [Test]
        public void ApplyUv2_IsAvailable_AfterSourceOnlyRepack()
        {
            var method = typeof(LightmapTransferTool).GetMethod(
                "CanApplyUv2",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "LightmapTransferTool should expose the Apply UV2 availability rule as a testable helper");

            Assert.IsTrue((bool)method.Invoke(null, new object[] { true, false }),
                "A source-only repack must remain applyable when transfer is skipped.");
            Assert.IsTrue((bool)method.Invoke(null, new object[] { false, true }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { false, false }));
        }

        [Test]
        public void PostPackDensityCorrection_KeepsEachSplitChartCentroidFixed()
        {
            var method = typeof(XatlasRepack).GetMethod(
                "ApplyPostPackDensityCorrection",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method);

            var uv2 = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
                new Vector2(10, 0), new Vector2(11, 0), new Vector2(10, 1),
                new Vector2(4, 0), new Vector2(4.2f, 0), new Vector2(4, 0.2f),
                new Vector2(7, 0), new Vector2(7.2f, 0), new Vector2(7, 0.2f)
            };
            var positions = new Vector3[uv2.Length];
            for (int i = 0; i < positions.Length; i += 3)
            {
                positions[i] = Vector3.zero;
                positions[i + 1] = Vector3.right;
                positions[i + 2] = Vector3.up;
            }
            var tris = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
            var shells = new List<UvShell>
            {
                new UvShell { faceIndices = new List<int> { 0, 1 }, vertexIndices = new HashSet<int> { 0, 1, 2, 3, 4, 5 } },
                new UvShell { faceIndices = new List<int> { 2 }, vertexIndices = new HashSet<int> { 6, 7, 8 } },
                new UvShell { faceIndices = new List<int> { 3 }, vertexIndices = new HashSet<int> { 9, 10, 11 } }
            };
            var chartIds = new uint[] { 10, 10, 10, 20, 20, 20, 30, 30, 30, 40, 40, 40 };
            Vector2 firstCentroid = (uv2[0] + uv2[1] + uv2[2]) / 3f;
            Vector2 secondCentroid = (uv2[3] + uv2[4] + uv2[5]) / 3f;

            int modified = (int)method.Invoke(null,
                new object[] { uv2, tris, positions, shells, chartIds, "split-chart-test" });

            Assert.AreEqual(1, modified);
            Assert.That(Vector2.Distance(firstCentroid, (uv2[0] + uv2[1] + uv2[2]) / 3f), Is.LessThan(1e-5f));
            Assert.That(Vector2.Distance(secondCentroid, (uv2[3] + uv2[4] + uv2[5]) / 3f), Is.LessThan(1e-5f));
        }
    }
}
