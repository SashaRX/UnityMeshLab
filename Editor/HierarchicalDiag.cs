// HierarchicalDiag.cs — Editor-only diagnostic probe for the inverse-hierarchical
// UV2 transfer concept. Read-only — does NOT modify any mesh, asset, or sidecar.
//
// For every face on every non-deepest LOD of the active LODGroup it finds the
// best-matching parent on the deepest LOD. v2 reports two correspondence
// modes for the same input so the operator can compare:
//
//   1. face-level (naive, v1 baseline): nearest deepest-LOD triangle by 3D
//      centroid → angle between face normals + area-ratio against THAT
//      triangle. Suffers from tessellation mismatch — a single front-face
//      of LOD0 vs a 2-tri box-face of LOD3 trivially gives ratio>1 and
//      arbitrary angle picks.
//
//   2. shell-level (v2 main signal): extract 3D shells on the deepest LOD
//      via face-adjacency + normal threshold (≤30°), then for every fine
//      face pick the K=10 nearest shells by centroid and choose the one
//      with the smallest normal angle. Reported angle is to the shell's
//      area-weighted dominant normal; reported area-ratio is against
//      TOTAL shell area (not a single triangle). This matches what the
//      real HierarchicalRepack would do (LOD3 packs at shell granularity,
//      not at triangle granularity).
//
// CSV gets both columns side-by-side. Console summary reports both. The
// shell-level numbers are the ones that map to the GO/STOP decision in
// the plan; face-level is kept as a sanity reference.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class HierarchicalDiag
    {
        const string MenuPath = "Mesh Lab/Diag/Hierarchical Containment Probe";

        // θ thresholds in degrees. ~71% area is retained under orthographic
        // projection at θ=45°, ~50% at θ=60°. Below 30° projection is near
        // isometric — that band is the "safe" containment zone.
        static readonly float[] kThetaSamples = { 15f, 30f, 45f, 60f, 90f };

        // Adjacency-based shell extraction on the deepest LOD: two adjacent
        // faces belong to the same shell if their normals differ by less
        // than this. Matches xatlas hard-edge analysis convention.
        const float kShellNormalThresholdDeg = 30f;

        // K-nearest shells to consider when picking the best parent for a
        // fine face. The 1st-nearest by centroid often isn't the best by
        // angle on tessellated / curved deepest LODs — searching K=10
        // gives near-optimal results in our prior debugging without
        // performance impact (sub-millisecond per fine face).
        const int kShellSearchK = 10;

        [MenuItem(MenuPath, true)]
        static bool Validate()
        {
            var go = Selection.activeGameObject;
            return go != null && go.GetComponentInParent<LODGroup>() != null;
        }

        [MenuItem(MenuPath)]
        static void RunProbe()
        {
            var lg = Selection.activeGameObject?.GetComponentInParent<LODGroup>();
            if (lg == null)
            {
                EditorUtility.DisplayDialog("Hierarchical Containment Probe",
                    "Select a GameObject under a LODGroup first.", "OK");
                return;
            }
            try
            {
                string path = ProbeLodGroup(lg);
                EditorUtility.DisplayDialog("Hierarchical Containment Probe",
                    $"Probe complete.\n\nCSV: {path}\n\nSee console for summary.", "OK");
            }
            catch (System.Exception ex)
            {
                UvtLog.Error(UvtLog.Category.Benchmark,
                    $"[HierDiag] Probe failed: {ex.Message}\n{ex.StackTrace}");
                EditorUtility.DisplayDialog("Hierarchical Containment Probe",
                    $"Probe failed:\n{ex.Message}", "OK");
            }
        }

        /// <summary>
        /// Probe a single LODGroup. Writes a per-face CSV under
        /// <c>BenchmarkReports/hierdiag_&lt;ts&gt;_&lt;lgName&gt;.csv</c> and
        /// logs a summary block to the console. Returns the CSV path.
        /// </summary>
        public static string ProbeLodGroup(LODGroup lg)
        {
            var lods = lg.GetLODs();
            if (lods.Length < 2)
                throw new System.InvalidOperationException(
                    "LODGroup needs at least 2 LOD levels for hierarchical probe.");
            int deepestIdx = lods.Length - 1;

            // Group renderers across LODs by MeshGroupKey so each fine renderer
            // is paired with its deepest-LOD counterpart even when there are
            // many renderers per LOD level (e.g. multi-mesh LODGroups).
            var groups = new Dictionary<string, Renderer[]>();
            for (int li = 0; li < lods.Length; li++)
            {
                var rends = lods[li].renderers;
                if (rends == null) continue;
                foreach (var r in rends)
                {
                    if (r == null) continue;
                    string key = UvToolContext.ExtractGroupKey(r.name);
                    if (!groups.TryGetValue(key, out var arr))
                    {
                        arr = new Renderer[lods.Length];
                        groups[key] = arr;
                    }
                    arr[li] = r;
                }
            }

            if (groups.Count == 0)
                throw new System.InvalidOperationException(
                    "No renderers found under LODGroup.");

            var allRecords = new List<FaceProbeRecord>();
            int groupsProbed = 0, groupsSkipped = 0;
            foreach (var kv in groups)
            {
                var arr = kv.Value;
                if (arr[deepestIdx] == null) { groupsSkipped++; continue; }
                var deepMesh = arr[deepestIdx].GetComponent<MeshFilter>()?.sharedMesh;
                if (deepMesh == null) { groupsSkipped++; continue; }

                var deepFaces  = BuildFaceData(deepMesh, arr[deepestIdx].transform);
                var deepShells = ExtractShells(deepFaces, deepMesh.triangles, deepMesh.vertexCount);
                bool any = false;
                for (int li = 0; li < deepestIdx; li++)
                {
                    if (arr[li] == null) continue;
                    var fineMesh = arr[li].GetComponent<MeshFilter>()?.sharedMesh;
                    if (fineMesh == null) continue;
                    var fineFaces = BuildFaceData(fineMesh, arr[li].transform);
                    ProbeFineAgainstDeep(kv.Key, li, fineFaces, deepFaces, deepShells, allRecords);
                    any = true;
                }
                if (any) groupsProbed++;
            }

            string outPath = WriteReport(lg.name, allRecords);
            LogSummary(lg.name, allRecords, groupsProbed, groupsSkipped, deepestIdx);
            return outPath;
        }

        struct FaceData
        {
            public Vector3 centroid;   // world-space
            public Vector3 normal;     // world-space, unit length
            public float   area;       // world-space triangle area
        }

        struct ShellData
        {
            public Vector3 centroid;       // area-weighted world-space
            public Vector3 dominantNormal; // area-weighted world-space, unit length
            public float   totalArea;
            public int     faceCount;
        }

        struct FaceProbeRecord
        {
            public string groupKey;
            public int    lodIndex;             // fine LOD level (0 = LOD0)
            public int    faceIndex;            // index inside fine LOD's tris[]
            // Face-level (v1): nearest deepest-LOD triangle
            public int    parentFaceIndex;
            public float  faceCentroidDistance;
            public float  faceAngleDeg;
            public float  faceAreaRatio;
            // Shell-level (v2): best-angle match among K-nearest deepest-LOD shells
            public int    parentShellIndex;
            public float  shellCentroidDistance;
            public float  shellAngleDeg;
            public float  shellAreaRatio;
            public float  fineArea;
        }

        /// <summary>
        /// Read mesh.vertices once, transform to world space, derive per-tri
        /// centroid/normal/area. Returns a flat array indexed by tri index.
        /// </summary>
        static FaceData[] BuildFaceData(Mesh mesh, Transform xform)
        {
            var localVerts = mesh.vertices;
            var verts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                verts[i] = xform.TransformPoint(localVerts[i]);
            var tris = mesh.triangles;
            int n = tris.Length / 3;
            var data = new FaceData[n];
            for (int f = 0; f < n; f++)
            {
                var a = verts[tris[f * 3]];
                var b = verts[tris[f * 3 + 1]];
                var c = verts[tris[f * 3 + 2]];
                data[f].centroid = (a + b + c) / 3f;
                var cross = Vector3.Cross(b - a, c - a);
                float mag = cross.magnitude;
                data[f].normal = mag > 1e-12f ? cross / mag : Vector3.up;
                data[f].area   = mag * 0.5f;
            }
            return data;
        }

        /// <summary>
        /// Extract "3D shells" from the deepest LOD: connected groups of faces
        /// whose adjacent-pair normal angle is below
        /// <see cref="kShellNormalThresholdDeg"/>. Adjacency = shared edge
        /// (two vertex indices in common). For each shell, compute area-weighted
        /// dominant normal and centroid + total area. The shell index for each
        /// face is returned in <paramref name="faceToShell"/>.
        /// </summary>
        static ShellData[] ExtractShells(FaceData[] faces, int[] tris, int vertexCount)
        {
            int n = faces.Length;
            if (n == 0) return new ShellData[0];

            // Build edge → list-of-faces map for adjacency.
            // Key = (minVi, maxVi) packed as long.
            var edgeFaces = new Dictionary<long, List<int>>(n * 3);
            void AddEdge(int va, int vb, int face)
            {
                long key = va < vb
                    ? ((long)va << 32) | (uint)vb
                    : ((long)vb << 32) | (uint)va;
                if (!edgeFaces.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    edgeFaces[key] = list;
                }
                list.Add(face);
            }
            for (int f = 0; f < n; f++)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                AddEdge(v0, v1, f); AddEdge(v1, v2, f); AddEdge(v2, v0, f);
            }

            float thresholdCos = Mathf.Cos(kShellNormalThresholdDeg * Mathf.Deg2Rad);

            // Union-find over faces by adjacency + normal compatibility.
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            foreach (var kv in edgeFaces)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        float d = Vector3.Dot(faces[list[i]].normal, faces[list[j]].normal);
                        if (d >= thresholdCos) Union(list[i], list[j]);
                    }
            }

            // Materialise shells.
            var shellOf = new int[n];
            var rootToShell = new Dictionary<int, int>();
            var accumNormal = new List<Vector3>();
            var accumCentroid = new List<Vector3>();
            var accumArea = new List<float>();
            var accumCount = new List<int>();
            for (int f = 0; f < n; f++)
            {
                int r = Find(f);
                if (!rootToShell.TryGetValue(r, out int si))
                {
                    si = accumNormal.Count;
                    rootToShell[r] = si;
                    accumNormal.Add(Vector3.zero);
                    accumCentroid.Add(Vector3.zero);
                    accumArea.Add(0f);
                    accumCount.Add(0);
                }
                shellOf[f] = si;
                float a = faces[f].area;
                accumNormal[si]  = accumNormal[si]  + faces[f].normal * a;
                accumCentroid[si] = accumCentroid[si] + faces[f].centroid * a;
                accumArea[si]    = accumArea[si]    + a;
                accumCount[si]   = accumCount[si]   + 1;
            }
            var shells = new ShellData[accumNormal.Count];
            for (int si = 0; si < shells.Length; si++)
            {
                float ta = accumArea[si];
                shells[si].totalArea = ta;
                shells[si].faceCount = accumCount[si];
                if (ta > 1e-12f)
                {
                    shells[si].centroid = accumCentroid[si] / ta;
                    var n2 = accumNormal[si] / ta;
                    float m = n2.magnitude;
                    shells[si].dominantNormal = m > 1e-12f ? n2 / m : Vector3.up;
                }
                else
                {
                    shells[si].dominantNormal = Vector3.up;
                }
            }
            return shells;
        }

        /// <summary>
        /// For each fine face: brute-force nearest deepest-LOD triangle (face-level
        /// reference), plus K-nearest deepest-LOD shells + best-angle pick
        /// (shell-level main signal). O(N×M) per LOD pair — fine for diag on
        /// &lt;30k face meshes.
        /// </summary>
        static void ProbeFineAgainstDeep(string groupKey, int lodIndex,
            FaceData[] fine, FaceData[] deep, ShellData[] shells,
            List<FaceProbeRecord> sink)
        {
            if (deep.Length == 0) return;
            // Reusable K-nearest buffer for shell search: (distSq, shellIndex).
            int K = Mathf.Min(kShellSearchK, shells.Length);
            var topShells = new (float dsq, int si)[K];

            for (int f = 0; f < fine.Length; f++)
            {
                Vector3 fc = fine[f].centroid;

                // (A) face-level: nearest single deepest-LOD triangle by centroid.
                int   bestFace = -1;
                float bestFaceDistSq = float.MaxValue;
                for (int g = 0; g < deep.Length; g++)
                {
                    float dsq = (fc - deep[g].centroid).sqrMagnitude;
                    if (dsq < bestFaceDistSq) { bestFaceDistSq = dsq; bestFace = g; }
                }

                // (B) shell-level: K nearest shells by centroid, then pick the
                //     one with the smallest angle to the fine face's normal.
                //     Initialize buffer with +∞.
                for (int k = 0; k < K; k++) topShells[k] = (float.MaxValue, -1);
                for (int si = 0; si < shells.Length; si++)
                {
                    float dsq = (fc - shells[si].centroid).sqrMagnitude;
                    // Insertion into sorted top-K (smallest distSq first).
                    if (dsq >= topShells[K - 1].dsq) continue;
                    int pos = K - 1;
                    while (pos > 0 && topShells[pos - 1].dsq > dsq)
                    {
                        topShells[pos] = topShells[pos - 1];
                        pos--;
                    }
                    topShells[pos] = (dsq, si);
                }
                int   bestShell = -1;
                float bestShellAngle = float.MaxValue;
                float bestShellDistSq = float.MaxValue;
                for (int k = 0; k < K; k++)
                {
                    int si = topShells[k].si;
                    if (si < 0) break;
                    float dot = Vector3.Dot(fine[f].normal, shells[si].dominantNormal);
                    if (dot >  1f) dot =  1f;
                    if (dot < -1f) dot = -1f;
                    float ang = Mathf.Acos(dot) * Mathf.Rad2Deg;
                    if (ang < bestShellAngle)
                    {
                        bestShellAngle  = ang;
                        bestShell       = si;
                        bestShellDistSq = topShells[k].dsq;
                    }
                }

                // Record both modes.
                float faceAngle, faceRatio;
                if (bestFace >= 0)
                {
                    float dot = Vector3.Dot(fine[f].normal, deep[bestFace].normal);
                    if (dot >  1f) dot =  1f;
                    if (dot < -1f) dot = -1f;
                    faceAngle = Mathf.Acos(dot) * Mathf.Rad2Deg;
                    faceRatio = deep[bestFace].area > 1e-12f
                        ? fine[f].area / deep[bestFace].area
                        : float.PositiveInfinity;
                }
                else { faceAngle = 0f; faceRatio = 0f; }

                float shellRatio = (bestShell >= 0 && shells[bestShell].totalArea > 1e-12f)
                    ? fine[f].area / shells[bestShell].totalArea
                    : float.PositiveInfinity;

                sink.Add(new FaceProbeRecord
                {
                    groupKey               = groupKey,
                    lodIndex               = lodIndex,
                    faceIndex              = f,
                    parentFaceIndex        = bestFace,
                    faceCentroidDistance   = Mathf.Sqrt(bestFaceDistSq),
                    faceAngleDeg           = faceAngle,
                    faceAreaRatio          = faceRatio,
                    parentShellIndex       = bestShell,
                    shellCentroidDistance  = Mathf.Sqrt(bestShellDistSq),
                    shellAngleDeg          = bestShellAngle == float.MaxValue ? 0f : bestShellAngle,
                    shellAreaRatio         = shellRatio,
                    fineArea               = fine[f].area,
                });
            }
        }

        static string WriteReport(string lgName, List<FaceProbeRecord> records)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Application.dataPath;
            string dir = Path.Combine(projectRoot, "BenchmarkReports");
            Directory.CreateDirectory(dir);
            string stamp = System.DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff",
                CultureInfo.InvariantCulture);
            string path = Path.Combine(dir,
                $"hierdiag_{stamp}_{Sanitize(lgName)}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("groupKey,lodIndex,faceIndex," +
                          "parentFaceIndex,faceCentroidDistance,faceAngleDeg,faceAreaRatio," +
                          "parentShellIndex,shellCentroidDistance,shellAngleDeg,shellAreaRatio," +
                          "fineArea");
            var inv = CultureInfo.InvariantCulture;
            foreach (var r in records)
            {
                sb.Append(CsvField(r.groupKey)).Append(',');
                sb.Append(r.lodIndex.ToString(inv)).Append(',');
                sb.Append(r.faceIndex.ToString(inv)).Append(',');
                sb.Append(r.parentFaceIndex.ToString(inv)).Append(',');
                sb.Append(r.faceCentroidDistance.ToString("R", inv)).Append(',');
                sb.Append(r.faceAngleDeg.ToString("R", inv)).Append(',');
                sb.Append(r.faceAreaRatio.ToString("R", inv)).Append(',');
                sb.Append(r.parentShellIndex.ToString(inv)).Append(',');
                sb.Append(r.shellCentroidDistance.ToString("R", inv)).Append(',');
                sb.Append(r.shellAngleDeg.ToString("R", inv)).Append(',');
                sb.Append(r.shellAreaRatio.ToString("R", inv)).Append(',');
                sb.Append(r.fineArea.ToString("R", inv));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        static void LogSummary(string lgName, List<FaceProbeRecord> records,
            int groupsProbed, int groupsSkipped, int deepestIdx)
        {
            if (records.Count == 0)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierDiag] {lgName}: 0 face records " +
                    $"(groups probed={groupsProbed}, skipped={groupsSkipped}).");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[HierDiag] '{lgName}' — {records.Count} fine faces " +
                $"probed against deepest LOD (idx={deepestIdx}). " +
                $"groups: {groupsProbed} probed, {groupsSkipped} skipped.");

            // Mode A: face-level (naive v1 baseline)
            sb.AppendLine("  FACE-LEVEL (v1: nearest deepest triangle):");
            EmitCoverage(sb, records, r => r.faceAngleDeg);
            EmitRatioStats(sb, records, r => r.faceAreaRatio, "    ");

            // Mode B: shell-level (v2 main signal — matches what HierarchicalRepack would do)
            sb.AppendLine("  SHELL-LEVEL (v2: K=10 nearest shells, best angle):");
            EmitCoverage(sb, records, r => r.shellAngleDeg);
            EmitRatioStats(sb, records, r => r.shellAreaRatio, "    ");

            // Per-LOD shell-level breakdown
            var byLod = records.GroupBy(r => r.lodIndex).OrderBy(g => g.Key);
            foreach (var g in byLod)
            {
                int n = g.Count();
                float mAng = g.Average(r => r.shellAngleDeg);
                int s30 = g.Count(r => r.shellAngleDeg < 30f);
                int s60 = g.Count(r => r.shellAngleDeg < 60f);
                sb.AppendLine($"  LOD{g.Key}: {n,6} faces  shell-θ mean={mAng,5:F1}°  " +
                    $"θ<30°: {100f * s30 / n:F1}%  θ<60°: {100f * s60 / n:F1}%");
            }

            UvtLog.Info(UvtLog.Category.Benchmark, sb.ToString());
        }

        static void EmitCoverage(StringBuilder sb, List<FaceProbeRecord> records,
            System.Func<FaceProbeRecord, float> getAngle)
        {
            int total = records.Count;
            foreach (var theta in kThetaSamples)
            {
                int within = 0;
                foreach (var r in records) if (getAngle(r) < theta) within++;
                float pct = 100f * within / total;
                sb.AppendLine($"    θ <{theta,5:F1}°: {within,7} / {total} " +
                              $"({pct,5:F1}%)  retained-area ≥ {Mathf.Cos(theta * Mathf.Deg2Rad) * 100f:F0}%");
            }
        }

        static void EmitRatioStats(StringBuilder sb, List<FaceProbeRecord> records,
            System.Func<FaceProbeRecord, float> getRatio, string indent)
        {
            var ratios = new List<float>(records.Count);
            foreach (var r in records)
            {
                float v = getRatio(r);
                if (!float.IsInfinity(v) && !float.IsNaN(v)) ratios.Add(v);
            }
            if (ratios.Count == 0) return;
            ratios.Sort();
            float Pct(double p)
            {
                int i = (int)System.Math.Round(p * (ratios.Count - 1));
                if (i < 0) i = 0;
                if (i >= ratios.Count) i = ratios.Count - 1;
                return ratios[i];
            }
            int over = 0; foreach (var x in ratios) if (x > 1.0f) over++;
            sb.AppendLine($"{indent}area ratio (fine/parent): " +
                $"p50={Pct(0.50):F3}  p90={Pct(0.90):F3}  p99={Pct(0.99):F3}  " +
                $"max={ratios[ratios.Count - 1]:F3}");
            sb.AppendLine($"{indent}  fine LARGER than parent (ratio>1): " +
                $"{over}/{ratios.Count} ({100f * over / ratios.Count:F1}%) → promotion candidates");
        }

        static string CsvField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
