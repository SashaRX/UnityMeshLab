using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class GeometryHelperTests
    {
        [Test]
        public void DisjointSetRepresentativeIsTheSmallestMember()
        {
            var set = new DisjointSet(6);
            Assert.That(set.Union(4, 2), Is.True);
            Assert.That(set.Union(5, 4), Is.True);
            Assert.That(set.Union(2, 5), Is.False, "already one set");
            Assert.That(set.Find(5), Is.EqualTo(2));
            Assert.That(set.Connected(4, 5), Is.True);
            Assert.That(set.Connected(0, 5), Is.False);
            Assert.That(set.Find(0), Is.EqualTo(0));
            Assert.That(set.Count, Is.EqualTo(6));
        }

        [Test]
        public void DisjointSetLabelsAreDenseAndOrderedByFirstMember()
        {
            var set = new DisjointSet(7);
            set.Union(6, 1); set.Union(3, 5); set.Union(0, 6);
            var labels = set.Labels(out int count);
            Assert.That(count, Is.EqualTo(4));
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 2, 3, 2, 0 }, labels);
        }

        [Test]
        public void DisjointSetLabelsDoNotDependOnUnionOrder()
        {
            var forward = new DisjointSet(8); var backward = new DisjointSet(8);
            var pairs = new[] { (0, 7), (7, 3), (2, 5), (5, 6) };
            foreach (var (a, b) in pairs) forward.Union(a, b);
            for (int i = pairs.Length - 1; i >= 0; --i) backward.Union(pairs[i].Item2, pairs[i].Item1);
            CollectionAssert.AreEqual(forward.Labels(out _), backward.Labels(out _));
            Assert.That(forward.Find(6), Is.EqualTo(2));
        }

        [Test]
        public void DisjointSetDetachMakesASingletonAgain()
        {
            var set = new DisjointSet(3);
            set.Union(0, 1); set.Union(1, 2);
            set.Detach(2);
            Assert.That(set.Find(2), Is.EqualTo(2));
            Assert.That(set.Connected(0, 1), Is.True);
            Assert.That(set.Connected(0, 2), Is.False);
        }

        [Test]
        public void HasAreaUsesDoubleSoATinyTriangleStillCounts()
        {
            // Edges of 1e-25: the float cross product underflows to zero, the double one does not.
            Assert.That(MeshGeometry.HasArea(Vector3.zero, new Vector3(1e-25f, 0, 0), new Vector3(0, 1e-25f, 0)), Is.True);
            Assert.That(MeshGeometry.HasArea(Vector3.zero, Vector3.right, Vector3.right * 2), Is.False, "collinear");
            Assert.That(MeshGeometry.HasArea(Vector3.zero, Vector3.right, Vector3.right), Is.False, "repeated corner");
            Assert.That(MeshGeometry.HasArea(Vector3.zero, Vector3.right, new Vector3(float.NaN, 1, 0)), Is.False);
            Assert.That(MeshGeometry.HasArea(Vector3.zero, Vector3.right, new Vector3(0, float.PositiveInfinity, 0)), Is.False, "an infinite corner has no area");
        }

        [Test]
        public void BarycentricInDoubleKeepsASliverTheFloatCutoffLost()
        {
            // Determinant 1e-16 in UV units: below the former float cutoff of 1e-15, yet a real triangle.
            Assert.That(MeshGeometry.Barycentric(new Vector2(2.5e-9f, 2.5e-9f), Vector2.zero, new Vector2(1e-8f, 0), new Vector2(0, 1e-8f), out var w), Is.True);
            Assert.That(w.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(w.y, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(w.z, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(MeshGeometry.Barycentric(Vector2.zero, Vector2.zero, Vector2.right, Vector2.right * 2, out _), Is.False, "collinear");
            Assert.That(MeshGeometry.Barycentric(new Vector2(float.NaN, 0), Vector2.zero, Vector2.right, Vector2.up, out _), Is.False);
        }
    }
}
