using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class RemeshCapBudgetTests
    {
        [Serializable] public sealed class Metadata { public string settingsJson; public int[] sourceFaceOwners; }
        [Serializable] public sealed class Capture { public Metadata metadata; }
        [Serializable] public sealed class Report
        {
            public string method, description;
            public int addedFaces, remainingEdges, loops;
            public bool nativeChecked;
            public int nativeFaces;
            public double nativeSeconds;
            public string[] failures;
            public string[] bridgeFailures;
        }

        [TestCase("volvo")] [TestCase("playground")]
        public void FrozenManyRimCapturesKeepDonorsAndReportAuditedResults(string name)
        {
            string root = Environment.GetEnvironmentVariable("MESH_LAB_CAP_BUDGET_ROOT");
            if (string.IsNullOrEmpty(root)) Assert.Ignore("Set MESH_LAB_CAP_BUDGET_ROOT to decoded private captures.");
            string folder = Path.Combine(root, name);
            var metadata = JsonUtility.FromJson<Capture>(File.ReadAllText(Path.Combine(folder,"capture.json"))).metadata;
            var settings = RemeshSettings.FromSavedJson(metadata.settingsJson);
            using var reader = new BinaryReader(File.OpenRead(Path.Combine(folder,"source.bin")));
            int vertices = reader.ReadInt32(), count = reader.ReadInt32();
            var p = new Vector3[vertices]; var ix = new int[count];
            for (int i = 0; i < vertices; ++i) p[i] = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            for (int i = 0; i < count; ++i) ix[i] = reader.ReadInt32();
            var saved = (Vector3[])p.Clone(); var savedIndices = (int[])ix.Clone();
            int automaticAdded = -1;
            foreach (var mode in new[] { settings.closureMode }) {
                var support = RemeshPlanarCap.Prepare(p,ix,settings.planarCapLoops,default,true,mode,settings.capPlaneTolerance,
                    sourceFaceOwners:metadata.sourceFaceOwners,continueOnRefusal:true,elementScopedContacts:true,
                    bridgeCapFallback:settings.bridgeCapFallback);
                var report = new Report {method=mode.ToString(),description=support.Description,addedFaces=support.addedFaces,
                    remainingEdges=support.remainingBoundaryEdges,loops=support.loops,
                    bridgeFailures=support.bridgeCapFallbacks.Select(pair=>$"{pair.Key}: {pair.Value}").ToArray(),
                    failures=support.loopFailures.Select(pair=>$"{pair.Key}: {pair.Value}").ToArray()};
                File.WriteAllText(Path.Combine(folder,mode+"-report.json"),JsonUtility.ToJson(report,true));
                using (var writer = new BinaryWriter(File.Create(Path.Combine(folder,mode+"-support.bin")))) {
                    writer.Write(support.positions.Length); writer.Write(support.indices.Length);
                    foreach (var v in support.positions) {writer.Write(v.x);writer.Write(v.y);writer.Write(v.z);}
                    foreach (int v in support.indices) writer.Write(v);
                }
                TestContext.WriteLine(name+" "+mode+": "+support.Description);
                foreach (string reason in report.failures) TestContext.WriteLine(reason);
                foreach (string reason in report.bridgeFailures) TestContext.WriteLine(reason);
                Assert.IsTrue(RemeshTopology.Inspect(support.positions,support.indices).Valid);
                for (int i = 0; i < ix.Length; ++i) Assert.AreEqual(p[ix[i]],support.positions[support.indices[i]]);
                CollectionAssert.AreEqual(saved,p); CollectionAssert.AreEqual(savedIndices,ix);
                Assert.IsFalse(report.failures.Any(reason => reason.Contains("16 selected loops") || reason.Contains("16 rim edges")));
                if (mode == RemeshClosureMode.Automatic) automaticAdded = support.addedFaces;
                if (mode == RemeshClosureMode.Automatic) {
                    Assert.IsEmpty(support.loopFailures); Assert.AreEqual(0,support.remainingBoundaryEdges);
                    Assert.IsTrue(RemeshTopology.ClosedVolumeFaces(support.positions,support.indices,default).All(closed=>closed));
                    RemeshNative.CheckAvailable();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var voxel = RemeshNative.Voxelize(support.positions,support.indices,settings,default);
                    var topology = RemeshTopology.Inspect(voxel.positions,voxel.indices);
                    Assert.IsTrue(topology.Valid,topology.Description); Assert.AreEqual(0,topology.boundary.Count);
                    report.nativeChecked = true; report.nativeFaces = voxel.TriangleCount; report.nativeSeconds = timer.Elapsed.TotalSeconds;
                    File.WriteAllText(Path.Combine(folder,mode+"-report.json"),JsonUtility.ToJson(report,true));
                    TestContext.WriteLine($"{name} native: {settings.voxelResolution} resolution, solve={settings.solve}, {voxel.TriangleCount} faces, {report.nativeSeconds:F3}s");
                }
            }
            Assert.Greater(automaticAdded,name=="volvo"?18:0);
        }
    }
}
