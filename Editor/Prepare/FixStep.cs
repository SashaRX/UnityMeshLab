// FixStep.cs — the Fix step, after Validate: the simple errors a model fails Validate on are
// fixed in the model's own file, so the model is checked again fixed (Validate's errors wait for
// the reimport, as for Preset and Materials). Preview lists each fix as a change; Apply writes
// them into the FBX (FbxExport.WriteChannels: only the changed channel's changed corners) and
// reimports it. A fix is one the file can take without a decision: an error past its limit is
// left for Validate to report.
//
// The fixes: a lightmap UV (UV1) a little outside [0,1] is clamped onto the unit square's edge,
// up to the recipe's limit; farther out the faces would be squashed there, and it needs a repack.
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
    internal sealed class FixStep : IImporterStep
    {
        internal const string StepName = "Fix";
        const string Check = "Lightmap UV";

        /// <summary>How far outside [0,1] a lightmap UV is clamped by default, in UV units.</summary>
        internal const float DefaultMaxClamp = .02f;

        readonly float maxClamp;
        readonly Func<string, IReadOnlyList<MeshEntry>, bool> write;

        /// <param name="maxClamp">The farthest outside [0,1] a lightmap UV is clamped from; farther is left as it is.</param>
        /// <param name="write">Writes the entries' UV1 into the FBX at a path; the FBX channel write when null.</param>
        internal FixStep(float maxClamp = DefaultMaxClamp, Func<string, IReadOnlyList<MeshEntry>, bool> write = null)
        {
            this.maxClamp = Mathf.Max(0f, maxClamp);
            this.write = write ?? WriteUv1;
        }

        public string Name => StepName;

        public string Purpose => "Simple errors are fixed in the model's FBX: a lightmap UV a little outside [0,1] is clamped into it.";

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
            var fixes = new List<(string subject, string message)>();
            try
            {
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().OrderBy(m => m.name, StringComparer.Ordinal))
                {
                    if (MeshNaming.IsCollision(mesh.name) || !mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) continue;
                    var working = MeshAccess.ReadableCopy(mesh);
                    working.name = mesh.name;
                    working.hideFlags = HideFlags.HideAndDontSave;
                    copies.Add(working);
                    var uv = new List<Vector2>();
                    working.GetUVs(1, uv);
                    var (outside, overshoot) = Outside(uv);
                    if (outside == 0) continue;
                    string count = MeshValidation.Plural(outside, "UV1 vertex", "UV1 vertices");
                    if (overshoot > maxClamp)
                    {
                        report.Add(PrepareLevel.Unchanged, $"'{mesh.name}'", Check,
                            $"{count} up to {F(overshoot)} outside [0,1]: too far for a clamp (at most {F(maxClamp)}), which would squash their faces on the edge; repack the lightmap UV");
                        continue;
                    }
                    var clamped = uv.ToArray();
                    XatlasRepack.ClampUvsToUnit(clamped, out _);
                    working.SetUVs(1, clamped);
                    entries.Add(new MeshEntry { fbxMesh = mesh, originalMesh = working });
                    fixes.Add(($"'{mesh.name}'", $"{count} clamped into [0,1] (up to {F(overshoot)} outside), in '{model}'"));
                }
                if (fixes.Count == 0)
                {
                    if (report.Lines.Count == 0) report.Add(PrepareLevel.Unchanged, $"'{model}'", "Fix", "nothing to fix");
                    return;
                }
                if (apply && !write(model, entries))
                {
                    report.Add(PrepareLevel.Warning, $"'{model}'", Check, "the FBX was not written (see the Console); its lightmap UVs are as they were");
                    return;
                }
                foreach (var (subject, message) in fixes) report.Add(PrepareLevel.Changed, subject, Check, message);
            }
            finally
            {
                foreach (var copy in copies) if (copy != null) Object.DestroyImmediate(copy);
            }
        }

        static bool WriteUv1(string model, IReadOnlyList<MeshEntry> entries)
            => FbxExport.WriteChannels(model, entries, FbxExportIntent.UV1, null, null, null);

        static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
