using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests
{
    public class TransferBenchmarkTests
    {
        string root;
        [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "MeshLabTransferBenchTests_" + Guid.NewGuid().ToString("N"));
        [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [Test]
        public void Config_RejectsUnboundedRunsUnknownMethodsAndEmptyCorpus()
        {
            Assert.Throws<InvalidDataException>(() => TransferBenchmark.Validate(new TransferBenchmark.Config { repetitions = 1 }));
            Assert.Throws<InvalidDataException>(() => TransferBenchmark.Validate(new TransferBenchmark.Config { repetitions = 8 }));
            Assert.Throws<InvalidDataException>(() => TransferBenchmark.Validate(new TransferBenchmark.Config { maxPairs = 129 }));
            Assert.Throws<InvalidDataException>(() => TransferBenchmark.Validate(new TransferBenchmark.Config { methods = new[] { "unknown" } }));
            Assert.Throws<InvalidDataException>(() => TransferBenchmark.Validate(new TransferBenchmark.Config { methods = new[] { "grouped", "grouped" } }));
            Assert.Throws<InvalidDataException>(() => TransferBenchmark.Validate(new TransferBenchmark.Config { includeSynthetic = false }));
        }

        [UnityTest]
        public IEnumerator SyntheticCorpus_DistinguishesStretchOverlapSymmetryAndNonlinearMapping()
        {
            bool dirty = EditorSceneManager.GetActiveScene().isDirty;
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int iterations = GroupedShellTransfer.LastTopologyIterations, fixes = GroupedShellTransfer.LastTopologyFixed;
            bool cap = GroupedShellTransfer.LastTopologyCapHit;
            var task = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = root, repetitions = 2, warmup = 0 });
            while (!task.IsCompleted) yield return null;
            var report = task.GetAwaiter().GetResult();
            Assert.IsTrue(report.complete, JsonUtility.ToJson(report));
            Assert.AreEqual(TransferBenchmarkFixtures.Names.Length * TransferBenchmarkMethods.Names.Length, report.rows.Count);
            Assert.IsTrue(report.rows.All(row => row.deterministic && row.inputUnchanged && row.milliseconds.Length == 2));
            var corrected = Find(report, "texture-1024x2048-corrected", "uv0-nearest");
            var wrong = Find(report, "texture-1024x2048-uncorrected-control", "uv0-nearest");
            Assert.AreEqual(1, corrected.quality.areaWeightedAnisotropy, 1e-5);
            Assert.AreEqual(2, wrong.quality.areaWeightedAnisotropy, 1e-5);
            Assert.IsTrue(corrected.referencePass); Assert.IsFalse(wrong.referencePass); Assert.IsTrue(wrong.negativeControl);
            Assert.Greater(Find(report, "mirrored-stacked-uv0", "uv0-nearest").maximumReferenceTexels, 100);
            Assert.IsTrue(Find(report, "mirrored-stacked-uv0", "surface-nearest").referencePass);
            Assert.IsFalse(Find(report, "close-opposite-surfaces", "surface-nearest").referencePass);
            Assert.IsTrue(Find(report, "close-opposite-surfaces", "surface-normal").referencePass);
            Assert.Greater(Find(report, "nonlinear-uv2", "shell-similarity").maximumReferenceTexels, 1);
            var fold = Find(report, "same-shell-overlap-control", "uv0-nearest");
            Assert.Greater(fold.sourceQuality.overlapPairs, 0);
            Assert.Greater(fold.quality.overlapPairs, 0); Assert.IsTrue(fold.quality.overlapScanComplete);
            Assert.IsTrue(File.Exists(Path.Combine(report.folder, "comparison.csv")));
            Assert.IsTrue(File.Exists(Path.Combine(report.folder, "index.html")));
            var details = TransferCaseCapture.ReadDetails<TransferBenchmark.Details>(report.folder, corrected.details);
            Assert.AreEqual(corrected.vertices, details.uv.Length); Assert.AreEqual(corrected.vertices, details.reference.Length);
            Assert.AreEqual(dirty, EditorSceneManager.GetActiveScene().isDirty);
            Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length, "Temporary benchmark meshes leaked.");
            Assert.AreEqual(iterations, GroupedShellTransfer.LastTopologyIterations); Assert.AreEqual(fixes, GroupedShellTransfer.LastTopologyFixed);
            Assert.AreEqual(cap, GroupedShellTransfer.LastTopologyCapHit); Assert.IsFalse(UvProgress.IsActive);
        }

        [UnityTest]
        public IEnumerator CapturedCorpus_SupportsLegacyAndExternalPayloadsAndPreservesHints()
        {
            string first = Capture(1), second = Capture(2);
            var task = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = root, repetitions = 2, warmup = 0, includeSynthetic = false,
                captures = new[] { first, second }, methods = new[] { "grouped", "grouped-no-hints", "uv0-nearest" } });
            while (!task.IsCompleted) yield return null;
            var report = task.GetAwaiter().GetResult();
            Assert.IsTrue(report.complete, JsonUtility.ToJson(report)); Assert.AreEqual(6, report.rows.Count);
            Assert.IsTrue(report.rows.All(row => !row.referenceIsGroundTruth && row.matchHints == 1 && row.overlapHints == 1));
            Assert.IsTrue(report.rows.All(row => row.referencePass));
            Assert.IsTrue(report.rows.All(row => row.quality.areaWeightedAnisotropy > 1.9), "Captured world scale must affect quality, not correspondence.");
        }

        [UnityTest]
        public IEnumerator CorruptCapture_FailsOnlyItsCaseAndWritesPartialReport()
        {
            string capture = Capture(2);
            var manifest = JsonUtility.FromJson<TransferCaseCapture.Manifest>(File.ReadAllText(capture));
            string mesh = Path.Combine(Path.GetDirectoryName(capture), "meshes", manifest.pairs[0].sourceMesh + ".bin");
            byte[] bytes = File.ReadAllBytes(mesh); bytes[bytes.Length - 1] ^= 1; File.WriteAllBytes(mesh, bytes);
            var task = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = root, repetitions = 2, warmup = 0,
                captures = new[] { capture }, methods = new[] { "surface-nearest" } });
            while (!task.IsCompleted) yield return null;
            var report = task.GetAwaiter().GetResult();
            Assert.IsFalse(report.complete); Assert.AreEqual(8, report.rows.Count);
            Assert.IsTrue(report.rows[7].error.Contains("checksum"));
            Assert.IsTrue(File.Exists(Path.Combine(report.folder, "comparison.json"))); Assert.IsFalse(UvProgress.IsActive);
        }

        [UnityTest]
        public IEnumerator SnapshotWithoutTransfer_IsExplicitFailure()
        {
            Directory.CreateDirectory(root); string file = Path.Combine(root, "manifest.json");
            File.WriteAllText(file, JsonUtility.ToJson(new TransferCaseCapture.Manifest()));
            var task = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = root, includeSynthetic = false, captures = new[] { file } });
            while (!task.IsCompleted) yield return null;
            var report = task.GetAwaiter().GetResult();
            Assert.IsFalse(report.complete); StringAssert.Contains("no completed transfer pairs", report.error);
            Assert.IsTrue(File.Exists(Path.Combine(report.folder, "comparison.json")));
        }

        [UnityTest]
        public IEnumerator AssetPreparation_NormalizesRectangularTextureAndFreezesPortableInputs()
        {
            string asset = "Assets/__MeshLabTransferBench_" + Guid.NewGuid().ToString("N") + ".asset";
            using var fixture = TransferBenchmarkFixtures.Create("texture-1024x2048-corrected");
            fixture.source.name = "Bench_LOD0"; fixture.target.name = "Bench_LOD1";
            var container = ScriptableObject.CreateInstance<TestSuiteAsset>();
            AssetDatabase.StartAssetEditing();
            try { AssetDatabase.CreateAsset(container, asset); AssetDatabase.AddObjectToAsset(fixture.source, asset); AssetDatabase.AddObjectToAsset(fixture.target, asset); }
            finally { AssetDatabase.StopAssetEditing(); }
            try {
                string sourceHash = TransferMeshSnapshot.Hash(TransferMeshSnapshot.Capture(fixture.source));
                var task = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = root, includeSynthetic = false, repetitions = 2, warmup = 0,
                    methods = new[] { "uv0-nearest" }, assetCases = new[] {
                        new TransferBenchmarkAssets.Case { asset = asset, label = "corrected", textureWidth = 1024, textureHeight = 2048,
                            resolution = 128, arap = false, normalizeDensity = false, symmetry = "adaptive" },
                        new TransferBenchmarkAssets.Case { asset = asset, label = "control", textureWidth = 1024, textureHeight = 2048,
                            resolution = 128, arap = false, normalizeDensity = false, correctSourceAspect = false, symmetry = "legacy" },
                        new TransferBenchmarkAssets.Case { asset = asset, label = "reverse", textureWidth = 1024, textureHeight = 2048,
                            sourceLod = 1, includeHigherDetailTargets = true, resolution = 128, arap = false, normalizeDensity = false } } });
                while (!task.IsCompleted) yield return null;
                var report = task.GetAwaiter().GetResult();
                Assert.IsTrue(report.complete, JsonUtility.ToJson(report)); Assert.AreEqual(3, report.rows.Count);
                Assert.AreEqual(1, report.rows[0].sourceQuality.areaWeightedAnisotropy, .02);
                Assert.AreEqual(2, report.rows[1].sourceQuality.areaWeightedAnisotropy, .02);
                Assert.AreEqual(1, report.rows[2].quality.areaWeightedAnisotropy, .02);
                StringAssert.Contains("LOD0", report.rows[2].name);
                Assert.AreEqual(sourceHash, TransferMeshSnapshot.Hash(TransferMeshSnapshot.Capture(fixture.source)));
                string frozen = Path.Combine(report.folder, "manifest.json");
                var replay = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = root, includeSynthetic = false,
                    repetitions = 2, warmup = 0, captures = new[] { frozen }, methods = new[] { "uv0-nearest" } });
                while (!replay.IsCompleted) yield return null;
                var repeated = replay.GetAwaiter().GetResult();
                Assert.IsTrue(repeated.complete, JsonUtility.ToJson(repeated)); Assert.AreEqual(3, repeated.rows.Count);
                Assert.AreEqual(report.rows[0].uvHash, repeated.rows[0].uvHash); Assert.AreEqual(report.rows[1].uvHash, repeated.rows[1].uvHash);
                Assert.AreEqual(report.rows[2].uvHash, repeated.rows[2].uvHash);
            }
            finally { AssetDatabase.DeleteAsset(asset); }
        }

        [Test]
        public void Reports_EscapeAssetNamesAndKeepOneCsvRecordPerRow()
        {
            var report = new TransferBenchmark.Report();
            report.rows.Add(new TransferBenchmark.Row { name = "=1+1\n<script>bad</script>", method = "x", error = "<img src=x onerror=alert(1)>" });
            var html = TransferBenchmarkReport.Html(report); var csv = TransferBenchmarkReport.Csv(report);
            Assert.IsFalse(html.Contains("<script>")); Assert.IsFalse(html.Contains("<img src=x"));
            StringAssert.Contains("&lt;script&gt;", html); StringAssert.Contains("'=1+1 ", csv);
            Assert.AreEqual(3, csv.Split('\n').Length);
        }

        [UnityTest]
        public IEnumerator CancelBeforeFirstCase_WritesAbortedCorpusAndRestoresProgress()
        {
            Action cancel = null;
            cancel = () => {
                if (!UvProgress.IsActive) return;
                UvProgress.OnChanged -= cancel;
                UvProgress.RequestCancel();
            };
            UvProgress.OnChanged += cancel;
            try {
                var task = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = root });
                while (!task.IsCompleted) yield return null;
                var report = task.GetAwaiter().GetResult();
                Assert.IsTrue(report.cancelled); Assert.IsFalse(report.complete); Assert.AreEqual(0, report.rows.Count);
                Assert.IsFalse(UvProgress.IsActive);
                var corpus = JsonUtility.FromJson<TransferCaseCapture.Manifest>(File.ReadAllText(Path.Combine(report.folder, "manifest.json")));
                Assert.AreEqual("aborted", corpus.status);
            }
            finally { UvProgress.OnChanged -= cancel; }
        }

        [UnityTest]
        public IEnumerator OutputInsideAssets_IsRejectedBeforeCreatingFiles()
        {
            var task = TransferBenchmark.Run(new TransferBenchmark.Config { outputRoot = Application.dataPath });
            while (!task.IsCompleted) yield return null;
            Assert.Throws<InvalidDataException>(() => task.GetAwaiter().GetResult());
            Assert.IsFalse(UvProgress.IsActive);
        }

        static TransferBenchmark.Row Find(TransferBenchmark.Report report, string fixture, string method)
            => report.rows.Single(row => row.name == fixture && row.method == method);

        string Capture(int schema)
        {
            string directory = Path.Combine(root, "capture_" + schema); Directory.CreateDirectory(Path.Combine(directory, "meshes"));
            using var input = TransferBenchmarkFixtures.Create("retriangulated-lod");
            var pair = new TransferCaseCapture.Pair { index = 0, source = "Source_LOD0", target = "Target_LOD1", status = "complete", atlasWidth = 512, atlasHeight = 512,
                localToWorld = Matrix4x4.Scale(new Vector3(2, 1, 1)), sourceMesh = Store(directory, input.source), targetMesh = Store(directory, input.target),
                baselineUvHash = TransferMeshSnapshot.UvHash(input.reference), overlapHints = new System.Collections.Generic.List<GroupedShellTransfer.OverlapSourceHint> {
                    new GroupedShellTransfer.OverlapSourceHint { sourceShellIndex = 0, centroid3D = new Vector3(.5f, .5f, 0) } },
                matchHints = new System.Collections.Generic.List<GroupedShellTransfer.CrossLodMatchHint> {
                    new GroupedShellTransfer.CrossLodMatchHint { sourceShellIndex = 0, centroid3D = new Vector3(.5f, .5f, 0), uv0Centroid = new Vector2(.5f, .5f),
                        uv0BoundsMin = Vector2.zero, uv0BoundsMax = Vector2.one, faceCount = 2, quality = GroupedShellTransfer.ShellStatus.Accepted } } };
            input.target.uv2 = input.reference; pair.outputMesh = Store(directory, input.target);
            if (schema == 2) {
                byte[] details = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(new TransferCaseCapture.PairDetails {
                    overlapHints = pair.overlapHints, matchHints = pair.matchHints }));
                pair.details = TransferMeshSnapshot.Hash(details);
                Directory.CreateDirectory(Path.Combine(directory, "details"));
                File.WriteAllBytes(Path.Combine(directory, "details", pair.details + ".json"), details);
                pair.overlapHints = null; pair.matchHints = null;
            }
            var manifest = new TransferCaseCapture.Manifest { schema = schema, status = "complete" }; manifest.pairs.Add(pair);
            string path = Path.Combine(directory, "manifest.json"); File.WriteAllText(path, JsonUtility.ToJson(manifest)); return path;
        }
        static string Store(string directory, Mesh mesh)
        {
            byte[] bytes = TransferMeshSnapshot.Capture(mesh); string hash = TransferMeshSnapshot.Hash(bytes);
            File.WriteAllBytes(Path.Combine(directory, "meshes", hash + ".bin"), bytes); return hash;
        }
    }
}
