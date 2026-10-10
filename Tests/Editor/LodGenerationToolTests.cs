using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class LodGenerationToolTests
    {
        GameObject root;
        readonly List<Mesh> meshes = new List<Mesh>();

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Root");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            foreach (var mesh in meshes)
                if (mesh != null) Object.DestroyImmediate(mesh);
            meshes.Clear();
        }

        [TestCase("Asset_LOD999999999999999999999")]
        [TestCase("Asset_LOD8")]
        [TestCase("Asset_LOD100000000")]
        public void FindLodSiblings_RejectsUnsupportedIndices(string name)
        {
            var selected = CreateChild(name);

            Assert.That(FindLodSiblings(selected), Is.Null);
        }

        [Test]
        public void GenerationOptions_DefaultFarProfileAndToggleReachThePipeline()
        {
            var tool = new LodGenerationTool();
            var build = typeof(LodGenerationTool).GetMethod("BuildGenerationOptions",BindingFlags.NonPublic|BindingFlags.Instance);
            var options = (LodPipelineOps.Options)build.Invoke(tool,new object[] {1});
            var near = LodPipelineOps.ForLevel(options,0); var far = LodPipelineOps.ForLevel(options,1);
            Assert.That(near.maxColorError,Is.EqualTo(.02f)); Assert.That(near.targetError,Is.EqualTo(.2f));
            Assert.That(far.maxColorError,Is.EqualTo(.1f)); Assert.That(far.targetError,Is.EqualTo(.3f));
            Assert.That(far.colorWeight,Is.EqualTo(.25f));
            typeof(LodGenerationTool).GetField("generateRelaxFarLods",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(tool,false);
            options = (LodPipelineOps.Options)build.Invoke(tool,new object[] {1});
            Assert.That(options.levelQuality,Is.Null);
            Assert.That(LodPipelineOps.ForLevel(options,1).maxColorError,Is.EqualTo(.02f));
        }

        [Test]
        public void GenerationOptions_DefaultBudgetAndThreefoldRatiosReachPipeline()
        {
            var tool = new LodGenerationTool();
            var build = typeof(LodGenerationTool).GetMethod("BuildGenerationOptions",BindingFlags.NonPublic|BindingFlags.Instance);
            var options = (LodPipelineOps.Options)build.Invoke(tool,new object[] {1});
            Assert.That(options.prioritizeTriangleBudget,Is.True);
            Assert.That(options.preserveHardEdges,Is.True);
            SetField(tool,"generatePreserveHardEdges",false);
            Assert.That(((LodPipelineOps.Options)build.Invoke(tool,new object[] {1})).preserveHardEdges,Is.False);
            Assert.That(options.candidateCount,Is.EqualTo(3),"Budget mode defaults to Balanced quality selection.");
            Assert.That(options.count,Is.EqualTo(2),"Default generation adds LOD1 and LOD2 to the source LOD0.");
            CollectionAssert.AreEqual(new[] {1f/3,1f/9,1f/27,1f/81},options.ratios);
            Assert.That(LodGenerationTool.SteppedRatios(.3f,3,2),Is.EqualTo(new[] {.1f,.03333333f}).Within(1e-7f));
            SetField(tool,"generateBudgetPriority",false);
            options = (LodPipelineOps.Options)build.Invoke(tool,new object[] {1});
            Assert.That(options.prioritizeTriangleBudget,Is.False);
        }

        [Test]
        public void FindLodSiblings_IgnoresUnsupportedSiblingIndices()
        {
            var selected = CreateChild("Asset_LOD0");
            CreateChild("Asset_LOD1");
            CreateChild("Asset_LOD999999999999999999999");
            CreateChild("Asset_LOD100000000");

            var siblings = FindLodSiblings(selected);

            Assert.That(siblings, Has.Count.EqualTo(2));
            Assert.That(siblings[0].lodIndex, Is.EqualTo(0));
            Assert.That(siblings[1].lodIndex, Is.EqualTo(1));
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_AutoCreatedGroup_RestoresLod0Transition()
        {
            var lods = new List<LOD> { new LOD(0.01f, new Renderer[0]) };

            NormalizeSingleLodTransitionForGeneration(lods, 1);

            Assert.AreEqual(0.5f, lods[0].screenRelativeTransitionHeight);
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_ExistingTransition_PreservesValue()
        {
            var lods = new List<LOD> { new LOD(0.25f, new Renderer[0]) };

            NormalizeSingleLodTransitionForGeneration(lods, 1);

            Assert.AreEqual(0.25f, lods[0].screenRelativeTransitionHeight);
        }

        [Test]
        public void NormalizeSingleLodTransitionForGeneration_NotAppendingAfterLod0_PreservesValue()
        {
            var lods = new List<LOD> { new LOD(0.01f, new Renderer[0]) };

            NormalizeSingleLodTransitionForGeneration(lods, 2);

            Assert.AreEqual(0.01f, lods[0].screenRelativeTransitionHeight);
        }

        [Test]
        public void Generate_UsesEachLodRatioAndRegeneratesTheSameLevels()
        {
            var (tool, context) = CreateToolWithSource();
            SetField(tool, "generateLodCount", 3);
            SetField(tool, "generateLodRatios", new[] { 0.5f, 0.25f, 0.125f, 0.0625f });

            Generate(tool, 1);
            var first = GeneratedTriangleCounts(context);
            Assert.That(first, Has.Length.EqualTo(4));
            Assert.That(first[1], Is.InRange(390, 400));
            Assert.That(first[2], Is.InRange(190, 200));
            Assert.That(first[3], Is.InRange(90, 100));
            var baseline = GetGenerationBaseline(tool);
            Assert.That(baseline.sourceTris, Is.EqualTo(800));
            Assert.That(baseline.startLod, Is.EqualTo(1), "generated levels are replaced by the next Generate");
            Assert.That(baseline.lastRatio, Is.EqualTo(1f), "generated counts must not clamp the sliders");

            SetField(tool, "generateLodCount", 2);
            SetField(tool, "generateLodRatios", new[] { 0.4f, 0.15f, 0.125f, 0.0625f });
            // The old UI counted the generated levels before clearing them.
            Generate(tool, 4);
            var second = GeneratedTriangleCounts(context);
            Assert.That(second, Has.Length.EqualTo(3));
            Assert.That(context.LodGroup.GetLODs()[1].renderers[0].name, Is.EqualTo("Grid_LOD1"));
            Assert.That(context.LodGroup.GetLODs()[2].renderers[0].name, Is.EqualTo("Grid_LOD2"));
            Assert.That(second[1], Is.InRange(310, 320));
            Assert.That(second[2], Is.InRange(110, 120));
        }

        [Test]
        public void RegenerateFullLoops_KeepsEditedWorkingSourceAndPreflightBindings()
        {
            var (tool, context) = CreateToolWithSource();
            SetField(tool, "generateLodCount", 1);
            Generate(tool, 1);
            var previousMesh = context.GeneratedLodObjects[0].GetComponent<MeshFilter>().sharedMesh;
            var entry = context.MeshEntries[0];
            var working = Object.Instantiate(entry.originalMesh);
            meshes.Add(working);
            const int size = 20;
            var quads = new int[size*size*4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int vertex = y*(size+1)+x, offset = (y*size+x)*4;
                    quads[offset] = vertex; quads[offset+1] = vertex+size+1;
                    quads[offset+2] = vertex+size+2; quads[offset+3] = vertex+1;
                }
            working.SetIndices(quads, MeshTopology.Quads, 0);
            var colors = new Color[working.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.red;
            working.colors = colors;
            entry.repackedMesh = working;
            context.HasRepack = true;
            SetField(tool, "generateReductionMode", LodReductionMode.FullLoops);
            SetField(tool, "generatePruneParts", true);

            Generate(tool, 2);

            Assert.That(context.MeshEntries[0], Is.SameAs(entry));
            Assert.That(context.MeshEntries[0].repackedMesh, Is.SameAs(working));
            Assert.That(context.HasRepack, Is.True);
            Assert.That(previousMesh == null, Is.True, "Old generated mesh is released");
            Assert.That(context.GeneratedLodObjects, Has.Count.EqualTo(1));
            var generated = context.GeneratedLodObjects[0].GetComponent<MeshFilter>().sharedMesh;
            meshes.Add(generated);
            Assert.That(generated.colors, Is.Not.Empty);
            foreach (var color in generated.colors) Assert.That(color, Is.EqualTo(Color.red));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancelGeneration_RestoresAuthoredLevelsAndReleasesPendingMeshes(bool afterCompleteLevel)
        {
            var (_, context) = CreateToolWithSource();
            var secondSource = CreateChild("Second_LOD0");
            secondSource.AddComponent<MeshFilter>().sharedMesh = CreateGrid();
            var secondRenderer = secondSource.AddComponent<MeshRenderer>();
            var oldChild = CreateChild("Authored_LOD1");
            oldChild.AddComponent<MeshFilter>().sharedMesh = CreateGrid();
            var oldRenderer = oldChild.AddComponent<MeshRenderer>();
            var originalLods = new[] {
                new LOD(.5f, new[] { context.MeshEntries[0].renderer, secondRenderer }),
                new LOD(.1f, new[] { oldRenderer })
            };
            context.LodGroup.SetLODs(originalLods);
            context.Refresh(context.LodGroup);
            var options = new LodPipelineOps.Options { count = 2, ratios = new[] { .5f, .25f }, targetError = 1 };
            var pending = new List<Mesh>();
            bool CancelAfterCreatedMeshes()
            {
                foreach (var mesh in context.GeneratedLodMeshes.Values)
                    if (!pending.Contains(mesh)) pending.Add(mesh);
                return afterCompleteLevel ? pending.Count >= 2 && oldRenderer == null : pending.Count >= 1;
            }

            var result = LodPipelineOps.Generate(context, 1, options, cancelled: CancelAfterCreatedMeshes);

            Assert.That(result.ok, Is.False);
            Assert.That(result.error, Does.Contain("cancelled"));
            Assert.That(result.generatedObjects, Is.Empty);
            Assert.That(result.perLod, Is.Empty);
            Assert.That(context.GeneratedLodMeshes, Is.Empty);
            Assert.That(context.GeneratedLodObjects, Is.Empty);
            Assert.That(root.transform.childCount, Is.EqualTo(3));
            Assert.That(oldRenderer != null, Is.True, "Existing authored renderer survives cancellation");
            var restored = context.LodGroup.GetLODs();
            Assert.That(restored, Has.Length.EqualTo(2));
            Assert.That(restored[0].renderers, Is.EqualTo(originalLods[0].renderers));
            Assert.That(restored[1].renderers, Is.EqualTo(originalLods[1].renderers));
            Assert.That(pending, Has.Count.EqualTo(afterCompleteLevel ? 2 : 1));
            foreach (var mesh in pending) Assert.That(mesh == null, Is.True, "Cancelled geometry is released");
            Assert.That(UvProgress.Last.status, Is.EqualTo(Progress.Status.Canceled));
        }

        [TestCase(false,false)]
        [TestCase(true,false)]
        [TestCase(false,true)]
        public void AbortRegenerationAfterClearing_RestoresGeneratedObjectsMeshesAndBindings(bool fail,bool reducerFailure)
        {
            var (tool, context) = CreateToolWithSource();
            SetField(tool,"generateLodCount",1);
            Generate(tool,1);
            var previous = context.GeneratedLodObjects[0];
            var previousMesh = context.GeneratedLodMeshes[previous];
            meshes.Add(previousMesh);
            var previousEntries = context.MeshEntries.ToArray();
            if (reducerFailure)
            {
                var invalid = new Mesh { name = "InvalidWorkingSource" };
                meshes.Add(invalid);
                context.MeshEntries[0].repackedMesh = invalid;
                context.HasRepack = true;
            }
            var options = new LodPipelineOps.Options { count = 1,ratios = new[] {.25f},targetError = 1 };
            if (fail) options.reductionMode = LodReductionMode.FullLoops;

            var result = LodPipelineOps.Generate(context,1,options,
                prepared: fail ? new Dictionary<Mesh,LodSourceTopology>() : null,
                cancelled: () => !fail && !reducerFailure && context.GeneratedLodObjects.Count == 0 && context.GeneratedLodMeshes.Count > 0,
                replaceGenerated: true);

            Assert.That(result.cancelled,Is.EqualTo(!fail && !reducerFailure));
            Assert.That(result.ok,Is.False);
            Assert.That(result.error,Is.Not.Empty);
            if (reducerFailure) Assert.That(result.error,Does.Contain("no vertices"));
            Assert.That(previous != null && previousMesh != null,Is.True);
            Assert.That(context.GeneratedLodObjects,Is.EqualTo(new[] {previous}));
            Assert.That(context.GeneratedLodMeshes,Has.Count.EqualTo(1));
            Assert.That(context.GeneratedLodMeshes[previous],Is.SameAs(previousMesh));
            Assert.That(context.MeshEntries,Is.EqualTo(previousEntries));
            Assert.That(context.LodGroup.GetLODs()[1].renderers[0].gameObject == previous,Is.True);
            Assert.That(GetGenerationBaseline(tool).startLod,Is.EqualTo(1));
            Assert.That(root.transform.childCount,Is.EqualTo(2));
        }

        [Test]
        public void GenerationBaseline_PreservesAuthoredLodsWhenGeneratedLevelsExist()
        {
            var source = CreateGrid();
            var authored = Simplify(source, new MeshSimplifier.SimplifySettings
            {
                targetRatio = 0.5f, targetError = 0.2f, uvChannel = 1
            }).simplifiedMesh;
            var generated = Simplify(source, new MeshSimplifier.SimplifySettings
            {
                targetRatio = 0.25f, targetError = 0.2f, uvChannel = 1
            }).simplifiedMesh;
            var group = root.AddComponent<LODGroup>();
            var renderers = new Renderer[3];
            var lodMeshes = new[] { source, authored, generated };
            for (int i = 0; i < renderers.Length; i++)
            {
                var child = CreateChild($"Grid_LOD{i}");
                child.AddComponent<MeshFilter>().sharedMesh = lodMeshes[i];
                renderers[i] = child.AddComponent<MeshRenderer>();
            }
            group.SetLODs(new[]
            {
                new LOD(0.5f, new[] { renderers[0] }),
                new LOD(0.25f, new[] { renderers[1] }),
                new LOD(0.125f, new[] { renderers[2] })
            });
            var context = new UvToolContext();
            context.Refresh(group);
            var owned = (List<GameObject>)typeof(UvToolContext)
                .GetProperty("GeneratedLodObjects", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(context);
            owned.Add(renderers[2].gameObject);
            var tool = new LodGenerationTool();
            tool.OnActivate(context, null);

            var baseline = GetGenerationBaseline(tool);
            Assert.That(baseline.sourceTris, Is.EqualTo(800));
            Assert.That(baseline.startLod, Is.EqualTo(2));
            Assert.That(baseline.lastRatio, Is.EqualTo((float)authored.triangles.Length / source.triangles.Length));
        }

        [Test]
        public void Generate_ErrorLimitCanProduceEqualCountsAndRelaxingItUpdatesEveryLod()
        {
            var (tool, context) = CreateToolWithSource();
            SetField(tool, "generateRelaxFarLods", false); // This regression exercises one shared strict error budget.
            SetField(tool, "generateBudgetPriority", false);
            SetField(tool, "generateLodCount", 2);
            SetField(tool, "generateLodRatios", new[] { 0.25f, 0.125f, 0.0625f, 0.03125f });
            SetField(tool, "generateTargetError", 0.001f);
            SetField(tool, "generateUv2Weight", 500f);
            Generate(tool, 1);
            var constrained = GeneratedTriangleCounts(context);
            Assert.That(constrained[1], Is.GreaterThan(200));
            Assert.That(constrained[2], Is.EqualTo(constrained[1]));
            AssertReportedTargets(tool, targetNotReached: true);

            SetField(tool, "generateTargetError", 0.2f);
            Generate(tool, 3);
            var relaxed = GeneratedTriangleCounts(context);
            Assert.That(relaxed[1], Is.InRange(190, 200));
            Assert.That(relaxed[2], Is.InRange(90, 100),
                "the later LOD's settings apply once the error budget allows it");
            AssertReportedTargets(tool, targetNotReached: false);
        }

        (LodGenerationTool tool, UvToolContext context) CreateToolWithSource()
        {
            var child = CreateChild("Grid_LOD0");
            child.AddComponent<MeshFilter>().sharedMesh = CreateGrid();
            var renderer = child.AddComponent<MeshRenderer>();
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(0.5f, new[] { renderer }) });
            var context = new UvToolContext();
            context.Refresh(group);
            var tool = new LodGenerationTool();
            tool.OnActivate(context, null);
            return (tool, context);
        }

        static void AssertReportedTargets(LodGenerationTool tool, bool targetNotReached)
        {
            var results = (IList)typeof(LodGenerationTool)
                .GetField("lastResults", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tool);
            Assert.That(results, Has.Count.EqualTo(2));
            for (int i = 0; i < results.Count; i++)
            {
                object result = results[i];
                var type = result.GetType();
                Assert.That(type.GetField("targetRatio").GetValue(result), Is.EqualTo(i == 0 ? 0.25f : 0.125f));
                Assert.That(type.GetField("targetTris").GetValue(result), Is.EqualTo(i == 0 ? 200 : 100));
                Assert.That(type.GetField("targetNotReached").GetValue(result), Is.EqualTo(targetNotReached));
            }
        }

        MeshSimplifier.SimplifyResult Simplify(Mesh source, MeshSimplifier.SimplifySettings settings)
        {
            var result = MeshSimplifier.Simplify(source, settings);
            Assert.That(result.ok, Is.True, result.error);
            meshes.Add(result.simplifiedMesh);
            return result;
        }

        Mesh CreateGrid()
        {
            const int size = 20;
            var vertices = new Vector3[(size + 1) * (size + 1)];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[size * size * 6];
            for (int y = 0; y <= size; y++)
                for (int x = 0; x <= size; x++)
                {
                    int index = y * (size + 1) + x;
                    vertices[index] = new Vector3((float)x / size, 0f, (float)y / size);
                    uv[index] = new Vector2((float)x / size, (float)y / size);
                }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int vertex = y * (size + 1) + x;
                    int index = (y * size + x) * 6;
                    triangles[index] = vertex;
                    triangles[index + 1] = vertex + size + 1;
                    triangles[index + 2] = vertex + 1;
                    triangles[index + 3] = vertex + 1;
                    triangles[index + 4] = vertex + size + 1;
                    triangles[index + 5] = vertex + size + 2;
                }
            var mesh = new Mesh { name = "Grid", vertices = vertices, triangles = triangles, uv2 = uv };
            meshes.Add(mesh);
            return mesh;
        }

        int[] GeneratedTriangleCounts(UvToolContext context)
        {
            var lods = context.LodGroup.GetLODs();
            var counts = new int[lods.Length];
            for (int i = 0; i < lods.Length; i++)
            {
                Assert.That(lods[i].renderers, Has.Length.EqualTo(1));
                var mesh = lods[i].renderers[0].GetComponent<MeshFilter>().sharedMesh;
                if (!meshes.Contains(mesh)) meshes.Add(mesh);
                counts[i] = mesh.triangles.Length / 3;
            }
            return counts;
        }

        static void SetField(LodGenerationTool tool, string name, object value)
            => typeof(LodGenerationTool).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(tool, value);

        static (int sourceTris, int startLod, float lastRatio) GetGenerationBaseline(LodGenerationTool tool)
        {
            var args = new object[] { 0, 0, 0f };
            typeof(LodGenerationTool).GetMethod("GetGenerationBaseline", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(tool, args);
            return ((int)args[0], (int)args[1], (float)args[2]);
        }

        static void Generate(LodGenerationTool tool, int startLod)
        {
            Undo.IncrementCurrentGroup();
            typeof(LodGenerationTool).GetMethod("ExecGenerateLods", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(tool, new object[] { startLod });
        }

        GameObject CreateChild(string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(root.transform);
            return child;
        }

        // The helpers below are internal to the editor assembly, so the test
        // assembly reaches them through reflection like the other suites here.
        static void NormalizeSingleLodTransitionForGeneration(List<LOD> lods, int startLod)
        {
            var method = typeof(LodGenerationTool).GetMethod(
                "NormalizeSingleLodTransitionForGeneration",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method,
                "LodGenerationTool should expose the LOD0 transition normalization as a static helper");

            method.Invoke(null, new object[] { lods, startLod });
        }

        static List<(GameObject go, int lodIndex)> FindLodSiblings(GameObject go)
        {
            var method = typeof(LodGenerationTool).GetMethod(
                "FindLodSiblings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "LodGenerationTool should expose FindLodSiblings as a static helper");

            object result = method.Invoke(null, new object[] { go });
            if (result == null) return null;

            var siblings = new List<(GameObject go, int lodIndex)>();
            foreach (object item in (IEnumerable)result)
            {
                var type = item.GetType();
                siblings.Add((
                    (GameObject)type.GetField("Item1").GetValue(item),
                    (int)type.GetField("Item2").GetValue(item)));
            }
            return siblings;
        }
    }
}
