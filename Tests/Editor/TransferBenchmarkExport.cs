using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>Optional private-fixture artifacts; the same read-only metric as the Viewer.</summary>
    internal static class TransferBenchmarkExport
    {
        [Serializable] sealed class Layout
        {
            public int lod;
            public string key;
            public Vector3[] positions;
            public Vector3[] metricPositions;
            public Matrix4x4 metricToWorld;
            public Vector2[] uv;
            public int[] indices;
        }

        internal static void Reverse(string directory, ReverseUvTransfer.Result result, ReverseUvTransfer.Level[] inputs, bool finalAtlas = true)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"report.json"),JsonUtility.ToJson(result.report,true));
            var provenance = ReverseUvAudit.Provenance(result.report)
                .Select(JsonUtility.FromJson<ReverseUvAudit.ProvenanceEntry>).ToArray();
            var parts = inputs.Select((level, l) => level.inputs.Select((input, n) => {
                var part = Capture(result.meshes[l][n], input.key, input.toWorld);
                part.provenance = provenance.Single(p => p.surface.lod == level.lod && p.surface.key == input.key);
                WriteLayout(directory, level.lod, input.key, part);
                return part;
            }).ToArray()).ToArray();
            if(!finalAtlas) return;
            int reference = Array.FindIndex(inputs, l => l.lod == 0);
            for (int l = 0; l < inputs.Length; ++l)
            {
                var report = WriteQuality(directory, inputs[l].lod, parts[l],
                    inputs[l].lod == 0 || reference < 0 ? null : parts[reference]);
                Assert.IsTrue(report.complete, "Kamaz Viewer quality scan must complete.");
                Assert.AreEqual(0, report.invalidFaces + report.collapsedFaces);
                Assert.AreEqual(0,report.unexpectedOverlapPairs,"Viewer must not manufacture intersections in an audited reverse atlas.");
            }
        }

        internal static void Forward(string directory, UvToolContext context)
        {
            var levels = context.MeshEntries.GroupBy(e => e.lodIndex).OrderBy(g => g.Key).ToArray();
            var parts = levels.Select(level => level.Select(entry => {
                var part = Capture(context.Assets.GetResultMesh(entry), entry.fbxMesh.name, entry.renderer.localToWorldMatrix);
                WriteLayout(directory, level.Key, entry.fbxMesh.name, part);
                return part;
            }).ToArray()).ToArray();
            int reference = Array.FindIndex(levels, l => l.Key == 0);
            for (int l = 0; l < levels.Length; ++l)
            {
                var report = WriteQuality(directory, levels[l].Key, parts[l],
                    levels[l].Key == 0 || reference < 0 ? null : parts[reference]);
                Assert.IsTrue(report.complete, "Forward Viewer quality scan must complete.");
            }
        }

        static LightmapUvQuality.Part Capture(Mesh mesh, string key, Matrix4x4 frame)
        {
            var part = UvToolHub.CaptureQualityPart(mesh, new MeshEntry {
                meshGroupKey = UvToolContext.ExtractGroupKey(key)
            }, frame, 1);
            part.sourceKey = key;
            return part;
        }

        static void WriteLayout(string directory, int lod, string key, LightmapUvQuality.Part part)
            => File.WriteAllText(Path.Combine(directory, key + ".layout.json"), JsonUtility.ToJson(new Layout {
                lod = lod, key = key, positions = part.positions, metricPositions = part.metricPositions,
                metricToWorld = part.metricToWorld, uv = part.uv, indices = part.triangles
            }));

        static LightmapUvQuality.Report WriteQuality(string directory, int lod,
            LightmapUvQuality.Part[] parts, LightmapUvQuality.Part[] reference)
        {
            var report = LightmapUvQuality.Measure(parts, reference);
            File.WriteAllText(Path.Combine(directory, "quality_LOD" + lod + ".json"), JsonUtility.ToJson(report, true));
            return report;
        }
    }
}
