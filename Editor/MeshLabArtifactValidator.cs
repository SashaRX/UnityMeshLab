// MeshLabArtifactValidator.cs — Detect symptoms of bad external MeshLab settings
// in imported meshes. Flags: collapsed UV seams (Merge Close Vertices),
// recalculated normals (UseExistingNormals OFF), broken UV1 parameterization
// (Flat Plane / wiped UV1). See Documentation~/EXPERIMENTS.md, section
// "External MeshLab — Settings to Avoid Artifacts".

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    public static class MeshLabArtifactValidator
    {
        const float FlatNormalDot          = 0.9999f; // cos(0.81°) — vertex normal matches face normal
        const float FlatShadedTriThreshold = 0.90f;   // ≥90% triangles flat-shaded → suspicious
        const float DegenerateAreaEpsilon  = 1e-12f;
        const float DegenerateUv1Threshold = 0.05f;   // ≥5% degenerate UV1 tris → suspicious
        const float Uv1OutsideUnitThreshold = 0.05f;  // ≥5% UV1 verts outside [0..1] → suspicious
        const int   MinVertsForSeamCheck   = 16;      // skip trivial meshes

        public class Report
        {
            public string meshName;
            public int    vertexCount;
            public int    triangleCount;
            public int    submeshCount;

            // Mesh has Read/Write disabled — CPU buffers are inaccessible, so
            // no analysis could run. Reported instead of throwing.
            public bool   notReadable;

            // Symptom 1: Merge Close Vertices ate UV seams
            public int    uniquePositionCount;
            public float  splitVertexRatio;     // (vertexCount - uniquePositionCount) / vertexCount
            public bool   seamsLikelyMerged;

            // Symptom 2: UseExistingNormals OFF → RecalculateNormals without smoothing
            public bool   hasNormals;
            public int    flatShadedTriCount;
            public float  flatShadedRatio;
            public bool   normalsLikelyRecalculated;

            // Symptom 3: Bad UV1 parameterization
            public bool   hasUv1;
            public int    degenerateUv1TriCount;
            public float  degenerateUv1Ratio;
            public bool   uv1AllZero;
            public int    uv1OutsideUnitCount;
            public float  uv1OutsideUnitRatio;

            public bool HasIssues =>
                seamsLikelyMerged
                || normalsLikelyRecalculated
                || (hasUv1 && (uv1AllZero
                               || degenerateUv1Ratio >= DegenerateUv1Threshold
                               || uv1OutsideUnitRatio >= Uv1OutsideUnitThreshold));

            public string Summary =>
                $"'{meshName}' verts={vertexCount} tris={triangleCount} sub={submeshCount} | " +
                $"uniquePos={uniquePositionCount} splitRatio={splitVertexRatio:P1} | " +
                (hasNormals
                    ? $"flatTri={flatShadedTriCount}/{triangleCount} ({flatShadedRatio:P1}) | "
                    : "noNormals | ") +
                (hasUv1
                    ? $"uv1 degen={degenerateUv1TriCount} ({degenerateUv1Ratio:P1}) " +
                      $"oob={uv1OutsideUnitCount} ({uv1OutsideUnitRatio:P1}) zero={uv1AllZero}"
                    : "noUv1");
        }

        /// <summary>
        /// Analyze a mesh for symptoms of bad external MeshLab export settings.
        /// Pure read-only inspection — does not mutate the mesh.
        /// </summary>
        public static Report Validate(Mesh mesh)
        {
            if (mesh == null) return null;

            var report = new Report
            {
                meshName     = mesh.name,
                vertexCount  = mesh.vertexCount,
                submeshCount = mesh.subMeshCount,
            };

            // Read/Write disabled meshes throw on any CPU buffer access
            // (mesh.vertices / .normals / .triangles / GetUVs). Common on
            // imported FBX assets — report it instead of aborting the run.
            if (!mesh.isReadable)
            {
                report.notReadable = true;
                return report;
            }

            var positions = mesh.vertices;
            var normals   = mesh.normals;
            var triangles = mesh.triangles;
            report.triangleCount = triangles.Length / 3;

            var uv1List = new List<Vector2>();
            mesh.GetUVs(1, uv1List);
            report.hasUv1 = uv1List.Count == report.vertexCount && report.vertexCount > 0;

            CheckCollapsedSeams(report, positions);
            CheckRecalculatedNormals(report, positions, normals, triangles);
            if (report.hasUv1) CheckUv1Parameterization(report, uv1List, triangles);

            return report;
        }

        /// <summary>
        /// Validate and emit log lines via UvtLog. Info on clean, Warn on issues.
        /// </summary>
        public static Report ValidateAndLog(Mesh mesh, string contextLabel = null)
        {
            var report = Validate(mesh);
            if (report == null) return null;
            string ctx = string.IsNullOrEmpty(contextLabel) ? "" : $"[{contextLabel}] ";
            if (report.notReadable)
            {
                UvtLog.Warn($"[MeshLabValidator] {ctx}'{report.meshName}': mesh is not readable " +
                            "(enable Read/Write in the model importer to validate).");
            }
            else if (report.HasIssues)
            {
                UvtLog.Warn($"[MeshLabValidator] {ctx}{report.Summary}");
                if (report.seamsLikelyMerged)
                    UvtLog.Warn($"[MeshLabValidator] {ctx}'{report.meshName}': UV seams likely merged " +
                                "(MeshLab 'Merge Close Vertices' or 'Remove Duplicate Vertices').");
                if (report.normalsLikelyRecalculated)
                    UvtLog.Warn($"[MeshLabValidator] {ctx}'{report.meshName}': normals likely recalculated " +
                                "(MeshLab FBX export 'UseExistingNormals' = OFF).");
                if (report.hasUv1 && report.uv1AllZero)
                    UvtLog.Warn($"[MeshLabValidator] {ctx}'{report.meshName}': UV1 is all zero — " +
                                "MeshLab parameterization wiped or never written.");
                if (report.hasUv1 && report.degenerateUv1Ratio >= DegenerateUv1Threshold)
                    UvtLog.Warn($"[MeshLabValidator] {ctx}'{report.meshName}': UV1 has " +
                                $"{report.degenerateUv1Ratio:P1} degenerate triangles — likely 'Flat Plane' parameterization.");
                if (report.hasUv1 && report.uv1OutsideUnitRatio >= Uv1OutsideUnitThreshold)
                    UvtLog.Warn($"[MeshLabValidator] {ctx}'{report.meshName}': " +
                                $"{report.uv1OutsideUnitRatio:P1} of UV1 verts outside [0..1] — parameterization not normalized.");
            }
            else
            {
                UvtLog.Info($"[MeshLabValidator] {ctx}{report.Summary} — clean.");
            }
            return report;
        }

        // ── Symptom 1 ─────────────────────────────────────────────────────────

        static void CheckCollapsedSeams(Report report, Vector3[] positions)
        {
            if (positions == null || positions.Length == 0) return;

            var unique = new HashSet<(int, int, int)>();
            for (int i = 0; i < positions.Length; i++)
                unique.Add(QuantizePos(positions[i]));

            report.uniquePositionCount = unique.Count;
            report.splitVertexRatio = positions.Length > 0
                ? (positions.Length - unique.Count) / (float)positions.Length
                : 0f;

            // Suspicious: no split vertices at all on a non-trivial mesh with
            // multiple submeshes or UV1 — almost always means seams got merged.
            bool nonTrivial = positions.Length >= MinVertsForSeamCheck;
            bool hasSeamSignal = report.submeshCount > 1 || report.hasUv1;
            report.seamsLikelyMerged = nonTrivial
                                    && hasSeamSignal
                                    && report.splitVertexRatio < 1e-4f;
        }

        // ── Symptom 2 ─────────────────────────────────────────────────────────

        static void CheckRecalculatedNormals(Report report, Vector3[] positions, Vector3[] normals, int[] triangles)
        {
            report.hasNormals = normals != null && normals.Length == positions.Length && normals.Length > 0;
            if (!report.hasNormals || triangles.Length < 3) return;

            int triCount = triangles.Length / 3;
            int flatCount = 0;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length) continue;

                Vector3 faceN = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
                if (faceN.sqrMagnitude < DegenerateAreaEpsilon) continue;
                faceN.Normalize();

                Vector3 n0 = normals[i0];
                Vector3 n1 = normals[i1];
                Vector3 n2 = normals[i2];
                if (n0.sqrMagnitude < 0.5f || n1.sqrMagnitude < 0.5f || n2.sqrMagnitude < 0.5f) continue;

                float d0 = Vector3.Dot(n0, faceN);
                float d1 = Vector3.Dot(n1, faceN);
                float d2 = Vector3.Dot(n2, faceN);
                if (d0 >= FlatNormalDot && d1 >= FlatNormalDot && d2 >= FlatNormalDot)
                    flatCount++;
            }

            report.flatShadedTriCount = flatCount;
            report.flatShadedRatio = triCount > 0 ? flatCount / (float)triCount : 0f;
            report.normalsLikelyRecalculated = triCount >= MinVertsForSeamCheck
                                            && report.flatShadedRatio >= FlatShadedTriThreshold;
        }

        // ── Symptom 3 ─────────────────────────────────────────────────────────

        static void CheckUv1Parameterization(Report report, List<Vector2> uv1, int[] triangles)
        {
            int triCount = triangles.Length / 3;
            int degenerate = 0;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= uv1.Count || i1 >= uv1.Count || i2 >= uv1.Count) continue;

                Vector2 a = uv1[i1] - uv1[i0];
                Vector2 b = uv1[i2] - uv1[i0];
                float cross = a.x * b.y - a.y * b.x;
                if (cross * cross < DegenerateAreaEpsilon) degenerate++;
            }
            report.degenerateUv1TriCount = degenerate;
            report.degenerateUv1Ratio = triCount > 0 ? degenerate / (float)triCount : 0f;

            int oob = 0;
            bool allZero = true;
            for (int i = 0; i < uv1.Count; i++)
            {
                Vector2 uv = uv1[i];
                if (uv.sqrMagnitude > 1e-12f) allZero = false;
                if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) oob++;
            }
            report.uv1AllZero = allZero;
            report.uv1OutsideUnitCount = oob;
            report.uv1OutsideUnitRatio = uv1.Count > 0 ? oob / (float)uv1.Count : 0f;
        }

        // ── Utility ───────────────────────────────────────────────────────────

        static (int, int, int) QuantizePos(Vector3 p) =>
            (Mathf.RoundToInt(p.x * 100000f),
             Mathf.RoundToInt(p.y * 100000f),
             Mathf.RoundToInt(p.z * 100000f));

        // ── Menu integration ──────────────────────────────────────────────────

        const string MenuPath = "Tools/Mesh Lab/Validators/Check Imported MeshLab Artifacts";

        [MenuItem(MenuPath, true)]
        static bool ValidateSelectionMenuValidate() =>
            CollectMeshes(Selection.objects).Count > 0;

        [MenuItem(MenuPath, false, 200)]
        static void ValidateSelectionMenu()
        {
            var meshes = CollectMeshes(Selection.objects);
            if (meshes.Count == 0)
            {
                UvtLog.Warn("[MeshLabValidator] Selection contains no meshes.");
                return;
            }

            int issueCount = 0;
            foreach (var (mesh, label) in meshes)
            {
                var r = ValidateAndLog(mesh, label);
                if (r != null && r.HasIssues) issueCount++;
            }
            UvtLog.Info($"[MeshLabValidator] Checked {meshes.Count} mesh(es), {issueCount} with issues.");
        }

        static List<(Mesh mesh, string label)> CollectMeshes(Object[] objects)
        {
            var result = new List<(Mesh, string)>();
            var seen = new HashSet<Mesh>();
            if (objects == null) return result;

            foreach (var obj in objects)
            {
                if (obj == null) continue;

                if (obj is Mesh m && m != null && seen.Add(m))
                {
                    result.Add((m, AssetDatabase.GetAssetPath(m)));
                    continue;
                }

                if (obj is GameObject go && go != null)
                {
                    foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                        if (mf != null && mf.sharedMesh != null && seen.Add(mf.sharedMesh))
                            result.Add((mf.sharedMesh, mf.gameObject.name));
                    foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (smr != null && smr.sharedMesh != null && seen.Add(smr.sharedMesh))
                            result.Add((smr.sharedMesh, smr.gameObject.name));
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path))
                {
                    foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (sub is Mesh subMesh && subMesh != null && seen.Add(subMesh))
                            result.Add((subMesh, $"{System.IO.Path.GetFileName(path)}::{subMesh.name}"));
                }
            }

            return result;
        }
    }
}
