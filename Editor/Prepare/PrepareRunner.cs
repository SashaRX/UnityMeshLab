// PrepareRunner.cs — runs a chain of prepare steps on a target. Preview plans every step
// and changes nothing; Apply plans every step first and applies them only when no plan has an
// Error line (an Error blocks Apply), then reports what each step did. The result holds the
// target and one report per step (or why the step was skipped).
//
// A prefab asset's contents are loaded, edited and saved only when a step changed something,
// so a second run over a prepared prefab writes nothing; a scene object is edited with Undo,
// all of one run in one Undo step.
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
            var text = new StringBuilder($"{Target.Display}: {(Refused != null ? "not applied, " + Refused : Applied ? "applied" : "preview")}");
            foreach (var step in Steps)
            {
                text.Append($"\n  {step.Step.Name}: {step.Summary}");
                foreach (var line in step.Report.Lines.OrderByDescending(l => l.level))
                    text.Append($"\n    {line.level}: {line.subject} — {line.check}: {line.message}")
                        .Append(string.Concat(line.details.Select(d => "\n      " + d)));
            }
            return text.ToString();
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
            => new List<IPrepareStep> { new ColliderStep(SurfaceBinding.FromSettings(), SurfaceBinding.Vocabulary()) };

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
                    var result = Run(new PrepareContext(target, target.SceneObject, true), steps, apply);
                    Undo.CollapseUndoOperations(group);
                    return result;
                }
                case PrepareTargetKind.PrefabAsset:
                {
                    var root = PrefabUtility.LoadPrefabContents(target.AssetPath);
                    try
                    {
                        var result = Run(new PrepareContext(target, root, false), steps, apply);
                        if (result.Applied && result.Changes) PrefabUtility.SaveAsPrefabAsset(root, target.AssetPath);
                        return result;
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
                default:
                    return Run(new PrepareContext(target, null, false), steps, apply);
            }
        }

        static PrepareResult Run(PrepareContext context, IReadOnlyList<IPrepareStep> steps, bool apply)
        {
            var plans = Pass(context, steps, false);
            if (!apply) return new PrepareResult(context.Target, plans, false);
            var errors = plans.Where(p => p.Report.BlocksApply).Select(p => p.Step.Name).ToList();
            if (errors.Count > 0)
                return new PrepareResult(context.Target, plans, false, $"{string.Join(", ", errors)} reported errors; fix them and apply again");
            if (!plans.Any(p => p.Report.Count(PrepareLevel.Changed) > 0))
                return new PrepareResult(context.Target, plans, true);
            return new PrepareResult(context.Target, Pass(context, steps, true), true);
        }

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
