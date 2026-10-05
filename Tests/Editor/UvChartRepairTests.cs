using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class UvChartRepairTests
    {
        [Test]
        public void HighPrecisionRetryUsesAvailableBudgetInsteadOfRefusingThirtyTwoTimes()
        {
            const int charts = 81, resolution = 512;
            int factor = UvChartMerge.HighPrecisionLimit(charts, resolution);
            Assert.AreEqual(30, factor, "A useful retry fits even though 32x exceeds the existing cost budget.");
            long internalResolution = (long)resolution * factor;
            Assert.That(charts * internalResolution * internalResolution, Is.LessThanOrEqualTo(20_000_000_000L));
            long nextResolution = (long)resolution * (factor + 1);
            Assert.That(charts * nextResolution * nextResolution, Is.GreaterThan(20_000_000_000L));
        }

        [TestCase(13, 512, 32)]
        [TestCase(1, 64, 32)]
        [TestCase(1, 2048, 8)]
        public void HighPrecisionRetryHonorsMultiplierAndAtlasCaps(int charts, int resolution, int expected)
        {
            int factor = UvChartMerge.HighPrecisionLimit(charts, resolution);
            Assert.AreEqual(expected, factor);
            Assert.That(factor, Is.InRange(5, 32));
            Assert.That((long)resolution * factor, Is.LessThanOrEqualTo(16384));
            Assert.That((long)charts * resolution * factor * resolution * factor, Is.LessThanOrEqualTo(20_000_000_000L));
        }

        [TestCase(1, 4096)]
        [TestCase(1, 8192)]
        [TestCase(4000, 512)]
        [TestCase(int.MaxValue, 512)]
        [TestCase(0, 512)]
        [TestCase(1, 0)]
        public void HighPrecisionRetryReturnsNoCandidateWhenHigherPrecisionCannotFit(int charts, int resolution)
            => Assert.AreEqual(0, UvChartMerge.HighPrecisionLimit(charts, resolution));

        static RemeshNative.Geometry FoldedNeighbour()
            => new RemeshNative.Geometry {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.forward, new Vector3(.25f, 0, .25f) },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, new Vector2(.25f, .25f) },
                indices = new[] { 0, 1, 2, 0, 1, 3 }, charts = new[] { 0, 0, 0, 0 }, chartCount = 1,
                originalChartCount = 1
            };

        [Test]
        public void CutConflictingNeighbours_DuplicatesSharedVerticesAndPreservesEveryCorner()
        {
            var original = FoldedNeighbour();
            var scan = UvAtlasDiagnostics.Measure(original, CancellationToken.None, collectConflicts: true);
            var repaired = UvChartRepair.SplitConflicts(original, scan.conflicts, CancellationToken.None);
            Assert.AreEqual(2, repaired.chartCount);
            Assert.AreEqual(6, repaired.positions.Length);
            for (int i = 0; i < original.indices.Length; ++i) {
                int a = original.indices[i], b = repaired.indices[i];
                Assert.AreEqual(original.positions[a], repaired.positions[b]);
                Assert.AreEqual(original.uv[a], repaired.uv[b]);
                Assert.AreEqual(original.normals[a], repaired.normals[b]);
                Assert.AreEqual(original.tangents[a], repaired.tangents[b]);
            }
            Assert.AreEqual(0, UvAtlasDiagnostics.Measure(repaired, CancellationToken.None, sameChartOnly: true).pairs);
            Assert.AreEqual(1, UvAtlasDiagnostics.Measure(original, CancellationToken.None).pairs);
            Assert.AreEqual(4, original.positions.Length, "repair is transactional");
        }

        [Test]
        public void CutSelection_IsDeterministic()
        {
            var g = FoldedNeighbour();
            var scan = UvAtlasDiagnostics.Measure(g, CancellationToken.None, collectConflicts: true);
            var a = UvChartRepair.SplitConflicts(g, scan.conflicts, CancellationToken.None);
            var b = UvChartRepair.SplitConflicts(g, scan.conflicts, CancellationToken.None);
            CollectionAssert.AreEqual(a.indices, b.indices);
            CollectionAssert.AreEqual(a.charts, b.charts);
            CollectionAssert.AreEqual(a.uv, b.uv);
        }

        static void CheckNativeAvailable()
        {
            try { RemeshNative.CheckAvailable(); }
            catch (InvalidOperationException e) when (e.InnerException is DllNotFoundException || e.InnerException is EntryPointNotFoundException || e.InnerException is BadImageFormatException) {
                Assert.Ignore("Native plugin unavailable: " + e.Message);
            }
        }

        static IEnumerator WaitForCompletion(Task task)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.ElapsedMilliseconds < 10_000) yield return null;
            Assert.IsTrue(task.IsCompleted, "The repair worker must finish within the test deadline.");
        }

        static IEnumerator WaitForBlockedWorker(Task task, ManualResetEventSlim started, Func<Thread> worker)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.ElapsedMilliseconds < 10_000) {
                if (started.IsSet && (worker().ThreadState & ThreadState.WaitSleepJoin) != 0) break;
                yield return null;
            }
            Assert.IsTrue(started.IsSet, "The repair must have started on its worker.");
            Assert.IsFalse(task.IsCompleted, "A mandatory repair must wait for the occupied native session.");
            Assert.That(worker().ThreadState & ThreadState.WaitSleepJoin, Is.Not.EqualTo((ThreadState)0));
        }

        static void DrainWorker(Task task, CancellationTokenSource cancellation)
        {
            cancellation.Cancel();
            if (task == null) return;
            Assert.IsTrue(SpinWait.SpinUntil(() => task.IsCompleted, 10_000), "Test cleanup must leave no repair worker in flight.");
            _ = task.Exception;
        }

        [Test]
        public void NativeRepair_SeparatesFoldedNeighboursWithoutDroppingFaces()
        {
            CheckNativeAvailable();
            var original = FoldedNeighbour();
            var result = UvChartRepair.Apply(original, new RemeshSettings { textureResolution = 128, padding = 2 }, CancellationToken.None);
            var scan = UvAtlasDiagnostics.Measure(result, CancellationToken.None);
            Assert.IsTrue(scan.complete);
            Assert.AreEqual(0, scan.pairs);
            Assert.IsTrue(UvChartQuality.Measure(result, CancellationToken.None).valid);
            Assert.AreEqual(original.indices.Length, result.indices.Length);
            for (int i = 0; i < original.indices.Length; ++i)
                Assert.AreEqual(original.positions[original.indices[i]], result.positions[result.indices[i]]);
            Assert.AreEqual(1, UvAtlasDiagnostics.Measure(original, CancellationToken.None).pairs);
        }

        [UnityTest]
        public IEnumerator NativeRepair_WaitsForOccupiedSessionThenPreservesAllCorners()
        {
            CheckNativeAvailable();
            var original = FoldedNeighbour();
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            bool held = true;
            using (var cancellation = new CancellationTokenSource())
            using (var started = new ManualResetEventSlim()) {
                Task<RemeshNative.Geometry> task = null;
                Thread worker = null;
                try {
                    task = Task.Run(() => {
                        worker = Thread.CurrentThread;
                        started.Set();
                        return UvChartRepair.Apply(original, new RemeshSettings { textureResolution = 128, padding = 2 }, cancellation.Token);
                    });
                    yield return WaitForBlockedWorker(task, started, () => worker);
                    Assert.AreEqual(1, UvAtlasDiagnostics.Measure(original, CancellationToken.None).pairs);
                    XatlasRepack.ReleaseNativeSession(); held = false;
                    yield return WaitForCompletion(task);
                    var repaired = task.GetAwaiter().GetResult();
                    var scan = UvAtlasDiagnostics.Measure(repaired, CancellationToken.None);
                    Assert.IsTrue(scan.complete); Assert.AreEqual(0, scan.pairs);
                    Assert.AreEqual(original.indices.Length, repaired.indices.Length);
                    for (int i = 0; i < original.indices.Length; ++i)
                        Assert.AreEqual(original.positions[original.indices[i]], repaired.positions[repaired.indices[i]]);
                    Assert.IsTrue(UvChartQuality.Measure(repaired, CancellationToken.None).valid);
                    Assert.IsTrue(XatlasRepack.TryAcquireNativeSession(), "A completed repair must release its own lease.");
                    XatlasRepack.ReleaseNativeSession();
                }
                finally {
                    cancellation.Cancel();
                    if (held) XatlasRepack.ReleaseNativeSession();
                    DrainWorker(task, cancellation);
                }
            }
        }

        [UnityTest]
        public IEnumerator MandatoryRepack_CancellationWhileWaitingPreservesCurrentOwnersLease()
        {
            var original = FoldedNeighbour();
            var conflicts = UvAtlasDiagnostics.Measure(original, CancellationToken.None, collectConflicts: true).conflicts;
            var geometry = UvChartRepair.SplitConflicts(original, conflicts, CancellationToken.None);
            var beforeUv = (Vector2[])geometry.uv.Clone();
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            using (var cancellation = new CancellationTokenSource())
            using (var started = new ManualResetEventSlim()) {
                Task<bool> task = null;
                Thread worker = null;
                try {
                    task = Task.Run(() => {
                        worker = Thread.CurrentThread;
                        started.Set();
                        return UvChartMerge.RepackForRepair(geometry, new RemeshSettings { textureResolution = 128, padding = 2 }, cancellation.Token);
                    });
                    yield return WaitForBlockedWorker(task, started, () => worker);
                    cancellation.Cancel();
                    yield return WaitForCompletion(task);
                    Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
                    Assert.IsFalse(XatlasRepack.TryAcquireNativeSession(), "Cancelling a waiter must not release the current owner's lease.");
                    CollectionAssert.AreEqual(beforeUv, geometry.uv);
                }
                finally {
                    cancellation.Cancel();
                    try { DrainWorker(task, cancellation); }
                    finally { XatlasRepack.ReleaseNativeSession(); }
                }
            }
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            XatlasRepack.ReleaseNativeSession();
        }

        [Test]
        public void CancelledAcquisitionDoesNotConsumeAFreeNativeSession()
        {
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel();
                bool acquired = false;
                try {
                    Assert.Throws<OperationCanceledException>(() => {
                        XatlasRepack.AcquireNativeSession(cancellation.Token);
                        acquired = true;
                    });
                    Assert.IsTrue(XatlasRepack.TryAcquireNativeSession()); acquired = true;
                }
                finally { if (acquired) XatlasRepack.ReleaseNativeSession(); }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OptionalRepack_RejectsBusySessionWithoutChangingGeometryOrReleasingOwner(bool highPrecision)
        {
            var original = FoldedNeighbour();
            var conflicts = UvAtlasDiagnostics.Measure(original, CancellationToken.None, collectConflicts: true).conflicts;
            var geometry = UvChartRepair.SplitConflicts(original, conflicts, CancellationToken.None);
            var beforeUv = (Vector2[])geometry.uv.Clone();
            Assert.IsTrue(XatlasRepack.TryAcquireNativeSession());
            try {
                var settings = new RemeshSettings { textureResolution = 128, padding = 2 };
                bool packed = highPrecision
                    ? UvChartMerge.RepackHighPrecision(geometry, settings, CancellationToken.None, 8)
                    : UvChartMerge.Repack(geometry, settings, CancellationToken.None);
                Assert.IsFalse(packed);
                Assert.IsFalse(XatlasRepack.TryAcquireNativeSession(), "A refused optional repack must not release the owner's lease.");
                CollectionAssert.AreEqual(beforeUv, geometry.uv);
            }
            finally { XatlasRepack.ReleaseNativeSession(); }
        }
    }
}
