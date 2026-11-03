// GeometryChecksTests.cs — the DCC-style cleanup checks: concave polygons, tiny faces and
// short edges under a threshold, as MeshValidation counts them.
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class GeometryChecksTests
    {
        static readonly Vector3[] Square = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
        static readonly Vector3[] Arrowhead = { new Vector3(0, 0, 0), new Vector3(1, 0.4f, 0), new Vector3(2, 0, 0), new Vector3(1, 2, 0) };

        [Test]
        public void IsConcave_FindsAReflexCornerWhateverTheWindingOrPlane()
        {
            Assert.IsFalse(MeshValidation.IsConcave(Square));
            Assert.IsFalse(MeshValidation.IsConcave(Square.Reverse().ToArray()));
            Assert.IsTrue(MeshValidation.IsConcave(Arrowhead));
            Assert.IsTrue(MeshValidation.IsConcave(Arrowhead.Reverse().ToArray()));
            var turn = Quaternion.Euler(30, 40, 50);
            Assert.IsFalse(MeshValidation.IsConcave(Square.Select(v => turn * v + new Vector3(5, 6, 7)).ToArray()), "a tilted square");
            Assert.IsTrue(MeshValidation.IsConcave(Arrowhead.Select(v => turn * v).ToArray()), "a tilted arrowhead");
            var collinear = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 1, 0), new Vector3(0, 1, 0) };
            Assert.IsFalse(MeshValidation.IsConcave(collinear), "a corner on a straight edge is no reflex corner");
            Assert.IsFalse(MeshValidation.IsConcave(Square.Take(3).ToArray()), "a triangle is never concave");
        }

        [Test]
        public void SmallFacesAndShortEdges_FollowTheThresholds()
        {
            var positions = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                new Vector3(1e-5f, 0, 0), new Vector3(0, 1e-5f, 0), new Vector3(2, 0, 0), new Vector3(2, 0, 0),
            };
            var triangles = new[] { 0, 1, 2, 0, 3, 4, 1, 5, 2 };

            CollectionAssert.AreEqual(new[] { false, true, false }, MeshValidation.SmallFaceMask(positions, triangles, 1e-8));
            var (edges, zero, on) = MeshValidation.ShortEdges(positions, triangles, 1e-4);
            Assert.AreEqual(3, edges, "the three edges of the speck");
            Assert.AreEqual(0, zero);
            Assert.IsTrue(on[3] && on[4] && on[0] && !on[1]);

            var (withZero, zeroLength, _) = MeshValidation.ShortEdges(positions, new[] { 0, 1, 2, 1, 5, 6 }, 1e-4);
            Assert.AreEqual(1, withZero);
            Assert.AreEqual(1, zeroLength, "two corners at one point");

            // A short edge on a seam: the faces on its two sides use their own copies of its vertices.
            var seam = new[] { new Vector3(0, 0, 0), new Vector3(1e-5f, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(1e-5f, 0, 0), new Vector3(0, -1, 0) };
            var (seamEdges, _, seamOn) = MeshValidation.ShortEdges(seam, new[] { 0, 1, 2, 4, 3, 5 }, 1e-4);
            Assert.AreEqual(1, seamEdges, "one edge, counted once");
            Assert.IsTrue(seamOn[0] && seamOn[1] && seamOn[3] && seamOn[4], "both sides' copies are marked");
        }
    }
}
