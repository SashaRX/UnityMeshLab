using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshPlanarCapTests
    {
        static readonly Vector3[] Box = {
            new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
            new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
        static readonly int[] Faces = { 0,2,1,0,3,2, 4,5,6,4,6,7, 0,1,5,0,5,4, 3,7,6,3,6,2, 0,4,7,0,7,3, 1,2,6,1,6,5 };

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void WeldsAttributeSplitBoxBeforeCapAndPreservesDonor(int missing)
        {
            var source = Faces.Where((v, k) => k / 6 != missing).Select(v => Box[v]).ToArray();
            var indices = Enumerable.Range(0, source.Length).ToArray();
            var saved = (Vector3[])source.Clone(); var savedIndices = (int[])indices.Clone();
            var a = RemeshPlanarCap.Prepare(source, indices, "0", default);
            var b = RemeshPlanarCap.Prepare(source, indices, "0", default);
            Assert.AreEqual(8, a.positions.Length); Assert.AreEqual(22, a.weldedVertices);
            Assert.AreEqual(2, a.addedFaces); Assert.AreEqual(1, a.loops);
            Assert.IsTrue(RemeshTopology.Inspect(a.positions, a.indices).Valid);
            Assert.AreEqual(0, RemeshTopology.Inspect(a.positions, a.indices).boundary.Count);
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(a.positions, a.indices, default).All(v => v));
            CollectionAssert.AreEqual(saved, source); CollectionAssert.AreEqual(savedIndices, indices);
            CollectionAssert.AreEqual(a.positions, b.positions); CollectionAssert.AreEqual(a.indices, b.indices);
            // Geometry of every donor face is preserved after the support remap.
            for (int i = 0; i < indices.Length; ++i) Assert.AreEqual(source[indices[i]], a.positions[a.indices[i]]);
        }

        [Test]
        public void SelectionPreservesOtherOpeningAndDoesNotInferBridge()
        {
            var ix = Faces.Skip(12).ToArray(); // opposite front/back faces absent
            var one = RemeshPlanarCap.Prepare(Box, ix, "0", default);
            Assert.AreEqual(2, one.loops); Assert.AreEqual(4, RemeshTopology.Inspect(one.positions, one.indices).boundary.Count);
            var both = RemeshPlanarCap.Prepare(Box, ix, "0,1", default);
            Assert.AreEqual(4, both.addedFaces); Assert.AreEqual(0, RemeshTopology.Inspect(both.positions, both.indices).boundary.Count);
            foreach (string invalid in new[] { "", "all", "bridge", "0,0", "2", "-1" })
                Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.Prepare(Box, ix, invalid, default));
        }

        [Test]
        public void NonplanarAdjacentFacesAndInvalidWeldTopologyRefuseAtomically()
        {
            var adjacent = Faces.Where((v, k) => k / 6 != 0 && k / 6 != 2).ToArray();
            StringAssert.Contains("not wholly planar", Assert.Throws<InvalidOperationException>(() =>
                RemeshPlanarCap.Prepare(Box, adjacent, "0", default)).Message);
            var p = new[] { Vector3.zero, Vector3.zero, Vector3.right };
            StringAssert.Contains("after weld", Assert.Throws<InvalidOperationException>(() =>
                RemeshPlanarCap.Prepare(p, new[] { 0, 1, 2 }, "0", default)).Message);
        }

        [Test]
        public void IntersectingObstacleRejectsCapEvenThoughSourceTopologyIsValid()
        {
            var p = Box.Concat(new[] { new Vector3(0,-2,0), new Vector3(0,0,0), new Vector3(.4f,-1,.4f) }).ToArray();
            var ix = Faces.Where((v, k) => k / 6 != 2).Concat(new[] { 8, 9, 10 }).ToArray();
            StringAssert.Contains("contacts face", Assert.Throws<InvalidOperationException>(() =>
                RemeshPlanarCap.Prepare(p, ix, "0", default)).Message);
        }

        [Test]
        public void CancellationAndInvalidGeometryDoNotPublishSupport()
        {
            var cancellation = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => RemeshPlanarCap.Prepare(Box, Faces, "0", cancellation));
            Assert.Throws<InvalidOperationException>(() => RemeshPlanarCap.Prepare(new[] { new Vector3(float.NaN,0,0) }, new[] {0,0,0}, "0", default));
        }

        [Test]
        public void SettingsInvalidateRemeshAndOlderSettingsKeepCapOff()
        {
            var a = new RemeshSettings(); string key = RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null);
            a.planarCap = true; Assert.AreNotEqual(key, RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null));
            key = RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null);
            a.planarCapLoops = "1"; Assert.AreNotEqual(key, RemeshPipeline.Key(RemeshPipeline.Stage.Remesh, a, null));
            Assert.IsFalse(RemeshSettings.FromSavedJson("{\"voxelResolution\":64}").planarCap);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeBoxCapSurvivesTrimAndSourceFittedSimplify(bool solve)
        {
            RemeshNative.CheckAvailable();
            var ix = Faces.Where((v, k) => k / 6 != 2).ToArray();
            var support = RemeshPlanarCap.Prepare(Box, ix, "0", default);
            var settings = new RemeshSettings { voxelResolution = 32, solve = solve, maximumError = .02f };
            RunGeometry(support, settings);
        }

        [TestCase(64, false)] [TestCase(64, true)]
        [TestCase(128, false)] [TestCase(128, true)]
        public void PrivateSandbagReplayThroughNativeTrimSimplify(int resolution, bool solve)
        {
            var support = PrivateSandbag();
            Assert.AreEqual(45, support.addedFaces); Assert.AreEqual(1, support.loops);
            var settings = new RemeshSettings { voxelResolution = resolution, solve = solve, maximumError = .005f,
                normalCrease = 133, normalSmoothing = 3, normalWeighting = RemeshNormalWeighting.FaceAreaAndCornerAngle,
                textureResolution = 512, padding = 3, mergeCharts = true };
            var simplified = RunGeometry(support, settings);
            var unwrapped = RemeshNative.Unwrap(simplified, settings, default);
            Assert.AreEqual(unwrapped.positions.Length, unwrapped.uv.Length);
            Assert.IsTrue(unwrapped.uv.All(uv => float.IsFinite(uv.x) && float.IsFinite(uv.y)));
            Assert.IsTrue(RemeshBaker.ClassifyCapFaces(unwrapped, support, default).Any(v => v));
            TestContext.WriteLine($"Sandbag r{resolution} solve={solve}: {support.Description}; simplified {simplified.TriangleCount}; charts {unwrapped.chartCount}");
        }

        static RemeshPlanarCap.Support PrivateSandbag()
        {
            string path = Environment.GetEnvironmentVariable("MESH_LAB_CAP_SOURCE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set MESH_LAB_CAP_SOURCE to the private decoded source.bin for this optional real-model replay.");
            using (var reader = new BinaryReader(File.OpenRead(path))) {
                int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
                var p = new Vector3[vertices]; var ix = new int[indices];
                for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (int i = 0; i < indices; ++i) ix[i] = reader.ReadInt32();
                return RemeshPlanarCap.Prepare(p, ix, "0", default);
            }
        }

        // Known native limitation measured during integration. This verifies refusal,
        // NOT successful geometry at 256. Do not delete the native guard to pass it.
        [TestCase(false)] [TestCase(true)]
        public void PrivateSandbag256StillRejectsInvalidNativeOutputBeforeTrim(bool solve)
        {
            var support = PrivateSandbag();
            Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(support.positions, support.indices, default).All(v => v));
            var settings = new RemeshSettings { voxelResolution = 256, solve = solve };
            var failure = Assert.Throws<InvalidOperationException>(() => RemeshNative.Voxelize(support.positions, support.indices, settings, default));
            StringAssert.Contains("before trim", failure.Message);
            StringAssert.Contains("duplicate faces 1", failure.Message);
        }

        [Test]
        public void BakeUsesOriginalDonorAndCountsMissingSyntheticSurfaceProjections()
        {
            var source = new RemeshSource { positions = (Vector3[])Box.Clone(), indices = Faces.Where((v,k) => k/6 != 2).ToArray(),
                normals = Box.Select(v => v.normalized).ToArray(), tangents = Box.Select(v => new Vector4(1,0,0,1)).ToArray(),
                uv = Box.Select(v => new Vector2(v.x,v.z)).ToArray(), uv2 = Box.Select(v => new Vector2(v.y,v.z)).ToArray(),
                colors = Box.Select(v => new Color(.2f,.3f,.7f,.4f)).ToArray(), hasColors = true,
                faceMaterials = new int[10], diagonal = Mathf.Sqrt(12), materials = new[] { new RemeshSource.Surface {
                    color = new RemeshSource.Map(), normal = new RemeshSource.Map(), metal = new RemeshSource.Map(),
                    ao = new RemeshSource.Map(), emission = new RemeshSource.Map(), tint = Color.white } } };
            var uv = (Vector2[])source.uv.Clone(); var uv2 = (Vector2[])source.uv2.Clone();
            var normals = (Vector3[])source.normals.Clone(); var tangents = (Vector4[])source.tangents.Clone();
            var colors = (Color[])source.colors.Clone(); var indices = (int[])source.indices.Clone();
            var support = RemeshPlanarCap.Prepare(source.positions, source.indices, "0", default);
            var target = new RemeshNative.Geometry { positions = new[] { Box[0],Box[1],Box[5],Box[4] },
                indices = new[] {0,1,2,0,2,3}, normals = Enumerable.Repeat(Vector3.down,4).ToArray(),
                uv = new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up}, charts = new int[4], chartCount = 1 };
            var maps = RemeshBaker.Bake(source, target, Enumerable.Repeat(new Vector4(1,0,0,1),4).ToArray(),
                new RemeshSettings { textureResolution = 64, padding = 1, dilationRadius = 0, projectionDistance = .001f, cageFit = false, bakeSamples = 1 },
                default, support: support);
            Assert.AreEqual(maps.covered, maps.capCovered); Assert.Greater(maps.capMisses,0);
            Assert.AreEqual(maps.misses,maps.capMisses); Assert.AreEqual(new Color32(255,0,255,255),maps.color[32*64+32]);
            CollectionAssert.AreEqual(uv,source.uv); CollectionAssert.AreEqual(uv2,source.uv2);
            CollectionAssert.AreEqual(normals,source.normals); CollectionAssert.AreEqual(tangents,source.tangents);
            CollectionAssert.AreEqual(colors,source.colors); CollectionAssert.AreEqual(indices,source.indices);
        }

        [Test]
        public void Version3CaptureKeepsOriginalAndWeldedPreparedGeometrySeparate()
        {
            string folder = Path.Combine(Path.GetTempPath(),"meshlab-cap-test-"+Guid.NewGuid().ToString("N"));
            var source = Faces.Where((v,k) => k/6 != 2).Select(v => Box[v]).ToArray();
            var ix = Enumerable.Range(0,source.Length).ToArray(); var support = RemeshPlanarCap.Prepare(source,ix,"0",default);
            try {
                string path = RemeshGeometryDiagnostics.WriteFailure(folder,source,ix,null,null,
                    new RemeshGeometryDiagnostics.FailureMetadata { stage = "Cap preparation" },support);
                using (var reader = new BinaryReader(File.OpenRead(path))) {
                    Assert.AreEqual(0x524D4C42,reader.ReadInt32()); Assert.AreEqual(3,reader.ReadInt32()); reader.ReadString();
                    Assert.IsTrue(reader.ReadBoolean()); Assert.AreEqual(30,reader.ReadInt32()); Assert.AreEqual(30,reader.ReadInt32());
                    for(int i=0;i<30;++i) Assert.AreEqual(source[i],new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()));
                    for(int i=0;i<30;++i) Assert.AreEqual(ix[i],reader.ReadInt32());
                    Assert.IsTrue(reader.ReadBoolean()); Assert.AreEqual(8,reader.ReadInt32()); Assert.AreEqual(36,reader.ReadInt32());
                    for(int i=0;i<8;++i) Assert.AreEqual(support.positions[i],new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle()));
                    for(int i=0;i<36;++i) Assert.AreEqual(support.indices[i],reader.ReadInt32());
                    Assert.IsFalse(reader.ReadBoolean()); Assert.IsFalse(reader.ReadBoolean()); Assert.AreEqual(reader.BaseStream.Length,reader.BaseStream.Position);
                }
            }
            finally {
                string expected = Path.Combine(Path.GetFullPath(Path.GetTempPath()),"meshlab-cap-test-");
                if (!Path.GetFullPath(folder).StartsWith(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
                Directory.Delete(folder,true);
            }
        }

        static RemeshNative.IndexedMesh RunGeometry(RemeshPlanarCap.Support support, RemeshSettings settings)
        {
            var voxel = RemeshNative.Voxelize(support.positions, support.indices, settings, default);
            var trimmed = RemeshTrim.Trim(voxel, support.positions, support.indices, 1f, default);
            Assert.AreEqual(0, trimmed.removed);
            var simplified = settings.solve ? RemeshSurfaceRefine.Simplify(trimmed.mesh, support.positions, support.indices, settings, default, out _)
                : RemeshNative.Simplify(trimmed.mesh, settings, default, out _);
            var topology = RemeshTopology.Inspect(simplified.positions, simplified.indices);
            Assert.IsTrue(topology.Valid, topology.Description); Assert.AreEqual(0, topology.boundary.Count, topology.Description);
            return simplified;
        }

        [Test]
        public void ConcaveRimTriangulatesWithoutClosingItsNotch()
        {
            var ring = new[] { new Vector3(0,0,0), new Vector3(2,0,0), new Vector3(2,1,0),
                new Vector3(1,1,0), new Vector3(1,2,0), new Vector3(0,2,0) };
            var p = ring.Concat(ring.Select(v => v + Vector3.forward)).ToArray();
            var ix = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 6; ++i) { int j = (i+1)%6; ix.AddRange(new[] {i,j,j+6,i,j+6,i+6}); }
            ix.AddRange(new[] {6,7,9,7,8,9,6,9,11,9,10,11});
            var support = RemeshPlanarCap.Prepare(p, ix.ToArray(), "0", default);
            Assert.AreEqual(4, support.addedFaces); Assert.AreEqual(0, RemeshTopology.Inspect(support.positions, support.indices).boundary.Count);
            float area = 0;
            for (int f = support.originalFaces; f < support.indices.Length/3; ++f) {
                var a = support.positions[support.indices[f*3]]; var b = support.positions[support.indices[f*3+1]];
                var c = support.positions[support.indices[f*3+2]];
                area += Vector3.Cross(b-a,c-a).magnitude*.5f;
                var center = (a+b+c)/3;
                Assert.IsFalse(center.x > 1 && center.y > 1, "Cap covered the concave notch.");
            }
            Assert.AreEqual(3, area, 1e-6f);
        }

        [Test]
        public void ExactContactAuditRejectsCrossingsCoplanarOverlapAndImproperAdjacentFaces()
        {
            var a = new[] { Vector3.zero, Vector3.right, Vector3.up };
            bool Hit(Vector3[] b) => RemeshCapIntersection.Improper(a.Select(RemeshCapIntersection.Point).ToArray(), b.Select(RemeshCapIntersection.Point).ToArray());
            Assert.IsFalse(Hit(new[] { Vector3.right, Vector3.zero, Vector3.down }));
            Assert.IsFalse(Hit(new[] { Vector3.zero, Vector3.back, Vector3.left }));
            Assert.IsTrue(Hit(new[] { Vector3.right, Vector3.zero, new Vector3(.2f,.2f,0) }));
            Assert.IsTrue(Hit(new[] { new Vector3(.2f,.2f,-1), new Vector3(.2f,.2f,1), new Vector3(.7f,.2f,0) }));
            Assert.IsTrue(Hit(new[] { new Vector3(.2f,.2f,0), new Vector3(.3f,.2f,0), new Vector3(.2f,.3f,0) }));
        }
    }
}
