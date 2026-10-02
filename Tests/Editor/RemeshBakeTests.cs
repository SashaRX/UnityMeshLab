using System;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class RemeshBakeTests
    {
        static RemeshSource.Map Map() => new RemeshSource.Map();
        static RemeshSource Source()
        {
            return new RemeshSource {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                tangents = new[] { new Vector4(1,0,0,1), new Vector4(1,0,0,1), new Vector4(1,0,0,1) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, indices = new[] { 0,1,2 },
                colors = new[] { new Color(1,0,0,0.25f), new Color(0,1,0,0.5f), new Color(0,0,1,1) }, hasColors = true,
                faceMaterials = new[] { 0 }, diagonal = Mathf.Sqrt(2),
                materials = new[] { new RemeshSource.Surface { color = Map(), normal = Map(), metal = Map(), ao = Map(), emission = Map(),
                    tint = new Color(0.25f,0.5f,0.75f), emissionTint = new Color(4,2,1), metallic = 0.8f,
                    smoothness = 0.6f, aoStrength = 1, normalScale = 1 } }
            };
        }
        [Test]
        public void SettingsRejectNaNAndInvalidResolution()
        {
            Assert.Throws<ArgumentException>(() => new RemeshSettings { maximumError = float.NaN }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { textureResolution = 1000 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { projectionDistance = 0 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { bakeSamples = 3 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { chartIterations = 0 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { hullResolution = 2 }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { minPartSize = 0.6f }.Validate());
            Assert.Throws<ArgumentException>(() => new RemeshSettings { minRodVoxels = float.NaN }.Validate());
            Assert.DoesNotThrow(() => new RemeshSettings { targetTriangles = 0 }.Validate());
        }
        [Test]
        public void BarycentricSupportsBothWindings()
        {
            Assert.IsTrue(MeshGeometry.Barycentric(new Vector2(0.2f,0.3f), Vector2.zero, Vector2.right, Vector2.up, out var a));
            Assert.IsTrue(MeshGeometry.Barycentric(new Vector2(0.2f,0.3f), Vector2.zero, Vector2.up, Vector2.right, out var b));
            Assert.That(a.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(a.y, Is.EqualTo(b.z).Within(1e-5f));
            Assert.IsFalse(MeshGeometry.Barycentric(Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, out _));
        }
        [Test]
        public void BvhRaycastHitsMillimetreScaleTriangles()
        {
            // Edges of 1e-4: the old absolute parallel epsilon rejected det ≈ 1e-8 and the
            // ray passed straight through. The test is relative to the triangle now.
            float s = 1e-4f;
            var p = new[] { new Vector3(0,0,0), new Vector3(s,0,0), new Vector3(0,s,0) };
            var bvh = new TriangleBvh(p, new[] { 0,1,2 });
            var hit = bvh.Raycast(new Vector3(s*0.25f, s*0.25f, 1f), Vector3.back, 2f);
            Assert.AreEqual(0, hit.triangleIndex);
            Assert.That(hit.t, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(hit.barycentric.x + hit.barycentric.y + hit.barycentric.z, Is.EqualTo(1f).Within(1e-4f));
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(s*2f, s*2f, 1f), Vector3.back, 2f).triangleIndex, "beside the triangle");
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(s*0.25f, s*0.25f, 1f), Vector3.right, 2f).triangleIndex, "parallel to it");
        }
        [Test]
        public void BvhRaysAreWatertightAcrossSharedEdgesAndVertices()
        {
            // A 4×4 grid of quads split into triangles, all in z = 0; rays cast exactly
            // through every shared vertex and through points along every shared edge
            // must all hit (Möller–Trumbore can leak a ray between two triangles there).
            const int n = 4; var p = new System.Collections.Generic.List<Vector3>(); var idx = new System.Collections.Generic.List<int>();
            for (int y = 0; y <= n; ++y) for (int x = 0; x <= n; ++x) p.Add(new Vector3(x * 0.37f, y * 0.53f, 0));
            for (int y = 0; y < n; ++y) for (int x = 0; x < n; ++x) {
                int a = y * (n + 1) + x; idx.AddRange(new[] { a, a + 1, a + n + 2, a, a + n + 2, a + n + 1 });
            }
            var bvh = new TriangleBvh(p.ToArray(), idx.ToArray());
            var dirs = new[] { Vector3.back, new Vector3(0.3f, -0.2f, -1f).normalized, new Vector3(-0.7f, 0.4f, 1f).normalized };
            int tested = 0;
            foreach (var d in dirs) {
                // Shared vertices.
                for (int y = 1; y < n; ++y) for (int x = 1; x < n; ++x) {
                    var target = new Vector3(x * 0.37f, y * 0.53f, 0);
                    var hit = bvh.Raycast(target - d * 2f, d, 5f);
                    Assert.GreaterOrEqual(hit.triangleIndex, 0, $"vertex {x},{y} along {d}"); ++tested;
                }
                // Points along shared edges (horizontal, vertical and diagonal).
                for (int i = 1; i < 7; ++i) {
                    float s = i / 7f;
                    foreach (var target in new[] { new Vector3((1 + s) * 0.37f, 2 * 0.53f, 0), new Vector3(2 * 0.37f, (1 + s) * 0.53f, 0), new Vector3((1 + s) * 0.37f, (1 + s) * 0.53f, 0) }) {
                        var hit = bvh.Raycast(target - d * 2f, d, 5f);
                        Assert.GreaterOrEqual(hit.triangleIndex, 0, $"edge point {target} along {d}"); ++tested;
                        Assert.That(hit.t, Is.EqualTo(2f).Within(1e-4f));
                        var w = hit.barycentric; int f = hit.triangleIndex;
                        var at = p[idx[f * 3]] * w.x + p[idx[f * 3 + 1]] * w.y + p[idx[f * 3 + 2]] * w.z;
                        Assert.That((at - target).magnitude, Is.LessThan(1e-4f), "barycentric weights locate the hit");
                    }
                }
            }
            Assert.Greater(tested, 50);
            // Behind the origin and beyond the reach: no hit.
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(0.5f, 0.5f, -1f), Vector3.back, 5f).triangleIndex);
            Assert.AreEqual(-1, bvh.Raycast(new Vector3(0.5f, 0.5f, 3f), Vector3.back, 2f).triangleIndex);
        }
        [Test]
        public void BvhQueriesMatchBruteForceOnARandomSoup()
        {
            // The SAH build must not change what the queries answer: nearest point and
            // first ray hit agree with an exhaustive scan over 400 random triangles.
            var rng = new System.Random(7);
            float R() => (float)rng.NextDouble();
            int faces = 400; var p = new Vector3[faces * 3]; var idx = new int[faces * 3];
            for (int f = 0; f < faces; ++f) {
                var centre = new Vector3(R() * 4 - 2, R() * 4 - 2, R() * 4 - 2);
                for (int k = 0; k < 3; ++k) { p[f * 3 + k] = centre + new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f) * 0.6f; idx[f * 3 + k] = f * 3 + k; }
            }
            var bvh = new TriangleBvh(p, idx);
            var frameDir = Vector3.zero;
            for (int q = 0; q < 200; ++q) {
                var point = new Vector3(R() * 5 - 2.5f, R() * 5 - 2.5f, R() * 5 - 2.5f);
                var near = bvh.FindNearest(point);
                float brute = float.MaxValue;
                for (int f = 0; f < faces; ++f) {
                    var cp = TriangleBvh.ClosestPointOnTriangle(point, p[f * 3], p[f * 3 + 1], p[f * 3 + 2], out _);
                    brute = Mathf.Min(brute, (cp - point).sqrMagnitude);
                }
                Assert.That(near.distSq, Is.EqualTo(brute).Within(1e-5f), "nearest " + q);
                var dir = new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f).normalized;
                var hit = bvh.Raycast(point, dir, 10f);
                float bruteT = 10f; var frame = new TriangleBvh.RayFrame(dir);
                for (int f = 0; f < faces; ++f)
                    if (TriangleBvh.Watertight(in frame, point, p[f * 3], p[f * 3 + 1], p[f * 3 + 2], bruteT, out float t, out _)) bruteT = t;
                if (bruteT < 10f) { Assert.GreaterOrEqual(hit.triangleIndex, 0, "ray " + q); Assert.That(hit.t, Is.EqualTo(bruteT).Within(1e-5f)); }
                else Assert.AreEqual(-1, hit.triangleIndex, "ray " + q);
            }
        }
        [Test]
        public void MeshGeometryHelpersAgreeWithTheirDefinitions()
        {
            var p = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.zero, Vector3.right };
            var slots = MeshGeometry.WeldPositions(p, out int count);
            Assert.AreEqual(3, count); Assert.AreEqual(slots[0], slots[3]); Assert.AreEqual(slots[1], slots[4]); Assert.AreNotEqual(slots[0], slots[2]);
            var n = MeshGeometry.FaceNormals(p, new[] { 0,1,2, 0,0,1 });
            Assert.That(Vector3.Angle(n[0], Vector3.forward), Is.LessThan(1e-4f)); Assert.AreEqual(Vector3.zero, n[1]);
            var dirs = MeshGeometry.SphereDirections(64);
            Assert.AreEqual(64, dirs.Length);
            Vector3 sum = Vector3.zero;
            foreach (var d in dirs) { Assert.That(d.magnitude, Is.EqualTo(1f).Within(1e-5f)); sum += d; }
            Assert.That(sum.magnitude, Is.LessThan(2f), "evenly spread: the directions nearly cancel");
            Assert.That(MeshGeometry.SqDistToAabb(new Vector3(2,0,0), Vector3.zero, Vector3.one), Is.EqualTo(1f).Within(1e-6f));
            Assert.AreEqual(0f, MeshGeometry.SqDistToAabb(new Vector3(0.5f,0.5f,0.5f), Vector3.zero, Vector3.one));
            // A box carried through a 90° yaw and a translation stays axis-aligned and the right size.
            var yaw90 = new Matrix4x4(new Vector4(0, 0, -1, 0), new Vector4(0, 1, 0, 0), new Vector4(1, 0, 0, 0), new Vector4(10, 0, 0, 1));
            var yawed = MeshGeometry.TransformBounds(new Bounds(Vector3.zero, new Vector3(2, 1, 4)), yaw90);
            Assert.That((yawed.center - new Vector3(10, 0, 0)).magnitude, Is.LessThan(1e-4f));
            Assert.That((yawed.size - new Vector3(4, 1, 2)).magnitude, Is.LessThan(1e-4f));
            Assert.That(MeshGeometry.BoundsDistance(new Bounds(Vector3.zero, Vector3.one), new Bounds(new Vector3(3, 0, 0), Vector3.one)), Is.EqualTo(2f).Within(1e-6f));
            Assert.AreEqual(0f, MeshGeometry.BoundsDistance(new Bounds(Vector3.zero, Vector3.one), new Bounds(new Vector3(0.5f, 0, 0), Vector3.one)));
        }
        [Test]
        public void MaterialChannelsRemainSeparateAndEmissionRetainsHdr()
        {
            var source = Source();
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(1,0,0,1),
                out var color, out var normal, out var metal, out var ao, out var emission);
            Assert.That(color.r, Is.EqualTo(new Color(0.25f,0.5f,0.75f).gamma.r).Within(1e-5f));
            Assert.That(normal.r, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(normal.b, Is.EqualTo(1).Within(1e-5f));
            Assert.That(metal.r, Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(metal.a, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(ao.g, Is.EqualTo(1));
            Assert.That(emission.r, Is.EqualTo(4));
        }
        [Test]
        public void SourceNormalMapIsReprojectedIntoNewTangentFrame()
        {
            var source = Source();
            source.materials[0].normal.image = new RemeshSource.Image {
                width=1,height=1,pixels=new[] { new Color32(204,128,230,255) }, wrapU=TextureWrapMode.Clamp,wrapV=TextureWrapMode.Clamp };
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(0,1,0,1),
                out _,out var normal,out _,out _,out _);
            Assert.That(normal.r, Is.EqualTo(0.5f).Within(0.01f));
            Assert.That(normal.g, Is.EqualTo(0.2f).Within(0.01f));
            Assert.That(normal.b, Is.EqualTo(0.9f).Within(0.01f));
        }
        [Test]
        public void CoincidentSurfaceBakesWithoutMissesAndHonorsCancellation()
        {
            var source=Source();
            var target=new RemeshNative.Geometry { positions=source.positions, normals=source.normals, uv=source.uv, indices=source.indices };
            var settings=new RemeshSettings { textureResolution=64,padding=2 };
            var maps=RemeshBaker.Bake(source,target,source.tangents,settings,CancellationToken.None);
            Assert.That(maps.covered, Is.GreaterThan(1000));
            Assert.That(maps.misses, Is.Zero);
            using (var cancellation=new CancellationTokenSource()) {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => RemeshBaker.Bake(source,target,source.tangents,settings,cancellation.Token));
            }
        }
        [Test]
        public void SourceRootFollowsSelectionAndResolvesLodChildren()
        {
            var root=new GameObject("RemeshSelectionRoot");
            var child=GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.transform.SetParent(root.transform);
            root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(0.5f,new Renderer[] { child.GetComponent<Renderer>() }) });
            var light=new GameObject("RemeshSelectionLight",typeof(Light));
            var previous=Selection.objects;
            try {
                var tool=new RemeshBakeTool();
                Selection.activeGameObject=child; tool.FollowSelection();
                Assert.AreSame(root,tool.Source);
                Selection.activeGameObject=light; tool.FollowSelection();
                Assert.AreSame(root,tool.Source);
            }
            finally {
                Selection.objects=previous;
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(light);
            }
        }
        [Test]
        public void MultisamplingCoversThinChartEdges()
        {
            var source=Source();
            // A band between texel centres at 64² (row 6 centre is v=0.1016): centre-only
            // sampling sees no texel at all, 4×4 samples at v=0.1035 do.
            var sliver=new RemeshNative.Geometry { positions=source.positions, normals=source.normals,
                uv=new[] { new Vector2(0.1f,0.103f), new Vector2(0.9f,0.103f), new Vector2(0.9f,0.104f) }, indices=source.indices };
            Assert.Throws<InvalidOperationException>(() => RemeshBaker.Bake(source,sliver,source.tangents,
                new RemeshSettings { textureResolution=64,padding=1,bakeSamples=1 },CancellationToken.None));
            var super=RemeshBaker.Bake(source,sliver,source.tangents,new RemeshSettings { textureResolution=64,padding=1,bakeSamples=16 },CancellationToken.None);
            Assert.That(super.covered, Is.GreaterThan(0));
            Assert.That(super.misses, Is.Zero);
        }
        [Test]
        public void VertexColorAndAlphaTransferIndependently()
        {
            var source=Source();
            var target=new RemeshNative.Geometry { positions=source.positions, normals=source.normals, uv=source.uv, indices=source.indices };
            var bvh=new TriangleBvh(source.positions,source.indices);
            var both=RemeshBaker.TransferVertexColors(source,target,bvh,new RemeshSettings { transferVertexColor=true,transferVertexAlpha=true },CancellationToken.None);
            Assert.That(both[0].r, Is.EqualTo(1).Within(1e-4f));
            Assert.That(both[1].g, Is.EqualTo(1).Within(1e-4f));
            Assert.That(both[0].a, Is.EqualTo(0.25f).Within(1e-4f));
            var alpha=RemeshBaker.TransferVertexColors(source,target,bvh,new RemeshSettings { transferVertexAlpha=true },CancellationToken.None);
            Assert.That(alpha[2].a, Is.EqualTo(1).Within(1e-4f));
            Assert.That(alpha[2].b, Is.EqualTo(1));
            Assert.That(alpha[1].a, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(alpha[1].g, Is.EqualTo(1).Within(1e-4f));
            Assert.That(alpha[1].r, Is.EqualTo(1).Within(1e-4f));
        }
        [Test]
        public void CageKeepsDoubleSidedSheetApart()
        {
            // A quad with both windings on the SAME four vertices: a wall thinner than a
            // voxel after the simplifier collapsed its slab. A position weld sums the two
            // sides to nothing; the sided cage gives every front corner +z and every back
            // corner -z, with a uniform reach when no source is given.
            var p=new[] { Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up };
            var sheet=new RemeshNative.Geometry { positions=p, normals=new Vector3[4], indices=new[] { 0,1,2, 0,2,3, 0,2,1, 0,3,2 } };
            var cage=RemeshBaker.BuildCage(sheet, 0.1f, 2f, null);
            for (int c=0;c<6;++c) Assert.That(Vector3.Angle(cage.directions[c], Vector3.forward), Is.LessThan(0.01f), "front corner "+c);
            for (int c=6;c<12;++c) Assert.That(Vector3.Angle(cage.directions[c], Vector3.back), Is.LessThan(0.01f), "back corner "+c);
            Assert.AreEqual(4, cage.positions); Assert.AreEqual(8, cage.sides); Assert.AreEqual(4, cage.folded);
            Assert.AreEqual(4, cage.zeroNormals);
            foreach (float r in cage.reach) Assert.That(r, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(cage.Direction(0, new Vector3(0.2f,0.3f,0.5f)).z, Is.GreaterThan(0.99f));
            Assert.That(cage.Reach(2, new Vector3(0.2f,0.3f,0.5f)), Is.EqualTo(0.1f).Within(1e-6f));
        }
        [Test]
        public void CageWeldsSplitCopiesAcrossACrease()
        {
            // The 90° fold split along its shared edge (a chart border): the copies at one
            // position agree within 120°, so they share one side whose direction is the
            // average of both faces, and the sheet's far corners keep their own face.
            var p=new[] { Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, Vector3.zero, Vector3.right };
            var fold=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 5,4,3 } };
            RemeshNative.GenerateSplitNormals(fold, RemeshNormalWeighting.FaceArea);
            var cage=RemeshBaker.BuildCage(fold, 0.1f, 0f, null);
            var average=Vector3.Normalize(new Vector3(0,-1,-1));
            Assert.AreEqual(4, cage.positions); Assert.AreEqual(4, cage.sides); Assert.AreEqual(0, cage.folded);
            Assert.AreEqual(cage.side[0], cage.side[4], "coincident copies share a side");
            Assert.That(Vector3.Angle(cage.directions[0], average), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(cage.directions[4], average), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(cage.directions[2], Vector3.down), Is.LessThan(0.01f), "far corner of the floor face");
            Assert.That(Vector3.Angle(cage.directions[5], Vector3.back), Is.LessThan(0.01f), "far corner of the wall face");
            // Every direction leaves the front of its own face.
            for (int c=0;c<6;++c) {
                int f=c/3; int a=fold.indices[f*3], b=fold.indices[f*3+1], d=fold.indices[f*3+2];
                var fn=Vector3.Cross(p[b]-p[a], p[d]-p[a]).normalized;
                Assert.That(Vector3.Dot(cage.directions[c], fn), Is.GreaterThan(RemeshBaker.Cage.MinFacing));
            }
            Assert.AreEqual(4, cage.oneSided, "all four split copies along the crease sit 45° off the welded cage");
        }
        [Test]
        public void CageFitReachesTheSource()
        {
            // A flat target under a source sheet 0.3 above it: with a 0.1 projection
            // distance the plain cage misses the source; the fitted reach measures 0.3
            // along the ray, doubles it for oblique surfaces and stays within 8×.
            var target=new RemeshNative.Geometry { positions=new[] { Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up },
                normals=new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward }, indices=new[] { 0,1,2, 0,2,3 } };
            var sp=new[] { new Vector3(-2,-2,0.3f), new Vector3(3,-2,0.3f), new Vector3(3,3,0.3f), new Vector3(-2,3,0.3f) };
            var source=new TriangleBvh(sp, new[] { 0,1,2, 0,2,3 });
            var fitted=RemeshBaker.BuildCage(target, 0.1f, 2f, source);
            foreach (float r in fitted.reach) Assert.That(r, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(fitted.maxReach, Is.EqualTo(0.6f).Within(1e-4f));
            // Far beyond the 8× range the fit falls back to the projection distance.
            var far=new TriangleBvh(new[] { new Vector3(-2,-2,5), new Vector3(3,-2,5), new Vector3(3,3,5), new Vector3(-2,3,5) }, new[] { 0,1,2, 0,2,3 });
            foreach (float r in RemeshBaker.BuildCage(target, 0.1f, 2f, far).reach) Assert.That(r, Is.EqualTo(0.1f).Within(1e-6f));
            // Closer than the projection distance the reach stays at the distance.
            var near=new TriangleBvh(new[] { new Vector3(-2,-2,0.02f), new Vector3(3,-2,0.02f), new Vector3(3,3,0.02f), new Vector3(-2,3,0.02f) }, new[] { 0,1,2, 0,2,3 });
            foreach (float r in RemeshBaker.BuildCage(target, 0.1f, 2f, near).reach) Assert.That(r, Is.EqualTo(0.1f).Within(1e-6f));
        }
        [Test]
        public void UvIslandNormalsAreHardOnlyAtSplitVertices()
        {
            // Two faces folded 90° along x. Shared vertices smooth; split vertices stay hard.
            var p=new[] { Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, Vector3.zero, Vector3.right };
            var shared=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 1,0,3 } };
            RemeshNative.GenerateSplitNormals(shared, RemeshNormalWeighting.FaceArea);
            Assert.That(Vector3.Angle(shared.normals[0], Vector3.Normalize(new Vector3(0,-1,-1))), Is.LessThan(0.01f));
            var split=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=new[] { 0,1,2, 5,4,3 } };
            RemeshNative.GenerateSplitNormals(split, RemeshNormalWeighting.FaceArea);
            Assert.That(Vector3.Angle(split.normals[0], split.normals[4]), Is.EqualTo(90).Within(0.01f));
            // Corner-angle weighting keeps the same directions on this symmetric fold.
            var angled=new RemeshNative.Geometry { positions=(Vector3[])p.Clone(), normals=new Vector3[6], indices=new[] { 0,1,2, 1,0,3 } };
            RemeshNative.GenerateSplitNormals(angled, RemeshNormalWeighting.CornerAngle);
            Assert.That(Vector3.Angle(angled.normals[0], shared.normals[0]), Is.LessThan(0.01f));
            var both=new RemeshNative.Geometry { positions=(Vector3[])p.Clone(), normals=new Vector3[6], indices=new[] { 0,1,2, 1,0,3 } };
            RemeshNative.GenerateSplitNormals(both, RemeshNormalWeighting.FaceAreaAndCornerAngle);
            Assert.That(Vector3.Angle(both.normals[0], shared.normals[0]), Is.LessThan(0.01f));
        }
        [Test]
        public void SmoothModeWeldsChartBorderCopiesByNativeNormalGroup()
        {
            // The same 90° fold, split along its shared edge as xatlas splits a chart
            // border. With the native (smooth) normals as the group key the copies weld
            // back into one smooth normal; with crease-split native normals they stay hard.
            var p=new[] { Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, Vector3.zero, Vector3.right };
            var indices=new[] { 0,1,2, 5,4,3 };
            var smooth=Vector3.Normalize(new Vector3(0,-1,-1));
            var welded=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=indices };
            var groups=RemeshNative.GenerateSplitNormals(welded, RemeshNormalWeighting.FaceArea, new[] { smooth,smooth,smooth,smooth,smooth,smooth });
            Assert.That(Vector3.Angle(welded.normals[0], welded.normals[4]), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(welded.normals[0], smooth), Is.LessThan(0.01f));
            Assert.AreEqual(groups[0], groups[4], "coincident copies share a normal group");
            var creased=new RemeshNative.Geometry { positions=p, normals=new Vector3[6], indices=indices };
            RemeshNative.GenerateSplitNormals(creased, RemeshNormalWeighting.FaceArea,
                new[] { Vector3.back,Vector3.back,Vector3.back, Vector3.down,Vector3.down,Vector3.down });
            Assert.That(Vector3.Angle(creased.normals[0], creased.normals[4]), Is.EqualTo(90).Within(0.01f));
            // Smoothing over the welded groups keeps every member of a group identical.
            welded.normals[4]=Vector3.up;
            RemeshNative.SmoothNormals(welded, 1, groups);
            Assert.That(Vector3.Angle(welded.normals[0], welded.normals[4]), Is.LessThan(0.01f));
            // Tangents come out orthogonal to the final normal, handedness intact.
            welded.tangents=new Vector4[6];
            for (int i=0;i<6;++i) welded.tangents[i]=new Vector4(0,1,1,-1);
            RemeshNative.OrthogonalizeTangents(welded);
            Assert.That(Mathf.Abs(Vector3.Dot(welded.tangents[0], welded.normals[0])), Is.LessThan(1e-5f));
            Assert.AreEqual(-1f, welded.tangents[0].w);
        }
        [Test]
        public void LightmapRegionCoversTheRendererRectAndRemapsUv2()
        {
            // A renderer using the quarter [0.5,1]×[0.25,0.5] of a 1024² lightmap.
            var st=new Vector4(0.5f,0.25f,0.5f,0.25f);
            var region=RemeshBeauty.LightmapRegion(st,1024,1024,out var regionSt);
            float pad=1f/1024;
            Assert.That(region.xMin,Is.EqualTo(0.5f-pad).Within(1e-6f));
            Assert.That(region.xMax,Is.EqualTo(1f).Within(1e-6f));
            Assert.That(region.yMin,Is.EqualTo(0.25f-pad).Within(1e-6f));
            Assert.That(region.yMax,Is.EqualTo(0.5f+pad).Within(1e-6f));
            // uv2 (0,0) and (1,1) map to the rect's corners inside the region.
            Vector2 lo=new Vector2(regionSt.z,regionSt.w), hi=new Vector2(regionSt.x+regionSt.z,regionSt.y+regionSt.w);
            Assert.That(lo.x*region.width+region.x,Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(lo.y*region.height+region.y,Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(hi.x*region.width+region.x,Is.EqualTo(1f).Within(1e-5f));
            Assert.That(hi.y*region.height+region.y,Is.EqualTo(0.5f).Within(1e-5f));
            // A degenerate rect still yields a readable region.
            var tiny=RemeshBeauty.LightmapRegion(new Vector4(0,0,0.3f,0.3f),256,256,out _);
            Assert.That(tiny.width,Is.GreaterThan(0)); Assert.That(tiny.height,Is.GreaterThan(0));
        }
        [Test]
        public void ImageSamplingUsesLinearInterpolationAndWrapModes()
        {
            var image=new RemeshSource.Image { width=2,height=1,pixels=new[] { new Color32(0,0,0,255),new Color32(255,255,255,255) },
                srgb=true,wrapU=TextureWrapMode.Repeat,wrapV=TextureWrapMode.Clamp };
            Assert.That(image.Sample(new Vector2(0.5f,0.5f)).r,Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(image.Sample(new Vector2(1.25f,0.5f)).r,Is.EqualTo(0).Within(1e-5f));
            image.wrapU=TextureWrapMode.Clamp;
            Assert.That(image.Sample(new Vector2(1.25f,0.5f)).r,Is.EqualTo(1).Within(1e-5f));
        }

        // A 1×1 wall sheet at the origin, a 5-long × 0.01-wide rod strip at z = 3 (optionally
        // yawed 45° so its axis-aligned bounds read fat), two renderers, no shared vertices.
        // Rodrigues rotation: the test runs outside Unity too, where Quaternion is native.
        static Vector3 Rotate(Vector3 p, Vector3 axis, float degrees)
        {
            axis.Normalize();
            float c = Mathf.Cos(degrees * Mathf.Deg2Rad), s = Mathf.Sin(degrees * Mathf.Deg2Rad);
            return p * c + Vector3.Cross(axis, p) * s + axis * (Vector3.Dot(axis, p) * (1 - c));
        }
        static RemeshSource SheetAndRod(float diagonal, bool diagonalRod)
        {
            Vector3 R(float x, float y) => (diagonalRod ? Rotate(new Vector3(x, y, 0), Vector3.forward, 45) : new Vector3(x, y, 0)) + new Vector3(0, 0, 3);
            return new RemeshSource {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0),
                    R(0, 0), R(5, 0), R(5, 0.01f), R(0, 0.01f) },
                normals = new Vector3[8], tangents = new Vector4[8], uv = new Vector2[8],
                colors = new[] { Color.red, Color.red, Color.red, Color.red, Color.blue, Color.blue, Color.blue, Color.blue }, hasColors = true,
                indices = new[] { 0,1,2, 1,3,2, 4,5,6, 4,6,7 }, faceMaterials = new[] { 0, 0, 0, 0 }, faceLightmaps = new[] { -1, -1, -1, -1 },
                vertexRenderer = new[] { 0,0,0,0, 1,1,1,1 }, rendererToSpace = new[] { Matrix4x4.identity, Matrix4x4.identity },
                diagonal = diagonal, materials = new[] { new RemeshSource.Surface() }
            };
        }
        [Test]
        public void FilterSmallPartsDropsRodsNotSheetsAndCompactsEveryStream()
        {
            // Longest side 5 (x) at resolution 50 → one cell = 0.1: the 0.01-wide rod's section is
            // under a cell, the sheet's second extent (1) is ten cells — it stays.
            var source = SheetAndRod(10, false);
            Assert.IsTrue(source.FilterSmallParts(0, 1f, 50, out int small, out int rods));
            Assert.AreEqual(0, small); Assert.AreEqual(1, rods);
            Assert.AreEqual(4, source.positions.Length);
            Assert.AreEqual(new[] { 0,1,2, 1,3,2 }, source.indices);
            Assert.AreEqual(2, source.faceMaterials.Length); Assert.AreEqual(2, source.faceLightmaps.Length);
            Assert.AreEqual(4, source.colors.Length); Assert.AreEqual(Color.red, source.colors[3]);
            Assert.AreEqual(new[] { 0,0,0,0 }, source.vertexRenderer);
            Assert.AreEqual(10, source.diagonal);
            // The same rod yawed 45°: its axis-aligned box is 3.5 × 3.5, its principal section still 0.01.
            var yawed = SheetAndRod(10, true);
            Assert.IsTrue(yawed.FilterSmallParts(0, 1f, 50, out small, out rods));
            Assert.AreEqual(1, rods); Assert.AreEqual(4, yawed.positions.Length);
            // A coarser grid (cell 0.5) still keeps the sheet: one thin extent is not a rod.
            var coarse = SheetAndRod(10, false);
            Assert.IsTrue(coarse.FilterSmallParts(0, 1f, 10, out _, out rods));
            Assert.AreEqual(1, rods); Assert.AreEqual(4, coarse.positions.Length);
            // Size test: the rod's extent (5) passes a 0.2 × 10 floor, the sheet's (√2) does not.
            var sized = SheetAndRod(10, false);
            Assert.IsTrue(sized.FilterSmallParts(0.2f, 0, 50, out small, out rods));
            Assert.AreEqual(1, small); Assert.AreEqual(0, rods); Assert.AreEqual(4, sized.positions.Length);
            Assert.AreEqual(Color.blue, sized.colors[0]);
            // A filter that would drop everything is refused and changes nothing.
            var all = SheetAndRod(10, false);
            Assert.IsFalse(all.FilterSmallParts(0.9f, 0, 50, out small, out rods));
            Assert.AreEqual(0, small); Assert.AreEqual(0, rods); Assert.AreEqual(8, all.positions.Length);
            // 0/0 is a no-op.
            Assert.IsTrue(SheetAndRod(10, false).FilterSmallParts(0, 0, 50, out _, out _));
        }
        [Test]
        public void ClassifyPartsLabelsFacesWithoutChangingTheCapture()
        {
            var source = SheetAndRod(10, false);
            var cls = source.ClassifyParts(0, 1f, 50);
            Assert.AreEqual(new[] { RemeshSource.PartKept, RemeshSource.PartKept, RemeshSource.PartRod, RemeshSource.PartRod }, cls);
            Assert.AreEqual(8, source.positions.Length); Assert.AreEqual(12, source.indices.Length);
            Assert.AreEqual(RemeshSource.PartSmall, SheetAndRod(10, false).ClassifyParts(0.2f, 0, 50)[0]);
            Assert.IsNull(SheetAndRod(10, false).ClassifyParts(0, 0, 50));
        }
        static void Cube(out Vector3[] positions, out int[] indices, bool inverted)
        {
            positions = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1) };
            // Unity-style clockwise-from-outside faces, the winding OrientedBoxes emits.
            int[] faces = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            indices = new int[36];
            for (int i = 0; i < 36; i += 3) { indices[i] = faces[i]; indices[i+1] = faces[inverted ? i+2 : i+1]; indices[i+2] = faces[inverted ? i+1 : i+2]; }
        }
        [Test]
        public void WindingProbeJudgesFromOutsideAndStaysOffOnOpenSheets()
        {
            Cube(out var p, out var tri, false);
            var normals = new Vector3[12];
            for (int f = 0; f < 12; ++f) normals[f] = Vector3.Cross(p[tri[f*3+1]] - p[tri[f*3]], p[tri[f*3+2]] - p[tri[f*3]]).normalized;
            int outward = RemeshBaker.ProbeWinding(new TriangleBvh(p, tri), p, normals);
            Cube(out p, out tri, true);
            for (int f = 0; f < 12; ++f) normals[f] = Vector3.Cross(p[tri[f*3+1]] - p[tri[f*3]], p[tri[f*3+2]] - p[tri[f*3]]).normalized;
            int inverted = RemeshBaker.ProbeWinding(new TriangleBvh(p, tri), p, normals);
            // The two windings are told apart with certainty, whatever the sign convention names them.
            Assert.AreNotEqual(0, outward); Assert.AreEqual(-outward, inverted);
            // A single sheet is seen from both sides half the time: no verdict, filter stays off.
            var sheet = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) };
            var sheetTri = new[] { 0, 1, 2, 1, 3, 2 };
            var sheetN = new[] { Vector3.back, Vector3.back };
            Assert.AreEqual(0, RemeshBaker.ProbeWinding(new TriangleBvh(sheet, sheetTri), sheet, sheetN));
        }
        [Test]
        public void TrimKeepsTheSideTheSourceHasAndDropsTheSlabsBackAndRims()
        {
            // Source: one quad whose front faces -z (Unity's Quad winding). Remesh: a thin
            // slab around it (a box 1×1×0.05), as the voxelizer returns for an open sheet.
            var sourcePositions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
            var sourceIndices = new[] { 0, 2, 1, 0, 3, 2 };
            Cube(out var p, out var tri, false);
            for (int i = 0; i < p.Length; ++i) p[i] = new Vector3(p[i].x, p[i].y, p[i].z * 0.05f - 0.025f);
            var slab = new RemeshNative.IndexedMesh { positions = p, indices = tri };
            var trimmed = RemeshTrim.Trim(slab, sourcePositions, sourceIndices, 0.1f, CancellationToken.None, out int removed);
            // The two faces on the source's front side (-z) survive; the back and the four rims (10 faces) go.
            Assert.AreEqual(10, removed);
            Assert.AreEqual(2, trimmed.TriangleCount);
            Assert.AreEqual(4, trimmed.positions.Length);
            foreach (var v in trimmed.positions) Assert.That(v.z, Is.EqualTo(-0.025f).Within(1e-5f));
            // Orientation decides, not position: the remesher fits both slab faces onto the
            // sheet itself, so only the face whose normal agrees with the source can be
            // told apart. The same slab wound inside out keeps the face at +z whose
            // (inverted) normal points -z like the source.
            Cube(out var pi, out var triInverted, true);
            for (int i = 0; i < pi.Length; ++i) pi[i] = new Vector3(pi[i].x, pi[i].y, pi[i].z * 0.05f - 0.025f);
            var inverted = RemeshTrim.Trim(new RemeshNative.IndexedMesh { positions = pi, indices = triInverted }, sourcePositions, sourceIndices, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(10, removed);
            foreach (var v in inverted.positions) Assert.That(v.z, Is.EqualTo(0.025f).Within(1e-5f));
            for (int f = 0; f < inverted.indices.Length; f += 3) {
                var fn = Vector3.Cross(inverted.positions[inverted.indices[f + 1]] - inverted.positions[inverted.indices[f]],
                    inverted.positions[inverted.indices[f + 2]] - inverted.positions[inverted.indices[f]]).normalized;
                Assert.That(Vector3.Dot(fn, Vector3.back), Is.GreaterThan(0.99f));
            }
            // A zero-thickness double-sided sheet — what the remesher really returns for an
            // open source — keeps exactly the winding that matches the source.
            var flat = new RemeshNative.IndexedMesh { positions = (Vector3[])sourcePositions.Clone(),
                indices = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 } };
            var oneSided = RemeshTrim.Trim(flat, sourcePositions, sourceIndices, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(2, removed); Assert.AreEqual(2, oneSided.TriangleCount); Assert.AreEqual(4, oneSided.positions.Length);
            for (int f = 0; f < oneSided.indices.Length; f += 3) {
                var fn = Vector3.Cross(oneSided.positions[oneSided.indices[f + 1]] - oneSided.positions[oneSided.indices[f]],
                    oneSided.positions[oneSided.indices[f + 2]] - oneSided.positions[oneSided.indices[f]]).normalized;
                Assert.That(Vector3.Dot(fn, Vector3.back), Is.GreaterThan(0.99f));
            }
            // A source facing +z keeps the +z side instead.
            var flippedSource = RemeshTrim.Trim(slab, sourcePositions, new[] { 0, 1, 2, 0, 2, 3 }, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(10, removed);
            foreach (var v in flippedSource.positions) Assert.That(v.z, Is.EqualTo(0.025f).Within(1e-5f));
            // A closed source keeps the whole remesh: the slab against a copy of itself.
            var whole = RemeshTrim.Trim(slab, p, tri, 0.1f, CancellationToken.None, out removed);
            Assert.AreEqual(0, removed); Assert.AreEqual(12, whole.TriangleCount);
            // The classes name why each raw face went: 2 kept, 2 back (a source face within
            // reach faces the other way), 8 rims (nothing parallel within reach).
            var result = RemeshTrim.Trim(slab, sourcePositions, sourceIndices, 0.1f, CancellationToken.None);
            int kept = 0, back = 0, rim = 0;
            foreach (byte c in result.classes) { if (c == RemeshTrim.Kept) ++kept; else if (c == RemeshTrim.Back) ++back; else ++rim; }
            Assert.AreEqual(2, kept); Assert.AreEqual(2, back); Assert.AreEqual(8, rim);
            Assert.IsFalse(result.gaveUp); Assert.AreEqual(0, result.flipped);
        }
        [Test]
        public void OrientConsistentlyRewindsTheMinorityOfEachPiece()
        {
            // A 3-quad strip with the middle quad wound the other way: both of its
            // triangles flip; the majority (4 faces) keeps its winding. A detached quad
            // keeps its own orientation (no shared edge constrains it).
            var p = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(2,0,0), new Vector3(3,0,0),
                            new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(2,1,0), new Vector3(3,1,0),
                            new Vector3(5,0,0), new Vector3(6,0,0), new Vector3(6,1,0), new Vector3(5,1,0) };
            var mesh = new RemeshNative.IndexedMesh { positions = p, indices = new[] {
                0,1,5, 0,5,4,        // +z
                1,6,2, 1,5,6,        // -z (flipped quad)
                2,3,7, 2,7,6,        // +z
                8,10,9, 8,11,10 } }; // detached, -z
            int flipped = RemeshTrim.OrientConsistently(mesh);
            Assert.AreEqual(2, flipped);
            for (int f = 0; f < 6; ++f) {
                var n = Vector3.Cross(p[mesh.indices[f*3+1]] - p[mesh.indices[f*3]], p[mesh.indices[f*3+2]] - p[mesh.indices[f*3]]);
                Assert.That(n.z, Is.GreaterThan(0), "strip face " + f);
            }
            for (int f = 6; f < 8; ++f) {
                var n = Vector3.Cross(p[mesh.indices[f*3+1]] - p[mesh.indices[f*3]], p[mesh.indices[f*3+2]] - p[mesh.indices[f*3]]);
                Assert.That(n.z, Is.LessThan(0), "detached face " + f);
            }
            // A sheet trimmed from a source with alternating winding comes out orientable:
            // the remesh keeps per face the side agreeing with the source, then re-winds.
            var src = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(2,0,0), new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(2,1,0) };
            var srcIdx = new[] { 0,1,4, 0,4,3,  1,2,5, 1,5,4 };  // both +z
            srcIdx = new[] { 0,1,4, 0,4,3,  1,5,2, 1,4,5 };      // right quad -z
            var sheet = new RemeshNative.IndexedMesh { positions = (Vector3[])src.Clone(),
                indices = new[] { 0,1,4, 0,4,3, 1,2,5, 1,5,4,  0,4,1, 0,3,4, 1,5,2, 1,4,5 } };  // both sides, both windings
            var trimmed = RemeshTrim.Trim(sheet, src, srcIdx, 0.1f, CancellationToken.None);
            Assert.AreEqual(4, trimmed.removed); Assert.AreEqual(4, trimmed.mesh.TriangleCount);
            Assert.AreEqual(2, trimmed.flipped, "the right quad's two faces, kept as -z like the source, are re-wound to the majority");
            var tp = trimmed.mesh.positions; var ti = trimmed.mesh.indices;
            for (int f = 0; f < 4; ++f)
                Assert.That(Vector3.Cross(tp[ti[f*3+1]] - tp[ti[f*3]], tp[ti[f*3+2]] - tp[ti[f*3]]).z, Is.GreaterThan(0));
        }
        [Test]
        public void TwoSidedFacesFollowTheMaterialsOrTheSetting()
        {
            var source = Source();
            Assert.IsNull(source.TwoSidedFaces(RemeshBackfaces.FromMaterials), "no two-sided material");
            Assert.IsNull(source.TwoSidedFaces(RemeshBackfaces.Never));
            CollectionAssert.AreEqual(new[] { true }, source.TwoSidedFaces(RemeshBackfaces.Always));
            source.materials[0].twoSided = true;
            CollectionAssert.AreEqual(new[] { true }, source.TwoSidedFaces(RemeshBackfaces.FromMaterials));
            Assert.IsNull(source.TwoSidedFaces(RemeshBackfaces.Never), "Never overrides the material");
            // The bake accepts a two-sided face from behind: a sheet facing away from the
            // target bakes without misses when its material is two-sided.
            var target = new RemeshNative.Geometry { positions = source.positions, normals = source.normals, uv = source.uv, indices = source.indices };
            var away = Source(); away.indices = new[] { 0, 2, 1 }; away.materials[0].twoSided = true;
            var maps = RemeshBaker.Bake(away, target, source.tangents, new RemeshSettings { textureResolution = 64, padding = 2, sourceBackfaces = RemeshBackfaces.FromMaterials }, CancellationToken.None);
            Assert.AreEqual(0, maps.misses); Assert.AreEqual(1, maps.twoSidedFaces);
        }
        [Test]
        public void PrincipalExtentsFollowThePointSetsOwnAxes()
        {
            var pts = new System.Collections.Generic.List<Vector3>();
            foreach (var x in new[] { 0f, 4f }) foreach (var y in new[] { 0f, 2f }) foreach (var z in new[] { 0f, 0.5f })
                pts.Add(Rotate(Rotate(new Vector3(x, y, z), Vector3.up, 45), new Vector3(1, 0, 1), 30) + Vector3.one * 7);
            var e = RemeshSource.PrincipalExtents(pts);
            Assert.That(e.x, Is.EqualTo(4).Within(1e-3f));
            Assert.That(e.y, Is.EqualTo(2).Within(1e-3f));
            Assert.That(e.z, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.AreEqual(Vector3.zero, RemeshSource.PrincipalExtents(new System.Collections.Generic.List<Vector3>()));
        }
        [Test]
        public void OrientedBoxesFollowTheRendererAxesAndWindOutward()
        {
            // A 2×1 rectangle authored in renderer-local XZ, the renderer yawed 45°.
            var yaw = Matrix4x4.TRS(new Vector3(3,0,0), Quaternion.Euler(0,45,0), Vector3.one);
            Vector3[] local = { Vector3.zero, new Vector3(2,0,0), new Vector3(2,0,1), new Vector3(0,0,1) };
            var source = new RemeshSource {
                positions = new Vector3[4], indices = new[] { 0,1,2, 0,2,3 },
                vertexRenderer = new[] { 0,0,0,0 }, rendererToSpace = new[] { yaw }
            };
            for (int i = 0; i < 4; ++i) source.positions[i] = yaw.MultiplyPoint3x4(local[i]);
            var box = source.OrientedBoxes();
            Assert.AreEqual(8, box.positions.Length); Assert.AreEqual(36, box.indices.Length);
            // Corners land on the local 2×0×1 box, not on the ~2.1×2.1 axis-aligned one.
            var inv = yaw.inverse; var mn = Vector3.one * float.MaxValue; var mx = Vector3.one * float.MinValue;
            foreach (var p in box.positions) { var l = inv.MultiplyPoint3x4(p); mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l); }
            Assert.That(mn.x, Is.EqualTo(0).Within(1e-5f)); Assert.That(mx.x, Is.EqualTo(2).Within(1e-5f));
            Assert.That(mn.z, Is.EqualTo(0).Within(1e-5f)); Assert.That(mx.z, Is.EqualTo(1).Within(1e-5f));
            // The flat axis is padded to 0.4% of the longest side, centred on the sheet.
            Assert.That(mx.y - mn.y, Is.EqualTo(0.008f).Within(1e-5f));
            Assert.That(mx.y + mn.y, Is.EqualTo(0).Within(1e-5f));
            // A mirrored renderer gets its winding flipped back, so the signed volume keeps its sign.
            float Volume(RemeshNative.IndexedMesh m) {
                float v = 0;
                for (int i = 0; i < m.indices.Length; i += 3)
                    v += Vector3.Dot(m.positions[m.indices[i]], Vector3.Cross(m.positions[m.indices[i+1]], m.positions[m.indices[i+2]]));
                return v / 6;
            }
            var cube = new RemeshSource { positions = new[] { Vector3.zero, Vector3.one }, indices = new int[0],
                vertexRenderer = new[] { 0, 0 }, rendererToSpace = new[] { Matrix4x4.identity } };
            var mirrored = new RemeshSource { positions = new[] { Vector3.zero, new Vector3(-1,1,1) }, indices = new int[0],
                vertexRenderer = new[] { 0, 0 }, rendererToSpace = new[] { Matrix4x4.Scale(new Vector3(-1,1,1)) } };
            float straight = Volume(cube.OrientedBoxes()), flipped = Volume(mirrored.OrientedBoxes());
            Assert.That(Mathf.Abs(straight), Is.EqualTo(1).Within(1e-5f));
            Assert.That(flipped, Is.EqualTo(straight).Within(1e-5f));
            // No vertices at all → no box.
            Assert.IsNull(new RemeshSource { positions = new Vector3[0], indices = new int[0], vertexRenderer = new int[0],
                rendererToSpace = new[] { Matrix4x4.identity } }.OrientedBoxes());
        }
        [Test]
        public void VertexColorTintMultipliesTheLinearAlbedo()
        {
            var source = Source();
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(1,0,0,1),true,
                out var color, out _, out _, out _, out _);
            // Vertex 0 is pure red: the tinted albedo keeps only the red channel of the material tint.
            Assert.That(color.r, Is.EqualTo(new Color(0.25f,0.5f,0.75f).gamma.r).Within(1e-5f));
            Assert.That(color.g, Is.EqualTo(0).Within(1e-5f)); Assert.That(color.b, Is.EqualTo(0).Within(1e-5f));
            Assert.AreEqual(1, color.a);
            // Half-way between red and green vertices: both channels at half strength, blue gone.
            RemeshBaker.Evaluate(source,0,new Vector3(0.5f,0.5f,0),Vector3.forward,new Vector4(1,0,0,1),true,
                out color, out _, out _, out _, out _);
            Assert.That(color.r, Is.EqualTo(new Color(0.125f,0,0).gamma.r).Within(1e-5f));
            Assert.That(color.g, Is.EqualTo(new Color(0,0.25f,0).gamma.g).Within(1e-5f));
            Assert.That(color.b, Is.EqualTo(0).Within(1e-5f));
            // Off: untouched.
            RemeshBaker.Evaluate(source,0,new Vector3(1,0,0),Vector3.forward,new Vector4(1,0,0,1),false,
                out color, out _, out _, out _, out _);
            Assert.That(color.g, Is.EqualTo(new Color(0.25f,0.5f,0.75f).gamma.g).Within(1e-5f));
        }
        [Test]
        public void NearestSeedsAssignsEveryTexelToItsClosestSeed()
        {
            var seed = new bool[16]; seed[0] = true; seed[15] = true;
            var nearest = RemeshBaker.NearestSeeds(seed, 4, CancellationToken.None);
            Assert.AreEqual(0, nearest[0]); Assert.AreEqual(0, nearest[1]); Assert.AreEqual(0, nearest[4]);
            Assert.AreEqual(15, nearest[15]); Assert.AreEqual(15, nearest[14]); Assert.AreEqual(15, nearest[11]);
            foreach (var n in nearest) Assert.That(n, Is.GreaterThanOrEqualTo(0));
            // No seeds at all: everything stays unassigned.
            foreach (var n in RemeshBaker.NearestSeeds(new bool[16], 4, CancellationToken.None)) Assert.AreEqual(-1, n);
        }
    }
}
