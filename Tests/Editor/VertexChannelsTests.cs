// VertexChannelsTests.cs — the scalar vertex channel: decoding, read/write round trips,
// the submesh write, levels and blend, colour snapshots.
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class VertexChannelsTests
    {
        Mesh mesh;

        [SetUp]
        public void SetUp()
        {
            mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 1, 3, 2 }, 1);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(mesh);

        [TestCase(AOTargetChannel.VertexColorR, true, 0, -1, -1, "Vertex Color R")]
        [TestCase(AOTargetChannel.VertexColorA, true, 3, -1, -1, "Vertex Color A")]
        [TestCase(AOTargetChannel.UV0_X, false, -1, 0, 0, "UV0 X")]
        [TestCase(AOTargetChannel.UV2_Y, false, -1, 2, 1, "UV2 Y")]
        [TestCase(AOTargetChannel.UV4_Y, false, -1, 4, 1, "UV4 Y")]
        public void ChannelsDecodeToComponentAndSet(AOTargetChannel ch, bool isColor, int colorComp, int uvSet, int uvComp, string name)
        {
            Assert.AreEqual(isColor, VertexChannels.IsColor(ch));
            Assert.AreEqual(colorComp, VertexChannels.ColorComponent(ch));
            Assert.AreEqual(uvSet, VertexChannels.UvChannel(ch));
            Assert.AreEqual(uvComp, VertexChannels.UvComponent(ch));
            Assert.AreEqual(name, VertexChannels.Name(ch));
        }

        [Test]
        public void ColorWriteKeepsTheOtherComponentsOverAWhiteDefault()
        {
            Assert.IsNull(VertexChannels.Read(mesh, AOTargetChannel.VertexColorG), "no colours yet");
            VertexChannels.Write(mesh, new[] { 0f, .5f, 1f, 2f }, AOTargetChannel.VertexColorG);
            var c = mesh.colors32;
            Assert.AreEqual(255, c[0].r, "untouched components start white");
            Assert.AreEqual(0, c[0].g);
            Assert.AreEqual(127, c[1].g);
            Assert.AreEqual(255, c[3].g, "clamped");
            var back = VertexChannels.Read(mesh, AOTargetChannel.VertexColorG);
            Assert.AreEqual(127f / 255f, back[1], 1e-6f);
            VertexChannels.Write(mesh, new[] { 1f, 1f, 1f, 1f }, AOTargetChannel.VertexColorA);
            Assert.AreEqual(127, mesh.colors32[1].g, "a write to A leaves G alone");
        }

        [Test]
        public void UvWriteKeepsTheOtherComponentOverAZeroDefault()
        {
            VertexChannels.Write(mesh, new[] { .1f, .2f, .3f, .4f }, AOTargetChannel.UV2_Y);
            var uv = new System.Collections.Generic.List<Vector2>();
            mesh.GetUVs(2, uv);
            Assert.AreEqual(new Vector2(0f, .2f), uv[1]);
            VertexChannels.Write(mesh, new[] { .9f, .9f, .9f, .9f }, AOTargetChannel.UV2_X);
            mesh.GetUVs(2, uv);
            Assert.AreEqual(new Vector2(.9f, .2f), uv[1]);
            Assert.AreEqual(.2f, VertexChannels.Read(mesh, AOTargetChannel.UV2_Y)[1], 1e-6f);
            Assert.IsNull(VertexChannels.Read(mesh, AOTargetChannel.UV3_X));
        }

        [Test]
        public void MismatchedLengthsWriteNothing()
        {
            VertexChannels.Write(mesh, new[] { 1f }, AOTargetChannel.VertexColorR);
            Assert.AreEqual(0, mesh.colors32.Length);
            Assert.IsFalse(VertexChannels.WriteSubmesh(mesh, new float[4], AOTargetChannel.VertexColorR, 5), "submesh out of range");
        }

        [Test]
        public void SubmeshWriteTouchesOnlyThatSubmeshesVertices()
        {
            // Vertex 0 (submesh 0 only) carries an out-of-range value the write must not touch.
            mesh.SetUVs(1, new System.Collections.Generic.List<Vector2> { new Vector2(2f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f) });
            Assert.IsTrue(VertexChannels.WriteSubmesh(mesh, new[] { 0f, 0f, 0f, 0f }, AOTargetChannel.UV1_X, 1));
            var raw = new System.Collections.Generic.List<Vector2>();
            mesh.GetUVs(1, raw);
            Assert.AreEqual(2f, raw[0].x, 1e-6f, "vertex 0 is only in submesh 0 and keeps its raw value");
            Assert.AreEqual(0f, raw[1].x, 1e-6f);
            Assert.AreEqual(0f, raw[3].x, 1e-6f);
            Assert.AreEqual(.5f, raw[3].y, 1e-6f, "the other component is untouched");
        }

        [Test]
        public void LevelsAndBlendAreTheTabsFormulas()
        {
            var v = new[] { 0f, .5f, 1f };
            VertexChannels.Levels(v, .1f, 2f);
            Assert.AreEqual(new[] { 0f, .6f, 1f }, v);
            var blended = VertexChannels.Blend(new[] { 0f, 1f }, new[] { 1f, 0f }, .25f);
            Assert.AreEqual(.25f, blended[0], 1e-6f);
            Assert.AreEqual(.75f, blended[1], 1e-6f);
            Assert.AreEqual(new[] { 0f, 1f }, VertexChannels.Blend(new[] { 0f, 1f }, null, .5f), "no partner: a copy");
            Assert.AreEqual(new byte[] { 0, 127, 255 }, System.Array.ConvertAll(VertexChannels.Greyscale(new[] { 0f, .5f, 1f }), c => c.r));
        }

        [Test]
        public void ColourSnapshotsRoundTripAndNullClears()
        {
            Assert.IsNull(VertexChannels.SnapshotColors(mesh));
            VertexChannels.FillColors(mesh, Color.red, null);
            var snap = VertexChannels.SnapshotColors(mesh);
            Assert.AreEqual(255, snap[2].r);
            VertexChannels.FillColors(mesh, Color.blue, null);
            VertexChannels.RestoreColors(mesh, snap, null);
            Assert.AreEqual(255, mesh.colors32[2].r);
            VertexChannels.RestoreColors(mesh, null, null);
            Assert.AreEqual(0, mesh.colors32[2].r, "a null snapshot clears to black");
        }
    }
}
