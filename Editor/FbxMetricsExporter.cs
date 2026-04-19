// FbxMetricsExporter.cs — Menu-driven export of source-FBX characterization metrics
// + UV0/UV2 snapshot PNGs. Run once per test suite to pair with BenchmarkRecorder
// CSVs so the sweep numbers can be interpreted against each model's baseline.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LightmapUvTool
{
    public static class FbxMetricsExporter
    {
        [MenuItem("Mesh Lab/Export FBX Metrics (Selected Assets)")]
        public static void ExportForSelection()
        {
            var fbxPaths = Selection.assetGUIDs
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p) &&
                            p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            if (fbxPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Export FBX Metrics",
                    "No .fbx assets selected in the Project window.", "OK");
                return;
            }
            Run(fbxPaths);
        }

        [MenuItem("Mesh Lab/Export FBX Metrics (Scene LODGroup)")]
        public static void ExportForSceneLodGroup()
        {
            var lod = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<LODGroup>()
                : null;
            if (lod == null)
            {
                EditorUtility.DisplayDialog("Export FBX Metrics",
                    "Select a GameObject under a LODGroup in the Hierarchy.", "OK");
                return;
            }

            var renderers = new List<Renderer>();
            foreach (var level in lod.GetLODs())
                foreach (var r in level.renderers ?? new Renderer[0])
                    if (r != null) renderers.Add(r);

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string outDir = PrepareOutDir(stamp);
            var rows = new List<Row>();
            int lodIdx = 0;
            foreach (var level in lod.GetLODs())
            {
                foreach (var r in level.renderers ?? new Renderer[0])
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var row = AnalyzeMesh(lod.name, lodIdx, r.name, mf.sharedMesh);
                    rows.Add(row);
                    UvPngWriter.Render(Path.Combine(outDir, "png",
                        $"{Sanitize(lod.name)}_LOD{lodIdx}_{Sanitize(r.name)}_uv0.png"),
                        mf.sharedMesh, 0);
                    if (row.hasUv2)
                        UvPngWriter.Render(Path.Combine(outDir, "png",
                            $"{Sanitize(lod.name)}_LOD{lodIdx}_{Sanitize(r.name)}_uv2.png"),
                            mf.sharedMesh, 1);
                }
                lodIdx++;
            }
            WriteCsv(outDir, stamp, rows);
            UvtLog.Info(UvtLog.Category.Benchmark,
                $"FBX metrics: {rows.Count} row(s) → {outDir}");
            EditorUtility.RevealInFinder(outDir);
        }

        static void Run(List<string> fbxPaths)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string outDir = PrepareOutDir(stamp);
            string pngDir = Path.Combine(outDir, "png");
            Directory.CreateDirectory(pngDir);

            var rows = new List<Row>();
            int total = fbxPaths.Count;
            for (int i = 0; i < total; i++)
            {
                string path = fbxPaths[i];
                if (EditorUtility.DisplayCancelableProgressBar("Export FBX Metrics",
                        $"{i + 1}/{total}: {Path.GetFileName(path)}",
                        (float)i / Mathf.Max(1, total)))
                    break;

                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) continue;
                string modelName = Path.GetFileNameWithoutExtension(path);

                // Collect renderers (either under a LODGroup or loose).
                var lodGroups = root.GetComponentsInChildren<LODGroup>(true);
                if (lodGroups != null && lodGroups.Length > 0)
                {
                    foreach (var lg in lodGroups)
                    {
                        int lodIdx = 0;
                        foreach (var level in lg.GetLODs())
                        {
                            foreach (var r in level.renderers ?? new Renderer[0])
                                AnalyzeAndDump(r, modelName, lg.name, lodIdx, rows, pngDir);
                            lodIdx++;
                        }
                    }
                }
                else
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        AnalyzeAndDump(r, modelName, root.name, 0, rows, pngDir);
                }
            }
            EditorUtility.ClearProgressBar();

            WriteCsv(outDir, stamp, rows);
            UvtLog.Info(UvtLog.Category.Benchmark,
                $"FBX metrics: {rows.Count} row(s) from {fbxPaths.Count} FBX → {outDir}");
            EditorUtility.RevealInFinder(outDir);
        }

        static void AnalyzeAndDump(Renderer r, string modelName, string lodGroupName,
            int lodIdx, List<Row> rows, string pngDir)
        {
            if (r == null) return;
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            var mesh = mf.sharedMesh;
            var row = AnalyzeMesh($"{modelName}:{lodGroupName}", lodIdx, r.name, mesh);
            rows.Add(row);

            UvPngWriter.Render(Path.Combine(pngDir,
                $"{Sanitize(modelName)}_{Sanitize(lodGroupName)}_LOD{lodIdx}_{Sanitize(r.name)}_uv0.png"),
                mesh, 0);
            if (row.hasUv2)
                UvPngWriter.Render(Path.Combine(pngDir,
                    $"{Sanitize(modelName)}_{Sanitize(lodGroupName)}_LOD{lodIdx}_{Sanitize(r.name)}_uv2.png"),
                    mesh, 1);
        }

        // ── Analysis ───────────────────────────────────────────────────

        class Row
        {
            public string modelName, lodGroupName, rendererName;
            public int lodIndex;
            public int vertexCount, triangleCount, submeshCount;
            public Vector3 boundsSize;
            public float avgEdgeLengthWorld;
            public int uv0ShellCount;
            public float uv0TotalCoverage;
            public float uv0MaxShellArea;
            public float uv0MeanShellArea;
            public int uv0AabbOverlapPairs;
            public int uv0OobVertexCount;
            public int disconnectedGeomIslands;
            public bool hasUv2;
            public int uv2ShellCount;
            public int uv2AabbOverlapPairs;
            public int uv2OobVertexCount;
            public int mirrorPairsDetected;
            public float shellAreaStdDev;
        }

        static Row AnalyzeMesh(string modelAndGroup, int lodIdx, string rendererName, Mesh mesh)
        {
            var row = new Row
            {
                modelName    = modelAndGroup,
                lodGroupName = modelAndGroup.Contains(":") ? modelAndGroup.Split(':')[1] : modelAndGroup,
                rendererName = rendererName,
                lodIndex     = lodIdx,
                vertexCount  = mesh.vertexCount,
                triangleCount = mesh.triangles.Length / 3,
                submeshCount = mesh.subMeshCount,
                boundsSize   = mesh.bounds.size,
            };

            var verts = mesh.vertices;
            var tris  = mesh.triangles;
            row.avgEdgeLengthWorld = AvgEdgeLength(verts, tris);
            row.disconnectedGeomIslands = CountGeometryIslands(tris, verts.Length);

            // UV0 analysis
            var uv0List = new List<Vector2>();
            mesh.GetUVs(0, uv0List);
            if (uv0List.Count > 0 && tris.Length >= 3)
            {
                var uv0 = uv0List.ToArray();
                List<UvShell> shells = null;
                try { shells = UvShellExtractor.Extract(uv0, tris); } catch { }
                if (shells != null)
                {
                    row.uv0ShellCount = shells.Count;
                    float total = 0f, max = 0f;
                    foreach (var sh in shells)
                    {
                        float a = Mathf.Max(0f, sh.bboxArea);
                        total += a;
                        if (a > max) max = a;
                    }
                    row.uv0TotalCoverage = total;
                    row.uv0MaxShellArea  = max;
                    row.uv0MeanShellArea = shells.Count > 0 ? total / shells.Count : 0f;
                    row.uv0AabbOverlapPairs = UvShellExtractor.CountAabbOverlaps(shells);
                    row.shellAreaStdDev = StdDev(shells.Select(s => Mathf.Max(0f, s.bboxArea)));
                    row.mirrorPairsDetected = CountMirrorPairs(shells);
                }
                row.uv0OobVertexCount = CountOob(uv0);
            }

            // UV2 analysis (if present)
            var uv2List = new List<Vector2>();
            mesh.GetUVs(1, uv2List);
            if (uv2List.Count > 0 && uv2List.Count == mesh.vertexCount)
            {
                row.hasUv2 = true;
                var uv2 = uv2List.ToArray();
                List<UvShell> shells = null;
                try { shells = UvShellExtractor.Extract(uv2, tris); } catch { }
                if (shells != null)
                {
                    row.uv2ShellCount = shells.Count;
                    row.uv2AabbOverlapPairs = UvShellExtractor.CountAabbOverlaps(shells);
                }
                row.uv2OobVertexCount = CountOob(uv2);
            }

            return row;
        }

        static float AvgEdgeLength(Vector3[] v, int[] t)
        {
            if (v == null || t == null || t.Length < 3) return 0f;
            double sum = 0; long n = 0;
            for (int f = 0; f < t.Length; f += 3)
            {
                int a = t[f], b = t[f + 1], c = t[f + 2];
                if (a >= v.Length || b >= v.Length || c >= v.Length) continue;
                sum += Vector3.Distance(v[a], v[b]); n++;
                sum += Vector3.Distance(v[b], v[c]); n++;
                sum += Vector3.Distance(v[c], v[a]); n++;
            }
            return n > 0 ? (float)(sum / n) : 0f;
        }

        static int CountOob(Vector2[] uv)
        {
            int c = 0;
            for (int i = 0; i < uv.Length; i++)
                if (uv[i].x < -0.001f || uv[i].x > 1.001f ||
                    uv[i].y < -0.001f || uv[i].y > 1.001f) c++;
            return c;
        }

        // Geometry-island count via union-find on shared vertex indices
        static int CountGeometryIslands(int[] tris, int vertCount)
        {
            if (tris == null || tris.Length < 3 || vertCount <= 0) return 0;
            var parent = new int[vertCount];
            for (int i = 0; i < vertCount; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
            for (int f = 0; f < tris.Length; f += 3)
            {
                int a = tris[f], b = tris[f + 1], c = tris[f + 2];
                if (a >= vertCount || b >= vertCount || c >= vertCount) continue;
                Union(a, b); Union(b, c);
            }
            var roots = new HashSet<int>();
            for (int f = 0; f < tris.Length; f += 3)
            {
                int a = tris[f];
                if (a < vertCount) roots.Add(Find(a));
            }
            return roots.Count;
        }

        static float StdDev(IEnumerable<float> xs)
        {
            var arr = xs as float[] ?? xs.ToArray();
            if (arr.Length == 0) return 0f;
            double mean = 0; foreach (var x in arr) mean += x; mean /= arr.Length;
            double sq = 0; foreach (var x in arr) { double d = x - mean; sq += d * d; }
            return (float)Math.Sqrt(sq / arr.Length);
        }

        // Rough mirror-pair count: pairs of shells with similar area + boundaryLength
        // but centroid.x reflected across 0.5. Heuristic only, matches SymSplit legacy.
        static int CountMirrorPairs(List<UvShell> shells)
        {
            if (shells == null || shells.Count < 2) return 0;
            int pairs = 0;
            var used = new bool[shells.Count];
            for (int i = 0; i < shells.Count; i++)
            {
                if (used[i]) continue;
                var a = shells[i];
                float aCx = (a.boundsMin.x + a.boundsMax.x) * 0.5f;
                for (int j = i + 1; j < shells.Count; j++)
                {
                    if (used[j]) continue;
                    var b = shells[j];
                    float bCx = (b.boundsMin.x + b.boundsMax.x) * 0.5f;
                    float bCyDy = Mathf.Abs(
                        (a.boundsMin.y + a.boundsMax.y) * 0.5f -
                        (b.boundsMin.y + b.boundsMax.y) * 0.5f);
                    bool mirroredX = Mathf.Abs((aCx + bCx) - 1f) < 0.05f && bCyDy < 0.05f;
                    if (!mirroredX) continue;
                    float areaRel = Mathf.Abs(a.bboxArea - b.bboxArea) /
                                    Mathf.Max(1e-6f, Mathf.Max(a.bboxArea, b.bboxArea));
                    if (areaRel > 0.1f) continue;
                    pairs++; used[i] = used[j] = true; break;
                }
            }
            return pairs;
        }

        // ── Output ─────────────────────────────────────────────────────

        static string PrepareOutDir(string stamp)
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string dir = Path.Combine(root, "BenchmarkReports", $"FbxMetrics_{stamp}");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "png"));
            return dir;
        }

        static void WriteCsv(string dir, string stamp, List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("model,lodGroup,renderer,lodIndex,vertexCount,triangleCount,submeshCount," +
                "boundsSizeX,boundsSizeY,boundsSizeZ,avgEdgeLengthWorld,disconnectedGeomIslands," +
                "uv0ShellCount,uv0TotalCoverage,uv0MaxShellArea,uv0MeanShellArea,uv0ShellAreaStdDev," +
                "uv0AabbOverlapPairs,uv0OobVertexCount,mirrorPairsDetected," +
                "hasUv2,uv2ShellCount,uv2AabbOverlapPairs,uv2OobVertexCount");
            var inv = CultureInfo.InvariantCulture;
            foreach (var r in rows)
            {
                sb.Append(Csv(r.modelName)).Append(',')
                  .Append(Csv(r.lodGroupName)).Append(',')
                  .Append(Csv(r.rendererName)).Append(',')
                  .Append(r.lodIndex).Append(',')
                  .Append(r.vertexCount).Append(',')
                  .Append(r.triangleCount).Append(',')
                  .Append(r.submeshCount).Append(',')
                  .Append(r.boundsSize.x.ToString("R", inv)).Append(',')
                  .Append(r.boundsSize.y.ToString("R", inv)).Append(',')
                  .Append(r.boundsSize.z.ToString("R", inv)).Append(',')
                  .Append(r.avgEdgeLengthWorld.ToString("R", inv)).Append(',')
                  .Append(r.disconnectedGeomIslands).Append(',')
                  .Append(r.uv0ShellCount).Append(',')
                  .Append(r.uv0TotalCoverage.ToString("R", inv)).Append(',')
                  .Append(r.uv0MaxShellArea.ToString("R", inv)).Append(',')
                  .Append(r.uv0MeanShellArea.ToString("R", inv)).Append(',')
                  .Append(r.shellAreaStdDev.ToString("R", inv)).Append(',')
                  .Append(r.uv0AabbOverlapPairs).Append(',')
                  .Append(r.uv0OobVertexCount).Append(',')
                  .Append(r.mirrorPairsDetected).Append(',')
                  .Append(r.hasUv2 ? 1 : 0).Append(',')
                  .Append(r.uv2ShellCount).Append(',')
                  .Append(r.uv2AabbOverlapPairs).Append(',')
                  .Append(r.uv2OobVertexCount);
                sb.AppendLine();
            }
            File.WriteAllText(Path.Combine(dir, $"FbxMetrics_{stamp}.csv"), sb.ToString(), Encoding.UTF8);
        }

        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool q = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            return q ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        // PNG rendering delegated to UvPngWriter.
    }
}
