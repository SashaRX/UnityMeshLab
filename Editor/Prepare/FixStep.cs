// FixStep.cs — the Fix step, after Validate: the simple errors a model fails Validate on are
// fixed in the model's own file, so the model is checked again fixed (Validate's errors wait for
// the reimport, as for Preset and Materials). Preview lists each fix as a change; Apply writes
// them into the FBX in one save (FbxExport.WriteRepairs) and reimports it. A fix is one the file
// can take without a decision: an error past its limit is left for Validate to report.
//
// The fixes:
// - a lightmap UV (UV1) a little outside [0,1] is clamped onto the unit square's edge, up to the
//   recipe's limit; farther out the faces would be squashed there, and it needs a repack. Only
//   the changed corners of that channel are written;
// - a collision mesh's faces without area (MeshValidation's degenerate faces: no area for
//   their size, a height at most a millionth of their longest edge) are removed: a
//   collider needs no face that covers nothing, and the mesh holds positions and faces alone,
//   so it is written whole. A render mesh's are left for Cleanup or the DCC: written whole it
//   would lose its polygons (quads) and the channels a triangle list does not carry.
//
// It changes the model's file, whichever target the run prepares: a model asset, or a scene
// object made from one (every object made from the model gets the fix; the file's change has
// no Undo). A prefab asset is skipped: its contents are open while the model would reimport.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>The fixes the Fix step makes.</summary>
    [Flags]
    internal enum ModelFixes
    {
        None = 0,
        /// <summary>A lightmap UV a little outside [0,1] is clamped into it.</summary>
        LightmapUv = 1,
        /// <summary>A collision mesh's faces without area are removed.</summary>
        CollisionFaces = 2,
        All = LightmapUv | CollisionFaces,
    }

    internal sealed class FixStep : IImporterStep
    {
        internal const string StepName = "Fix";
        const string Check = "Lightmap UV";
        // Validate's name for the finding: the runner pairs a fix with the error it cures by it.
        const string FacesCheck = "Degenerate faces";

        /// <summary>How far outside [0,1] a lightmap UV is clamped by default, in UV units.</summary>
        internal const float DefaultMaxClamp = .02f;

        readonly float maxClamp;
        readonly ModelFixes fixes;
        readonly Func<string, IReadOnlyList<MeshEntry>, IReadOnlyList<Mesh>, FbxWriteOutcome> write;

        /// <param name="maxClamp">The farthest outside [0,1] a lightmap UV is clamped from; farther is left as it is.</param>
        /// <param name="fixes">The fixes it makes; every one by default.</param>
        /// <param name="write">Writes the entries' UV1 and the meshes' geometry (each the FBX mesh of its name) into the FBX at a path; FbxExport.WriteRepairs when null.</param>
        internal FixStep(float maxClamp = DefaultMaxClamp, ModelFixes fixes = ModelFixes.All,
            Func<string, IReadOnlyList<MeshEntry>, IReadOnlyList<Mesh>, FbxWriteOutcome> write = null)
        {
            this.maxClamp = Mathf.Max(0f, maxClamp);
            this.fixes = fixes;
            this.write = write ?? FbxExport.WriteRepairs;
        }

        public string Name => StepName;

        public string Purpose => "Simple errors are fixed in the model's FBX: a lightmap UV a little outside [0,1] is clamped into it, "
            + "and a collision mesh's faces without area are removed.";

        public string SkipReason(PrepareContext context)
        {
            if (context.Target.Kind == PrepareTargetKind.PrefabAsset) return "the model's file is fixed from the model, or a scene object made from it";
            string model = context.Target.ModelPath;
            if (model == null) return "it is not made from a model: there is no file to fix";
            if (!model.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return "only an FBX file can be fixed";
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            return null;
#else
            return "the FBX Exporter package is not installed: the file cannot be written";
#endif
        }

        public void Plan(PrepareContext context, PrepareReport report) => Run(context, report, false);

        public void Apply(PrepareContext context, PrepareReport report) => Run(context, report, true);

        /// <summary>
        /// The lightmap UVs outside [0,1] past rounding, and the farthest any lies outside
        /// (<see cref="MeshValidation.LightmapTolerance"/>, the bound Validate reports from).
        /// </summary>
        internal static (int outside, float overshoot) Outside(IReadOnlyList<Vector2> uv)
        {
            int outside = 0;
            float overshoot = 0f;
            foreach (var value in uv)
            {
                float distance = XatlasRepack.UnitOvershoot(value);
                if (distance <= MeshValidation.LightmapTolerance) continue;
                ++outside;
                overshoot = Mathf.Max(overshoot, distance);
            }
            return (outside, overshoot);
        }

        void Run(PrepareContext context, PrepareReport report, bool apply)
        {
            string model = context.Target.ModelPath;
            var copies = new List<Mesh>();
            var entries = new List<MeshEntry>();
            var reshaped = new List<Mesh>();
            var changes = new List<(string subject, string check, string message)>();
            try
            {
                // Validate's rule: a mesh no node renders, used by a collision node or a MeshCollider.
                var collisionOnly = ModelValidation.CollisionOnlyMeshes(AssetDatabase.LoadAssetAtPath<GameObject>(model));
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().OrderBy(m => m.name, StringComparer.Ordinal))
                {
                    bool collision = collisionOnly.Contains(mesh) || MeshNaming.IsCollision(mesh.name);
                    if (collision ? (fixes & ModelFixes.CollisionFaces) == 0
                        : (fixes & ModelFixes.LightmapUv) == 0 || !mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) continue;
                    var working = MeshAccess.ReadableCopy(mesh);
                    working.name = mesh.name;
                    working.hideFlags = HideFlags.HideAndDontSave;
                    copies.Add(working);
                    if (collision) PlanFaces(model, working, reshaped, changes, report);
                    else PlanClamp(model, mesh, working, entries, changes, report);
                }
                if (changes.Count == 0)
                {
                    if (report.Lines.Count == 0) report.Add(PrepareLevel.Unchanged, $"'{model}'", "Fix", "nothing to fix");
                    return;
                }
                var outcome = apply ? write(model, entries, reshaped) : FbxWriteOutcome.Written;
                if (outcome == FbxWriteOutcome.NotWritten)
                {
                    report.Add(PrepareLevel.Warning, $"'{model}'", "Fix", "the FBX was not written (see the Console); the model is as it was");
                    return;
                }
                if (outcome == FbxWriteOutcome.Unchanged)
                {
                    report.Add(PrepareLevel.Unchanged, $"'{model}'", "Fix", "the FBX already holds the fixes; it is left as it is");
                    return;
                }
                // The file is changed either way: the fixes are in it.
                foreach (var (subject, check, message) in changes) report.Add(PrepareLevel.Changed, subject, check, message);
                if (outcome == FbxWriteOutcome.WrittenUnconfigured)
                    report.Add(PrepareLevel.Warning, $"'{model}'", "Fix",
                        "the FBX was written, but an import setting it needs could not be set (see the Console; a read-only .meta, say): the import may not show the fix until it is");
            }
            finally
            {
                foreach (var copy in copies) if (copy != null) Object.DestroyImmediate(copy);
            }
        }

        void PlanClamp(string model, Mesh mesh, Mesh working, List<MeshEntry> entries,
            List<(string subject, string check, string message)> changes, PrepareReport report)
        {
            var uv = new List<Vector2>();
            working.GetUVs(1, uv);
            var (outside, overshoot) = Outside(uv);
            if (outside == 0) return;
            string count = MeshValidation.Plural(outside, "UV1 vertex", "UV1 vertices");
            if (overshoot > maxClamp)
            {
                report.Add(PrepareLevel.Unchanged, $"'{mesh.name}'", Check,
                    $"{count} up to {F(overshoot)} outside [0,1]: too far for a clamp (at most {F(maxClamp)}), which would squash their faces on the edge; repack the lightmap UV");
                return;
            }
            var clamped = uv.ToArray();
            XatlasRepack.ClampUvsToUnit(clamped, out _);
            working.SetUVs(1, clamped);
            entries.Add(new MeshEntry { fbxMesh = mesh, originalMesh = working });
            changes.Add(($"'{mesh.name}'", Check, $"{count} clamped into [0,1] (up to {F(overshoot)} outside), in '{model}'"));
        }

        // The working copy loses its degenerate faces (MeshHygieneUtility: a triangle dropped, a
        // quad dropped or split along its other diagonal). A collision mesh left without a face
        // is not written: nothing would collide.
        static void PlanFaces(string model, Mesh working, List<Mesh> reshaped,
            List<(string subject, string check, string message)> changes, PrepareReport report)
        {
            int removed = MeshHygieneUtility.RemoveDegenerateFaces(working, out int stuck);
            if (stuck > 0)
                report.Add(PrepareLevel.Unchanged, $"'{working.name}'", FacesCheck,
                    $"{MeshValidation.Plural(stuck, "quad", "quads")} with a doubled corner cannot be fixed as a quad; fix {(stuck == 1 ? "it" : "them")} in the DCC");
            if (removed == 0) return;
            string count = MeshValidation.Plural(removed, "face", "faces");
            long indices = 0;
            for (int s = 0; s < working.subMeshCount; ++s) indices += (long)working.GetIndexCount(s);
            if (indices == 0)
            {
                report.Add(PrepareLevel.Unchanged, $"'{working.name}'", FacesCheck, $"{count} without area, and no other: the collision mesh would be left empty; fix it in the DCC");
                return;
            }
            reshaped.Add(working);
            changes.Add(($"'{working.name}'", FacesCheck, $"{count} without area removed from the collision mesh, in '{model}'"));
        }

        static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
