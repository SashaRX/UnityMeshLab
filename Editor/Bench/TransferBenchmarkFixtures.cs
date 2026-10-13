using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TransferBenchmarkFixtures
    {
        internal static readonly string[] Names = {
            "texture-1024x2048-corrected", "texture-1024x2048-uncorrected-control",
            "mirrored-stacked-uv0", "close-opposite-surfaces", "retriangulated-lod", "nonlinear-uv2", "same-shell-overlap-control"
        };

        internal static TransferBenchmark.Input Create(string name)
        {
            var source = Grid(2, false); var target = Grid(1, true);
            var input = new TransferBenchmark.Input { name = name, source = source, target = target, atlasWidth = 512, atlasHeight = 512,
                referenceIsGroundTruth = true, description = "Analytic fixture; fixed source UV2, no native repack in timed transfer." };
            try {
                switch (name) {
                    case "texture-1024x2048-corrected":
                        SetRectangularUv(source); SetRectangularUv(target);
                        break;
                    case "texture-1024x2048-uncorrected-control":
                        SetRectangularUv(source); SetRectangularUv(target);
                        var wrong = source.uv2;
                        for (int i = 0; i < wrong.Length; ++i) wrong[i].y = .1f + (wrong[i].y - .1f) * .5f;
                        source.uv2 = wrong; input.negativeControl = true;
                        input.description = "Intentionally uncorrected source atlas: anisotropy 2. Transfer alone cannot fix source UV2.";
                        break;
                    case "mirrored-stacked-uv0":
                        UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(target);
                        input.source = Stacked(false, false); input.target = Stacked(true, false);
                        input.description = "Two disconnected 3D instances share mirrored UV0 but occupy distinct UV2 charts.";
                        break;
                    case "close-opposite-surfaces":
                        UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(target);
                        input.source = Stacked(false, true); input.target = Stacked(true, true);
                        input.description = "Opposite normals, 0.02 gap; LOD vertices move closer to the other surface.";
                        break;
                    case "retriangulated-lod": break;
                    case "nonlinear-uv2":
                        var nonlinear = source.uv2; var positions = source.vertices;
                        for (int i = 0; i < nonlinear.Length; ++i) nonlinear[i].y += .1f * positions[i].x * positions[i].y;
                        source.uv2 = nonlinear;
                        break;
                    case "same-shell-overlap-control":
                        UnityEngine.Object.DestroyImmediate(target);
                        input.target = Grid(2, true);
                        source.uv2 = Fold(source.vertices); input.negativeControl = true;
                        input.description = "A connected source chart folds over itself; same-shell overlap must be reported.";
                        break;
                    default: throw new ArgumentException("Unknown fixture: " + name);
                }
                input.reference = Reference(input);
                return input;
            }
            catch { input.Dispose(); throw; }
        }

        static Mesh Grid(int divisions, bool flipDiagonal)
        {
            var positions = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var triangles = new List<int>();
            for (int y = 0; y <= divisions; ++y)
                for (int x = 0; x <= divisions; ++x) {
                    float u = (float)x / divisions, v = (float)y / divisions;
                    positions.Add(new Vector3(u, v, 0)); uv.Add(new Vector2(u, v)); uv2.Add(new Vector2(.1f + u * .3f, .1f + v * .3f));
                }
            for (int y = 0; y < divisions; ++y)
                for (int x = 0; x < divisions; ++x) {
                    int a = y * (divisions + 1) + x, b = a + 1, c = b + divisions + 1, d = a + divisions + 1;
                    triangles.AddRange(flipDiagonal ? new[] { a, b, d, b, c, d } : new[] { a, b, c, a, c, d });
                }
            var mesh = new Mesh { name = "Benchmark grid" };
            mesh.SetVertices(positions); mesh.SetUVs(0, uv); mesh.SetUVs(1, uv2); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals();
            return mesh;
        }

        static void SetRectangularUv(Mesh mesh)
        {
            var uv = mesh.uv;
            for (int i = 0; i < uv.Length; ++i) uv[i].y *= .5f;
            mesh.uv = uv;
        }

        static Mesh Stacked(bool target, bool close)
        {
            var positions = new List<Vector3>(); var uv0 = new List<Vector2>(); var uv2 = new List<Vector2>(); var triangles = new List<int>();
            for (int side = 0; side < 2; ++side) AddStackedSide(target, close, side, positions, uv0, uv2, triangles);
            var mesh = new Mesh { name = "Stacked mirror fixture" };
            mesh.SetVertices(positions); mesh.SetUVs(0, uv0); mesh.SetUVs(1, uv2); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals();
            return mesh;
        }

        static void AddStackedSide(bool target, bool close, int side, List<Vector3> positions, List<Vector2> uv0,
            List<Vector2> uv2, List<int> triangles)
        {
            float z = side * 2;
            if (close) z = side == 0 ? 0 : .02f;
            if (target && close) z += side == 0 ? .015f : -.015f;
            int start = positions.Count;
            positions.AddRange(new[] { new Vector3(0, 0, z), new Vector3(1, 0, z), new Vector3(1, 1, z), new Vector3(0, 1, z) });
            uv0.AddRange(side == 0 ? new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }
                : new[] { Vector2.right, Vector2.zero, Vector2.up, Vector2.one });
            uv2.AddRange(new[] { new Vector2(.1f + side * .5f, .1f), new Vector2(.4f + side * .5f, .1f),
                new Vector2(.4f + side * .5f, .4f), new Vector2(.1f + side * .5f, .4f) });
            bool reverse = close && side == 1;
            int[] local = target ? new[] { 0, 1, 3, 1, 2, 3 } : new[] { 0, 1, 2, 0, 2, 3 };
            for (int i = 0; i < local.Length; i += 3) {
                triangles.Add(start + local[i]); triangles.Add(start + local[i + (reverse ? 2 : 1)]); triangles.Add(start + local[i + (reverse ? 1 : 2)]);
            }
        }

        static Vector2[] Reference(TransferBenchmark.Input input)
        {
            var positions = input.target.vertices; var uv = new Vector2[positions.Length];
            for (int i = 0; i < uv.Length; ++i) {
                bool stacked = input.name == "mirrored-stacked-uv0" || input.name == "close-opposite-surfaces";
                uv[i] = new Vector2(.1f + positions[i].x * .3f + (stacked && i >= 4 ? .5f : 0), .1f + positions[i].y * .3f);
                if (input.name == "nonlinear-uv2") uv[i].y += .1f * positions[i].x * positions[i].y;
            }
            return uv;
        }

        static Vector2[] Fold(Vector3[] positions)
        {
            var uv = new Vector2[positions.Length];
            for (int i = 0; i < uv.Length; ++i) uv[i] = new Vector2(.1f + Mathf.Abs(positions[i].x - .5f) * .6f, .1f + positions[i].y * .3f);
            return uv;
        }
    }
}
