using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class TargetBevelUvRecoveryTests
    {
        /// <summary>Builds a collapsed boundary quad with optional unrelated or newly intersecting faces.</summary>
        static (GroupedShellTransfer.TransferResult result, List<UvShell> shells, int[] indices, Vector3[] positions) Fixture(string contact = "none")
        {
            var positions = new List<Vector3> { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) };
            var uv = new List<Vector2> { new Vector2(.2f, .2f), new Vector2(.4f, .2f), new Vector2(.2f, .4f), new Vector2(.3f, .3f) };
            var indices = new List<int> { 0, 1, 2, 2, 1, 3 };
            void Triangle(float x, float y)
            {
                int start = positions.Count;
                positions.AddRange(new[] { Vector3.forward, Vector3.forward + Vector3.right, Vector3.forward + Vector3.up });
                uv.AddRange(new[] { new Vector2(x, y), new Vector2(x + .08f, y), new Vector2(x, y + .08f) });
                indices.AddRange(new[] { start, start + 1, start + 2 });
            }
            if (contact == "new") Triangle(.36f, .36f);
            if (contact == "existing") { Triangle(.7f, .7f); Triangle(.72f, .72f); }
            if (contact == "crowded") for (int i = 0; i < 100; ++i) Triangle(.7f, .7f);
            if (contact == "metric-anchor") { Triangle(.7f, .7f); uv[uv.Count - 1] = new Vector2(.7f, .708f); }
            if (contact == "existing-changed") { uv[3] = new Vector2(.3f, .300001f); Triangle(.29f, .29f); }
            var shells = new List<UvShell> { new UvShell { shellId = 0,
                vertexIndices = new HashSet<int> { 0, 1, 2, 3 }, faceIndices = new List<int> { 0, 1 } } };
            for (int f = 2; f < indices.Count / 3; ++f)
                shells.Add(new UvShell { shellId = shells.Count, faceIndices = new List<int> { f },
                    vertexIndices = new HashSet<int>(indices.Skip(f * 3).Take(3)) });
            var result = new GroupedShellTransfer.TransferResult {
                uv2 = uv.ToArray(), targetShellToSourceShell = new int[shells.Count], vertexToSourceShell = new int[uv.Count],
                targetShellStatus = new GroupedShellTransfer.ShellStatus[shells.Count]
            };
            return (result, shells, indices.ToArray(), positions.ToArray());
        }

        /// <summary>Runs target recovery against either a full square donor or a donor missing the collapsed face's footprint.</summary>
        static int Recover((GroupedShellTransfer.TransferResult result, List<UvShell> shells, int[] indices, Vector3[] positions) input,
            bool covered = true, CancellationToken token = default, long budget = 2000000,
            int atlasSize = 512, Vector2[] donorUv = null, TransferMatchTrace trace = null, bool allowPatch = false)
        {
            var uv = covered ? new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }
                : new[] { new Vector2(.2f, .2f), new Vector2(.4f, .2f), new Vector2(.2f, .4f), new Vector2(.3f, .3f) };
            uv = donorUv ?? uv;
            var indices = new[] { 0, 1, 2, 2, 1, 3 };
            var sources = new List<UvShell> { new UvShell { faceIndices = new List<int> { 0, 1 }, vertexIndices = new HashSet<int> { 0, 1, 2, 3 } } };
            return TargetBevelUvRecovery.Apply(input.result, input.shells, input.indices, input.positions,
                sources, indices, uv, atlasSize, UvTransferTolerance.BoundaryTexels, trace, token, budget, allowPatch);
        }

        /// <summary>Repairs a collapsed boundary without moving anchors or unrelated existing contacts.</summary>
        [TestCase("none")] [TestCase("existing")]
        public void RepairRetainsHealthyGeometryAndUnrelatedContacts(string contact)
        {
            var input = Fixture(contact);
            var original = input.result.uv2;
            var positions = (Vector3[])input.positions.Clone();
            var indices = (int[])input.indices.Clone();
            Assert.AreEqual(1, Recover(input));
            Assert.AreNotSame(original, input.result.uv2);
            for (int v = 0; v < original.Length; ++v)
                if (v != 3) Assert.AreEqual(original[v], input.result.uv2[v]);
            Assert.AreEqual(.4f, input.result.uv2[3].x, 1e-6f);
            Assert.AreEqual(.4f, input.result.uv2[3].y, 1e-6f);
            CollectionAssert.AreEqual(positions, input.positions);
            CollectionAssert.AreEqual(indices, input.indices);
        }

        /// <summary>Neither new contacts nor uncovered donor regions may publish a partial repair.</summary>
        [TestCase(true)] [TestCase(false)]
        public void RefusedRepairPreservesOriginalBuffer(bool contact)
        {
            var input = Fixture(contact ? "new" : "none");
            var original = input.result.uv2;
            var snapshot = (Vector2[])original.Clone();
            Assert.AreEqual(0, Recover(input, covered: contact));
            Assert.AreSame(original, input.result.uv2);
            CollectionAssert.AreEqual(snapshot, original);
        }

        /// <summary>A shared vertex, rejected shell or mixed donor assignment cannot be locally developed.</summary>
        [TestCase("shared")] [TestCase("rejected")] [TestCase("mixed")]
        public void AmbiguousOrRejectedShellRemainsUntouched(string reason)
        {
            var input = Fixture();
            if (reason == "shared") input.shells.Add(new UvShell { shellId = 0, vertexIndices = new HashSet<int> { 3 } });
            if (reason == "rejected") input.result.targetShellStatus[0] = GroupedShellTransfer.ShellStatus.Rejected;
            if (reason == "mixed") input.result.vertexToSourceShell[3] = -1;
            var original = input.result.uv2;
            Assert.AreEqual(0, Recover(input));
            Assert.AreSame(original, input.result.uv2);
        }

        /// <summary>Cancellation and exhausted coverage work leave the complete original UV array owned by the caller.</summary>
        [Test]
        public void CancellationAndBudgetRefusalAreAtomic()
        {
            var input = Fixture(); var original = input.result.uv2;
            Assert.AreEqual(0, Recover(input, budget: 1));
            Assert.AreSame(original, input.result.uv2);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => Recover(input, token: cancelled.Token));
            Assert.AreSame(original, input.result.uv2);
        }

        /// <summary>The physical quarter-texel allowance uses the packed atlas size rather than the requested resolution.</summary>
        [TestCase(512, 1)] [TestCase(2344, 0)]
        public void DonorCoverageUsesActualAtlasSize(int atlasSize, int expected)
        {
            var input = Fixture();
            var donor = new[] { Vector2.zero, new Vector2(.3998f, 0), Vector2.up, new Vector2(.3998f, 1) };
            Assert.AreEqual(expected, Recover(input, atlasSize: atlasSize, donorUv: donor));
        }

        /// <summary>Duplicated donor coverage cannot conceal an uncovered half; nonfinite donor coordinates also refuse publication.</summary>
        [TestCase(false)] [TestCase(true)]
        public void InvalidOrOverlappingDonorCannotCertifyCoverage(bool invalid)
        {
            var input = Fixture(); var original = input.result.uv2;
            // Both triangles cover the same half of the proposed boundary face.
            var donor = new[] { new Vector2(.2f, .4f), new Vector2(.4f, .2f), new Vector2(.4f, .3f), new Vector2(.2f, .4f) };
            if (invalid) donor[0].x = float.NaN;
            Assert.AreEqual(0, Recover(input, donorUv: donor));
            Assert.AreSame(original, input.result.uv2);
        }

        /// <summary>A successful local footprint check cannot publish when the full-atlas contact scan exhausts its budget.</summary>
        [Test]
        public void IncompleteGlobalContactScanRefusesRepair()
        {
            Assert.AreEqual(1, Recover(Fixture(), budget: 1000));
            var input = Fixture("crowded"); var original = input.result.uv2;
            Assert.AreEqual(0, Recover(input, budget: 1000));
            Assert.AreSame(original, input.result.uv2);
        }

        /// <summary>Exhausting the optional shell-trace capacity cannot interrupt publication of an audited repair.</summary>
        [Test]
        public void TruncatedDiagnosticsDoNotChangeRecovery()
        {
            var input = Fixture(); input.shells[0].shellId = 10000;
            input.result.targetShellToSourceShell = new int[10001];
            input.result.targetShellStatus = new GroupedShellTransfer.ShellStatus[10001];
            var trace = new TransferMatchTrace();
            Assert.AreEqual(1, Recover(input, trace: trace));
            Assert.IsTrue(trace.truncated);
        }

        /// <summary>A small incident patch can recover inside the donor when its free-corner development would exceed the texel band.</summary>
        [TestCase(false, .001f, 1)] [TestCase(true, .001f, 1)] [TestCase(false, .1f, 0)]
        public void PatchRespectsNeighbourMetricsAndMirroredWinding(bool mirrored, float width, int expected)
        {
            var input = Fixture("metric-anchor");
            input.positions[2] = new Vector3(1, 1, 0); input.positions[3] = new Vector3(1 + width, 1, 0);
            var uv = new[] { new Vector2(.1f, .1f), new Vector2(.4f, .1f), new Vector2(.4f, .4f), new Vector2(.4f, .4f) }
                .Concat(input.result.uv2.Skip(4)).ToArray();
            var donor = new[] { new Vector2(.1f, .1f), new Vector2(.4f, .1f), new Vector2(.1f, .4f), new Vector2(.4f, .4f) };
            if (mirrored) { uv = uv.Select(p => new Vector2(1 - p.x, p.y)).ToArray(); donor = donor.Select(p => new Vector2(1 - p.x, p.y)).ToArray(); }
            input.result.uv2 = uv;
            Assert.AreEqual(0, Recover(input, atlasSize: 2344, donorUv: donor));
            Assert.AreEqual(expected, Recover(input, atlasSize: 2344, donorUv: donor, allowPatch: true));
            if (expected == 0) Assert.AreSame(uv, input.result.uv2);
            else
            {
                Assert.AreEqual(uv[0], input.result.uv2[0]); Assert.AreEqual(uv[1], input.result.uv2[1]);
                Assert.Less(ReverseUvTransfer.TriangleAnisotropy(input.positions[0], input.positions[1], input.positions[2],
                    input.result.uv2[0], input.result.uv2[1], input.result.uv2[2]), 1.01);
                Assert.Less(ReverseUvTransfer.TriangleAnisotropy(input.positions[2], input.positions[1], input.positions[3],
                    input.result.uv2[2], input.result.uv2[1], input.result.uv2[3]), 1.01);
            }
        }

        /// <summary>Fewer collapsed faces cannot compensate for increased whole-mesh finite stretch.</summary>
        [Test]
        public void PatchRefusesGlobalStretchTradeoff()
        {
            var input = Fixture();
            input.positions[2] = new Vector3(1, 1, 0); input.positions[3] = new Vector3(1.001f, 1, 0);
            input.result.uv2 = new[] { new Vector2(.1f, .1f), new Vector2(.4f, .1f), new Vector2(.4f, .4f), new Vector2(.4f, .4f) };
            var original = input.result.uv2;
            var donor = new[] { new Vector2(.1f, .1f), new Vector2(.4f, .1f), new Vector2(.1f, .4f), new Vector2(.4f, .4f) };
            Assert.AreEqual(0, Recover(input, atlasSize: 2344, donorUv: donor, allowPatch: true));
            Assert.AreSame(original, input.result.uv2);
        }

        /// <summary>A completely collapsed triangle has no healthy incident face to determine its UV orientation.</summary>
        [Test]
        public void PatchRefusesAnUnanchoredMirrorChoice()
        {
            var input = Fixture("metric-anchor"); input.indices = new[] { 0, 1, 2, 4, 5, 6 };
            input.shells[0].faceIndices = new List<int> { 0 };
            input.shells[0].vertexIndices = new HashSet<int> { 0, 1, 2 };
            input.shells[1].faceIndices = new List<int> { 1 };
            input.result.uv2[2] = new Vector2(.3f, .2f);
            var original = input.result.uv2;
            Assert.AreEqual(0, Recover(input, allowPatch: true));
            Assert.AreSame(original, input.result.uv2);
        }

        /// <summary>Moving a face already in contact is refused even when it would retain the same overlapping pair identities.</summary>
        [TestCase(false)] [TestCase(true)]
        public void ExistingContactCannotGrowDuringRepair(bool allowPatch)
        {
            var input = Fixture("existing-changed"); var original = input.result.uv2;
            Assert.AreEqual(0, Recover(input, allowPatch: allowPatch));
            Assert.AreSame(original, input.result.uv2);
        }

        /// <summary>Three faces using one edge cannot establish an unambiguous incident patch.</summary>
        [Test]
        public void PatchRefusesNonmanifoldEdges()
        {
            var input = Fixture(); input.indices = input.indices.Concat(new[] { 2, 1, 0 }).ToArray();
            input.shells[0].faceIndices.Add(2);
            var original = input.result.uv2;
            Assert.AreEqual(0, Recover(input, allowPatch: true));
            Assert.AreSame(original, input.result.uv2);
        }
    }
}
