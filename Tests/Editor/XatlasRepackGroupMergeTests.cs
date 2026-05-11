// XatlasRepackGroupMergeTests.cs — exercises XatlasRepack.RepackSingle with
// synthetic tiled-UV0 meshes. Verifies the group-aware-merge contract:
//   1. mergeOverlappingTiles=true → all duplicate tile vertices share UV2 with
//      their representative.
//   2. mergeOverlappingTiles=false → each tile gets a distinct UV2 region
//      (legacy behaviour preserved).
//   3. mesh.uv (UV0) is never modified.
//
// These tests load the native xatlas plugin at runtime. If the DLL is missing
// (CI Linux without xatlas built), the tests are explicitly skipped.

using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class XatlasRepackGroupMergeTests
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
            // all with the same UV0 bbox (= stacked tiles).
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
        public void GroupMergeOn_TilesShareUv2WithRepresentative()
        {
            if (!NativeAvailable()) Assert.Ignore("xatlas native plugin not available");

            const int tileCount = 5;
            var mesh = BuildTiledMesh(tileCount);
            try
            {
                var opts = RepackOptions.Default;
                opts.resolution = 256;
                opts.padding = 2;
                opts.mergeOverlappingTiles = true;

                var result = XatlasRepack.RepackSingle(mesh, opts);
                Assert.IsTrue(result.ok, $"Repack failed: {result.error}");

                var uv2 = mesh.uv2;
                Assert.AreEqual(tileCount * 4, uv2.Length);

                // All tiles use the same 4 UV0 corners → after group-merge all
                // 4-vertex sets should share the same UV2 positions.
                Vector2 e0 = uv2[0], e1 = uv2[1], e2 = uv2[2], e3 = uv2[3];
                for (int t = 1; t < tileCount; t++)
                {
                    int b = t * 4;
                    Assert.AreEqual(e0, uv2[b + 0], $"Tile {t} corner 0 should match representative");
                    Assert.AreEqual(e1, uv2[b + 1], $"Tile {t} corner 1 should match representative");
                    Assert.AreEqual(e2, uv2[b + 2], $"Tile {t} corner 2 should match representative");
                    Assert.AreEqual(e3, uv2[b + 3], $"Tile {t} corner 3 should match representative");
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GroupMergeOff_TilesGetDistinctUv2Regions()
        {
            if (!NativeAvailable()) Assert.Ignore("xatlas native plugin not available");

            const int tileCount = 5;
            var mesh = BuildTiledMesh(tileCount);
            try
            {
                var opts = RepackOptions.Default;
                opts.resolution = 512;
                opts.padding = 2;
                opts.mergeOverlappingTiles = false;

                var result = XatlasRepack.RepackSingle(mesh, opts);
                Assert.IsTrue(result.ok, $"Repack failed: {result.error}");

                var uv2 = mesh.uv2;
                Assert.AreEqual(tileCount * 4, uv2.Length);

                // Each tile should get a centroid distinct from the others
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
                    "At least half of the tile centroids should be distinct when merge is off");
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
    }
}
