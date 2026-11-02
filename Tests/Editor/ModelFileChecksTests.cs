using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ModelFileChecksTests
    {
        readonly List<Object> owned = new List<Object>();
        string file;

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            if (file != null && File.Exists(file)) File.Delete(file);
            file = null;
        }

        [Test]
        public void ImportScale_NamesAScaleFactorAndConvertUnitsOff()
        {
            CollectionAssert.IsEmpty(ModelFileChecks.ImportScaleFindings(1f, true));
            CollectionAssert.IsEmpty(ModelFileChecks.ImportScaleFindings(.99999994f, true), "FBX rounding is 1");
            CollectionAssert.AreEqual(new[] {"Scale Factor is 100, not 1", "Convert Units is off"},
                ModelFileChecks.ImportScaleFindings(100f, false));
        }

        [TestCase(.01f, "centimetres")]
        [TestCase(.001f, "millimetres")]
        [TestCase(.0254f, "inches")]
        [TestCase(.3048f, "feet")]
        [TestCase(2f, "units of 2 m")]
        public void UnitName_ReadsTheFileScale(float fileScale, string unit) => Assert.AreEqual(unit, ModelFileChecks.UnitName(fileScale));

        [Test]
        public void ScaleFinding_TellsAScaleFromAMirror()
        {
            Assert.IsNull(ModelFileChecks.ScaleFinding(Vector3.one));
            Assert.IsNull(ModelFileChecks.ScaleFinding(new Vector3(.99999994f, 1.00001f, 1f)));
            Assert.AreEqual("at (100, 100, 100)", ModelFileChecks.ScaleFinding(Vector3.one * 100f));
            Assert.AreEqual("mirrored (1, 1, -1)", ModelFileChecks.ScaleFinding(new Vector3(1, 1, -1)));
        }

        GameObject Model(Vector3 rootScale, Vector3 seatScale, float colSize = 1f)
        {
            var quad = new Mesh {name = "Quad", vertices = new[] {Vector3.zero, Vector3.right, new Vector3(1, 1), Vector3.up}, triangles = new[] {0, 1, 2, 0, 2, 3}};
            var col = Object.Instantiate(quad); col.name = "Seat_COL"; col.vertices = col.vertices.Select(p => p * colSize).ToArray(); col.RecalculateBounds();
            owned.Add(quad); owned.Add(col);
            var root = new GameObject("Bench"); owned.Add(root);
            root.transform.localScale = rootScale;
            var seat = new GameObject("Seat"); seat.transform.SetParent(root.transform, false);
            seat.transform.localScale = seatScale; seat.AddComponent<MeshFilter>().sharedMesh = quad;
            var collision = new GameObject("Seat_COL"); collision.transform.SetParent(root.transform, false);
            collision.AddComponent<MeshFilter>().sharedMesh = col;
            return root;
        }

        [Test]
        public void NodeScale_NamesTheNodesAndLeavesAPlacedRootAlone()
        {
            var root = Model(Vector3.one * 100f, new Vector3(1, 1, -1));
            var report = new PrepareReport("Bench.fbx");
            ModelFileChecks.CheckNodeScales(root, true, ModelRequirements.Default, report);
            var line = report.Lines.Single();
            Assert.AreEqual(PrepareLevel.Error, line.level); Assert.AreEqual("Node scale", line.check);
            StringAssert.Contains("2 nodes are not at scale 1: 'Bench' at (100, 100, 100), 'Seat' mirrored (1, 1, -1)", line.message);

            report = new PrepareReport("Bench");
            ModelFileChecks.CheckNodeScales(root, false, ModelRequirements.Default, report);
            StringAssert.Contains("1 node is not at scale 1: 'Seat' mirrored", report.Lines.Single().message);
        }

        [Test]
        public void Size_MeasuresTheRenderMeshesInTheRootsSpace()
        {
            var root = Model(Vector3.one * 7f, Vector3.one * 1000f, colSize: 1e-4f);
            Assert.AreEqual(1000f, ModelFileChecks.RenderSize(root).Value, 1e-2f, "the root's own scale is placement; the collision mesh does not count");
            var report = new PrepareReport("Bench.fbx");
            ModelFileChecks.CheckSize(root, ModelRequirements.Default, report);
            var line = report.Lines.Single();
            Assert.AreEqual(PrepareLevel.Warning, line.level);
            StringAssert.Contains("the model is 1000 m at its largest, outside 0.01–200 m", line.message);

            report = new PrepareReport("Bench.fbx");
            ModelFileChecks.CheckSize(Model(Vector3.one, Vector3.one), ModelRequirements.Default, report);
            CollectionAssert.IsEmpty(report.Lines, "a metre-wide bench");
        }

        [Test]
        public void Smoothing_ListsTheRenderMeshesWithoutIt()
        {
            var smoothing = new Dictionary<string, bool> {{"Seat", false}, {"Base", true}, {"Seat_COL", false}, {"Back", false}};
            CollectionAssert.AreEqual(new[] {"Back", "Seat"}, ModelFileChecks.WithoutSmoothing(smoothing));
        }

        [Test]
        public void Smoothing_ReadsTheFbxItself()
        {
            file = Path.Combine(Path.GetTempPath(), "MeshLabSmoothingCheck.fbx");
            File.WriteAllText(file, "; FBX 7.4.0 project file\nObjects:  {\n"
                + "\tGeometry: 1, \"Geometry::Seat\", \"Mesh\" {\n\t\tLayerElementSmoothing: 0 {\n\t\t}\n\t}\n"
                + "\tGeometry: 2, \"Geometry::Base\", \"Mesh\" {\n\t\tLayerElementNormal: 0 {\n\t\t}\n\t}\n"
                + "\tGeometry: 3, \"Geometry::Base_COL\", \"Mesh\" {\n\t}\n}\n");
            var report = new PrepareReport(file);
            ModelFileChecks.CheckSmoothing(file, ModelRequirements.Default, report);
            var line = report.Lines.Single();
            Assert.AreEqual(PrepareLevel.Error, line.level);
            StringAssert.Contains("1 mesh has no smoothing groups in the FBX: 'Base'", line.message);
        }
    }
}
