// PrepareMaterialsTests.cs — the importer prepare steps. Materials: the project's material of a
// slot's name, the nearest of several; on an exported FBX, a slot remapped on the importer
// before the variant is made, the whole chain (Materials, Colliders, Prefab) applied once and
// changing nothing the second time. Preset: the settings that differ applied, the model's
// remaps never, a model without the class prefix skipped.
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
            var remap = importer.GetExternalObjectMap().Single(kv => kv.Key.type == typeof(Material) && kv.Key.name == "M_Wood_Oak");
            Assert.AreSame(project, remap.Value);
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Bench.prefab");
            Assert.IsNotNull(variant, "the variant next to the model");
            Assert.AreSame(project, variant.transform.Find("Seat_LOD0").GetComponent<MeshRenderer>().sharedMaterial, "the variant shows the remapped material");
            Assert.IsNotNull(variant.transform.Find("Seat_COL").GetComponent<MeshCollider>());

            var second = PrepareRunner.Apply(target, steps);

            Assert.IsFalse(second.Changes, second.ToString());
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
            Assert.AreSame(mine, importer.GetExternalObjectMap()[slot], "the model keeps its own remap");
            Assert.IsFalse(PrepareRunner.Preview(target, new IPrepareStep[] { step }).Changes, "a second run changes nothing");
        }

        [Test]
        public void Preset_SkipsAModelWithoutTheClassPrefix()
        {
            string fbxPath = ExportBench();
            var preset = new UnityEditor.Presets.Preset(AssetImporter.GetAtPath(fbxPath));
            owned.Add(preset);
            var context = new PrepareContext(PrepareTarget.ForAsset(fbxPath), null, false);
            Assert.IsNull(new PresetStep(preset).SkipReason(context), "SM_Bench has the prefix");
            StringAssert.Contains("SK_", new PresetStep(preset, "SK").SkipReason(context));
            StringAssert.Contains("no importer preset", new PresetStep(null).SkipReason(context));
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
