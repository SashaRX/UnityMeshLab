// PrepareFixTests.cs — the Fix prepare step: which lightmap UVs count as outside [0,1], why a
// target is skipped, and the runner's order on a scene object: a step that changes the model's
// file applies first, the check that waited checks again, and an error then stops the rest.
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareFixTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        GameObject Root(string name)
        {
            var root = new GameObject(name);
            owned.Add(root);
            return root;
        }

        // Stands for a model with an error a fix cures (or does not).
        sealed class Model { internal bool broken = true; internal bool curable = true; internal readonly List<string> applied = new List<string>(); }

        sealed class FileFix : IImporterStep
        {
            readonly Model model;
            internal FileFix(Model model) { this.model = model; }
            public string Name => FixStep.StepName;
            public string Purpose => "";
            public string SkipReason(PrepareContext context) => null;
            public void Plan(PrepareContext context, PrepareReport report)
            {
                if (model.broken) report.Add(PrepareLevel.Changed, "'Seat'", "Lightmap UV", "clamped");
            }
            public void Apply(PrepareContext context, PrepareReport report)
            {
                if (!model.broken) return;
                model.applied.Add(Name);
                if (model.curable) model.broken = false;
                report.Add(PrepareLevel.Changed, "'Seat'", "Lightmap UV", "clamped");
            }
        }

        sealed class Check : ICheckStep
        {
            readonly Model model;
            readonly string check, subject;
            internal Check(Model model, string check = "Lightmap UV", string subject = "'Bench/Seat'")
            {
                this.model = model; this.check = check; this.subject = subject;
            }
            public string Name => ValidateStep.StepName;
            public string Purpose => "";
            public string SkipReason(PrepareContext context) => null;
            public void Plan(PrepareContext context, PrepareReport report) => Apply(context, report);
            public void Apply(PrepareContext context, PrepareReport report)
            {
                if (model.broken) report.Add(PrepareLevel.Error, subject, check, "outside");
            }
        }

        sealed class Change : IPrepareStep
        {
            readonly Model model;
            internal Change(Model model) { this.model = model; }
            public string Name => ColliderStep.StepName;
            public string Purpose => "";
            public string SkipReason(PrepareContext context) => null;
            public void Plan(PrepareContext context, PrepareReport report) => report.Add(PrepareLevel.Changed, "'Seat'", "Collider", "added");
            public void Apply(PrepareContext context, PrepareReport report)
            {
                model.applied.Add(Name);
                report.Add(PrepareLevel.Changed, "'Seat'", "Collider", "added");
            }
        }

        [Test]
        public void Outside_CountsTheLightmapUvsPastRoundingAndTheFarthest()
        {
            var (outside, overshoot) = FixStep.Outside(new[]
            {
                new Vector2(-.008f, .5f), new Vector2(.5f, 1.00005f), new Vector2(1f, 0f), new Vector2(.2f, 1.003f),
            });
            Assert.AreEqual(2, outside, "rounding past the edge is not outside");
            Assert.AreEqual(.008f, overshoot, 1e-6f);
            Assert.AreEqual((0, 0f), FixStep.Outside(new[] { Vector2.zero, Vector2.one }));
        }

        [Test]
        public void Help_ExplainsEveryMeshFindingAndItsGeometryIsDrawn()
        {
            foreach (string check in new[] { "Lightmap UV", "Degenerate faces", "Tiny faces", "Short edges", "Flipped normals", "Non-manifold edges" })
            {
                Assert.IsTrue(PrepareHelp.TryGet(check, out var hint), check);
                Assert.IsNotEmpty(hint.Why, check);
                Assert.IsTrue(FindingGeometry.Draws(check), check);
            }
            Assert.IsTrue(PrepareHelp.TryGet("Units", out _));
            Assert.IsFalse(FindingGeometry.Draws("Units"), "nothing to look at: the file's units");
            Assert.IsFalse(PrepareHelp.TryGet("Collider", out _), "a step's own lines need no help");
            Assert.AreEqual("Seat_LOD0", PrepareHelp.SubjectPath("'Seat_LOD0' (mesh 'Seat')"));
            Assert.IsNull(PrepareHelp.SubjectPath("Assets/Bench.fbx"));
        }

        [Test]
        public void FindingGeometry_GivesTheFacesAndVerticesAFindingIsAbout()
        {
            // A quad towards +z: its second face's normals flipped, its first vertex's lightmap UV outside.
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1), Vector3.up, new Vector3(5, 5) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward },
                uv2 = new[] { new Vector2(-.05f, 0), Vector2.right, Vector2.one, Vector2.up, Vector2.zero },
            };
            owned.Add(mesh);

            Assert.IsTrue(FindingGeometry.Build("Flipped normals", mesh, null).IsEmpty, "no face is flipped");
            CollectionAssert.AreEqual(new[] { 4 }, FindingGeometry.Build("Unused vertices", mesh, null).points);
            var outside = FindingGeometry.Build("Lightmap UV", mesh, null);
            CollectionAssert.AreEqual(new[] { 0 }, outside.points, "the vertex outside [0,1]");
            Assert.AreEqual(6, outside.triangles.Count, "both faces are on it");

            mesh.normals = new[] { Vector3.back, Vector3.forward, Vector3.back, Vector3.back, Vector3.forward };
            CollectionAssert.AreEqual(new[] { 0, 2, 3 }, FindingGeometry.Build("Flipped normals", mesh, null).triangles, "the face all of whose normals point back");
            Assert.IsNull(FindingGeometry.Build("Units", mesh, null));
        }

        [Test]
        public void Preview_WhoseErrorsAFixCuresIsNotLoggedAsAnError()
        {
            var model = new Model();
            var target = PrepareTarget.ForSceneObject(Root("Bench"));

            var cured = PrepareRunner.Preview(target, new IPrepareStep[] { new Check(model), new FileFix(model) });
            var left = PrepareRunner.Preview(target, new IPrepareStep[] { new Check(model) });

            Assert.AreEqual(PrepareLevel.Error, cured.Level, "the finding is still an error");
            Assert.AreEqual(PrepareLevel.Warning, cured.LogLevel, "but it waits for the fix");
            Assert.AreEqual(PrepareLevel.Error, left.LogLevel, "no fix: an error");
            var other = PrepareRunner.Preview(target, new IPrepareStep[] { new Check(model, "Degenerate faces"), new FileFix(model) });
            Assert.AreEqual(PrepareLevel.Error, other.LogLevel, "the fix is for another check: Apply would stop on this error");
            var named = PrepareRunner.Preview(target, new IPrepareStep[] { new Check(model, subject: "'Bench/Other' (mesh 'Seat')"), new FileFix(model) });
            Assert.AreEqual(PrepareLevel.Warning, named.LogLevel, "the mesh the fix names, on a node of another name");
            var back = PrepareRunner.Preview(target, new IPrepareStep[] { new Check(model, subject: "'Other/Seat' (mesh 'Back')"), new FileFix(model) });
            Assert.AreEqual(PrepareLevel.Error, back.LogLevel, "another mesh on a node named as the fixed one");
        }

        [Test]
        public void Fix_IsSkippedOnWhatIsNotMadeFromAModel()
        {
            var result = PrepareRunner.Preview(PrepareTarget.ForSceneObject(Root("Bench")), new IPrepareStep[] { new FixStep() });
            StringAssert.Contains("not made from a model", result.Steps.Single().Skipped);
        }

        [Test]
        public void SceneObject_AFixAppliesFirstAndTheCheckThatWaitedLetsTheRestRun()
        {
            var model = new Model();
            var target = PrepareTarget.ForSceneObject(Root("Bench"));
            var steps = new IPrepareStep[] { new Check(model), new FileFix(model), new Change(model) };

            var plan = PrepareRunner.Preview(target, steps);
            Assert.IsFalse(plan.BlocksApply, "the error waits for the fix: " + plan);
            StringAssert.Contains("checked again once Fix has reimported the model", plan.Steps[0].Deferred);
            CollectionAssert.IsEmpty(model.applied, "a preview changes nothing");

            var result = PrepareRunner.Apply(target, steps);
            Assert.IsTrue(result.Applied, result.ToString());
            CollectionAssert.AreEqual(new[] { FixStep.StepName, ColliderStep.StepName }, model.applied);
            Assert.AreEqual(PrepareLevel.Unchanged, result.Steps[0].Report.Level, "checked again on the fixed model: " + result);
            CollectionAssert.AreEqual(steps.Select(s => s.Name), result.Steps.Select(s => s.Step.Name), "in the chain's order");
        }

        [Test]
        public void SceneObject_AnErrorTheFixDoesNotCureStopsTheRest()
        {
            var model = new Model { curable = false };
            var target = PrepareTarget.ForSceneObject(Root("Bench"));
            var steps = new IPrepareStep[] { new Check(model), new FileFix(model), new Change(model) };

            var result = PrepareRunner.Apply(target, steps);

            Assert.IsFalse(result.Applied, result.ToString());
            StringAssert.Contains("file was changed", result.Refused);
            CollectionAssert.AreEqual(new[] { FixStep.StepName }, model.applied, "the Colliders step was stopped");
            StringAssert.StartsWith("not applied", result.Steps[2].Skipped);
        }

        [Test]
        public void SceneObject_AnErrorNoFixCuresStillBlocksApply()
        {
            var model = new Model();
            var target = PrepareTarget.ForSceneObject(Root("Bench"));

            var result = PrepareRunner.Apply(target, new IPrepareStep[] { new Check(model), new Change(model) });

            Assert.IsFalse(result.Applied, result.ToString());
            StringAssert.Contains(ValidateStep.StepName, result.Refused);
            CollectionAssert.IsEmpty(model.applied);
        }
    }
}
