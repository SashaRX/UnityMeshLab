// PrepareMaterialsTests.cs — the importer prepare steps. Materials: the project's material of a
// slot's name, the nearest of several; on an exported FBX, a slot remapped on the importer
// before the variant is made, the whole chain (Materials, Colliders, Prefab) applied once and
// changing nothing the second time, and the variant matching its model's recipe. Preset: the
// settings that differ applied, the model's remaps never, the reason a model is skipped.
// Validate: an error the preset fixes waits for the reimport; one it leaves stops the rest.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareMaterialsTests
    {
        [Test]
        public void Nearest_OrdersByTheFoldersSharedWithTheModel()
        {
            var paths = new[] { "Assets/Common/M_Wood.mat", "Assets/Props/Benches/M_Wood.mat", "Assets/Props/M_Wood.mat" };
            CollectionAssert.AreEqual(new[] { "Assets/Props/Benches/M_Wood.mat", "Assets/Props/M_Wood.mat", "Assets/Common/M_Wood.mat" },
                MaterialStep.Nearest(paths, "Assets/Props/Benches/Bench.fbx"));
            CollectionAssert.AreEqual(new[] { "Assets/Common/M_Wood.mat", "Assets/Props/Benches/M_Wood.mat", "Assets/Props/M_Wood.mat" },
                MaterialStep.Nearest(paths, null), "no model path: by path");
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        readonly List<Object> owned = new List<Object>();
        string folder;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/__MeshLabPrepareMaterials_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            AssetDatabase.DeleteAsset(folder);
        }

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

        // SM_Bench.fbx: a seat rendered with 'M_Wood_Oak' and its collision node off the root's placement.
        string ExportBench()
        {
            var root = new GameObject("SM_Bench"); owned.Add(root);
            var oak = new Material(Shader.Find("Standard")) { name = "M_Wood_Oak" }; owned.Add(oak);
            var seat = new GameObject("Seat_LOD0"); seat.transform.SetParent(root.transform, false);
            seat.AddComponent<MeshFilter>().sharedMesh = Tetra("Seat_LOD0");
            seat.AddComponent<MeshRenderer>().sharedMaterial = oak;
            var col = new GameObject("Seat_COL"); col.transform.SetParent(root.transform, false);
            col.transform.localPosition = new Vector3(0, .5f, 0);
            col.AddComponent<MeshFilter>().sharedMesh = Tetra("Seat_COL");
            col.AddComponent<MeshRenderer>().sharedMaterial = oak;
            string fbxPath = $"{folder}/SM_Bench.fbx";
            FbxExport.Write(fbxPath, root);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);
            return fbxPath;
        }

        [Test]
        public void Apply_RemapsTheSlotAndMakesTheVariantOnceThenChangesNothing()
        {
            string fbxPath = ExportBench();
            var project = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(project, $"{folder}/M_Wood_Oak.mat");
            var steps = new IPrepareStep[] { new MaterialStep(), new ColliderStep(), new PrefabStep() };
            var target = PrepareTarget.ForAsset(fbxPath);

            var first = PrepareRunner.Apply(target, steps);

            Assert.IsTrue(first.Applied && first.Changes, first.ToString());
            var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            // By the asset: the reimport may give the material a new instance.
            string projectPath = $"{folder}/M_Wood_Oak.mat";
            var remap = importer.GetExternalObjectMap().Single(kv => kv.Key.type == typeof(Material) && kv.Key.name == "M_Wood_Oak");
            Assert.AreEqual(projectPath, AssetDatabase.GetAssetPath(remap.Value));
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Bench.prefab");
            Assert.IsNotNull(variant, "the variant next to the model");
            Assert.AreEqual(projectPath, AssetDatabase.GetAssetPath(variant.transform.Find("Seat_LOD0").GetComponent<MeshRenderer>().sharedMaterial),
                "the variant shows the remapped material");
            Assert.IsNotNull(variant.transform.Find("Seat_COL").GetComponent<MeshCollider>());

            var second = PrepareRunner.Apply(target, steps);

            Assert.IsFalse(second.Changes, second.ToString());

            var variantTarget = PrepareTarget.ForAsset($"{folder}/Bench.prefab");
            Assert.AreEqual(fbxPath, variantTarget.ModelPath, "the variant is made from the model");
            var chain = PrepareRecipes.Chain(variantTarget, new[] { new PrepareRecipe { colliderSurfaces = false } });
            Assert.IsNotNull(chain.Recipe, "the variant takes its model's recipe: " + chain.Match);
            StringAssert.Contains("its model 'SM_Bench'", chain.Match);
        }

        [Test]
        public void Preset_AppliesTheDifferingSettingsButNotTheModelsRemaps()
        {
            string fbxPath = ExportBench();
            var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            var mine = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mine, $"{folder}/Mine.mat");
            var other = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(other, $"{folder}/Other.mat");
            var slot = new AssetImporter.SourceAssetIdentifier(typeof(Material), "M_Wood_Oak");
            importer.globalScale = 2;
            importer.AddRemap(slot, other);
            var preset = new UnityEditor.Presets.Preset(importer);
            AssetDatabase.CreateAsset(preset, $"{folder}/Importer.preset");
            importer.globalScale = 1;
            importer.AddRemap(slot, mine);
            importer.SaveAndReimport();
            var target = PrepareTarget.ForAsset(fbxPath);
            var step = new PresetStep(preset);

            var plan = PrepareRunner.Preview(target, new IPrepareStep[] { step });
            var line = plan.Steps.Single().Report.Lines.Single();
            Assert.AreEqual(PrepareLevel.Changed, line.level, plan.ToString());
            Assert.IsTrue(line.details.Any(d => d.StartsWith("m_GlobalScale: 1 → 2")), string.Join("\n", line.details));
            Assert.IsFalse(line.details.Any(d => d.StartsWith("m_ExternalObjects")), "the model's remaps are never applied");

            PrepareRunner.Apply(target, new IPrepareStep[] { step });

            importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            Assert.AreEqual(2f, importer.globalScale);
            Assert.AreEqual($"{folder}/Mine.mat", AssetDatabase.GetAssetPath(importer.GetExternalObjectMap()[slot]), "the model keeps its own remap");
            Assert.IsFalse(PrepareRunner.Preview(target, new IPrepareStep[] { step }).Changes, "a second run changes nothing");
        }

        [Test]
        public void Preset_LeavesOutWhatThePresetItselfExcludes()
        {
            string fbxPath = ExportBench();
            var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            importer.globalScale = 2;
            var preset = new UnityEditor.Presets.Preset(importer) { excludedProperties = new[] { "m_GlobalScale" } };
            owned.Add(preset);
            importer.globalScale = 1;
            importer.SaveAndReimport();

            var plan = PrepareRunner.Preview(PrepareTarget.ForAsset(fbxPath), new IPrepareStep[] { new PresetStep(preset) });

            Assert.IsFalse(plan.Changes, "the preset's own exclusion is not applied, so it is no difference: " + plan);
        }

        [Test]
        public void Preset_SaysWhyItIsSkipped()
        {
            string fbxPath = ExportBench();
            var preset = new UnityEditor.Presets.Preset(AssetImporter.GetAtPath(fbxPath));
            owned.Add(preset);
            var target = PrepareTarget.ForAsset(fbxPath);
            var context = new PrepareContext(target, null, false);
            Assert.IsNull(new PresetStep(preset).SkipReason(context));
            StringAssert.Contains("sets no importer preset", new PresetStep(null).SkipReason(context));

            var other = PrepareRecipes.Chain(target, new[] { new PrepareRecipe { namePattern = "SK_*", importerPreset = preset } });
            Assert.IsNull(other.Recipe, "a model the recipe's name does not match");
            StringAssert.Contains("no recipe matches 'SM_Bench'", PrepareRunner.Preview(target, other).Steps[0].Skipped);
        }

        static ModelRequirements OnlyImportScale()
        {
            var requirements = new ModelRequirements();
            foreach (var field in typeof(ModelRequirements).GetFields().Where(f => f.FieldType == typeof(RequirementLevel)))
                field.SetValue(requirements, RequirementLevel.Off);
            requirements.importScale = RequirementLevel.Error;
            return requirements;
        }

        [Test]
        public void Validate_ChecksAgainOnceThePresetHasReimportedTheModel()
        {
            string fbxPath = ExportBench();
            var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            var preset = new UnityEditor.Presets.Preset(importer);   // Scale Factor 1
            owned.Add(preset);
            importer.globalScale = 2;
            importer.SaveAndReimport();
            var target = PrepareTarget.ForAsset(fbxPath);
            var validate = new ValidateStep(OnlyImportScale());

            var alone = PrepareRunner.Apply(target, new IPrepareStep[] { validate });
            Assert.IsFalse(alone.Applied, "Scale Factor 2 is an error: " + alone);

            var steps = new IPrepareStep[] { validate, new PresetStep(preset) };
            var plan = PrepareRunner.Preview(target, steps);
            Assert.AreEqual(PrepareLevel.Error, plan.Steps[0].Report.Level, plan.ToString());
            StringAssert.Contains("checked again once Preset has reimported", plan.Steps[0].Deferred);
            Assert.IsFalse(plan.BlocksApply, "the preset sets Scale Factor 1: " + plan);

            var applied = PrepareRunner.Apply(target, steps);
            Assert.IsTrue(applied.Applied, applied.ToString());
            Assert.AreEqual(1f, ((ModelImporter)AssetImporter.GetAtPath(fbxPath)).globalScale);
            Assert.AreEqual(PrepareLevel.Unchanged, applied.Steps[0].Report.Level, "checked again on the reimported model: " + applied);
        }

        [Test]
        public void Validate_StopsTheStepsAfterWhenTheReimportedModelStillFails()
        {
            string fbxPath = ExportBench();
            var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            importer.globalScale = 3;
            var preset = new UnityEditor.Presets.Preset(importer);   // Scale Factor 3
            owned.Add(preset);
            importer.globalScale = 2;
            importer.SaveAndReimport();
            var steps = new IPrepareStep[] { new ValidateStep(OnlyImportScale()), new PresetStep(preset), new ColliderStep(), new PrefabStep() };

            var result = PrepareRunner.Apply(PrepareTarget.ForAsset(fbxPath), steps);

            Assert.IsFalse(result.Applied, result.ToString());
            StringAssert.Contains("importer was changed", result.Refused);
            Assert.AreEqual(3f, ((ModelImporter)AssetImporter.GetAtPath(fbxPath)).globalScale, "the preset's change stays");
            StringAssert.StartsWith("not applied", result.Steps[2].Skipped, "the Colliders step was stopped");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Bench.prefab"), "no variant was saved");
        }

        // A recipe for SM_ models without Validate (the test meshes have no UVs) or surfaces.
        static PrepareRecipe BenchRecipe(bool runOnImport = false)
            => new PrepareRecipe { validate = false, colliderSurfaces = false, runOnImport = runOnImport };

        [Test]
        public void OnImport_ANewModelWaitsAndOnlyARecipeThatRunsOnImportPreparesIt()
        {
            string fbxPath = ExportBench();
            Assert.IsTrue(PrepareOnImport.IsPending(fbxPath), "a model imported for the first time waits for the import to end");

            Assert.IsNull(PrepareOnImport.Run(fbxPath, new[] { BenchRecipe() }), "its recipe does not run on import");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Bench.prefab"));

            var result = PrepareOnImport.Run(fbxPath, new[] { BenchRecipe(runOnImport: true) });
            Assert.IsTrue(result.Applied, result.ToString());
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Bench.prefab"), "prepared as Apply would");
        }

        [Test]
        public void Batch_PreparesTheModelsARecipeMatchesUnderTheFolders()
        {
            string fbxPath = ExportBench();
            var models = PrepareBatch.Models(new[] { folder });
            CollectionAssert.AreEqual(new[] { fbxPath }, models);

            var preview = PrepareBatch.Prepare(models, new[] { BenchRecipe() }, apply: false);
            Assert.IsTrue(preview.results.Single().Changes, PrepareBatch.Report(preview));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Bench.prefab"), "a preview changes nothing");

            var other = PrepareBatch.Prepare(models, new[] { new PrepareRecipe { namePattern = "SK_*" } }, apply: true);
            Assert.AreEqual(0, other.results.Count);
            Assert.AreEqual(1, other.unmatched);
            Assert.IsFalse(other.Failed);

            var refused = PrepareBatch.Prepare(models, new[] { new PrepareRecipe { colliderSurfaces = false } }, apply: true);
            Assert.IsTrue(refused.Failed, "Validate's errors (no UVs) refuse it: " + PrepareBatch.Report(refused));

            var applied = PrepareBatch.Prepare(models, new[] { BenchRecipe() }, apply: true);
            Assert.IsFalse(applied.Failed, PrepareBatch.Report(applied));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Bench.prefab"));
            StringAssert.Contains("1 model prepared, 1 with changes, 0 not applied, 0 matched no recipe", PrepareBatch.Report(applied));
        }

        [Test]
        public void Plan_WarnsOfASlotWithNoProjectMaterial()
        {
            string fbxPath = ExportBench();
            var result = PrepareRunner.Preview(PrepareTarget.ForAsset(fbxPath), new IPrepareStep[] { new MaterialStep() });
            var line = result.Steps.Single().Report.Lines.Single();
            Assert.AreEqual(PrepareLevel.Warning, line.level, result.ToString());
            Assert.AreEqual("'M_Wood_Oak'", line.subject);
        }
#endif
    }
}
