using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ModelValidationTests
    {
        static readonly Vector3[] Quad = {Vector3.zero, Vector3.right, new Vector3(1, 1), Vector3.up};
        static readonly int[] QuadFaces = {0, 1, 2, 0, 2, 3};
        readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        Mesh QuadMesh(Vector2[] uv2 = null, bool tangents = true)
        {
            var mesh = new Mesh {name = "Quad", vertices = Quad, triangles = QuadFaces,
                uv = new[] {Vector2.zero, Vector2.right, Vector2.one, Vector2.up},
                normals = Enumerable.Repeat(Vector3.forward, 4).ToArray()};
            mesh.uv2 = uv2 ?? mesh.uv.Select(p => p * .5f + Vector2.one * .25f).ToArray();
            if (tangents) mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 4).ToArray();
            owned.Add(mesh);
            return mesh;
        }

        [Test]
        public void DegenerateFaces_CountsCollapsedAndFlatFaces()
        {
            var positions = new[] {Vector3.zero, Vector3.right, new Vector3(1, 1), new Vector3(2, 0), new Vector3(2, 0),
                new Vector3(.5f, 1e-8f), new Vector3(.5f, 1e-4f)};
            var faces = new[] {0, 1, 2,   0, 0, 1,   3, 4, 0,   0, 1, 5,   0, 1, 6};
            var (degenerate, zeroEdge) = MeshValidation.DegenerateFaces(positions, faces);
            Assert.AreEqual(3, degenerate, "a repeated index, two corners at one point, a face a hundred-millionth high");
            Assert.AreEqual(2, zeroEdge, "the flat face has no short edge");
        }

        [Test]
        public void DegenerateFaces_MeasuresAFaceAgainstItsOwnSize()
        {
            var tenthOfAMillimetre = Quad.Select(p => p * 1e-4f).ToArray();
            Assert.AreEqual((0, 0), MeshValidation.DegenerateFaces(tenthOfAMillimetre, QuadFaces), "a small face is not a degenerate one");
        }

        [Test]
        public void NonManifoldEdges_CountsAnEdgeOfThreeFacesAcrossSplitVertices()
        {
            // Three fins on the edge (0,0,0)-(1,0,0), each with its own copies of the edge's vertices.
            var tips = new[] {Vector3.up, Vector3.forward, Vector3.back};
            var positions = tips.SelectMany(tip => new[] {Vector3.zero, Vector3.right, tip}).ToArray();
            var faces = Enumerable.Range(0, positions.Length).ToArray();
            Assert.AreEqual(1, MeshValidation.NonManifoldEdges(positions, faces));
            Assert.AreEqual(0, MeshValidation.NonManifoldEdges(Quad, QuadFaces), "two faces on an edge");
        }

        [Test]
        public void FlippedFaces_CountsFacesWhoseNormalsAllPointAgainstTheWinding()
        {
            // Unity's front is Cross(b - a, c - a): the quad's faces wind towards +z.
            var front = Enumerable.Repeat(Vector3.forward, 4).ToArray();
            var back = front.Select(n => -n).ToArray();
            var oneCorner = (Vector3[])back.Clone(); oneCorner[1] = Vector3.forward;
            Assert.AreEqual(0, MeshValidation.FlippedFaces(Quad, front, QuadFaces));
            Assert.AreEqual(2, MeshValidation.FlippedFaces(Quad, back, QuadFaces));
            Assert.AreEqual(1, MeshValidation.FlippedFaces(Quad, oneCorner, QuadFaces), "the face with a corner along its winding is not flipped");
            Assert.AreEqual(0, MeshValidation.FlippedFaces(Quad, null, QuadFaces), "no normals, nothing to compare");
        }

        [Test]
        public void UnusedVerticesAndLightmapBounds()
        {
            Assert.AreEqual(1, MeshValidation.UnusedVertices(QuadFaces, 5));
            var (outside, min, max) = MeshValidation.OutsideUnit(new[] {new Vector2(1.00005f, 0f), new Vector2(-.02f, .5f), Vector2.one},
                MeshValidation.LightmapTolerance);
            Assert.AreEqual(1, outside, "rounding past the edge is not outside");
            Assert.AreEqual(new Vector2(-.02f, 0f), min); Assert.AreEqual(new Vector2(1.00005f, 1f), max);
        }

        [Test]
        public void Check_ReportsMissingChannelsAndALightmapOutsideAsErrors()
        {
            var mesh = QuadMesh(new[] {Vector2.zero, new Vector2(1.05f, 0f), Vector2.one, Vector2.up}, tangents: false);
            var report = new PrepareReport("Bench.fbx");
            MeshValidation.Check(mesh, "'Seat'", false, ModelRequirements.Default, report);
            CollectionAssert.AreEquivalent(new[] {"Channels", "Lightmap UV"}, report.Lines.Select(l => l.check));
            var channels = report.Lines.Single(l => l.check == "Channels");
            Assert.AreEqual(PrepareLevel.Error, channels.level); StringAssert.Contains("missing tangents", channels.message);
            StringAssert.Contains("1 UV1 vertex lies outside [0,1]", report.Lines.Single(l => l.check == "Lightmap UV").message);
            Assert.IsTrue(report.BlocksApply); Assert.AreEqual("2 errors", report.Summary());
        }

        [Test]
        public void Check_HoldsACollisionMeshToTheGeometryChecks()
        {
            var mesh = QuadMesh(new[] {Vector2.zero, new Vector2(1.05f, 0f), Vector2.one, Vector2.up}, tangents: false);
            var report = new PrepareReport("Bench.fbx");
            MeshValidation.Check(mesh, "'Seat_COL'", true, ModelRequirements.Default, report);
            CollectionAssert.IsEmpty(report.Lines);
            Assert.AreEqual(PrepareLevel.Unchanged, report.Level); Assert.AreEqual("no findings", report.Summary());
        }

        [Test]
        public void Check_ReportsAtTheLevelTheRequirementsSet()
        {
            var mesh = QuadMesh(new[] {Vector2.zero, new Vector2(1.05f, 0f), Vector2.one, Vector2.up}, tangents: false);
            var requirements = ModelRequirements.Default;
            requirements.channels = RequirementLevel.Warning; requirements.lightmapInsideUnit = RequirementLevel.Off;
            var report = new PrepareReport("Bench.fbx");
            MeshValidation.Check(mesh, "'Seat'", false, requirements, report);
            Assert.AreEqual(1, report.Lines.Count); Assert.AreEqual(PrepareLevel.Warning, report.Level);
            Assert.IsFalse(report.BlocksApply, "warnings are only shown"); Assert.AreEqual("1 warning", report.Summary());
        }

        [Test]
        public void Check_WarnsOfFlippedNormals()
        {
            var mesh = QuadMesh();
            mesh.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
            var report = new PrepareReport("Bench.fbx");
            MeshValidation.Check(mesh, "'Seat'", false, ModelRequirements.Default, report);
            var line = report.Lines.Single();
            Assert.AreEqual("Flipped normals", line.check); Assert.AreEqual(PrepareLevel.Warning, line.level, "a warning by default");
            Assert.IsFalse(report.BlocksApply);
        }

        [Test]
        public void Check_NamesTheStreamsWithNonFiniteValues()
        {
            var mesh = QuadMesh();
            mesh.uv = new[] {Vector2.zero, new Vector2(float.NaN, 0f), Vector2.one, new Vector2(float.PositiveInfinity, 1f)};
            var report = new PrepareReport("Bench.fbx");
            MeshValidation.Check(mesh, "'Seat'", false, ModelRequirements.Default, report);
            var line = report.Lines.Single();
            Assert.AreEqual("Finite values", line.check); Assert.AreEqual(PrepareLevel.Error, line.level);
            StringAssert.Contains("2 vertices with NaN or infinite values (UV0)", line.message);
        }

        [Test]
        public void Check_ReadsTheFacesOfAQuadSubmesh()
        {
            var mesh = QuadMesh();
            mesh.SetIndices(new[] {0, 1, 2, 3}, MeshTopology.Quads, 0);
            mesh.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
            var report = new PrepareReport("Bench.fbx");
            MeshValidation.Check(mesh, "'Seat'", false, ModelRequirements.Default, report);
            var line = report.Lines.Single();
            Assert.AreEqual("Flipped normals", line.check, "the quad's vertices are in a face, not unused");
            StringAssert.Contains("2 faces whose vertex normals point against the face winding", line.message, "a quad is two triangles");
        }

        [Test]
        public void Validate_ChecksAMeshARenderNodeSharesWithACollisionNodeAsARenderMesh()
        {
            var mesh = QuadMesh(tangents: false);
            var root = new GameObject("Bench"); owned.Add(root);
            foreach (string name in new[] {"Seat_COL", "Seat"})
            {
                var node = new GameObject(name);
                node.transform.SetParent(root.transform);
                node.AddComponent<MeshFilter>().sharedMesh = mesh;
            }
            var report = new PrepareReport("Bench.fbx");
            ModelValidation.Validate(root, null, report);
            var channels = report.Lines.Single(l => l.check == "Channels");
            Assert.AreEqual("'Seat' (mesh 'Quad')", channels.subject, "reported under the node that renders it, whatever the sibling order");
        }

        [Test]
        public void Validate_ChecksEachMeshOnceAndACollisionNodeForGeometryOnly()
        {
            var mesh = QuadMesh(tangents: false);
            var root = new GameObject("Bench"); owned.Add(root);
            foreach (string name in new[] {"Seat", "Seat_LOD1", "Seat_COL"})
            {
                var node = new GameObject(name);
                node.transform.SetParent(root.transform);
                node.AddComponent<MeshFilter>().sharedMesh = name == "Seat_COL" ? Object.Instantiate(mesh) : mesh;
                if (name == "Seat_COL") owned.Add(node.GetComponent<MeshFilter>().sharedMesh);
            }
            var report = new PrepareReport("Bench.fbx");
            ModelValidation.Validate(root, null, report);
            Assert.AreEqual(1, report.Lines.Count(l => l.check == "Channels"), "Seat and Seat_LOD1 share the mesh");
            Assert.AreEqual("'Seat' (mesh 'Quad')", report.Lines.First(l => l.check == "Channels").subject);
            Assert.IsFalse(report.Lines.Any(l => l.subject.StartsWith("'Seat_COL'")), "the collision mesh needs no channels");
        }
    }
}
