// PrepareRunner.cs — runs a chain of prepare steps on a target. Preview plans every step
// and changes nothing; Apply plans every step first and applies them only when no plan has an
// Error line (an Error blocks Apply), then reports what each step did. The result holds the
// target and one report per step (or why the step was skipped).
//
// A prefab asset's contents are loaded, edited and saved only when a step changed something,
// so a second run over a prepared prefab writes nothing; a scene object is edited with Undo,
// all of one run in one Undo step. A model asset is edited through its prefab variant when the
// chain has the Prefab step (PrefabOutput: the variant's contents, or the model's instance a
// first run saves as the variant), else its hierarchy is left to the steps that need none. Steps
// that change a model's importer (IImporterStep: Materials) apply before its variant is opened.
//
// Tools ▸ Mesh Lab ▸ Prepare ▸ Preview / Apply run the chain on the selection and log each
// result, until the Prefab Builder carries the prepare section.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    /// <summary>One step's part of a run: its report, or why it was skipped.</summary>
    internal sealed class PrepareStepResult
    {
        internal PrepareStepResult(IPrepareStep step, string skipped, PrepareReport report)
        {
            Step = step; Skipped = skipped; Report = report;
        }

        internal IPrepareStep Step { get; }

        /// <summary>Why the step did not act on the target, or null.</summary>
        internal string Skipped { get; }

        internal PrepareReport Report { get; }

        internal string Summary => Skipped != null ? $"skipped: {Skipped}" : Report.Summary();
    }

    /// <summary>A run over one target: each step's result, and whether the changes were made.</summary>
    internal sealed class PrepareResult
    {
        internal PrepareResult(PrepareTarget target, List<PrepareStepResult> steps, bool applied, string refused = null)
        {
            Target = target; Steps = steps; Applied = applied; Refused = refused;
        }

        internal PrepareTarget Target { get; }
        internal IReadOnlyList<PrepareStepResult> Steps { get; }

        /// <summary>Whether the steps were applied; false for a preview and for a refused Apply.</summary>
        internal bool Applied { get; }

        /// <summary>Why Apply made no change, or null.</summary>
        internal string Refused { get; }

        internal PrepareLevel Level => Steps.Count == 0 ? PrepareLevel.Unchanged : Steps.Max(s => s.Report.Level);

        internal bool BlocksApply => Level == PrepareLevel.Error;

        /// <summary>Whether any step reports a change (made, or to make in a preview).</summary>
        internal bool Changes => Steps.Any(s => s.Report.Count(PrepareLevel.Changed) > 0);

        public override string ToString()
        {
            var text = new StringBuilder($"{Target.Display}: {State()}");
            foreach (var step in Steps)
            {
                text.Append($"\n  {step.Step.Name}: {step.Summary}");
                foreach (var line in step.Report.Lines.OrderByDescending(l => l.level))
                    text.Append($"\n    {line.level}: {line.subject} — {line.check}: {line.message}")
                        .Append(string.Concat(line.details.Select(d => "\n      " + d)));
            }
            return text.ToString();
        }

        string State()
        {
            if (Refused != null) return "not applied, " + Refused;
            return Applied ? "applied" : "preview";
        }

        /// <summary>The result at its level: errors as errors, warnings as warnings, the rest as info.</summary>
        internal void Log()
        {
            string message = $"[{PrepareRunner.Tag}] {this}";
            if (Level == PrepareLevel.Error) UvtLog.Error(message);
            else if (Level == PrepareLevel.Warning) UvtLog.Warn(message);
            else UvtLog.Info(message);
        }
    }

    internal static class PrepareRunner
    {
        internal const string Tag = "Prepare";
        const string MenuRoot = "Tools/Mesh Lab/Prepare/";

        /// <summary>The steps a run takes when none are given, in chain order.</summary>
        internal static List<IPrepareStep> DefaultSteps()
            => new List<IPrepareStep> { new MaterialStep(), new ColliderStep(SurfaceBinding.FromSettings(), SurfaceBinding.Vocabulary()), new PrefabStep() };

        /// <summary>What each step would change on <paramref name="target"/>; changes nothing.</summary>
        internal static PrepareResult Preview(PrepareTarget target, IReadOnlyList<IPrepareStep> steps) => Run(target, steps, false);

        /// <summary>Applies the steps to <paramref name="target"/> unless a plan has an Error line.</summary>
        internal static PrepareResult Apply(PrepareTarget target, IReadOnlyList<IPrepareStep> steps) => Run(target, steps, true);

        static PrepareResult Run(PrepareTarget target, IReadOnlyList<IPrepareStep> steps, bool apply)
        {
            switch (target.Kind)
            {
                case PrepareTargetKind.SceneObject:
                {
                    Undo.IncrementCurrentGroup();
                    int group = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName($"Prepare {target.Name}");
                    var result = RunSteps(new PrepareContext(target, target.SceneObject, true), steps, apply);
                    Undo.CollapseUndoOperations(group);
                    return result;
                }
                case PrepareTargetKind.PrefabAsset:
                {
                    var root = PrefabUtility.LoadPrefabContents(target.AssetPath);
                    try
                    {
                        var result = RunSteps(new PrepareContext(target, root, false), steps, apply);
                        if (!result.Applied || !result.Changes) return result;
                        PrefabUtility.SaveAsPrefabAsset(root, target.AssetPath, out bool saved);
                        return saved ? result : NotSaved(result, "Unity refused to save the prefab");
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
                default:
                    return RunModel(target, steps, apply);
            }
        }

        // A model: the steps that change its importer apply first (the model is reimported), then
        // its prefab variant is opened on the result for the others, and saved when they changed it.
        static PrepareResult RunModel(PrepareTarget target, IReadOnlyList<IPrepareStep> steps, bool apply)
        {
            List<PrepareStepResult> plans;
            using (var output = OpenOutput(target, steps))
                plans = Pass(ModelContext(target, output), steps, false);
            string refused = apply ? Refusal(plans) : null;
            if (!apply || refused != null || !HasChanges(plans)) return new PrepareResult(target, plans, apply && refused == null, refused);

            var done = Pass(ModelContext(target, null), steps.Where(s => s is IImporterStep).ToList(), true);
            using (var output = OpenOutput(target, steps))
            {
                var hierarchy = Pass(ModelContext(target, output), steps.Where(s => !(s is IImporterStep)).ToList(), true);
                done.AddRange(hierarchy);
                var result = new PrepareResult(target, steps.Select(s => done.First(d => d.Step == s)).ToList(), true);
                if (output == null || !HasChanges(hierarchy)) return result;
                return output.Save(out string error) ? result : NotSaved(result, $"the prefab variant could not be saved: {error}");
            }
        }

        static PrefabOutput OpenOutput(PrepareTarget target, IReadOnlyList<IPrepareStep> steps)
            => steps.OfType<PrefabStep>().Any() ? PrefabOutput.Open(target.AssetPath, PrefabStep.VariantPath(target.AssetPath)) : null;

        static PrepareContext ModelContext(PrepareTarget target, PrefabOutput output)
            => output != null
                ? new PrepareContext(target, output.Root, false, output.Refusal, output)
                : new PrepareContext(target, null, false,
                    "a model's own hierarchy cannot be changed; add the Prefab step, or prepare a prefab or scene object made from it");

        // The steps' changes were made in memory, but nothing was written.
        static PrepareResult NotSaved(PrepareResult result, string why)
            => new PrepareResult(result.Target, result.Steps.ToList(), false, why);

        static PrepareResult RunSteps(PrepareContext context, IReadOnlyList<IPrepareStep> steps, bool apply)
        {
            var plans = Pass(context, steps, false);
            string refused = apply ? Refusal(plans) : null;
            if (!apply || refused != null || !HasChanges(plans)) return new PrepareResult(context.Target, plans, apply && refused == null, refused);
            return new PrepareResult(context.Target, Pass(context, steps, true), true);
        }

        // An Error line in any plan blocks Apply.
        static string Refusal(IEnumerable<PrepareStepResult> plans)
        {
            var errors = plans.Where(p => p.Report.BlocksApply).Select(p => p.Step.Name).ToList();
            return errors.Count > 0 ? $"{string.Join(", ", errors)} reported errors; fix them and apply again" : null;
        }

        static bool HasChanges(IEnumerable<PrepareStepResult> results) => results.Any(r => r.Report.Count(PrepareLevel.Changed) > 0);

        static List<PrepareStepResult> Pass(PrepareContext context, IReadOnlyList<IPrepareStep> steps, bool apply)
        {
            var results = new List<PrepareStepResult>(steps.Count);
            foreach (var step in steps)
            {
                var report = new PrepareReport(context.Target.Display);
                string skipped = step.SkipReason(context);
                if (skipped == null)
                {
                    if (apply) step.Apply(context, report);
                    else step.Plan(context, report);
                }
                results.Add(new PrepareStepResult(step, skipped, report));
            }
            return results;
        }

        // ── Menu (until the Prefab Builder carries the prepare section) ──

        [MenuItem(MenuRoot + "Preview", true)]
        [MenuItem(MenuRoot + "Apply", true)]
        static bool HasSelection() => PrepareTarget.FromSelection().Count > 0;

        [MenuItem(MenuRoot + "Preview", false, 21)]
        static void PreviewSelection()
        {
            foreach (var target in PrepareTarget.FromSelection()) Preview(target, DefaultSteps()).Log();
        }

        [MenuItem(MenuRoot + "Apply", false, 22)]
        static void ApplySelection()
        {
            foreach (var target in PrepareTarget.FromSelection()) Apply(target, DefaultSteps()).Log();
        }
    }
}
