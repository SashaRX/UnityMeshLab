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
            internal Check(Model model) { this.model = model; }
            public string Name => ValidateStep.StepName;
            public string Purpose => "";
            public string SkipReason(PrepareContext context) => null;
            public void Plan(PrepareContext context, PrepareReport report) => Apply(context, report);
            public void Apply(PrepareContext context, PrepareReport report)
            {
                if (model.broken) report.Add(PrepareLevel.Error, "'Seat'", "Lightmap UV", "outside");
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
