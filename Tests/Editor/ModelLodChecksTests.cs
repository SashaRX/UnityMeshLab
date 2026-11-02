using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ModelLodChecksTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        static PrepareReport Lods(params (string name, int triangles)[] nodes)
        {
            var report = new PrepareReport("Bench.fbx");
            ModelLodChecks.CheckLods(nodes, ModelRequirements.Default, report);
            return report;
        }

        [Test]
        public void ACleanChainHasNoFindings()
        {
            var report = Lods(("Seat_Wood_LOD0", 1000), ("Seat_Wood_LOD1", 400), ("Base_Concrete_LOD0", 800), ("Base_Concrete_LOD1", 300),
                ("Base_Concrete_COL", 12), ("Pillow", 50));
            CollectionAssert.IsEmpty(report.Lines);
        }

        [Test]
        public void ALodWithoutFewerTrianglesIsAnError()
        {
            var report = Lods(("Seat_LOD0", 1000), ("Seat_LOD1", 1000), ("Seat_LOD2", 950));
            var lines = report.Lines.Where(l => l.check == "LOD triangles").ToList();
            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual(PrepareLevel.Error, lines[0].level); Assert.AreEqual("'Seat'", lines[0].subject);
            StringAssert.Contains("LOD1 has 1000 triangles, not fewer than LOD0's 1000", lines[0].message);
            Assert.AreEqual(PrepareLevel.Warning, lines[1].level);
            StringAssert.Contains("LOD2 keeps 95% of LOD1's 1000 triangles (at most 80%)", lines[1].message);
        }

        [Test]
        public void TheLodsOfOnePartAddUpAcrossItsNodes()
        {
            // Two nodes of one part on one level (under different parents) count together; an
            // instance-numbered copy (.001) is a part of its own.
            CollectionAssert.IsEmpty(Lods(("Seat_LOD0", 600), ("Seat_LOD0", 600), ("Seat_LOD1", 900), ("Seat_LOD1", 0)).Lines);
            var copy = Lods(("Seat_LOD0", 600), ("Seat_LOD1", 300), ("Seat_LOD0.001", 600));
            Assert.AreEqual("'Seat.001'", copy.Lines.Single().subject);
        }

        [Test]
        public void ChainFindings_NameGapsAndShortParts()
        {
            Assert.IsNull(ModelLodChecks.ChainFinding(new List<int> {0, 1}, 1));
            Assert.AreEqual("has LOD0, LOD2; missing LOD1", ModelLodChecks.ChainFinding(new List<int> {0, 2}, 2));
            Assert.AreEqual("has LOD0; other parts go to LOD1", ModelLodChecks.ChainFinding(new List<int> {0}, 1));
            Assert.AreEqual("has LOD1; missing LOD0", ModelLodChecks.ChainFinding(new List<int> {1}, 1));

            var report = Lods(("Seat_LOD0", 1000), ("Seat_LOD1", 400), ("Base_LOD0", 800), ("Back_LOD9", 10));
            var chain = report.Lines.Where(l => l.check == "LOD chain").ToList();
            Assert.IsTrue(chain.All(l => l.level == PrepareLevel.Warning));
            StringAssert.Contains("'Back_LOD9': past the 8 levels a LODGroup holds", chain[0].message);
            Assert.AreEqual("'Base'", chain[1].subject); Assert.AreEqual("has LOD0; other parts go to LOD1", chain[1].message);
        }

        [Test]
        public void Names_ReportsSiblingsSharingAName()
        {
            var root = new GameObject("Bench"); owned.Add(root);
            foreach (var name in new[] {"Seat", "Seat", "Base"}) new GameObject(name).transform.SetParent(root.transform);
            var report = new PrepareReport("Bench.fbx");
            ModelLodChecks.CheckNames(root, ModelRequirements.Default, report);
            var line = report.Lines.Single();
            Assert.AreEqual(PrepareLevel.Warning, line.level);
            StringAssert.Contains("'Seat' ×2 under 'Bench'", line.message);
        }

        [Test]
        public void VertexLimit_IsAnErrorPastTheLimit()
        {
            var mesh = new Mesh {name = "Strip"}; owned.Add(mesh);
            var positions = Enumerable.Range(0, 12).Select(i => new Vector3(i % 2, i / 2, 0)).ToArray();
            mesh.vertices = positions;
            mesh.triangles = Enumerable.Range(0, 10).SelectMany(i => i % 2 == 0 ? new[] {i, i + 1, i + 2} : new[] {i + 1, i, i + 2}).ToArray();
            var requirements = ModelRequirements.Default;
            requirements.maxVertices = 10; requirements.channels = RequirementLevel.Off; requirements.lightmapInsideUnit = RequirementLevel.Off;
            var report = new PrepareReport("Strip.fbx");
            MeshValidation.Check(mesh, "'Strip'", false, requirements, report);
            var line = report.Lines.Single(l => l.check == "Vertex limit");
            Assert.AreEqual(PrepareLevel.Error, line.level); StringAssert.Contains("12 vertices, over the limit of 10", line.message);

            report = new PrepareReport("Strip.fbx");
            MeshValidation.Check(mesh, "'Strip_COL'", true, requirements, report);
            Assert.IsFalse(report.Lines.Any(l => l.check == "Vertex limit"), "a collision mesh has no vertex limit here");
        }
    }
}
