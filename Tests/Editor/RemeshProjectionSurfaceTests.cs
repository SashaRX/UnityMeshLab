using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshProjectionSurfaceTests
    {
        static void Layers(float scale, float[] depths, out Vector3[] positions, out int[] indices)
        {
            positions = new Vector3[depths.Length * 3]; indices = new int[positions.Length];
            for (int face = 0; face < depths.Length; ++face) {
                positions[face * 3] = new Vector3(0, 0, depths[face]) * scale;
                positions[face * 3 + 1] = new Vector3(1, 0, depths[face]) * scale;
                positions[face * 3 + 2] = new Vector3(0, 1, depths[face]) * scale;
                for (int corner = 0; corner < 3; ++corner) indices[face * 3 + corner] = face * 3 + corner;
            }
        }

        [TestCase(1f)]
        [TestCase(.0001f)]
        public void ClosestTargetSelectsTheOwnSurfaceInsteadOfTheOuterSameFacingLayer(float scale)
        {
            Layers(scale, new[] { .2f, 0 }, out var positions, out var indices);
            var bvh = new TriangleBvh(positions, indices);
            var normals = new[] { Vector3.forward, Vector3.forward };
            Vector3 origin = new Vector3(.25f, .25f, 1) * scale;
            Assert.AreEqual(0, bvh.RaycastFacingFiltered(origin, Vector3.back, 2 * scale, normals).triangleIndex,
                "the outer layer remains the first hit for visibility/proxy queries");
            var hit = bvh.RaycastClosestToTarget(origin, Vector3.back, 2 * scale, scale, normals);
            Assert.AreEqual(1, hit.triangleIndex, "an exact target-surface hit must beat the intercepted outer layer");
            Assert.That(hit.t / scale, Is.EqualTo(1).Within(1e-5));
            Assert.That((hit.barycentric - new Vector3(.5f, .25f, .25f)).magnitude, Is.LessThan(1e-5));
        }

        [Test]
        public void SegmentRankingFindsTheNearestLayerOnEitherSideAcrossBvhBranches()
        {
            var depths = new float[41];
            for (int face = 0; face < depths.Length; ++face) depths[face] = -.8f + face * .04f;
            Layers(1, depths, out var positions, out var indices);
            var bvh = new TriangleBvh(positions, indices);
            Vector3 origin = new Vector3(.15f, .2f, 1.2f), direction = new Vector3(.13f, .07f, -1).normalized;
            for (int query = 0; query < 57; ++query) {
                float preferred = .1f + query * .045f;
                int expected = -1; float expectedT = 0, distance = float.MaxValue;
                for (int face = 0; face < depths.Length; ++face) {
                    float t = (depths[face] - origin.z) / direction.z;
                    float candidate = Mathf.Abs(t - preferred);
                    if (candidate < distance) { expected = face; expectedT = t; distance = candidate; }
                }
                var hit = bvh.RaycastClosestToTarget(origin, direction, 3, preferred);
                Assert.AreEqual(expected, hit.triangleIndex, "analytic parallel-plane intersection " + query);
                Assert.That(hit.t, Is.EqualTo(expectedT).Within(2e-6));
            }
        }

        [TestCase(1f)]
        [TestCase(.0001f)]
        public void TinyNonzeroDirectionEntersTheTargetSlabAtThePreferredSegmentCenter(float scale)
        {
            Layers(scale, new[] { 0f }, out var positions, out var indices);
            var bvh = new TriangleBvh(positions, indices);
            // Both origins lie outside the planar triangle's bounds. At t=scale
            // the tiny component enters by 1e-8*scale. This positive margin avoids
            // treating float input rounding at an exact boundary as a slab failure.
            foreach (bool xAxis in new[] { true, false }) {
                var origin = (xAxis ? new Vector3(-4e-8f, .25f, 1) : new Vector3(.25f, -4e-8f, 1)) * scale;
                var direction = xAxis ? new Vector3(5e-8f, 0, -1) : new Vector3(0, 5e-8f, -1);
                var expected = xAxis ? new Vector3(.75f - 1e-8f, 1e-8f, .25f) : new Vector3(.75f - 1e-8f, .25f, 1e-8f);
                var hit = bvh.RaycastClosestToTarget(origin, direction, 2 * scale, scale);
                Assert.AreEqual(0, hit.triangleIndex, "a nonzero component must not be treated as parallel; axis=" + (xAxis ? "x" : "y"));
                Assert.That(hit.t / scale, Is.EqualTo(1).Within(1e-6));
                Assert.That((hit.barycentric - expected).magnitude, Is.LessThan(1e-6));
                Assert.That(xAxis ? hit.barycentric.y : hit.barycentric.z, Is.GreaterThan(0));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TargetRankingPreservesBackfaceEligibilityAndTwoSidedExceptions(bool twoSided)
        {
            Layers(1, new[] { .1f, 0 }, out var positions, out var indices);
            indices[4] = 5; indices[5] = 4; // the exact target layer faces the opposite way
            var bvh = new TriangleBvh(positions, indices);
            var normals = new[] { Vector3.forward, Vector3.back };
            var hit = bvh.RaycastClosestToTarget(new Vector3(.25f, .25f, 1), Vector3.back, 2, 1,
                normals, new[] { false, twoSided });
            Assert.AreEqual(twoSided ? 1 : 0, hit.triangleIndex);
            Assert.That(hit.t, Is.EqualTo(twoSided ? 1 : .9f).Within(1e-6));
        }

        [Test]
        public void NonpositivePreferenceKeepsLegacyFirstHitAndTargetTiesPreferTheOuterSurface()
        {
            Layers(1, new[] { -.25f, .25f, .25f }, out var positions, out var indices);
            var bvh = new TriangleBvh(positions, indices);
            var normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            Vector3 origin = new Vector3(.25f, .25f, 1);
            foreach (float preferred in new[] { 0f, -1f }) {
                var old = bvh.RaycastFacingFiltered(origin, Vector3.back, 2, normals);
                var hit = bvh.RaycastClosestToTarget(origin, Vector3.back, 2, preferred, normals);
                Assert.AreEqual(old.triangleIndex, hit.triangleIndex); Assert.AreEqual(old.t, hit.t);
                Assert.AreEqual(old.barycentric, hit.barycentric);
                Assert.AreEqual(bvh.Raycast(origin, Vector3.back, 2).triangleIndex,
                    bvh.RaycastClosestToTarget(origin, Vector3.back, 2, preferred).triangleIndex);
            }
            Assert.AreEqual(1, bvh.RaycastClosestToTarget(origin, Vector3.back, 2, 1, normals).triangleIndex,
                "equal target distances prefer the outer intersection, then the smaller face index");
            Assert.AreEqual(-1, bvh.RaycastClosestToTarget(new Vector3(2, 2, 1), Vector3.back, 2, 1).triangleIndex);
            Assert.AreEqual(-1, bvh.RaycastClosestToTarget(origin, Vector3.back, 0, 1).triangleIndex);
        }

        [TestCase(1f)]
        [TestCase(.0001f)]
        public void LayeredBakeTransfersTheTargetsColorRatherThanTheOuterLayersColor(float scale)
        {
            Layers(scale, new[] { .2f, 0 }, out var positions, out var indices);
            var green = new Color(.05f, .7f, .1f, 1);
            var source = new RemeshSource {
                positions = positions, indices = indices, diagonal = Mathf.Sqrt(2.04f) * scale,
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward },
                tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1),
                    new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) },
                colors = new[] { Color.red, Color.red, Color.red, green, green, green }, hasColors = true,
                faceMaterials = new[] { 0, 0 }, materials = new[] { new RemeshSource.Surface {
                    color = new RemeshSource.Map(), normal = new RemeshSource.Map(), metal = new RemeshSource.Map(),
                    ao = new RemeshSource.Map(), emission = new RemeshSource.Map(), tint = Color.white,
                    emissionTint = Color.black, normalScale = 1, aoStrength = 1 } }
            };
            var target = new RemeshNative.Geometry {
                positions = new[] { positions[3], positions[4], positions[5] }, indices = new[] { 0, 1, 2 },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) },
                uv = new[] { new Vector2(8, 8) / 64, new Vector2(56, 8) / 64, new Vector2(8, 56) / 64 }
            };
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 1, dilationRadius = 0, bakeSamples = 1,
                projectionDistance = .5f, cageFit = false, vertexColorTint = true }, CancellationToken.None);
            Assert.AreEqual(0, maps.misses); Assert.That(maps.covered, Is.GreaterThan(0));
            Color actual = ((Color)maps.color[16 * 64 + 16]).linear;
            Assert.That(actual.r, Is.EqualTo(green.r).Within(.006));
            Assert.That(actual.g, Is.EqualTo(green.g).Within(.006));
            Assert.That(actual.b, Is.EqualTo(green.b).Within(.006));
        }

        static IEnumerator Await(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "GPU projection query must complete without blocking the Editor");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString()); Assert.IsFalse(task.IsCanceled);
        }

        [UnityTest]
        public IEnumerator GpuTinyNonzeroDirectionEntersTheSlabLikeCpuAtUnitAndTinyScale()
        {
            if (!GpuBvh.Supported || !SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Async GPU queries unavailable on this device.");
            foreach (float scale in new[] { 1f, .0001f }) {
                Layers(scale, new[] { 0f }, out var positions, out var indices);
                var bvh = new TriangleBvh(positions, indices);
                using (var gpu = GpuBvh.TryCreate(bvh, new[] { Vector3.forward })) {
                    Assert.IsNotNull(gpu, "the actual GPU target traversal must run");
                    var origins = new[] {
                        new Vector4(-4e-8f * scale, .25f * scale, scale, 2 * scale),
                        new Vector4(.25f * scale, -4e-8f * scale, scale, 2 * scale)
                    };
                    var directions = new[] { new Vector4(5e-8f, 0, -1, scale), new Vector4(0, 5e-8f, -1, scale) };
                    var hits = new GpuBvh.RayHit[origins.Length];
                    yield return Await(gpu.RaycastAsync(origins, directions, origins.Length, true, hits, CancellationToken.None));
                    for (int query = 0; query < origins.Length; ++query) {
                        var expected = query == 0 ? new Vector3(.75f - 1e-8f, 1e-8f, .25f) : new Vector3(.75f - 1e-8f, .25f, 1e-8f);
                        var cpu = bvh.RaycastClosestToTarget(origins[query], directions[query], origins[query].w, directions[query].w);
                        Assert.AreEqual(0, cpu.triangleIndex);
                        Assert.AreEqual(0, hits[query].tri, "tiny nonzero slab component must enter the target triangle; scale=" + scale + ", query=" + query);
                        Assert.That(hits[query].t / scale, Is.EqualTo(1).Within(1e-6));
                        Assert.That(hits[query].u, Is.EqualTo(expected.y).Within(1e-6));
                        Assert.That(hits[query].v, Is.EqualTo(expected.z).Within(1e-6));
                        Assert.That(query == 0 ? hits[query].u : hits[query].v, Is.GreaterThan(0));
                        Assert.That((cpu.barycentric - expected).magnitude, Is.LessThan(1e-6));
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator GpuTargetRankingMatchesCpuIncludingLegacyRaysBackfacesAndTinyGeometry()
        {
            if (!GpuBvh.Supported || !SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Async GPU queries unavailable on this device.");
            foreach (float scale in new[] { 1f, .0001f })
            foreach (bool twoSided in new[] { false, true }) {
                Layers(scale, new[] { .3f, 0, -.1f, -.25f }, out var positions, out var indices);
                indices[4] = 5; indices[5] = 4;
                var normals = new[] { Vector3.forward, Vector3.back, Vector3.forward, Vector3.forward };
                var either = new[] { false, twoSided, false, false };
                var bvh = new TriangleBvh(positions, indices);
                using (var gpu = GpuBvh.TryCreate(bvh, normals, either)) {
                    Assert.IsNotNull(gpu, "the compute shader must compile and the GPU backend must actually run");
                    var origins = new[] {
                        new Vector4(.25f * scale, .25f * scale, scale, 2 * scale),
                        new Vector4(.25f * scale, .25f * scale, scale, 2 * scale),
                        new Vector4(.25f * scale, .25f * scale, scale, 2 * scale),
                        new Vector4(2 * scale, 2 * scale, scale, 2 * scale),
                        new Vector4(.25f * scale, .25f * scale, -.1f * scale, scale)
                    };
                    var dirs = new[] { new Vector4(0, 0, -1, scale), new Vector4(0, 0, -1, 0),
                        new Vector4(0, 0, -1, 1.2f * scale), new Vector4(0, 0, -1, scale), new Vector4(0, 0, -1, .01f * scale) };
                    foreach (bool filtered in new[] { false, true }) {
                        var hits = new GpuBvh.RayHit[origins.Length];
                        yield return Await(gpu.RaycastAsync(origins, dirs, origins.Length, filtered, hits, CancellationToken.None));
                        for (int query = 0; query < origins.Length; ++query) {
                            var cpu = bvh.RaycastClosestToTarget(origins[query], dirs[query], origins[query].w, dirs[query].w,
                                filtered ? normals : null, filtered ? either : null);
                            Assert.AreEqual(cpu.triangleIndex, hits[query].tri, $"scale={scale}, filtered={filtered}, two-sided={twoSided}, query={query}");
                            if (cpu.triangleIndex < 0) continue;
                            Assert.That(hits[query].t / scale, Is.EqualTo(cpu.t / scale).Within(1e-5));
                            Assert.That(hits[query].u, Is.EqualTo(cpu.barycentric.y).Within(1e-5));
                            Assert.That(hits[query].v, Is.EqualTo(cpu.barycentric.z).Within(1e-5));
                        }
                        Assert.AreEqual(filtered && !twoSided ? 2 : 1, hits[0].tri);
                        Assert.AreEqual(0, hits[1].tri, "preferredT=0 still means the outer first hit");
                        Assert.AreEqual(2, hits[4].tri, "zero-distance surface hits must not be skipped by an epsilon offset");
                    }
                }
            }
        }
    }
}
