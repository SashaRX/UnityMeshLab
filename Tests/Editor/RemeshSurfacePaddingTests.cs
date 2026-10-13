using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshSurfacePaddingTests
    {
        static readonly CancellationToken Token = CancellationToken.None;

        static RemeshBaker.Context RequestContext(int grid, int budget)
        {
            const int size = 16;
            var target = new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.zero, Vector3.up, Vector3.forward },
                indices = new[] { 0, 1, 2, 3, 4, 5 },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.right, Vector3.right, Vector3.right },
                uv = new[] { new Vector2(2, 2), new Vector2(14, 2), new Vector2(2, 14),
                    new Vector2(14, 14), new Vector2(14, 2), new Vector2(2, 14) }
            };
            for (int i = 0; i < target.uv.Length; ++i) target.uv[i] /= size;
            var receivers = new int[size * size];
            for (int pixel = 0; pixel < receivers.Length; ++pixel)
                receivers[pixel] = pixel % 11 == 0 ? -1 : pixel % size < 8 ? 0 : 1;
            return new RemeshBaker.Context { size = size, bandRows = 3, queryBudget = budget,
                offsets = new Vector2[grid * grid], owners = new int[receivers.Length], receivers = receivers,
                target = target, footprint = new RemeshTexelFootprint(target, new[] { -1, 5, -1, -1, -1, 1 }, size),
                proxy = true, faceDirs = new[] { Vector3.forward, Vector3.right }, depth = 2 };
        }

        [TestCase(1, 5, false)]
        [TestCase(2, 7, false)]
        [TestCase(4, 31, false)]
        [TestCase(4, 16384, false)]
        [TestCase(1, 5, true)]
        [TestCase(2, 7, true)]
        [TestCase(4, 31, true)]
        [TestCase(4, 16384, true)]
        public void ParallelRequestsMatchSerialFootprintsAcrossMidRowCutoffsAndCacheWrap(int grid, int budget, bool swapBands)
        {
            var reference = RequestContext(grid, budget);
            var expected = new List<RemeshBaker.Request>();
            var samples = new List<RemeshTexelFootprint.Sample>();
            for (int pixel = 0; pixel < reference.receivers.Length; ++pixel) {
                int receiver = reference.receivers[pixel];
                if (receiver < 0) continue;
                samples.Clear();
                reference.footprint.Gather(receiver, pixel % reference.size, pixel / reference.size, grid, samples, Token);
                foreach (var sample in samples) if (sample.area > 0)
                    expected.Add(new RemeshBaker.Request { pixel = pixel, sample = sample });
            }
            var context = RequestContext(grid, budget);
            var band = new RemeshBaker.Band(context);
            var spare = swapBands ? new RemeshBaker.Band(context, band) : band;
            int compared = 0, continued = 0, boundary = 0;
            for (int pixel = 0; pixel < context.owners.Length;) {
                RemeshBaker.BuildRequests(context, band, pixel, Token);
                Assert.That(band.nextPixel, Is.GreaterThan(pixel));
                Assert.That(band.rowStart[band.y1 - band.y0], Is.EqualTo(band.count));
                for (int i = 0; i < band.count; ++i) {
                    var wanted = expected[compared++];
                    var actual = band.requests[i];
                    Assert.AreEqual(wanted.pixel, actual.pixel);
                    Assert.AreEqual(wanted.sample, actual.sample, "sample order and transported frame must remain exact");
                    Assert.AreEqual(wanted.pixel, band.pixel[i]);
                    Assert.AreEqual(wanted.sample.face, band.face[i]);
                    Assert.AreEqual(wanted.sample.weights, band.weights[i]);
                    Assert.AreEqual(wanted.sample.area, band.area[i]);
                    Assert.AreEqual(wanted.sample.normalTransport, band.normalTransport[i]);
                    int row = wanted.pixel / context.size - band.y0;
                    Assert.That(i, Is.InRange(band.rowStart[row], band.rowStart[row + 1] - 1));
                    if (wanted.sample.boundary) ++boundary;
                    else if (wanted.sample.face != context.receivers[wanted.pixel]) ++continued;
                }
                pixel = band.nextPixel;
                var completed = band; band = spare; spare = completed;
            }
            Assert.AreEqual(expected.Count, compared);
            Assert.AreEqual(boundary, context.boundarySamples);
            Assert.AreEqual(continued, context.surfaceSamples);
            Assert.AreEqual(reference.footprint.NavigationFallbacks, context.footprint.NavigationFallbacks);
            Assert.AreEqual(reference.footprint.NavigationLimitHits, context.footprint.NavigationLimitHits);
            Assert.AreEqual(reference.footprint.LocalFaceLimitHits, context.footprint.LocalFaceLimitHits);
            Assert.AreEqual(reference.footprint.UnfoldOverlapTexels, context.footprint.UnfoldOverlapTexels);
        }

        [Test]
        public void ParallelFootprintCancellationDoesNotPublishARequestBand()
        {
            var context = RequestContext(4, 31);
            var band = new RemeshBaker.Band(context);
            using (var cancelled = new CancellationTokenSource()) {
                cancelled.Cancel();
                Assert.Throws<OperationCanceledException>(() => RemeshBaker.BuildRequests(context, band, 0, cancelled.Token));
                Assert.AreEqual(0, band.count);
                Assert.AreEqual(0, band.nextPixel);
            }
        }

        static RemeshNative.Geometry Fold(float scale = 1)
        {
            return new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right * scale, Vector3.up * scale, Vector3.forward * scale },
                indices = new[] { 0, 1, 2, 0, 2, 3 }
            };
        }

        static void AssertOnlyPair(int[] neighbours, int first, int second)
        {
            for (int slot = 0; slot < neighbours.Length; ++slot)
                Assert.AreEqual(slot == first ? second : slot == second ? first : -1, neighbours[slot], "edge slot " + slot);
        }

        static void AssertDisconnected(int[] neighbours)
        {
            foreach (int other in neighbours) Assert.AreEqual(-1, other, "untrusted edges must remain boundaries");
        }

        // Split every corner as an unwrap/normal operation does; retain source face
        // winding while independently reordering faces and cyclically rotating corners.
        static RemeshNative.Geometry Split(RemeshNative.Geometry geometry, params int[] faceAndRotation)
        {
            int count = faceAndRotation.Length / 2;
            var result = new RemeshNative.Geometry { positions = new Vector3[count * 3], indices = new int[count * 3] };
            for (int face = 0; face < count; ++face)
                for (int corner = 0; corner < 3; ++corner) {
                    int original = faceAndRotation[face * 2] * 3 + (corner + faceAndRotation[face * 2 + 1]) % 3;
                    result.positions[face * 3 + corner] = geometry.positions[geometry.indices[original]];
                    result.indices[face * 3 + corner] = face * 3 + corner;
                }
            return result;
        }

        [TestCase(1f)]
        [TestCase(.0001f)]
        [TestCase(1e-20f)]
        public void IndexedPhysicalEdgeSurvivesScaleAndUsesOppositeCornerSlots(float scale)
        {
            var geometry = Fold(scale);
            AssertOnlyPair(RemeshSurfaceTopology.Build(geometry.positions, geometry.indices, Token), 1, 5);
        }

        [Test]
        public void CoincidentSplitPositionsAreNotProofOfPhysicalConnectivity()
        {
            var original = Fold();
            var split = Split(original, 0, 0, 1, 0);
            AssertDisconnected(RemeshSurfaceTopology.Build(split.positions, split.indices, Token));
            // Only the original indexed mesh supplies the missing provenance.
            AssertOnlyPair(RemeshSurfaceTopology.Transfer(original.positions, original.indices, split, Token), 1, 5);
        }

        [TestCase(1f)]
        [TestCase(1e-20f)]
        public void TransferPreservesCyclicWindingAndReorderedSplitFaces(float scale)
        {
            var original = Fold(scale);
            var result = Split(original, 1, 1, 0, 2);
            AssertOnlyPair(RemeshSurfaceTopology.Transfer(original.positions, original.indices, result, Token), 1, 5);
        }

        [Test]
        public void TransferDoesNotInventLinksAfterDeletionReversalOrGeometryChange()
        {
            var original = Fold();
            AssertDisconnected(RemeshSurfaceTopology.Transfer(original.positions, original.indices, Split(original, 0, 0), Token));
            var reversed = Split(original, 0, 0, 1, 0);
            reversed.indices[4] = 5; reversed.indices[5] = 4;
            AssertDisconnected(RemeshSurfaceTopology.Transfer(original.positions, original.indices, reversed, Token));
            var changed = Split(original, 0, 0, 1, 0);
            changed.positions[5] += new Vector3(0, 0, 1e-6f);
            AssertDisconnected(RemeshSurfaceTopology.Transfer(original.positions, original.indices, changed, Token));
        }

        [Test]
        public void DuplicateOrientedFacesCannotSupplyUniqueTransferProvenance()
        {
            var original = Fold();
            var duplicates = Split(original, 0, 0, 1, 0, 0, 1);
            AssertDisconnected(RemeshSurfaceTopology.Transfer(original.positions, original.indices, duplicates, Token));
            var ambiguousOriginal = new RemeshNative.Geometry { positions = original.positions,
                indices = new[] { 0, 1, 2, 0, 2, 3, 0, 1, 2 } };
            AssertDisconnected(RemeshSurfaceTopology.Transfer(ambiguousOriginal.positions, ambiguousOriginal.indices,
                Split(original, 0, 0, 1, 0), Token));
        }

        [Test]
        public void SameDirectedNonmanifoldAndCoincidentBackSheetEdgesAreUntrusted()
        {
            var geometry = Fold();
            AssertDisconnected(RemeshSurfaceTopology.Build(geometry.positions, new[] { 0, 1, 2, 2, 0, 3 }, Token));
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.left };
            AssertDisconnected(RemeshSurfaceTopology.Build(positions, new[] { 0, 1, 2, 0, 2, 3, 0, 2, 4 }, Token));
            AssertDisconnected(RemeshSurfaceTopology.Build(geometry.positions, new[] { 0, 1, 2, 0, 2, 1 }, Token));
        }

        [Test]
        public void InvalidAdjacentFacesDoNotBecomeWalkableEdges()
        {
            var geometry = Fold();
            foreach (var invalid in new[] { Vector3.up * 2, new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0) }) {
                var positions = (Vector3[])geometry.positions.Clone(); positions[3] = invalid;
                AssertDisconnected(RemeshSurfaceTopology.Build(positions, geometry.indices, Token));
            }
            AssertDisconnected(RemeshSurfaceTopology.Build(geometry.positions, new[] { 0, 1, 2, 0, 2, 99 }, Token));
            AssertDisconnected(RemeshSurfaceTopology.Build(geometry.positions, new[] { 0, 1, 2, 0, 2, 0 }, Token));
        }

        [Test]
        public void TopologyRejectsMalformedArraysAndHonorsCancellation()
        {
            var geometry = Fold();
            Assert.Throws<ArgumentNullException>(() => RemeshSurfaceTopology.Build(null, geometry.indices, Token));
            Assert.Throws<ArgumentNullException>(() => RemeshSurfaceTopology.Build(geometry.positions, null, Token));
            Assert.Throws<ArgumentException>(() => RemeshSurfaceTopology.Build(geometry.positions, new[] { 0, 1 }, Token));
            Assert.IsEmpty(RemeshSurfaceTopology.Build(Array.Empty<Vector3>(), Array.Empty<int>(), Token));
            using (var cancelled = new CancellationTokenSource()) {
                cancelled.Cancel();
                Assert.Throws<OperationCanceledException>(() => RemeshSurfaceTopology.Build(geometry.positions, geometry.indices, cancelled.Token));
                Assert.Throws<OperationCanceledException>(() => RemeshSurfaceTopology.Transfer(geometry.positions, geometry.indices,
                    Split(geometry, 0, 0, 1, 0), cancelled.Token));
            }
        }

        static RemeshNative.Geometry TexelGeometry(Vector2[] texels, int[] indices, float scale = 1)
        {
            var positions = new Vector3[texels.Length]; var uv = new Vector2[texels.Length];
            for (int i = 0; i < texels.Length; ++i) {
                positions[i] = new Vector3(texels[i].x - 8, texels[i].y - 8, 0) * scale;
                uv[i] = texels[i] / 64;
            }
            return new RemeshNative.Geometry { positions = positions, indices = indices, uv = uv };
        }

        static float Area(List<RemeshTexelFootprint.Sample> samples, int face = -1, bool interiorOnly = false)
        {
            float sum = 0;
            foreach (var sample in samples)
                if ((face < 0 || sample.face == face) && (!interiorOnly || !sample.boundary)) sum += sample.area;
            return sum;
        }

        static Vector2 TexelPoint(RemeshNative.Geometry geometry, RemeshTexelFootprint.Sample sample)
        {
            int a = geometry.indices[sample.face * 3], b = geometry.indices[sample.face * 3 + 1], c = geometry.indices[sample.face * 3 + 2];
            return (geometry.uv[a] * sample.weights.x + geometry.uv[b] * sample.weights.y + geometry.uv[c] * sample.weights.z) * 64;
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        public void ClippedFootprintUsesPolygonAreaAndAreaCentroid(int grid)
        {
            // y <= 1 - .75*x clipped to [0,1]^2: area=5/8,
            // centroid=(2/5,7/20), unlike the four polygon vertices' mean.
            var geometry = TexelGeometry(new[] { new Vector2(8, 8), new Vector2(8 + 4f / 3, 8), new Vector2(8, 9) }, new[] { 0, 1, 2 });
            var footprint = new RemeshTexelFootprint(geometry, RemeshSurfaceTopology.Build(geometry.positions, geometry.indices, Token), 64);
            var samples = new List<RemeshTexelFootprint.Sample>();
            footprint.Gather(0, 8, 8, grid, samples, Token);
            Assert.That(Area(samples), Is.EqualTo(1).Within(2e-6f), "true boundary continuation completes the filter footprint");
            float covered = Area(samples, interiorOnly: true);
            Assert.That(covered, Is.EqualTo(.625f).Within(2e-6f));
            Vector2 moment = Vector2.zero;
            foreach (var sample in samples)
                if (!sample.boundary) moment += (TexelPoint(geometry, sample) - new Vector2(8, 8)) * sample.area;
            Assert.That(Vector2.Distance(moment / covered, new Vector2(.4f, .35f)), Is.LessThan(3e-6f), "an affine signal's integral needs the area centroid");
        }

        [TestCase(1, 1f)]
        [TestCase(2, 1f)]
        [TestCase(4, 1f)]
        [TestCase(1, .000001f)]
        public void TinyConnectedContributorIsIncludedEvenWithoutAStratifiedHit(int grid, float scale)
        {
            var geometry = TexelGeometry(new[] { new Vector2(8, 8), new Vector2(9, 8), new Vector2(9, 9),
                new Vector2(8.5f, 8.5f + 1f / 1024) }, new[] { 0, 1, 2, 0, 2, 3 }, scale);
            var footprint = new RemeshTexelFootprint(geometry, RemeshSurfaceTopology.Build(geometry.positions, geometry.indices, Token), 64);
            var samples = new List<RemeshTexelFootprint.Sample>();
            footprint.Gather(0, 8, 8, grid, samples, Token);
            Assert.That(Area(samples, 0, true), Is.EqualTo(.5f).Within(2e-6f));
            Assert.That(Area(samples, 1, true), Is.EqualTo(1f / 2048).Within(2e-7f), "positive clipped area must not disappear when every sample centre misses it");
            Assert.That(Area(samples), Is.EqualTo(1).Within(2e-6f));
            if (grid == 1) Assert.That(samples.Count, Is.GreaterThan(1), "one stratum can produce several true surface pieces plus its boundary remainder");
        }

        [TestCase(1)]
        [TestCase(4)]
        public void ContinuousChartKeepsItsAuthoredMetricAcrossBothTriangulations(int grid)
        {
            foreach (var indices in new[] { new[] { 0, 1, 2, 0, 2, 3 }, new[] { 0, 1, 3, 1, 2, 3 } }) {
                var geometry = TexelGeometry(new[] { new Vector2(8, 8), new Vector2(9, 8), new Vector2(9, 9), new Vector2(8, 9) }, indices);
                geometry.positions[3] = new Vector3(-1, 1, 0); // authored UVs intentionally differ from the intrinsic metric
                var footprint = new RemeshTexelFootprint(geometry, RemeshSurfaceTopology.Build(geometry.positions, geometry.indices, Token), 64);
                var samples = new List<RemeshTexelFootprint.Sample>(); footprint.Gather(0, 8, 8, grid, samples, Token);
                Assert.That(Area(samples, interiorOnly: true), Is.EqualTo(1).Within(2e-6f));
                Vector2 moment = Vector2.zero;
                foreach (var sample in samples) {
                    Assert.IsFalse(sample.boundary); moment += TexelPoint(geometry, sample) * sample.area;
                }
                Assert.That(Vector2.Distance(moment, new Vector2(8.5f, 8.5f)), Is.LessThan(3e-6f));
            }
        }

        static RemeshNative.Geometry FoldAtlas(float scale = 1)
        {
            var original = Fold(scale); var target = Split(original, 0, 0, 1, 0);
            target.uv = new[] { new Vector2(8.5f, 7), new Vector2(12.5f, 7), new Vector2(8.5f, 11),
                new Vector2(40, 40), new Vector2(40, 44), new Vector2(44, 40) };
            for (int i = 0; i < target.uv.Length; ++i) target.uv[i] /= 64;
            target.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.right, Vector3.right, Vector3.right };
            target.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1),
                new Vector4(0, 0, 1, -1), new Vector4(0, 0, 1, -1), new Vector4(0, 0, 1, -1) };
            target.surfaceNeighbors = RemeshSurfaceTopology.Transfer(original.positions, original.indices, target, Token);
            return target;
        }

        [TestCase(1f)]
        [TestCase(.000001f)]
        public void PhysicalFoldContinuationCrossesTheUvSeamAndKeepsReceiverFrameOnItsEdge(float scale)
        {
            var target = FoldAtlas(scale);
            var footprint = new RemeshTexelFootprint(target, target.surfaceNeighbors, 64);
            var samples = new List<RemeshTexelFootprint.Sample>();
            footprint.Gather(0, 7, 8, 1, samples, Token);
            Assert.That(Area(samples, 1, true), Is.EqualTo(1).Within(2e-6f));
            Vector3 moment = Vector3.zero;
            foreach (var sample in samples) {
                Assert.IsFalse(sample.boundary); Assert.AreEqual(1, sample.face);
                Assert.That(Vector3.Distance(sample.transport * Vector3.right, Vector3.forward), Is.LessThan(1e-5f), "donor geometric X is transported to initial receiver geometric Z");
                moment += sample.weights * sample.area;
            }
            Assert.That(Vector3.Distance(moment, new Vector3(.375f, .375f, .25f)), Is.LessThan(2e-5f));
            var receiver = footprint.ReceiverWeights(0, new Vector2(7.5f, 8.5f) / 64);
            Assert.That(Vector3.Distance(receiver, new Vector3(.625f, 0, .375f)), Is.LessThan(2e-6f), "the padding uses a receiving frame on A, not B's remote UV chart");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OpenPhysicalBoundaryCannotJumpToAnotherChartEvenAtCoincidentPositions(bool closeAtlas)
        {
            var target = FoldAtlas();
            // Split positions still touch in 3D, but deliberately remove original
            // indexed provenance. Atlas placement cannot supply that missing link.
            var neighbours = RemeshSurfaceTopology.Build(target.positions, target.indices, Token);
            AssertDisconnected(neighbours);
            if (closeAtlas) {
                target.uv[3] = new Vector2(7, 8) / 64;
                target.uv[4] = new Vector2(7, 9) / 64;
                target.uv[5] = new Vector2(8, 8) / 64;
            }
            var footprint = new RemeshTexelFootprint(target, neighbours, 64);
            var samples = new List<RemeshTexelFootprint.Sample>();
            footprint.Gather(0, 7, 8, 2, samples, Token);
            Assert.That(Area(samples), Is.EqualTo(1).Within(2e-6f));
            foreach (var sample in samples) {
                Assert.AreEqual(0, sample.face); Assert.IsTrue(sample.boundary);
                Assert.That(sample.weights.y, Is.EqualTo(0).Within(2e-6f));
            }
        }

        [Test]
        public void FootprintAppendsAndHonorsCancellation()
        {
            var target = FoldAtlas();
            var footprint = new RemeshTexelFootprint(target, target.surfaceNeighbors, 64);
            var samples = new List<RemeshTexelFootprint.Sample> { new RemeshTexelFootprint.Sample { face = -7, area = 123 } };
            footprint.Gather(0, 7, 8, 1, samples, Token);
            Assert.AreEqual(-7, samples[0].face); Assert.AreEqual(123, samples[0].area);
            using (var cancelled = new CancellationTokenSource()) {
                cancelled.Cancel();
                Assert.Throws<OperationCanceledException>(() => footprint.Gather(0, 7, 8, 1, samples, cancelled.Token));
            }
        }

        static RemeshSource FoldSource(float scale, out Vector3 edgeNormal, out Vector3 sideNormal)
        {
            var original = Split(Fold(scale), 0, 0, 1, 0);
            edgeNormal = new Vector3(.2f, .25f, Mathf.Sqrt(1 - .2f * .2f - .25f * .25f));
            sideNormal = new Vector3(.8f, .2f, Mathf.Sqrt(1 - .8f * .8f - .2f * .2f));
            // The same surface detail is folded with B's geometric normal. A
            // common WORLD normal on both planes would specify different detail.
            var fold = Quaternion.FromToRotation(Vector3.forward, Vector3.right);
            Vector3 foldedEdge = fold * edgeNormal, foldedSide = fold * sideNormal;
            return new RemeshSource {
                positions = original.positions, indices = original.indices,
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.up, Vector2.right },
                normals = new[] { edgeNormal, edgeNormal, edgeNormal, foldedEdge, foldedEdge, foldedSide },
                tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1),
                    new Vector4(0, 0, -1, 1), new Vector4(0, 0, -1, 1), new Vector4(0, 0, -1, 1) },
                colors = new[] { new Color(.4f, .2f, .6f, 1), new Color(.8f, .2f, .6f, 1), new Color(.4f, .2f, .6f, 1),
                    new Color(.4f, .2f, .6f, 1), new Color(.4f, .2f, .6f, 1), new Color(0, .2f, .6f, 1) },
                hasColors = true, faceMaterials = new[] { 0, 0 }, diagonal = Mathf.Sqrt(3) * scale,
                materials = new[] { new RemeshSource.Surface {
                    color = new RemeshSource.Map(), normal = new RemeshSource.Map(), metal = new RemeshSource.Map(),
                    ao = new RemeshSource.Map(), emission = new RemeshSource.Map(), tint = Color.white,
                    emissionTint = Color.black, normalScale = 1, aoStrength = 1, smoothness = .5f
                } }
            };
        }

        static void MirrorNeighborUv(RemeshNative.Geometry target)
        {
            target.uv[4] = new Vector2(40, 36) / 64;
            for (int i = 3; i < 6; ++i) target.tangents[i].w = 1;
        }

        [TestCase(1f, false)]
        [TestCase(1f, true)]
        [TestCase(.0001f, false)]
        public void BakeGutterSamplesTheConnectedSourceAndEncodesTransportedDetailInReceiverFrame(float scale, bool mirrored)
        {
            var source = FoldSource(scale, out var edgeNormal, out var sideNormal); var target = FoldAtlas(scale);
            if (mirrored) MirrorNeighborUv(target);
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 2, dilationRadius = 0, bakeSamples = 1, vertexColorTint = true }, Token);
            int pixel = 8 * 64 + 7;
            var color = ((Color)maps.color[pixel]).linear;
            Assert.That(color.r, Is.EqualTo(.3f).Within(.006f), "source r=.4+.4*(x-z), so continuation at z=.25 gives .3; copying the boundary gives .4");
            Assert.That(color.g, Is.EqualTo(.2f).Within(.006f));
            Assert.That(color.b, Is.EqualTo(.6f).Within(.006f));
            var encoded = (Color)maps.normal[pixel];
            var actual = new Vector3(encoded.r * 2 - 1, encoded.g * 2 - 1, encoded.b * 2 - 1).normalized;
            var expected = (edgeNormal * .75f + sideNormal * .25f).normalized;
            Assert.That(Vector3.Angle(actual, expected), Is.LessThan(1), "remove B's geometric bend before encoding its surface detail in A's frame");
            var detailOnB = (edgeNormal * .875f + sideNormal * .125f).normalized;
            int neighborPixel = (mirrored ? 39 : 40) * 64 + 40;
            Color neighborNormal = maps.normal[neighborPixel];
            var neighborTn = new Vector3(neighborNormal.r * 2 - 1, neighborNormal.g * 2 - 1, neighborNormal.b * 2 - 1).normalized;
            var expectedB = new Vector3(-detailOnB.x, mirrored ? -detailOnB.y : detailOnB.y, detailOnB.z);
            Assert.That(Vector3.Angle(neighborTn, expectedB), Is.LessThan(1), "mirroring B changes tangent handedness, not A's continued surface detail");
            Assert.That(maps.surfaceSamples, Is.GreaterThan(0), "connected continuation must actually run");
        }

        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(16, false)]
        [TestCase(16, true)]
        public void IdenticalFoldedSourceAndTargetKeepFlatNormalsAcrossSeamAndGutters(int samples, bool mirrored)
        {
            var source = FoldSource(1, out _, out _); var target = FoldAtlas();
            source.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.right, Vector3.right, Vector3.right };
            if (mirrored) MirrorNeighborUv(target);
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 2, dilationRadius = 0, bakeSamples = samples }, Token);
            foreach (int pixel in new[] { 8 * 64 + 7, 8 * 64 + 8, (mirrored ? 39 : 40) * 64 + 40 }) {
                Color packed = maps.normal[pixel];
                var n = new Vector3(packed.r * 2 - 1, packed.g * 2 - 1, packed.b * 2 - 1).normalized;
                Assert.That(Vector3.Angle(n, Vector3.forward), Is.LessThan(.5f), "geometric 90-degree bend must not become fake normal-map detail");
            }
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void SmoothUvSeamDoesNotRotateAConstantPhysicalSourceNormal(bool mirrored, bool urp, bool varyingShading)
        {
            var source = FoldSource(1, out _, out _); var target = FoldAtlas();
            var mode = urp ? RemeshNormalFrame.Mode.Urp : RemeshNormalFrame.Mode.BuiltIn;
            // A smooth shared normal need not lie in the plane spanned by these
            // two geometric face normals: the rest of a vertex fan contributes.
            var shadingNormal = new Vector3(.6f, .4f, .6f).normalized;
            var physicalNormal = new Vector3(.25f, .3f, 1).normalized;
            Array.Fill(target.normals, shadingNormal);
            Array.Fill(source.normals, physicalNormal);
            target.normalFrameMode = mode;
            if (mirrored) MirrorNeighborUv(target);
            if (varyingShading) {
                target.normals[1] = new Vector3(0, .1f, 1).normalized;
                target.normals[5] = new Vector3(1, .1f, 0).normalized;
            }
            for (int vertex = 0; vertex < target.tangents.Length; ++vertex) {
                var old = target.tangents[vertex];
                var tangent = Vector3.ProjectOnPlane((Vector3)old, target.normals[vertex]).normalized;
                target.tangents[vertex] = new Vector4(tangent.x, tangent.y, tangent.z, old.w);
            }
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 2, dilationRadius = 0, bakeSamples = 16 }, Token);
            var footprint = new RemeshTexelFootprint(target, target.surfaceNeighbors, 64);
            foreach (int x in new[] { 7, 8, 9 }) {
                int pixel = 8 * 64 + x;
                var weights = footprint.ReceiverFrameWeights(0, new Vector2(x + .5f, 8.5f) / 64);
                var frame = RemeshNormalFrame.Interpolate(target, target.tangents, 0, weights, mode);
                Assert.That(Vector3.Angle(frame.Decode(DecodeNormal(maps.normal[pixel])), physicalNormal), Is.LessThan(1),
                    "The gutter, seam texel and interior must agree with the same physical normal; x=" + x);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BilinearSmoothSeamPreservesPhysicalNormalWithVaryingReceiverFrame(bool urp)
        {
            var source = FoldSource(1, out _, out _); var target = FoldAtlas();
            var physical = new Vector3(.25f, .3f, 1).normalized;
            var shared = new Vector3(.6f, .4f, .6f).normalized;
            Array.Fill(source.normals, physical); Array.Fill(target.normals, shared);
            target.normals[1] = new Vector3(0, .1f, 1).normalized;
            target.normals[5] = new Vector3(1, .1f, 0).normalized;
            target.normalFrameMode = urp ? RemeshNormalFrame.Mode.Urp : RemeshNormalFrame.Mode.BuiltIn;
            for (int i = 0; i < 3; ++i) target.uv[i].x -= .25f / 64;
            for (int i = 0; i < target.tangents.Length; ++i) {
                var old = target.tangents[i]; var tangent = Vector3.ProjectOnPlane((Vector3)old, target.normals[i]).normalized;
                target.tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, old.w);
            }
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 2, dilationRadius = 0, bakeSamples = 16 }, Token);
            var colors = Array.ConvertAll(maps.normal, value => (Color)value);
            var uv = new Vector2(8.25f, 8.5f) / 64;
            var packed = SampleColor(colors, 64, uv, true);
            var tangentNormal = new Vector3(packed.r * 2 - 1, packed.g * 2 - 1, packed.b * 2 - 1).normalized;
            var frame = RemeshNormalFrame.Interpolate(target, target.tangents, 0, new Vector3(.625f, 0, .375f), target.normalFrameMode);
            Assert.That(Vector3.Angle(frame.Decode(tangentNormal), physical), Is.LessThan(1),
                "Filtering between the interior and gutter must preserve the continuous receiver frame at the edge.");
        }

        [Test]
        public void PartialSourceMissesRemainVisibleAfterSuccessfulSamplesAreRenormalized()
        {
            var source = FoldSource(1, out var sourceNormal, out _); var target = FoldAtlas();
            source.indices = new[] { 0, 1, 2 }; source.faceMaterials = new[] { 0 };
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 2, dilationRadius = 0, bakeSamples = 1,
                vertexColorTint = true, projectionDistance = 1e-5f }, Token);
            // Pixel 8,8 covers half A and half B. Only A exists on the source;
            // its centroid is x=.0625, so the surviving half has r=.425.
            Assert.That(((Color)maps.color[8 * 64 + 8]).linear.r, Is.EqualTo(.425f).Within(.006f));
            Assert.That(maps.partialMisses, Is.GreaterThan(0));
            Assert.That(maps.missedSampleArea, Is.GreaterThan(0));
            Assert.That(maps.misses, Is.GreaterThan(0));
            Assert.That(maps.gutterMisses, Is.GreaterThan(0));
            foreach (int pixel in new[] { 7 * 64 + 6, 7 * 64 + 7, 8 * 64 + 6, 8 * 64 + 7 }) {
                Assert.AreEqual(255, maps.color[pixel].a, "failed gutter projection still needs initialized filtering data");
                Assert.That(maps.color[pixel].g, Is.GreaterThan(0), "a successful A seed must replace the black/magenta hole");
                Assert.AreEqual(255, maps.normal[pixel].a);
                Assert.That(Vector3.Angle(DecodeNormal(maps.normal[pixel]), sourceNormal), Is.LessThan(1), "fallback normal stays in the receiving A frame");
            }
        }

        static Vector3 DecodeNormal(Color32 encoded)
        {
            Color color = encoded;
            return new Vector3(color.r * 2 - 1, color.g * 2 - 1, color.b * 2 - 1).normalized;
        }

        static System.Collections.IEnumerator Await(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 60;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "bake must finish within sixty seconds without blocking the Editor");
            Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
            Assert.IsFalse(task.IsCanceled);
        }

        static void RequireGpu()
        {
            if (!GpuBvh.Supported || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("GPU parity requires compute shaders and async GPU readback.");
        }

        static void AssertGpuParity(RemeshBaker.Maps cpu, RemeshBaker.Maps gpu)
        {
            Assert.IsTrue(gpu.gpu, "the shader backend must run rather than silently use CPU fallback");
            Assert.AreEqual(cpu.covered, gpu.covered); Assert.AreEqual(cpu.misses, gpu.misses);
            Assert.AreEqual(cpu.partialMisses, gpu.partialMisses); Assert.AreEqual(cpu.gutterMisses, gpu.gutterMisses);
            Assert.AreEqual(cpu.surfaceSamples, gpu.surfaceSamples);
            for (int pixel = 0; pixel < cpu.color.Length; ++pixel) {
                if (cpu.color[pixel].a == 0) continue;
                Assert.AreEqual(cpu.color[pixel].a, gpu.color[pixel].a);
                Color a = cpu.color[pixel], b = gpu.color[pixel];
                Assert.That(Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)), Is.LessThanOrEqualTo(1.01f / 255), "color pixel " + pixel);
                Assert.That(Vector3.Angle(DecodeNormal(cpu.normal[pixel]), DecodeNormal(gpu.normal[pixel])), Is.LessThan(1), "normal pixel " + pixel);
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator FoldedMirroredFootprintsTransportTheSameDetailOnCpuAndGpu()
        {
            RequireGpu();
            var source = FoldSource(1, out var edgeNormal, out var sideNormal); var target = FoldAtlas(); MirrorNeighborUv(target);
            var settings = new RemeshSettings { textureResolution = 64, padding = 2, dilationRadius = 0, bakeSamples = 16, vertexColorTint = true };
            using (var cancellation = new CancellationTokenSource())
            try {
                var cpu = RemeshBaker.BakeAsync(source, target, target.tangents, settings, cancellation.Token, null, _ => null);
                yield return Await(cpu);
                var gpu = RemeshBaker.BakeAsync(source, target, target.tangents, settings, cancellation.Token, null, RemeshBaker.CreateGpu);
                yield return Await(gpu);
                AssertGpuParity(cpu.Result, gpu.Result);
                Assert.AreEqual(0, gpu.Result.misses); Assert.That(gpu.Result.surfaceSamples, Is.GreaterThan(0));
                Assert.That(((Color)gpu.Result.color[8 * 64 + 7]).linear.r, Is.EqualTo(.3f).Within(.006f));
                Assert.That(Vector3.Angle(DecodeNormal(gpu.Result.normal[8 * 64 + 7]), (edgeNormal * .75f + sideNormal * .25f).normalized), Is.LessThan(1));
            }
            finally { cancellation.Cancel(); }
        }

        static RemeshSource FlatSource(RemeshNative.Geometry target, Color[] colors, Vector3 normal, float diagonal)
        {
            var normals = new Vector3[target.positions.Length]; Array.Fill(normals, normal);
            return new RemeshSource { positions = target.positions, indices = target.indices, uv = target.uv, tangents = target.tangents,
                normals = normals, colors = colors, hasColors = true, diagonal = diagonal, faceMaterials = new int[target.indices.Length / 3],
                materials = new[] { new RemeshSource.Surface { color = new RemeshSource.Map(), normal = new RemeshSource.Map(),
                    metal = new RemeshSource.Map(), ao = new RemeshSource.Map(), emission = new RemeshSource.Map(), tint = Color.white,
                    emissionTint = Color.black, normalScale = 1, aoStrength = 1, smoothness = .5f } } };
        }

        static RemeshNative.Geometry FullAtlas(out RemeshSource source)
        {
            var uv = new[] { new Vector2(-1, -1), new Vector2(3, -1), new Vector2(-1, 3) };
            var target = new RemeshNative.Geometry { positions = new[] { new Vector3(-1, -1, 0), new Vector3(3, -1, 0), new Vector3(-1, 3, 0) },
                uv = uv, indices = new[] { 0, 1, 2 }, normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) } };
            var colors = Array.ConvertAll(uv, p => new Color(.3f + .2f * p.x, .25f + .15f * p.y, .2f + .1f * p.x + .1f * p.y, 1));
            source = FlatSource(target, colors, Vector3.forward, Mathf.Sqrt(32));
            return target;
        }

        static void AssertFullAtlasOracle(RemeshBaker.Maps maps)
        {
            Assert.AreEqual(maps.size * maps.size, maps.covered); Assert.AreEqual(0, maps.misses); Assert.AreEqual(0, maps.partialMisses);
            Assert.AreEqual(0, maps.gutterTexels, "full atlas coverage has no margin despite the settings' minimum padding of one");
            for (int y = 0; y < maps.size; ++y) for (int x = 0; x < maps.size; ++x) {
                int pixel = y * maps.size + x; float u = (x + .5f) / maps.size, v = (y + .5f) / maps.size;
                Color actual = ((Color)maps.color[pixel]).linear;
                Assert.AreEqual(255, maps.color[pixel].a, "pixel " + pixel);
                Assert.That(actual.r, Is.EqualTo(.3f + .2f * u).Within(.006f), "red pixel " + pixel);
                Assert.That(actual.g, Is.EqualTo(.25f + .15f * v).Within(.006f), "green pixel " + pixel);
                Assert.That(actual.b, Is.EqualTo(.2f + .1f * u + .1f * v).Within(.006f), "blue pixel " + pixel);
                Assert.That(Vector3.Angle(DecodeNormal(maps.normal[pixel]), Vector3.forward), Is.LessThan(.5f));
            }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator DenseStreamingBakePreservesEveryAffineTexelOnCpuAndGpu()
        {
            var target = FullAtlas(out var source);
            var settings = new RemeshSettings { textureResolution = 256, padding = 1, dilationRadius = 0, bakeSamples = 16, vertexColorTint = true };
            Assert.That(256 * 256 * settings.bakeSamples, Is.GreaterThan(262144 * 2), "fixture must stream through both enlarged GPU query bands repeatedly");
            using (var cancellation = new CancellationTokenSource())
            try {
                var cpu = RemeshBaker.BakeAsync(source, target, target.tangents, settings, cancellation.Token, null, _ => null);
                yield return Await(cpu); AssertFullAtlasOracle(cpu.Result);
                RequireGpu();
                var gpu = RemeshBaker.BakeAsync(source, target, target.tangents, settings, cancellation.Token, null, RemeshBaker.CreateGpu);
                yield return Await(gpu); AssertFullAtlasOracle(gpu.Result); AssertGpuParity(cpu.Result, gpu.Result);
                Assert.That(gpu.Result.gpuBands, Is.GreaterThanOrEqualTo(4));
            }
            finally { cancellation.Cancel(); }
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StreamingGpuSourceAoRetainsTheFlatSurfaceOracle()
        {
            RequireGpu();
            var target = FullAtlas(out var source);
            var settings = new RemeshSettings { textureResolution = 256, padding = 1, dilationRadius = 0,
                bakeSamples = 9, vertexColorTint = true, bakeSourceAO = true, sourceAO = new SourceAoSettings { samples = 16 } };
            var cpu = RemeshBaker.BakeAsync(source, target, target.tangents, settings, Token, null, _ => null);
            yield return Await(cpu); AssertFullAtlasOracle(cpu.Result);
            var gpu = RemeshBaker.BakeAsync(source, target, target.tangents, settings, Token, null, RemeshBaker.CreateGpu, true);
            yield return Await(gpu); AssertFullAtlasOracle(gpu.Result); AssertGpuParity(cpu.Result, gpu.Result);
            Assert.IsTrue(gpu.Result.gpuAO, "the shared BVH AO backend must run");
            Assert.That(gpu.Result.gpuBands, Is.GreaterThanOrEqualTo(3));
            CollectionAssert.AreEqual(cpu.Result.ao, gpu.Result.ao);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator StreamingCancellationDrainsTheSubmittedProjectionAndWorker()
        {
            RequireGpu();
            var target = FullAtlas(out var source);
            var settings = new RemeshSettings { textureResolution = 128, padding = 1, dilationRadius = 0, bakeSamples = 16 };
            using (var cancellation = new CancellationTokenSource()) {
                GpuBvh backend = null;
                var cancelled = RemeshBaker.BakeAsync(source, target, target.tangents, settings, cancellation.Token, null,
                    ctx => { backend = RemeshBaker.CreateGpu(ctx); return backend; });
                double deadline = EditorApplication.timeSinceStartup + 30;
                while (!cancelled.IsCompleted && (backend == null || backend.ProjectionBatchCount == 0) && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                Assert.IsNotNull(backend); Assert.That(backend.ProjectionBatchCount, Is.GreaterThan(0));
                Assert.IsFalse(cancelled.IsCompleted, "cancel with a submitted readback and CPU producer in flight");
                cancellation.Cancel();
                while (!cancelled.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(cancelled.IsCompleted); Assert.IsTrue(cancelled.IsCanceled, cancelled.Exception?.ToString());
            }
            var restarted = RemeshBaker.BakeAsync(source, target, target.tangents, settings, Token, null, RemeshBaker.CreateGpu);
            yield return Await(restarted); AssertFullAtlasOracle(restarted.Result);
        }

        [Test]
        public void RequestBudgetCanResumeMidRowWithoutDroppingOrDuplicatingPixels()
        {
            var target = FullAtlas(out var source);
            var settings = new RemeshSettings { textureResolution = 64, padding = 1, dilationRadius = 0, bakeSamples = 16 };
            var prepare = typeof(RemeshBaker).GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static);
            var build = typeof(RemeshBaker).GetMethod("BuildRequests", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(prepare); Assert.IsNotNull(build);
            var context = (RemeshBaker.Context)prepare.Invoke(null, new object[] { source, target, target.tangents, settings, Token, null, null });
            context.bandRows = 64; // isolate the query budget from the row budget
            var band = new RemeshBaker.Band(context);
            var counts = new int[4096]; var areas = new float[4096]; int chunks = 0;
            for (int first = 1; first < 4096; first = band.nextPixel) {
                build.Invoke(null, new object[] { context, band, first, Token }); ++chunks;
                Assert.That(band.nextPixel, Is.GreaterThan(first)); Assert.That(band.nextPixel, Is.LessThanOrEqualTo(4096));
                Assert.That(band.count, Is.LessThanOrEqualTo(RemeshBaker.QueryBudget + 16));
                if (chunks == 1) Assert.AreEqual(1, band.nextPixel % 64, "the fixture must stop inside a row");
                for (int i = 0; i < band.count; ++i) {
                    int pixel = band.pixel[i]; Assert.AreEqual(first + i / 16, pixel, "query order/progress");
                    ++counts[pixel]; areas[pixel] += band.area[i];
                }
                for (int row = 0; row < band.y1 - band.y0; ++row)
                    for (int i = band.rowStart[row]; i < band.rowStart[row + 1]; ++i)
                        Assert.AreEqual(band.y0 + row, band.pixel[i] / 64, "row slice must match resumed pixel range");
            }
            Assert.That(chunks, Is.GreaterThan(1)); Assert.AreEqual(0, counts[0]);
            for (int pixel = 1; pixel < counts.Length; ++pixel) {
                Assert.AreEqual(16, counts[pixel], "each covered pixel needs exactly sixteen strata");
                Assert.That(areas[pixel], Is.EqualTo(1).Within(1e-6f));
            }
        }

        [Test]
        public void TinySuccessfulAreaRetainsItsNonflatNormalWhenOtherSurfacePiecesMiss()
        {
            var target = TexelGeometry(new[] { new Vector2(8, 8), new Vector2(9, 8), new Vector2(9, 9),
                new Vector2(8.5f, 8.5f + 1f / 1048576), new Vector2(8, 9) },
                new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 3, 2, 4 });
            target.normals = new Vector3[5]; Array.Fill(target.normals, Vector3.forward);
            target.tangents = new Vector4[5]; Array.Fill(target.tangents, new Vector4(1, 0, 0, 1));
            target.surfaceNeighbors = RemeshSurfaceTopology.Build(target.positions, target.indices, Token);
            var colors = new Color[5]; Array.Fill(colors, new Color(.3f, .4f, .5f, 1));
            var expected = new Vector3(.6f, 0, .8f);
            var source = FlatSource(target, colors, expected, Mathf.Sqrt(2));
            source.indices = new[] { 0, 2, 3 }; source.faceMaterials = new[] { 0 };
            var samples = new List<RemeshTexelFootprint.Sample>();
            new RemeshTexelFootprint(target, target.surfaceNeighbors, 64).Gather(1, 8, 8, 1, samples, Token);
            Assert.That(Area(samples, 1, true), Is.EqualTo(1f / 2097152).Within(1e-10f));
            Assert.That(Area(samples, interiorOnly: true), Is.EqualTo(1).Within(1e-6f), "large missing surface pieces must not be replaced by an open-boundary sample");
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 1, dilationRadius = 0, bakeSamples = 1, vertexColorTint = true, projectionDistance = 1e-8f }, Token);
            Assert.That(maps.partialMisses, Is.GreaterThan(0)); Assert.That(maps.missedSampleArea, Is.GreaterThan(.99f));
            Assert.AreEqual(255, maps.color[8 * 64 + 8].a);
            Assert.That(Vector3.Angle(DecodeNormal(maps.normal[8 * 64 + 8]), expected), Is.LessThan(1), "positive hit area below1e-6 must normalize its detail rather than become a flat normal");
        }

        [Test]
        public void ConnectedPaddingPreservesAnAffineSignalThroughSafeNearestAndBilinearMips()
        {
            var source = FoldSource(1, out var edgeNormal, out _); var target = FoldAtlas();
            target.uv[0] = new Vector2(16.5f, 8) / 64; target.uv[1] = new Vector2(48.5f, 8) / 64;
            target.uv[2] = new Vector2(16.5f, 40) / 64;
            target.uv[3] = new Vector2(48, 48) / 64; target.uv[4] = new Vector2(48, 60) / 64; target.uv[5] = new Vector2(60, 48) / 64;
            var maps = RemeshBaker.Bake(source, target, target.tangents, new RemeshSettings {
                textureResolution = 64, padding = 4, dilationRadius = 0, bakeSamples = 1, vertexColorTint = true }, Token);
            var actual = LinearColors(maps.color);
            var legacy = (Color[])actual.Clone();
            var legacyNormal = (Color32[])maps.normal.Clone();
            Color boundaryColor = new Color(.4f, .2f, .6f, 1);
            Color32 boundaryNormal = new Color(edgeNormal.x * .5f + .5f, edgeNormal.y * .5f + .5f, edgeNormal.z * .5f + .5f, 1);
            // Analytic edge-copy control, not an execution of the old baker. Limit
            // it to this receiving chart's gutter; the distant B chart keeps its frame.
            for (int y = 12; y < 28; ++y) for (int x = 12; x < 16; ++x) {
                legacy[y * 64 + x] = boundaryColor; legacyNormal[y * 64 + x] = boundaryNormal;
            }
            var newMips = ColorMips(actual, 64, 2); var oldMips = ColorMips(legacy, 64, 2);
            for (int mip = 0; mip <= 2; ++mip) {
                Vector2 nearestPoint = mip == 0 ? new Vector2(15.5f, 17.5f) : mip == 1 ? new Vector2(15, 17) : new Vector2(14, 18);
                Vector2 bilinearPoint = mip == 0 ? new Vector2(15.75f, 17.75f) : mip == 1 ? new Vector2(15.25f, 17.25f) : new Vector2(15, 18);
                int size = 64 >> mip;
                foreach (bool bilinear in new[] { false, true }) {
                    var point = bilinear ? bilinearPoint : nearestPoint;
                    float expected = .4f + .4f * (point.x - 16.5f) / 32;
                    float improved = SampleColor(newMips[mip], size, point / 64, bilinear).r;
                    float clamped = SampleColor(oldMips[mip], size, point / 64, bilinear).r;
                    Assert.That(improved, Is.EqualTo(expected).Within(.006f), "mip=" + mip + ", bilinear=" + bilinear);
                    Assert.That(Mathf.Abs(clamped - expected), Is.GreaterThan(.008f), "edge-copy control must distinguish this regression");
                }
            }
            SaveDiagnosticCaptures(maps, legacy, legacyNormal, newMips, oldMips, edgeNormal);
        }

        static Color[] LinearColors(Color32[] encoded)
        {
            var result = new Color[encoded.Length];
            for (int i = 0; i < result.Length; ++i) result[i] = ((Color)encoded[i]).linear;
            return result;
        }

        static Color[][] ColorMips(Color[] pixels, int size, int levels)
        {
            var result = new Color[levels + 1][]; result[0] = pixels;
            for (int mip = 1; mip <= levels; ++mip) {
                int nextSize = size / 2; var next = new Color[nextSize * nextSize];
                for (int y = 0; y < nextSize; ++y) for (int x = 0; x < nextSize; ++x) {
                    int i = y * 2 * size + x * 2;
                    next[y * nextSize + x] = (result[mip - 1][i] + result[mip - 1][i + 1] +
                        result[mip - 1][i + size] + result[mip - 1][i + size + 1]) * .25f;
                }
                result[mip] = next; size = nextSize;
            }
            return result;
        }

        static Color SampleColor(Color[] pixels, int size, Vector2 uv, bool bilinear)
        {
            if (!bilinear) {
                int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * size), 0, size - 1), y = Mathf.Clamp(Mathf.FloorToInt(uv.y * size), 0, size - 1);
                return pixels[y * size + x];
            }
            float px = uv.x * size - .5f, py = uv.y * size - .5f;
            int left = Mathf.FloorToInt(px), bottom = Mathf.FloorToInt(py);
            int x0 = Mathf.Clamp(left, 0, size - 1), x1 = Mathf.Clamp(left + 1, 0, size - 1);
            int y0 = Mathf.Clamp(bottom, 0, size - 1), y1 = Mathf.Clamp(bottom + 1, 0, size - 1);
            return Color.Lerp(Color.Lerp(pixels[y0 * size + x0], pixels[y0 * size + x1], px - left),
                Color.Lerp(pixels[y1 * size + x0], pixels[y1 * size + x1], px - left), py - bottom);
        }

        static void SaveDiagnosticCaptures(RemeshBaker.Maps maps, Color[] legacy, Color32[] legacyNormal,
            Color[][] newMips, Color[][] oldMips, Vector3 edgeNormal)
        {
            string output = Environment.GetEnvironmentVariable("MESHLAB_SURFACE_PADDING_RENDER_OUTPUT");
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            SavePng(output, "diagnostic-fold-new-color", LinearColors(maps.color), 64, 64, true);
            SavePng(output, "diagnostic-fold-legacy-edge-copy-reference-color", legacy, 64, 64, true);
            var normals = Array.ConvertAll(maps.normal, value => (Color)value);
            var oldNormals = Array.ConvertAll(legacyNormal, value => (Color)value);
            var wrongWorld = (Color[])normals.Clone();
            FoldSource(1, out _, out var sideNormal);
            var fold = Quaternion.FromToRotation(Vector3.forward, Vector3.right);
            for (int y = 12; y < 28; ++y) for (int x = 12; x <= 20; ++x) {
                Vector3 n = edgeNormal;
                if (x < 16) {
                    float z = (16.5f - (x + .5f)) / 32;
                    n = fold * (edgeNormal * (1 - z) + sideNormal * z).normalized;
                }
                else if (x == 16) {
                    const float z = .25f / 32;
                    n = (edgeNormal + fold * (edgeNormal * (1 - z) + sideNormal * z).normalized).normalized;
                }
                wrongWorld[y * 64 + x] = new Color(n.x * .5f + .5f, n.y * .5f + .5f, n.z * .5f + .5f, 1);
            }
            SavePng(output, "diagnostic-fold-new-normal-raw", normals, 64, 64, false);
            SavePng(output, "diagnostic-fold-legacy-normal-reference-raw", oldNormals, 64, 64, false);
            SavePng(output, "diagnostic-fold-wrong-world-averaging-normal-reference-raw", wrongWorld, 64, 64, false);
            for (int mip = 0; mip <= 2; ++mip) {
                const int width = 512, height = 128; var strip = new Color[width * height];
                for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
                    var pixels = y < 64 ? newMips[mip] : oldMips[mip];
                    bool bilinear = (y / 32 & 1) != 0;
                    var uv = new Vector2(12 + 8f * (x + .5f) / width, 18) / 64;
                    strip[y * width + x] = SampleColor(pixels, 64 >> mip, uv, bilinear);
                }
                SavePng(output, "diagnostic-fold-color-strip-mip" + mip, strip, width, height, true);
            }
            const int profileWidth = 512, profileHeight = 192;
            var error = new Color[profileWidth * profileHeight];
            for (int x = 0; x < profileWidth; ++x) {
                float u = 12.5f + 7f * (x + .5f) / profileWidth;
                float z = Mathf.Max(0, 16.5f - u) / 32;
                Vector3 expected = (edgeNormal * (1 - z) + sideNormal * z).normalized;
                for (int y = 0; y < profileHeight; ++y) {
                    var texels = y < 64 ? normals : y < 128 ? oldNormals : wrongWorld;
                    Color packed = SampleColor(texels, 64, new Vector2(u, 18) / 64, true);
                    Vector3 n = new Vector3(packed.r * 2 - 1, packed.g * 2 - 1, packed.b * 2 - 1).normalized;
                    float angle = Mathf.Clamp01(Vector3.Angle(n, expected) / 5);
                    error[y * profileWidth + x] = new Color(angle, 1 - angle, 0, 1);
                }
            }
            SavePng(output, "diagnostic-fold-world-normal-error-0-to-5deg", error, profileWidth, profileHeight, false);
            File.WriteAllText(Path.Combine(output, "diagnostic-fold-readme.txt"),
                "Synthetic orthogonal folded planes, not the user's bust. Legacy images are an analytic edge-copy control, not an old-version bake.\n" +
                "Color strip bottom-to-top bands: new nearest, new bilinear, legacy nearest, legacy bilinear. CPU linear 2x2 box mips; no importer/compression/PBR validation.\n" +
                "Normal error bottom-to-top bands: new transported detail, legacy edge copy, wrong world averaging; green=0deg, red>=5deg. Frame is receiving A's XYZ.\n" +
                "Assertions use only safe footprint locations at mip0..2; farther strip locations may exceed the four-pixel gutter.\n");
        }

        static void SavePng(string output, string name, Color[] pixels, int width, int height, bool encodeSrgb)
        {
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try {
                if (encodeSrgb) {
                    pixels = (Color[])pixels.Clone();
                    for (int i = 0; i < pixels.Length; ++i) pixels[i] = pixels[i].gamma;
                }
                image.SetPixels(pixels); image.Apply();
                File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }
    }
}
