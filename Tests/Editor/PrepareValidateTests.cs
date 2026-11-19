// PrepareValidateTests.cs — the Validate prepare step on scene objects: an Error blocks Apply
// and nothing changes, Warnings let the steps after it run, and a model that meets every
// requirement says so.
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareValidateTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        // A tetrahedron with normals and no UVs or tangents.
        Mesh Tetra(string name)
        {
            var mesh = new Mesh
            {
                name = name,
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 },
            };
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        // Bench: a render node Seat and a collision node Seat_COL.
        GameObject Bench()
        {
            var root = new GameObject("Bench");
            owned.Add(root);
            foreach (string name in new[] { "Seat", "Seat_COL" })
            {
                var node = new GameObject(name);
                node.transform.SetParent(root.transform, false);
                node.AddComponent<MeshFilter>().sharedMesh = Tetra(name);
                node.AddComponent<MeshRenderer>();
            }
            return root;
        }

        static ModelRequirements AllOff()
        {
            var requirements = new ModelRequirements();
            foreach (var field in typeof(ModelRequirements).GetFields().Where(f => f.FieldType == typeof(RequirementLevel)))
                field.SetValue(requirements, RequirementLevel.Off);
            return requirements;
        }

        [Test]
        public void Validate_AnErrorBlocksApplyAndNothingChanges()
        {
            var root = Bench();
            var target = PrepareTarget.ForSceneObject(root);
            var steps = new IPrepareStep[] { new ValidateStep(), new ColliderStep() };

            var plan = PrepareRunner.Preview(target, steps);
            var result = PrepareRunner.Apply(target, steps);

            Assert.IsTrue(plan.BlocksApply, "Seat has no UV0, lightmap UV or tangents: " + plan);
            Assert.IsFalse(result.Applied, result.ToString());
            StringAssert.Contains(ValidateStep.StepName, result.Refused);
            Assert.IsNull(root.transform.Find("Seat_COL").GetComponent<MeshCollider>(), "the Colliders step did not run");
        }

        [Test]
        public void Validate_WarningsLetTheStepsAfterRun()
        {
            var root = Bench();
            var requirements = new ModelRequirements { channels = RequirementLevel.Warning };
            var steps = new IPrepareStep[] { new ValidateStep(requirements), new ColliderStep() };

            var result = PrepareRunner.Apply(PrepareTarget.ForSceneObject(root), steps);

            Assert.IsTrue(result.Applied, result.ToString());
            Assert.AreEqual(PrepareLevel.Warning, result.Steps[0].Report.Level, result.ToString());
            Assert.IsNotNull(root.GetComponent<MeshCollider>(), "the collider is on the root");
        }

        [Test]
        public void Validate_SaysWhenTheModelMeetsEveryRequirement()
        {
            var root = Bench();

            var result = PrepareRunner.Preview(PrepareTarget.ForSceneObject(root), new IPrepareStep[] { new ValidateStep(AllOff()) });

            var line = result.Steps[0].Report.Lines.Single();
            Assert.AreEqual(PrepareLevel.Unchanged, line.level);
            StringAssert.Contains("meets every requirement", line.message);
            CollectionAssert.IsEmpty(line.details, "every check is off: there is nothing it was checked for");
        }

        [Test]
        public void Requirements_ListTheChecksThatAreOnByGroupAndLevel()
        {
            var requirements = new ModelRequirements { requireTangents = false, smoothingGroups = RequirementLevel.Off };

            var lines = requirements.Checked();

            Assert.AreEqual(4, lines.Count, string.Join("\n", lines));
            StringAssert.StartsWith("Geometry: finite values, degenerate faces (error); unused vertices", lines[0]);
            Assert.AreEqual("Channels: has UV0, UV1 (lightmap), normals, lightmap UV inside [0,1] (error)", lines[1]);
            StringAssert.DoesNotContain("smoothing groups", lines[2]);
            StringAssert.Contains("units", lines[2]);
        }
    }
}
