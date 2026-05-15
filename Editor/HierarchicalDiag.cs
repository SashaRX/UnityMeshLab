// HierarchicalDiag.cs — Editor-only diagnostic probe for the inverse-hierarchical
// UV2 transfer concept. Read-only — does NOT modify any mesh, asset, or sidecar.
//
// For every face on every non-deepest LOD of the active LODGroup it finds the
// nearest face on the deepest LOD by 3D centroid distance, then records the
// angle between normals and the area ratio. The summary reports coverage at a
// fixed set of θ thresholds plus area-ratio percentiles so the operator can
// decide — before writing HierarchicalRepack.cs / InverseTransfer.cs — whether
// the simple `containment + normal-sign` correspondence is enough on this
// asset, or whether the design needs to escalate to multi-layer SDF lookup.
//
// See Documentation~/EXPERIMENTS.md for the architectural discussion.

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

                var deepFaces = BuildFaceData(deepMesh, arr[deepestIdx].transform);
                bool any = false;
                for (int li = 0; li < deepestIdx; li++)
                {
                    if (arr[li] == null) continue;
                    var fineMesh = arr[li].GetComponent<MeshFilter>()?.sharedMesh;
                    if (fineMesh == null) continue;
                    var fineFaces = BuildFaceData(fineMesh, arr[li].transform);
                    ProbeFineAgainstDeep(kv.Key, li, fineFaces, deepFaces, allRecords);
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

        struct FaceProbeRecord
        {
            public string groupKey;
            public int    lodIndex;        // fine LOD level (0 = LOD0)
            public int    faceIndex;       // index inside fine LOD's tris[]
            public int    parentFaceIndex; // index inside deepest LOD's tris[]
            public float  centroidDistance;
            public float  angleDeg;        // angle(fine.normal, parent.normal)
            public float  areaRatio;       // fine.area / parent.area
            public float  fineArea;
            public float  parentArea;
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
        /// O(N×M) nearest-face match by 3D centroid. Acceptable for diag — typical
        /// LODGroup meshes have &lt; 10k faces so this is sub-second. Replace
        /// with a BVH if profile shows otherwise.
        /// </summary>
        static void ProbeFineAgainstDeep(string groupKey, int lodIndex,
            FaceData[] fine, FaceData[] deep, List<FaceProbeRecord> sink)
        {
            if (deep.Length == 0) return;
            for (int f = 0; f < fine.Length; f++)
            {
                int   best = -1;
                float bestDistSq = float.MaxValue;
                Vector3 fc = fine[f].centroid;
                for (int g = 0; g < deep.Length; g++)
                {
                    float dsq = (fc - deep[g].centroid).sqrMagnitude;
                    if (dsq < bestDistSq)
                    {
                        bestDistSq = dsq;
                        best = g;
                    }
                }
                if (best < 0) continue;
                float dot = Vector3.Dot(fine[f].normal, deep[best].normal);
                if (dot >  1f) dot =  1f;
                if (dot < -1f) dot = -1f;
                float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;
                float ratio = deep[best].area > 1e-12f
                    ? fine[f].area / deep[best].area
                    : float.PositiveInfinity;
                sink.Add(new FaceProbeRecord
                {
                    groupKey         = groupKey,
                    lodIndex         = lodIndex,
                    faceIndex        = f,
                    parentFaceIndex  = best,
                    centroidDistance = Mathf.Sqrt(bestDistSq),
                    angleDeg         = angle,
                    areaRatio        = ratio,
                    fineArea         = fine[f].area,
                    parentArea       = deep[best].area,
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
            sb.AppendLine("groupKey,lodIndex,faceIndex,parentFaceIndex," +
                          "centroidDistance,angleDeg,areaRatio,fineArea,parentArea");
            var inv = CultureInfo.InvariantCulture;
            foreach (var r in records)
            {
                sb.Append(CsvField(r.groupKey)).Append(',');
                sb.Append(r.lodIndex.ToString(inv)).Append(',');
                sb.Append(r.faceIndex.ToString(inv)).Append(',');
                sb.Append(r.parentFaceIndex.ToString(inv)).Append(',');
                sb.Append(r.centroidDistance.ToString("R", inv)).Append(',');
                sb.Append(r.angleDeg.ToString("R", inv)).Append(',');
                sb.Append(r.areaRatio.ToString("R", inv)).Append(',');
                sb.Append(r.fineArea.ToString("R", inv)).Append(',');
                sb.Append(r.parentArea.ToString("R", inv));
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

            // Coverage at each θ — orthographic projection retains ~cos(θ)
            // of the original area, so this is the "fraction of fine faces
            // that project onto their nearest deepest face without severe
            // foreshortening".
            sb.AppendLine("  Containment coverage by θ (fine.normal vs parent.normal):");
            foreach (var theta in kThetaSamples)
            {
                int within = 0;
                foreach (var r in records) if (r.angleDeg < theta) within++;
                float pct = 100f * within / records.Count;
                sb.AppendLine($"    θ <{theta,5:F1}°: {within,7} / {records.Count} " +
                              $"({pct,5:F1}%)  retained-area ≥ {Mathf.Cos(theta * Mathf.Deg2Rad) * 100f:F0}%");
            }

            // Area-ratio percentiles. fine/parent — a value < 1 means the
            // fine face is a subset of its parent (good). > 1 means the
            // fine face is LARGER than its parent — projection will stretch
            // or wrap; that face is a candidate for promotion to a top-level
            // atlas slot in the real pipeline.
            var ratios = new List<float>(records.Count);
            foreach (var r in records)
                if (!float.IsInfinity(r.areaRatio)) ratios.Add(r.areaRatio);
            ratios.Sort();
            if (ratios.Count > 0)
            {
                float Pct(double p)
                {
                    int i = (int)System.Math.Round(p * (ratios.Count - 1));
                    if (i < 0) i = 0;
                    if (i >= ratios.Count) i = ratios.Count - 1;
                    return ratios[i];
                }
                int over = 0; foreach (var x in ratios) if (x > 1.0f) over++;
                sb.AppendLine($"  Area ratio (fine/parent): " +
                    $"p50={Pct(0.50):F3}  p90={Pct(0.90):F3}  p99={Pct(0.99):F3}  " +
                    $"max={ratios[ratios.Count - 1]:F3}");
                sb.AppendLine($"    fine LARGER than parent (ratio>1): {over}/{ratios.Count} " +
                    $"({100f * over / ratios.Count:F1}%) → promotion candidates");
            }

            // Per-LOD breakdown — angle distribution may differ between LOD1
            // (almost-LOD0) and the deepest non-base LOD.
            var byLod = records.GroupBy(r => r.lodIndex).OrderBy(g => g.Key);
            foreach (var g in byLod)
            {
                int n = g.Count();
                float meanAngle = g.Average(r => r.angleDeg);
                int strict = g.Count(r => r.angleDeg < 30f);
                int loose  = g.Count(r => r.angleDeg < 60f);
                sb.AppendLine($"  LOD{g.Key}: {n,6} faces  mean θ={meanAngle,5:F1}°  " +
                    $"θ<30°: {100f * strict / n:F1}%  θ<60°: {100f * loose / n:F1}%");
            }

            UvtLog.Info(UvtLog.Category.Benchmark, sb.ToString());
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
