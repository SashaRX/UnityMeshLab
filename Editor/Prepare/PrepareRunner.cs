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
// that change a model's importer (IImporterStep: Preset, Materials) apply before its variant is opened.
// A check (ICheckStep: Validate) of a model whose importer they change waits for the reimport: its
// plan's errors do not block Apply, it checks again on the reimported model, and an error then
// stops the steps after it (the importer's changes stay, as a fix towards the requirements).
//
// Tools ▸ Mesh Lab ▸ Prepare ▸ Preview / Apply run each selected target's chain (its recipe's,
// PrepareRecipe) and log each result; the Prepare tab (PrepareTool) runs it with its result shown.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    /// <summary>One step's part of a run: its report, or why it was skipped.</summary>
    internal sealed class PrepareStepResult
    {
        internal PrepareStepResult(IPrepareStep step, string skipped, PrepareReport report, string deferred = null)
        {
            Step = step; Skipped = skipped; Report = report; Deferred = deferred;
        }

        internal IPrepareStep Step { get; }

        /// <summary>Why the step did not act on the target, or null.</summary>
        internal string Skipped { get; }

        internal PrepareReport Report { get; }

        /// <summary>Why a check's errors do not block Apply yet (it checks again once the model is reimported), or null.</summary>
        internal string Deferred { get; }

        /// <summary>Whether the step's report blocks Apply: an Error that is not deferred.</summary>
        internal bool BlocksApply => Deferred == null && Report.BlocksApply;

        internal string Summary
        {
            get
            {
                if (Skipped != null) return $"skipped: {Skipped}";
                return Deferred != null ? $"{Report.Summary()} ({Deferred})" : Report.Summary();
            }
        }
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

        /// <summary>The recipe the steps are from and why (<see cref="PrepareChain.Match"/>); null when the steps were given.</summary>
        internal string Recipe { get; private set; }

        /// <summary>This result, from the steps of <paramref name="chain"/>.</summary>
        internal PrepareResult From(PrepareChain chain)
        {
            Recipe = chain.Match;
            return this;
        }

        internal PrepareLevel Level => Steps.Count == 0 ? PrepareLevel.Unchanged : Steps.Max(s => s.Report.Level);

        internal bool BlocksApply => Steps.Any(s => s.BlocksApply);

        /// <summary>Whether any step reports a change (made, or to make in a preview).</summary>
        internal bool Changes => Steps.Any(s => s.Report.Count(PrepareLevel.Changed) > 0);

        public override string ToString()
        {
            var text = new StringBuilder($"{Target.Display}: {State()}");
            if (Recipe != null) text.Append($"\n  {Recipe}");
            foreach (var step in Steps)
            {
                text.Append($"\n  {step.Step.Name}: {step.Summary}");
                foreach (var line in step.Report.Lines.OrderByDescending(l => l.level))
                    text.Append($"\n    {line.level}: {line.subject} — {line.check}: {line.message}")
                        .Append(string.Concat(line.details.Select(d => "\n      " + d)));
            }
            return text.ToString();
        }

        /// <summary>"applied", "preview", or "not applied, " and why.</summary>
        internal string State()
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

        /// <summary>The chain of the first of the project's recipes that matches <paramref name="target"/>.</summary>
        internal static PrepareChain Chain(PrepareTarget target) => PrepareRecipes.Chain(target, MeshLabProjectSettings.Instance.recipes);

        /// <summary>What each step would change on <paramref name="target"/>; changes nothing.</summary>
        internal static PrepareResult Preview(PrepareTarget target, IReadOnlyList<IPrepareStep> steps) => Run(target, steps, false);

        /// <summary>What the target's chain would change; changes nothing.</summary>
        internal static PrepareResult Preview(PrepareTarget target, PrepareChain chain) => Run(target, chain.Steps, false).From(chain);

        /// <summary>Applies the steps to <paramref name="target"/> unless a plan has an Error line.</summary>
        internal static PrepareResult Apply(PrepareTarget target, IReadOnlyList<IPrepareStep> steps) => Run(target, steps, true);

        /// <summary>Applies the target's chain unless a plan has an Error line.</summary>
        internal static PrepareResult Apply(PrepareTarget target, PrepareChain chain) => Run(target, chain.Steps, true).From(chain);

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
                plans = Defer(Pass(ModelContext(target, output), steps, false));
            string refused = apply ? Refusal(plans) : null;
            if (!apply || refused != null || !HasChanges(plans)) return new PrepareResult(target, plans, apply && refused == null, refused);

            var done = Pass(ModelContext(target, null), steps.Where(s => s is IImporterStep).ToList(), true);
            using (var output = OpenOutput(target, steps))
            {
                var context = ModelContext(target, output);
                var rest = steps.Where(s => !(s is IImporterStep)).ToList();
                // The checks that waited for the reimport check the reimported model; an error stops the rest.
                var waited = rest.Where(s => plans.Any(p => p.Step == s && p.Deferred != null)).ToList();
                var rechecked = Pass(context, waited, false);
                string blocked = Refusal(rechecked);
                if (blocked != null)
                {
                    done.AddRange(rechecked);
                    done.AddRange(rest.Except(waited).Select(s => new PrepareStepResult(s, "not applied: " + blocked, new PrepareReport(target.Display))));
                    return NotSaved(new PrepareResult(target, Ordered(steps, done), true), $"the model's importer was changed, but {blocked}");
                }
                var hierarchy = Pass(context, rest.Except(waited).ToList(), true);
                done.AddRange(rechecked);
                done.AddRange(hierarchy);
                var result = new PrepareResult(target, Ordered(steps, done), true);
                if (output == null || !HasChanges(hierarchy) || output.Save(out string error)) return result;
                string imported = HasChanges(done.Except(hierarchy)) ? "the model's importer was changed, but " : "";
                return NotSaved(result, $"{imported}the prefab variant could not be saved: {error}; apply again to finish");
            }
        }

        static List<PrepareStepResult> Ordered(IReadOnlyList<IPrepareStep> steps, List<PrepareStepResult> results)
            => steps.Select(s => results.First(r => r.Step == s)).ToList();

        // When an importer step will change the model, a check's plan describes the model before
        // the reimport: its errors wait for the check on the reimported model.
        static List<PrepareStepResult> Defer(List<PrepareStepResult> plans)
        {
            var importers = plans.Where(p => p.Step is IImporterStep && p.Report.Count(PrepareLevel.Changed) > 0).Select(p => p.Step.Name).ToList();
            if (importers.Count == 0) return plans;
            string deferred = $"checked again once {string.Join(" and ", importers)} {(importers.Count > 1 ? "have" : "has")} reimported the model";
            return plans.Select(p => p.Step is ICheckStep && p.Skipped == null ? new PrepareStepResult(p.Step, null, p.Report, deferred) : p).ToList();
        }

        static PrefabOutput OpenOutput(PrepareTarget target, IReadOnlyList<IPrepareStep> steps)
            => steps.OfType<PrefabStep>().Any() ? PrefabOutput.Open(target.AssetPath, PrefabStep.VariantPath(target.AssetPath)) : null;

        static PrepareContext ModelContext(PrepareTarget target, PrefabOutput output)
            => output != null
                ? new PrepareContext(target, output.Root, false, output.Refusal, output)
                : new PrepareContext(target, null, false,
                    "a model's own hierarchy cannot be changed: turn on the recipe's Prefab step, or prepare a prefab or scene object made from it");

        // The hierarchy's changes were made in memory but not written (a model's importer
        // changes, made before, stay: the reason says so, and a second Apply finds them done).
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
            var errors = plans.Where(p => p.BlocksApply).Select(p => p.Step.Name).ToList();
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

        // ── Menu (the Prepare tab runs the same chains with the result shown) ──

        [MenuItem(MenuRoot + "Preview", true)]
        [MenuItem(MenuRoot + "Apply", true)]
        static bool HasSelection() => PrepareTarget.FromSelection().Count > 0;

        [MenuItem(MenuRoot + "Preview", false, 21)]
        static void PreviewSelection()
        {
            foreach (var target in PrepareTarget.FromSelection()) Preview(target, Chain(target)).Log();
        }

        [MenuItem(MenuRoot + "Apply", false, 22)]
        static void ApplySelection()
        {
            foreach (var target in PrepareTarget.FromSelection()) Apply(target, Chain(target)).Log();
        }
    }
}
