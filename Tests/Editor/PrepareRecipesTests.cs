// PrepareRecipesTests.cs — prepare recipes: the name and folder patterns, the first recipe a
// target's name or its model's matches, and the chain a recipe gives (Validate first): a step
// it turns off, or every step when no recipe matches, skipped with the reason.
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareRecipesTests
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

        [TestCase("SM_*", "SM_Bench", true)]
        [TestCase("SM_*", "sm_bench", true)]
        [TestCase("SM_*", "SK_Bench", false)]
        [TestCase("SM_*", "XSM_Bench", false)]
        [TestCase("SM_*; SK_*", "SK_Door", true)]
        [TestCase("SM_?ench", "SM_Bench", true)]
        [TestCase("SM_University_*", "SM_University_Bench_Straight _A", true)]
        [TestCase("", "Rock", true)]
        public void NameMatches_StarAnyRunQuestionOneCharacterCaseIgnored(string pattern, string name, bool expected)
            => Assert.AreEqual(expected, PrepareRecipes.NameMatches(pattern, name));

        [TestCase("", "Assets/Art", true)]
        [TestCase("Assets/Art", "Assets/Art", true)]
        [TestCase("Assets/Art", "Assets/Art/Props/Benches", true)]
        [TestCase("Assets/Art/", "Assets/Art/Props", true)]
        [TestCase("assets/art", "Assets/Art", true)]
        [TestCase("Assets/Art", "Assets/Artwork", false)]
        [TestCase("Assets/Art", "", false)]
        [TestCase("Assets/*/Props", "Assets/City/Props/Benches", true)]
        [TestCase("Assets/*/Props", "Assets/City/Old/Props", false)]
        [TestCase("Assets/**/Props", "Assets/City/Old/Props", true)]
        [TestCase("Assets/**/Props", "Assets/Props", true)]
        [TestCase("Assets/Env; Assets/Props", "Assets/Props/Crates", true)]
        [TestCase("Assets/Art/**", "Assets/Art", true)]
        [TestCase("Assets/Art/**", "Assets/Art/Props", true)]
        [TestCase("**", "Assets/Art", true)]
        public void FolderMatches_TheFolderOrOneUnderIt(string pattern, string folder, bool expected)
            => Assert.AreEqual(expected, PrepareRecipes.FolderMatches(pattern, folder));

        [Test]
        public void Match_TakesTheFirstRecipeTheNameOrItsModelMatches()
        {
            var walls = new PrepareRecipe { name = "Walls", namePattern = "SM_Wall*" };
            var props = new PrepareRecipe { name = "Props", namePattern = "SM_*", folderPattern = "Assets/Props" };
            var recipes = new[] { walls, props };
            var bench = new[]
            {
                new PrepareRecipes.Subject("the name", "Bench", "Assets/Prefabs"),
                new PrepareRecipes.Subject("its model", "SM_Bench", "Assets/Props/City"),
            };

            var (recipe, match) = PrepareRecipes.Match(bench, recipes);

            Assert.AreSame(props, recipe);
            Assert.AreEqual("Recipe 'Props': its model 'SM_Bench' in 'Assets/Props/City' matches 'SM_*' in 'Assets/Props'", match);
            Assert.AreSame(walls, PrepareRecipes.Match(new[] { new PrepareRecipes.Subject("the name", "SM_Wall_A", "Assets/Props") }, recipes).recipe,
                "of two that match, the first");
        }

        [Test]
        public void Match_SaysWhatMatchesNoRecipe()
        {
            var (recipe, match) = PrepareRecipes.Match(new[] { new PrepareRecipes.Subject("the name", "Rock", "Assets/Env") },
                new[] { PrepareRecipe.StaticMeshes() });

            Assert.IsNull(recipe);
            StringAssert.StartsWith("No recipe: the name 'Rock' in 'Assets/Env' matches no recipe", match);
        }

        [Test]
        public void Chain_SkipsTheStepsTheRecipeTurnsOffAndTheReportNamesTheRecipe()
        {
            var root = Root("SM_Bench");
            var target = PrepareTarget.ForSceneObject(root);
            var recipe = new PrepareRecipe { name = "Bench only", materials = false, colliderSurfaces = false };

            var chain = PrepareRecipes.Chain(target, new[] { recipe });
            var result = PrepareRunner.Preview(target, chain);

            Assert.AreSame(recipe, chain.Recipe);
            CollectionAssert.AreEqual(new[] { ValidateStep.StepName, FixStep.StepName, PresetStep.StepName, MaterialStep.StepName, ColliderStep.StepName, PrefabStep.StepName },
                result.Steps.Select(s => s.Step.Name));
            Assert.IsNull(result.Steps[0].Skipped, "Validate checks a scene object");
            StringAssert.Contains("not made from a model", result.Steps[1].Skipped, "there is no file to fix");
            StringAssert.Contains("prepare the FBX", result.Steps[2].Skipped, "the preset is the model's, not a scene object's");
            Assert.AreEqual("off in the recipe 'Bench only'", result.Steps[3].Skipped, result.ToString());
            Assert.IsNull(result.Steps[4].Skipped, "Colliders acts on a scene object");
            StringAssert.Contains("Recipe 'Bench only': the name 'SM_Bench'", result.ToString());
            StringAssert.Contains("matches 'SM_*'", result.ToString());
        }

        [Test]
        public void Chain_WithNoRecipeSkipsEveryStepAndChangesNothing()
        {
            var root = Root("Rock");
            var target = PrepareTarget.ForSceneObject(root);

            var chain = PrepareRecipes.Chain(target, new[] { PrepareRecipe.StaticMeshes() });
            var result = PrepareRunner.Apply(target, chain);

            Assert.IsNull(chain.Recipe);
            Assert.AreEqual(6, result.Steps.Count);
            Assert.IsTrue(result.Steps.All(s => s.Skipped == "no recipe matches 'Rock'"), result.ToString());
            Assert.IsFalse(result.Changes, result.ToString());
            StringAssert.Contains("No recipe: the name 'Rock'", result.ToString());
            StringAssert.Contains("matches no recipe", result.ToString());
        }

        [Test]
        public void Without_SkipsTheStepsLeftOutOfThisRunAndKeepsTheRecipe()
        {
            var recipe = new PrepareRecipe { name = "Bench only", materials = false, colliderSurfaces = false };
            var chain = PrepareRecipes.Chain(PrepareTarget.ForSceneObject(Root("SM_Bench")), new[] { recipe });

            var run = chain.Without(new[] { ColliderStep.StepName, MaterialStep.StepName }, "off for this run");

            Assert.AreSame(recipe, run.Recipe);
            Assert.AreEqual(chain.Match, run.Match);
            Assert.IsNotInstanceOf<SkippedStep>(run.Steps[0], "Validate runs");
            Assert.AreEqual("off in the recipe 'Bench only'", ((SkippedStep)run.Steps[3]).Reason, "a step the recipe turns off keeps its reason");
            Assert.AreEqual("off for this run", ((SkippedStep)run.Steps[4]).Reason);
            Assert.IsInstanceOf<ColliderStep>(((SkippedStep)run.Steps[4]).Step);
            Assert.AreSame(chain, chain.Without(new string[0], "off for this run"), "nothing left out: the same chain");
        }

        [Test]
        public void Settings_MoveTheImporterPresetSetBeforeRecipesIntoTheRecipe()
        {
            var host = Root("Host");
            var preset = new UnityEditor.Presets.Preset(host.transform);
            owned.Add(preset);
            var settings = ScriptableObject.CreateInstance<MeshLabProjectSettings>();
            owned.Add(settings);
            settings.importerPreset = preset;
            settings.presetLodScreenPercentages = true;

            Assert.IsTrue(settings.MoveLegacyPreset());

            Assert.AreSame(preset, settings.recipes[0].importerPreset, "the default recipe takes it");
            Assert.IsTrue(settings.recipes[0].presetLodScreenPercentages);
            Assert.IsNull(settings.importerPreset, "the old setting is cleared");
            Assert.IsFalse(settings.MoveLegacyPreset(), "nothing left to move");
        }

        [Test]
        public void Settings_GiveARecipeFromBeforeValidateTheStepOnce()
        {
            var settings = ScriptableObject.CreateInstance<MeshLabProjectSettings>();
            owned.Add(settings);
            var recipe = settings.recipes[0];
            recipe.validate = false;                         // as a recipe saved before the field loads
            recipe.requirements = new ModelRequirements { channels = RequirementLevel.Off };

            Assert.IsTrue(settings.MigrateRecipes());

            Assert.IsTrue(recipe.validate, "Validate on, as in a new recipe");
            Assert.AreEqual(RequirementLevel.Error, recipe.requirements.channels, "the default requirements");
            recipe.validate = false;
            Assert.IsFalse(settings.MigrateRecipes(), "once only");
            Assert.IsFalse(recipe.validate, "a later choice is kept");
        }

        [Test]
        public void Prefab_GivesASceneObjectThePrefabsNameAndMarksItStatic()
        {
            var root = Root("SM_Old Bench");
            var child = new GameObject("Seat_LOD0");
            child.transform.SetParent(root.transform, false);
            var target = PrepareTarget.ForSceneObject(root);

            var plan = PrepareRunner.Preview(target, new IPrepareStep[] { new PrefabStep(true) });
            Assert.AreEqual("SM_Old Bench", root.name, "a preview changes nothing");
            Assert.AreEqual(2, plan.Steps.Single().Report.Count(PrepareLevel.Changed), plan.ToString());

            var result = PrepareRunner.Apply(target, new IPrepareStep[] { new PrefabStep(true) });

            Assert.IsTrue(result.Applied, result.ToString());
            Assert.AreEqual("Old_Bench", root.name, "no class prefix, spaces as underscores");
            Assert.IsTrue(root.isStatic && child.isStatic, "the whole hierarchy");
            Assert.IsFalse(PrepareRunner.Apply(target, new IPrepareStep[] { new PrefabStep(true) }).Changes, "a second run changes nothing");
        }

        [Test]
        public void Prefab_GivesEachLodGroupTheListedComponentsOnce()
        {
            var root = Root("Bench");
            var plain = Root("Crate");
            root.AddComponent<LODGroup>();
            var names = new[] { typeof(PrepareTestSurface).FullName, "No.Such.Type" };

            var result = PrepareRunner.Apply(PrepareTarget.ForSceneObject(root), new IPrepareStep[] { new PrefabStep(false, names) });

            Assert.IsNotNull(root.GetComponent<PrepareTestSurface>(), result.ToString());
            var missing = result.Steps.Single().Report.Lines.Single(l => l.level == PrepareLevel.Warning);
            StringAssert.Contains("no component type 'No.Such.Type'", missing.message);
            Assert.IsFalse(PrepareRunner.Apply(PrepareTarget.ForSceneObject(root), new IPrepareStep[] { new PrefabStep(false, names) }).Changes, "a second run adds nothing");
            Assert.AreEqual(1, root.GetComponents<PrepareTestSurface>().Length);

            Assert.IsFalse(PrepareRunner.Apply(PrepareTarget.ForSceneObject(plain), new IPrepareStep[] { new PrefabStep(false, names) }).Changes, "no LODs, no component");
            Assert.IsNull(plain.GetComponent<PrepareTestSurface>());
        }

        [Test]
        public void Prefab_LeavesTheStaticFlagsWhenTheRecipeDoesNotAsk()
        {
            var root = Root("Bench");

            var result = PrepareRunner.Apply(PrepareTarget.ForSceneObject(root), new IPrepareStep[] { new PrefabStep() });

            Assert.IsFalse(result.Changes, result.ToString());
            Assert.IsFalse(root.isStatic);
        }

        [Test]
        public void Settings_GiveARecipeFromBeforeFixTheStepOnce()
        {
            var settings = ScriptableObject.CreateInstance<MeshLabProjectSettings>();
            owned.Add(settings);
            var recipe = settings.recipes[0];
            recipe.fix = false;                              // as a recipe saved before the field loads
            recipe.fixLightmapClamp = 0f;
            recipe.prefabStatic = false;
            recipe.prefabLodComponents = false;

            Assert.IsTrue(settings.MigrateRecipes());

            Assert.IsTrue(recipe.fix, "Fix on, as in a new recipe");
            Assert.AreEqual(FixStep.DefaultMaxClamp, recipe.fixLightmapClamp);
            Assert.IsTrue(recipe.prefabStatic, "the Prefab step's Static on, as in a new recipe");
            Assert.IsTrue(recipe.prefabLodComponents, "and its LOD components");
            recipe.fix = false;
            Assert.IsFalse(settings.MigrateRecipes(), "once only");
            Assert.IsFalse(recipe.fix, "a later choice is kept");
        }

        [Test]
        public void Batch_ReadsItsArguments()
        {
            var options = PrepareBatch.Parse(new[] { "Unity", "-batchmode", "-PREPAREFOLDERS", "Assets/Art; Assets/Props/", "-preparePreview", "-prepareReport", "out.txt" });

            CollectionAssert.AreEqual(new[] { "Assets/Art", "Assets/Props" }, options.folders);
            Assert.IsTrue(options.preview);
            Assert.AreEqual("out.txt", options.report);

            var none = PrepareBatch.Parse(new[] { "Unity", "-prepareFolders" });
            CollectionAssert.AreEqual(new[] { "Assets" }, none.folders, "no folders given: the whole project");
            Assert.IsFalse(none.preview);
            Assert.IsNull(none.report);
            Assert.IsFalse(new PrepareRecipe().runOnImport, "a recipe never runs on import unless asked");
        }

        [Test]
        public void Steps_CarryTheRecipesSettings()
        {
            var recipe = new PrepareRecipe { colliderSurfaces = false, colliderFallbackLod = 1 };

            var steps = recipe.Steps();

            Assert.IsInstanceOf<ValidateStep>(steps[0]);
            Assert.IsInstanceOf<FixStep>(steps[1]);
            Assert.IsInstanceOf<PresetStep>(steps[2]);
            Assert.IsInstanceOf<MaterialStep>(steps[3]);
            Assert.AreEqual(1, ((ColliderStep)steps[4]).FallbackLod);
            Assert.IsInstanceOf<PrefabStep>(steps[5]);
            Assert.IsInstanceOf<SkippedStep>(new PrepareRecipe { validate = false }.Steps()[0], "Validate can be turned off");
        }
    }
}
