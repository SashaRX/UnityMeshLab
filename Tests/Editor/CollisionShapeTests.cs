using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class CollisionShapeTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
            owned.Clear();
            Undo.ClearAll();
        }

        Mesh Mesh(RemeshNative.IndexedMesh geometry, Quaternion rotation, float scale)
        {
            var mesh = new Mesh(); owned.Add(mesh);
            mesh.vertices = geometry.positions.Select(p => rotation * p * scale + new Vector3(3,5,-2) * scale).ToArray();
            mesh.triangles = geometry.indices; mesh.RecalculateBounds(); return mesh;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeBoxOccludersAreCollectedAlongsideProfilesAndReleased(bool mixed)
        {
            var root = new GameObject("CollisionBake"); owned.Add(root);
            root.transform.SetPositionAndRotation(new Vector3(3, 2, 1), Quaternion.Euler(12, 35, 6));
            root.transform.localScale = new Vector3(2, 3, 1);
            var node = new GameObject("Bench_COL_Hull0"); node.transform.SetParent(root.transform, false);
            var box = node.AddComponent<BoxCollider>(); box.center = new Vector3(1, 2, 3); box.size = new Vector3(4, 2, 1);
            if (mixed)
            {
                var profile = new GameObject("Bench_COL_Hull1"); profile.transform.SetParent(root.transform, false);
                var mesh = new Mesh { name = profile.name }; owned.Add(mesh);
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 }; mesh.RecalculateBounds();
                profile.AddComponent<MeshCollider>().sharedMesh = mesh;
                profile.AddComponent<MeshFilter>().sharedMesh = mesh;
            }
            var type = typeof(VertexColorBakingTool);
            var batchType = type.GetNestedType("LodBakeBatch", BindingFlags.NonPublic);
            var batch = System.Activator.CreateInstance(batchType, true);
            var tool = new VertexColorBakingTool();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            int count = (int)type.GetMethod("CollectLiveCollisionOccluders", flags).Invoke(tool, new object[] {
                batch, root, new Bounds(root.transform.position, Vector3.one * 100), new List<Bounds>(), new List<Vector3>(), 100f
            });
            Assert.AreEqual(mixed ? 2 : 1, count);
            var proxies = (List<Mesh>)batchType.GetField("temporaryMeshesToDestroy").GetValue(batch);
            Assert.AreEqual(1, proxies.Count, "only the box needs owned temporary geometry");
            var proxy = proxies[0];
            Assert.AreEqual(8, proxy.vertexCount); Assert.AreEqual(36, proxy.triangles.Length);
            Assert.AreEqual(box.center, proxy.bounds.center); Assert.AreEqual(box.size, proxy.bounds.size);
            var occluders = (List<(Mesh mesh, Matrix4x4 matrix)>)batchType.GetField("occluderMeshes").GetValue(batch);
            Assert.AreEqual(node.transform.localToWorldMatrix, occluders.Single(c => c.mesh == proxy).matrix);
            type.GetMethod("DisposeBatchTemporaryMeshes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { batch });
            Assert.IsTrue(proxy == null); Assert.IsEmpty(proxies);
        }

        void Own(CollisionMeshBuilder.ConvexDecompResult result) { if (result.hulls != null) owned.AddRange(result.hulls); }

        static List<Vector2> Rectangle(float x, float y) => new List<Vector2> {
            new Vector2(-x,-y), new Vector2(x,-y), new Vector2(x,y), new Vector2(-x,y) };

        [TestCase(.001f)] [TestCase(1f)] [TestCase(100f)]
        public void OrientedBoxIsAFinalEightVertexShape(float scale)
        {
            var source = Mesh(CollisionShapeFitter.Extrude(Rectangle(1,.3f), -.05f, .05f), Quaternion.Euler(23,37,11), scale);
            var result = CollisionMeshBuilder.BuildConvexDecomposition(source, CollisionMeshBuilder.ConvexDecompSettings.Structural);
            Own(result);
            Assert.IsTrue(result.ok, result.error);
            Assert.AreEqual(1, result.hulls.Count);
            Assert.AreEqual(CollisionMeshBuilder.ShapeKind.Box, result.shapeReports[0].kind);
            Assert.AreEqual(8, result.hulls[0].vertexCount);
            Assert.AreEqual(12, result.hulls[0].triangles.Length / 3);
            Assert.IsEmpty(result.elementReports, "a final box must not pass through remesh or V-HACD");
        }

        [TestCase(.001f)] [TestCase(1f)] [TestCase(100f)]
        public void ThinConvexProfileUsesOneExtrusionInItsOwnPlane(float scale)
        {
            var polygon = new List<Vector2> { new Vector2(-1,0), new Vector2(-.4f,-.6f), new Vector2(.7f,-.5f), new Vector2(1,.2f), new Vector2(.3f,.7f) };
            var mesh = Mesh(CollisionShapeFitter.Extrude(polygon, -.025f, .025f), Quaternion.Euler(17,51,33), scale);
            var settings = CollisionMeshBuilder.ConvexDecompSettings.Structural; settings.shapeFitError = .001f;
            Assert.IsTrue(CollisionShapeFitter.TryBuild(mesh, settings, 0, out var result));
            Own(result);
            Assert.AreEqual(1, result.hulls.Count);
            Assert.AreEqual(CollisionMeshBuilder.ShapeKind.Projected, result.shapeReports[0].kind);
            Assert.AreEqual(10, result.hulls[0].vertexCount);
            var topology = RemeshTopology.Inspect(result.hulls[0].vertices, result.hulls[0].triangles, default);
            Assert.IsTrue(topology.Valid, topology.Description);
            Assert.IsEmpty(topology.boundary);
            Assert.Less(result.shapeReports[0].sampledError, .0001f);
        }

        [Test]
        public void FlatSurfaceGetsPositiveThicknessWithinFitTolerance()
        {
            var mesh = Mesh(new RemeshNative.IndexedMesh { positions = new[] { Vector3.zero, Vector3.right, Vector3.up }, indices = new[] {0,1,2} }, Quaternion.identity, 1);
            Assert.IsTrue(CollisionShapeFitter.TryBuild(mesh, CollisionMeshBuilder.ConvexDecompSettings.Structural, 0, out var result));
            Own(result);
            Assert.AreEqual(6, result.hulls[0].vertexCount);
            Assert.Greater(result.hulls[0].bounds.size.z, 0);
        }

        static RemeshNative.IndexedMesh Frame()
        {
            var positions = new List<Vector3>(); var triangles = new List<int>();
            foreach (var rectangle in new[] { new Vector4(-1,-1,1,-.6f), new Vector4(-1,.6f,1,1), new Vector4(-1,-.6f,-.6f,.6f), new Vector4(.6f,-.6f,1,.6f) })
            {
                var profile = new List<Vector2> { new Vector2(rectangle.x,rectangle.y), new Vector2(rectangle.z,rectangle.y), new Vector2(rectangle.z,rectangle.w), new Vector2(rectangle.x,rectangle.w) };
                var geometry = CollisionShapeFitter.Extrude(profile, -.025f, .025f);
                int offset = positions.Count; positions.AddRange(geometry.positions); triangles.AddRange(geometry.indices.Select(i => i + offset));
            }
            return new RemeshNative.IndexedMesh { positions = positions.ToArray(), indices = triangles.ToArray() };
        }

        [Test]
        public void EmptyCenterPreventsASingleBoxOrProfile()
        {
            var mesh = Mesh(Frame(), Quaternion.identity, 1);
            var settings = CollisionMeshBuilder.ConvexDecompSettings.Structural; settings.maxHulls = 1;
            Assert.IsFalse(CollisionShapeFitter.TryBuild(mesh, settings, 0, out _));
            settings.maxHulls = 8;
            Assert.IsTrue(CollisionShapeFitter.TryBuild(mesh, settings, 0, out var result)); Own(result);
            Assert.Greater(result.hulls.Count, 1);
            foreach (var hull in result.hulls)
                Assert.Greater(new TriangleBvh(hull.vertices, hull.triangles).FindNearest(new Vector3(3,5,-2)).distSq, .1f);
        }

        [Test]
        public void TriangleClippingRetainsTheStraddlingSurfaceOnBothSides()
        {
            var source = new RemeshNative.IndexedMesh { positions = new[] { new Vector3(-1,0,0), new Vector3(1,0,0), new Vector3(0,2,0) }, indices = new[] {0,1,2} };
            var lower = CollisionShapeFitter.Clip(source, 0, .2f, true);
            var upper = CollisionShapeFitter.Clip(source, 0, .2f, false);
            float Area(RemeshNative.IndexedMesh mesh) {
                float area = 0;
                for (int i = 0; i < mesh.indices.Length; i += 3) area += Vector3.Cross(mesh.positions[mesh.indices[i+1]] - mesh.positions[mesh.indices[i]], mesh.positions[mesh.indices[i+2]] - mesh.positions[mesh.indices[i]]).magnitude * .5f;
                return area;
            }
            Assert.AreEqual(Area(source), Area(lower) + Area(upper), .00001f);
            Assert.IsTrue(lower.positions.All(p => p.x <= .2f)); Assert.IsTrue(upper.positions.All(p => p.x >= .2f));
        }

        [Test]
        public void HierarchyPlanRetainsSharedMeshInstancesAndExcludesOtherLods()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var left = new GameObject("Side"); left.transform.SetParent(root.transform);
            var right = new GameObject("Side"); right.transform.SetParent(root.transform);
            var a = new MeshEntry { include = true, lodIndex = 0, renderer = left.AddComponent<MeshRenderer>() };
            var b = new MeshEntry { include = true, lodIndex = 0, renderer = right.AddComponent<MeshRenderer>() };
            var plan = CollisionHierarchyAnalyzer.Analyze(root.transform, new List<MeshEntry> { b, new MeshEntry { include = true, lodIndex = 1 }, a, new MeshEntry { include = false } }, 0);
            Assert.AreEqual(2, plan.Count); Assert.AreSame(a, plan[0].entry); Assert.AreSame(b, plan[1].entry);
            Assert.AreNotEqual(plan[0].path, plan[1].path); Assert.AreEqual(1, plan[0].depth);
        }

        [TestCase(false)] [TestCase(true)]
        public void ActualBoxPlacementMatchesItsProxyAndIsUndoable(bool reflected)
        {
            var go = new GameObject("Box"); owned.Add(go);
            var shape = new CollisionMeshBuilder.ShapeReport { center = new Vector3(2,3,4), size = new Vector3(2,.5f,1), rotation = Quaternion.Euler(20,40,10) };
            var matrix = Matrix4x4.TRS(new Vector3(1,4,2), Quaternion.Euler(15,30,0), new Vector3(reflected ? -2 : 2, 2, 2));
            Assert.IsTrue(CollisionMeshTool.TryApplyBox(go, shape, matrix, Matrix4x4.identity));
            var box = go.GetComponent<BoxCollider>(); Assert.IsNotNull(box);
            var expected = matrix * Matrix4x4.TRS(shape.center, shape.rotation, shape.size);
            for (int i = 0; i < 8; i++) {
                var corner = new Vector3((i&1) == 0 ? -.5f : .5f, (i&2) == 0 ? -.5f : .5f, (i&4) == 0 ? -.5f : .5f);
                var world = expected.MultiplyPoint3x4(corner);
                var local = go.transform.InverseTransformPoint(world);
                Assert.Less((Vector3.Scale(new Vector3(Mathf.Abs(local.x),Mathf.Abs(local.y),Mathf.Abs(local.z)), new Vector3(1/box.size.x,1/box.size.y,1/box.size.z)) - Vector3.one * .5f).magnitude, .00001f);
            }
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); Assert.IsNull(go.GetComponent<BoxCollider>());
        }

        [Test]
        public void ShearedBoxFallsBackWithoutMutatingTheObject()
        {
            var go = new GameObject("Box"); owned.Add(go);
            var shape = new CollisionMeshBuilder.ShapeReport { size = Vector3.one, rotation = Quaternion.Euler(0,30,0) };
            Assert.IsFalse(CollisionMeshTool.TryApplyBox(go, shape, Matrix4x4.Scale(new Vector3(2,1,3)), Matrix4x4.identity));
            Assert.IsNull(go.GetComponent<Collider>()); Assert.AreEqual(Vector3.zero, go.transform.localPosition);
            Assert.IsFalse(CollisionMeshTool.TryApplyBox(go, shape, Matrix4x4.identity, Matrix4x4.Scale(new Vector3(2,1,3))));
        }

        [Test]
        public void GeneratedBoxAppliesAsAPrimitiveAndUndoRemovesItsHierarchy()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            var child = new GameObject("Seat_LOD0"); child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(1,2,3);
            var mesh = Mesh(CollisionShapeFitter.Extrude(Rectangle(1,.3f), -.05f, .05f), Quaternion.identity, 1);
            mesh.name = "Seat_LOD0";
            var renderer = child.AddComponent<MeshRenderer>();
            var context = new UvToolContext { LodGroup = root.AddComponent<LODGroup>() };
            context.MeshEntries.Add(new MeshEntry { renderer = renderer, originalMesh = mesh, include = true });
            var tool = new CollisionMeshTool(); tool.OnActivate(context, null);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var mode = typeof(CollisionMeshTool).GetField("mode", flags);
            mode.SetValue(tool, System.Enum.ToObject(mode.FieldType, 1));
            void Call(string method, params object[] args) => typeof(CollisionMeshTool).GetMethod(method, flags).Invoke(tool, args);
            try {
                Call("ApplyConvexPreset", CollisionMeshBuilder.ConvexDecompSettings.Structural);
                Call("ExecuteGenerate"); Call("ApplyToScene");
                var box = root.GetComponentInChildren<BoxCollider>(); Assert.IsNotNull(box);
                Assert.AreEqual("Seat_COL_Hull0", box.name);
                Assert.AreEqual(0, root.GetComponentsInChildren<MeshCollider>().Length);
                var expectedCenter = child.transform.TransformPoint(mesh.bounds.center);
                Assert.Less((box.transform.TransformPoint(box.center) - expectedCenter).magnitude, .00001f);
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                Assert.IsNull(root.transform.Find("Seat_COL"));
                Assert.IsNotNull(child);
            }
            finally { tool.OnDeactivate(); }
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(0f)]
        public void InvalidShapeFitCannotReachTheNativeFallback(float tolerance)
        {
            var settings = CollisionMeshBuilder.ConvexDecompSettings.Structural; settings.shapeFitError = tolerance;
            var mesh = Mesh(CollisionShapeFitter.Extrude(Rectangle(1,.3f), -.05f, .05f), Quaternion.identity, 1);
            var result = CollisionMeshBuilder.BuildConvexDecomposition(mesh, settings);
            Own(result); Assert.IsFalse(result.ok); StringAssert.Contains("Shape fit", result.error);
        }

        [Test]
        public void ProfileVertexBudgetIsACeilingRatherThanATarget()
        {
            var profile = new List<Vector2>();
            for (int i = 0; i < 64; i++) profile.Add(new Vector2(Mathf.Cos(i * Mathf.PI / 32), Mathf.Sin(i * Mathf.PI / 32)));
            var mesh = Mesh(CollisionShapeFitter.Extrude(profile, -.025f, .025f), Quaternion.identity, 1);
            var settings = CollisionMeshBuilder.ConvexDecompSettings.Structural; settings.shapeFitError = .075f; settings.maxVertsPerHull = 32;
            Assert.IsTrue(CollisionShapeFitter.TryBuild(mesh, settings, 0, out var result)); Own(result);
            Assert.AreEqual(CollisionMeshBuilder.ShapeKind.Projected, result.shapeReports[0].kind);
            Assert.Less(result.hulls[0].vertexCount, settings.maxVertsPerHull);
            Assert.LessOrEqual(result.shapeReports[0].sampledError, settings.shapeFitError);
            var points = result.hulls[0].vertices; var triangles = result.hulls[0].triangles;
            foreach (var sourcePoint in mesh.vertices)
                for (int i = 0; i < triangles.Length; i += 3) {
                    var a = points[triangles[i]];
                    var normal = Vector3.Cross(points[triangles[i+1]] - a, points[triangles[i+2]] - a).normalized;
                    Assert.LessOrEqual(Vector3.Dot(sourcePoint - a, normal), .00001f, "simplifying the profile must not cut away contact points");
                }
        }
    }
}
