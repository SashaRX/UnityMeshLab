// PreparePrefabTests.cs — the Prefab prepare step: the variant's name next to the model, a
// first run that saves the steps' changes as a new variant of the model (the model itself
// untouched), a second run that changes nothing, and another prefab in the variant's place.
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PreparePrefabTests
    {
        string folder;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/__MeshLabPrepareTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(folder);
        }

        [TestCase("Assets/Props/SM_Old Chair.fbx", "Assets/Props/Old_Chair.prefab")]
        [TestCase("Assets/Bench_Straight _A.fbx", "Assets/Bench_Straight__A.prefab")]
        [TestCase("Assets/sk_Door.fbx", "Assets/Door.prefab")]
        [TestCase("Assets/Props/Crate.FBX", "Assets/Props/Crate.prefab")]
        public void VariantPath_DropsTheClassPrefixAndSpacesNextToTheModel(string model, string variant)
            => Assert.AreEqual(variant, PrefabStep.VariantPath(model));

        // A stand-in for an imported model: a prefab with a render node and a collision node.
        string Model(string name)
        {
            var mesh = new Mesh
            {
                name = "Seat_Wood_COL",
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 },
            };
            AssetDatabase.CreateAsset(mesh, $"{folder}/{name}_mesh.asset");
            var root = new GameObject(name);
            try
            {
                foreach (string node in new[] { "Seat_Wood_LOD0", "Seat_Wood_COL" })
                {
                    var child = new GameObject(node);
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = new Vector3(0, 1, 0);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    child.AddComponent<MeshRenderer>();
                }
                string path = $"{folder}/{name}.prefab";
                PrefabUtility.SaveAsPrefabAsset(root, path);
                return path;
            }
            finally { Object.DestroyImmediate(root); }
        }

        static PrepareReport Run(IPrepareStep step, PrefabOutput output, string model, bool apply)
        {
            var context = new PrepareContext(PrepareTarget.ForAsset(model), output.Root, false, output.Refusal, output);
            var report = new PrepareReport(model);
            if (apply) step.Apply(context, report);
            else step.Plan(context, report);
            return report;
        }

        [Test]
        public void Output_SavesTheChangesAsAVariantOfTheModelAndAReopenedOneNeedsNothing()
        {
            string model = Model("SM_Bench");
            string variant = $"{folder}/Bench.prefab";

            using (var output = PrefabOutput.Open(model, variant))
            {
                Assert.IsTrue(output.Creates);
                Assert.AreEqual(PrepareLevel.Changed, Run(new PrefabStep(), output, model, false).Level);
                Run(new ColliderStep(), output, model, true);
                Assert.IsTrue(output.Save(out string error), error);
            }

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(variant);
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(saved));
            Assert.AreEqual(model, AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(saved)));
            var col = saved.transform.Find("Seat_Wood_COL");
            Assert.IsNotNull(col.GetComponent<MeshCollider>(), "the collider is in the variant");
            Assert.IsNull(col.GetComponent<MeshRenderer>());
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(model).transform.Find("Seat_Wood_COL").GetComponent<MeshRenderer>(),
                "the model keeps its renderer");

            using (var output = PrefabOutput.Open(model, variant))
            {
                Assert.IsFalse(output.Creates);
                Assert.AreEqual(PrepareLevel.Unchanged, Run(new PrefabStep(), output, model, false).Level);
                Assert.AreEqual(0, Run(new ColliderStep(), output, model, false).Count(PrepareLevel.Changed), "a second run changes nothing");
            }
        }

        [Test]
        public void Output_RefusesWhenAnotherPrefabIsInTheVariantsPlace()
        {
            string model = Model("SM_Bench");
            string other = Model("Bench");   // Bench.prefab: a prefab of its own, not a variant of SM_Bench

            using (var output = PrefabOutput.Open(model, other))
            {
                Assert.IsNull(output.Root);
                StringAssert.Contains("not a prefab variant of this model", output.Refusal);
                var report = Run(new PrefabStep(), output, model, false);
                Assert.IsTrue(report.BlocksApply, "an Error: Apply is refused");
                Assert.AreEqual(output.Refusal, new ColliderStep().SkipReason(
                    new PrepareContext(PrepareTarget.ForAsset(model), null, false, output.Refusal, output)));
            }
        }

        [Test]
        public void SkipReason_SaysAPrefabAndASceneObjectAreChangedInPlace()
        {
            string model = Model("SM_Bench");
            var step = new PrefabStep();
            StringAssert.Contains("in place", step.SkipReason(new PrepareContext(PrepareTarget.ForAsset(model), null, false)));
            var go = new GameObject("Bench");
            try { StringAssert.Contains("with Undo", step.SkipReason(new PrepareContext(PrepareTarget.ForSceneObject(go), go, true))); }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
