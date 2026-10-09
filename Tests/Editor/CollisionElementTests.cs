using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class CollisionElementTests
    {
        static RemeshNative.IndexedMesh Box(Vector3 size, Quaternion rotation, Vector3 offset)
        {
            var p = new Vector3[8];
            var corners = new[] { new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
                new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
            for (int i = 0; i < p.Length; i++) p[i] = rotation * Vector3.Scale(corners[i], size * .5f) + offset;
            return new RemeshNative.IndexedMesh { positions = p,
                indices = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 } };
        }

        static RemeshNative.IndexedMesh Tetrahedron(Vector3 offset) => new RemeshNative.IndexedMesh {
            positions = new[] { offset, offset + Vector3.right, offset + Vector3.up, offset + Vector3.forward },
            indices = new[] { 0,2,1, 0,1,3, 0,3,2, 1,2,3 }
        };

        static RemeshNative.IndexedMesh Subdivide(RemeshNative.IndexedMesh mesh)
        {
            var p = new List<Vector3>(mesh.positions);
            var t = new List<int>();
            for (int f = 0; f < mesh.indices.Length; f += 3)
            {
                int a = mesh.indices[f], b = mesh.indices[f + 1], c = mesh.indices[f + 2];
                int ab = p.Count; p.Add((p[a] + p[b]) * .5f);
                int bc = p.Count; p.Add((p[b] + p[c]) * .5f);
                int ca = p.Count; p.Add((p[c] + p[a]) * .5f);
                t.AddRange(new[] { a,ab,ca, ab,b,bc, ca,bc,c, ab,bc,ca });
            }
            return new RemeshNative.IndexedMesh { positions = p.ToArray(), indices = t.ToArray() };
        }

        static RemeshNative.IndexedMesh Tunnel()
        {
            var p = new List<Vector3>();
            foreach (float z in new[] { -.05f, .05f })
            {
                foreach (float r in new[] { 1f, .1f })
                    p.AddRange(new[] { new Vector3(-r,-r,z), new Vector3(r,-r,z), new Vector3(r,r,z), new Vector3(-r,r,z) });
            }
            var t = new List<int>();
            void Quad(int a, int b, int c, int d) => t.AddRange(new[] { a,b,c, a,c,d });
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                Quad(8+i,8+j,12+j,12+i);
                Quad(i,4+i,4+j,j);
                Quad(i,j,8+j,8+i);
                Quad(4+i,12+i,12+j,4+j);
            }
            return new RemeshNative.IndexedMesh { positions = p.ToArray(), indices = t.ToArray() };
        }

        [TestCase(.001f)]
        [TestCase(1f)]
        [TestCase(100f)]
        public void FilledRotatedElementUsesAnOrientedBoxWithoutCallingRemesh(float scale)
        {
            var source = Subdivide(Box(new Vector3(2,.5f,.1f) * scale, Quaternion.Euler(23,37,11), new Vector3(3,5,-2) * scale));
            var output = CollisionElementPreprocessor.PrepareCore(source, CollisionMeshBuilder.ConvexDecompSettings.CoarseParts,
                out var report, _ => throw new InvalidOperationException("Remesh must not be used for this box"),
                _ => throw new InvalidOperationException("Decimation must not be used for this box"));
            Assert.AreEqual(CollisionMeshBuilder.ElementPreparation.Box, report.preparation);
            Assert.AreEqual(12, output.TriangleCount);
            Assert.AreEqual(8, output.positions.Length);
            Assert.Greater(report.boxFill, .99f);
            Assert.Less(report.sampledError, .00001f);
            Assert.IsNull(output.normals);
            Assert.IsNull(output.uv);
        }

        [Test]
        public void HighBboxFillDoesNotAllowABoxToSealAnAuthoredTunnel()
        {
            var source = Tunnel();
            var topology = RemeshTopology.Inspect(source.positions, source.indices, default);
            Assert.IsTrue(topology.Valid, topology.Description);
            Assert.IsEmpty(topology.boundary);
            var output = CollisionElementPreprocessor.PrepareCore(source, CollisionMeshBuilder.ConvexDecompSettings.CoarseParts,
                out var report, input => input, input => input);
            Assert.Greater(report.boxFill, .98f, "the hole removes only one percent of bbox volume");
            Assert.AreEqual(CollisionMeshBuilder.ElementPreparation.Source, report.preparation);
            Assert.AreSame(source, output, "high occupancy alone is insufficient evidence for replacing a hollow part");
        }

        [Test]
        public void AGeometricallyWrongRemeshIsRejectedEvenWhenTopologyAndTriangleBudgetPass()
        {
            var source = Subdivide(Tetrahedron(Vector3.zero));
            var wrong = Tetrahedron(Vector3.right);
            var output = CollisionElementPreprocessor.PrepareCore(source, CollisionMeshBuilder.ConvexDecompSettings.CoarseParts,
                out var report, _ => wrong, _ => throw new InvalidOperationException("No decimation available"));
            Assert.AreSame(source, output);
            Assert.AreEqual(CollisionMeshBuilder.ElementPreparation.Source, report.preparation);
            StringAssert.Contains("surface distance", report.detail);
        }

        [Test]
        public void FailedRemeshCanFallBackToAccurateSourceDecimation()
        {
            var simplified = Tetrahedron(Vector3.zero);
            var source = Subdivide(simplified);
            var output = CollisionElementPreprocessor.PrepareCore(source, CollisionMeshBuilder.ConvexDecompSettings.CoarseParts,
                out var report, _ => throw new InvalidOperationException("Bad voxel topology"), _ => simplified);
            Assert.AreSame(simplified, output);
            Assert.AreEqual(CollisionMeshBuilder.ElementPreparation.Decimate, report.preparation);
            Assert.AreEqual(16, report.sourceTriangles);
            Assert.AreEqual(4, report.preparedTriangles);
            Assert.Less(report.sampledError, .00001f);
        }

        [Test]
        public void OpenElementDoesNotGetASpuriousVolumeFillEstimate()
        {
            var source = new RemeshNative.IndexedMesh {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up }, indices = new[] { 0,1,2 }
            };
            var output = CollisionElementPreprocessor.PrepareCore(source, CollisionMeshBuilder.ConvexDecompSettings.CoarseParts,
                out var report, _ => throw new InvalidOperationException("No closed replacement"), input => input);
            Assert.AreEqual(-1f, report.boxFill);
            Assert.AreSame(source, output);
        }

        [Test]
        public void OpenElementCanUseAThinClosedRemeshThatRetainsItsFootprint()
        {
            var source = new RemeshNative.IndexedMesh {
                positions = new[] { Vector3.zero, Vector3.right, Vector3.up }, indices = new[] { 0,1,2 }
            };
            var closed = new RemeshNative.IndexedMesh {
                positions = new[] { new Vector3(0,0,-.005f), new Vector3(1,0,-.005f), new Vector3(0,1,-.005f),
                    new Vector3(0,0,.005f), new Vector3(1,0,.005f), new Vector3(0,1,.005f) },
                indices = new[] { 0,2,1, 3,4,5, 0,1,4, 0,4,3, 1,2,5, 1,5,4, 2,0,3, 2,3,5 }
            };
            var output = CollisionElementPreprocessor.PrepareCore(source, CollisionMeshBuilder.ConvexDecompSettings.CoarseParts,
                out var report, _ => closed, input => input);
            Assert.AreSame(closed, output);
            Assert.AreEqual(CollisionMeshBuilder.ElementPreparation.RemeshDecimate, report.preparation);
            Assert.AreEqual(-1f, report.boxFill, "open source volume must stay unknown even after successful repair");
            Assert.Less(report.sampledError, .01f);
            Assert.IsEmpty(RemeshTopology.Inspect(output.positions, output.indices, default).boundary);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(0f)]
        public void ElementSettingsRejectInvalidFitTolerance(float error)
        {
            var settings = CollisionMeshBuilder.ConvexDecompSettings.CoarseParts;
            settings.elementFitError = error;
            Assert.IsFalse(CollisionElementPreprocessor.ValidSettings(settings));
        }
    }
}
