// PrepareReport.cs — what a model check or a prepare step found or did, line by line,
// with the level that decides whether Apply may run: an Error line blocks it, a
// Warning is shown and lets it run.

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SashaRX.UnityMeshLab
{
    /// <summary>How a check or step left its subject. Ordered: the worst line is the report's level.</summary>
    internal enum PrepareLevel { Unchanged, Changed, Warning, Error }

    /// <summary>One finding: its level, the subject (an asset, node or mesh), the check and what it found.</summary>
    internal readonly struct PrepareLine
    {
        internal readonly PrepareLevel level;
        internal readonly string subject;
        internal readonly string check;
        internal readonly string message;

        internal PrepareLine(PrepareLevel level, string subject, string check, string message)
        {
            this.level = level; this.subject = subject; this.check = check; this.message = message;
        }

        public override string ToString() => $"{level}: {subject} — {check}: {message}";
    }

    /// <summary>The lines of one asset's check or preparation.</summary>
    internal sealed class PrepareReport
    {
        readonly List<PrepareLine> lines = new List<PrepareLine>();

        internal PrepareReport(string asset) { Asset = asset ?? string.Empty; }

        /// <summary>The asset the report is about (its path, or a scene object's name).</summary>
        internal string Asset { get; }

        internal IReadOnlyList<PrepareLine> Lines => lines;

        /// <summary>The worst level of any line; Unchanged without lines.</summary>
        internal PrepareLevel Level => lines.Count == 0 ? PrepareLevel.Unchanged : lines.Max(l => l.level);

        /// <summary>An Error line blocks Apply; warnings are only shown.</summary>
        internal bool BlocksApply => Level == PrepareLevel.Error;

        internal void Add(PrepareLevel level, string subject, string check, string message)
            => lines.Add(new PrepareLine(level, subject, check, message));

        /// <summary>Adds a finding at the level a requirement sets; nothing when it is off.</summary>
        internal void Add(RequirementLevel requirement, string subject, string check, string message)
        {
            if (requirement == RequirementLevel.Off) return;
            Add(requirement == RequirementLevel.Error ? PrepareLevel.Error : PrepareLevel.Warning, subject, check, message);
        }

        internal int Count(PrepareLevel level) => lines.Count(l => l.level == level);

        /// <summary>One line per level count, e.g. "2 errors, 1 warning"; "no findings" without lines.</summary>
        internal string Summary()
        {
            int errors = Count(PrepareLevel.Error), warnings = Count(PrepareLevel.Warning), changed = Count(PrepareLevel.Changed);
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
            if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
            if (changed > 0) parts.Add($"{changed} change{(changed == 1 ? "" : "s")}");
            return parts.Count == 0 ? "no findings" : string.Join(", ", parts);
        }

        public override string ToString()
        {
            var text = new StringBuilder($"{Asset}: {Summary()}");
            foreach (var line in lines.OrderByDescending(l => l.level)) text.Append("\n  ").Append(line);
            return text.ToString();
        }

        /// <summary>The summary, then each line at its level (Error, Warn, Info).</summary>
        internal void Log(string tag)
        {
            string head = $"[{tag}] '{Asset}': {Summary()}{(BlocksApply ? "; Apply is blocked until the errors are fixed" : "")}";
            if (BlocksApply) UvtLog.Error(head);
            else if (Level == PrepareLevel.Warning) UvtLog.Warn(head);
            else UvtLog.Info(head);
            foreach (var line in lines.OrderByDescending(l => l.level))
            {
                string text = $"[{tag}] '{Asset}' {line.subject} — {line.check}: {line.message}";
                if (line.level == PrepareLevel.Error) UvtLog.Error(text);
                else if (line.level == PrepareLevel.Warning) UvtLog.Warn(text);
                else UvtLog.Info(text);
            }
        }
    }
}
