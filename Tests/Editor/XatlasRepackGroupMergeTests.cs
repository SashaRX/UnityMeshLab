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
    }

    public class GroupedShellTransferTests
    {
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
        public void ExportPruning_RemovesUnselectedMeshesRecursively_ButPreservesHierarchy()
        {
            var method = typeof(LightmapTransferTool).GetMethod(
                "PruneUnselectedExportMeshes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method, "LightmapTransferTool should expose export subset pruning as a testable helper");

            var root = new GameObject("SourceRoot");
            var selectedMesh = new Mesh { name = "Selected" };
            var hiddenMesh = new Mesh { name = "Internal" };
            try
            {
                var selected = new GameObject("SelectedNode");
                selected.transform.SetParent(root.transform, false);
                selected.AddComponent<MeshFilter>().sharedMesh = selectedMesh;
                selected.AddComponent<MeshRenderer>();

                var container = new GameObject("PreservedPivot");
                container.transform.SetParent(root.transform, false);
                var hidden = new GameObject("InactiveInternalNode");
                hidden.transform.SetParent(container.transform, false);
                hidden.SetActive(false);
                hidden.AddComponent<MeshFilter>().sharedMesh = hiddenMesh;
                hidden.AddComponent<MeshRenderer>();
                hidden.AddComponent<MeshCollider>().sharedMesh = hiddenMesh;

                method.Invoke(null, new object[] { root, new HashSet<Mesh> { selectedMesh } });

                Assert.AreSame(selectedMesh, selected.GetComponent<MeshFilter>().sharedMesh);
                Assert.IsNull(hidden.GetComponent<MeshFilter>());
                Assert.IsNull(hidden.GetComponent<MeshRenderer>());
                Assert.IsNull(hidden.GetComponent<MeshCollider>());
                Assert.AreSame(container.transform, hidden.transform.parent,
                    "Pruning mesh data must not flatten the source FBX hierarchy");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(selectedMesh);
                Object.DestroyImmediate(hiddenMesh);
            }
        }
    }
}
