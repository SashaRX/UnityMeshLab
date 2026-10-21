using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ShellLayoutScoreTests
    {
        // A unit square shell, faces 0 (corners 0, 1, 3) and 1 (corners 1, 2, 3), whose
        // edges keep every triangle frame exact, and one face (2) of another shell.
        static readonly Vector3[] Positions = {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(2, 1, 0),
        };
        static readonly int[] Triangles = { 0, 1, 3, 1, 2, 3, 4, 5, 6 };
        static readonly List<int> Faces = new List<int> { 0, 1 };
        static readonly int[] Vertices = { 0, 1, 2, 3 };
        static readonly List<int> Neighbours = new List<int> { 2 };

        static Vector2[] Layout(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
            => new[] { new Vector2(ax, ay), new Vector2(bx, by), new Vector2(cx, cy), new Vector2(dx, dy),
                new Vector2(.15f, .15f), new Vector2(.35f, .15f), new Vector2(.15f, .35f) };

        static ShellLayoutScore Measure(Vector2[] uv) => ShellLayoutScore.Measure(Faces, Vertices, Neighbours, Triangles, Positions, uv);

        static ShellLayoutScore Clear() => Measure(Layout(.6f, .6f, .8f, .6f, .8f, .8f, .6f, .8f));

        [Test]
        public void MovingTheWholeShellOffANeighbourDominates()
        {
            var before = Measure(Layout(.1f, .1f, .3f, .1f, .3f, .3f, .1f, .3f));
            var after = Clear();
            Assert.AreEqual(2, before.overlapPairs, "both faces cover part of the neighbour");
            Assert.AreEqual(0, after.overlapPairs);
            Assert.AreEqual(1, after.worstAnisotropy, 1e-9);
            Assert.IsTrue(ShellLayoutScore.Dominates(after, before, 0, 0));
            Assert.IsFalse(ShellLayoutScore.Dominates(before, after, 0, 0));
        }

        [Test]
        public void MovingOneCornerThatFlipsItsFaceIsDominated()
        {
            // The safety net moves an outlier corner alone; here its face flips over the other.
            var flipped = Measure(Layout(.6f, .6f, .8f, .6f, .55f, .55f, .6f, .8f));
            var clear = Clear();
            Assert.IsFalse(ShellLayoutScore.Dominates(flipped, clear, 0, 0));
            Assert.IsTrue(ShellLayoutScore.Dominates(clear, flipped, 0, 0));
        }

        [Test]
        public void RestoringCollapsedFacesWithStretchIsATradeoff()
        {
            var collapsed = Measure(Layout(.6f, .6f, .8f, .6f, .8f, .8f, .8f, .6f));
            var sheared = Measure(Layout(.6f, .6f, .8f, .6f, 1f, .8f, .6f, .8f));
            Assert.AreEqual(2, collapsed.zeroAreaFaces); Assert.AreEqual(2, collapsed.degenerateFaces);
            Assert.AreEqual(0, sheared.degenerateFaces); Assert.Greater(sheared.worstAnisotropy, 1.5);
            Assert.IsFalse(ShellLayoutScore.Dominates(sheared, collapsed, 0, 0), "fewer collapsed faces, but stretch appears");
            Assert.IsFalse(ShellLayoutScore.Dominates(collapsed, sheared, 0, 0));
        }

        [Test]
        public void TheAreaWeightedAnisotropyIsTheWholeMeshs()
        {
            // Both layouts stretch the square 2:1; the second also collapses face 1 onto a line.
            var whole = Measure(Layout(.5f, .5f, .75f, .5f, .75f, .625f, .5f, .625f));
            var collapsed = Measure(Layout(.5f, .5f, .75f, .5f, .625f, .5625f, .5f, .625f));
            Assert.AreEqual(2, whole.worstAnisotropy, 1e-9); Assert.AreEqual(2, collapsed.worstAnisotropy, 1e-9);
            Assert.AreEqual(1, collapsed.degenerateFaces);
            Assert.IsTrue(ShellLayoutScore.Dominates(whole, collapsed, 0, 0), "on its own the shell only gains a face");
            // With the rest of the mesh at anisotropy 1, the restored face raises the mesh's mean.
            Assert.IsFalse(ShellLayoutScore.Dominates(whole, collapsed, 10, 10));
        }
    }
}
