// PrepareCollidersTests.cs — the Colliders prepare step on scene objects: a MeshCollider on
// every collision node, the largest on the root when it sits at the root's placement, the
// nodes' renderers removed, the LOD fallback, a root with a MeshCollider left alone, and the
// surface each collider gets from its part's name, its materials or the model's name.
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareCollidersTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        // Records the surface each collider's object would get.
        sealed class RecordingBinding : ISurfaceBinding
        {
            internal readonly Dictionary<string, string> bound = new Dictionary<string, string>();
            public string Unavailable => null;

            public void Bind(PrepareContext context, GameObject host, string surface, string origin, string subject, PrepareReport report, bool apply)
            {
                if (apply) bound[host.name] = surface;
            }
        }

        Mesh Box(string name, float size)
        {
            float h = size / 2;
            var mesh = new Mesh
            {
                name = name,
                vertices = new[]
                {
                    new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h),
                    new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h),
                },
                triangles = new[]
                {
                    0, 2, 1, 0, 3, 2,  4, 5, 6, 4, 6, 7,  0, 1, 5, 0, 5, 4,
                    2, 3, 7, 2, 7, 6,  1, 2, 6, 1, 6, 5,  0, 4, 7, 0, 7, 3,
                },
            };
            owned.Add(mesh);
            return mesh;
        }

        GameObject Root(string name)
        {
            var root = new GameObject(name);
            owned.Add(root);
            return root;
        }

        static GameObject Node(GameObject root, string name, Mesh mesh, Vector3 position = default, Material material = null)
        {
            var node = new GameObject(name);
            node.transform.SetParent(root.transform, false);
            node.transform.localPosition = position;
            node.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = node.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;
            return node;
        }

        static PrepareResult Apply(GameObject root, IPrepareStep step)
            => PrepareRunner.Apply(PrepareTarget.ForSceneObject(root), new[] { step });

        [Test]
        public void Apply_GivesEveryCollisionNodeAColliderAndTheLargestGoesOnTheRoot()
        {
            var root = Root("Bench");
            Node(root, "Base_Concrete_LOD0", Box("Base", 2));
            var baseCol = Node(root, "Base_Concrete_COL", Box("Base_Concrete_COL", 2));
            var seatCol = Node(root, "Seat_Wood_COL", Box("Seat_Wood_COL", 1), new Vector3(0, 1.5f, 0));
            var binding = new RecordingBinding();

            var result = Apply(root, new ColliderStep(binding));

            Assert.IsTrue(result.Applied && result.Changes, result.ToString());
            var main = root.GetComponent<MeshCollider>();
            Assert.IsNotNull(main, "the largest collider, its node at the root's placement, goes on the root");
            Assert.AreEqual("Base_Concrete_COL", main.sharedMesh.name);
            Assert.IsFalse(main.convex);
            Assert.AreEqual(ColliderStep.DefaultCooking, main.cookingOptions);
            Assert.IsNull(baseCol.GetComponent<MeshCollider>());
            Assert.AreEqual("Seat_Wood_COL", seatCol.GetComponent<MeshCollider>().sharedMesh.name, "the others stay on their nodes");
            foreach (var col in new[] { baseCol, seatCol })
                Assert.IsTrue(col.GetComponent<MeshRenderer>() == null && col.GetComponent<MeshFilter>() == null, $"{col.name} keeps a renderer");
            Assert.IsNotNull(root.transform.Find("Base_Concrete_LOD0").GetComponent<MeshRenderer>(), "render nodes keep theirs");
            Assert.AreEqual("Concrete", binding.bound["Bench"]);
            Assert.AreEqual("Wood", binding.bound["Seat_Wood_COL"]);
        }

        [Test]
        public void Apply_LeavesTheMainColliderOnItsNodeWhenItSitsOffTheRootsPlacement()
        {
            var root = Root("Bench");
            var baseCol = Node(root, "Base_Concrete_COL", Box("Base_Concrete_COL", 2), new Vector3(0.392f, 0, 0.002f));
            Node(root, "Seat_Wood_COL", Box("Seat_Wood_COL", 1));

            Apply(root, new ColliderStep());

            Assert.IsNull(root.GetComponent<MeshCollider>(), "on the root its mesh would land elsewhere");
            Assert.AreEqual("Base_Concrete_COL", baseCol.GetComponent<MeshCollider>().sharedMesh.name);
        }

        [Test]
        public void Apply_RemovesTheMainNodesOwnColliderWhenTheRootTakesItsPlace()
        {
            var root = Root("Bench");
            var baseCol = Node(root, "Base_Concrete_COL", Box("Base_Concrete_COL", 2));
            baseCol.AddComponent<MeshCollider>().sharedMesh = baseCol.GetComponent<MeshFilter>().sharedMesh;

            Apply(root, new ColliderStep());

            Assert.AreEqual("Base_Concrete_COL", root.GetComponent<MeshCollider>().sharedMesh.name);
            Assert.IsNull(baseCol.GetComponent<MeshCollider>(), "one collider for the mesh, not two");
        }

        [Test]
        public void Apply_SwitchesOnAnInactiveCollisionNodeThatAlreadyHasItsCollider()
        {
            var root = Root("Bench");
            var seat = Node(root, "Seat_Wood_COL", Box("Seat_Wood_COL", 1), new Vector3(0, 1, 0));
            seat.AddComponent<MeshCollider>().sharedMesh = seat.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(seat.GetComponent<MeshRenderer>());
            Object.DestroyImmediate(seat.GetComponent<MeshFilter>());
            seat.SetActive(false);

            var result = Apply(root, new ColliderStep());

            Assert.IsTrue(seat.activeSelf, result.ToString());
        }

        [Test]
        public void Apply_ASecondTimeChangesNothing()
        {
            foreach (var offset in new[] { Vector3.zero, new Vector3(0.392f, 0, 0.002f) })
            {
                var root = Root("Bench");
                Node(root, "Base_Concrete_COL", Box("Base_Concrete_COL", 2), offset);
                Node(root, "Seat_Wood_COL", Box("Seat_Wood_COL", 1));
                Assert.IsTrue(Apply(root, new ColliderStep()).Changes);

                var again = Apply(root, new ColliderStep());

                Assert.IsFalse(again.Changes, again.ToString());
                Assert.AreEqual(1, root.GetComponentsInChildren<MeshCollider>().Count(c => c.sharedMesh.name == "Base_Concrete_COL"));
            }
        }

        [Test]
        public void Apply_LeavesAModelWhoseRootHasAMeshColliderAsItIs()
        {
            var root = Root("TrashCan");
            root.AddComponent<MeshCollider>().sharedMesh = Box("Own", 1);
            var col = Node(root, "Can_Metal_COL", Box("Can_Metal_COL", 1));

            var result = Apply(root, new ColliderStep(new RecordingBinding()));

            Assert.IsFalse(result.Changes);
            Assert.IsNotNull(col.GetComponent<MeshRenderer>());
            Assert.IsNull(col.GetComponent<MeshCollider>());
            StringAssert.Contains("left as it is", result.Steps.Single().Report.Lines.Single().message);
        }

        [Test]
        public void Apply_WithoutCollisionNodesCollidesWithTheCoarsestLodAndKeepsItsRenderers()
        {
            var root = Root("Crate");
            var lod0 = Node(root, "Crate_Wood_LOD0", Box("Crate_LOD0", 1));
            var lod1 = Node(root, "Crate_Wood_LOD1", Box("Crate_LOD1", 1));
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[]
            {
                new LOD(0.5f, new Renderer[] { lod0.GetComponent<MeshRenderer>() }),
                new LOD(0.1f, new Renderer[] { lod1.GetComponent<MeshRenderer>() }),
            });

            Apply(root, new ColliderStep());

            Assert.AreEqual("Crate_LOD1", root.GetComponent<MeshCollider>().sharedMesh.name);
            Assert.IsNotNull(lod1.GetComponent<MeshRenderer>());
            Assert.IsNull(lod0.GetComponent<MeshCollider>());
        }

        [Test]
        public void Preview_ChangesNothing()
        {
            var root = Root("Bench");
            var col = Node(root, "Seat_Wood_COL", Box("Seat_Wood_COL", 1));

            var result = PrepareRunner.Preview(PrepareTarget.ForSceneObject(root), new[] { new ColliderStep() });

            Assert.IsTrue(result.Changes && !result.Applied);
            Assert.IsNull(root.GetComponent<MeshCollider>());
            Assert.IsNotNull(col.GetComponent<MeshRenderer>());
        }

        [Test]
        public void Resolve_ReadsThePartThenItsMaterialsThenTheModel()
        {
            var words = MeshNaming.Vocabulary.Default;
            Assert.AreEqual("Wood", SurfaceBinding.Resolve("University_Bench_Straight _Seat_Wood_COL", new[] { "M_Metal" }, "SM_Glass", words).surface);
            Assert.AreEqual("Wood", SurfaceBinding.Resolve("Seat_COL", new[] { "M_Building_Atlas_01", "M_Wood_Oak" }, "SM_Glass", words).surface);
            Assert.AreEqual("Glass", SurfaceBinding.Resolve("Seat_COL", new[] { "M_Atlas" }, "SM_Glass_Door", words).surface);
            Assert.IsNull(SurfaceBinding.Resolve("Seat_COL", null, "Bench", words).surface);
        }

        [Test]
        public void Volume_IsAClosedMeshsOwnElseItsBounds()
        {
            var box = Box("Box", 2);
            Assert.IsTrue(MeshGeometry.IsClosed(box.vertices, box.triangles));
            Assert.AreEqual(8, ColliderStep.Volume(box.vertices, box.triangles, box.bounds), 1e-4);
            var open = box.triangles.Take(30).ToArray();
            Assert.IsFalse(MeshGeometry.IsClosed(box.vertices, open), "a box without its last side is open");
            Assert.AreEqual(8, ColliderStep.Volume(box.vertices, open, box.bounds), 1e-4, "its bounds' volume");
        }

        [Test]
        public void ComponentBinding_PutsTheSurfaceAssetOnEachColliderAndKeepsOneAlreadySet()
        {
            var wood = ScriptableObject.CreateInstance<PrepareTestSurfaceData>();
            wood.name = "Wood";
            owned.Add(wood);
            var settings = new SurfaceBindingSettings
            {
                componentType = typeof(PrepareTestSurface).FullName, field = "_material", dataType = nameof(PrepareTestSurfaceData),
            };
            var binding = new ComponentSurfaceBinding(settings, (type, name, near) => name == "Wood" ? wood : null);
            Assert.IsNull(binding.Unavailable);
            var root = Root("Bench");
            var seat = Node(root, "Seat_Wood_COL", Box("Seat_Wood_COL", 1), new Vector3(0, 1, 0));
            Node(root, "Base_Concrete_COL", Box("Base_Concrete_COL", 2), new Vector3(0, 0, 1));

            var result = Apply(root, new ColliderStep(binding));

            Assert.AreSame(wood, seat.GetComponent<PrepareTestSurface>().Material);
            var concrete = result.Steps.Single().Report.Lines.Single(l => l.check == ComponentSurfaceBinding.Check && l.subject.Contains("Base"));
            Assert.AreEqual(PrepareLevel.Warning, concrete.level, "no Concrete asset");
            var again = Apply(root, new ColliderStep(binding));
            Assert.IsFalse(again.Changes, again.ToString());
        }

        [Test]
        public void ComponentBinding_SaysWhyItCannotBind()
        {
            StringAssert.Contains("no surface component", new ComponentSurfaceBinding(new SurfaceBindingSettings()).Unavailable);
            StringAssert.Contains("no component type", new ComponentSurfaceBinding(new SurfaceBindingSettings { componentType = "No.Such.Type" }).Unavailable);
            var wrongField = new SurfaceBindingSettings
            {
                componentType = nameof(PrepareTestSurface), field = "_missing", dataType = nameof(PrepareTestSurfaceData),
            };
            StringAssert.Contains("no serialized field", new ComponentSurfaceBinding(wrongField).Unavailable);
        }
    }
}
