// BuildValidator.cs — Pre-save validation for ModelBuilder's Build Pipeline.
// Groups issues into NullRefs / Materials / Uv2Readable / Topology; callers
// decide which groups block the save. NullRefs and Materials are fatal —
// FBX export cannot succeed without them. Uv2Readable and Topology are
// surfaced as warnings.

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    internal static class BuildValidator
    {
        internal enum IssueGroup
        {
            NullRefs,
            Materials,
            Uv2Readable,
            Topology
        }

        internal class Issue
        {
            public IssueGroup group;
            public string meshName;
            public string detail;
            public GameObject target;
        }

        internal static bool IsBlocker(IssueGroup group)
            => group == IssueGroup.NullRefs || group == IssueGroup.Materials;

        internal static List<Issue> Run(UvToolContext ctx)
        {
            var issues = new List<Issue>();
            if (ctx == null || ctx.MeshEntries == null) return issues;

            foreach (var e in ctx.MeshEntries)
            {
                if (!e.include) continue;
                string label = e.renderer != null ? e.renderer.name : (e.fbxMesh != null ? e.fbxMesh.name : "<unknown>");
                var target = e.renderer != null ? e.renderer.gameObject : null;

                CheckNullRefs(e, label, target, issues);
                CheckMaterials(e, label, target, issues);
                CheckUv2Readable(e, label, target, issues);
                CheckTopology(e, label, target, issues);
            }

            return issues;
        }

        static void CheckNullRefs(MeshEntry e, string label, GameObject target, List<Issue> issues)
        {
            if (e.renderer == null)
            {
                issues.Add(new Issue { group = IssueGroup.NullRefs, meshName = label, detail = "missing renderer", target = target });
                return;
            }
            if (e.meshFilter == null || e.meshFilter.sharedMesh == null)
                issues.Add(new Issue { group = IssueGroup.NullRefs, meshName = label, detail = "null sharedMesh", target = target });
            if (e.fbxMesh == null)
                issues.Add(new Issue { group = IssueGroup.NullRefs, meshName = label, detail = "no FBX source", target = target });
        }

        static void CheckMaterials(MeshEntry e, string label, GameObject target, List<Issue> issues)
        {
            if (e.renderer == null) return;
            var mats = e.renderer.sharedMaterials;
            if (mats == null || mats.Length == 0)
            {
                issues.Add(new Issue { group = IssueGroup.Materials, meshName = label, detail = "no materials", target = target });
                return;
            }
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null)
                    issues.Add(new Issue { group = IssueGroup.Materials, meshName = label, detail = $"null material in slot {i}", target = target });
                else if (mats[i].shader == null)
                    issues.Add(new Issue { group = IssueGroup.Materials, meshName = label, detail = $"null shader in '{mats[i].name}'", target = target });
            }
        }

        static void CheckUv2Readable(MeshEntry e, string label, GameObject target, List<Issue> issues)
        {
            Mesh mesh = e.originalMesh ?? e.fbxMesh;
            if (mesh == null) return;
            if (!mesh.isReadable)
                issues.Add(new Issue { group = IssueGroup.Uv2Readable, meshName = label, detail = "mesh not readable", target = target });
            var uv2 = new List<Vector2>();
            mesh.GetUVs(1, uv2);
            if (uv2.Count == 0)
                issues.Add(new Issue { group = IssueGroup.Uv2Readable, meshName = label, detail = "missing UV2", target = target });
        }

        static void CheckTopology(MeshEntry e, string label, GameObject target, List<Issue> issues)
        {
            Mesh mesh = e.originalMesh ?? e.fbxMesh;
            if (mesh == null || !mesh.isReadable) return;
            int deg = MeshHygieneUtility.CountDegenerateTriangles(mesh);
            if (deg > 0)
                issues.Add(new Issue { group = IssueGroup.Topology, meshName = label, detail = $"{deg} degenerate tris", target = target });
            int unused = MeshHygieneUtility.CountUnusedVertices(mesh);
            if (unused > 0)
                issues.Add(new Issue { group = IssueGroup.Topology, meshName = label, detail = $"{unused} unused verts", target = target });
            var seamVerts = Uv0Analyzer.GetFalseSeamVertices(mesh);
            if (seamVerts != null && seamVerts.Count > 0)
                issues.Add(new Issue { group = IssueGroup.Topology, meshName = label, detail = $"{seamVerts.Count} false-seam verts", target = target });
        }
    }
}
